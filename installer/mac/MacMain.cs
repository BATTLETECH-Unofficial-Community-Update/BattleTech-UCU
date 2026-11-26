// The macOS command-line tool (ucu): the installer of tools/release-gate on a Mac, with no window. The same gate, installer and
// online fetch as BattleTechUCUSetup.exe, from the same sources, plus what macOS needs (mac/MacPlatform.cs). It reads the
// player's own BATTLETECH (Steam, macOS) and never writes to it; a Mac unlocks on those game files, as GOG does (Unlock.cs), so
// there are no access codes, no Steam ticket and nothing secret in an argument, the environment or a log. There is no uninstall
// command, as in the Windows installer: the player deletes the install folder.
//
//   ucu install   --dest <folder> [--retail <folder>] [--remote [<source>]]      a new install; resumes one that stopped
//   ucu update    --dest <folder> [--retail <folder>] [--remote [<source>]]      brings an install made by this tool up to date
//   ucu verify    --dest <folder>                                                checks every installed file against the install's manifest; no network
//   ucu repair    --dest <folder> [--retail <folder>] [--remote [<source>]]      verify, then rewrites what differs
// --remote reads a release online (default: Remote.DefaultSource, the macOS data repository); with no package beside the tool
// it is the default. --retail defaults to the Steam library's BATTLETECH. Before the first write of an install or update the
// tool backs up BATTLETECH's Steam Cloud saves once (SaveBackup.cs) and stops if it can't.
// Tests only: --unlock-url <url> (ask this server), --steam-root <folder> and --backup-root <folder> (SaveBackup.cs), --dry-run.
// Exit codes are InstallResult's (0 installed, 1 failed, 2 BATTLETECH not found, 3 refused, 4 damaged, 5 cancelled), 64 for usage.
using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace ReleaseGate
{
    static class MacMain
    {
        const int Usage = 64;

        static int Main(string[] a)
        {
            try { return Run(a); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is UnlockException || e is ArgumentException || e is InvalidDataException)
            {
                Console.Error.WriteLine(e.Message);
                return (int)InstallResult.Failed;
            }
        }

        static int Run(string[] a)
        {
            Gate.Use(Gate.MacPlatform);
            string cmd = a.Length > 0 ? a[0] : "", dest = null, retail = null, remote = null;
            bool dryRun = false;
            for (int i = 1; i < a.Length; i++)
            {
                if (a[i] == "--dest" && i + 1 < a.Length) dest = a[++i];
                else if (a[i] == "--retail" && i + 1 < a.Length) retail = a[++i];
                else if (a[i] == "--remote") remote = i + 1 < a.Length && !a[i + 1].StartsWith("--") ? a[++i] : Remote.DefaultSource;
                else if (a[i] == "--dry-run") dryRun = true;
                else if (a[i] == "--unlock-url" && i + 1 < a.Length) Unlock.UrlOverride = a[++i];
                else if (a[i] == "--steam-root" && i + 1 < a.Length) SaveBackup.SteamRootOverride = Path.GetFullPath(a[++i]);
                else if (a[i] == "--backup-root" && i + 1 < a.Length) SaveBackup.BackupRootOverride = Path.GetFullPath(a[++i]);
                else return Fail("Unknown option " + a[i] + ".", Usage);
            }
            if (new[] { "install", "update", "verify", "repair" }.All(c => c != cmd)) return Fail("Usage: ucu install|update|verify|repair --dest <folder> [--retail <folder>] [--remote [<source>]]", Usage);
            if (dest == null) return Fail("Give the folder with --dest <folder>.", Usage);
            dest = Path.GetFullPath(dest);
            switch (cmd)
            {
                case "verify": return Verify(dest);
                case "repair":
                    if (!Installer.HasRecord(dest)) return Fail(dest + (Installer.Unfinished(dest) ? " holds an install that stopped before it wrote anything down: run install again." : " holds no install made by this tool: run install."), (int)InstallResult.Failed);
                    int v = Verify(dest);
                    if (v == (int)InstallResult.Installed) { Console.Out.WriteLine("Nothing to repair."); return 0; }
                    if (v != (int)InstallResult.Damaged) return v;
                    break;
                case "update":
                    if (!Installer.HasRecord(dest)) return Fail(dest + (Installer.Unfinished(dest) ? " holds an install that stopped before it wrote anything down: run install again." : " holds no install made by this tool: run install."), (int)InstallResult.Failed);
                    break;
                case "install":
                    if (Installer.HasRecord(dest) && !Installer.Unfinished(dest)) return Fail(dest + " already holds an install: run update, or repair.", (int)InstallResult.Failed);
                    break;
            }
            int r = Install(dest, retail, remote, dryRun);
            if (cmd == "repair" && r == 0) return Verify(dest);
            return r;
        }

        static int Fail(string message, int code)
        {
            Console.Error.WriteLine(message);
            return code;
        }

        static int Verify(string dest)
        {
            string message;
            InstallResult r = Installer.Verify(dest, new InstallProgress(), CancellationToken.None, out message);
            (r == InstallResult.Installed ? Console.Out : Console.Error).WriteLine(message);
            return (int)r;
        }

        static int Install(string dest, string retail, string remote, bool dryRun)
        {
            retail = retail ?? Gate.FindRetail();
            if (retail == null) return Fail("BATTLETECH wasn't found in your Steam libraries. Pass --retail <the folder that holds BattleTech.app>.", (int)InstallResult.NoRetail);
            string inside = Installer.InsideRetail(dest, retail) ?? Installer.DestRefusal(dest, new Entry[0]);   // before any server is asked
            if (inside != null) return Fail(inside, (int)InstallResult.Failed);
            if (remote == null && !Installer.HasPackage) remote = Remote.DefaultSource;
            if (remote != null)
            {
                Console.Out.WriteLine("Reading " + remote + "...");
                RemotePackage p;
                try { p = Remote.Open(remote, CancellationToken.None); }
                catch (IOException e) { return Fail(e.Message, (int)InstallResult.Failed); }
                if (p.NeedsNewerInstaller) return Fail("Release " + p.Version + " needs a newer version of this tool. Download the tool again.", (int)InstallResult.Failed);
                Installer.UseRemote(p);
                Console.Out.WriteLine(string.Format("Release {0}: {1} package files in {2} data parts.", p.Version, p.Blobs.Count, p.Blobs.Values.Select(b => b.Part).Distinct().Count()));
            }
            RetailCheck c = Installer.Check(retail);
            if (c.Problem != null) return Fail(c.Problem, (int)InstallResult.RetailRefused);
            long space;
            string problem = Installer.CheckDest(dest, c, out space);
            if (problem != null) return Fail(problem, (int)InstallResult.Failed);
            string note = Installer.LinkNote(c, dest);
            if (note.Length > 0) Console.Out.WriteLine(note);
            Installer.Plan plan = Installer.PlanInstall(c, dest);
            if (remote != null)
            {
                Console.Out.WriteLine(string.Format("{0} files to write ({1:F2} GiB), {2} up to date, {3} to remove; {4} package files to download ({5:F2} GiB).",
                    plan.Files, plan.WriteBytes / 1073741824.0, plan.Same, plan.Remove, plan.Fetch.Count, plan.FetchBytes / 1073741824.0));
                if (dryRun) return (int)InstallResult.Installed;
            }
            else if (dryRun) return Fail("--dry-run needs --remote.", Usage);
            // The saves first, and nothing is written if they can't be backed up: BATTLETECH and UCU share one Steam Cloud folder.
            string saves = SaveBackup.Run();
            if (saves != null) Console.Out.WriteLine(saves);
            if (saves != null && saves.Contains("couldn't be backed up")) return Fail("Nothing was installed: the saves backup has to work first.", (int)InstallResult.Failed);
            problem = Installer.ProbeWrite(dest);
            if (problem != null) return Fail(problem, (int)InstallResult.Failed);
            string message;
            if (remote != null)
            {
                InstallResult f = Installer.Fetch(plan, dest, new InstallProgress(), CancellationToken.None, out message);
                if (f != InstallResult.Installed) return Fail(message, (int)f);
            }
            Console.Out.WriteLine("Installing...");
            InstallResult r = Installer.Install(c, dest, new InstallProgress(), CancellationToken.None, out message);
            (r == InstallResult.Installed ? Console.Out : Console.Error).WriteLine(message);
            return (int)r;
        }
    }
}
