// Packer: turns a built player into a package. Never ship this tool.
//   ReleasePack --player <dir> --retail <dir> --key <file> --out <new dir> [--extra-fingerprint <hex>]...
//               [--master-secret <file> --unlock-url <url>]   a locked package, unlocked by the unlock server (docs/keyserver-design.md)
//               [--unity <Editor folder>]   marks files byte-equal to that Unity Editor's player files as "unity" in the public
//                                           manifest (PublicManifest.cs); default: the player's own version under Unity Hub
//               [--gog <GOG install>]       also accepts that GOG install: its fingerprint slot, and a GOG section in the vault for
//                                           the retail files whose GOG bytes differ (GOG's own copy where gog-accept.tsv lists it,
//                                           else Steam's packed) plus GOG's Galaxy natives; writes ucu-manifest-gog.tsv
//   ReleasePack --fingerprint <retail dir>     prints a retail install's fingerprint, for adding a slot
//   ReleasePack --check-report <retail dir> <report>   (macOS build of this tool) compares a retail install's fingerprint with a
//                                           hash-install report and prints "parts n of 19, fingerprint equal: yes|no", never a digest
//   macOS packages (the macOS build of this tool, UCU_MAC): a --player that holds BattleTech.app is packed with the --retail Mac
//               install of the same layout, always locked (--master-secret and --unlock-url), as package-mac.vault (vault BTU6GAM4),
//               ucu-manifest-mac.tsv and data/*.pak; no installer is copied. --gog takes a GOG macOS install (the folder that
//               holds BattleTech.app and Contents/Resources/goggame-*.info): its fingerprint slot and a GOG section that installs
//               the player's own bytes wherever GOG's differ, with no Galaxy files; writes ucu-manifest-mac-gog.tsv. It takes its own content key,
//               --mac-key <file> in place of --key (key separation): a file the packer makes, "mac:" and the hex; a Windows key
//               file is refused, so the key that opens the Windows data is never the one a Mac owner's files-only unlock returns.
//               [--previous <package folder>]   reuses that package's work: a file whose SHA-256 is unchanged keeps its pieces and its
//                                           blob (hard-linked, or copied across volumes), so only new and changed files are scanned
//                                           and sealed. The result is the same package a full pack gives. Needs a package made by a
//                                           packer with retail ranges; give --master-secret when it is locked. Only the pieces are
//                                           reused, and every blob is sealed anew, when that package is older than vault v4 or has
//                                           another content key.
//               [--new-key]                 with --previous: this release moves to another content key on purpose
// The key file holds the content key, which every release reuses so unchanged files keep the same blob. It is created on
// first use. Keep it out of the repository and out of packages; a lost key only means the next release is uploaded whole.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ReleaseGate
{
    /// <summary>
    /// An earlier package, read so a new pack can reuse its work (Pack.Run, previous). Nothing in it is trusted without a check:
    /// a file is reused only when its SHA-256 equals the old entry's, a blob only when its own SHA-256 equals the vault's record,
    /// and range pieces only while the retail files they name are still long enough.
    /// </summary>
    sealed class PreviousPack
    {
        public string Dir;
        public bool Blobs;                          // its blobs are the ones this packer seals: vault v4, same content key
        public Dictionary<string, Entry> Steam;     // the vault's main manifest, by path
        public Dictionary<string, Entry> Gog;       // the manifest a GOG install gets; null when the old package took no GOG install
        public int Linked, Copied;
        readonly ConcurrentDictionary<string, long> _lengths = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateHardLink(string newFile, string existingFile, IntPtr security);

        static Dictionary<string, Entry> ByPath(List<Entry> list)
        {
            var d = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (Entry e in list) d[e.Path] = e;
            return d;
        }

        public static PreviousPack Load(string dir, byte[] key, bool newKey, byte[] steamFingerprint, byte[] gogFingerprint, byte[] master)
        {
            dir = Path.GetFullPath(dir);
            string vault = Path.Combine(dir, Gate.VaultName);
            if (!File.Exists(vault)) throw new FileNotFoundException("The previous package has no " + Gate.VaultName + ": " + dir, vault);
            var head = new byte[8];
            using (var f = new FileStream(vault, FileMode.Open, FileAccess.Read, FileShare.Read))
                if (f.Read(head, 0, head.Length) < head.Length) head = null;
            if (!Gate.IsRangedVault(head))
                throw new ArgumentException("The previous package was made before retail-range packing (or its vault isn't one), so its files were never scanned for retail ranges. Pack without --previous once; later packs can reuse that one.");
            Func<VaultLock, byte[]> share = lk =>
            {
                if (master == null) throw new UnlockException("The previous package needs the unlock server's master secret to be read: give --master-secret (or fill the Master secret box).");
                return Gate.Share(master, lk.ReleaseId);
            };
            var p = new PreviousPack { Dir = dir };
            List<Entry> main;
            byte[] k = Read(vault, steamFingerprint, share, null, out main);
            if (k == null) throw new ArgumentException("The previous package doesn't accept this retail install.");
            bool sameKey = k.SequenceEqual(key);
            if (!sameKey && !newKey) throw new ArgumentException("The previous package was packed under a different content key, so its blobs can't be reused. Check the key file. If this release moves to another key, add --new-key (ReleasePack), or pack without --previous.");
            if (sameKey && newKey) throw new ArgumentException("--new-key was given, but the previous package was packed under this same content key.");
            p.Blobs = sameKey && main.All(e => e.Blob.Length == 0 || e.BlobPlain != null);
            p.Steam = ByPath(main);
            if (gogFingerprint != null)
            {
                List<Entry> merged;
                byte[] k2 = Read(vault, gogFingerprint, share, Gate.Gog, out merged);
                if (k2 != null && k2.SequenceEqual(k)) p.Gog = ByPath(merged);
            }
            return p;
        }

        static byte[] Read(string vault, byte[] fingerprint, Func<VaultLock, byte[]> share, string store, out List<Entry> entries)
        {
            try
            {
                using (var f = new FileStream(vault, FileMode.Open, FileAccess.Read, FileShare.Read))
                    return Gate.ReadVault(f, fingerprint, share, store, out entries);
            }
            catch (UnlockException e) { throw new ArgumentException(e.Message); }
        }

        /// <summary>True when every retail range still lies inside a file of that name under root, and the pieces add up to size.</summary>
        public bool Fits(List<RangePiece> pieces, string root, long size)
        {
            long total = 0;
            foreach (RangePiece p in pieces)
            {
                total += p.Length;
                if (!p.FromRetail) continue;
                string path = Path.Combine(root, p.Path.Replace('/', '\\'));
                long n;
                if (!_lengths.TryGetValue(path, out n))
                {
                    n = File.Exists(path) ? new FileInfo(path).Length : -1;
                    _lengths[path] = n;
                }
                if (n < 0 || p.Offset < 0 || p.Offset + p.Length > n) return false;
            }
            return total == size;
        }

        /// <summary>The old blob's SHA-256 for a whole-sealed GOG entry of this file, or null.</summary>
        public string GogWhole(string path, string sha)
        {
            Entry e;
            return Gog != null && Gog.TryGetValue(path, out e) && e.Sha256 == sha && e.RetailPath.Length == 0 && string.IsNullOrEmpty(e.Pieces) && e.Blob.Length > 0 ? e.BlobSha256 : null;
        }

        /// <summary>
        /// Puts the old package's blob at dst when it is there and its SHA-256 is what the old vault recorded: a hard link, or a
        /// copy where links aren't possible (another volume). Returns that SHA-256, or null when the blob has to be sealed anew.
        /// </summary>
        public string Reuse(string blob, string expectSha, string dst)
        {
            string old = Path.Combine(Dir, blob.Replace('/', '\\'));
            if (!Blobs || !File.Exists(old) || !string.Equals(Gate.HashFile(old), expectSha, StringComparison.OrdinalIgnoreCase)) return null;
            if (CreateHardLink(dst, old, IntPtr.Zero)) Interlocked.Increment(ref Linked);
            else { File.Copy(old, dst); Interlocked.Increment(ref Copied); }
            return expectSha;
        }
    }

    public sealed class PackProgress
    {
        public long Done;    // bytes of player files finished
        public long Total;   // bytes of player files to pack
    }

    public sealed class OutputExistsException : IOException
    {
        public OutputExistsException(string output)
            : base("The package folder already exists: " + output + ". Move or delete the old package first, or pick another folder.") { }
    }

    public sealed class PackResult
    {
        public int Files, Encrypted, Blobs, FromRetail, Ranged;
        public long SealedBytes, PackedBytes, CopiedBytes, LargestBlob, SteamRangeBytes, GogRangeBytes;
        public string Summary;
        public string ReleaseId;   // locked only: the id the unlock server is asked about
        public string ManifestSha256;   // of ucu-manifest.tsv, for the release notes
        public string GogManifestSha256;   // of ucu-manifest-gog.tsv; null when the package doesn't take GOG installs
    }

    public static class Pack
    {
        // Development and per-machine files that are not part of a release.
        static readonly string[] SkipDirs = { "Mods", "ModSaves", "BTASaves", "DumpBox", "Mods_sfx", "Music", "BattleTech_Data/StreamingAssets/aa" };
        static readonly string[] SkipFiles = { "MapProbe.dll", "DlcProbe.dll", "doorstop_config.probe.ini", "doorstop_config.modded.ini", "HarmonyFileLog.log", "BattleTechPortBootstrap.log",
            // The mod bootstrap needs Mods/, which a vanilla release leaves out.
            "winhttp.dll", "doorstop_config.ini", "BattleTechPortBootstrap.dll", "doorstop_config.off.ini", ".doorstop_version",
            // Probe left in full-6 by the visual track's build checks.
            "VisualBuildProbe.dll" };
        // Present in a dev retail folder but not in Steam's depot, so they can't be copied from a user's install.
        static readonly string[] NeverFromRetail = { "steam_appid.txt" };
        // HBS's settings and logger overrides, which players and mod installers edit in retail (GitHub issue 40: BEX turns
        // off the splash screens there). Packed, so an edit in retail never fails the install.
        const string PlayerSettings = "BattleTech_Data/StreamingAssets/data/debug/";

        static int Main(string[] a)
        {
            try
            {
                if (a.Length == 2 && a[0] == "--fingerprint")
                {
                    Console.WriteLine(Gate.Hex(Gate.Fingerprint(a[1])));
                    return 0;
                }
#if UCU_MAC
                if (a.Length == 3 && a[0] == "--check-report")
                {
                    bool equal;
                    Console.WriteLine(Layout.Of(a[1]).CheckReport(a[1], a[2], out equal));
                    return equal ? 0 : 1;
                }
#endif
                string player = null, retail = null, output = null, keyFile = null, masterFile = null, unlockUrl = null, unity = null, gog = null, previous = null;
#if UCU_MAC
                string macKey = null;
#endif
                var extra = new List<byte[]>();
                bool newKey = a.Contains("--new-key");
                if (newKey) a = a.Where(x => x != "--new-key").ToArray();
                for (int i = 0; i + 1 < a.Length; i += 2)
                {
                    if (a[i] == "--player") player = a[i + 1];
                    else if (a[i] == "--retail") retail = a[i + 1];
                    else if (a[i] == "--out") output = a[i + 1];
                    else if (a[i] == "--key") keyFile = a[i + 1];
#if UCU_MAC
                    else if (a[i] == "--mac-key") macKey = a[i + 1];
#endif
                    else if (a[i] == "--extra-fingerprint") extra.Add(FromHex(a[i + 1]));
                    else if (a[i] == "--master-secret") masterFile = a[i + 1];
                    else if (a[i] == "--unlock-url") unlockUrl = a[i + 1];
                    else if (a[i] == "--unity") unity = a[i + 1];
                    else if (a[i] == "--gog") gog = a[i + 1];
                    else if (a[i] == "--previous") previous = a[i + 1];
                    else throw new ArgumentException("Unknown option " + a[i]);
                }
#if UCU_MAC
                bool macPlayer = player != null && Directory.Exists(player) && Layout.Of(Path.GetFullPath(player)) == Layout.Mac;
                if (macPlayer)
                {
                    if (keyFile != null) throw new ArgumentException("A macOS package has its own content key: give --mac-key <file>, not --key. A Windows key file is never used for it.");
                    if (macKey == null) throw new ArgumentException("Need --mac-key <file>: the macOS package's own content key (the packer creates the file on first use).");
                    keyFile = macKey;
                }
                else if (macKey != null) throw new ArgumentException("--mac-key is for a macOS player (a folder that holds BattleTech.app).");
#endif
                if (player == null || retail == null || output == null || keyFile == null) throw new ArgumentException("Need --player, --retail, --key and --out");
                if (Directory.Exists(output)) throw new IOException("Output exists: " + output);
                if ((masterFile == null) != (unlockUrl == null)) throw new ArgumentException("--master-secret and --unlock-url go together");
                if (newKey && previous == null) throw new ArgumentException("--new-key goes with --previous");
                byte[] master = masterFile == null ? null : LoadMaster(masterFile, output);
#if UCU_MAC
                PackResult r = Run(player, retail, output, LoadKey(keyFile, output, Console.WriteLine, macPlayer), extra, new PackProgress(), CancellationToken.None, master, unlockUrl, unity, gog, previous, newKey);
#else
                PackResult r = Run(player, retail, output, LoadKey(keyFile, output, Console.WriteLine), extra, new PackProgress(), CancellationToken.None, master, unlockUrl, unity, gog, previous, newKey);
#endif
                Console.WriteLine(r.Summary);
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("FAILED: " + e.Message);
                return 1;
            }
        }

#if UCU_MAC
        /// <summary>The rows of the public manifest: every packed file but the one a macOS package keeps out of it.</summary>
        static IEnumerable<ManifestRow> Listed(IEnumerable<ManifestRow> rows)
        {
            return rows.Where(r => !PublicManifest.IsHidden(r.Path));
        }

#endif
        public static byte[] FromHex(string s)
        {
            var b = new byte[s.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
            return b;
        }

#if UCU_MAC
        /// <summary>
        /// A macOS package's own content key: a file of "mac:" and 64 hex digits, created on first use. A file in the Windows format
        /// (the hex alone) is refused, and a Windows packer can't read this one, so one key never serves both platforms.
        /// </summary>
        public static byte[] LoadKey(string keyFile, string output, Action<string> log, bool mac)
        {
            if (!mac) return LoadKey(keyFile, output, log);
            keyFile = Path.GetFullPath(keyFile);
            if (keyFile.StartsWith(Path.GetFullPath(output).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Keep the key file outside the package");
            const string Tag = "mac:";
            if (File.Exists(keyFile))
            {
                string text = File.ReadAllText(keyFile).Trim();
                if (!text.StartsWith(Tag, StringComparison.Ordinal))
                    throw new ArgumentException("That key file isn't a macOS content key (it is in the Windows format): a macOS package has its own key. Give a new path with --mac-key and the packer creates it.");
                byte[] k = FromHex(text.Substring(Tag.Length));
                if (k.Length != 32) throw new InvalidDataException("The key file doesn't hold a 32-byte hex key: " + keyFile);
                return k;
            }
            byte[] made = Gate.Random(32);
            File.WriteAllText(keyFile, Tag + Gate.Hex(made));
            log("Created a new macOS content key at " + keyFile + ". Keep it for every later macOS release; it is not the Windows key.");
            return made;
        }

#endif
        public static byte[] LoadKey(string keyFile, string output, Action<string> log)
        {
            keyFile = Path.GetFullPath(keyFile);
            if (keyFile.StartsWith(Path.GetFullPath(output).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Keep the key file outside the package");
            if (File.Exists(keyFile))
            {
                byte[] k = FromHex(File.ReadAllText(keyFile).Trim());
                if (k.Length != 32) throw new InvalidDataException("The key file doesn't hold a 32-byte hex key: " + keyFile);
                return k;
            }
            byte[] made = Gate.Random(32);
            File.WriteAllText(keyFile, Gate.Hex(made));
            log("Created a new content key at " + keyFile + ". Keep it for every later release.");
            return made;
        }

        /// <summary>
        /// The unlock server's master secret (32 bytes, hex). Never created here: a new one must also be set on the server,
        /// so the Release Manager creates it on request and says so.
        /// </summary>
        public static byte[] LoadMaster(string file, string output)
        {
            file = Path.GetFullPath(file);
            if (output != null && file.StartsWith(Path.GetFullPath(output).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Keep the master secret outside the package");
            if (!File.Exists(file)) throw new FileNotFoundException("The master secret file doesn't exist: " + file);
            byte[] k = FromHex(File.ReadAllText(file).Trim());
            if (k.Length != 32) throw new InvalidDataException("The master secret file doesn't hold a 32-byte hex key: " + file);
            return k;
        }

        static string Rel(string root, string path)
        {
            return path.Substring(root.TrimEnd('\\', '/').Length + 1).Replace('\\', '/');
        }

        /// <summary>True when a file at this player-relative path goes into a release.</summary>
        public static bool Ships(string rel)
        {
            rel = rel.Replace('\\', '/');
            // ModSupport/ holds the mod loader's files, inert there (Doorstop only reads files beside the exe). The names below are
            // skipped at the top level, where a development player keeps its probe loader, but ship inside this folder.
#if UCU_MAC
            if (Gate.Platform != null && (Layout.IsMacJunk(rel) || rel.StartsWith(Layout.Mac.AssetsSkip + "/", StringComparison.OrdinalIgnoreCase))) return false;
#endif
            if (rel.StartsWith("ModSupport/", StringComparison.OrdinalIgnoreCase)) return true;
            if (SkipDirs.Any(d => rel.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase))) return false;
            return !SkipFiles.Contains(Path.GetFileName(rel), StringComparer.OrdinalIgnoreCase);
        }

        public static PackResult Run(string player, string retail, string output, byte[] key, List<byte[]> extra, PackProgress progress, CancellationToken stop,
            byte[] masterSecret = null, string unlockUrl = null, string unity = null, string gog = null, string previousPackage = null, bool newKey = false)
        {
#if UCU_MAC
            // The player's layout decides the platform; the retail install must be the same one, and everything that follows
            // (vault magic, file names, fingerprint, retail paths) follows Gate.Use.
            Layout lay = Layout.Of(Path.GetFullPath(player));
            if (Layout.Of(Path.GetFullPath(retail)) != lay)
                throw new ArgumentException("The player is a " + lay.Name + " build but --retail is a " + Layout.Of(Path.GetFullPath(retail)).Name + " install: pack a player with the retail install of its own platform.");
            Gate.Use(lay.Platform);
            if (lay.Platform != null && gog != null && Layout.Of(Path.GetFullPath(gog)) != lay)
                throw new ArgumentException("--gog must be a GOG install of the player's own platform (" + lay.Name + "): " + gog);
            if (lay.Platform != null && (masterSecret == null || unlockUrl == null)) throw new ArgumentException("A macOS package is always locked: give --master-secret and --unlock-url.");
#endif
            if (gog != null)
            {
                gog = Path.GetFullPath(gog);
                if (Gate.StoreOf(gog) != Gate.Gog) throw new ArgumentException("--gog must be a GOG install of BATTLETECH (it has no goggame-" + Gate.GogId + ".info in " + Gate.GogInfoDir(gog) + "): " + gog);
            }
            var clock = Stopwatch.StartNew();
            player = Path.GetFullPath(player);
            retail = Path.GetFullPath(retail);
            // A package is never written over another: the old one's blobs, vault and installer would mix with the new.
            if (Directory.Exists(output)) throw new OutputExistsException(Path.GetFullPath(output));
#if UCU_MAC
            if (lay.Platform != null)
            {
                string executable = CodeResources.PlayerExecutable(Path.Combine(player, "BattleTech.app", "Contents"));
                if (executable == null) throw new FileNotFoundException("The player's Info.plist has no safe CFBundleExecutable naming a file in MacOS/. See Package a release in the README.");
            }
            else if (!File.Exists(Path.Combine(player, lay.Exe))) throw new FileNotFoundException("The player has no " + lay.Exe + ". See Package a release in the README.");
#else
            if (!File.Exists(Path.Combine(player, "BattleTech.exe"))) throw new FileNotFoundException("The player has no BattleTech.exe. See Package a release in the README.");
#endif
            if (!Gate.HasAppId(player))
                throw new FileNotFoundException("The player doesn't set the Steam app ID in its code (it predates patch 101) and has no steam_appid.txt holding 637090. See Package a release in the README.");
            // Galaxy64.dll beside the exe turns on the player's GOG mode, and goggame-*.info files are the GOG user's own DLC
            // markers: a GOG install gets them from the user's GOG folder, so a player carrying them would put a Steam install
            // in GOG mode.
#if UCU_MAC
            string gogOnly = lay.Platform != null ? null : new[] { "Galaxy64.dll", "GalaxyCSharpGlue.dll" }.Where(n => File.Exists(Path.Combine(player, n)))
                .Concat(Directory.EnumerateFiles(player, "goggame-*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName)).FirstOrDefault();
#else
            string gogOnly = new[] { "Galaxy64.dll", "GalaxyCSharpGlue.dll" }.Where(n => File.Exists(Path.Combine(player, n)))
                .Concat(Directory.EnumerateFiles(player, "goggame-*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName)).FirstOrDefault();
#endif
            if (gogOnly != null)
                throw new ArgumentException("The player carries " + gogOnly + " beside BattleTech.exe. GOG installs copy that from the user's GOG install; remove it from the player (a Steam install would start in GOG mode).");
            if (Gate.StoreOf(retail) != Gate.Steam) throw new ArgumentException("--retail must be the Steam install: its files are what a Steam install copies. Give a GOG install with --gog.");
            byte[] fingerprint = Gate.Fingerprint(retail);
            if (unity == null) unity = PublicManifest.FindUnity(player);
            var stock = new PublicManifest.UnityIndex(unity);
            VaultLock lk = null;
            if (masterSecret != null)
            {
                Uri u;
                if (!Uri.TryCreate(unlockUrl ?? "", UriKind.Absolute, out u) || u.Scheme != Uri.UriSchemeHttps)
                    throw new ArgumentException("The unlock URL must be an https address: " + unlockUrl);
                lk = new VaultLock { ReleaseId = Gate.Random(16), Url = unlockUrl };
            }

            var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in Directory.EnumerateFiles(retail, "*", SearchOption.AllDirectories))
            {
                string rel = Rel(retail, f);
                if (rel.StartsWith("DumpBox/", StringComparison.OrdinalIgnoreCase)) continue;
                string k = Path.GetFileName(f) + "|" + new FileInfo(f).Length;
                List<string> l;
                if (!index.TryGetValue(k, out l)) index[k] = l = new List<string>();
                l.Add(rel);
            }
            // The earlier package whose work is reused, if one was given. A full pack of the same player is the reference result.
            PreviousPack previous = null;
            if (previousPackage != null)
            {
                if (!Directory.Exists(previousPackage)) throw new DirectoryNotFoundException("The previous package folder doesn't exist: " + previousPackage);
                if (string.Equals(Path.GetFullPath(previousPackage).TrimEnd('\\'), Path.GetFullPath(output).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("--previous can't be the output folder");
                previous = PreviousPack.Load(previousPackage, key, newKey, fingerprint, gog == null ? null : Gate.Fingerprint(gog), masterSecret);
            }
            // Byte ranges of the retail installs, for files that aren't retail-identical but embed retail runs. Built on first use:
            // a pack that reuses an earlier one's scans may never need them.
            var steamRanges = new Lazy<RetailIndex>(() => RetailIndex.Build(retail));
            var gogRanges = gog == null ? null : new Lazy<RetailIndex>(() => RetailIndex.Build(gog));

            var files = new List<string>();
            foreach (string f in Directory.EnumerateFiles(player, "*", SearchOption.AllDirectories))
            {
                string rel = Rel(player, f);
                if (Ships(rel)) files.Add(rel);
            }
#if UCU_MAC
            if (lay.Platform != null)
            {
                // The installer writes the player's own bytes and never signs: its seal must hold as built.
                int hashed, nested;
                List<string> bad = CodeResources.Check(Path.Combine(player, "BattleTech.app", "Contents"), out hashed, out nested, r => Ships("BattleTech.app/Contents/" + r));
                if (bad.Count > 0) throw new InvalidDataException("The player's " + PublicManifest.MacHidden + " doesn't match its files: " + string.Join("; ", bad));
            }
#endif
            progress.Total = files.Sum(r => new FileInfo(Path.Combine(player, r)).Length);

            Directory.CreateDirectory(Path.Combine(output, "data"));
            var entries = new Entry[files.Count];
            var rows = new ManifestRow[files.Count];
            long copied = 0, sealedBytes = 0, blobBytes = 0, largest = 0, steamRange = 0, gogRange = 0, residualBytes = 0;
            int ranged = 0;
            var gogRanged = new ConcurrentDictionary<string, List<RangePiece>>(StringComparer.OrdinalIgnoreCase);
            var gogReuse = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // GOG ranged file -> its old blob's SHA-256
            int reusedFiles = 0, reusedBlobs = 0;
            long reusedBytes = 0;
            // Files with the same content share one blob. A ranged file's blob holds only its residual bytes. reuseSha is the
            // SHA-256 the previous package's vault records for this blob: when that package still holds a blob of that hash, it
            // is linked in and nothing is sealed.
            var blobs = new ConcurrentDictionary<string, Lazy<string>>();
            Func<string, string, List<RangePiece>, string, string> sealOnce = (src, sha, residual, reuseSha) =>
            {
                string blob = Gate.BlobFor(key, sha);
                return blobs.GetOrAdd(sha, _ => new Lazy<string>(() =>
                {
                    string dst = Path.Combine(output, blob);
                    string s = reuseSha == null || previous == null ? null : previous.Reuse(blob, reuseSha, dst);
                    if (s != null) Interlocked.Increment(ref reusedBlobs);
                    else if (residual == null)
                        s = Seal(src, dst, key, sha);
                    else
                        using (var input = new ResidualStream(src, residual)) s = Seal(input, dst, key, sha);
                    long n = new FileInfo(dst).Length;
                    if (reuseSha != null && s == reuseSha) Interlocked.Add(ref reusedBytes, n);
                    Interlocked.Add(ref blobBytes, n);
                    long seen;
                    while (n > (seen = Interlocked.Read(ref largest)) && Interlocked.CompareExchange(ref largest, n, seen) != seen) { }
                    return s;
                })).Value;
            };
            Parallel.ForEach(Enumerable.Range(0, files.Count), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount / 2), CancellationToken = stop }, i =>
            {
                string rel = files[i];
                string src = Path.Combine(player, rel);
                long size = new FileInfo(src).Length;
                string sha, sha512;
                bool exec;
                PublicManifest.HashBoth(src, out sha, out sha512, out exec);
                var e = new Entry { Path = rel, Size = size, Blob = "", BlobSha256 = "", RetailPath = "", Sha256 = sha };
                List<string> candidates;
                if (!NeverFromRetail.Contains(Path.GetFileName(rel), StringComparer.OrdinalIgnoreCase)
#if UCU_MAC
                    && !rel.Replace('\\', '/').StartsWith(lay.Settings, StringComparison.OrdinalIgnoreCase)
#else
                    && !rel.Replace('\\', '/').StartsWith(PlayerSettings, StringComparison.OrdinalIgnoreCase)
#endif
                    && index.TryGetValue(Path.GetFileName(rel) + "|" + size, out candidates))
                    foreach (string c in candidates)
                        if (Gate.HashFile(Path.Combine(retail, c)) == sha) { e.RetailPath = c; break; }
                List<RangePiece> pieces = null;
                // The old package already analysed this exact file (same path, size and SHA-256) against the same retail install:
                // keep its pieces and its blob instead of scanning and sealing again.
                string reuseSha = null;
                Entry old = null;
                bool again = e.RetailPath.Length == 0 && previous != null && previous.Steam.TryGetValue(rel, out old)
                    && old.Sha256 == sha && old.Size == size && old.RetailPath.Length == 0 && old.Blob.Length > 0 && old.BlobSha256.Length > 0;
                if (again && !string.IsNullOrEmpty(old.Pieces))
                {
                    List<RangePiece> kept = Gate.ParsePieces(old.Pieces);
                    if (previous.Fits(kept, retail, size)) pieces = kept; else again = false;
                }
                if (again) { reuseSha = old.BlobSha256; Interlocked.Increment(ref reusedFiles); }
                else if (e.RetailPath.Length == 0 && size >= 64 * 1024)
                {
                    RetailIndex.Scan scan = steamRanges.Value.ScanFile(src, stop);
                    if (scan.Matched >= 1 << 20) pieces = scan.Pieces;
                }
                if (e.RetailPath.Length > 0)
                    Interlocked.Add(ref copied, size);
                else if (pieces != null)
                {
                    string residualSha = RetailRanges.ResidualSha256(src, pieces);
                    e.Pieces = Gate.FormatPieces(pieces);
                    e.Blob = Gate.BlobFor(key, residualSha);
                    e.BlobPlain = residualSha;
                    e.BlobSha256 = sealOnce(src, residualSha, pieces, reuseSha);
                    long fromSteam = RetailRanges.RetailBytes(pieces);
                    Interlocked.Add(ref steamRange, fromSteam);
                    Interlocked.Add(ref sealedBytes, size - fromSteam);
                    Interlocked.Add(ref residualBytes, size - fromSteam);
                    Interlocked.Increment(ref ranged);
                    if (gogRanges != null)
                    {
                        List<RangePiece> g = null;
                        Entry og;
                        if (reuseSha != null && previous.Gog != null && previous.Gog.TryGetValue(rel, out og) && og.Sha256 == sha && !string.IsNullOrEmpty(og.Pieces))
                        {
                            List<RangePiece> kept = Gate.ParsePieces(og.Pieces);
                            if (previous.Fits(kept, gog, size)) { g = kept; gogReuse[rel] = og.BlobSha256; }
                        }
                        if (g == null) g = gogRanges.Value.ScanFile(src, stop).Pieces;
                        gogRanged[rel] = g;
                        Interlocked.Add(ref gogRange, RetailRanges.RetailBytes(g));
                    }
                }
                else
                {
                    e.Blob = Gate.BlobFor(key, sha);
                    e.BlobPlain = sha;
                    e.BlobSha256 = sealOnce(src, sha, null, reuseSha);
                    Interlocked.Add(ref sealedBytes, size);
                }
                entries[i] = e;
                string origin = e.RetailPath.Length > 0 ? PublicManifest.Retail
                    : stock.IsStock(Path.GetFileName(rel), size, sha) ? PublicManifest.Unity
                    : exec ? PublicManifest.Built : PublicManifest.Data;
                rows[i] = new ManifestRow { Path = rel, Size = size, Sha512 = sha512, Origin = origin, Exec = exec };
                Interlocked.Add(ref progress.Done, size);
            });

            var slots = new List<byte[]> { fingerprint };
            slots.AddRange(extra);
            Dictionary<string, List<Entry>> stores = null;
            List<ManifestRow> gogRows = null;
            string gogSummary = "";
            if (gog != null)
            {
                slots.Add(Gate.Fingerprint(gog));
                List<Entry> section = GogSection(player, gog, key, entries, rows, sealOnce, stop, gogRanged, previous, gogReuse, out gogRows, out gogSummary);
                stores = new Dictionary<string, List<Entry>> { { Gate.Gog, section } };
            }
            string vault = Path.Combine(output, Gate.VaultName);
            byte[] share = lk == null ? null : Gate.Share(masterSecret, lk.ReleaseId);
            Gate.WriteVault(vault, key, slots, entries.ToList(), lk, share, stores);
            CheckVault(vault, key, slots, entries, lk, share, stores);
#if UCU_MAC
            string setup = lay.Platform != null ? null : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BattleTechUCUSetup.exe");
            if (setup != null && !File.Exists(setup)) throw new FileNotFoundException("Keep BattleTechUCUSetup.exe beside ReleasePack.exe", setup);
            byte[] manifest = PublicManifest.Write(Listed(rows));
#else
            string setup = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BattleTechUCUSetup.exe");
            if (!File.Exists(setup)) throw new FileNotFoundException("Keep BattleTechUCUSetup.exe beside ReleasePack.exe", setup);
            byte[] manifest = PublicManifest.Write(rows);
#endif
            File.WriteAllBytes(Path.Combine(output, PublicManifest.FileName), manifest);
#if UCU_MAC
            byte[] gogManifest = gogRows == null ? null : PublicManifest.Write(Listed(gogRows));   // the GOG manifest hides CodeResources too
#else
            byte[] gogManifest = gogRows == null ? null : PublicManifest.Write(gogRows);
#endif
            if (gogManifest != null) File.WriteAllBytes(Path.Combine(output, PublicManifest.GogFileName), gogManifest);
#if UCU_MAC
            if (setup != null)
            {
#endif
            File.Copy(setup, Path.Combine(output, "BattleTechUCUSetup.exe"));
#if UCU_MAC
            }
#endif
            var result = new PackResult
            {
                Files = files.Count,
                Encrypted = entries.Count(x => x.RetailPath.Length == 0),
                Blobs = blobs.Count,
                FromRetail = entries.Count(x => x.RetailPath.Length > 0),
                Ranged = ranged,
                SteamRangeBytes = steamRange,
                GogRangeBytes = gogRange,
                SealedBytes = sealedBytes,
                PackedBytes = blobBytes,
                CopiedBytes = copied,
                LargestBlob = largest,
                ReleaseId = lk == null ? null : lk.Id,
                ManifestSha256 = PublicManifest.Sha256Of(manifest),
                GogManifestSha256 = gogManifest == null ? null : PublicManifest.Sha256Of(gogManifest),
            };
#if UCU_MAC
            string origins = string.Join(", ", Listed(rows).GroupBy(x => x.Origin).OrderBy(g => g.Key, StringComparer.Ordinal)
#else
            string origins = string.Join(", ", rows.GroupBy(x => x.Origin).OrderBy(g => g.Key, StringComparer.Ordinal)
#endif
                .Select(g => string.Format("{0} {1} ({2} exec)", g.Key, g.Count(), g.Count(x => x.Exec))));
            result.Summary = string.Format("files {0}; encrypted {1}, {2:F2} GiB in {3} blobs, {4:F2} GiB packed, largest blob {5:F2} GiB; copied from retail at install {6}, {7:F2} GiB; {8:F0} s",
                result.Files, result.Encrypted, sealedBytes / 1073741824.0, result.Blobs, blobBytes / 1073741824.0, largest / 1073741824.0,
                result.FromRetail, copied / 1073741824.0, clock.Elapsed.TotalSeconds)
                + string.Format("; ranged {0} files, {1:F2} GiB from Steam ranges, {2:F2} GiB from GOG ranges, residual {3:F2} GiB",
                    ranged, steamRange / 1073741824.0, gogRange / 1073741824.0, residualBytes / 1073741824.0)
                + (previous == null ? "" : string.Format("; reused from {0}: {1} files kept their scan, {2} of {3} blobs ({4:F2} GiB; {5} hard-linked, {6} copied), {7} sealed anew",
                    previous.Dir, reusedFiles, reusedBlobs, blobs.Count, reusedBytes / 1073741824.0, previous.Linked, previous.Copied, blobs.Count - reusedBlobs))
                + (lk == null ? "; no unlock server" : "; unlock server release " + lk.Id)
                + "; public manifest " + origins + (unity == null ? " (the player's Unity Editor wasn't found and no --unity was given: stock Unity files count as built or data)" : " (Unity files from " + unity + ")")
                + ", SHA-256 " + result.ManifestSha256
                + (gogManifest == null ? "; Steam installs only" : "; GOG: " + gogSummary + ", " + PublicManifest.GogFileName + " SHA-256 " + result.GogManifestSha256);
            return result;
        }

        /// <summary>
        /// Reads the vault just written back as an installer does, for every accepted install and both stores, and compares each
        /// row, and the key its blob opens with, with what was packed. A difference fails the pack and takes the vault away, so
        /// a package some install can't read is never uploaded.
        /// </summary>
        static void CheckVault(string vault, byte[] key, List<byte[]> slots, Entry[] entries, VaultLock lk, byte[] share, IDictionary<string, List<Entry>> stores)
        {
            for (int i = 0; i < slots.Count; i++)
                foreach (string store in new[] { Gate.Steam, Gate.Gog })
                {
                    List<Entry> want = entries.ToList(), section, got;
                    if (stores != null && stores.TryGetValue(store, out section)) want = Gate.ApplyStore(want, section);
                    string problem;
                    try
                    {
                        byte[] k;
                        using (var f = new FileStream(vault, FileMode.Open, FileAccess.Read, FileShare.Read))
                            k = Gate.ReadVault(f, slots[i], l => lk != null && l.Id == lk.Id && l.Url == lk.Url ? share : new byte[32], store, out got);
                        problem = k == null ? "no slot opens" : !k.SequenceEqual(key) ? "another content key" : got.Count != want.Count ? got.Count + " rows, not " + want.Count : null;
                        for (int r = 0; problem == null && r < want.Count; r++)
                            if (!SameRow(key, want[r], got[r])) problem = "a different row for " + want[r].Path;
                    }
                    catch (Exception e) { problem = e.Message; }
                    if (problem == null) continue;
                    File.Delete(vault);
                    throw new InvalidDataException(string.Format("The package vault doesn't read back as it was packed (accepted install {0} of {1}, {2}): {3}. The package can't be used.", i + 1, slots.Count, store, problem));
                }
        }

        static bool SameRow(byte[] key, Entry want, Entry got)
        {
            if (want.Path != got.Path || want.Size != got.Size || want.Blob != got.Blob || want.BlobSha256 != got.BlobSha256 || want.RetailPath != got.RetailPath
                || want.Sha256 != got.Sha256 || (want.Pieces ?? "") != got.Pieces || want.BlobPlain != got.BlobPlain) return false;
            return want.Blob.Length == 0 || (want.BlobPlain != null && Gate.FileKey(key, want.BlobPlain).SequenceEqual(Gate.BlobKey(key, got)));
        }

        // GOG's Galaxy runtime, beside BattleTech.exe in a GOG install; the player's GOG mode loads it from beside its own exe.
        static readonly string[] GogNatives = { "Galaxy64.dll", "GalaxyCSharpGlue.dll" };

        /// <summary>
        /// A macOS package's GOG section: GOG's Galaxy.bundle is Intel-only and the Mac player loads no Galaxy (D-20261126-4 B), so
        /// no natives are added; and the installed app's seal (CodeResources) lists the player's own bytes, so a file whose GOG copy
        /// differs is always installed from the player's bytes, never GOG's (gog-accept.tsv doesn't apply).
        /// </summary>
        static bool MacGog
        {
#if UCU_MAC
            get { return Gate.Platform != null; }
#else
            get { return false; }
#endif
        }

        /// <summary>
        /// Retail files whose GOG bytes are shown equivalent to Steam's for the player (gog-accept.tsv, embedded at build):
        /// "path|Steam SHA-512|GOG SHA-512". SHA-512 because this list is public source (PublicManifest.cs says why not SHA-256).
        /// </summary>
        static HashSet<string> GogAccept()
        {
            var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (Stream r = typeof(Pack).Assembly.GetManifestResourceStream("gog-accept.tsv"))
            {
                if (r == null) throw new FileNotFoundException("This build has no gog-accept.tsv; build it with build.ps1.");
                using (var t = new StreamReader(r))
                    for (string l; (l = t.ReadLine()) != null;)
                    {
                        if (l.Length == 0 || l[0] == '#') continue;
                        string[] c = l.Split('\t');
                        s.Add(c[0] + "|" + c[1].ToLowerInvariant() + "|" + c[2].ToLowerInvariant());
                    }
            }
            return s;
        }

        /// <summary>
        /// The vault's GOG section and GOG's public manifest. A retail-sourced file whose GOG copy differs is copied from GOG
        /// when gog-accept.tsv lists both hashes, else installed from Steam's bytes, packed; the Galaxy natives are added.
        /// </summary>
        static List<Entry> GogSection(string player, string gog, byte[] key, Entry[] entries, ManifestRow[] rows,
            Func<string, string, List<RangePiece>, string, string> sealOnce, CancellationToken stop, ConcurrentDictionary<string, List<RangePiece>> gogRanged,
            PreviousPack previous, ConcurrentDictionary<string, string> gogReuse, out List<ManifestRow> gogRows, out string summary)
        {
            HashSet<string> accept = MacGog ? new HashSet<string>() : GogAccept();
            var section = new ConcurrentBag<Entry>();
            var newRows = new ConcurrentDictionary<string, ManifestRow>(StringComparer.OrdinalIgnoreCase);
            var byPath = entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
            long own = 0, packed = 0, packedBytes = 0, ranged = 0;
            Parallel.ForEach(Enumerable.Range(0, entries.Length).Where(i => entries[i].RetailPath.Length > 0),
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount / 2), CancellationToken = stop }, i =>
            {
                Entry e = entries[i];
                string g = Path.Combine(gog, e.RetailPath), sha = null, sha512 = null;
                bool exec;
                if (File.Exists(g)) PublicManifest.HashBoth(g, out sha, out sha512, out exec);
                if (sha == e.Sha256) return;
                ManifestRow r = rows[i];
                if (sha != null && accept.Contains(e.RetailPath + "|" + r.Sha512 + "|" + sha512))
                {
                    long size = new FileInfo(g).Length;
                    section.Add(new Entry { Path = e.Path, Size = size, Blob = "", BlobSha256 = "", RetailPath = e.RetailPath, Sha256 = sha });
                    newRows[e.Path] = new ManifestRow { Path = e.Path, Size = size, Sha512 = sha512, Origin = PublicManifest.Retail, Exec = r.Exec };
                    Interlocked.Increment(ref own);
                }
                else
                {
                    string blobSha = sealOnce(Path.Combine(player, e.Path), e.Sha256, null, previous == null ? null : previous.GogWhole(e.Path, e.Sha256));
                    section.Add(new Entry { Path = e.Path, Size = e.Size, Blob = Gate.BlobFor(key, e.Sha256), BlobPlain = e.Sha256, BlobSha256 = blobSha, RetailPath = "", Sha256 = e.Sha256 });
                    newRows[e.Path] = new ManifestRow { Path = e.Path, Size = e.Size, Sha512 = r.Sha512, Origin = r.Exec ? PublicManifest.Built : PublicManifest.Data, Exec = r.Exec };
                    Interlocked.Increment(ref packed);
                    Interlocked.Add(ref packedBytes, e.Size);
                }
            });
            // A ranged file rebuilt from GOG's own bytes where they differ from Steam's. Its public manifest row is
            // unchanged: the file it installs is the same bytes either way.
            Parallel.ForEach(gogRanged, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount / 2), CancellationToken = stop }, kv =>
            {
                Entry e = byPath[kv.Key];
                string text = Gate.FormatPieces(kv.Value);
                if (text == e.Pieces) return;
                string src = Path.Combine(player, kv.Key);
                string residualSha = RetailRanges.ResidualSha256(src, kv.Value);
                string reuse;
                gogReuse.TryGetValue(kv.Key, out reuse);
                string blobSha = sealOnce(src, residualSha, kv.Value, reuse);
                section.Add(new Entry { Path = e.Path, Size = e.Size, Blob = Gate.BlobFor(key, residualSha), BlobPlain = residualSha, BlobSha256 = blobSha, RetailPath = "", Sha256 = e.Sha256, Pieces = text });
                Interlocked.Increment(ref ranged);
            });
            var list = section.ToList();
            string[] natives = MacGog ? new string[0] : GogNatives;
            foreach (string n in natives)
            {
                string g = Path.Combine(gog, n), sha, sha512;
                bool exec;
                if (!File.Exists(g)) throw new FileNotFoundException("The GOG install has no " + n, g);
                PublicManifest.HashBoth(g, out sha, out sha512, out exec);
                long size = new FileInfo(g).Length;
                list.Add(new Entry { Path = n, Size = size, Blob = "", BlobSha256 = "", RetailPath = n, Sha256 = sha });
                newRows[n] = new ManifestRow { Path = n, Size = size, Sha512 = sha512, Origin = PublicManifest.Retail, Exec = exec };
            }
            gogRows = rows.Select(r => { ManifestRow x; return newRows.TryGetValue(r.Path, out x) ? x : r; })
                .Concat(natives.Where(n => !rows.Any(r => string.Equals(r.Path, n, StringComparison.OrdinalIgnoreCase))).Select(n => newRows[n])).ToList();
            summary = string.Format("{0} files copied from GOG's own differing copies, {1} ({2:F2} GiB) from Steam's bytes packed, {3} ranged files from GOG's own ranges, {4} Galaxy files",
                own, packed, packedBytes / 1073741824.0, ranged, natives.Length);
            return list.OrderBy(x => x.Path, StringComparer.Ordinal).ToList();
        }

        // Blob = IV, then AES-CBC of the deflated file. Key and IV come from the content key and the file's hash, so the
        // same file always gives the same bytes.
        static string Seal(string src, string dst, byte[] key, string plainSha)
        {
            using (var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
                return Seal(i, dst, key, plainSha);
        }

        /// <summary>Seals a stream: a whole file, or a ranged file's residual gaps.</summary>
        static string Seal(Stream src, string dst, byte[] key, string plainSha)
        {
            byte[] iv = Gate.BlobIv(key, plainSha);
            using (var hash = Gate.NewSha())
            using (var a = Gate.NewAes(Gate.FileKey(key, plainSha), iv))
            {
                using (var o = new FileStream(dst, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
                using (var hashed = new CryptoStream(o, hash, CryptoStreamMode.Write))
                {
                    hashed.Write(iv, 0, iv.Length);
                    using (var enc = new CryptoStream(hashed, a.CreateEncryptor(), CryptoStreamMode.Write))
                    using (var z = new DeflateStream(enc, CompressionLevel.Optimal))
                        src.CopyTo(z, 1 << 20);
                }
                return Gate.Hex(hash.Hash);
            }
        }
    }
}
