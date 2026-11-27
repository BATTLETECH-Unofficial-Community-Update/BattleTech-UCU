// Shared by the packer and the installer. Compiled against .NET Framework 4.x, which every supported Windows has.
//
// The content key is kept by whoever packs releases and reused for every release. It is stored only wrapped under a key
// derived from a retail install, so the package alone decrypts nothing. Each file is compressed, then encrypted with a key
// and IV derived from the content key and the file's own hash, so an unchanged file gives the same blob in every release.
// The hashes sit in the sealed manifest (key separation, vault v4: docs/keyserver-design.md). Anyone who owns the game can
// derive the same key: this makes the game a real requirement, it does not stop a determined attacker, and it does not
// change who holds the rights to the content.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace ReleaseGate
{
    public sealed class Entry
    {
        public string Path;        // destination, relative to the install folder
        public long Size;          // plaintext size
        public string Blob;        // package file holding the ciphertext; empty when copied from retail
        public string BlobSha256;  // hex, of the whole blob
        public string RetailPath;  // source inside the retail install; empty when decrypted from the package
        public string Sha256;      // hex, of the plaintext
        public string Pieces;      // ranged entry: B:<len> and R:<offset>:<len>:<retail path> joined by |, else empty
        public string BlobPlain;   // hex SHA-256 of the bytes the blob holds (a ranged entry's residual, else the file), which keys it; null in a v2 or v3 vault
    }

    /// <summary>One piece of a ranged file: copy length bytes from the residual blob (Path empty) or from retail.</summary>
    public sealed class RangePiece
    {
        public long Offset;
        public long Length;
        public string Path;   // retail-relative source; null for a residual piece
        public bool FromRetail { get { return Path != null; } }
    }

    /// <summary>
    /// A locked vault's (v3, v4) lock: the release id and where to ask for its share (docs/keyserver-design.md). The share is
    /// HMAC-SHA256(master secret, "btucu-share|" + hex id); only the packer and the unlock server hold the master secret.
    /// </summary>
    public sealed class VaultLock
    {
        public byte[] ReleaseId;
        public string Url;
#if UCU_MAC
        public string Platform;    // null: a Windows package; Gate.MacPlatform: a macOS one
#endif
        public string Id { get { return Gate.Hex(ReleaseId); } }
    }

#if UCU_MAC
    /// <summary>A package of the other platform: refused before the retail install is looked at and before any server is asked.</summary>
    public sealed class PlatformException : Exception
    {
        public readonly string Package;   // the package's platform; null for a Windows package
        public PlatformException(string package, string reader)
            : base(package == null ? "This package is for Windows." : "This package is for macOS (" + package + ").") { Package = package; }
    }

#endif
    /// <summary>A locked vault couldn't be unlocked; Message is for the player.</summary>
    public sealed class UnlockException : Exception
    {
        public UnlockException(string message) : base(message) { }
    }

    public static class Gate
    {
        public const uint AppId = 637090;
#if UCU_MAC
        public static string VaultName = "package.vault";
        /// <summary>The platform string of a macOS Steam package (vault BTU6GAM4). A Windows package has none.</summary>
        public const string MacPlatform = "mac-steam";
        /// <summary>The platform whose packages this process reads and writes: null for Windows, MacPlatform for macOS.</summary>
        public static string Platform;
        /// <summary>Switches this process to a platform's names (vault, public manifest); the reader and the writer both follow it.</summary>
        public static void Use(string platform)
        {
            Platform = platform;
            VaultName = platform == null ? "package.vault" : "package-mac.vault";
            PublicManifest.FileName = platform == null ? "ucu-manifest.tsv" : "ucu-manifest-mac.tsv";
            PublicManifest.GogFileName = platform == null ? "ucu-manifest-gog.tsv" : "ucu-manifest-mac-gog.tsv";
        }

        /// <summary>A package of the other platform is refused here, before anything else is read and before any server is asked.</summary>
        static void CheckPlatform(string package)
        {
            if (package != Platform) throw new PlatformException(package, Platform);
        }

        /// <summary>What a retail slot's check text says: a macOS vault repeats its platform inside the sealed text, so the header can't be edited.</summary>
        static string RetailText(string platform)
        {
            return platform == null ? RetailOk : RetailOk + "|" + platform;
        }
#else
        public const string VaultName = "package.vault";
#endif

        /// <summary>GOG's product id for BATTLETECH; its install holds goggame-&lt;id&gt;.info and a registry key of that id.</summary>
        public const string GogId = "1482783682";
        public const string Steam = "steam", Gog = "gog";

        /// <summary>The store a retail install comes from: GOG when it carries GOG's game info file, otherwise Steam.</summary>
        public static string StoreOf(string retail)
        {
            return File.Exists(Path.Combine(GogInfoDir(retail), "goggame-" + GogId + ".info")) ? Gog : Steam;
        }

        /// <summary>
        /// Where an install keeps its goggame-*.info files: beside BattleTech.exe on Windows; on macOS, GOG's installer puts them in
        /// its wrapper folder's Contents/Resources, beside BattleTech.app and outside its seal (mac-gog-s1, bytes-read 2026-11-26).
        /// </summary>
        public static string GogInfoDir(string root)
        {
#if UCU_MAC
            if (Layout.Of(root) == Layout.Mac) return Path.Combine(root, "Contents", "Resources");
#endif
            return root;
        }

        /// <summary>
        /// True when the player's game code sets SteamAppId itself (transplant patch 101, 2026-11-12), so it needs no
        /// steam_appid.txt. Older players need the file, or they relaunch retail through Steam. Looks for the
        /// "SteamGameId" string literal (UTF-16) that the patch adds to Assembly-CSharp.dll.
        /// </summary>
        public static bool SetsAppIdInCode(string player)
        {
#if UCU_MAC
            string dll = Path.Combine(player, Layout.Of(player).Data + "Managed/Assembly-CSharp.dll");
#else
            string dll = Path.Combine(player, "BattleTech_Data", "Managed", "Assembly-CSharp.dll");
#endif
            if (!File.Exists(dll)) return false;
            byte[] b = File.ReadAllBytes(dll), w = Encoding.Unicode.GetBytes("SteamGameId");
            for (int i = 0; i + w.Length <= b.Length; i++)
            {
                int k = 0;
                while (k < w.Length && b[i + k] == w[k]) k++;
                if (k == w.Length) return true;
            }
            return false;
        }

        /// <summary>True when a player starts as BATTLETECH's app ID: from its code, or from steam_appid.txt.</summary>
        public static bool HasAppId(string player)
        {
            string f = Path.Combine(player, "steam_appid.txt");
            return SetsAppIdInCode(player) || (File.Exists(f) && File.ReadAllText(f).Trim() == AppId.ToString());
        }
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("BTU6GAT2");
        static readonly byte[] Magic3 = Encoding.ASCII.GetBytes("BTU6GAT3");
        // A package with any ranged entry says so in the magic: an installer from before ranges doesn't know these and
        // hands on to a newer one. The layout after the magic is unchanged.
        static readonly byte[] MagicR = Encoding.ASCII.GetBytes("BTU6GAR2");
        static readonly byte[] MagicR3 = Encoding.ASCII.GetBytes("BTU6GAR3");
        // v4 (key separation): a byte after the magic says whether the vault is locked; ranged entries need no magic of their
        // own. The packer writes only v4. v2 and v3 vaults are still read.
        static readonly byte[] Magic4 = Encoding.ASCII.GetBytes("BTU6GAR4");
#if UCU_MAC
        // The macOS package: the v4 layout with a platform string after the URL (always locked).
        static readonly byte[] MagicMac4 = Encoding.ASCII.GetBytes("BTU6GAM4");
#endif
        const int Window = 1 << 20;

        static bool Starts(byte[] head, byte[] magic)
        {
            if (head == null || head.Length < magic.Length) return false;
            for (int i = 0; i < magic.Length; i++)
                if (head[i] != magic[i]) return false;
            return true;
        }

        /// <summary>False when a vault starts with another format's mark: a newer installer is needed to read it.</summary>
        public static bool IsVault(byte[] head)
        {
            return Starts(head, Magic) || Starts(head, Magic3) || Starts(head, MagicR) || Starts(head, MagicR3) || Starts(head, Magic4)
#if UCU_MAC
                || Starts(head, MagicMac4)
#endif
                ;
        }

        /// <summary>True for a vault whose packer scanned for retail ranges: the ranged v2 and v3 magics, and every v4.</summary>
        public static bool IsRangedVault(byte[] head)
        {
            return Starts(head, MagicR) || Starts(head, MagicR3) || Starts(head, Magic4)
#if UCU_MAC
                || Starts(head, MagicMac4)
#endif
                ;
        }

        public static bool IsV4Vault(byte[] head)
        {
            return Starts(head, Magic4)
#if UCU_MAC
                || Starts(head, MagicMac4)
#endif
                ;
        }

        static bool IsV3(byte[] magic)
        {
            return Starts(magic, Magic3) || Starts(magic, MagicR3);
        }

        /// <summary>A release's share: what the unlock server hands out for it.</summary>
        public static byte[] Share(byte[] masterSecret, byte[] releaseId)
        {
            using (var h = new HMACSHA256(masterSecret))
#if UCU_MAC
                // A macOS package has its own share label, so what the server hands a Mac install opens Mac packages only.
                return h.ComputeHash(Encoding.UTF8.GetBytes((Platform != null ? "btucu-share-mac|" : "btucu-share|") + Hex(releaseId)));
#else
                return h.ComputeHash(Encoding.UTF8.GetBytes("btucu-share|" + Hex(releaseId)));
#endif
        }

        static byte[] Concat(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, r, 0, a.Length);
            Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
            return r;
        }

        /// <summary>The lock of a v3 or v4 vault, or null for one without the unlock server. Reads only the header.</summary>
        public static VaultLock PeekLock(Stream f)
        {
            using (var r = new BinaryReader(f, Encoding.UTF8, true))
            {
                byte[] magic = r.ReadBytes(Magic3.Length);
                if (Starts(magic, Magic) || Starts(magic, MagicR)) return null;
                bool v4 = Starts(magic, Magic4);
#if UCU_MAC
                bool mac = Starts(magic, MagicMac4);
                v4 = v4 || mac;
#endif
                if (v4 && !r.ReadBoolean())
                {
#if UCU_MAC
                    if (mac) throw new InvalidDataException("A macOS package is always locked");
                    CheckPlatform(null);
#endif
                    return null;
                }
                if (!v4 && !IsV3(magic)) throw new InvalidDataException("Not a package vault");
#if UCU_MAC
                var peeked = new VaultLock { ReleaseId = r.ReadBytes(16), Url = r.ReadString() };
                if (mac) peeked.Platform = r.ReadString();
                CheckPlatform(peeked.Platform);
                return peeked;
#else
                return new VaultLock { ReleaseId = r.ReadBytes(16), Url = r.ReadString() };
#endif
            }
        }

        // Files from the base depot that mods leave alone. Managed DLLs are deliberately absent: mod tools rewrite them.
        // BattleTech.exe is whole because its code is Unity's stock launcher; only the whole file is BATTLETECH's.
        static readonly string[] WholeFiles = { "BattleTech.exe" };
        static readonly string[] SampledFiles =
        {
            "BattleTech_Data/globalgamemanagers.assets",
            "BattleTech_Data/resources.assets",
            "BattleTech_Data/resources.assets.resS",
            "BattleTech_Data/sharedassets0.assets",
            "BattleTech_Data/level10",
            "BattleTech_Data/level10.resS",
        };

        public static string Hex(byte[] b)
        {
            var s = new StringBuilder(b.Length * 2);
            foreach (byte x in b) s.Append(x.ToString("x2"));
            return s.ToString();
        }

        public static byte[] FromHex(string s)
        {
            if (s.Length % 2 != 0) throw new FormatException("Odd-length hex");
            var b = new byte[s.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
            return b;
        }

        public static SHA256 NewSha() { return new SHA256Cng(); }

        public static string HashFile(string path)
        {
            using (var h = NewSha())
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
                return Hex(h.ComputeHash(f));
        }

        /// <summary>Reads the retail files the key depends on. A missing file throws, naming it.</summary>
        public static byte[] Fingerprint(string retail)
        {
#if UCU_MAC
            return Layout.Of(retail).Fingerprint(retail, null);
#else
            using (var h = NewSha())
            using (var ms = new MemoryStream())
            {
                foreach (string rel in WholeFiles)
                {
                    string p = Path.Combine(retail, rel);
                    if (!File.Exists(p)) throw new FileNotFoundException("Retail file not found", rel);
                    Add(ms, rel, h.ComputeHash(File.ReadAllBytes(p)));
                }
                foreach (string rel in SampledFiles)
                {
                    string p = Path.Combine(retail, rel);
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
                            Add(ms, rel + "@" + at + "/" + len, h.ComputeHash(buf, 0, got));
                        }
                    }
                }
                return h.ComputeHash(ms.ToArray());
            }
#endif
        }

        static void Add(Stream s, string label, byte[] digest)
        {
            byte[] l = Encoding.UTF8.GetBytes(label.ToLowerInvariant());
            s.Write(l, 0, l.Length);
            s.Write(digest, 0, digest.Length);
        }

        /// <summary>HKDF-SHA256 (RFC 5869), one output block.</summary>
        public static byte[] Derive(byte[] secret, byte[] salt, string info)
        {
            byte[] prk;
            using (var h = new HMACSHA256(salt)) prk = h.ComputeHash(secret);
            byte[] i = Encoding.UTF8.GetBytes(info);
            var t = new byte[i.Length + 1];
            Buffer.BlockCopy(i, 0, t, 0, i.Length);
            t[i.Length] = 1;
            using (var h = new HMACSHA256(prk)) return h.ComputeHash(t);
        }

        public static Aes NewAes(byte[] key, byte[] iv)
        {
#if NET
            Aes a = Aes.Create();
            a.KeySize = 256;
            a.Mode = CipherMode.CBC;
            a.Padding = PaddingMode.PKCS7;
#else
            var a = new AesCryptoServiceProvider { KeySize = 256, Mode = CipherMode.CBC, Padding = PaddingMode.PKCS7 };
#endif
            a.Key = key;
            a.IV = iv;
            return a;
        }

        public static byte[] Random(int n)
        {
            var b = new byte[n];
            using (var r = new RNGCryptoServiceProvider()) r.GetBytes(b);
            return b;
        }

        static byte[] Seal(byte[] plain, byte[] secret, string purpose)
        {
            byte[] salt = Random(16), iv = Random(16), ct;
            using (var a = NewAes(Derive(secret, salt, purpose + "|enc"), iv))
            using (var e = a.CreateEncryptor())
                ct = e.TransformFinalBlock(plain, 0, plain.Length);
            using (var ms = new MemoryStream())
            {
                ms.Write(salt, 0, 16);
                ms.Write(iv, 0, 16);
                ms.Write(ct, 0, ct.Length);
                byte[] body = ms.ToArray();
                using (var h = new HMACSHA256(Derive(secret, salt, purpose + "|mac")))
                {
                    byte[] tag = h.ComputeHash(body);
                    ms.Write(tag, 0, tag.Length);
                }
                return ms.ToArray();
            }
        }

        /// <summary>Returns null when the secret is wrong or the data was changed.</summary>
        static byte[] Open(byte[] sealedData, byte[] secret, string purpose)
        {
            if (sealedData.Length < 16 + 16 + 16 + 32) return null;
            var salt = new byte[16];
            var iv = new byte[16];
            Buffer.BlockCopy(sealedData, 0, salt, 0, 16);
            Buffer.BlockCopy(sealedData, 16, iv, 0, 16);
            int bodyLen = sealedData.Length - 32;
            byte[] want;
            using (var h = new HMACSHA256(Derive(secret, salt, purpose + "|mac")))
                want = h.ComputeHash(sealedData, 0, bodyLen);
            int diff = 0;
            for (int i = 0; i < 32; i++) diff |= want[i] ^ sealedData[bodyLen + i];
            if (diff != 0) return null;
            using (var a = NewAes(Derive(secret, salt, purpose + "|enc"), iv))
            using (var d = a.CreateDecryptor())
                return d.TransformFinalBlock(sealedData, 32, bodyLen - 32);
        }

        /// <summary>One slot per accepted retail build, so a new retail version needs a new slot, not a new pack.</summary>
        public static void WriteVault(string path, byte[] packageKey, IEnumerable<byte[]> fingerprints, List<Entry> entries)
        {
            WriteVault(path, packageKey, fingerprints, entries, null, null);
        }

        /// <summary>
        /// Writes a v4 vault. With a lock and its share, each retail slot is a pair: a check sealed under the fingerprint
        /// alone (so a wrong install is refused before the server is asked) and the package key sealed under fingerprint
        /// plus share. The manifest is sealed under a key from the package key and the share, so an old package key alone
        /// doesn't reveal a new release's file hashes, and with them its new blobs' keys. Without a lock the package key is
        /// sealed under the fingerprint alone and opens the manifest.
        /// </summary>
        public static void WriteVault(string path, byte[] packageKey, IEnumerable<byte[]> fingerprints, List<Entry> entries, VaultLock lk, byte[] share)
        {
            WriteVault(path, packageKey, fingerprints, entries, lk, share, null);
        }

        /// <summary>
        /// As above, with per-store sections after the manifest: for an install from that store, an entry of the same path
        /// replaces the manifest's and the others are added. An installer from before them stops reading at the manifest, so
        /// the main manifest stays what a Steam install gets.
        /// </summary>
        public static void WriteVault(string path, byte[] packageKey, IEnumerable<byte[]> fingerprints, List<Entry> entries, VaultLock lk, byte[] share,
            IDictionary<string, List<Entry>> stores)
        {
            using (var f = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(f))
            {
                var slots = new List<byte[]>();
#if UCU_MAC
                if (Platform != null && lk == null) throw new ArgumentException("A macOS package is always locked: give the unlock server's master secret and URL.");
                w.Write(Platform == null ? Magic4 : MagicMac4);
#else
                w.Write(Magic4);
#endif
                w.Write(lk != null);
                if (lk == null)
                {
                    foreach (byte[] fp in fingerprints) slots.Add(Seal(packageKey, fp, "slot4"));
                }
                else
                {
                    w.Write(lk.ReleaseId);
                    w.Write(lk.Url);
#if UCU_MAC
                    if (Platform != null) w.Write(Platform);
#endif
                    foreach (byte[] fp in fingerprints)
                    {
#if UCU_MAC
                        slots.Add(Seal(Encoding.ASCII.GetBytes(RetailText(Platform)), fp, "retail4"));
#else
                        slots.Add(Seal(Encoding.ASCII.GetBytes(RetailOk), fp, "retail4"));
#endif
                        slots.Add(Seal(packageKey, Concat(fp, share), "slot4"));
                    }
                }
                w.Write(slots.Count);
                foreach (byte[] s in slots) { w.Write(s.Length); w.Write(s); }
                byte[] manifestKey = lk == null ? packageKey : Derive(packageKey, share, "manifest4");
                byte[] m = Seal(Encoding.UTF8.GetBytes(WriteManifest(entries)), manifestKey, "manifest4");
                w.Write(m.Length);
                w.Write(m);
                if (stores == null || stores.Count == 0) return;
                w.Write(StoresMark);
                w.Write(stores.Count);
                foreach (var s in stores)
                {
                    byte[] b = Seal(Encoding.UTF8.GetBytes(WriteManifest(s.Value)), manifestKey, "store4|" + s.Key);
                    w.Write(s.Key);
                    w.Write(b.Length);
                    w.Write(b);
                }
            }
        }

        const int StoresMark = 0x53545231; // "STR1"

        /// <summary>The manifest with a store's section applied: same-path entries replaced, the others added.</summary>
        internal static List<Entry> ApplyStore(List<Entry> entries, List<Entry> section)
        {
            var at = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < entries.Count; i++) at[entries[i].Path] = i;
            foreach (Entry e in section)
            {
                int i;
                if (at.TryGetValue(e.Path, out i)) entries[i] = e;
                else { at[e.Path] = entries.Count; entries.Add(e); }
            }
            return entries;
        }

        const string RetailOk = "btucu-retail-ok";

        /// <summary>Returns the package key, or null when no slot accepts this retail install.</summary>
        public static byte[] ReadVault(string path, byte[] fingerprint, out List<Entry> entries)
        {
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read))
                return ReadVault(f, fingerprint, out entries);
        }

        /// <summary>Reads the vault from any stream, read front to back (a zip entry works).</summary>
        public static byte[] ReadVault(Stream f, byte[] fingerprint, out List<Entry> entries)
        {
            return ReadVault(f, fingerprint, null, out entries);
        }

        /// <summary>
        /// As above; a locked vault calls share once the retail install is accepted. share returns the release's share or
        /// throws UnlockException; without it a locked vault throws UnlockException.
        /// </summary>
        public static byte[] ReadVault(Stream f, byte[] fingerprint, Func<VaultLock, byte[]> share, out List<Entry> entries)
        {
            return ReadVault(f, fingerprint, share, null, out entries);
        }

        /// <summary>As above; store (StoreOf the retail install) picks the vault's section for that store, if it has one.</summary>
        public static byte[] ReadVault(Stream f, byte[] fingerprint, Func<VaultLock, byte[]> share, string store, out List<Entry> entries)
        {
            entries = null;
            using (var r = new BinaryReader(f, Encoding.UTF8, true))
            {
                byte[] magic = r.ReadBytes(Magic.Length);
                bool v4 = Starts(magic, Magic4);
#if UCU_MAC
                bool mac = Starts(magic, MagicMac4);
                v4 = v4 || mac;
#endif
                if (!v4 && !IsV3(magic) && !Starts(magic, Magic) && !Starts(magic, MagicR)) throw new InvalidDataException("Not a package vault");
                bool locked = v4 ? r.ReadBoolean() : IsV3(magic);
                // Each version seals under its own labels, so nothing in a v4 vault opens as another version. v2 and v3 share
                // the unnumbered ones.
                string any = v4 ? "4" : "", own = v4 ? "4" : "3";
                VaultLock lk = locked ? new VaultLock { ReleaseId = r.ReadBytes(16), Url = r.ReadString() } : null;
#if UCU_MAC
                if (mac)
                {
                    if (lk == null) throw new InvalidDataException("A macOS package is always locked");
                    lk.Platform = r.ReadString();
                }
                CheckPlatform(lk == null ? null : lk.Platform);
#endif
                byte[] key = null, slot = null;
                int slots = r.ReadInt32();
                for (int i = 0; i < slots; i += locked ? 2 : 1)
                {
                    byte[] s = r.ReadBytes(r.ReadInt32());
                    if (!locked) { key = key ?? Open(s, fingerprint, "slot" + any); continue; }
                    byte[] sealedKey = r.ReadBytes(r.ReadInt32());
#if UCU_MAC
                    byte[] check = slot == null ? Open(s, fingerprint, "retail" + own) : null;
                    if (check != null && Encoding.ASCII.GetString(check) == RetailText(lk.Platform)) slot = sealedKey;
#else
                    if (slot == null && Open(s, fingerprint, "retail" + own) != null) slot = sealedKey;
#endif
                }
                if (locked && slot == null) return null;
                byte[] manifestKey = key;
                if (locked)
                {
                    if (share == null) throw new UnlockException("This package needs an access code.");
                    byte[] s = share(lk);
                    key = Open(slot, Concat(fingerprint, s), "slot" + own);
                    if (key == null) throw new UnlockException("The unlock server's answer doesn't fit this release (release " + lk.Id + ").");
                    manifestKey = Derive(key, s, "manifest" + own);
                }
                if (key == null) return null;
                byte[] m = Open(r.ReadBytes(r.ReadInt32()), manifestKey, "manifest" + any);
                if (m == null) throw new InvalidDataException("Package manifest failed its check");
                entries = ReadManifest(Encoding.UTF8.GetString(m), v4);
                if (store == null) return key;
                int mark;
                try { mark = r.ReadInt32(); }
                catch (EndOfStreamException) { return key; }
                if (mark != StoresMark) throw new InvalidDataException("Unknown section after the package manifest");
                for (int n = r.ReadInt32(); n > 0; n--)
                {
                    string name = r.ReadString();
                    byte[] b = r.ReadBytes(r.ReadInt32());
                    if (name != store) continue;
                    byte[] s = Open(b, manifestKey, "store" + any + "|" + name);
                    if (s == null) throw new InvalidDataException("Package store section failed its check");
                    entries = ApplyStore(entries, ReadManifest(Encoding.UTF8.GetString(s), v4));
                }
                return key;
            }
        }

        static string WriteManifest(List<Entry> entries)
        {
            var s = new StringBuilder();
            foreach (Entry e in entries)
            {
                // A ranged entry always carries its hash: with no retail run at all, its residual is the whole file.
                string plain = e.BlobPlain == null || (string.IsNullOrEmpty(e.Pieces) && e.BlobPlain == e.Sha256) ? "" : e.BlobPlain;
                s.Append(e.Path).Append('\t').Append(e.Size).Append('\t').Append(e.Blob).Append('\t')
                 .Append(e.BlobSha256).Append('\t').Append(e.RetailPath).Append('\t').Append(e.Sha256).Append('\t')
                 .Append(e.Pieces ?? "").Append('\t').Append(plain).Append('\n');
            }
            return s.ToString();
        }

        static List<Entry> ReadManifest(string text, bool v4)
        {
            var list = new List<Entry>();
            foreach (string line in text.Split('\n'))
            {
                if (line.Length == 0) continue;
                string[] c = line.Split('\t');
                // The 7th column (pieces) came with retail-range packing; older manifests have 6. The 8th came with v4: the
                // hash of a ranged entry's residual, empty for any other entry.
                if (v4 && !GoodRow(c)) throw new InvalidDataException("The package manifest has a bad row: " + c[0]);
                var e = new Entry { Path = c[0], Size = long.Parse(c[1]), Blob = c[2], BlobSha256 = c[3], RetailPath = c[4], Sha256 = c[5],
                    Pieces = c.Length > 6 ? c[6] : "" };
                if (v4 && e.Blob.Length > 0) e.BlobPlain = c[7].Length > 0 ? c[7] : e.Sha256;
                list.Add(e);
            }
            return list;
        }

        /// <summary>A v4 row: 8 columns, a plain number for the size, 64 hex digits for every hash it needs, readable pieces.</summary>
        static bool GoodRow(string[] c)
        {
            long size;
            if (c.Length != 8 || c[0].Length == 0 || !Number(c[1], out size) || !IsHash(c[5])) return false;
            if (c[7].Length > 0 && !IsHash(c[7])) return false;
            if (c[2].Length > 0 && (!IsHash(c[3]) || (c[6].Length > 0 && c[7].Length == 0))) return false;
            ParsePieces(c[6]);
            return true;
        }

        static bool IsHash(string s)
        {
            return s.Length == 64 && s.All(x => (x >= '0' && x <= '9') || (x >= 'a' && x <= 'f'));
        }

        static bool Number(string s, out long n)
        {
            return long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n);
        }

        /// <summary>Parses an entry's pieces: B:&lt;length&gt; or R:&lt;offset&gt;:&lt;length&gt;:&lt;retail relative path&gt;.</summary>
        public static List<RangePiece> ParsePieces(string text)
        {
            var list = new List<RangePiece>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (string p in text.Split('|'))
            {
                string[] c = p.Split(new[] { ':' }, 4);
                long at, n;
                if (c[0] == "B" && c.Length == 2 && Number(c[1], out n))
                    list.Add(new RangePiece { Length = n });
                else if (c[0] == "R" && c.Length == 4 && Number(c[1], out at) && Number(c[2], out n))
                    list.Add(new RangePiece { Offset = at, Length = n, Path = c[3] });
                else throw new InvalidDataException("Bad piece in the package manifest: " + p);
            }
            return list;
        }

        /// <summary>The manifest's piece column for a piece list.</summary>
        public static string FormatPieces(IEnumerable<RangePiece> pieces)
        {
            var b = new StringBuilder();
            foreach (RangePiece p in pieces)
            {
                if (b.Length > 0) b.Append('|');
                b.Append(p.FromRetail ? "R:" + p.Offset + ":" + p.Length + ":" + p.Path : "B:" + p.Length);
            }
            return b.ToString();
        }

        /// <summary>Key separation: a blob's key comes from the content key and the hash of the bytes the blob holds.</summary>
        public static byte[] FileKey(byte[] contentKey, string plainSha)
        {
            return Derive(contentKey, FromHex(plainSha), "file4");
        }

        /// <summary>The key of an entry's blob, in the form its vault's version uses.</summary>
        public static byte[] BlobKey(byte[] packageKey, Entry e)
        {
            return e.BlobPlain != null ? FileKey(packageKey, e.BlobPlain) : Derive(packageKey, Encoding.UTF8.GetBytes(e.Blob), "file");
        }

        /// <summary>
        /// The blob a file is stored in. It depends only on the content key and the file's hash. "v4-" keeps the names apart
        /// from v2's and v3's, so a blob kept from an older package or download is never taken for a v4 one.
        /// </summary>
        public static string BlobFor(byte[] contentKey, string plainSha)
        {
            return "data/v4-" + Hex(Derive(contentKey, FromHex(plainSha), "blob4-name")).Substring(0, 32) + ".pak";
        }

        public static byte[] BlobIv(byte[] contentKey, string plainSha)
        {
            var iv = new byte[16];
            Buffer.BlockCopy(Derive(contentKey, FromHex(plainSha), "blob4-iv"), 0, iv, 0, 16);
            return iv;
        }

        /// <summary>Finds a Steam install, else a GOG one (GOG's installer writes its folder to the registry).</summary>
        public static string FindRetail()
        {
            return FindSteam() ?? FindGog();
        }

        public static string FindGog()
        {
#if UCU_MAC
            // GOG's macOS installer (and GOG Galaxy) puts the wrapper folder BATTLETECH, holding BattleTech.app and
            // Contents/Resources/goggame-*.info, in /Applications or the user's own Applications folder (mac-gog-s1).
            foreach (string apps in new[] { "/Applications", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications") })
            {
                string p = Path.Combine(apps, "BATTLETECH");
                if (Directory.Exists(Path.Combine(p, "BattleTech.app")) && StoreOf(p) == Gog) return p;
            }
            return null;
#else
            foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
                try
                {
                    using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (RegistryKey k = hklm.OpenSubKey(@"SOFTWARE\GOG.com\Games\" + GogId))
                    {
                        string p = k == null ? null : k.GetValue("path") as string;
                        if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p;
                    }
                }
                catch (Exception e) when (e is System.Security.SecurityException || e is UnauthorizedAccessException || e is IOException) { }
            return null;
#endif
        }

        /// <summary>Finds the install through Steam's own library files, without needing Steam to be running.</summary>
        public static string FindSteam()
        {
            string steam = null;
#if UCU_MAC
            steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Steam");
            if (!Directory.Exists(steam)) return null;
#else
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                if (k != null) steam = k.GetValue("SteamPath") as string;
            if (string.IsNullOrEmpty(steam)) return null;
#endif
            var libraries = new List<string> { steam };
            string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (string line in File.ReadAllLines(vdf))
                {
                    string v = VdfValue(line, "path");
                    if (v != null) libraries.Add(v);
                }
            foreach (string lib in libraries)
            {
                string acf = Path.Combine(lib, "steamapps", "appmanifest_" + AppId + ".acf");
                if (!File.Exists(acf)) continue;
                foreach (string line in File.ReadAllLines(acf))
                {
                    string v = VdfValue(line, "installdir");
                    if (v == null) continue;
                    string dir = Path.Combine(lib, "steamapps", "common", v);
                    if (Directory.Exists(dir)) return dir;
                }
            }
            return null;
        }

        static string VdfValue(string line, string key)
        {
            string[] p = line.Split('"');
            if (p.Length >= 4 && string.Equals(p[1], key, StringComparison.OrdinalIgnoreCase))
                return p[3].Replace(@"\\", @"\");
            return null;
        }
    }
}
