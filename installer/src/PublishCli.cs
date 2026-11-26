// ReleasePublish.exe: the Release Manager's GitHub steps from a command line (the maintainer, 2026-11-14: "Pack and push when ready",
// for an agent that can't press the manager's buttons). Each command runs the manager's own code: Volumes.WriteParts and its
// part check, ManagerForm.CheckServer (live unlock server and its Steam route), GitHub.HasAttestation (the installer is the
// one the public repository's CI built), GitHubReleases.UploadRelease and PublishBoth. Nothing here decides anything the
// manager doesn't; the manager's confirmation dialogs become the explicit command and its arguments.
//   prepare --package <dir> --assets <new dir> --label <label> --setup <BattleTechUCUSetup.exe> --verify <verify.ps1> [--part-gib 1.95]
//   prepare --platform mac --package <dir> --assets <new dir> --label <label> --ucu-arm64 <file> --ucu-x64 <file>
//   check   --repo <owner/name> [--data-repo <owner/name>] --token <file> --tag <tag>
//   upload  --assets <dir> --repo <owner/name> [--data-repo <owner/name>] --token <file> --master-secret <file> --tag <tag>
//           --name <release name> --label <label> [--notes <file>] [--no-draft] [--platform mac --prerelease]
//   verify  --assets <dir> --repo <owner/name> [--data-repo <owner/name>] --token <file> --tag <tag>
//           every asset of both releases against the local file: name, size, GitHub's sha256 digest (all sizes)
//   publish --repo <owner/name> [--data-repo <owner/name>] --token <file> --tag <tag> [--no-prune]
//           also prunes: the data repository keeps the data parts of its newest 10 published releases (the maintainer, 2026-11-15)
//   prune   [--data-repo <owner/name>] --token <file> [--keep 10] [--dry-run]
// Tests point the API at a local stand-in with BTUCU_GITHUB_API=http://127.0.0.1:<port>/repos/ (test/github-standin.py).
// The token is read from its file on every call and never printed. Exit 0 ok, 1 failed (nothing further was done).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace ReleaseGate
{
    static class PublishCli
    {
        const string DataRepoWindows = "BATTLETECH-Unofficial-Community-Update/BattleTech-UCU-data";
        const string DataRepoMac = "BATTLETECH-Unofficial-Community-Update/BattleTech-UCU-mac-data";
        const string MacVaultName = "package-mac.vault";
        const string MacManifestName = "ucu-manifest-mac.tsv";

        static int Main(string[] a)
        {
            // As ManagerProgram.Main: .NET Framework 4.x defaults to protocols GitHub refuses.
            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
            string api = Environment.GetEnvironmentVariable("BTUCU_GITHUB_API");
            if (!string.IsNullOrEmpty(api))
            {
                if (!api.StartsWith("http://127.0.0.1:")) { Console.Error.WriteLine("FAILED: BTUCU_GITHUB_API must be a local stand-in"); return 1; }
                GitHub.ApiBase = api;
            }
            try
            {
                if (a.Length == 0) throw new ArgumentException("Need a command: prepare, check, upload or publish");
                var o = new Dictionary<string, string>(StringComparer.Ordinal);
                var flags = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 1; i < a.Length; i++)
                {
                    if (!a[i].StartsWith("--")) throw new ArgumentException("Unexpected " + a[i]);
                    if (a[i] == "--no-draft" || a[i] == "--no-prune" || a[i] == "--dry-run" || a[i] == "--prerelease") { flags.Add(a[i]); continue; }
                    if (i + 1 >= a.Length) throw new ArgumentException("Missing value for " + a[i]);
                    o[a[i]] = a[++i];
                }
                Func<string, string> need = k => { string v; if (!o.TryGetValue(k, out v) || v.Trim().Length == 0) throw new ArgumentException("Need " + k); return v.Trim(); };
                Func<string, string, string> opt = (k, d) => { string v; return o.TryGetValue(k, out v) && v.Trim().Length > 0 ? v.Trim() : d; };
                string platform = opt("--platform", "windows").ToLowerInvariant();
                if (platform != "windows" && platform != "mac") throw new ArgumentException("--platform must be windows or mac");
                string dataDefault = platform == "mac" ? DataRepoMac : DataRepoWindows;
                Action<string> log = s => Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + "  " + s);
                switch (a[0])
                {
                    case "prepare": Console.WriteLine(platform == "mac"
                        ? PrepareMac(need("--package"), need("--assets"), need("--label"), need("--ucu-arm64"), need("--ucu-x64"), opt("--part-gib", "1.95"), log)
                        : Prepare(need("--package"), need("--assets"), need("--label"), need("--setup"), need("--verify"), opt("--part-gib", "1.95"), log)); break;
                    case "check": Console.WriteLine(Check(new GitHub(need("--repo"), need("--token")), new GitHub(opt("--data-repo", dataDefault), need("--token")), need("--tag"), log)); break;
                    case "upload":
                        if (platform == "mac" && !flags.Contains("--prerelease")) throw new ArgumentException("Mac upload requires --prerelease");
                        Console.WriteLine(Upload(need("--assets"), need("--repo"), opt("--data-repo", dataDefault), need("--token"), need("--master-secret"), need("--tag"),
                            need("--name"), o.ContainsKey("--notes") ? File.ReadAllText(need("--notes")) : platform == "mac"
                                ? MacDefaultNotes(need("--label"), opt("--changelog", null)) : ManagerForm.DefaultNotes(need("--label"), opt("--changelog", null)), !flags.Contains("--no-draft"),
                            platform == "mac", flags.Contains("--prerelease"), log));
                        break;
                    case "verify":
                        string vm = need("--repo"), vd = opt("--data-repo", dataDefault);
                        Console.WriteLine(Verify(need("--assets"), new GitHub(vm, need("--token")), new GitHub(vd, need("--token")), vm, vd, need("--tag"), platform == "mac", log));
                        break;
                    case "publish":
                        string main = need("--repo"), data = opt("--data-repo", dataDefault);
                        CheckDataRepo(data, platform == "mac");
                        Console.WriteLine(GitHubReleases.PublishBoth(new GitHub(main, need("--token")), new GitHub(data, need("--token")), main, data, need("--tag"), log, !flags.Contains("--no-prune"), platform == "mac"));
                        break;
                    case "prune":
                        string pd = opt("--data-repo", dataDefault);
                        Console.WriteLine(GitHubReleases.PruneData(new GitHub(pd, need("--token")), pd, int.Parse(opt("--keep", GitHubReleases.KeepDataReleases.ToString())), flags.Contains("--dry-run"), log));
                        break;
                    default: throw new ArgumentException("Unknown command " + a[0]);
                }
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("FAILED: " + e.Message);
                return 1;
            }
        }

        static void CheckDataRepo(string dataName, bool mac)
        {
            string refused = mac ? "BattleTech-UCU-data" : "BattleTech-UCU-mac-data";
            string leaf = dataName.TrimEnd('/').Split('/').Last();
            if (string.Equals(leaf, refused, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException((mac ? "Mac" : "Windows") + " assets cannot be sent to " + refused + ".");
        }

        static string PrepareMac(string package, string outDir, string label, string arm64, string x64, string partGib, Action<string> log)
        {
            package = Path.GetFullPath(package); outDir = Path.GetFullPath(outDir); label = Checks.SafeLabel(label);
            long partBytes = PartBytes(partGib);
            ValidateMacPackage(package, false);
            if (Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any()) throw new IOException("The assets folder is not empty: " + outDir);
            if (!File.Exists(arm64)) throw new IOException("No arm64 ucu binary at " + arm64);
            if (!File.Exists(x64)) throw new IOException("No x64 ucu binary at " + x64);
            Directory.CreateDirectory(outDir);
            File.Copy(Path.Combine(package, MacVaultName), Path.Combine(outDir, MacVaultName));
            File.Copy(Path.Combine(package, MacManifestName), Path.Combine(outDir, MacManifestName));
            File.Copy(arm64, Path.Combine(outDir, "ucu-macos-arm64"));
            File.Copy(x64, Path.Combine(outDir, "ucu-macos-intel"));
            List<string> parts = Volumes.WriteParts(package, outDir, label, partBytes, n => { }, CancellationToken.None);
            var index = Volumes.Index(outDir);
            foreach (string blob in Directory.EnumerateFiles(Path.Combine(package, "data"), "*.pak"))
            {
                PartBlob b;
                if (!index.TryGetValue("data/" + Path.GetFileName(blob), out b) || b.Length != new FileInfo(blob).Length)
                    throw new IOException("Part check failed for " + Path.GetFileName(blob));
            }
            var files = MacAssetFiles(outDir);
            long biggest = files.Max(f => new FileInfo(f).Length);
            if (biggest >= Volumes.GitHubFileLimit) throw new IOException("A file is 2 GiB or more; GitHub won't take it.");
            if (files.Count > Volumes.GitHubAssetLimit) throw new IOException("More than 1,000 files; use a larger part size.");
            foreach (string f in files) log(string.Format("  {0}  {1}", Path.GetFileName(f), Checks.Gib(new FileInfo(f).Length)));
            return string.Format("Prepared Mac assets: {0} files, {1}, largest {2}; all {3} blobs found in {4} parts", files.Count,
                Checks.Gib(files.Sum(f => new FileInfo(f).Length)), Checks.Gib(biggest), index.Count, parts.Count);
        }

        static long PartBytes(string partGib)
        {
            double g;
            if (!double.TryParse(partGib, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out g) || g < 0.01 || g >= 2.0)
                throw new ArgumentException("--part-gib must be from 0.01 to under 2");
            return (long)(g * 1073741824.0);
        }

        // ManagerForm.PrepareAssets without its dialogs: the assets folder must be new or empty.
        static string Prepare(string package, string outDir, string label, string setup, string verify, string partGib, Action<string> log)
        {
            package = Path.GetFullPath(package); outDir = Path.GetFullPath(outDir); label = Checks.SafeLabel(label);
            double g;
            if (!double.TryParse(partGib, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out g) || g < 0.01 || g >= 2.0)
                throw new ArgumentException("--part-gib must be from 0.01 to under 2");
            long partBytes = (long)(g * 1073741824.0);
            string problem = ManagerForm.PublishProblem(package);
            if (problem != null) throw new IOException(problem);
            if (Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any()) throw new IOException("The assets folder is not empty: " + outDir);
            if (!File.Exists(setup)) throw new IOException("No installer at " + setup);
            if (!File.Exists(verify)) throw new IOException("No verify script at " + verify);
            Directory.CreateDirectory(outDir);
            File.Copy(setup, Path.Combine(outDir, Remote.InstallerName));
            File.Copy(Path.Combine(package, Gate.VaultName), Path.Combine(outDir, Gate.VaultName));
            foreach (string m in new[] { PublicManifest.FileName, PublicManifest.GogFileName })
                if (File.Exists(Path.Combine(package, m))) File.Copy(Path.Combine(package, m), Path.Combine(outDir, m));
            File.Copy(verify, Path.Combine(outDir, ManagerForm.VerifyScript));
            List<string> parts = Volumes.WriteParts(package, outDir, label, partBytes, n => { }, CancellationToken.None);
            var index = Volumes.Index(outDir);
            foreach (string blob in Directory.EnumerateFiles(Path.Combine(package, "data"), "*.pak"))
            {
                PartBlob b;
                if (!index.TryGetValue("data/" + Path.GetFileName(blob), out b) || b.Length != new FileInfo(blob).Length)
                    throw new IOException("Part check failed for " + Path.GetFileName(blob));
            }
            var files = ManagerForm.AssetFiles(outDir);
            long biggest = files.Max(f => new FileInfo(f).Length);
            if (biggest >= Volumes.GitHubFileLimit) throw new IOException("A file is 2 GiB or more; GitHub won't take it.");
            if (files.Count > Volumes.GitHubAssetLimit) throw new IOException("More than 1,000 files; use a larger part size.");
            foreach (string f in files) log(string.Format("  {0}  {1}", Path.GetFileName(f), Checks.Gib(new FileInfo(f).Length)));
            return string.Format("Prepared {0} files, {1}, largest {2}; all {3} blobs found in {4} parts", files.Count, Checks.Gib(files.Sum(f => new FileInfo(f).Length)),
                Checks.Gib(biggest), index.Count, parts.Count);
        }

        // Each release's assets against the local files the upload sends to it (data: every asset file; main: installer,
        // public manifest, verify.ps1): same names, sizes and sha256 (GitHub's digest, computed here for every size).
        static string Verify(string assets, GitHub main, GitHub data, string mainName, string dataName, string tag, bool mac, Action<string> log)
        {
            assets = Path.GetFullPath(assets);
            CheckDataRepo(dataName, mac);
            if (mac)
            {
                ValidateMacPackage(assets, true);
            }
            List<string> files = mac ? MacAssetFiles(assets) : ManagerForm.AssetFiles(assets);
            var exeOnly = MainAssets(files, mac);
            var dataFiles = DataAssets(files, mac);
            if (mac) RequireMacAssets(files);
            int bad = 0, ok = 0;
            foreach (var pair in new[] { Tuple.Create(data, dataName, dataFiles), Tuple.Create(main, mainName, exeOnly) })
            {
                var rel = pair.Item1.FindRelease(tag);
                if (rel == null) { log("MISSING release " + tag + " in " + pair.Item2); bad++; continue; }
                log(string.Format("{0} {1}: {2}, {3} assets", pair.Item2, tag, true.Equals(rel["draft"]) ? "draft" : "published", GitHub.Assets(rel).Count()));
                var have = GitHub.Assets(rel).ToDictionary(x => (string)x["name"], StringComparer.OrdinalIgnoreCase);
                foreach (string f in pair.Item3)
                {
                    string fn = Path.GetFileName(f);
                    long len = new FileInfo(f).Length;
                    string sha;
                    using (var h = Gate.NewSha()) using (var s = File.OpenRead(f)) sha = Gate.Hex(h.ComputeHash(s));
                    Dictionary<string, object> x;
                    if (!have.TryGetValue(fn, out x)) { log("  MISSING " + fn); bad++; continue; }
                    object dg;
                    string digest = x.TryGetValue("digest", out dg) ? dg as string : null;
                    bool same = Convert.ToInt64(x["size"]) == len && (x["state"] as string) == "uploaded" && digest != null &&
                        string.Equals(digest, "sha256:" + sha, StringComparison.OrdinalIgnoreCase);
                    log(string.Format("  {0} {1} {2} {3}", same ? "OK" : "DIFFERS", fn, len, sha.Substring(0, 16)));
                    if (same) ok++; else bad++;
                    have.Remove(fn);
                }
                foreach (string extra in have.Keys) { log("  EXTRA " + extra); bad++; }
            }
            if (bad > 0) throw new IOException(string.Format("{0} assets differ, are missing or extra; {1} equal.", bad, ok));
            return string.Format("VERIFY OK: {0} assets equal to the local files, by name, size and sha256.", ok);
        }

        // ManagerForm.GitHubCheck.
        static string Check(GitHub main, GitHub data, string tag, Action<string> log)
        {
            var lines = new List<string>();
            foreach (GitHub gh in new[] { main, data })
            {
                var repo = gh.Repo();
                object perms;
                bool push = repo.TryGetValue("permissions", out perms) && perms is Dictionary<string, object> && true.Equals(((Dictionary<string, object>)perms)["push"]);
                string s = string.Format("{0} is reachable; {1}.", repo["full_name"], push ? "the token can write releases" : "the token CAN'T write; it needs Contents read and write");
                if (gh.IsEmpty()) s += " It has no commit yet.";
                else
                {
                    var rel = gh.FindRelease(tag);
                    s += rel == null ? " No release has tag " + tag + " yet." :
                        string.Format(" Release {0} exists ({1}), {2} files.", tag, true.Equals(rel["draft"]) ? "draft" : "published", GitHub.Assets(rel).Count());
                }
                log(s);
                lines.Add(s);
            }
            return string.Join(" ", lines);
        }

        // ManagerForm.GitHubUpload without its dialogs: a package that isn't a locked v4 one, or has no public manifest, is refused.
        static List<string> MainAssets(List<string> files, bool mac)
        {
            return files.Where(f => (mac
                ? new[] { "ucu-macos-arm64", "ucu-macos-intel", MacManifestName }
                : new[] { Remote.InstallerName, PublicManifest.FileName, PublicManifest.GogFileName, ManagerForm.VerifyScript })
                .Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)).ToList();
        }

        static List<string> DataAssets(List<string> files, bool mac)
        {
            return mac ? files.Where(f => !new[] { "ucu-macos-arm64", "ucu-macos-intel" }
                .Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)).ToList() : files;
        }

        static void RequireMacAssets(List<string> files)
        {
            foreach (string required in new[] { "ucu-macos-arm64", "ucu-macos-intel", MacManifestName, MacVaultName })
                if (!files.Any(f => string.Equals(Path.GetFileName(f), required, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("Mac assets are missing " + required + ". Prepare them again.");
        }

        static List<string> MacAssetFiles(string dir)
        {
            return Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }

        static void ValidateMacPackage(string dir, bool preparedAssets)
        {
            string vault = Path.Combine(dir, MacVaultName);
            try
            {
                using (var f = File.OpenRead(vault))
                using (var r = new BinaryReader(f))
                {
                    if (Encoding.ASCII.GetString(r.ReadBytes(8)) != "BTU6GAM4" || !r.ReadBoolean())
                        throw new IOException("This package's Mac vault is not a locked v4 vault. Pack it again.");
                    if (r.ReadBytes(16).Length != 16) throw new EndOfStreamException();
                    r.ReadString();
                    if (r.ReadString() != "mac-steam") throw new IOException("This vault is not a macOS package.");
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is FormatException)
            {
                throw new IOException("The package's " + MacVaultName + " is not a readable locked macOS v4 vault: " + e.Message, e);
            }
            if (!File.Exists(Path.Combine(dir, MacManifestName))) throw new IOException("The package has no " + MacManifestName + ". Pack it again.");
            if (preparedAssets)
            {
                if (!Volumes.Index(dir).Keys.Any(k => k.StartsWith("data/", StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("The Mac assets have no indexed data parts.");
            }
            else if (!Directory.Exists(Path.Combine(dir, "data")) || !Directory.EnumerateFiles(Path.Combine(dir, "data"), "*.pak").Any())
                throw new IOException("The macOS package has no data/*.pak files.");
        }

        static string MacDefaultNotes(string label, string changelog)
        {
            string notes = "Download ucu-macos-arm64 for Apple silicon or ucu-macos-intel for Intel, then run `ucu install --remote`.";
            string section = ManagerForm.ChangelogSection(ManagerForm.PublicChangelog(changelog), label);
            return section.Length == 0 ? notes : notes + "\r\n\r\n" + section;
        }

        static string Upload(string assets, string mainName, string dataName, string tokenFile, string masterFile, string tag, string name, string body, bool draft, bool mac, bool prerelease, Action<string> log)
        {
            assets = Path.GetFullPath(assets);
            CheckDataRepo(dataName, mac);
            GitHub main = new GitHub(mainName, tokenFile), data = new GitHub(dataName, tokenFile);
            if (mac) ValidateMacPackage(assets, true);
            else
            {
                string problem = ManagerForm.PublishProblem(assets);
                if (problem != null) throw new IOException(problem + " Nothing was uploaded.");
            }
            List<string> files = mac ? MacAssetFiles(assets) : ManagerForm.AssetFiles(assets);
            var exeOnly = MainAssets(files, mac);
            var dataFiles = DataAssets(files, mac);
            if (mac) RequireMacAssets(files);
            string manifestName = mac ? MacManifestName : PublicManifest.FileName;
            string manifestFile = Path.Combine(assets, manifestName);
            if (!File.Exists(manifestFile)) throw new IOException("This package has no public manifest (" + manifestName + "). Pack it again.");
            string manifestLine = "Public manifest: " + manifestName + ", SHA-256 " + PublicManifest.Sha256Of(File.ReadAllBytes(manifestFile)) +
                (mac ? ". It lists every file the Mac tool installs, with SHA-512 and origin; check an install with ucu verify."
                    : ". It lists every file this release installs, with SHA-512 and origin; check an install with " + ManagerForm.VerifyScript + " or the installer's Verify install.")
                + (mac ? "" : ManagerForm.GogManifestLine(assets));
            if (!body.Contains(manifestLine)) body = (body.TrimEnd() + "\n\n" + manifestLine).TrimStart();
            string dataBody = mac
                ? "Encrypted macOS package data for " + name + ". Download the ucu-macos-arm64 or ucu-macos-intel tool from https://github.com/" + mainName + "/releases."
                : "Package data for " + name + ". Players: download BattleTechUCUSetup.exe from https://github.com/" + mainName + "/releases instead; it downloads these files itself.";
            if (mac)
            {
                if (!prerelease) throw new ArgumentException("Mac upload requires --prerelease");
                if (!File.Exists(masterFile)) throw new IOException("No master secret file at " + masterFile);
            }
            else
            {
                ManagerForm.CheckServer(assets, masterFile, log, true);
                string setupFile = Path.Combine(assets, Remote.InstallerName);
                string setupSha;
                using (var h = Gate.NewSha()) using (var f = File.OpenRead(setupFile)) setupSha = Gate.Hex(h.ComputeHash(f));
                if (!main.HasAttestation(setupSha))
                    throw new IOException("The installer in the assets folder (SHA-256 " + setupSha + ") has no build attestation in " + mainName + ". Nothing was uploaded.");
                log("Installer SHA-256 " + setupSha + " is attested by " + mainName + "'s CI.");
            }
            var sent = new long[1];
            int uploaded = 0, skipped = 0;
            var d = GitHubReleases.UploadRelease(data, dataName, tag, name, dataBody, draft, false, dataFiles, sent, fn => { }, CancellationToken.None, ref uploaded, ref skipped, mainName, log);
            var m = GitHubReleases.UploadRelease(main, mainName, tag, name, body, draft, prerelease, exeOnly, sent, fn => { }, CancellationToken.None, ref uploaded, ref skipped, mainName, log, mac);
            if (!((m["body"] as string) ?? "").Contains(manifestLine))
            {
                m = main.AppendToBody(m, manifestLine);
                log("Added the public manifest's SHA-256 to the notes of " + tag + " in " + mainName);
            }
            bool drafts = true.Equals(d["draft"]) || true.Equals(m["draft"]);
            return string.Format("{0} uploaded, {1} already there. {2}", uploaded, skipped, drafts ? "Drafts: run publish to make them public." : "Both releases are live.") + " " + m["html_url"];
        }
    }
}
