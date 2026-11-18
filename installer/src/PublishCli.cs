// ReleasePublish.exe: the Release Manager's GitHub steps from a command line (the maintainer, 2026-11-14: "Pack and push when ready",
// for an agent that can't press the manager's buttons). Each command runs the manager's own code: Volumes.WriteParts and its
// part check, ManagerForm.CheckServer (live unlock server and its Steam route), GitHub.HasAttestation (the installer is the
// one the public repository's CI built), GitHubReleases.UploadRelease and PublishBoth. Nothing here decides anything the
// manager doesn't; the manager's confirmation dialogs become the explicit command and its arguments.
//   prepare --package <dir> --assets <new dir> --label <label> --setup <BattleTechUCUSetup.exe> --verify <verify.ps1> [--part-gib 1.95]
//   check   --repo <owner/name> [--data-repo <owner/name>] --token <file> --tag <tag>
//   upload  --assets <dir> --repo <owner/name> [--data-repo <owner/name>] --token <file> --master-secret <file> --tag <tag>
//           --name <release name> --label <label> [--notes <file>] [--no-draft]
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
using System.Threading;

namespace ReleaseGate
{
    static class PublishCli
    {
        const string DataRepoDefault = "BATTLETECH-Unofficial-Community-Update/BattleTech-UCU-data";

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
                    if (a[i] == "--no-draft" || a[i] == "--no-prune" || a[i] == "--dry-run") { flags.Add(a[i]); continue; }
                    if (i + 1 >= a.Length) throw new ArgumentException("Missing value for " + a[i]);
                    o[a[i]] = a[++i];
                }
                Func<string, string> need = k => { string v; if (!o.TryGetValue(k, out v) || v.Trim().Length == 0) throw new ArgumentException("Need " + k); return v.Trim(); };
                Func<string, string, string> opt = (k, d) => { string v; return o.TryGetValue(k, out v) && v.Trim().Length > 0 ? v.Trim() : d; };
                Action<string> log = s => Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + "  " + s);
                switch (a[0])
                {
                    case "prepare": Console.WriteLine(Prepare(need("--package"), need("--assets"), need("--label"), need("--setup"), need("--verify"), opt("--part-gib", "1.95"), log)); break;
                    case "check": Console.WriteLine(Check(new GitHub(need("--repo"), need("--token")), new GitHub(opt("--data-repo", DataRepoDefault), need("--token")), need("--tag"), log)); break;
                    case "upload":
                        Console.WriteLine(Upload(need("--assets"), need("--repo"), opt("--data-repo", DataRepoDefault), need("--token"), need("--master-secret"), need("--tag"),
                            need("--name"), o.ContainsKey("--notes") ? File.ReadAllText(need("--notes")) : ManagerForm.DefaultNotes(need("--label"), opt("--changelog", null)), !flags.Contains("--no-draft"), log));
                        break;
                    case "verify":
                        string vm = need("--repo"), vd = opt("--data-repo", DataRepoDefault);
                        Console.WriteLine(Verify(need("--assets"), new GitHub(vm, need("--token")), new GitHub(vd, need("--token")), vm, vd, need("--tag"), log));
                        break;
                    case "publish":
                        string main = need("--repo"), data = opt("--data-repo", DataRepoDefault);
                        Console.WriteLine(GitHubReleases.PublishBoth(new GitHub(main, need("--token")), new GitHub(data, need("--token")), main, data, need("--tag"), log, !flags.Contains("--no-prune")));
                        break;
                    case "prune":
                        string pd = opt("--data-repo", DataRepoDefault);
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

        // ManagerForm.PrepareAssets without its dialogs: the assets folder must be new or empty.
        static string Prepare(string package, string outDir, string label, string setup, string verify, string partGib, Action<string> log)
        {
            package = Path.GetFullPath(package); outDir = Path.GetFullPath(outDir); label = Checks.SafeLabel(label);
            double g;
            if (!double.TryParse(partGib, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out g) || g < 0.01 || g >= 2.0)
                throw new ArgumentException("--part-gib must be from 0.01 to under 2");
            long partBytes = (long)(g * 1073741824.0);
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
        static string Verify(string assets, GitHub main, GitHub data, string mainName, string dataName, string tag, Action<string> log)
        {
            assets = Path.GetFullPath(assets);
            List<string> files = ManagerForm.AssetFiles(assets);
            var exeOnly = files.Where(f => new[] { Remote.InstallerName, PublicManifest.FileName, PublicManifest.GogFileName, ManagerForm.VerifyScript }
                .Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)).ToList();
            int bad = 0, ok = 0;
            foreach (var pair in new[] { Tuple.Create(data, dataName, files), Tuple.Create(main, mainName, exeOnly) })
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

        // ManagerForm.GitHubUpload without its dialogs: a package with no unlock lock or no public manifest is refused.
        static string Upload(string assets, string mainName, string dataName, string tokenFile, string masterFile, string tag, string name, string body, bool draft, Action<string> log)
        {
            assets = Path.GetFullPath(assets);
            GitHub main = new GitHub(mainName, tokenFile), data = new GitHub(dataName, tokenFile);
            if (ManagerForm.LockOf(assets) == null) throw new IOException("This package needs no access code, and GitHub releases are public. Pack it with the unlock server.");
            List<string> files = ManagerForm.AssetFiles(assets);
            var exeOnly = files.Where(f => new[] { Remote.InstallerName, PublicManifest.FileName, PublicManifest.GogFileName, ManagerForm.VerifyScript }
                .Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)).ToList();
            string manifestFile = Path.Combine(assets, PublicManifest.FileName);
            if (!File.Exists(manifestFile)) throw new IOException("This package has no public manifest (" + PublicManifest.FileName + "). Pack it again.");
            string manifestLine = "Public manifest: " + PublicManifest.FileName + ", SHA-256 " + PublicManifest.Sha256Of(File.ReadAllBytes(manifestFile)) +
                ". It lists every file this release installs, with SHA-512 and origin; check an install with " + ManagerForm.VerifyScript + " or the installer's Verify install."
                + ManagerForm.GogManifestLine(assets);
            if (!body.Contains(manifestLine)) body = (body.TrimEnd() + "\n\n" + manifestLine).TrimStart();
            string dataBody = "Package data for " + name + ". Players: download BattleTechUCUSetup.exe from https://github.com/" + mainName + "/releases instead; " +
                "it downloads these files itself.";
            ManagerForm.CheckServer(assets, masterFile, log, true);
            string setupFile = Path.Combine(assets, Remote.InstallerName);
            string setupSha;
            using (var h = Gate.NewSha()) using (var f = File.OpenRead(setupFile)) setupSha = Gate.Hex(h.ComputeHash(f));
            if (!main.HasAttestation(setupSha))
                throw new IOException("The installer in the assets folder (SHA-256 " + setupSha + ") has no build attestation in " + mainName + ". Nothing was uploaded.");
            log("Installer SHA-256 " + setupSha + " is attested by " + mainName + "'s CI.");
            var sent = new long[1];
            int uploaded = 0, skipped = 0;
            var d = GitHubReleases.UploadRelease(data, dataName, tag, name, dataBody, draft, false, files, sent, fn => { }, CancellationToken.None, ref uploaded, ref skipped, mainName, log);
            var m = GitHubReleases.UploadRelease(main, mainName, tag, name, body, draft, false, exeOnly, sent, fn => { }, CancellationToken.None, ref uploaded, ref skipped, mainName, log);
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
