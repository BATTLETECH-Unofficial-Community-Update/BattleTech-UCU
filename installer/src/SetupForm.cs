// The installer and launcher window. The Install tab (this file) chooses the BATTLETECH folder and a destination, then
// installs or updates; Home, Mods, Settings and Tools (LauncherTabs.cs) manage the copy that is installed.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ReleaseGate
{
    // Fills the window's width and keeps the art's proportions. It adds no width of its own, so the form below sets the size.
    sealed class Banner : Control
    {
        readonly Image _art;

        public Banner()
        {
            _art = Image.FromStream(Assembly.GetExecutingAssembly().GetManifestResourceStream("banner.jpg"));
            Name = "Banner";
            Dock = DockStyle.Top;
            TabStop = false;
            AccessibleRole = AccessibleRole.Graphic;
            AccessibleName = "BattleTech: Unofficial Community Update (UCU)";
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public override Size GetPreferredSize(Size proposed) { return new Size(0, Height); }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Height = Width * _art.Height / _art.Width;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(_art, ClientRectangle);
        }
    }

    sealed partial class SetupForm : Form
    {
        public const string Title = "BattleTech: Unofficial Community Update (UCU) Launcher";
        static readonly Color Good = Color.FromArgb(0, 110, 50), Bad = Color.FromArgb(170, 30, 30);

        readonly TextBox _retail = new TextBox { Name = "RetailPath", AccessibleName = "BATTLETECH folder", Dock = DockStyle.Fill };
        readonly TextBox _dest = new TextBox { Name = "DestPath", AccessibleName = "Install to", Dock = DockStyle.Fill };
        readonly Label _retailNote = Note("RetailNote");
        readonly TextBox _code = new TextBox { Name = "AccessCode", AccessibleName = "Access code", Dock = DockStyle.Fill, CharacterCasing = CharacterCasing.Upper };
        readonly Label _codeCaption = new Label { Text = "Access code (only if you were given one: a Steam or GOG copy unlocks without one)", AutoSize = true, Margin = new Padding(3, 0, 3, 3) };
        readonly Label _codeNote = Note("CodeNote");
        readonly Label _destNote = Note("DestNote");
        readonly CheckBox _mods = new CheckBox { Name = "ModSupport", Text = "Mod support: load ModTek mods from the Mods folder", AutoSize = true, Checked = true, Margin = new Padding(3, 0, 3, 8) };
        readonly ProgressBar _bar = new ProgressBar { Name = "Progress", Dock = DockStyle.Fill, Maximum = 1000 };
        readonly Label _status = Note("Status");
        readonly Button _go = new UcuButton(UcuButton.Look.Primary) { Name = "Install", Text = "Install", Enabled = false };
        readonly Button _close = new UcuButton { Name = "Close", Text = "Close" };
        readonly Button _updates = new UcuButton { Name = "CheckUpdates", Text = "Check for updates" };
        readonly Button _verify = new UcuButton { Name = "VerifyInstall", Text = "Verify install", Visible = false };
        readonly Button _browseRetail = new UcuButton(UcuButton.Look.Compact) { Name = "BrowseRetail", Text = "Browse..." };
        readonly Button _browseDest = new UcuButton(UcuButton.Look.Compact) { Name = "BrowseDest", Text = "Browse..." };
        readonly System.Windows.Forms.Timer _settle = new System.Windows.Forms.Timer { Interval = 350 };
        readonly System.Windows.Forms.Timer _tick = new System.Windows.Forms.Timer { Interval = 150 };
        readonly System.Windows.Forms.Timer _planSettle = new System.Windows.Forms.Timer { Interval = 400 };
        readonly System.Windows.Forms.Timer _destSettle = new System.Windows.Forms.Timer { Interval = 300 };

        RetailCheck _check;
        InstallProgress _progress;
        CancellationTokenSource _stop;
        int _checkId, _planId, _destId;
        bool _destEdited, _settingDest, _installing, _installed, _opening;
        string _repair;             // the folder a failed Verify install is to repair once the package is read; null when none
        string _source = Remote.DefaultSource;
        volatile string _phase = "Installing";

        static Label Note(string name)
        {
            return new Label { Name = name, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 0, 3, 8), MaximumSize = new Size(560, 0) };
        }

        /// <summary>
        /// BATTLETECH-UCU beside the retail folder. An install made before the rename, in BATTLETECH-Unity6, is offered
        /// instead while there's no BATTLETECH-UCU, so updating it doesn't download the whole game into a new folder.
        /// </summary>
        public static string DefaultDest(string retail)
        {
            string parent = Path.GetDirectoryName(retail.TrimEnd('\\', '/')) ?? retail;
            string dest = Path.Combine(parent, "BATTLETECH-UCU"), old = Path.Combine(parent, "BATTLETECH-Unity6");
            try
            {
                if (!Directory.Exists(dest) && File.Exists(Path.Combine(old, Installer.RecordName))) return old;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException) { }
            return dest;
        }

        /// <summary>
        /// remote: a release source to install from (Remote.Open), or null for the package beside the installer. home: the
        /// install this exe sits in (Launcher.InstallBeside), whose launcher reads a release only when asked.
        /// </summary>
        public SetupForm(string detected, string remote, string home = null)
        {
            SuspendLayout();
            Text = Title;
            Name = "SetupForm";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? Icon;   // the exe's own icon (ucu.ico, via build.ps1)
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            // The tabs set the size: their width, and the banner's height at that width above their height (Load below).
            ClientSize = new Size(660, 640);

            var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Width = 600, Padding = new Padding(14) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 500));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var intro = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(590, 0),
                Margin = new Padding(3, 0, 3, 12),
                Text = "This installs or updates BattleTech: Unofficial Community Update (UCU). It needs your own copy of BATTLETECH, which stays unchanged.",
            };
            t.Controls.Add(intro);
            t.SetColumnSpan(intro, 2);
            AddRow(t, "BATTLETECH folder", _retail, _browseRetail, _retailNote);
            // A v3 package is unlocked by the unlock server: through Steam, or for an access code (docs/keyserver-design.md).
            foreach (Control c in new Control[] { _codeCaption, _code, _codeNote })
            {
                t.Controls.Add(c);
                t.SetColumnSpan(c, 2);
            }
            AddRow(t, "Install to", _dest, _browseDest, _destNote);
            t.Controls.Add(_mods);
            t.SetColumnSpan(_mods, 2);
            t.Controls.Add(_bar);
            t.SetColumnSpan(_bar, 2);
            t.Controls.Add(_status);
            t.SetColumnSpan(_status, 2);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
            buttons.Controls.Add(_close);
            buttons.Controls.Add(_go);
            buttons.Controls.Add(_updates);
            buttons.Controls.Add(_verify);
            t.Controls.Add(buttons);
            t.SetColumnSpan(buttons, 2);
            Controls.Add(BuildTabs(t));
            var banner = new Banner();
            Controls.Add(banner);
            Load += (s, e) => ClientSize = new Size(ClientSize.Width, banner.Height + _tabs.Height);
            AcceptButton = _go;

            _retail.TextChanged += (s, e) => QueueCheck();
            _code.TextChanged += (s, e) => QueueCheck();
            // Only typing (box focused) or Browse counts as the player's choice: Wine's WinForms raises TextChanged on its own.
            _dest.TextChanged += (s, e) => { if (!_settingDest && _dest.Focused) _destEdited = true; ShowDest(); ShowVerify(); _mods.Checked = Installer.ModsWanted(_dest.Text.Trim().Trim('"')); QueueHome(); };
            _verify.Click += (s, e) => StartVerify();
            _settle.Tick += (s, e) => { _settle.Stop(); RunCheck(); };
            _tick.Tick += (s, e) => ShowProgress();
            _planSettle.Tick += (s, e) => { _planSettle.Stop(); PlanDest(); };
            _destSettle.Tick += (s, e) => { _destSettle.Stop(); RunDestCheck(); };
            _updates.Click += (s, e) => OpenRemote(_source);
            _browseRetail.Click += (s, e) => Browse(_retail, "Choose the folder that holds BattleTech.exe");
            _browseDest.Click += (s, e) => Browse(_dest, "Choose where to install");
            _go.Click += (s, e) => GoPressed();
            _close.Click += (s, e) => Close();
            FormClosing += OnClosing;

            _code.Text = Unlock.Code ?? Unlock.LoadSaved() ?? "";
            if (remote == null) ShowLock();
            _retail.Text = detected ?? "";
            if (detected == null) Show(_retailNote, "BATTLETECH wasn't found automatically. Enter its folder, or use Browse.", Bad);
            if (remote != null)
            {
                _updates.Visible = false;
                _opening = true;
                Shown += (s, e) => OpenRemote(remote);
            }
            StartLauncher(home);
            ResumeLayout(true);
        }

        /// <summary>Shows the access code only for a package that the unlock server unlocks.</summary>
        void ShowLock()
        {
            bool locked = Installer.PackageLock != null;
            _codeCaption.Visible = _code.Visible = _codeNote.Visible = locked;
        }

        static void AddRow(TableLayoutPanel t, string caption, TextBox box, Button browse, Label note)
        {
            var label = new Label { Text = caption, AutoSize = true, Margin = new Padding(3, 0, 3, 3) };
            t.Controls.Add(label);
            t.SetColumnSpan(label, 2);
            t.Controls.Add(box);
            t.Controls.Add(browse);
            t.Controls.Add(note);
            t.SetColumnSpan(note, 2);
        }

        static void Show(Label l, string text, Color colour)
        {
            l.Text = text;
            l.ForeColor = colour;
        }

        void Browse(TextBox box, string prompt)
        {
            using (var d = new FolderBrowserDialog { Description = prompt, ShowNewFolderButton = box == _dest })
            {
                if (Directory.Exists(box.Text)) d.SelectedPath = box.Text;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (box == _dest) _destEdited = true;
                box.Text = d.SelectedPath;
            }
        }

        void QueueCheck()
        {
            _check = null;
            _go.Enabled = false;
            _settle.Stop();
            _settle.Start();
        }

        void RunCheck()
        {
            if (_opening)
            {
                Show(_retailNote, "Waiting for the release...", SystemColors.ControlText);
                return;
            }
            // A launcher in its install has no package until a release is read: nothing to check BATTLETECH against yet.
            if (!Installer.HasPackage)
            {
                Show(_retailNote, "Use Check for updates to update or repair the install; the release is read online.", SystemColors.ControlText);
                ShowDest();
                return;
            }
            int id = ++_checkId;
            string typed = _retail.Text;
            Unlock.Code = _code.Text;
            Show(_retailNote, "Checking...", SystemColors.ControlText);
            if (_code.Visible && Unlock.Normalize(_code.Text).Length == 0 && Gate.StoreOf(Installer.TidyRetail(typed)) == Gate.Steam) Show(_codeNote, "Confirming your copy with Steam...", SystemColors.ControlText);
            Task.Run(() => Installer.Check(typed)).ContinueWith(t =>
            {
                if (id != _checkId || IsDisposed) return;
                _check = t.Result;
                if (_check.Problem != null) _repair = null;
                if (_check.CodeProblem)
                {
                    Show(_retailNote, "BATTLETECH found.", Good);
                    Show(_codeNote, _check.Problem, Bad);
                }
                else if (_check.Problem != null)
                {
                    Show(_retailNote, _check.Problem, Bad);
                    Show(_codeNote, "", Good);
                }
                else
                {
                    Show(_retailNote, "BATTLETECH found.", Good);
                    if (_code.Visible && Unlock.LastRoute == "steam") Show(_codeNote, "Steam confirmed that you own BATTLETECH.", Good);
                    else if (_code.Visible && Unlock.LastRoute == "gog") Show(_codeNote, "The unlock server accepted this GOG install of BATTLETECH.", Good);
                    else if (_code.Visible)
                    {
                        Show(_codeNote, "Access code accepted.", Good);
                        Unlock.Save(_code.Text);
                    }
                    else Show(_codeNote, "", Good);
                    if (!_destEdited)
                    {
                        _settingDest = true;
                        _dest.Text = DefaultDest(_check.Folder);
                        _settingDest = false;
                    }
                    // The manifest's hash is what the release notes publish, so a player can tie this package to them.
                    string release = Installer.Remote != null ? "Release " + Installer.Remote.Version + ". " : "";
                    if (_check.ManifestSha256 != null)
                        Show(_status, release + "Public manifest SHA-256 " + _check.ManifestSha256 + " (compare it with the release notes).", SystemColors.ControlText);
                    else
                        Show(_status, release + "This package has no public manifest.", SystemColors.ControlText);
                }
                ShowDest();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>
        /// A busy bar for the folder reads below. Reading an existing install (Installer.CheckDest, PlanInstall) compares
        /// every file with the package and takes tens of seconds, so it runs off the UI thread while the bar animates.
        /// Not while an install or check owns the bar.
        /// </summary>
        void Busy(bool on)
        {
            if (_installing) return;
            _bar.Style = on ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
            if (!on) _bar.Value = 0;
        }

        void ShowDest()
        {
            _destSettle.Stop();
            _planSettle.Stop();
            ++_planId;
            ++_destId;
            if (_check == null || _check.Problem != null)
            {
                Show(_destNote, "", Good);
                _go.Enabled = false;
                Busy(false);
                return;
            }
            // Debounced: typing a path would otherwise start a read per keystroke.
            _go.Enabled = false;
            Show(_destNote, "Reading that folder...", SystemColors.ControlText);
            Busy(true);
            _destSettle.Start();
        }

        void RunDestCheck()
        {
            if (_check == null || _check.Problem != null) return;
            int id = _destId;
            RetailCheck check = _check;
            string typed = _dest.Text;
            Task.Run(() =>
            {
                long s;
                string p = Installer.CheckDest(typed, check, out s);
                return Tuple.Create(p, s);
            }).ContinueWith(t =>
            {
                if (id != _destId || IsDisposed) return;
                Busy(false);
                if (t.IsFaulted)
                {
                    Show(_destNote, "That folder can't be read: " + t.Exception.GetBaseException().Message, Bad);
                    _go.Enabled = false;
                    return;
                }
                string problem = t.Result.Item1;
                long space = t.Result.Item2;
                if (problem != null) Show(_destNote, problem, Bad);
                else if (Installer.Remote != null)
                {
                    Show(_destNote, "Checking what to download...", SystemColors.ControlText);
                    _planSettle.Start();
                }
                else
                {
                    string dest = _dest.Text.Trim().Trim('"');
                    string link = Installer.LinkNote(check, dest);
                    string used = space < Installer.FreshSpace(check, dest)
                        ? string.Format("Part of the game is already here; {0:F1} GiB more will be used.", space / 1073741824.0)
                        : string.Format("{0:F1} GiB will be used.", space / 1073741824.0);
                    Show(_destNote, link.Length > 0 ? used + " " + link : used, Good);
                }
                _go.Enabled = problem == null && !_installing;
                if (problem != null) _repair = null;
                else if (Installer.Remote == null) RepairIfAsked();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Online installs: says what this destination still needs, and names the button Update when it holds an install.</summary>
        void PlanDest()
        {
            if (_check == null || _check.Problem != null || _installing) return;
            int id = ++_planId;
            RetailCheck check = _check;
            string dest;
            try { dest = DestFull(); }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException) { return; }
            Busy(true);
            Task.Run(() => Installer.PlanInstall(check, dest)).ContinueWith(t =>
            {
                if (id != _planId || IsDisposed || _installing) return;
                Busy(false);
                if (t.IsFaulted)
                {
                    Show(_destNote, "That folder can't be read: " + t.Exception.GetBaseException().Message, Bad);
                    return;
                }
                Installer.Plan p = t.Result;
                string version = Installer.Remote.Version;
                if (p.Files == 0 && p.Remove == 0) Show(_destNote, "Up to date with release " + version + ".", Good);
                else
                {
                    string link = Installer.LinkNote(check, dest);
                    string what = string.Format("Release {0}: {1} files to write, {2} to download.", version, p.Files, Amount(p.FetchBytes));
                    Show(_destNote, link.Length > 0 ? what + " " + link : what, Good);
                }
                if (!_installed) _go.Text = p.Same > 0 ? "Update" : "Install";
                RepairIfAsked();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Reads a release online. Once found, its vault replaces the local package's and both folders are checked again.</summary>
        void OpenRemote(string source)
        {
            _source = source;
            _opening = true;
            _updates.Enabled = false;
            _check = null;
            _go.Enabled = false;
            Show(_status, "Looking for the newest release...", SystemColors.ControlText);
            Task.Run(() => Remote.Open(source, CancellationToken.None)).ContinueWith(t =>
            {
                if (IsDisposed) return;
                _opening = false;
                if (t.IsFaulted)
                {
                    _repair = null;
                    Show(_status, "Couldn't get the release: " + t.Exception.GetBaseException().Message, Bad);
                    // With no package beside the installer the release is the only way on, so offer it again.
                    _updates.Text = Installer.HasPackage ? "Check for updates" : "Try again";
                    _updates.Visible = true;
                    _updates.Enabled = true;
                    QueueCheck();
                    return;
                }
                RemotePackage p = t.Result;
                if (p.NeedsNewerInstaller)
                {
                    OfferNewerInstaller(p, source);
                    return;
                }
                Installer.UseRemote(p);
                ShowLock();
                _updates.Visible = false;
                Show(_status, "Release " + p.Version + " found online.", Good);
                QueueCheck();
                QueueHome();
                CheckLauncher(p.Installer);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void OfferNewerInstaller(RemotePackage p, string source)
        {
            _repair = null;
            if (Setup.Handed)
            {
                Show(_status, "Release " + p.Version + " needs a newer installer than the one it ships.", Bad);
                return;
            }
            if (MessageBox.Show(this, "Release " + p.Version + " needs a newer installer. Download it and continue there?", Title,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                Show(_status, "Release " + p.Version + " needs a newer installer.", Bad);
                _updates.Enabled = true;
                QueueCheck();
                return;
            }
            Show(_status, "Downloading the newer installer...", SystemColors.ControlText);
            Task.Run(() => Remote.StartNewerInstaller(p, Setup.ArgumentsFor(source), false, CancellationToken.None)).ContinueWith(t =>
            {
                if (IsDisposed) return;
                if (t.IsFaulted)
                {
                    Show(_status, "Couldn't start the newer installer: " + t.Exception.GetBaseException().Message, Bad);
                    _updates.Enabled = true;
                    QueueCheck();
                    return;
                }
                t.Result.Dispose();
                Close();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        static string Amount(long bytes)
        {
            return bytes < (1L << 30) ? string.Format("{0:F1} MiB", bytes / 1048576.0) : string.Format("{0:F2} GiB", bytes / 1073741824.0);
        }

        void GoPressed()
        {
            if (_installed)
            {
                Play();
                return;
            }
            string dest = DestFull();
            // A folder this installer filled before is updated in place, without asking.
            if (Directory.Exists(dest) && Directory.GetFileSystemEntries(dest).Length > 0 && !File.Exists(Path.Combine(dest, Installer.RecordName)) &&
                MessageBox.Show(this, "That folder isn't empty. Files with the same names will be replaced. Continue?", Title,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            string problem = Installer.ProbeWrite(dest);
            if (problem != null)
            {
                Show(_destNote, problem, Bad);
                return;
            }
            StartInstall(dest);
        }

        string DestFull() { return Path.GetFullPath(_dest.Text.Trim().Trim('"')); }

        void StartInstall(string dest)
        {
            _installing = true;
            SetInputs(false);
            SetLauncherInputs(false);
            _close.Text = "Cancel";
            _progress = new InstallProgress { Total = _check.Need };
            _stop = new CancellationTokenSource();
            ++_planId;
            _planSettle.Stop();
            RetailCheck check = _check;
            InstallProgress progress = _progress;
            CancellationToken token = _stop.Token;
            RemotePackage remote = Installer.Remote;
            bool mods = _mods.Checked;
            _phase = remote != null ? "Downloading" : "Installing";
            _tick.Start();
            string message = null;
            Task.Run(() =>
            {
                if (remote != null)
                {
                    InstallResult f = Installer.Fetch(Installer.PlanInstall(check, dest), dest, progress, token, out message);
                    if (f != InstallResult.Installed) return f;
                    _phase = "Installing";
                    Interlocked.Exchange(ref progress.Done, 0);
                }
                InstallResult r = Installer.Install(check, dest, progress, token, out message);
                if (r == InstallResult.Installed)
                {
                    string m = Installer.ApplyMods(dest, mods);
                    if (m != null) message += " " + m;
                    string l = Launcher.PlaceCopy(dest);
                    if (l != null) message += " " + l;
                    _phase = "Backing up saves";
                    string b = SaveBackup.Run();
                    if (b != null) message += " " + b;
                }
                return r;
            }).ContinueWith(t =>
            {
                _tick.Stop();
                _installing = false;
                InstallResult r = t.IsFaulted ? InstallResult.Failed : t.Result;
                string text = t.IsFaulted ? "Install failed: " + t.Exception.GetBaseException().Message : message;
                if (r == InstallResult.Installed)
                {
                    _installed = true;
                    _bar.Value = _bar.Maximum;
                    if (remote != null) text = "Release " + remote.Version + ". " + text;
                    Show(_status, text + (_check != null && _check.Store == Gate.Gog ? "" : " Steam must be running when you play."), Good);
                    _go.Text = "Start game";
                    _go.Enabled = true;
                    _close.Text = "Close";
                    _verify.Enabled = true;
                    ShowVerify();
                    Installed(dest);
                }
                else
                {
                    Show(_status, text, r == InstallResult.Cancelled ? SystemColors.ControlText : Bad);
                    _close.Text = "Close";
                    SetInputs(true);
                    SetLauncherInputs(true);
                    ShowDest();
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void SetInputs(bool on)
        {
            _retail.Enabled = _code.Enabled = _dest.Enabled = _browseRetail.Enabled = _browseDest.Enabled = _go.Enabled = _updates.Enabled = _verify.Enabled = _mods.Enabled = on;
        }

        /// <summary>Verify install shows when the destination holds a public manifest this installer left there.</summary>
        void ShowVerify()
        {
            bool has;
            try { has = File.Exists(Path.Combine(DestFull(), PublicManifest.FileName)); }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException) { has = false; }
            _verify.Visible = has;
            _verify.Enabled = has && !_installing;
        }

        /// <summary>Checks every installed file against the install's copy of the public manifest (Installer.Verify).</summary>
        void StartVerify()
        {
            string dest = DestFull();
            _installing = true;
            SetInputs(false);
            SetLauncherInputs(false);
            _close.Text = "Cancel";
            _progress = new InstallProgress();
            _stop = new CancellationTokenSource();
            InstallProgress progress = _progress;
            CancellationToken token = _stop.Token;
            _phase = "Checking";
            _tick.Start();
            string message = null;
            Task.Run(() => Installer.Verify(dest, progress, token, out message)).ContinueWith(t =>
            {
                _tick.Stop();
                _installing = false;
                InstallResult r = t.IsFaulted ? InstallResult.Failed : t.Result;
                string text = t.IsFaulted ? "Check failed: " + t.Exception.GetBaseException().Message : message;
                _bar.Value = r == InstallResult.Installed ? _bar.Maximum : 0;
                Show(_status, text, r == InstallResult.Installed ? Good : r == InstallResult.Cancelled ? SystemColors.ControlText : Bad);
                _close.Text = "Close";
                SetInputs(true);
                if (_installed) _go.Enabled = true; else ShowDest();
                ShowVerify();
                SetLauncherInputs(true);
                if (r == InstallResult.Damaged && MessageBox.Show(this, text + "\n\nRepair now? It installs the newest release's copy of these files, so an older install is updated too.", Title,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    StartRepair(dest);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Reads the package again and installs into dest as soon as it can (RepairIfAsked).</summary>
        void StartRepair(string dest)
        {
            _repair = dest;
            _installed = false;
            _destEdited = true;     // the check below must not move the folder to the default one
            if (Installer.HasPackage) QueueCheck();
            else if (!_opening) OpenRemote(_source);
        }

        void RepairIfAsked()
        {
            string dest = _repair;
            _repair = null;
            if (dest != null && _go.Enabled && string.Equals(DestFull(), dest, StringComparison.OrdinalIgnoreCase)) GoPressed();
        }

        void ShowProgress()
        {
            long done = Interlocked.Read(ref _progress.Done), total = Math.Max(1, _progress.Total);
            _bar.Style = ProgressBarStyle.Continuous;   // a folder read may have left it animating
            _bar.Value = (int)Math.Min(_bar.Maximum, done * _bar.Maximum / total);
            Show(_status, string.Format("{0}... {1} of {2}", _phase, Amount(done), Amount(total)), SystemColors.ControlText);
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (!_installing) return;
            e.Cancel = true;
            if (_stop.IsCancellationRequested) return;
            if (MessageBox.Show(this, _phase == "Checking" ? "Stop checking the install?" : "Stop installing? Run this again later to finish.", Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                _stop.Cancel();
                Show(_status, "Stopping...", SystemColors.ControlText);
            }
        }
    }
}
