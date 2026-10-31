using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClaudeSwitcher.Core;
using ClaudeSwitcher.UI;
using Forms = System.Windows.Forms;

namespace ClaudeSwitcher;

public partial class MainWindow : Window
{
    static readonly int[] RefreshChoices = { 60, 120, 300, 600 };
    static readonly int[] WarnChoices = { 70, 80, 90, 95 };
    static readonly (string Mode, string LabelKey)[] ThemeChoices =
        { (Theme.System, "ThemeSystem"), (Theme.Light, "ThemeLight"), (Theme.Dark, "ThemeDark") };
    // Each language is named in itself, so it can be found whichever one is active.
    static readonly (string Code, string Label)[] LanguageChoices =
        { (Loc.English, "English"), (Loc.Vietnamese, "Tiếng Việt") };

    public AppSettings Settings { get; }
    readonly ObservableCollection<ProfileViewModel> _profiles = new();
    readonly ObservableCollection<McpRow> _mcpRows = new();
    readonly UsageService _usage = new();
    readonly DispatcherTimer _usageTimer = new();
    readonly DispatcherTimer _processTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    readonly HashSet<string> _warned = new();
    readonly Forms.NotifyIcon _tray;
    readonly TrayPopup _trayPopup;
    readonly string? _screenshotPath;
    bool _loadingSettings;
    bool _exiting;
    bool _refreshing;

    public MainWindow(bool fromStartup, string? screenshotPath, string? themeOverride = null, string? languageOverride = null)
    {
        Settings = ProfileStore.Load();
        Theme.Apply(themeOverride ?? Settings.Theme);
        Theme.ApplyLanguage(languageOverride ?? Settings.Language);
        InitializeComponent();
        // The user may have just picked the default app in Settings; refresh the hint when they come back.
        Activated += (_, _) => UpdateLinkHint();
        Theme.ApplyTitleBar(this);
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == Microsoft.Win32.UserPreferenceCategory.General && Settings.Theme == Theme.System)
                Dispatcher.BeginInvoke(() => Theme.Apply(Theme.System));
        };
        _screenshotPath = screenshotPath;
        if (screenshotPath != null)
        {
            // Render off-screen without activating, so capture never steals the user's focus.
            ShowActivated = false;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -5000;
            Top = 0;
        }
        ProfileStore.Save(Settings);

        ProfileList.ItemsSource = _profiles;
        McpList.ItemsSource = _mcpRows;
        RebuildProfiles();
        LoadSettingsUi();

        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            Text = "Claude Switcher",
            Visible = true,
        };
        _trayPopup = new TrayPopup(LaunchProfile, ShowFromTray);
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button != Forms.MouseButtons.Left) return;
            RefreshRunning();
            _trayPopup.Toggle(_profiles);
        };
        _tray.BalloonTipClicked += (_, _) => ShowFromTray();
        RebuildTrayMenu();

        _usageTimer.Tick += async (_, _) => await RefreshUsageAsync();
        _usageTimer.Interval = TimeSpan.FromSeconds(Math.Max(30, Settings.UsageRefreshSeconds));
        _usageTimer.Start();
        _processTimer.Tick += (_, _) => RefreshRunning();
        _processTimer.Start();

        Loaded += async (_, _) =>
        {
            RefreshRunning();
            await RefreshUsageAsync();
            if (_screenshotPath != null) await CaptureAndExit(_screenshotPath);
        };
        if (fromStartup) AutoStartProfiles();
        else if (_screenshotPath != null) Show();
    }

    // ---------- Profiles ----------

    void RebuildProfiles()
    {
        var previous = _profiles.ToDictionary(p => p.Id);
        _profiles.Clear();
        foreach (var cfg in Settings.Profiles)
        {
            var vm = new ProfileViewModel(cfg);
            if (previous.TryGetValue(cfg.Id, out var old) && old.Usage != null) vm.SetUsage(old.Usage);
            _profiles.Add(vm);
        }
        var src = (SourceCombo.SelectedItem as ProfileViewModel)?.Id;
        var dst = (TargetCombo.SelectedItem as ProfileViewModel)?.Id;
        SourceCombo.ItemsSource = _profiles.ToList();
        TargetCombo.ItemsSource = _profiles.ToList();
        SourceCombo.SelectedItem = _profiles.FirstOrDefault(p => p.Id == src) ?? _profiles.FirstOrDefault();
        TargetCombo.SelectedItem = _profiles.FirstOrDefault(p => p.Id == dst) ?? _profiles.Skip(1).FirstOrDefault();
        RefreshRunning();
        ReloadMcp();
        if (_tray != null) RebuildTrayMenu();
    }

    void RefreshRunning()
    {
        var running = InstanceManager.RunningInstances();
        foreach (var p in _profiles)
            p.IsRunning = running.ContainsKey(InstanceManager.Normalize(p.DataDir));
    }

    void AutoStartProfiles()
    {
        foreach (var p in Settings.Profiles.Where(p => p.AutoStart))
        {
            if (InstanceManager.FindMainProcess(p) != null) continue;
            try { InstanceManager.Launch(p); }
            catch (Exception e) { SetStatus(Loc.T("OpenFailed", p.Name, e.Message)); }
        }
    }

    static ProfileViewModel Vm(object sender) => (ProfileViewModel)((FrameworkElement)sender).Tag;

    void Launch_Click(object sender, RoutedEventArgs e) => LaunchProfile(Vm(sender));

    void LaunchProfile(ProfileViewModel p)
    {
        try
        {
            var running = InstanceManager.FindMainProcess(p.Config) != null;
            InstanceManager.Launch(p.Config);
            SetStatus(Loc.T(running ? "Focusing" : "Opening", p.Name));
        }
        catch (Exception ex) { Error(ex.Message); }
    }

    void Stop_Click(object sender, RoutedEventArgs e) => StopProfile(Vm(sender), ask: true);

    bool StopProfile(ProfileViewModel p, bool ask)
    {
        if (InstanceManager.FindMainProcess(p.Config) == null) { SetStatus(Loc.T("NotRunning", p.Name)); return true; }
        if (ask && MessageBox.Show(this, Loc.T("StopConfirm", p.Name),
                Loc.T("StopTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return false;
        InstanceManager.Stop(p.Config);
        RefreshRunning();
        SetStatus(Loc.T("StoppedProfile", p.Name));
        return true;
    }

    void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ProfileDialog(null) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var cfg = ProfileStore.CreateProfile(Settings, dlg.ProfileName, dlg.ProfileColor);
        cfg.AutoStart = dlg.AutoStart;
        ProfileStore.Save(Settings);
        RebuildProfiles();
        if (MessageBox.Show(this, Loc.T("CreatedOpenNow", cfg.Name), Loc.T("NewProfileTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            LaunchProfile(_profiles.First(p => p.Id == cfg.Id));
    }

    void Edit_Click(object sender, RoutedEventArgs e)
    {
        var p = Vm(sender);
        var dlg = new ProfileDialog(p.Config) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        p.Config.Name = dlg.ProfileName;
        p.Config.Color = dlg.ProfileColor;
        p.Config.AutoStart = dlg.AutoStart;
        ProfileStore.Save(Settings);
        RebuildProfiles();
    }

    void More_Click(object sender, RoutedEventArgs e)
    {
        var p = Vm(sender);
        var menu = new ContextMenu();
        void Add(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => { try { action(); } catch (Exception ex) { Error(ex.Message); } };
            menu.Items.Add(item);
        }
        Add(Loc.T("MenuOpenData"), () => OpenFolder(p.DataDir));
        Add(Loc.T("MenuShortcut"), () => SetStatus(Loc.T("ShortcutCreated", StartupManager.CreateProfileShortcut(p.Config))));
        Add(Loc.T("MenuTransferFrom"), () => { SourceCombo.SelectedItem = p; Tabs.SelectedIndex = 1; });
        menu.Items.Add(new Separator());
        Add(Loc.T("MenuDelete"), () => DeleteProfile(p), enabled: !p.IsDefault);
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
    }

    void DeleteProfile(ProfileViewModel p)
    {
        var answer = MessageBox.Show(this, Loc.T("DeleteConfirm", p.Name),
            Loc.T("DeleteTitle"), MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Cancel) return;
        if (!StopProfile(p, ask: false)) return;
        Settings.Profiles.Remove(p.Config);
        ProfileStore.Save(Settings);
        if (answer == MessageBoxResult.Yes && p.Config.DataDir is { } dir && Directory.Exists(dir))
        {
            try { Directory.Delete(dir, true); }
            catch (IOException ex) { Error(Loc.T("DeleteFolderFailed", ex.Message)); }
        }
        RebuildProfiles();
    }

    // ---------- Usage ----------

    async void RefreshUsage_Click(object sender, RoutedEventArgs e) => await RefreshUsageAsync();

    async Task RefreshUsageAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            SetStatus(Loc.T("FetchingUsage"));
            var tasks = _profiles.ToList().Select(async p => (p, report: await Task.Run(() => _usage.FetchAsync(p.DataDir))));
            foreach (var (p, report) in await Task.WhenAll(tasks)) p.SetUsage(report);
            SetStatus(Loc.T("UsageUpdated", DateTime.Now.ToString("HH:mm:ss")));
            UpdateTray();
            WarnIfNeeded();
        }
        finally { _refreshing = false; }
    }

    void WarnIfNeeded()
    {
        foreach (var p in _profiles)
        {
            var five = p.FiveHourPercent;
            if (five < Settings.WarnFiveHourPercent) { _warned.Remove(p.Id); continue; }
            if (!p.IsRunning || !_warned.Add(p.Id)) continue;
            var best = _profiles.Where(o => o != p && o.Usage?.Status == UsageStatus.Ok && o.FiveHourPercent >= 0)
                .OrderBy(o => o.FiveHourPercent).FirstOrDefault();
            var tip = best != null && best.FiveHourPercent < five
                ? Loc.T("WarnSuggest", best.Name, best.FiveHourPercent)
                : Loc.T("WarnNoBetter");
            _tray.ShowBalloonTip(8000, Loc.T("WarnTitle", p.Name, five), tip, Forms.ToolTipIcon.Warning);
        }
    }

    // ---------- Session transfer ----------

    void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadSessions();
    void ShowArchived_Changed(object sender, RoutedEventArgs e) => LoadSessions();

    void LoadSessions()
    {
        if (SourceCombo.SelectedItem is not ProfileViewModel src) { SessionList.ItemsSource = null; return; }
        var all = SessionTransfer.List(src.DataDir);
        SessionList.ItemsSource = ShowArchived.IsChecked == true ? all : all.Where(s => !s.IsArchived).ToList();
    }

    void Transfer_Click(object sender, RoutedEventArgs e)
    {
        if (SourceCombo.SelectedItem is not ProfileViewModel src || TargetCombo.SelectedItem is not ProfileViewModel dst) return;
        if (src.Id == dst.Id) { Error(Loc.T("SameProfile")); return; }
        var chosen = (SessionList.ItemsSource as IEnumerable<CodeSession>)?.Where(s => s.Selected).ToList() ?? new();
        if (chosen.Count == 0) { Error(Loc.T("PickSession")); return; }
        var move = MoveSessions.IsChecked == true;
        // A running Desktop keeps its sessions in memory and would write them back, so both ends must be closed.
        var mustClose = new[] { dst }.Concat(move ? new[] { src } : Array.Empty<ProfileViewModel>())
            .Where(p => InstanceManager.FindMainProcess(p.Config) != null).ToList();
        if (mustClose.Count > 0)
        {
            var names = string.Join(", ", mustClose.Select(p => $"\"{p.Name}\""));
            if (MessageBox.Show(this, Loc.T("TransferMustClose", names),
                    Loc.T("TransferTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            foreach (var p in mustClose) InstanceManager.Stop(p.Config);
            RefreshRunning();
        }
        try
        {
            var n = SessionTransfer.CopyTo(chosen, dst.DataDir, OverwriteSessions.IsChecked == true);
            var skipped = chosen.Count - n;
            var msg = Loc.T("Copied", n, dst.Name) + (skipped > 0 ? Loc.T("Skipped", skipped) : "");
            if (move)
            {
                SessionTransfer.RemoveFromSource(chosen);
                msg += Loc.T("RemovedFromSource", src.Name);
                LoadSessions();
            }
            if (MessageBox.Show(this, msg + Loc.T("OpenTargetNow"), Loc.T("TransferTitle"),
                    MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                LaunchProfile(dst);
            SetStatus(msg);
        }
        catch (Exception ex) { Error(ex.Message); }
    }

    // ---------- MCP ----------

    void ReloadMcp()
    {
        _mcpRows.Clear();
        foreach (var s in McpSync.Collect(Settings.Profiles))
            _mcpRows.Add(new McpRow(s, Settings.Profiles));
    }

    void McpReload_Click(object sender, RoutedEventArgs e) { ReloadMcp(); SetStatus(Loc.T("McpReloaded")); }

    void McpAllOn_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _mcpRows) r.SetAll(true);
    }

    void McpRemoveAll_Click(object sender, RoutedEventArgs e) => ((McpRow)((FrameworkElement)sender).Tag).SetAll(false);

    void McpAddJson_Click(object sender, RoutedEventArgs e)
    {
        var text = InputDialog.Ask(this, Loc.T("McpAddJsonTitle"), Loc.T("McpAddJsonPrompt"),
            "{\n  \"my-server\": {\n    \"command\": \"npx\",\n    \"args\": [\"-y\", \"package-name\"]\n  }\n}", multiline: true);
        if (text == null) return;
        try
        {
            var node = JsonNode.Parse(text) as JsonObject ?? throw new JsonException(Loc.T("NeedJsonObject"));
            if (node["mcpServers"] is JsonObject inner) node = inner;
            foreach (var (name, def) in node.ToList())
            {
                if (def is not JsonObject d) continue;
                AddMcp(name, (JsonObject)d.DeepClone());
            }
        }
        catch (JsonException ex) { Error(Loc.T("BadJson", ex.Message)); }
    }

    void McpAddUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = InputDialog.Ask(this, Loc.T("McpAddUrl"), Loc.T("McpAddUrlPrompt"), "https://");
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return;
        var name = InputDialog.Ask(this, Loc.T("ConnectorNameTitle"), Loc.T("ConnectorNamePrompt"), uri.Host.Split('.').FirstOrDefault(x => x != "mcp" && x != "www") ?? uri.Host);
        if (string.IsNullOrWhiteSpace(name)) return;
        AddMcp(name.Trim(), McpSync.RemoteConnector(uri.ToString()));
    }

    void AddMcp(string name, JsonObject def)
    {
        var existing = _mcpRows.FirstOrDefault(r => r.Name == name);
        if (existing != null) { existing.Server.Definition = def; existing.SetAll(true); return; }
        var server = new SharedMcpServer { Name = name, Definition = def };
        foreach (var p in Settings.Profiles) server.EnabledFor[p.Id] = true;
        _mcpRows.Add(new McpRow(server, Settings.Profiles));
        SetStatus(Loc.T("McpAdded", name));
    }

    void McpApply_Click(object sender, RoutedEventArgs e)
    {
        var servers = _mcpRows.Select(r => r.Server).ToList();
        var changed = McpSync.ProfilesNeedingChange(Settings.Profiles, servers);
        if (changed.Count == 0) { SetStatus(Loc.T("McpNoChange")); return; }

        // A running Desktop rewrites claude_desktop_config.json from memory and would drop the change,
        // so running profiles are closed, written, then reopened.
        var running = _profiles.Where(p => changed.Contains(p.Config) && InstanceManager.FindMainProcess(p.Config) != null).ToList();
        if (running.Count > 0 &&
            MessageBox.Show(this, Loc.T("McpRestart", string.Join(", ", running.Select(p => $"\"{p.Name}\""))),
                Loc.T("TabMcp"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        try
        {
            foreach (var p in running) InstanceManager.Stop(p.Config);
            var backup = McpSync.Apply(changed, servers);
            foreach (var p in running) InstanceManager.Launch(p.Config);
            RefreshRunning();
            SetStatus(Loc.T("McpApplied", string.Join(", ", changed.Select(p => p.Name)), backup));
            ReloadMcp();
        }
        catch (Exception ex) { Error(ex.Message); }
    }

    // ---------- Settings ----------

    void LoadSettingsUi()
    {
        _loadingSettings = true;
        LanguageCombo.ItemsSource = LanguageChoices.Select(l => l.Label).ToList();
        LanguageCombo.SelectedIndex = Math.Max(0, Array.FindIndex(LanguageChoices, l => l.Code == Loc.Language));
        ThemeCombo.ItemsSource = ThemeChoices.Select(t => Loc.T(t.LabelKey)).ToList();
        ThemeCombo.SelectedIndex = Math.Max(0, Array.FindIndex(ThemeChoices, t => t.Mode == Settings.Theme));
        StartWithWindows.IsChecked = StartupManager.IsEnabled;
        StartMinimized.IsChecked = Settings.StartMinimized;
        RouteLinks.IsChecked = LinkRouter.IsEnabled;
        UpdateLinkHint();
        RefreshCombo.ItemsSource = RefreshChoices.Select(s => s < 120 ? Loc.T("Seconds", s) : Loc.T("Minutes", s / 60)).ToList();
        RefreshCombo.SelectedIndex = Math.Max(0, Array.IndexOf(RefreshChoices, Settings.UsageRefreshSeconds));
        WarnCombo.ItemsSource = WarnChoices.Select(v => $"{v}%").ToList();
        WarnCombo.SelectedIndex = Math.Max(0, Array.IndexOf(WarnChoices, Settings.WarnFiveHourPercent));
        LauncherInfo.Text = Loc.T("LauncherInfo", ClaudeInstall.FindLauncher() ?? Loc.T("NotFound"), ClaudeInstall.FindDefaultDataDir(), ProfileStore.Root);
        _loadingSettings = false;
    }

    void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        try { StartupManager.SetEnabled(StartWithWindows.IsChecked == true); SetStatus(Loc.T("StartupSaved")); }
        catch (Exception ex) { Error(ex.Message); }
    }

    void RouteLinks_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        try
        {
            var enabled = RouteLinks.IsChecked == true;
            LinkRouter.SetEnabled(enabled);
            SetStatus(Loc.T("LinksSaved"));
            if (enabled && !LinkRouter.IsWindowsDefault && MessageBox.Show(this, Loc.T("LinksDefaultApp"),
                    Loc.T("RouteLinks"), MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK)
                LinkRouter.OpenDefaultAppsSettings();
            UpdateLinkHint();
        }
        catch (Exception ex) { Error(ex.Message); }
    }

    void SetDefaultApp_Click(object sender, RoutedEventArgs e) => LinkRouter.OpenDefaultAppsSettings();

    /// <summary>Shows the "set in Windows" button while the option is on but Windows still opens links in Claude.</summary>
    void UpdateLinkHint() =>
        SetDefaultApp.Visibility = RouteLinks.IsChecked == true && !LinkRouter.IsWindowsDefault ? Visibility.Visible : Visibility.Collapsed;

    void Theme_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || ThemeCombo.SelectedIndex < 0) return;
        Settings.Theme = ThemeChoices[ThemeCombo.SelectedIndex].Mode;
        Theme.Apply(Settings.Theme);
        ProfileStore.Save(Settings);
    }

    async void Language_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || LanguageCombo.SelectedIndex < 0) return;
        Settings.Language = LanguageChoices[LanguageCombo.SelectedIndex].Code;
        ProfileStore.Save(Settings);
        // XAML follows the T.* resources; strings built in code are rebuilt here.
        Theme.ApplyLanguage(Settings.Language);
        LoadSettingsUi();
        RebuildProfiles();
        LoadSessions();
        SetStatus("");
        await RefreshUsageAsync();
    }

    void Settings_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        Settings.StartMinimized = StartMinimized.IsChecked == true;
        if (RefreshCombo.SelectedIndex >= 0) Settings.UsageRefreshSeconds = RefreshChoices[RefreshCombo.SelectedIndex];
        if (WarnCombo.SelectedIndex >= 0) Settings.WarnFiveHourPercent = WarnChoices[WarnCombo.SelectedIndex];
        _usageTimer.Interval = TimeSpan.FromSeconds(Settings.UsageRefreshSeconds);
        ProfileStore.Save(Settings);
    }

    void OpenAppData_Click(object sender, RoutedEventArgs e) => OpenFolder(ProfileStore.Root);
    void OpenBackups_Click(object sender, RoutedEventArgs e) => OpenFolder(ProfileStore.BackupsRoot);

    // ---------- Tray ----------

    void RebuildTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Loc.T("TrayOpenApp"), null, (_, _) => ShowFromTray());
        menu.Items.Add(new Forms.ToolStripSeparator());
        foreach (var p in _profiles)
        {
            var item = new Forms.ToolStripMenuItem(p.Name);
            item.DropDownItems.Add(Loc.T("Open"), null, (_, _) => LaunchProfile(p));
            item.DropDownItems.Add(Loc.T("Stop"), null, (_, _) => StopProfile(p, ask: true));
            menu.Items.Add(item);
        }
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Loc.T("RefreshUsage"), null, async (_, _) => await RefreshUsageAsync());
        menu.Items.Add(Loc.T("TrayExit"), null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
    }

    void UpdateTray()
    {
        var text = string.Join("\n", _profiles.Select(p => p.TrayLine));
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            Hide();
            _tray.ShowBalloonTip(3000, Loc.T("TrayStillRunning"), Loc.T("TrayReopen"), Forms.ToolTipIcon.Info);
            return;
        }
        base.OnClosing(e);
    }

    void ExitApp()
    {
        _exiting = true;
        _tray.Visible = false;
        _tray.Dispose();
        Close();
        Application.Current.Shutdown();
    }

    // ---------- Helpers ----------

    void SetStatus(string text) => StatusText.Text = text;
    void Error(string text) => MessageBox.Show(this, text, "Claude Switcher", MessageBoxButton.OK, MessageBoxImage.Warning);

    static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    /// <summary>Renders each tab to PNG for UI review without taking focus, then exits.</summary>
    async Task CaptureAndExit(string path)
    {
        for (var i = 0; i < Tabs.Items.Count; i++)
        {
            Tabs.SelectedIndex = i;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(300);
            var root = (FrameworkElement)Content;
            var w = root.ActualWidth + root.Margin.Left + root.Margin.Right;
            var h = root.ActualHeight + root.Margin.Top + root.Margin.Bottom;
            var bmp = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen()) dc.DrawRectangle((Brush)FindResource("Bg"), null, new Rect(0, 0, w, h));
            bmp.Render(bg);
            bmp.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            var file = i == 0 ? path : Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}-{i}.png");
            await using var fs = File.Create(file);
            enc.Save(fs);
        }
        ExitApp();
    }
}
