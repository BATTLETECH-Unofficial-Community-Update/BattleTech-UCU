// What the shared installer sources need from macOS and the Unix family: cloning a file (APFS), the execute bit, the free space of
// the volume a folder is on, and the real path of a folder, links followed. Compiled only into the macOS tool (UCU_MAC).
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ReleaseGate
{
    static class MacPlatform
    {
        [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "clonefile", SetLastError = true)]
        static extern int CloneFile([MarshalAs(UnmanagedType.LPUTF8Str)] string src, [MarshalAs(UnmanagedType.LPUTF8Str)] string dst, uint flags);

        /// <summary>
        /// Clones src to dst (copy-on-write on APFS: no extra space, and a write to one never reaches the other). False when the
        /// clone isn't possible here (another volume, not APFS, not macOS): the caller copies instead. dst must not exist.
        /// </summary>
        public static bool TryClone(string src, string dst)
        {
            if (!OperatingSystem.IsMacOS()) return false;
            try { return CloneFile(src, dst, 0) == 0; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        static string _probed;
        static bool _probe;

        /// <summary>
        /// True when a file of retail can be cloned into dest's volume: the plan then counts those files as taking no space. Tried
        /// for real on one small retail file, into the folder dest will be made in (or its nearest existing parent); the probe file
        /// is removed at once.
        /// </summary>
        public static bool CanClone(string retail, string dest)
        {
            if (!OperatingSystem.IsMacOS()) return false;
            string parent = ExistingParent(dest);
            lock (typeof(MacPlatform))
            {
                if (_probed == parent + "|" + retail) return _probe;
                string src = Path.Combine(retail, "BattleTech.app", "Contents", "Info.plist");
                string dst = Path.Combine(parent, ".ucu-clone-probe-" + Process.GetCurrentProcess().Id);
                bool ok = false;
                try
                {
                    if (File.Exists(src) && !File.Exists(dst)) ok = TryClone(src, dst);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { ok = false; }
                finally
                {
                    try { if (File.Exists(dst)) File.Delete(dst); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
                }
                _probed = parent + "|" + retail;
                return _probe = ok;
            }
        }

        /// <summary>Starts 0755 for a file the manifest marks executable, else 0644, so the installed app starts and nothing else is executable.</summary>
        public static void SetMode(string path, bool executable)
        {
            if (OperatingSystem.IsWindows()) return;
            File.SetUnixFileMode(path, executable
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        /// <summary>The owner's execute bit is set (always true on Windows, where files have none).</summary>
        public static bool IsExecutable(string path)
        {
            return OperatingSystem.IsWindows() || (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0;
        }

        static string ExistingParent(string path)
        {
            string p = Path.GetFullPath(path);
            while (!Directory.Exists(p))
            {
                string up = Path.GetDirectoryName(p);
                if (up == null || up == p) break;
                p = up;
            }
            return p;
        }

        /// <summary>Free bytes on the volume a path is on (or would be made on): the nearest existing parent decides, never the root.</summary>
        public static long FreeSpace(string path)
        {
            return new DriveInfo(ExistingParent(path)).AvailableFreeSpace;
        }

        /// <summary>
        /// The path with every link in its existing part followed (/tmp is /private/tmp on a Mac), so a folder that is a link into
        /// the BATTLETECH folder is judged by where it really is. What doesn't exist yet is kept as written.
        /// </summary>
        public static string RealPath(string path)
        {
            return Real(Path.GetFullPath(path), 0);
        }

        static string Real(string path, int depth)
        {
            if (depth > 40) throw new IOException("Too many links in " + path);
            string root = Path.GetPathRoot(path);
            string[] parts = path.Substring(root.Length).Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            string cur = root;
            for (int i = 0; i < parts.Length; i++)
            {
                string next = Path.Combine(cur, parts[i]);
                FileSystemInfo fi = Directory.Exists(next) ? (FileSystemInfo)new DirectoryInfo(next) : new FileInfo(next);
                if (!fi.Exists && fi.LinkTarget == null)
                {
                    for (int k = i; k < parts.Length; k++) cur = Path.Combine(cur, parts[k]);
                    return cur;
                }
                if (fi.LinkTarget != null)
                {
                    FileSystemInfo target = fi.ResolveLinkTarget(true);
                    next = target == null ? next : Real(target.FullName, depth + 1);
                }
                cur = next;
            }
            return cur;
        }
    }
}
