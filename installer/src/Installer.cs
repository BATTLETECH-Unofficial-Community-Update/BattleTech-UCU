// Install logic, with no user interface. The form and the command line both call this.
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ReleaseGate
{
    public enum InstallResult { Installed = 0, Failed = 1, NoRetail = 2, RetailRefused = 3, Damaged = 4, Cancelled = 5 }

    /// <summary>The outcome of checking a BATTLETECH folder against this package.</summary>
    public sealed class RetailCheck
    {
        public string Folder;       // the install root, after tidying what the user typed
        public string Store;        // Gate.Steam or Gate.Gog (Gate.StoreOf the folder)
        public byte[] Key;          // null when refused
        public List<Entry> Entries;
        public string Problem;      // what to show the user; null when accepted
        public bool CodeProblem;    // Problem came from unlocking a locked package: the access code may need changing
        public byte[] ManifestBytes;    // the public manifest (PublicManifest.cs); null for a package packed before it existed
        public Dictionary<string, ManifestRow> Manifest;
        public string ManifestSha256 { get { return ManifestBytes == null ? null : PublicManifest.Sha256Of(ManifestBytes); } }
        public long Need { get { return Entries == null ? 0 : Entries.Sum(e => e.Size); } }
    }

    public sealed class InstallProgress
    {
        public long Done;
        public long Total;
    }

    public static class Installer
    {
        public static string PackageFolder { get { return AppDomain.CurrentDomain.BaseDirectory; } }

        // Package files come loose from the installer's folder (itch app, extracted zip), or straight out of zips: data
        // parts beside the installer (GitHub), or the zip itch.io serves for a website download, read in place so the
        // player never extracts it.
        static Dictionary<string, PartBlob> _parts;
        static readonly object PartsLock = new object();
        static string _chosenZip;

        static Dictionary<string, PartBlob> Parts
        {
            get { lock (PartsLock) return _parts ?? (_parts = Volumes.Index(CandidateZips())); }
        }

        /// <summary>A downloaded zip the player chose; its package files are read in place.</summary>
        public static void UseZip(string zip)
        {
            lock (PartsLock) { _chosenZip = zip; _parts = null; }
        }

        static IEnumerable<string> CandidateZips()
        {
            var l = new List<string>();
            try { l.AddRange(Directory.EnumerateFiles(PackageFolder, "*.zip")); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            string z = ZipRunFrom();
            if (z != null) l.Add(z);
            if (_chosenZip != null) l.Add(_chosenZip);
            return l;
        }

        /// <summary>
        /// Double-clicking the installer inside a zip in Explorer runs a copy extracted to %TEMP%\Temp<n>_<zip name>\.
        /// The zip's name comes from that folder; look for it where browsers save downloads.
        /// </summary>
        public static string ZipRunFrom()
        {
            string name = ZipNameRunFrom();
            if (name == null) return null;
            foreach (string dir in DownloadFolders())
            {
                try
                {
                    string p = Path.Combine(dir, name);
                    if (File.Exists(p)) return p;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        /// <summary>The zip's file name when Explorer ran this installer out of a zip, else null.</summary>
        public static string ZipNameRunFrom()
        {
            var m = System.Text.RegularExpressions.Regex.Match(PackageFolder, @"\\Temp\d+_([^\\]+\.zip)\\", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        public static IEnumerable<string> DownloadFolders()
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string shell = null;
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"))
                    shell = k == null ? null : k.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") as string;
            }
            catch (Exception e) when (e is System.Security.SecurityException || e is UnauthorizedAccessException || e is IOException) { }
            var l = new List<string>();
            if (!string.IsNullOrEmpty(shell)) l.Add(Environment.ExpandEnvironmentVariables(shell));
            l.Add(Path.Combine(profile, "Downloads"));
            l.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            l.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            return l.Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.OrdinalIgnoreCase);
        }

        // A release online (Remote.cs). Its vault replaces any local one; blobs still come from local package files when
        // they have them (a blob's name fixes its content), and the rest are downloaded into the destination first.
        static RemotePackage _remote;
        static string _downloads;

        /// <summary>Blobs downloaded for an install wait here, inside the destination, and are removed once it succeeds.</summary>
        public const string DownloadName = ".btu6-download";

        public static RemotePackage Remote { get { return _remote; } }

        public static void UseRemote(RemotePackage remote) { _remote = remote; }

        /// <summary>True when package.vault is beside the installer, in a zip it can read, or online.</summary>
        public static bool HasPackage
        {
            get { return _remote != null || File.Exists(Path.Combine(PackageFolder, Gate.VaultName)) || Parts.ContainsKey(Gate.VaultName); }
        }

        static Stream OpenVault()
        {
            if (_remote != null) return new MemoryStream(_remote.Vault, false);
            string loose = Path.Combine(PackageFolder, Gate.VaultName);
            if (File.Exists(loose)) return new FileStream(loose, FileMode.Open, FileAccess.Read, FileShare.Read);
            PartBlob p;
            if (Parts.TryGetValue(Gate.VaultName, out p)) return Volumes.OpenBlob(p);
            return null;
        }

        /// <summary>The package's public manifest, from the same place as its vault; null when it has none.</summary>
        static byte[] ReadManifest(string store)
        {
            string name = PublicManifest.FileNameFor(store);
            if (_remote != null) return store == Gate.Gog ? _remote.GogManifest : _remote.Manifest;
            string loose = Path.Combine(PackageFolder, name);
            if (File.Exists(loose)) return File.ReadAllBytes(loose);
            PartBlob p;
            if (!Parts.TryGetValue(name, out p)) return null;
            using (Stream s = Volumes.OpenBlob(p))
            using (var m = new MemoryStream())
            {
                s.CopyTo(m);
                return m.ToArray();
            }
        }

        /// <summary>The package's lock, or null for a package without one or none found.</summary>
        public static VaultLock PackageLock
        {
            get
            {
                try
                {
                    using (Stream v = OpenVault()) return v == null ? null : Gate.PeekLock(v);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidDataException) { return null; }
            }
        }

        static bool HasLocalBlob(string blob)
        {
            return File.Exists(Path.Combine(PackageFolder, blob)) || Parts.ContainsKey(blob);
        }

        static bool HasBlob(string blob)
        {
            return HasLocalBlob(blob) || (_remote != null && _remote.Blobs.ContainsKey(blob));
        }

        static Stream OpenBlob(string blob)
        {
            string loose = Path.Combine(PackageFolder, blob);
            if (File.Exists(loose)) return new FileStream(loose, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            PartBlob p;
            if (Parts.TryGetValue(blob, out p)) return Volumes.OpenBlob(p);
            string fetched = _downloads == null ? null : Path.Combine(_downloads, blob);
            if (fetched != null && File.Exists(fetched)) return new FileStream(fetched, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            throw new FileNotFoundException("Package file missing", blob);
        }

        /// <summary>Accepts the install folder, its BattleTech_Data folder, or the game's exe.</summary>
        public static string TidyRetail(string typed)
        {
            string p = (typed ?? "").Trim().Trim('"').TrimEnd('\\', '/');
            if (p.Length == 0) return p;
            if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) p = Path.GetDirectoryName(p) ?? p;
            if (string.Equals(Path.GetFileName(p), "BattleTech_Data", StringComparison.OrdinalIgnoreCase)) p = Path.GetDirectoryName(p) ?? p;
#if UCU_MAC
            if (string.Equals(Path.GetFileName(p), "BattleTech.app", StringComparison.OrdinalIgnoreCase)) p = Path.GetDirectoryName(p) ?? p;
#endif
            return p;
        }

        public static RetailCheck Check(string typed)
        {
            var c = new RetailCheck { Folder = TidyRetail(typed) };
            if (c.Folder.Length == 0) { c.Problem = "Choose your BATTLETECH folder."; return c; }
            try
            {
                if (!Directory.Exists(c.Folder)) { c.Problem = "That folder doesn't exist."; return c; }
#if UCU_MAC
                // Before anything is read or asked: a Windows install is not what this tool installs from.
                if (Layout.Of(c.Folder) != Layout.Mac)
                {
                    c.Problem = "That is not a macOS BATTLETECH install: it has no BattleTech.app. Choose the folder that holds BattleTech.app (in Steam: right-click BATTLETECH, Manage, Browse local files).";
                    return c;
                }
#endif
                byte[] fingerprint;
                try { fingerprint = Gate.Fingerprint(c.Folder); }
                catch (FileNotFoundException e)
                {
                    c.Problem = "That isn't a complete BATTLETECH install: " + e.FileName + " is missing.";
                    return c;
                }
                if (_remote != null && _remote.NeedsNewerInstaller)
                {
                    c.Problem = "Release " + _remote.Version + " needs a newer installer.";
                    return c;
                }
                List<Entry> entries;
                using (Stream vault = OpenVault())
                {
#if UCU_MAC
                    if (vault == null) { c.Problem = "The game package wasn't found. Put package-mac.vault and its data folder beside ucu, or use --remote."; return c; }
#else
                    if (vault == null) { c.Problem = "The game package wasn't found. Keep the downloaded zip, or package.vault, in the same folder as this installer."; return c; }
#endif
                    Unlock.Retail = c.Folder;
                    c.Store = Gate.StoreOf(c.Folder);
                    try { c.Key = Gate.ReadVault(vault, fingerprint, Unlock.Get, c.Store, out entries); }
                    catch (UnlockException e)
                    {
                        c.Problem = e.Message;
                        c.CodeProblem = true;
                        return c;
                    }
#if UCU_MAC
                    catch (PlatformException e)
                    {
                        c.Problem = e.Message + " This tool installs the macOS release only.";
                        return c;
                    }
#endif
                    // A vault in a format newer than this installer, or a damaged one. Online, the first is handed on instead.
                    catch (InvalidDataException)
                    {
#if UCU_MAC
                        c.Problem = "This package can't be read. It may need a newer version of this tool, or be damaged: download the tool or the package again.";
#else
                        c.Problem = "This package can't be read. It may need a newer installer: run the BattleTechUCUSetup.exe that came with it, or download the package again.";
#endif
                        return c;
                    }
                }
                c.Entries = entries;
#if UCU_MAC
                // A package is data from a server and from whoever sealed it: no entry may name a place outside the install folder.
                if (c.Key != null && entries.Any(e => UnsafePath(e.Path)))
                {
                    c.Problem = "This package lists a file outside its own folder. Nothing was installed. Download the package again.";
                    return c;
                }
#endif
                if (c.Key == null)
                    c.Problem = c.Store == Gate.Gog
                        ? "This package doesn't accept this GOG install of BATTLETECH. It needs version 1.9.1, unmodified: verify or repair the game in GOG Galaxy, or reinstall it, and try again."
                        : "This package needs an unmodified Steam install of BATTLETECH. Verify the game's files in Steam and try again.";
                else
                {
                    int missing = entries.Where(e => e.Blob.Length > 0).Select(e => e.Blob).Distinct(StringComparer.OrdinalIgnoreCase).Count(b => !HasBlob(b));
                    if (missing > 0 && _remote != null)
                        c.Problem = string.Format("Release {0} is missing {1} package file(s).", _remote.Version, missing);
                    else if (missing > 0)
#if UCU_MAC
                        c.Problem = string.Format("{0} package file(s) are missing. Keep every file of the package beside ucu, or use --remote.", missing);
#else
                        c.Problem = string.Format("{0} package file(s) are missing. Keep every downloaded file (the zip, or each data part) in the same folder as this installer.", missing);
#endif
                    else
                        CheckManifest(c);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
            {
                c.Problem = "That folder can't be read: " + e.Message;
            }
            return c;
        }

        // A package with a public manifest must list exactly the files the manifest lists, at the same sizes; each file's
        // SHA-512 is then checked as it is written. A package from before manifests installs as before, with no manifest.
        static void CheckManifest(RetailCheck c)
        {
            byte[] bytes = ReadManifest(c.Store);
#if UCU_MAC
            if (bytes == null) { c.Problem = "This package has no public manifest (" + PublicManifest.FileName + "). Download the package again."; return; }
#else
            if (bytes == null) return;
#endif
            try
            {
                c.Manifest = PublicManifest.Read(bytes);
            }
            catch (InvalidDataException e)
            {
                c.Problem = "This package's public manifest (" + PublicManifest.FileName + ") can't be read: " + e.Message + " Download the package again.";
                return;
            }
            string diff = PublicManifest.Mismatch(c.Entries, c.Manifest);
            if (diff != null)
            {
                c.Problem = "This package doesn't match its public manifest: " + diff + ". Nothing was installed. Download the package again.";
                c.Manifest = null;
                return;
            }
            c.ManifestBytes = bytes;
        }

        /// <summary>
        /// Checks every file the install folder's public manifest lists: present, same size, same SHA-512. Lists what differs
        /// in message. Files the manifest doesn't list (saves, logs, settings, mods) are not checked. The differing files are
        /// marked stale in the install record, so the next run rewrites them: one damaged with its size and write time kept
        /// looks current to the record.
        /// </summary>
        public static InstallResult Verify(string dest, InstallProgress progress, CancellationToken stop, out string message)
        {
            string path = Path.Combine(dest, PublicManifest.FileName);
            if (!File.Exists(path))
            {
#if UCU_MAC
                message = "This folder has no " + PublicManifest.FileName + ": it holds no install made by this tool. Run ucu install first.";
#else
                message = "This folder has no " + PublicManifest.FileName + ". Install or update with this installer first.";
#endif
                return InstallResult.Failed;
            }
            byte[] bytes = File.ReadAllBytes(path);
            Dictionary<string, ManifestRow> rows;
            try { rows = PublicManifest.Read(bytes); }
            catch (InvalidDataException e)
            {
                message = PublicManifest.FileName + " can't be read: " + e.Message;
                return InstallResult.Failed;
            }
            progress.Total = rows.Values.Sum(r => r.Size);
            var bad = new ConcurrentBag<string>();
            try
            {
                Parallel.ForEach(rows.Values.OrderByDescending(r => r.Size), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = stop }, r =>
                {
                    string f = Path.Combine(dest, r.Path);
                    if (!File.Exists(f)) bad.Add("missing " + r.Path);
                    else if (new FileInfo(f).Length != r.Size) bad.Add("size " + r.Path);
                    else if (PublicManifest.Sha512File(f) != r.Sha512) bad.Add("hash " + r.Path);
#if UCU_MAC
                    else if (r.Exec && !MacPlatform.IsExecutable(f) && MachOFile(f)) bad.Add("mode " + r.Path);   // a lost execute bit: the app wouldn't start
#endif
                    Interlocked.Add(ref progress.Done, r.Size);
                });
            }
            catch (OperationCanceledException)
            {
                message = "Check stopped.";
                return InstallResult.Cancelled;
            }
#if UCU_MAC
            CheckSeal(dest, bad);
#endif
            string sum = "Manifest SHA-256 " + PublicManifest.Sha256Of(bytes) + ".";
            if (bad.Count > 0)
            {
                var l = bad.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
#if UCU_MAC
                string next = "Run ucu repair to repair them.";
#else
                string next = "Run the installer again to repair them.";
#endif
                try
                {
                    Dictionary<string, string[]> record = ReadRecord(dest);
                    foreach (string b in l) MarkStale(record, b.Substring(b.IndexOf(' ') + 1));
                    if (record.Count > 0) WriteRecord(dest, record);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    next = "They couldn't be marked for repair (" + e.Message + "), so a repair may skip them.";
                }
                message = string.Format("{0} of {1} files differ from the public manifest: {2}{3}. {4} {5}",
                    l.Count, rows.Count, string.Join(", ", l.Take(5)), l.Count > 5 ? ", ..." : "", next, sum);
                return InstallResult.Damaged;
            }
#if UCU_MAC
            sum = "The signature list (CodeResources) matches what was installed. " + sum;
#endif
            message = string.Format("All {0} files match the public manifest. {1}", rows.Count, sum);
            return InstallResult.Installed;
        }

        /// <summary>Kept free beyond what an install needs, for .part files of files being replaced and the record.</summary>
        const long Headroom = 1L << 30;

        /// <summary>
        /// Returns what is wrong with this destination, or null when it can be used. space is the extra disk space the
        /// install needs there (PlanInstall), so a folder holding part of the game asks only for the rest.
        /// </summary>
        public static string CheckDest(string dest, RetailCheck c, out long space)
        {
            space = 0;
            if (string.IsNullOrWhiteSpace(dest)) return "Choose a folder to install to.";
            string full, root;
            try
            {
#if UCU_MAC
                // The real path, links followed, so a link into the BATTLETECH folder is the folder it points to.
                full = MacPlatform.RealPath(Path.GetFullPath(dest.Trim().Trim('"'))).TrimEnd('/') + "/";
#else
                full = Path.GetFullPath(dest.Trim().Trim('"')).TrimEnd('\\') + "\\";
#endif
                root = Path.GetPathRoot(full);
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException)
            {
                return "That isn't a valid folder.";
            }
            if (!Directory.Exists(root)) return "Drive " + root + " isn't available.";
#if UCU_MAC
            string r = MacPlatform.RealPath(Path.GetFullPath(c.Folder)).TrimEnd('/') + "/";
#else
            string r = Path.GetFullPath(c.Folder).TrimEnd('\\') + "\\";
#endif
            if (full.StartsWith(r, StringComparison.OrdinalIgnoreCase))
                return "Install somewhere outside your BATTLETECH folder. The original game stays as it is.";
#if !UCU_MAC
            // The same line on the real paths (links, junctions, 8.3 names, case). Only the comparisons use them: the drive, the plan
            // and the free space stay on the typed path, so a mapped drive or a subst letter is judged as it always was.
            string real = RealPath(dest).TrimEnd('\\') + "\\";
            if (real.StartsWith(RealPath(c.Folder).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                return "Install somewhere outside your BATTLETECH folder. The original game stays as it is.";
#endif
#if UCU_MAC
            string refusal = DestRefusal(dest, c.Entries);
            if (refusal != null) return refusal;
            string pkg = MacPlatform.RealPath(Path.GetFullPath(PackageFolder)).TrimEnd('/') + "/";
#else
            string foreign = ForeignGame(dest);
            if (foreign != null) return foreign;
            string pkg = Path.GetFullPath(PackageFolder).TrimEnd('\\') + "\\";
#endif
            // Only a folder that holds the package is protected. The launcher copy sits in the install it updates, and an
            // installer may sit at a drive root or above the install: neither holds package.vault (issue 27).
            bool holdsPackage = File.Exists(Path.Combine(PackageFolder, Gate.VaultName)) || Parts.ContainsKey(Gate.VaultName);
            if (full.StartsWith(pkg, StringComparison.OrdinalIgnoreCase) && holdsPackage)
                return "Install somewhere outside this package folder.";
#if !UCU_MAC
            if (holdsPackage && real.StartsWith(RealPath(PackageFolder).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                return "Install somewhere outside this package folder.";
#endif
            try { space = PlanInstall(c, full).Space; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return "That folder can't be read: " + e.Message;
            }
#if UCU_MAC
            long free = MacPlatform.FreeSpace(full);   // the volume the folder is on, not "/"
#else
            long free = new DriveInfo(root).AvailableFreeSpace;
#endif
            if (free < space + Headroom)
                return string.Format("{0:F1} GiB {1}needed on {2}, {3:F1} GiB free.", (space + Headroom) / 1073741824.0,
                    space < FreshSpace(c, full) ? "more " : "", root, free / 1073741824.0);
            return null;
        }

#if !UCU_MAC
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle h, System.Text.StringBuilder path, uint size, uint flags);

        /// <summary>
        /// The folder's real path: as much of it as exists with links, junctions, 8.3 names and letter case resolved by Windows
        /// (the rest, not yet created, as typed). Two spellings of one folder give the same string, apart from the case of a
        /// part that doesn't exist yet, so compare with OrdinalIgnoreCase.
        /// </summary>
        public static string RealPath(string path)
        {
            string full = Path.GetFullPath(path.Trim().Trim('"'));
            string rest = "", cur = full.Length > 3 ? full.TrimEnd('\\') : full;
            while (cur != null && !Directory.Exists(cur))
            {
                rest = "\\" + Path.GetFileName(cur) + rest;
                cur = Path.GetDirectoryName(cur);
            }
            if (cur == null) return full;
            // FILE_FLAG_BACKUP_SEMANTICS (0x02000000) lets CreateFile open a folder; no access is asked, only its name.
            using (var h = CreateFileW(cur, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
            {
                if (h.IsInvalid) return full;
                var sb = new System.Text.StringBuilder(1024);
                uint n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
                if (n == 0 || n > sb.Capacity) return full;
                string real = sb.ToString();
                if (real.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) real = @"\\" + real.Substring(8);
                else if (real.StartsWith(@"\\?\", StringComparison.Ordinal)) real = real.Substring(4);
                return real.TrimEnd('\\') + (rest.Length == 0 && real.Length <= 3 ? "\\" : rest);
            }
        }

        /// <summary>
        /// A folder holding a BATTLETECH game: its data folder, or for the destination itself its exe too (an exe with no data
        /// folder is no game to write over, so a loose BattleTech.exe in a folder above the destination doesn't count).
        /// </summary>
        static bool HoldsGame(string folder, bool exeCounts)
        {
            return Directory.Exists(Path.Combine(folder, "BattleTech_Data")) || (exeCounts && File.Exists(Path.Combine(folder, "BattleTech.exe")));
        }

        /// <summary>
        /// A UCU player's exe, told from retail's by its bytes: the Win32 version resource of BattleTech.exe is the Unity version that
        /// built it, 6000.x for every UCU player and 2018.4 for BATTLETECH, Steam and GOG alike. A GOG install also carries the base
        /// game's goggame-&lt;id&gt;.info, which a UCU install never has (only the DLC markers are copied). An exe with no readable
        /// version, or no exe at all, is not told apart from retail.
        /// </summary>
        static bool IsUcuPlayer(string folder)
        {
            string exe = Path.Combine(folder, "BattleTech.exe");
            try
            {
                return File.Exists(exe) && !File.Exists(Path.Combine(folder, "goggame-" + Gate.GogId + ".info"))
                    && System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).FileMajorPart >= 6000;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return false; }
        }

        /// <summary>Our record (a finished install), our unfinished marker (written before an install's first file), or a UCU player's exe.</summary>
        static bool IsOurInstall(string folder)
        {
            return File.Exists(Path.Combine(folder, RecordName)) || File.Exists(Path.Combine(folder, PendingName)) || IsUcuPlayer(folder);
        }

        /// <summary>
        /// A BATTLETECH game that this installer didn't install, or a copy of one: it holds a game, and has neither our install record
        /// (a finished install), nor our unfinished marker (written before an install's first file), nor a UCU player's exe (a
        /// restored backup, a lost or damaged record). Anything else is theirs, a doubtful folder included.
        /// </summary>
        static bool IsForeignGame(string folder, bool exeCounts)
        {
            return HoldsGame(folder, exeCounts) && !IsOurInstall(folder);
        }
#endif

        /// <summary>
        /// The refusal for a destination that is, or lies inside, a BATTLETECH game this installer didn't install (another Steam or
        /// GOG copy, a backup, a second library), or null. Judged on the real path, so a link, a junction, an 8.3 name, another case
        /// or a trailing slash doesn't hide it. A destination that is ours (our record, our unfinished marker, or a UCU player's exe)
        /// is never refused because of what lies around it. A parent that merely holds a game isn't refused either: nothing of the
        /// game's is written. Reads only; the command line asks it before anything is read online or any server is asked, and Install
        /// asks it again before its first write. The Mac tool has its own line (DestRefusal).
        /// </summary>
        public static string ForeignGame(string dest)
        {
#if UCU_MAC
            return null;
#else
            if (string.IsNullOrWhiteSpace(dest)) return null;
            string hit = null, how = null;
            try
            {
                string real = RealPath(dest);
                string start = real.Length > 3 ? real.TrimEnd('\\') : real;
                if (IsOurInstall(start)) return null;   // ours: updated in place, writes only inside itself, whatever lies around it
                for (string p = start; p != null && hit == null; p = Path.GetDirectoryName(p))
                {
                    bool self = string.Equals(p, start, StringComparison.OrdinalIgnoreCase);
                    if (IsForeignGame(p, self)) { hit = p; how = self ? "is" : "is inside"; }
                }
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException)
            {
                return "That isn't a valid folder.";
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return "That folder can't be read: " + e.Message;
            }
            if (hit == null) return null;
            return string.Format("That folder {0} a BATTLETECH game that this installer didn't install: {1}. Installing there would write over it, so nothing was written. " +
                "Choose another folder for UCU, such as a new BATTLETECH-UCU beside it. If {1} is really a UCU install that lost its files, move it away first; don't delete it.", how, hit);
#endif
        }

        /// <summary>Creates the folder and proves it can be written to, so a protected location fails before any copying.</summary>
        public static string ProbeWrite(string dest)
        {
            try
            {
                Directory.CreateDirectory(dest);
                string p = Path.Combine(dest, ".write-test");
                File.WriteAllText(p, "");
                File.Delete(p);
                return null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
#if UCU_MAC
                return "Can't write to that folder. Choose a folder you own. If macOS asked for permission to use the folder, allow it in System Settings > Privacy & Security.";
#else
                return "Can't write to that folder. Choose another one, or run this installer as administrator.";
#endif
            }
        }

        // Lists what this installer put in the folder: path, size, SHA-256, last write time. A later run skips files that are
        // unchanged and removes files the new version no longer has.
        public const string RecordName = ".btu6-install.tsv";

        // In the folder from an install's first write until it succeeds. While it is there the folder may hold files of two
        // versions: the last run failed, was cancelled or was killed.
        public const string PendingName = ".btu6-updating";

        /// <summary>The last install or update of this folder didn't finish.</summary>
        public static bool Unfinished(string dest)
        {
            try { return !string.IsNullOrEmpty(dest) && File.Exists(Path.Combine(dest, PendingName)); }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is IOException) { return false; }
        }

#if UCU_MAC
        /// <summary>This folder has an install record: an install of it finished, or started and stopped.</summary>
        public static bool HasRecord(string dest)
        {
            return File.Exists(Path.Combine(dest, RecordName));
        }

        /// <summary>
        /// The refusal for a destination in the BATTLETECH folder (real paths, links followed), or null. The command line asks it
        /// before anything is read online and before any server is asked; CheckDest holds the same line again.
        /// </summary>
        public static string InsideRetail(string dest, string retail)
        {
            string full = MacPlatform.RealPath(Path.GetFullPath(dest.Trim().Trim('"'))).TrimEnd('/') + "/";
            string r = MacPlatform.RealPath(Path.GetFullPath(TidyRetail(retail))).TrimEnd('/') + "/";
            return full.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? "Install somewhere outside your BATTLETECH folder. The original game stays as it is." : null;
        }

        /// <summary>An entry path that isn't plainly inside the install folder: empty, absolute, or with an empty, "." or ".." part.</summary>
        static bool UnsafePath(string p)
        {
            return string.IsNullOrEmpty(p) || Path.IsPathRooted(p) || p[0] == '\\' || p.Split('/', '\\').Any(s => s.Length == 0 || s == "." || s == "..");
        }

        /// <summary>A symbolic link (or, on Windows, a junction), whether or not its target exists. A link is never followed by an install.</summary>
        static bool IsLink(string p)
        {
            try
            {
                return new FileInfo(p).LinkTarget != null
                    || ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return false; }
        }

        /// <summary>
        /// Why this folder can't take an install, or null. The folder must not be a link and must not hold a BattleTech.app this tool
        /// didn't install (a Steam library, a copy or a backup of the game: whatever --retail says), and no entry of the package
        /// may lead through a link or out of the folder: a file written or deleted through a link lands outside it, in BATTLETECH
        /// among other places. Case-insensitive, as the Mac's volumes are. Nothing is written.
        /// </summary>
        public static string DestRefusal(string dest, IEnumerable<Entry> entries)
        {
            string full = Path.GetFullPath(dest.Trim().Trim('"')).TrimEnd('/');
            if (IsLink(full)) return "The install folder is a link. Choose a real folder.";
            if (Directory.Exists(Path.Combine(full, "BattleTech.app")) && !HasRecord(full) && !Unfinished(full))   // the unfinished marker is written before the first file: a killed first install is ours
                return "That folder already holds a BattleTech.app that this tool didn't install (BATTLETECH itself, or a copy). Choose an empty folder.";
            string real = MacPlatform.RealPath(full).TrimEnd('/') + "/";
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Entry e in entries)
            {
                if (UnsafePath(e.Path)) return "This package lists a file outside its own folder. Nothing was installed.";
                string cur = full, rel = "";
                foreach (string part in e.Path.Split('/'))
                {
                    cur = Path.Combine(cur, part);
                    rel += "/" + part;
                    if (!seen.Add(rel)) continue;
                    if (IsLink(cur)) return "Inside the install folder, " + rel.Substring(1) + " is a link. An install writes through links, so move it away first or choose another folder.";
                    if (Directory.Exists(cur) && !(MacPlatform.RealPath(cur).TrimEnd('/') + "/").StartsWith(real, StringComparison.OrdinalIgnoreCase))
                        return "Inside the install folder, " + rel.Substring(1) + " leads outside it. Choose another folder.";
                }
            }
            return null;
        }

        /// <summary>A folder on the way to this file (below dest) is a link.</summary>
        static bool ThroughLink(string dest, string path)
        {
            string cur = dest;
            string[] parts = path.Split('/', '\\');
            for (int i = 0; i < parts.Length - 1; i++)
                if (IsLink(cur = Path.Combine(cur, parts[i]))) return true;
            return false;
        }

        /// <summary>
        /// A bundle's _CodeSignature/CodeResources is packed but not in the public manifest (it lists every file's hash), so verify checks
        /// it against the install record, which holds what the install wrote: present, same size, same SHA-256, not marked for repair.
        /// The app's own must be there, and so must every other one the record lists.
        /// </summary>
        static void CheckSeal(string dest, ConcurrentBag<string> bad)
        {
            Dictionary<string, string[]> record = ReadRecord(dest);
            foreach (string rel in record.Keys.Where(PublicManifest.IsHidden).Concat(new[] { PublicManifest.MacHidden }).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string f = Path.Combine(dest, rel);
                string[] row;
                if (!File.Exists(f)) bad.Add("missing " + rel);
                else if (!record.TryGetValue(rel, out row) || row[1] == "-" || new FileInfo(f).Length.ToString() != row[0]
                         || !string.Equals(PublicManifest.Sha256Of(File.ReadAllBytes(f)), row[1], StringComparison.OrdinalIgnoreCase))
                    bad.Add("hash " + rel);
            }
        }

        /// <summary>A Mach-O file, by its first four bytes: the files whose execute bit matters.</summary>
        static bool MachOFile(string path)
        {
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var b = new byte[4];
                return PublicManifest.IsMachO(b, f.Read(b, 0, 4));
            }
        }
#endif

        static Dictionary<string, string[]> ReadRecord(string dest)
        {
            var r = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            string p = Path.Combine(dest, RecordName);
            if (!File.Exists(p)) return r;
            foreach (string line in File.ReadAllLines(p))
            {
                string[] c = line.Split('\t');
                if (c.Length == 4) r[c[0]] = new[] { c[1], c[2], c[3] };
            }
            return r;
        }

        static void WriteRecord(string dest, IDictionary<string, string[]> record)
        {
            string p = Path.Combine(dest, RecordName);
            File.WriteAllLines(p + ".part", record.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => x.Key + "\t" + string.Join("\t", x.Value)));
#if UCU_MAC
            File.Move(p + ".part", p, true);   // renamed over the old record: no moment without one, so a kill never leaves the folder disowned
#else
            if (File.Exists(p)) File.Delete(p);
            File.Move(p + ".part", p);
#endif
        }

        static string[] Stamp(string file, string sha)
        {
            var f = new FileInfo(file);
            return new[] { f.Length.ToString(), sha, f.LastWriteTimeUtc.Ticks.ToString() };
        }

        /// <summary>The next run rewrites the file. Its row stays, so a version without the file still deletes it.</summary>
        static void MarkStale(IDictionary<string, string[]> record, string path)
        {
            string[] had;
            if (record.TryGetValue(path, out had)) record[path] = new[] { had[0], "-", had[2] };
        }

        /// <summary>The file is there as this installer last wrote it, with the hash this entry wants.</summary>
        static bool IsCurrent(string dest, Dictionary<string, string[]> old, Entry e)
        {
            string target = Path.Combine(dest, e.Path);
            string[] had;
            return old.TryGetValue(e.Path, out had) && had[1] == e.Sha256 && File.Exists(target) && Stamp(target, e.Sha256).SequenceEqual(had);
        }

        // Retail files: on the same NTFS drive the install hard-links the large binary ones to the player's own retail files
        // instead of copying them, about 10.8 GiB of 34. A link is the same file under a second name: it takes no space,
        // survives retail being removed, and a delete-and-replace on either side only separates the two. A write in place
        // would reach the other side, so only an allow-list is linked: asset types nobody edits by hand (asset bundles, map
        // .bin data, video, sound banks, Unity asset files), each 1 MiB or more. Code, and text and data files a player
        // might edit (JSON, CSV and the like), are always copied (the maintainer, 2026-11-15: "Copy them"). The MDD database, which
        // the game rewrites in place, is outside the list.
        static readonly string[] LinkExtensions = { ".bin", ".bk2", ".bnk", ".assets", ".ress", ".resource" };
        const string AssetBundles = "BattleTech_Data/StreamingAssets/data/assetbundles/";
        const long LinkFloor = 1L << 20;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool GetVolumePathName(string fileName, System.Text.StringBuilder volumePath, int length);

        [StructLayout(LayoutKind.Sequential)]
        struct FileInfoByHandle
        {
            public uint Attributes;
            public long Created, Accessed, Written;
            public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out FileInfoByHandle info);

        static bool Linkable(Entry e)
        {
#if UCU_MAC
            return e.RetailPath.Length > 0;   // a clone shares no bytes that a write could reach, so every retail file may be one
#endif
            if (e.RetailPath.Length == 0 || e.Size < LinkFloor) return false;
            string p = e.Path.Replace('\\', '/');
            string ext = Path.GetExtension(p).ToLowerInvariant();
            if (ext.Length == 0) return p.StartsWith(AssetBundles, StringComparison.OrdinalIgnoreCase);
            return LinkExtensions.Contains(ext);
        }

        static string Volume(string path)
        {
            string p = Path.GetFullPath(path);
            while (!Directory.Exists(p) && !File.Exists(p))
            {
                string up = Path.GetDirectoryName(p);
                if (up == null || up == p) return null;
                p = up;
            }
            var sb = new System.Text.StringBuilder(1024);
            return GetVolumePathName(p, sb, sb.Capacity) ? sb.ToString().TrimEnd('\\').ToUpperInvariant() : null;
        }

        /// <summary>True when the install folder and the retail folder are on the same NTFS (or ReFS) volume.</summary>
        public static bool CanLink(string retail, string dest)
        {
#if UCU_MAC
            return MacPlatform.CanClone(retail, dest);
#endif
            try
            {
                string a = Volume(retail), b = Volume(dest);
                if (a == null || a != b) return false;
                string format = new DriveInfo(a + "\\").DriveFormat;
                return format == "NTFS" || format == "ReFS";
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
            {
                return false;
            }
        }

        /// <summary>Volume serial and file index; equal for two names of one file.</summary>
        static string FileId(string path)
        {
            try
            {
                using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    FileInfoByHandle i;
                    return GetFileInformationByHandle(f.SafeFileHandle, out i) ? i.VolumeSerial + ":" + i.IndexHigh + ":" + i.IndexLow : null;
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
        }

        static bool SameFile(string a, string b)
        {
            string x = FileId(a);
            return x != null && x == FileId(b);
        }

        static string Size(long bytes)
        {
            return bytes < (1L << 30) ? string.Format("{0:F1} MiB", bytes / 1048576.0) : string.Format("{0:F2} GiB", bytes / 1073741824.0);
        }

        /// <summary>Disk space a new install into dest takes: the whole game, less the files that become links.</summary>
        public static long FreshSpace(RetailCheck c, string dest)
        {
            return c.Need - (CanLink(c.Folder, dest) ? c.Entries.Where(Linkable).Sum(e => e.Size) : 0);
        }

        /// <summary>The sentence the form and the command line show about linking for this destination.</summary>
        public static string LinkNote(RetailCheck c, string dest)
        {
#if UCU_MAC
            long cloneBytes = c.Entries.Where(Linkable).Sum(e => e.Size);
            if (cloneBytes == 0) return "";
            return CanLink(c.Folder, dest)
                ? Size(cloneBytes) + " of BATTLETECH's own files will be clones of yours (APFS) and take no extra space."
                : "This folder isn't on the same APFS volume as BATTLETECH, so " + Size(cloneBytes) + " of BATTLETECH's own files will be copied. Install on the same volume to clone them instead.";
#endif
            long bytes = c.Entries.Where(Linkable).Sum(e => e.Size);
            if (bytes == 0) return "";
            string gib = Size(bytes);
            return CanLink(c.Folder, dest)
                ? gib + " of BATTLETECH's own files will be links to your BATTLETECH install and take no extra space."
                : "This folder isn't on the same NTFS drive as BATTLETECH, so " + gib + " of BATTLETECH's own files will be copied. " +
                  "Install on " + Path.GetPathRoot(Path.GetFullPath(c.Folder)) + " to link them instead.";
        }

        /// <summary>What an install into dest would write, and which blobs it would download first.</summary>
        public sealed class Plan
        {
            public int Files;           // files to write
            public long WriteBytes;
            public int Link;            // of Files, how many become hard links to retail
            public int Same;            // files already up to date
            public int Remove;          // files an earlier version installed that this one doesn't have
            public readonly List<string> Fetch = new List<string>();
            public long FetchBytes;     // bytes still to download (blobs a stopped run already saved don't count)
            public long Space;          // more disk space the install needs than dest already holds for it
        }

        /// <summary>
        /// A current file in the wrong form. One that should be a link but is a copy (an install made before linking) is
        /// replaced by a link, which gives its space back; a retail file that has gone away keeps the copy. One that is a link
        /// but should be a copy (linked by the first linking version, before the allow-list) is replaced by a copy, so an edit
        /// in UCU's folder never reaches retail. Only checked on retail's volume, where links can exist.
        /// </summary>
        static bool NeedsRelink(string retail, string dest, Entry e, bool sameVolume, bool linked)
        {
#if UCU_MAC
            return false;   // a clone is a file of its own: nothing to turn back into a link or a copy
#endif
            if (!sameVolume || e.RetailPath.Length == 0) return false;
            string src = Path.Combine(retail, e.RetailPath);
            if (!File.Exists(src)) return false;
            bool isLink = SameFile(Path.Combine(dest, e.Path), src);
            return linked ? !isLink : isLink;
        }

        static long Length(string path)
        {
            var f = new FileInfo(path);
            return f.Exists ? f.Length : 0;
        }

        /// <summary>
        /// What an install into dest still has to do. Space counts only what isn't there yet: files already current need
        /// nothing, a file being replaced (or a .part a stopped run left) gives back its own size, and blobs already in the
        /// download folder need no more room. Asking for the whole game again made a run stopped by a full disk unresumable
        /// (GitHub issue 1).
        /// </summary>
        public static Plan PlanInstall(RetailCheck c, string dest)
        {
            var plan = new Plan();
            Dictionary<string, string[]> old = ReadRecord(dest);
            var fetch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string downloads = Path.Combine(dest, DownloadName);
            bool link = CanLink(c.Folder, dest);
            foreach (Entry e in c.Entries)
            {
                bool linked = link && Linkable(e);
                if (IsCurrent(dest, old, e) && !NeedsRelink(c.Folder, dest, e, link, linked)) { plan.Same++; continue; }
                plan.Files++;
                plan.WriteBytes += e.Size;
                string target = Path.Combine(dest, e.Path);
                if (linked) { plan.Link++; continue; }
                plan.Space += Math.Max(0, e.Size - Length(target) - Length(target + ".part"));
                if (_remote == null || e.RetailPath.Length > 0 || HasLocalBlob(e.Blob) || !fetch.Add(e.Blob)) continue;
                plan.Fetch.Add(e.Blob);
                RemoteBlob b = _remote.Blobs[e.Blob];
                if (Length(Path.Combine(downloads, e.Blob)) == b.Size) continue;   // saved by a stopped run; Download skips it
                plan.FetchBytes += b.Length;
                plan.Space += b.Size;
            }
            var keep = new HashSet<string>(c.Entries.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
            plan.Remove = old.Keys.Count(p => !keep.Contains(p));
            return plan;
        }

        /// <summary>Downloads the blobs the plan names into dest's download folder; Install then reads them from there.</summary>
        public static InstallResult Fetch(Plan plan, string dest, InstallProgress progress, CancellationToken stop, out string message)
        {
            message = null;
            // Download counts blobs a stopped run already saved as done, so the total includes them.
            progress.Total = plan.Fetch.Sum(b => _remote.Blobs[b].Length);
            _downloads = Path.Combine(dest, DownloadName);
            if (plan.Fetch.Count == 0) return InstallResult.Installed;
#if UCU_MAC
            long free = MacPlatform.FreeSpace(dest);
#else
            long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dest))).AvailableFreeSpace;
#endif
            long need = plan.Space + Headroom;
            if (free < need)
            {
                message = string.Format("{0:F1} GiB more needed to download and install, {1:F1} GiB free.", need / 1073741824.0, free / 1073741824.0);
                return InstallResult.Failed;
            }
            try
            {
                global::ReleaseGate.Remote.Download(_remote, plan.Fetch, _downloads, progress, stop);
                return InstallResult.Installed;
            }
            catch (OperationCanceledException)
            {
                message = "Download stopped. Run this again to continue it.";
                return InstallResult.Cancelled;
            }
            catch (Exception e) when (e is IOException || e is System.Net.WebException || e is UnauthorizedAccessException)
            {
                message = "Download failed: " + e.Message + " Run this again to continue it.";
                return InstallResult.Failed;
            }
        }

        // Mod support (tools/mod-bootstrap/README.md): a release ships ModSupport\ inert, because Doorstop only reads files beside
        // the exe. Copying these three there turns it on. They aren't in the install record, so an update never reverts them.
        static readonly string[] ModLoaderFiles = { "winhttp.dll", "BattleTechPortBootstrap.dll", "doorstop_config.ini" };
        public const string ModsOffName = ".ucu-mods-off";

        /// <summary>The install's last choice: on unless the player turned it off. A new install starts on.</summary>
        public static bool ModsWanted(string dest)
        {
            try { return !File.Exists(Path.Combine(dest, ModsOffName)); }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is IOException) { return true; }
        }

        /// <summary>
        /// Turns mod support on or off after an install. On copies the loader beside the exe, replacing any other copy (BTA's
        /// installer leaves ModTek's Doorstop there, which crashes on D3D12). Off removes only files identical to ModSupport's,
        /// and remembers the choice. Null when the release has no ModSupport folder.
        /// </summary>
        public static string ApplyMods(string dest, bool on)
        {
            string src = Path.Combine(dest, "ModSupport");
            if (!ModLoaderFiles.All(f => File.Exists(Path.Combine(src, f)))) return null;
            string off = Path.Combine(dest, ModsOffName);
            try
            {
                if (on)
                {
                    foreach (string f in ModLoaderFiles) File.Copy(Path.Combine(src, f), Path.Combine(dest, f), true);
                    if (File.Exists(off)) File.Delete(off);
                    return "Mod support is on: ModTek mods in the Mods folder load when the game starts.";
                }
                foreach (string f in ModLoaderFiles)
                {
                    string target = Path.Combine(dest, f);
                    if (File.Exists(target) && File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(Path.Combine(src, f)))) File.Delete(target);
                }
                File.WriteAllText(off, "Mod support was turned off in the installer. Run the installer with it ticked to turn it on.\r\n");
                return "Mod support is off.";
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return "Mod support couldn't be changed: " + e.Message;
            }
        }

        public const string LauncherLogName = "ucu-launcher.log";

        /// <summary>
        /// Before a DirectX 12 start with mod support on: when the Doorstop beside the exe is an older one than UCU's (ModTek's
        /// own v4.3 crashes when Unity loads d3d12.dll; a ModTek or mod-pack installer run after UCU's last update leaves it),
        /// copies UCU's loader back, as ApplyMods does. A newer or unidentified copy is kept and named. Each case writes one
        /// line to ucu-launcher.log and returns it; null when the loader is UCU's own, absent, or mod support is off.
        /// </summary>
        public static string CheckD3D12Loader(string dest)
        {
            string src = Path.Combine(dest, "ModSupport");
            if (!ModsWanted(dest) || !ModLoaderFiles.All(f => File.Exists(Path.Combine(src, f)))) return null;
            string have = Path.Combine(dest, "winhttp.dll"), ours = Path.Combine(src, "winhttp.dll");
            string line;
            try
            {
                if (!File.Exists(have) || File.ReadAllBytes(have).SequenceEqual(File.ReadAllBytes(ours))) return null;
                Version v = DoorstopVersion(have), u = DoorstopVersion(ours);
                if (v != null && u != null && v < u)
                {
                    foreach (string f in ModLoaderFiles) File.Copy(Path.Combine(src, f), Path.Combine(dest, f), true);
                    line = "DirectX 12: replaced the mod loader beside the game (Doorstop v" + v + ", which crashes on DirectX 12) with UCU's v" + u + ".";
                }
                else
                    line = "DirectX 12: kept the mod loader beside the game (winhttp.dll " + (v == null ? "of unknown version" : "Doorstop v" + v)
                         + "), which isn't UCU's v" + u + ". If the game crashes at start, turn mod support off and on in the Mods tab, or pick DirectX 11.";
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                line = "DirectX 12: couldn't check the mod loader beside the game: " + e.Message;
            }
            try { File.AppendAllText(Path.Combine(dest, LauncherLogName), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + line + "\r\n"); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            return line;
        }

        /// <summary>A Doorstop winhttp.dll's file version (NeighTools); null for any other file.</summary>
        static Version DoorstopVersion(string path)
        {
            var i = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            Version v;
            return i.ProductName == "NeighTools" && Version.TryParse(i.FileVersion, out v) ? v : null;
        }

        /// <summary>Stops after the files in progress when cancelled. Finished files stay, and a later run skips them.</summary>
        public static InstallResult Install(RetailCheck c, string dest, InstallProgress progress, CancellationToken stop, out string message)
        {
            message = null;
            byte[] key = c.Key;
            string retail = c.Folder;
            progress.Total = c.Need;
            var bad = new List<string>();
#if UCU_MAC
            // Before the first write, whoever called: no link or foreign app in the way (CheckDest asks the same).
            string refusal = DestRefusal(dest, c.Entries);
            if (refusal != null) { message = refusal; return InstallResult.Failed; }
#else
            // Before the first write, whoever called: not over a BATTLETECH game this installer didn't install (CheckDest asks the same).
            string foreign = ForeignGame(dest);
            if (foreign != null) { message = foreign; return InstallResult.Failed; }
#endif
            Directory.CreateDirectory(dest);
#if UCU_MAC
            File.WriteAllText(Path.Combine(dest, PendingName), "An install or update of this folder started and hasn't finished. Run ucu install or ucu update again.\r\n");
#else
            File.WriteAllText(Path.Combine(dest, PendingName), "An install or update of this folder started and hasn't finished. Run the installer again.\r\n");
#endif
            Dictionary<string, string[]> old = ReadRecord(dest);
            var now = new ConcurrentDictionary<string, string[]>(old, StringComparer.OrdinalIgnoreCase);
            int same = 0, linkedFiles = 0;
            long linkedBytes = 0, copiedRetail = 0;
            bool link = CanLink(retail, dest);
#if UCU_MAC
            var flushed = System.Diagnostics.Stopwatch.StartNew();
            bool wrote = false;
#endif
            string text = null;
            InstallResult result;
            try
            {
                result = Run();
            }
            finally
            {
                WriteRecord(dest, now);
            }
            message = text;
            return result;

            InstallResult Run()
            {
                try
                {
                    // Largest first, so one big file doesn't run alone at the end.
                    Parallel.ForEach(c.Entries.OrderByDescending(e => e.Size), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = stop }, e =>
                    {
                        string target = Path.Combine(dest, e.Path);
                        bool linked = link && Linkable(e);
                        if (IsCurrent(dest, old, e) && !NeedsRelink(retail, dest, e, link, linked))
                        {
                            Interlocked.Add(ref progress.Done, e.Size);
                            Interlocked.Increment(ref same);
                            return;
                        }
                        MarkStale(now, e.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        string part = target + ".part";
                        bool ok;
                        // A .part a killed run left may be a hard link to retail: writing into it would truncate retail's
                        // file, so it is always deleted (which only removes that name) before anything is written.
                        if (File.Exists(part)) File.Delete(part);
                        try
                        {
                            string src = e.RetailPath.Length > 0 ? Path.Combine(retail, e.RetailPath) : null;
#if UCU_MAC
                            // CodeResources is packed but not in the public manifest: it is still checked by its blob and plaintext hashes.
                            ManifestRow listed = null;
                            string sha512 = c.Manifest == null || !c.Manifest.TryGetValue(e.Path, out listed) ? null : listed.Sha512;
#else
                            string sha512 = c.Manifest == null ? null : c.Manifest[e.Path].Sha512;
#endif
                            // A read-only retail file is copied: a link shares its attributes, and a later update couldn't delete it.
                            if (src != null && linked && File.Exists(src) && (File.GetAttributes(src) & FileAttributes.ReadOnly) == 0)
                            {
                                ok = Verify(src, e.Sha256, sha512, progress, stop);
                                if (ok)
                                {
#if UCU_MAC
                                    if (MacPlatform.TryClone(src, part))
#else
                                    if (CreateHardLink(part, src, IntPtr.Zero))
#endif
                                    {
                                        Interlocked.Increment(ref linkedFiles);
                                        Interlocked.Add(ref linkedBytes, e.Size);
                                    }
                                    else
                                    {
                                        Interlocked.Add(ref progress.Done, -e.Size);
                                        ok = Copy(src, part, e.Sha256, sha512, progress, stop);
                                        if (ok) Interlocked.Add(ref copiedRetail, e.Size);
                                    }
                                }
                            }
                            else if (src != null)
                            {
                                ok = File.Exists(src) && Copy(src, part, e.Sha256, sha512, progress, stop);
                                if (ok) Interlocked.Add(ref copiedRetail, e.Size);
                            }
                            else ok = Open(part, key, e, sha512, progress, stop, retail);
                        }
                        catch (OperationCanceledException)
                        {
                            File.Delete(part);
                            throw;
                        }
                        if (ok)
                        {
#if UCU_MAC
                            ManifestRow row = null;
                            MacPlatform.SetMode(part, c.Manifest != null && c.Manifest.TryGetValue(e.Path, out row) && row.Exec && MachOFile(part));
#endif
                            if (File.Exists(target)) File.Delete(target);
                            File.Move(part, target);
                            now[e.Path] = Stamp(target, e.Sha256);
#if UCU_MAC
                            // The record is written every second, not only at the end: a killed install leaves what it finished, so install resumes it.
                            lock (now)
                                if (!wrote || flushed.ElapsedMilliseconds >= 1000) { WriteRecord(dest, now); wrote = true; flushed.Restart(); }
#endif
                        }
                        else
                        {
                            File.Delete(part);
                            lock (bad) bad.Add(e.Path);
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    text = "Install cancelled. Run this again to finish it.";
                    return InstallResult.Cancelled;
                }
                catch (AggregateException a)
                {
                    if (a.Flatten().InnerExceptions.All(x => x is OperationCanceledException))
                    {
                        text = "Install cancelled. Run this again to finish it.";
                        return InstallResult.Cancelled;
                    }
                    text = "Install failed: " + a.Flatten().InnerExceptions[0].Message;
                    return InstallResult.Failed;
                }
                // The DLC markers come from the GOG install, not from the files checked above, so one failed file doesn't
                // leave the DLC unowned (GitHub issue 40).
                int markers = 0, markersRemoved = 0;
                if (c.Store == Gate.Gog)
                {
                    try { markers = SyncGogDlcMarkers(retail, dest, out markersRemoved); }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        text = "Install failed: your GOG DLC files (goggame-*.info) couldn't be copied: " + e.Message;
                        return InstallResult.Failed;
                    }
                }
                // Files an earlier version installed that this one doesn't have. They go even when a file failed its check: one
                // left beside its replacement can be picked up by the game (a bundle patch is found by its name's start).
                var keep = new HashSet<string>(c.Entries.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
#if UCU_MAC
                string root = Path.GetFullPath(dest).TrimEnd('/') + "/";
#else
                string root = Path.GetFullPath(dest).TrimEnd('\\') + "\\";
#endif
                int removed = 0;
                foreach (string path in old.Keys.Where(p => !keep.Contains(p)))
                {
                    string full = Path.GetFullPath(Path.Combine(dest, path));
                    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
#if UCU_MAC
                    if (ThroughLink(dest, path)) continue;   // a folder that became a link: the file in it isn't ours to delete
#endif
                    if (File.Exists(full)) { File.Delete(full); removed++; }
                    string[] gone;
                    now.TryRemove(path, out gone);
                }
                // A copy of the public manifest stays with the install, for Verify install and verify.ps1. An install from a
                // package without one removes an older copy, which would no longer describe the folder. After a failed check too,
                // so Verify install names the files that failed instead of comparing with the version before.
                string kept = Path.Combine(dest, PublicManifest.FileName);
                if (c.ManifestBytes != null) File.WriteAllBytes(kept, c.ManifestBytes);
                else if (File.Exists(kept)) File.Delete(kept);
                if (bad.Count > 0)
                {
                    text = string.Format("{0} file(s) failed their check, first {1}. {2} or download this package again.", bad.Count, bad[0],
                        c.Store == Gate.Gog ? "Verify or repair BATTLETECH in GOG Galaxy (or reinstall it from GOG)" : "Verify BATTLETECH in Steam");
                    if (old.Count > 0) text += " Until then this folder mixes two versions of UCU, so graphics or saves may go wrong.";
                    return InstallResult.Damaged;
                }
                string downloads = Path.Combine(dest, DownloadName);
                if (Directory.Exists(downloads))
                {
                    try { Directory.Delete(downloads, true); }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
                }
                File.Delete(Path.Combine(dest, PendingName));
                text = string.Format("Installed {0} files, {1:F2} GiB.", c.Entries.Count, c.Need / 1073741824.0);
                if (same > 0 || removed > 0)
                    text += string.Format(" {0} were already up to date; {1} old file(s) removed.", same, removed);
#if UCU_MAC
                if (linkedFiles > 0)
                    text += string.Format(" {0} files ({1}) are clones of your BATTLETECH files and take no extra space.", linkedFiles, Size(linkedBytes));
#else
                if (linkedFiles > 0)
                    text += string.Format(" {0} files ({1}) are links to your BATTLETECH files and take no extra space.", linkedFiles, Size(linkedBytes));
#endif
                if (c.Store == Gate.Gog)
                    text += string.Format(" DLC from your GOG install: {0}{1}.", markers == 0 ? "none" : markers.ToString(),
                        markersRemoved > 0 ? string.Format(" ({0} no longer in it removed)", markersRemoved) : "");
#if UCU_MAC
                if (copiedRetail > 0)
                    text += string.Format(" {0} of BATTLETECH's files were copied, not cloned.", Size(copiedRetail));
#else
                if (copiedRetail > 0)
                    text += link
                        ? string.Format(" {0} of BATTLETECH's files (code, text and data files, and small files) were copied, so editing them never changes BATTLETECH.", Size(copiedRetail))
                        : string.Format(" This folder isn't on the same NTFS drive as BATTLETECH, so {0} of BATTLETECH's files were copied.", Size(copiedRetail));
#endif
                if (c.ManifestBytes != null)
                    text += " Files written match the public manifest, SHA-256 " + c.ManifestSha256 + ".";
                return InstallResult.Installed;
            }
        }

        static readonly System.Text.RegularExpressions.Regex GogMarkerName =
            new System.Text.RegularExpressions.Regex(@"^goggame-(\d+)\.info$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>The DLC id when this file is a GOG DLC marker of BATTLETECH (goggame-&lt;id&gt;.info, rootGameId BATTLETECH's, not the base game's), else null.</summary>
        static string GogDlcId(string file)
        {
            var m = GogMarkerName.Match(Path.GetFileName(file));
            if (!m.Success || m.Groups[1].Value == Gate.GogId) return null;
            string text;
            try { text = File.ReadAllText(file); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
            var root = System.Text.RegularExpressions.Regex.Match(text, "\"rootGameId\"\\s*:\\s*\"(\\d+)\"");
            return root.Success && root.Groups[1].Value == Gate.GogId ? m.Groups[1].Value : null;
        }

        /// <summary>
        /// In GOG mode the player owns a DLC when Galaxy's IsDlcInstalled finds goggame-&lt;dlc id&gt;.info beside its exe; GOG's DLC
        /// "installs" are only these markers (gog-legs-1 G1: none, no DLC; G1b: copied, the DLC load). The user's own markers
        /// are copied from their GOG install on every install or repair, and a marker for a DLC no longer there is removed.
        /// The base game's goggame-1482783682.info is not copied, so Gate.StoreOf never takes the UCU folder for a GOG install.
        /// Returns how many markers the folder now has.
        /// </summary>
        public static int SyncGogDlcMarkers(string retail, string dest, out int removed)
        {
            removed = 0;
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string src in Directory.EnumerateFiles(retail, "goggame-*.info", SearchOption.TopDirectoryOnly))
            {
                if (GogDlcId(src) == null) continue;
                string name = Path.GetFileName(src), dst = Path.Combine(dest, name);
                have.Add(name);
                byte[] bytes = File.ReadAllBytes(src);
                if (File.Exists(dst) && File.ReadAllBytes(dst).SequenceEqual(bytes)) continue;
                File.WriteAllBytes(dst + ".part", bytes);
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(dst + ".part", dst);
            }
            foreach (string old in Directory.EnumerateFiles(dest, "goggame-*.info", SearchOption.TopDirectoryOnly).ToList())
                if (!have.Contains(Path.GetFileName(old)) && GogDlcId(old) != null)
                {
                    File.Delete(old);
                    removed++;
                }
            return have.Count;
        }

        static void Pump(Stream from, Stream to, InstallProgress progress, CancellationToken stop)
        {
            var buf = new byte[1 << 20];
            int n;
            while ((n = from.Read(buf, 0, buf.Length)) > 0)
            {
                stop.ThrowIfCancellationRequested();
                to.Write(buf, 0, n);
                Interlocked.Add(ref progress.Done, n);
            }
        }

        // The written file passes through SHA-512 (when the package has a public manifest) and then to disk.
        static Stream Hashed(Stream o, HashAlgorithm sha512)
        {
            return sha512 == null ? o : new CryptoStream(o, sha512, CryptoStreamMode.Write);
        }

        static bool Matches512(HashAlgorithm h, string want)
        {
            return h == null || Gate.Hex(h.Hash) == want;
        }

        static bool Copy(string src, string dst, string sha, string sha512, InstallProgress progress, CancellationToken stop)
        {
            using (var hash = Gate.NewSha())
            using (HashAlgorithm wide = sha512 == null ? null : new SHA512Cng())
            {
                using (var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
                using (var o = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                using (var w = Hashed(o, wide))
                using (var h = new CryptoStream(w, hash, CryptoStreamMode.Write))
                    Pump(i, h, progress, stop);
                return Gate.Hex(hash.Hash) == sha && Matches512(wide, sha512);
            }
        }

        /// <summary>Reads a retail file through both hashes without writing anything; the check before linking it.</summary>
        static bool Verify(string src, string sha, string sha512, InstallProgress progress, CancellationToken stop)
        {
            using (var hash = Gate.NewSha())
            using (HashAlgorithm wide = sha512 == null ? null : new SHA512Cng())
            {
                using (var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
                using (var w = Hashed(Stream.Null, wide))
                using (var h = new CryptoStream(w, hash, CryptoStreamMode.Write))
                    Pump(i, h, progress, stop);
                return Gate.Hex(hash.Hash) == sha && Matches512(wide, sha512);
            }
        }

        static bool Open(string dst, byte[] key, Entry e, string sha512, InstallProgress progress, CancellationToken stop, string retail)
        {
            using (var cipherHash = Gate.NewSha())
            using (var plainHash = Gate.NewSha())
            using (HashAlgorithm wide = sha512 == null ? null : new SHA512Cng())
            {
                try
                {
                    List<RangePiece> pieces = e.Pieces == null ? null : Gate.ParsePieces(e.Pieces);
                    using (var i = OpenBlob(e.Blob))
                    using (var hashed = new CryptoStream(i, cipherHash, CryptoStreamMode.Read))
                    {
                        var iv = new byte[16];
                        if (hashed.Read(iv, 0, 16) != 16) return false;
                        using (var a = Gate.NewAes(Gate.BlobKey(key, e), iv))
                        using (var dec = new CryptoStream(hashed, a.CreateDecryptor(), CryptoStreamMode.Read))
                        using (var z = new DeflateStream(dec, CompressionMode.Decompress))
                        using (var o = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                        using (var w = Hashed(o, wide))
                        using (var h = new CryptoStream(w, plainHash, CryptoStreamMode.Write))
                        {
                            if (pieces == null || pieces.Count == 0)
                                Pump(z, h, progress, stop);
                            else if (!Ranged(z, h, e, pieces, retail, progress, stop))
                                return false;
                            else
                                Drain(z, stop);
                            // Read the rest, so the blob's hash covers the whole file.
                            var rest = new byte[1 << 16];
                            while (dec.Read(rest, 0, rest.Length) > 0) { }
                        }
                    }
                }
                catch (CryptographicException) { return false; }
                catch (InvalidDataException) { return false; }
                catch (FileNotFoundException) { return false; }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
                return Gate.Hex(cipherHash.Hash) == e.BlobSha256 && Gate.Hex(plainHash.Hash) == e.Sha256 && Matches512(wide, sha512);
            }
        }

        /// <summary>Rebuilds a ranged file: residual bytes from the blob, retail ranges from the retail install.</summary>
        static bool Ranged(Stream residual, Stream output, Entry e, List<RangePiece> pieces, string retail, InstallProgress progress, CancellationToken stop)
        {
            var buf = new byte[1 << 20];
            foreach (RangePiece p in pieces)
            {
                if (p.FromRetail)
                {
                    string src = Path.Combine(retail, p.Path);
                    if (!File.Exists(src)) return false;
                    using (var r = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
                    {
                        if (r.Length < p.Offset + p.Length) return false;
                        r.Position = p.Offset;
                        if (!CopyExactly(r, output, p.Length, buf, progress, stop)) return false;
                    }
                }
                else if (!CopyExactly(residual, output, p.Length, buf, progress, stop)) return false;
            }
            return true;
        }

        static bool CopyExactly(Stream from, Stream to, long length, byte[] buf, InstallProgress progress, CancellationToken stop)
        {
            while (length > 0)
            {
                int n = from.Read(buf, 0, (int)Math.Min(buf.Length, length));
                if (n <= 0) return false;
                stop.ThrowIfCancellationRequested();
                to.Write(buf, 0, n);
                Interlocked.Add(ref progress.Done, n);
                length -= n;
            }
            return true;
        }

        /// <summary>Reads any decompressed bytes past the pieces (none for a package this packer wrote); the whole-file
        /// hash fails a manifest whose pieces don't add up.</summary>
        static void Drain(Stream z, CancellationToken stop)
        {
            var buf = new byte[1 << 16];
            while (z.Read(buf, 0, buf.Length) > 0) stop.ThrowIfCancellationRequested();
        }
    }
}
