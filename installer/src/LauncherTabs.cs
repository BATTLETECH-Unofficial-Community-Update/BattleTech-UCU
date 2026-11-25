// The launcher's tabs around the Install tab (SetupForm.cs): Home (version, update check, Play), Mods (mod support and a
// linked mod pack: BTA, RogueTech, BEX:T, Hyades Rim or another ModTek v4 pack), Settings (how the game starts, update check) and Tools
// (verify, folders, shortcuts, reporting a problem). Every tab works on the install named in the Install tab's
// "Install to" box: the folder this exe sits in for the launcher copy, otherwise the last install the launcher managed.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ReleaseGate
{
    sealed partial class SetupForm
    {
        /// <summary>One solid page colour behind every tab, a shade warmer than the system grey.</summary>
        static readonly Color PageColor = Color.FromArgb(243, 243, 241);

        readonly TabControl _tabs = new TabControl { Name = "Tabs", Dock = DockStyle.Top, Size = new Size(660, 430) };
        readonly TabPage _homeTab = new TabPage("Home") { Name = "HomeTab", AutoScroll = true, UseVisualStyleBackColor = false, BackColor = PageColor };
        readonly TabPage _installTab = new TabPage("Install / Update") { Name = "InstallTab", AutoScroll = true, UseVisualStyleBackColor = false, BackColor = PageColor };
        readonly TabPage _modsTab = new TabPage("Mods") { Name = "ModsTab", AutoScroll = true, UseVisualStyleBackColor = false, BackColor = PageColor };
        readonly TabPage _settingsTab = new TabPage("Settings") { Name = "SettingsTab", AutoScroll = true, UseVisualStyleBackColor = false, BackColor = PageColor };
        readonly TabPage _toolsTab = new TabPage("Tools") { Name = "ToolsTab", AutoScroll = true, UseVisualStyleBackColor = false, BackColor = PageColor };

        // Home
        readonly Label _homeInstall = Note("HomeInstall");
        readonly Label _homeVersion = Note("HomeVersion");
        readonly Label _homeLatest = Note("HomeLatest");
        readonly Label _homeMods = Note("HomeMods");
        readonly Label _homeStatus = Note("HomeStatus");
        readonly Button _play = new UcuButton(UcuButton.Look.Primary) { Name = "Play", Text = "Play", Enabled = false };
        readonly Button _homeUpdate = new UcuButton { Name = "HomeUpdate", Text = "Check for updates" };
        readonly Button _homeNotes = new UcuButton { Name = "ReleaseNotes", Text = "Release notes" };
        readonly Button _homeLauncher = new UcuButton { Name = "UpdateLauncher", Text = "Update launcher", Visible = false };

        // Mods
        readonly CheckBox _modsOn = new CheckBox { Name = "ModsOn", Text = "Mod support: load ModTek mods from the install's Mods folder", AutoSize = true, Margin = new Padding(3, 0, 3, 8) };
        readonly Label _modsNote = Note("ModsNote");
        readonly RadioButton _packBta = new RadioButton { Name = "PackBta", Text = "BTA (BattleTech Advanced 3062)", AutoSize = true };
        readonly RadioButton _packRt = new RadioButton { Name = "PackRogueTech", Text = "RogueTech", AutoSize = true };
        readonly RadioButton _packBex = new RadioButton { Name = "PackBex", Text = "BEX:T (BattleTech Extended: Tactics)", AutoSize = true };
        readonly RadioButton _packHr = new RadioButton { Name = "PackHyadesRim", Text = "Hyades Rim", AutoSize = true };
        readonly RadioButton _packOther = new RadioButton { Name = "PackOther", Text = "Another ModTek v4 pack", AutoSize = true };
        readonly TextBox _packFolder = new TextBox { Name = "PackFolder", AccessibleName = "Mod pack folder", Dock = DockStyle.Fill };
        readonly Button _packBrowse = new UcuButton(UcuButton.Look.Compact) { Name = "PackBrowse", Text = "Browse..." };
        readonly Label _packNote = Note("PackNote");
        readonly RadioButton _packJunction = new RadioButton { Name = "PackJunction", Text = "Link (no extra disk space; both games share the mods' settings)", AutoSize = true, Checked = true };
        readonly RadioButton _packCopy = new RadioButton { Name = "PackCopy", Text = "Copy (about 20 GiB; UCU's copy is its own)", AutoSize = true };
        readonly CheckBox _packSaves = new CheckBox { Name = "PackSaves", Text = "Bring the pack's saves (BTASaves, ModSaves) across", AutoSize = true, Checked = true };
        readonly CheckBox _combatSaves = new CheckBox { Name = "CombatSaves", Text = "EXPERIMENTAL: Enable in-mission saves. Modpacks disable this for a good reason! We try to take away the reason with some on-the-fly changes to many mods. Make sure you still save before and after missions as this feature is a priority but it is still new! These saves can only be loaded by UCU with this option enabled.", AutoSize = true, MaximumSize = new Size(480, 0), Margin = new Padding(3, 4, 3, 2) };
        readonly Label _combatSavesNote = Note("CombatSavesNote");
        readonly Button _packLink = new UcuButton { Name = "PackLink", Text = "Link pack" };
        readonly Button _packUnlink = new UcuButton { Name = "PackUnlink", Text = "Unlink" };
        readonly Label _packState = Note("PackState");

        // Settings
        readonly ComboBox _api = new ComboBox { Name = "GraphicsApi", AccessibleName = "Graphics API", DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        readonly TextBox _extraArgs = new TextBox { Name = "ExtraArgs", AccessibleName = "Extra launch options", Dock = DockStyle.Fill };
        readonly CheckBox _checkOnStart = new CheckBox { Name = "CheckOnStart", Text = "Check for a new release when the launcher opens", AutoSize = true };
        readonly CheckBox _closeOnPlay = new CheckBox { Name = "CloseOnPlay", Text = "Close the launcher when the game starts", AutoSize = true };
        readonly Label _settingsNote = Note("SettingsNote");

        // Tools
        readonly Label _toolsStatus = Note("ToolsStatus");
        Button[] _toolButtons = new Button[0];

        readonly System.Windows.Forms.Timer _homeSettle = new System.Windows.Forms.Timer { Interval = 400 };
        LauncherSettings _settings = LauncherSettings.Load();
        ReleaseSummary _latest;
        string _latestProblem;
        byte[] _newLauncher;        // a newer published launcher, downloaded and checked; null when there is none
        int _homeId;
        bool _loadingSettings;

        static readonly string[] ApiValues = { LauncherSettings.Default, LauncherSettings.D3D11, LauncherSettings.D3D12 };

        TabControl BuildTabs(TableLayoutPanel install)
        {
            _installTab.Controls.Add(install);
            BuildHome();
            BuildMods();
            BuildSettings();
            BuildTools();
            _tabs.TabPages.AddRange(new[] { _homeTab, _installTab, _modsTab, _settingsTab, _toolsTab });
            _homeSettle.Tick += (s, e) => { _homeSettle.Stop(); RefreshHome(); };
            _tabs.SelectedIndexChanged += (s, e) => { if (_tabs.SelectedTab == _modsTab) RefreshMods(); };
            return _tabs;
        }

        static TableLayoutPanel Page(TabPage page)
        {
            var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(14) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 480));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            page.Controls.Add(t);
            return t;
        }

        static void Span(TableLayoutPanel t, Control c)
        {
            t.Controls.Add(c);
            t.SetColumnSpan(c, 2);
        }

        static Label Heading(string text)
        {
            return new Label { Text = text, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Margin = new Padding(3, 6, 3, 4) };
        }

        static FlowLayoutPanel Row(params Control[] controls)
        {
            var f = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 6), WrapContents = true };
            f.Controls.AddRange(controls);
            return f;
        }

        // ---- Home ----

        void BuildHome()
        {
            TableLayoutPanel t = Page(_homeTab);
            Span(t, Heading("Your install"));
            Span(t, _homeInstall);
            Span(t, _homeVersion);
            Span(t, _homeMods);
            Span(t, Heading("Newest release"));
            Span(t, _homeLatest);
            Span(t, Row(_play, _homeUpdate, _homeNotes, _homeLauncher));
            Span(t, _homeStatus);
            _play.Click += (s, e) => Play();
            _homeUpdate.Click += (s, e) => HomeUpdate();
            _homeNotes.Click += (s, e) => Launcher.Open(Launcher.NotesPage(_latest));
            _homeLauncher.Click += (s, e) => UpdateLauncher();
        }

        /// <summary>The install the launcher tabs act on, or null while the Install tab's box isn't a usable path.</summary>
        string CurrentInstall()
        {
            try
            {
                string d = _dest.Text.Trim().Trim('"');
                return d.Length == 0 ? null : Path.GetFullPath(d).TrimEnd('\\');
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException) { return null; }
        }

        void QueueHome()
        {
            _homeSettle.Stop();
            _homeSettle.Start();
        }

        void RefreshHome()
        {
            string dest = CurrentInstall();
            bool installed = Launcher.IsInstall(dest);
            int id = ++_homeId;
            _play.Enabled = installed && !_installing;
            if (!installed)
            {
                Show(_homeInstall, dest == null ? "No install folder chosen." : "UCU isn't installed in " + dest + " yet. Use the Install / Update tab.", SystemColors.ControlText);
                Show(_homeVersion, "", Good);
                Show(_homeMods, "", Good);
            }
            else
            {
                Show(_homeInstall, dest, SystemColors.ControlText);
                Show(_homeVersion, "Installed version: reading...", SystemColors.ControlText);
                bool mods = Installer.ModsWanted(dest);
                PackLink link = Launcher.ReadLink(dest);
                string pack = link == null ? "no mod pack linked" : (link.Junction ? "linked to " : "copied from ") + (link.Source ?? "a mod pack");
                Show(_homeMods, "Mod support " + (mods ? "on" : "off") + "; " + pack + ".", SystemColors.ControlText);
            }
            ShowLatest(null);
            if (!installed) return;
            Task.Run(() => Launcher.InstalledVersion(dest)).ContinueWith(t =>
            {
                if (id != _homeId || IsDisposed) return;
                if (t.Result == null) Show(_homeVersion, "Installed version: unknown (BattleTech_Data\\resources.assets has no version).", Bad);
                ShowLatest(t.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Shows the installed version (null while unknown) against the newest release, once that has been read.</summary>
        void ShowLatest(string installed)
        {
            if (installed != null) Show(_homeVersion, "Installed version: " + installed, SystemColors.ControlText);
            string latest = Installer.Remote != null ? Installer.Remote.Version : _latest != null ? _latest.Version : null;
            _homeNotes.Enabled = latest != null;
            if (latest == null)
            {
                Show(_homeLatest, _latestProblem ?? "Not checked yet.", _latestProblem != null ? Bad : SystemColors.ControlText);
                return;
            }
            if (installed == null)
            {
                Show(_homeLatest, "Release " + latest + ".", SystemColors.ControlText);
                return;
            }
            if (Launcher.SameVersion(installed, latest))
            {
                Show(_homeLatest, "Release " + latest + ": you're up to date.", Good);
                _homeUpdate.Text = "Check for updates";
            }
            else
            {
                Show(_homeLatest, "Release " + latest + " is available. Update installs only the files that changed.", Bad);
                _homeUpdate.Text = "Update";
            }
        }

        /// <summary>The update check: reads the newest release's tag only (Remote.Peek), not its package.</summary>
        void CheckLatest()
        {
            _homeUpdate.Enabled = false;
            Show(_homeLatest, "Looking for the newest release...", SystemColors.ControlText);
            Task.Run(() => Remote.Peek(_source)).ContinueWith(t =>
            {
                if (IsDisposed) return;
                _homeUpdate.Enabled = !_installing;
                if (t.IsFaulted)
                {
                    _latest = null;
                    _latestProblem = "Couldn't read the newest release: " + t.Exception.GetBaseException().Message;
                }
                else
                {
                    _latest = t.Result;
                    _latestProblem = null;
                    CheckLauncher(_latest.Installer);
                }
                QueueHome();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Offers the newest release's launcher when it is newer than this one. A check that fails says nothing.</summary>
        void CheckLauncher(RemoteAsset installer)
        {
            if (_newLauncher != null) return;
            Task.Run(() => Remote.NewerLauncher(installer, System.Threading.CancellationToken.None)).ContinueWith(t =>
            {
                if (IsDisposed || t.IsFaulted || t.Result == null || _newLauncher != null) return;
                _newLauncher = t.Result;
                _homeLauncher.Visible = true;
                // An install under way, or a repair about to start, isn't interrupted: the button stays.
                if (_installing || _repair != null) return;
                if (MessageBox.Show(this, "A new version of this launcher is available.\n\nUpdate it and restart the launcher now?", Title,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    UpdateLauncher();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void UpdateLauncher()
        {
            if (_installing)
            {
                Show(_homeStatus, "Wait for the install to finish, then update the launcher.", Bad);
                return;
            }
            try
            {
                Launcher.ReplaceSelf(_newLauncher);
                Setup.Restart();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is System.ComponentModel.Win32Exception)
            {
                _tabs.SelectedTab = _homeTab;
                Show(_homeStatus, "The launcher couldn't be updated: " + e.Message + " You can download it from " + Launcher.ReleasesPage + ".", Bad);
                return;
            }
            Close();
        }

        /// <summary>Update (or install, or repair): reads the release's package and hands over to the Install tab.</summary>
        void HomeUpdate()
        {
            _tabs.SelectedTab = _installTab;
            if (_opening) return;
            if (Installer.Remote == null) OpenRemote(_source);
            else QueueCheck();
        }

        void Play()
        {
            string dest = CurrentInstall();
            if (!Launcher.IsInstall(dest))
            {
                Show(_homeStatus, "UCU isn't installed in that folder.", Bad);
                return;
            }
            if (Launcher.GameRunning(dest))
            {
                Show(_homeStatus, "BattleTech UCU is already running.", Bad);
                return;
            }
            if (Installer.Unfinished(dest))
            {
                DialogResult r = MessageBox.Show(this, "The last install or update of this folder didn't finish, so it may mix files of two versions and graphics or saves may go wrong.\n\nRepair it now? No starts the game as it is.",
                    Title, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);
                if (r == DialogResult.Yes) HomeUpdate();
                if (r != DialogResult.No) return;
            }
            string loader = _settings.GraphicsApi == LauncherSettings.D3D12 ? Installer.CheckD3D12Loader(dest) : null;
            try { Launcher.StartGame(dest, _settings); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is IOException || e is InvalidOperationException)
            {
                Show(_homeStatus, "The game couldn't be started: " + e.Message, Bad);
                return;
            }
            Remember(dest);
            if (_settings.CloseOnPlay) Close();
            else Show(_homeStatus, "Game started." + (loader == null ? "" : " " + loader), loader == null || loader.Contains("replaced") ? Good : Bad);
        }

        // ---- Mods ----

        void BuildMods()
        {
            TableLayoutPanel t = Page(_modsTab);
            Span(t, Heading("Mod support"));
            Span(t, _modsOn);
            Span(t, _modsNote);
            Span(t, Heading("Mod pack"));
            var intro = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(590, 0),
                Margin = new Padding(3, 0, 3, 6),
                Text = "Uses a mod pack you already installed for retail BATTLETECH. The pack's own install stays as it is; UCU links or copies its Mods folder.",
            };
            Span(t, intro);
            Span(t, Row(_packBta, _packRt, _packBex, _packHr, _packOther));
            t.Controls.Add(_packFolder);
            t.Controls.Add(_packBrowse);
            Span(t, _packNote);
            var mode = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Margin = new Padding(0, 0, 0, 4) };
            mode.Controls.AddRange(new Control[] { _packJunction, _packCopy });
            Span(t, mode);
            Span(t, _packSaves);
            Span(t, _combatSaves);
            Span(t, _combatSavesNote);
            Span(t, Row(_packLink, _packUnlink));
            Span(t, _packState);

            _modsOn.CheckedChanged += (s, e) => ModsToggled();
            foreach (RadioButton r in new[] { _packBta, _packRt, _packBex, _packHr, _packOther }) r.CheckedChanged += (s, e) => { if (((RadioButton)s).Checked) PackChanged(); };
            _packFolder.TextChanged += (s, e) => PackChanged();
            _packJunction.CheckedChanged += (s, e) => PackChanged();
            _packSaves.CheckedChanged += (s, e) => PackChanged();
            _combatSaves.CheckedChanged += (s, e) => CombatSavesToggled();
            _packBrowse.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog { Description = "Choose the mod pack's install (the folder that holds Mods\\ModTek), or its Mods folder", ShowNewFolderButton = false })
                {
                    if (Directory.Exists(_packFolder.Text)) d.SelectedPath = _packFolder.Text;
                    if (d.ShowDialog(this) == DialogResult.OK) _packFolder.Text = d.SelectedPath;
                }
            };
            _packLink.Click += (s, e) => RunLink(false);
            _packUnlink.Click += (s, e) => RunLink(true);
        }

        string PackKind()
        {
            return _packRt.Checked ? Launcher.PackRogueTech : _packBex.Checked ? Launcher.PackBex : _packHr.Checked ? Launcher.PackHyades : _packOther.Checked ? Launcher.PackOther : Launcher.PackBta;
        }

        void PackChanged()
        {
            if (_loadingSettings) return;
            _settings.PackKind = PackKind();
            _settings.PackFolder = _packFolder.Text.Trim().Trim('"');
            _settings.PackCopy = _packCopy.Checked;
            _settings.PackNoSaves = !_packSaves.Checked;
            SaveSettings();
            RefreshPackNote();
        }

        void RefreshPackNote()
        {
            string folder = _packFolder.Text.Trim().Trim('"');
            string name = Launcher.PackName(PackKind());
            string rt = PackKind() == Launcher.PackRogueTech || PackKind() == Launcher.PackBex ? " " + name + " hasn't been tested on UCU yet; report what you find." : "";
            if (folder.Length == 0)
                Show(_packNote, (PackKind() == Launcher.PackBta ? "Leave empty to search your Steam libraries for BTA, or choose its folder." : "Choose the " + name + " install (the folder that holds Mods\\ModTek), or its Mods folder" +
                    (PackKind() == Launcher.PackHyades ? ", such as BATTLETECH\\Mods or BATTLETECH\\Mods_HyadesRim." : ".")) + rt, SystemColors.ControlText);
            else if (!Launcher.IsModTekPack(folder))
                Show(_packNote, "No ModTek v4 there (neither Mods\\ModTek\\lib\\ModTek.Preloader.dll nor ModTek\\lib\\ModTek.Preloader.dll). Choose the folder the pack's installer filled, or its Mods folder." + rt, Bad);
            else
            {
                string guess = Launcher.GuessPack(folder);
                string mismatch = guess != null && guess != PackKind() ? " It looks like " + Launcher.PackName(guess) + ", though." : "";
                Show(_packNote, "ModTek v4 found." + mismatch + rt, mismatch.Length > 0 ? Bad : Good);
            }
            RefreshModsButtons();
        }

        void RefreshMods()
        {
            string dest = CurrentInstall();
            bool installed = Launcher.IsInstall(dest);
            bool hasSupport = installed && File.Exists(Path.Combine(dest, "ModSupport", "BattleTechPortBootstrap.dll"));
            _loadingSettings = true;
            _modsOn.Checked = installed ? Installer.ModsWanted(dest) : _mods.Checked;
            bool combatSavesEnabled = installed && Launcher.CombatSavesEnabled(dest);
            _combatSaves.Checked = combatSavesEnabled;
            _settings.CombatSavesEnabled = combatSavesEnabled;
            _loadingSettings = false;
            if (!installed) Show(_modsNote, "Install UCU first; the choice here is then applied to it. Until then it is the Install tab's setting.", SystemColors.ControlText);
            else if (!hasSupport) Show(_modsNote, "This release has no ModSupport folder, so mod support can't be turned on here.", Bad);
            else Show(_modsNote, "Takes effect the next time the game starts.", SystemColors.ControlText);
            _combatSaves.Enabled = installed && hasSupport && Installer.ModsWanted(dest) && !_installing;
            if (!installed || !hasSupport || !Installer.ModsWanted(dest))
                Show(_combatSavesNote, "Turn on mod support and install its ModSupport files before enabling combat saves.", SystemColors.ControlText);
            else Show(_combatSavesNote, "Takes effect the next time the game starts. Story-mission-start autosaves stay skipped, and the in-combat quit popup keeps IRTweaks' wording.", SystemColors.ControlText);
            PackLink link = installed ? Launcher.ReadLink(dest) : null;
            if (!installed) Show(_packState, "", Good);
            else if (link == null) Show(_packState, "No mod pack is linked to this install.", SystemColors.ControlText);
            else
                Show(_packState, string.Format("{0} {1}{2}.", link.Junction ? "Linked to" : "Copied from", link.Source ?? "a mod pack",
                    link.LinkedAt != null ? " on " + link.LinkedAt.Replace('T', ' ') : ""), Good);
            _packLink.Text = link != null ? "Re-sync pack" : "Link pack";
            RefreshPackNote();
        }

        void RefreshModsButtons()
        {
            string dest = CurrentInstall();
            bool installed = Launcher.IsInstall(dest);
            bool script = installed && File.Exists(Launcher.LinkScriptPath(dest));
            string folder = _packFolder.Text.Trim().Trim('"');
            bool folderOk = folder.Length == 0 ? PackKind() == Launcher.PackBta : Launcher.IsModTekPack(folder);
            _packLink.Enabled = script && folderOk && !_installing;
            _packUnlink.Enabled = script && Launcher.ReadLink(dest) != null && !_installing;
            _modsOn.Enabled = !_installing;
            if (installed && !script) Show(_packState, "This release has no " + Launcher.LinkScript + " in its ModSupport folder; update UCU to link a pack.", Bad);
        }

        void ModsToggled()
        {
            if (_loadingSettings) return;
            _mods.Checked = _modsOn.Checked;
            string dest = CurrentInstall();
            if (!Launcher.IsInstall(dest)) return;
            if (Launcher.GameRunning(dest))
            {
                Show(_modsNote, "Close the game first; the change applies when it next starts.", Bad);
                _loadingSettings = true;
                _modsOn.Checked = Installer.ModsWanted(dest);
                _mods.Checked = _modsOn.Checked;
                _loadingSettings = false;
                return;
            }
            string m = Installer.ApplyMods(dest, _modsOn.Checked);
            Show(_modsNote, m ?? "This release has no ModSupport folder, so mod support can't be changed here.", m != null && !m.Contains("couldn't") ? Good : Bad);
            QueueHome();
        }

        void CombatSavesToggled()
        {
            if (_loadingSettings) return;
            string dest = CurrentInstall();
            if (!Launcher.IsInstall(dest))
            {
                RefreshMods();
                Show(_combatSavesNote, "Choose an installed UCU folder first.", Bad);
                return;
            }
            if (Launcher.GameRunning(dest))
            {
                RefreshMods();
                Show(_combatSavesNote, "Close the game first; the change applies when it next starts.", Bad);
                return;
            }
            bool enabled = _combatSaves.Checked;
            string problem = Launcher.SetCombatSavesEnabled(dest, enabled);
            if (problem != null)
            {
                RefreshMods();
                Show(_combatSavesNote, "Combat saves setting couldn't be changed: " + problem, Bad);
                return;
            }
            _settings.CombatSavesEnabled = enabled;
            string settingsProblem = _settings.Save();
            if (settingsProblem != null)
                Show(_combatSavesNote, "Combat saves are " + (enabled ? "on" : "off") + ", but the launcher setting couldn't be saved: " + settingsProblem, Bad);
            else
                Show(_combatSavesNote, "Combat saves " + (enabled ? "on" : "off") + ". Takes effect the next time the game starts.", Good);
        }

        void RunLink(bool unlink)
        {
            string dest = CurrentInstall();
            if (!Launcher.IsInstall(dest)) return;
            string folder = _packFolder.Text.Trim().Trim('"');
            if (unlink && MessageBox.Show(this, "Remove the linked or copied mods from this install? The mod pack's own install isn't touched, and saves stay.", Title,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            Process p;
            try { p = Launcher.RunLink(dest, folder, _packCopy.Checked, !_packSaves.Checked, unlink); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is IOException || e is InvalidOperationException)
            {
                Show(_packState, "The link script couldn't be started: " + e.Message, Bad);
                return;
            }
            Show(_packState, (unlink ? "Unlinking" : "Linking") + " in the window that opened. Follow it there, then press Enter in it to close it.", SystemColors.ControlText);
            _packLink.Enabled = _packUnlink.Enabled = false;
            Task.Run(() => { using (p) { p.WaitForExit(); return p.ExitCode; } }).ContinueWith(t =>
            {
                if (IsDisposed) return;
                int code = t.IsFaulted ? -1 : t.Result;
                RefreshMods();
                QueueHome();
                if (code != 0) Show(_packState, "The link script stopped with an error (exit " + code + "); its window said why. " + _packState.Text, Bad);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ---- Settings ----

        void BuildSettings()
        {
            TableLayoutPanel t = Page(_settingsTab);
            Span(t, Heading("Starting the game"));
            Span(t, new Label { Text = "Graphics API", AutoSize = true, Margin = new Padding(3, 0, 3, 3) });
            _api.Items.AddRange(new object[] { "Game default (DirectX 11)", "DirectX 11", "DirectX 12 with multi-core rendering" });
            Span(t, _api);
            Span(t, new Label { Text = "Extra launch options, passed to BattleTech.exe (for example -screen-fullscreen 0)", AutoSize = true, MaximumSize = new Size(590, 0), Margin = new Padding(3, 8, 3, 3) });
            Span(t, _extraArgs);
            Span(t, _closeOnPlay);
            Span(t, Heading("Updates"));
            Span(t, _checkOnStart);
            Span(t, _settingsNote);
            Show(_settingsNote, "Settings are kept for this Windows user in " + LauncherSettings.FilePath + ". The game's own options (resolution, sound, controls) stay in its options menu.", SystemColors.ControlText);

            _api.SelectedIndexChanged += (s, e) => SettingsChanged();
            _extraArgs.TextChanged += (s, e) => SettingsChanged();
            _closeOnPlay.CheckedChanged += (s, e) => SettingsChanged();
            _checkOnStart.CheckedChanged += (s, e) => SettingsChanged();
        }

        void SettingsChanged()
        {
            if (_loadingSettings) return;
            _settings.GraphicsApi = ApiValues[Math.Max(0, _api.SelectedIndex)];
            _settings.ExtraArgs = _extraArgs.Text.Trim();
            _settings.CloseOnPlay = _closeOnPlay.Checked;
            _settings.CheckOnStart = _checkOnStart.Checked;
            SaveSettings();
        }

        void SaveSettings()
        {
            string problem = _settings.Save();
            if (problem != null) Show(_settingsNote, "Settings couldn't be saved: " + problem, Bad);
        }

        void LoadSettingsIntoTabs()
        {
            _loadingSettings = true;
            _api.SelectedIndex = Math.Max(0, Array.IndexOf(ApiValues, _settings.GraphicsApi));
            _extraArgs.Text = _settings.ExtraArgs;
            _closeOnPlay.Checked = _settings.CloseOnPlay;
            _checkOnStart.Checked = _settings.CheckOnStart;
            _packRt.Checked = _settings.PackKind == Launcher.PackRogueTech;
            _packBex.Checked = _settings.PackKind == Launcher.PackBex;
            _packHr.Checked = _settings.PackKind == Launcher.PackHyades;
            _packOther.Checked = _settings.PackKind == Launcher.PackOther;
            _packBta.Checked = !_packRt.Checked && !_packBex.Checked && !_packHr.Checked && !_packOther.Checked;
            _packFolder.Text = _settings.PackFolder;
            _packCopy.Checked = _settings.PackCopy;
            _packJunction.Checked = !_settings.PackCopy;
            _packSaves.Checked = !_settings.PackNoSaves;
            _combatSaves.Checked = _settings.CombatSavesEnabled;
            _loadingSettings = false;
        }

        // ---- Tools ----

        void BuildTools()
        {
            TableLayoutPanel t = Page(_toolsTab);
            Span(t, Heading("Check and repair"));
            var verify = new UcuButton { Name = "ToolsVerify", Text = "Verify files" };
            var repair = new UcuButton { Name = "ToolsRepair", Text = "Repair / update" };
            Span(t, Row(verify, repair));
            Span(t, Heading("Folders"));
            var folder = new UcuButton { Name = "OpenInstall", Text = "Install folder" };
            var logs = new UcuButton { Name = "OpenLogs", Text = "Game logs" };
            var backups = new UcuButton { Name = "OpenBackups", Text = "Save backups" };
            var mods = new UcuButton { Name = "OpenMods", Text = "Mods folder" };
            Span(t, Row(folder, logs, backups, mods));
            Span(t, Heading("Shortcuts to this launcher"));
            var desktop = new UcuButton { Name = "DesktopShortcut", Text = "Desktop" };
            var start = new UcuButton { Name = "StartMenuShortcut", Text = "Start menu" };
            Span(t, Row(desktop, start));
            Span(t, Heading("Help"));
            var collect = new UcuButton { Name = "CollectReport", Text = "Collect bug report" };
            var report = new UcuButton { Name = "ReportProblem", Text = "Report a problem" };
            var releases = new UcuButton { Name = "AllReleases", Text = "All releases" };
            Span(t, Row(collect, report, releases));
            Span(t, _toolsStatus);
            _toolButtons = new[] { verify, repair, folder, mods, desktop, start };

            verify.Click += (s, e) =>
            {
                ShowVerify();
                if (!_verify.Visible) { Show(_toolsStatus, "This folder has no " + PublicManifest.FileName + " to check against. Update it first.", Bad); return; }
                _tabs.SelectedTab = _installTab;
                StartVerify();
            };
            repair.Click += (s, e) => { _tabs.SelectedTab = _installTab; if (!_opening) OpenRemote(_source); };
            folder.Click += (s, e) => OpenFolder(CurrentInstall());
            mods.Click += (s, e) => { string d = CurrentInstall(); OpenFolder(d == null ? null : Path.Combine(d, "Mods")); };
            logs.Click += (s, e) => OpenFolder(Launcher.LogFolder());
            backups.Click += (s, e) => OpenFolder(SaveBackup.BackupRoot);
            desktop.Click += (s, e) => MakeShortcut(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            start.Click += (s, e) => MakeShortcut(Environment.GetFolderPath(Environment.SpecialFolder.Programs));
            collect.Click += (s, e) => CollectReport(collect);
            report.Click += (s, e) => Launcher.Open(Launcher.IssuesPage);
            releases.Click += (s, e) => Launcher.Open(Launcher.ReleasesPage);
        }

        /// <summary>Zips every log to the Desktop, copies the zip's path and offers the GitHub issue page to attach it to.</summary>
        void CollectReport(Button button)
        {
            button.Enabled = false;
            Show(_toolsStatus, "Collecting logs...", SystemColors.ControlText);
            string install = CurrentInstall();
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            Task.Run(() => BugReport.Collect(install, _settings, desktop)).ContinueWith(t =>
            {
                if (IsDisposed) return;
                button.Enabled = true;
                if (t.IsFaulted)
                {
                    Exception x = t.Exception.GetBaseException();
                    Show(_toolsStatus, "The bug report couldn't be made: " + x.Message, Bad);
                    return;
                }
                string zip = t.Result;
                try { Clipboard.SetText(zip); } catch (System.Runtime.InteropServices.ExternalException) { }
                long size = new FileInfo(zip).Length;
                bool big = size > BugReport.GitHubLimit;
                Show(_toolsStatus, "Saved " + zip + " (" + Math.Max(1, size >> 10) + " KB); the path is on the clipboard." +
                    (big ? " It is over GitHub's 25 MB attachment limit: share it through a file host instead." : ""), big ? Bad : Good);
                try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + zip + "\""); } catch (System.ComponentModel.Win32Exception) { }
                if (MessageBox.Show(this, "Your bug report is on the Desktop:\n" + zip + "\n\nIt holds the game's and your mods' logs, with your Windows user folder name blanked out. Its path is on the clipboard. Open GitHub to describe the problem and attach the file?",
                        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    Launcher.Open(Launcher.IssuesPage);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void OpenFolder(string dir)
        {
            if (dir == null || !Directory.Exists(dir))
            {
                Show(_toolsStatus, "That folder doesn't exist yet" + (dir != null ? ": " + dir : "") + ".", Bad);
                return;
            }
            Launcher.Open(dir);
            Show(_toolsStatus, "", Good);
        }

        /// <summary>A shortcut to the install's launcher copy, made there first if it's missing.</summary>
        void MakeShortcut(string folder)
        {
            string dest = CurrentInstall();
            if (!Launcher.IsInstall(dest)) { Show(_toolsStatus, "Install UCU first.", Bad); return; }
            string problem = Launcher.PlaceCopy(dest);
            if (problem == null)
                problem = Launcher.Shortcut(Path.Combine(folder, "BattleTech UCU.lnk"), Launcher.CopyPath(dest), "", dest, "BattleTech: Unofficial Community Update (UCU) launcher");
            Show(_toolsStatus, problem ?? "Shortcut \"BattleTech UCU\" made in " + folder + ".", problem == null ? Good : Bad);
        }

        // ---- shared ----

        /// <summary>Called by the form's constructor once the Install tab is built.</summary>
        void StartLauncher(string home)
        {
            LoadSettingsIntoTabs();
            string install = home ?? (Launcher.IsInstall(_settings.Install) ? _settings.Install : null);
            if (install != null)
            {
                _destEdited = true;
                _settingDest = true;
                _dest.Text = install;
                _settingDest = false;
                _mods.Checked = Installer.ModsWanted(install);
            }
            _tabs.SelectedTab = install != null ? _homeTab : _installTab;
            Shown += (s, e) =>
            {
                RefreshHome();
                // The window opened on a release (no package beside it, not the launcher copy) reads it anyway; the launcher
                // copy and a local package only peek at the newest tag, when the player wants that.
                if (_settings.CheckOnStart && !_opening && Installer.Remote == null) CheckLatest();
            };
        }

        /// <summary>After an install or update: remember it, and show Home's view of it.</summary>
        void Installed(string dest)
        {
            Remember(dest);
            SetLauncherInputs(true);
            QueueHome();
        }

        void Remember(string dest)
        {
            if (string.Equals(_settings.Install, dest, StringComparison.OrdinalIgnoreCase)) return;
            _settings.Install = dest;
            SaveSettings();
        }

        void SetLauncherInputs(bool on)
        {
            foreach (Button b in _toolButtons) b.Enabled = on;
            _homeUpdate.Enabled = on;
            _play.Enabled = on && Launcher.IsInstall(CurrentInstall());
            if (on) RefreshModsButtons();
            else _packLink.Enabled = _packUnlink.Enabled = _modsOn.Enabled = false;
        }
    }
}
