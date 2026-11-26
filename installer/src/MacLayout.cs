// The macOS side of the gate: where a Mac install keeps what the fingerprint, the packer and the installer look for, the
// fingerprint over those files, and the check that a built player's CodeResources lists exactly its files. Compiled only with
// UCU_MAC (ReleasePack for macOS packages, and the macOS command-line tool in mac/). BattleTechUCUSetup.exe is built without
// it. Every print in here names a file or a count, never a digest: a fingerprint and a plaintext hash are key material.
#if UCU_MAC
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace ReleaseGate
{
    /// <summary>The paths of one platform's install, relative to the folder that holds the game.</summary>
    public sealed class Layout
    {
        const int Window = 1 << 20;
        static readonly string[] Sampled = { "globalgamemanagers.assets", "resources.assets", "resources.assets.resS", "sharedassets0.assets", "level10", "level10.resS" };

        public string Name;        // "windows" or "mac"
        public string Platform;    // null for Windows; Gate.MacPlatform
        public string Exe;         // the executable whose whole file is one fingerprint part
        public string Data;        // the data folder, with its trailing slash: six sampled files, the retail range index, settings
        public string AssetsSkip;  // a development folder a macOS release leaves out (Addressables output); the Windows packer has its own list (Pack.SkipDirs)

        /// <summary>The settings and logger overrides HBS ships, packed so an edit in retail never fails an install (GitHub issue 40).</summary>
        public string Settings { get { return Data + "StreamingAssets/data/debug/"; } }
        /// <summary>Mod tools rewrite these assemblies, so retail's copies are never a source of ranges.</summary>
        public string Managed { get { return Data + "Managed/"; } }

        public static readonly Layout Windows = new Layout { Name = "windows", Exe = "BattleTech.exe", Data = "BattleTech_Data/" };
        public static readonly Layout Mac = new Layout
        {
            Name = "mac", Platform = Gate.MacPlatform, Exe = "BattleTech.app/Contents/MacOS/BattleTech",
            Data = "BattleTech.app/Contents/Resources/Data/", AssetsSkip = "BattleTech.app/Contents/Resources/Data/StreamingAssets/aa",
        };

        /// <summary>The .app decides, as hash-install.sh does: a folder that holds BattleTech.app is a macOS install.</summary>
        public static Layout Of(string root)
        {
            return Directory.Exists(Path.Combine(root, "BattleTech.app")) ? Mac : Windows;
        }

        /// <summary>
        /// The fingerprint: the executable whole, and three 1 MiB windows (start, middle, end) of each sampled file, each part
        /// labelled by its lower-cased path, so the same bytes in the other layout give another fingerprint. For the Windows
        /// layout it is Gate.Fingerprint's own arithmetic. parts, when given, receives each part's label and digest.
        /// </summary>
        public byte[] Fingerprint(string root, List<KeyValuePair<string, string>> parts)
        {
            using (var h = Gate.NewSha())
            using (var ms = new MemoryStream())
            {
                Action<string, byte[]> add = (label, digest) =>
                {
                    byte[] l = Encoding.UTF8.GetBytes(label.ToLowerInvariant());
                    ms.Write(l, 0, l.Length);
                    ms.Write(digest, 0, digest.Length);
                    if (parts != null) parts.Add(new KeyValuePair<string, string>(label.ToLowerInvariant(), Gate.Hex(digest)));
                };
                string exe = Path.Combine(root, Exe);
                if (!File.Exists(exe)) throw new FileNotFoundException("Retail file not found", Exe);
                add(Exe, h.ComputeHash(File.ReadAllBytes(exe)));
                foreach (string name in Sampled)
                {
                    string rel = Data + name, p = Path.Combine(root, rel);
                    if (!File.Exists(p)) throw new FileNotFoundException("Retail file not found", rel);
                    using (var f = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        long len = f.Length;
                        var buf = new byte[Window];
                        foreach (long at in new[] { 0L, Math.Max(0, len / 2 - Window / 2), Math.Max(0, len - Window) })
                        {
                            f.Position = at;
                            int got = 0, n;
                            while (got < Window && (n = f.Read(buf, got, Window - got)) > 0) got += n;
                            add(rel + "@" + at + "/" + len, h.ComputeHash(buf, 0, got));
                        }
                    }
                }
                return h.ComputeHash(ms.ToArray());
            }
        }

        /// <summary>
        /// Compares this install's fingerprint with a hash-install report (the volunteer script's, [fingerprint] and
        /// [fingerprint-parts]) and returns "parts n of m, fingerprint equal: yes|no". Digests are compared, never printed:
        /// a differing part is named by its label.
        /// </summary>
        public string CheckReport(string root, string report, out bool allEqual)
        {
            var mine = new List<KeyValuePair<string, string>>();
            string fp = Gate.Hex(Fingerprint(root, mine));
            List<string> want = Section(report, "fingerprint-parts"), whole = Section(report, "fingerprint");
            var msg = new StringBuilder();
            int same = 0;
            for (int i = 0; i < Math.Max(mine.Count, want.Count); i++)
            {
                string a = i < mine.Count ? mine[i].Key + "\t" + mine[i].Value : null, b = i < want.Count ? want[i] : null;
                if (a == b) same++;
                else
                {
                    string row = a ?? b;   // a report's row isn't trusted to be "label, tab, value": never print a line that has no tab
                    string label = row.IndexOf('\t') > 0 ? row.Substring(0, row.IndexOf('\t')) : "";
                    if (label.Length >= 32 && label.All(Uri.IsHexDigit)) label = "";
                    msg.Append("part ").Append(i).Append(" differs: ").Append(label.Length > 0 ? label : "(a row without a label)").Append("; ");
                }
            }
            bool fpEqual = whole.Count == 1 && whole[0] == fp;
            allEqual = same == want.Count && same == mine.Count && want.Count > 0 && fpEqual;
            return msg + string.Format("parts {0} of {1}, fingerprint equal: {2}", same, want.Count, fpEqual ? "yes" : "no");
        }

        static List<string> Section(string report, string name)
        {
            var l = new List<string>();
            bool inside = false;
            foreach (string line in File.ReadLines(report))
            {
                if (line.StartsWith("[")) { if (inside) break; inside = line == "[" + name + "]"; continue; }
                if (inside && line.Length > 0) l.Add(line);
            }
            return l;
        }

        /// <summary>True for files a macOS build leaves behind that a release never carries: Finder's, debug symbols, Unity's do-not-ship folders.</summary>
        public static bool IsMacJunk(string rel)
        {
            foreach (string seg in rel.Replace('\\', '/').Split('/'))
                if (seg == ".DS_Store" || seg.EndsWith(".dSYM", StringComparison.OrdinalIgnoreCase)
                    || seg.EndsWith("_BurstDebugInformation_DoNotShip", StringComparison.OrdinalIgnoreCase)
                    || seg.EndsWith("_BackUpThisFolder_ButDontShipItWithYourGame", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    /// <summary>
    /// A signed Mac bundle's _CodeSignature/CodeResources is a plist of every file's hash. The packer checks that it lists exactly
    /// the player's files with the right SHA-256 (hash2), so the installer, which writes those bytes and never signs, installs a
    /// bundle whose seal holds. Files the signature covers by other means are exempt (Info.plist, the main executable, the old
    /// Contents/CodeResources). Nested code (an entry with cdhash and no hash2) is checked as Apple's and Unity's signers seal it:
    /// a single file by its code directory (MachOSeal), a nested bundle (a folder, listed as one entry, its files not listed) by
    /// its executable, and by its own CodeResources when it has one, checked the same way.
    /// </summary>
    public static class CodeResources
    {
        public const string SealFile = "_CodeSignature/CodeResources";

        sealed class Listed { public string Hash2; public byte[] CdHash; public string Requirement; public bool Link; }

        /// <summary>Checks a bundle's Contents folder, looking only at files ships accepts (all, when null). Returns what is wrong (paths only; at most 20), empty when consistent.</summary>
        public static List<string> Check(string contents, out int files, out int nested, Func<string, bool> ships = null)
        {
            var problems = new List<string>();
            int f = 0, n = 0;
            string exe = PlayerExecutable(contents);
            if (exe == null) problems.Add("Info.plist has no safe CFBundleExecutable naming a file in MacOS/");
            Run(contents, "", ships ?? (x => true), exe ?? "", problems, ref f, ref n);
            files = f; nested = n;
            if (exe != null) CheckApp(contents, exe, problems);
            if (problems.Count > 20) { int c = problems.Count; problems = problems.Take(20).ToList(); problems.Add("... " + (c - 20) + " more"); }
            return problems;
        }

        /// <summary>One CodeResources against its folder. prefix names the folder inside the outer bundle (for messages and for ships); exe is this bundle's main executable.</summary>
        static void Run(string contents, string prefix, Func<string, bool> ships, string exe, List<string> problems, ref int files, ref int nested)
        {
            string plist = Path.Combine(contents, SealFile);
            if (!File.Exists(plist)) { problems.Add("no " + prefix + SealFile + " (the player isn't signed)"); return; }
            Dictionary<string, Listed> listed = Read(plist);
            string[] exempt = { "Info.plist", "CodeResources", "MacOS/" + exe };
            var bundles = new List<string>();
            foreach (var kv in listed)
            {
                if (kv.Key.StartsWith("/", StringComparison.Ordinal) || kv.Key.Split('/').Contains("..")) { problems.Add("unsafe path in " + prefix + SealFile + ": " + kv.Key); continue; }
                if (kv.Value.Hash2 == null && kv.Value.CdHash != null && Directory.Exists(Path.Combine(contents, kv.Key))) bundles.Add(kv.Key);
            }
            var tree = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string file in Directory.EnumerateFiles(contents, "*", SearchOption.AllDirectories))
            {
                string rel = file.Substring(contents.TrimEnd('\\', '/').Length + 1).Replace('\\', '/');
                if (bundles.Any(b => rel.StartsWith(b + "/", StringComparison.Ordinal))) continue;   // a nested bundle's files are its own seal's business
                if (ships(prefix + rel)) tree[rel] = file;
            }
            foreach (var kv in tree.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                if (kv.Key == SealFile || exempt.Contains(kv.Key)) continue;
                Listed e;
                if (!listed.TryGetValue(kv.Key, out e)) { problems.Add("not in CodeResources: " + prefix + kv.Key); continue; }
                if (e.Hash2 != null)
                {
                    files++;
                    using (var h = Gate.NewSha())
                    using (var s = new FileStream(kv.Value, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
                        if (Convert.ToBase64String(h.ComputeHash(s)) != e.Hash2) problems.Add("hash differs: " + prefix + kv.Key);
                }
                else if (e.CdHash != null)
                {
                    nested++;
                    string why = MachOSeal.Verify(kv.Value, e.CdHash, e.Requirement, false, false, null, null);
                    if (why != null) problems.Add("nested code " + why + ": " + prefix + kv.Key);
                }
                else if (!e.Link) problems.Add("no hash2 or cdhash in CodeResources: " + prefix + kv.Key);
            }
            foreach (string k in listed.Keys.OrderBy(x => x, StringComparer.Ordinal))
                if (!tree.ContainsKey(k) && !bundles.Contains(k) && !listed[k].Link) problems.Add("listed but not in the bundle: " + prefix + k);
            foreach (string b in bundles.OrderBy(x => x, StringComparer.Ordinal))
            {
                nested++;
                CheckBundle(contents, b, listed[b].CdHash, listed[b].Requirement, prefix, ships, problems, ref files, ref nested);
            }
        }

        /// <summary>
        /// The root of the seal: the app's executable carries the signature that seals Info.plist (slot 1) and CodeResources (slot 3),
        /// and both are exempt from the list, so they are checked here: the executable's pages and its two slots against the files.
        /// </summary>
        static void CheckApp(string contents, string executable, List<string> problems)
        {
            string exe = Path.Combine(contents, "MacOS", executable), info = Path.Combine(contents, "Info.plist"), seal = Path.Combine(contents, SealFile);
            if (!File.Exists(seal) || problems.Any(x => x.StartsWith("no ", StringComparison.Ordinal))) return;   // unsigned: said already
            if (!File.Exists(exe)) { problems.Add("no MacOS/" + executable); return; }
            if (!File.Exists(info)) { problems.Add("no Info.plist"); return; }
            string why = MachOSeal.Verify(exe, null, null, true, true, File.ReadAllBytes(info), File.ReadAllBytes(seal));
            if (why != null) problems.Add("the app's executable " + why + ": MacOS/" + executable);
        }

        /// <summary>A nested bundle: its executable against the cdhash its parent lists, and its own CodeResources (when it has one) like any other.</summary>
        static void CheckBundle(string contents, string rel, byte[] cdhash, string requirement, string prefix, Func<string, bool> ships, List<string> problems, ref int files, ref int nested)
        {
            string dir = Path.Combine(contents, rel), name = prefix + rel;
            bool deep = Directory.Exists(Path.Combine(dir, "Contents"));
            string inner = deep ? Path.Combine(dir, "Contents") : dir;
            string exe = BundleExecutable(Path.Combine(inner, "Info.plist"));
            if (exe == null) { problems.Add("nested bundle without an executable named in its Info.plist: " + name); return; }
            string exePath = Path.Combine(inner, "MacOS", exe);
            if (!File.Exists(exePath)) { problems.Add("nested bundle without its executable: " + name); return; }
            string seal = Path.Combine(inner, SealFile);
            byte[] sealBytes = File.Exists(seal) ? File.ReadAllBytes(seal) : null;
            string why = MachOSeal.Verify(exePath, cdhash, requirement, true, false, File.ReadAllBytes(Path.Combine(inner, "Info.plist")), sealBytes);
            if (why != null) problems.Add("nested code " + why + ": " + name);
            if (sealBytes == null) return;
            try { Run(inner, prefix + rel + (deep ? "/Contents/" : "/"), ships, exe, problems, ref files, ref nested); }
            catch (Exception e) when (e is XmlException || e is InvalidDataException) { problems.Add("nested " + SealFile + " can't be read: " + name); }
        }

        /// <summary>CFBundleExecutable of an Info.plist, null when the file or the key is missing.</summary>
        static string BundleExecutable(string infoPlist)
        {
            if (!File.Exists(infoPlist)) return null;
            var rs = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true };
            try
            {
                using (XmlReader r = XmlReader.Create(infoPlist, rs))
                    while (r.Read())
                        if (r.NodeType == XmlNodeType.Element && r.Name == "key" && r.ReadElementContentAsString() == "CFBundleExecutable")
                        {
                            r.MoveToContent();
                            return r.Name == "string" && !r.IsEmptyElement ? r.ReadElementContentAsString() : null;
                        }
            }
            catch (XmlException) { }   // a binary property list: not read here
            return null;
        }

        /// <summary>CFBundleExecutable for the app, only when it is a safe filename naming an existing MacOS file.</summary>
        public static string PlayerExecutable(string contents)
        {
            string name = BundleExecutable(Path.Combine(contents, "Info.plist"));
            if (string.IsNullOrWhiteSpace(name) || name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 || name.Contains("..")) return null;
            return File.Exists(Path.Combine(contents, "MacOS", name)) ? name : null;
        }

        /// <summary>files2: path -> its hash2 (base64 SHA-256), its cdhash (nested code), or neither (a link, or something unknown).</summary>
        static Dictionary<string, Listed> Read(string plist)
        {
            var map = new Dictionary<string, Listed>(StringComparer.Ordinal);
            bool hasFiles2 = false;
            var rs = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true };
            using (XmlReader r = XmlReader.Create(plist, rs))
            {
                r.MoveToContent();
                r.ReadStartElement("plist");
                r.MoveToContent();
                if (r.IsEmptyElement || r.Name != "dict") throw new InvalidDataException("CodeResources isn't a property list dictionary");
                r.ReadStartElement("dict");
                while (r.MoveToContent() == XmlNodeType.Element)
                {
                    string key = r.ReadElementContentAsString();
                    r.MoveToContent();
                    if (key == "files2" && r.Name == "dict")
                    {
                        hasFiles2 = true;
                        if (!r.IsEmptyElement) ReadFiles2(r, map);
                        else r.Skip();
                    }
                    else r.Skip();
                }
            }
            if (!hasFiles2) throw new InvalidDataException("CodeResources has no files2 section");
            return map;
        }

        static void ReadFiles2(XmlReader r, Dictionary<string, Listed> map)
        {
            r.ReadStartElement("dict");
            while (r.MoveToContent() == XmlNodeType.Element)
            {
                string name = r.ReadElementContentAsString();
                r.MoveToContent();
                var e = new Listed();
                if (r.Name == "dict" && !r.IsEmptyElement)
                {
                    r.ReadStartElement("dict");
                    while (r.MoveToContent() == XmlNodeType.Element)
                    {
                        string k = r.ReadElementContentAsString();
                        r.MoveToContent();
                        if (k == "hash2") e.Hash2 = new string(r.ReadElementContentAsString().Where(c => !char.IsWhiteSpace(c)).ToArray());
                        else if (k == "cdhash")
                        {
                            try { e.CdHash = Convert.FromBase64String(r.ReadElementContentAsString().Trim()); } catch (FormatException) { e.CdHash = new byte[0]; }
                        }
                        else if (k == "requirement") e.Requirement = r.ReadElementContentAsString();
                        else if (k == "symlink") { e.Link = true; r.Skip(); }
                        else r.Skip();
                    }
                    r.ReadEndElement();
                }
                else r.Skip();
                map[name] = e;
            }
            r.ReadEndElement();
        }
    }

    /// <summary>
    /// The code signature of a Mach-O file (thin or universal): each slice's code directories, whether the hashes of the slice's
    /// pages match the bytes, and whether the cdhash a CodeResources lists is one of them. A cdhash alone binds the code
    /// directory, so without the page hashes a changed library would still carry its old seal. Nothing here prints a digest.
    /// </summary>
    public static class MachOSeal
    {
        static uint BE(byte[] b, long o) { Need(b, o, 4); return (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]); }
        static uint LE(byte[] b, long o) { Need(b, o, 4); return (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24); }
        static void Need(byte[] b, long o, long n) { if (o < 0 || n < 0 || o + n > b.Length) throw new InvalidDataException("truncated"); }

        static HashAlgorithm Algo(int type)
        {
            switch (type)
            {
                case 1: return SHA1.Create();
                case 2: case 3: return SHA256.Create();
                case 4: return SHA384.Create();
                default: throw new InvalidDataException("unknown hash type");
            }
        }

        static bool Same(byte[] a, long ao, byte[] b, long bo, int n)
        {
            Need(a, ao, n); Need(b, bo, n);
            for (int i = 0; i < n; i++) if (a[ao + i] != b[bo + i]) return false;
            return true;
        }

        static bool Zero(byte[] a, long ao, int n)
        {
            Need(a, ao, n);
            for (int i = 0; i < n; i++) if (a[ao + i] != 0) return false;
            return true;
        }

        /// <summary>
        /// Null when the file's signature holds, otherwise the fault in words. cdhash (when given) must be one of the file's code
        /// directories' hashes. requirement, when it names cdhashes (Unity's signer lists one per architecture: "cdhash H"..." or
        /// ...), binds every architecture: the file must have exactly those, each in the list and each listed one in the file, so a
        /// swapped, foreign or missing slice is refused. A designated requirement with no cdhash (Apple's own signing) can't bind
        /// the others: then only the listed cdhash is. bundle: the file is a bundle's executable, so its Info.plist and CodeResources
        /// bytes (null when the bundle has none) must agree with the special slots; app: it is the app's own, so Info.plist must be sealed.
        /// </summary>
        public static string Verify(string path, byte[] cdhash, string requirement, bool bundle, bool app, byte[] infoPlist, byte[] resources)
        {
            try
            {
                byte[] f = File.ReadAllBytes(path);
                var slices = new List<long[]>();
                if (f.Length >= 8 && BE(f, 0) == 0xCAFEBABE)
                {
                    uint n = BE(f, 4);
                    if (n == 0 || n > 16) return "isn't a Mach-O file";
                    for (uint i = 0; i < n; i++) slices.Add(new long[] { BE(f, 8 + 20 * i + 8), BE(f, 8 + 20 * i + 12) });
                }
                else if (f.Length >= 32 && (LE(f, 0) == 0xFEEDFACF || LE(f, 0) == 0xFEEDFACE)) slices.Add(new long[] { 0, f.Length });
                else return "isn't a Mach-O file";
                var hashes = new List<List<byte[]>>();   // per slice, the cdhash of each of its code directories
                foreach (long[] sl in slices)
                {
                    long off = sl[0], size = sl[1];
                    Need(f, off, size);
                    long sig = -1;
                    long p = off + (LE(f, off) == 0xFEEDFACF ? 32 : 28);
                    uint ncmds = LE(f, off + 16);
                    for (uint i = 0; i < ncmds; i++)
                    {
                        uint cmd = LE(f, p), len = LE(f, p + 4);
                        if (len < 8) return "has a damaged header";
                        if (cmd == 0x1D) { sig = off + LE(f, p + 8); break; }
                        p += len;
                    }
                    if (sig < 0 || BE(f, sig) != 0xFADE0CC0) return "isn't signed";
                    uint count = BE(f, sig + 8);
                    var mine = new List<byte[]>();
                    for (uint i = 0; i < count; i++)
                    {
                        uint type = BE(f, sig + 12 + 8 * i);
                        if (type != 0 && !(type >= 0x1000 && type <= 0x1004)) continue;
                        long cd = sig + BE(f, sig + 16 + 8 * i);
                        if (BE(f, cd) != 0xFADE0C02) return "has a damaged signature";
                        long cdLen = BE(f, cd + 4);
                        if (cdLen < 48) return "has a damaged signature";
                        Need(f, cd, cdLen);
                        string bad = CheckPagesAndSlots(f, off, size, cd, bundle, app, infoPlist, resources);
                        if (bad != null) return bad;
                        using (var h = Algo(f[cd + 37]))
                        {
                            byte[] full = h.ComputeHash(f, (int)cd, (int)cdLen);
                            var c20 = new byte[20]; Array.Copy(full, c20, 20);
                            mine.Add(c20);
                        }
                    }
                    if (mine.Count == 0) return "isn't signed";
                    hashes.Add(mine);
                }
                if (cdhash != null && !hashes.Any(m => m.Any(h => h.SequenceEqual(cdhash)))) return "doesn't match the cdhash CodeResources lists";
                List<byte[]> named = Named(requirement);
                if (named.Count > 0)
                {
                    if (named.Count != hashes.Count) return "has " + hashes.Count + " architecture(s) but its signature list names " + named.Count;
                    if (hashes.Any(m => !m.Any(h => named.Any(r => r.SequenceEqual(h))))) return "has an architecture its signature list doesn't name";
                    if (named.Any(r => !hashes.Any(m => m.Any(h => h.SequenceEqual(r))))) return "lacks an architecture its signature list names";
                }
                return null;
            }
            catch (Exception e) when (e is InvalidDataException || e is ArgumentException || e is OverflowException) { return "has a damaged signature"; }
        }

        /// <summary>The cdhash terms of a requirement string ("cdhash H"hex" or cdhash H"hex""), 20 bytes each; none for any other requirement.</summary>
        static List<byte[]> Named(string requirement)
        {
            var l = new List<byte[]>();
            if (requirement == null) return l;
            int at = 0;
            while ((at = requirement.IndexOf("cdhash H\"", at, StringComparison.Ordinal)) >= 0)
            {
                at += 9;
                int end = requirement.IndexOf('"', at);
                if (end < 0 || end - at != 40) break;
                var b = new byte[20];
                for (int i = 0; i < 20; i++) b[i] = Convert.ToByte(requirement.Substring(at + 2 * i, 2), 16);
                l.Add(b);
                at = end;
            }
            return l;
        }

        /// <summary>One code directory: every page of the slice hashes to its slot, and a bundle's Info.plist and CodeResources agree with their special slots.</summary>
        static string CheckPagesAndSlots(byte[] f, long off, long size, long cd, bool bundle, bool app, byte[] infoPlist, byte[] resources)
        {
            uint hashOffset = BE(f, cd + 16), nSpecial = BE(f, cd + 24), nCode = BE(f, cd + 28), limit = BE(f, cd + 32);
            int hashSize = f[cd + 36], type = f[cd + 37], pageLog = f[cd + 39];
            if (limit == 0 || limit > size || hashSize < 1) return "has a damaged signature";
            long page = pageLog == 0 ? limit : 1L << pageLog;
            if (pageLog > 24 || nCode != (limit + page - 1) / page) return "has a damaged signature";
            using (HashAlgorithm h = Algo(type))
            {
                if (hashSize > h.HashSize / 8) return "has a damaged signature";
                for (long i = 0; i < nCode; i++)
                {
                    long at = i * page, len = Math.Min(page, limit - at);
                    Need(f, off + at, len);
                    byte[] got = h.ComputeHash(f, (int)(off + at), (int)len);
                    if (!Same(got, 0, f, cd + hashOffset + i * hashSize, hashSize)) return "differs from its signature";
                }
                if (bundle)
                {
                    string bad = Slot(f, cd, hashOffset, nSpecial, hashSize, 1, infoPlist, h, "Info.plist", app);
                    if (bad == null) bad = Slot(f, cd, hashOffset, nSpecial, hashSize, 3, resources, h, "CodeResources", true);
                    if (bad != null) return bad;
                }
            }
            return null;
        }

        /// <summary>A special slot: when it is set it must equal the file's hash; a bundle's CodeResources must be sealed, and a slot that seals one the bundle lacks is a fault.</summary>
        static string Slot(byte[] f, long cd, uint hashOffset, uint nSpecial, int hashSize, uint slot, byte[] bytes, HashAlgorithm h, string what, bool required)
        {
            bool set = slot <= nSpecial && !Zero(f, cd + hashOffset - slot * hashSize, hashSize);
            if (!set) return required && bytes != null ? what + " isn't sealed by its executable" : null;
            if (bytes == null) return what + " is sealed but missing";
            return Same(h.ComputeHash(bytes), 0, f, cd + hashOffset - slot * hashSize, hashSize) ? null : what + " differs from its signature";
        }
    }
}
#endif
