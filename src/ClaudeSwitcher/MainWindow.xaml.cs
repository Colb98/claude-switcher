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

    public AppSettings Settings { get; }
    readonly ObservableCollection<ProfileViewModel> _profiles = new();
    readonly ObservableCollection<McpRow> _mcpRows = new();
    readonly UsageService _usage = new();
    readonly DispatcherTimer _usageTimer = new();
    readonly DispatcherTimer _processTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    readonly HashSet<string> _warned = new();
    readonly Forms.NotifyIcon _tray;
    readonly string? _screenshotPath;
    bool _loadingSettings;
    bool _exiting;
    bool _refreshing;

    public MainWindow(bool fromStartup, string? screenshotPath)
    {
        InitializeComponent();
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
        Settings = ProfileStore.Load();
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
        _tray.DoubleClick += (_, _) => ShowFromTray();
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
            catch (Exception e) { SetStatus($"Không mở được {p.Name}: {e.Message}"); }
        }
    }

    static ProfileViewModel Vm(object sender) => (ProfileViewModel)((FrameworkElement)sender).Tag;

    void Launch_Click(object sender, RoutedEventArgs e) => LaunchProfile(Vm(sender));

    void LaunchProfile(ProfileViewModel p)
    {
        try
        {
            if (InstanceManager.FindMainProcess(p.Config) != null)
            {
                SetStatus($"{p.Name} đang chạy. Bấm vào biểu tượng Claude trên taskbar để chuyển tới cửa sổ đó.");
                return;
            }
            InstanceManager.Launch(p.Config);
            SetStatus($"Đang mở {p.Name}…");
        }
        catch (Exception ex) { Error(ex.Message); }
    }

    void Stop_Click(object sender, RoutedEventArgs e) => StopProfile(Vm(sender), ask: true);

    bool StopProfile(ProfileViewModel p, bool ask)
    {
        if (InstanceManager.FindMainProcess(p.Config) == null) { SetStatus($"{p.Name} không chạy."); return true; }
        if (ask && MessageBox.Show(this, $"Tắt Claude Desktop của profile \"{p.Name}\"?\nCông việc đang chạy trong cửa sổ đó sẽ dừng.",
                "Tắt profile", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return false;
        InstanceManager.Stop(p.Config);
        RefreshRunning();
        SetStatus($"Đã tắt {p.Name}.");
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
        if (MessageBox.Show(this, $"Đã tạo profile \"{cfg.Name}\". Mở ngay để đăng nhập tài khoản?", "Profile mới",
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
        Add("Mở thư mục dữ liệu", () => OpenFolder(p.DataDir));
        Add("Tạo shortcut trên Desktop", () => SetStatus("Đã tạo " + StartupManager.CreateProfileShortcut(p.Config)));
        Add("Chuyển session từ profile này…", () => { SourceCombo.SelectedItem = p; Tabs.SelectedIndex = 1; });
        menu.Items.Add(new Separator());
        Add("Xoá profile…", () => DeleteProfile(p), enabled: !p.IsDefault);
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
    }

    void DeleteProfile(ProfileViewModel p)
    {
        var answer = MessageBox.Show(this,
            $"Xoá profile \"{p.Name}\"?\n\nYes: xoá profile và toàn bộ dữ liệu đăng nhập của nó.\nNo: chỉ gỡ khỏi danh sách, giữ thư mục dữ liệu.",
            "Xoá profile", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Cancel) return;
        if (!StopProfile(p, ask: false)) return;
        Settings.Profiles.Remove(p.Config);
        ProfileStore.Save(Settings);
        if (answer == MessageBoxResult.Yes && p.Config.DataDir is { } dir && Directory.Exists(dir))
        {
            try { Directory.Delete(dir, true); }
            catch (IOException ex) { Error("Chưa xoá được thư mục dữ liệu (còn tiến trình đang dùng): " + ex.Message); }
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
            SetStatus("Đang lấy usage…");
            var tasks = _profiles.ToList().Select(async p => (p, report: await Task.Run(() => _usage.FetchAsync(p.DataDir))));
            foreach (var (p, report) in await Task.WhenAll(tasks)) p.SetUsage(report);
            SetStatus($"Usage cập nhật lúc {DateTime.Now:HH:mm:ss}.");
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
                ? $"Gợi ý: chuyển session sang \"{best.Name}\" (5 giờ: {best.FiveHourPercent:0}%)."
                : "Chưa có profile nào còn nhiều usage hơn.";
            _tray.ShowBalloonTip(8000, $"{p.Name} đã dùng {five:0}% usage 5 giờ", tip, Forms.ToolTipIcon.Warning);
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
        if (src.Id == dst.Id) { Error("Profile nguồn và đích phải khác nhau."); return; }
        var chosen = (SessionList.ItemsSource as IEnumerable<CodeSession>)?.Where(s => s.Selected).ToList() ?? new();
        if (chosen.Count == 0) { Error("Hãy tick ít nhất một session."); return; }
        if (InstanceManager.FindMainProcess(dst.Config) != null)
        {
            if (MessageBox.Show(this, $"Profile đích \"{dst.Name}\" đang chạy. Phải tắt nó để chép session.\nTắt ngay?",
                    "Chuyển session", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            InstanceManager.Stop(dst.Config);
        }
        try
        {
            var n = SessionTransfer.CopyTo(chosen, dst.DataDir, OverwriteSessions.IsChecked == true);
            var skipped = chosen.Count - n;
            var msg = $"Đã chép {n} session sang \"{dst.Name}\"." + (skipped > 0 ? $" Bỏ qua {skipped} session đã có sẵn." : "");
            if (MessageBox.Show(this, msg + "\n\nMở profile đích ngay để làm tiếp?", "Chuyển session",
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

    void McpReload_Click(object sender, RoutedEventArgs e) { ReloadMcp(); SetStatus("Đã đọc lại cấu hình MCP."); }

    void McpAllOn_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _mcpRows) r.SetAll(true);
    }

    void McpRemoveAll_Click(object sender, RoutedEventArgs e) => ((McpRow)((FrameworkElement)sender).Tag).SetAll(false);

    void McpAddJson_Click(object sender, RoutedEventArgs e)
    {
        var text = InputDialog.Ask(this, "Thêm MCP server",
            "Dán JSON dạng {\"tên\": {\"command\": \"...\", \"args\": [...]}} hoặc cả khối \"mcpServers\":",
            "{\n  \"my-server\": {\n    \"command\": \"npx\",\n    \"args\": [\"-y\", \"package-name\"]\n  }\n}", multiline: true);
        if (text == null) return;
        try
        {
            var node = JsonNode.Parse(text) as JsonObject ?? throw new JsonException("Cần một object JSON.");
            if (node["mcpServers"] is JsonObject inner) node = inner;
            foreach (var (name, def) in node.ToList())
            {
                if (def is not JsonObject d) continue;
                AddMcp(name, (JsonObject)d.DeepClone());
            }
        }
        catch (JsonException ex) { Error("JSON không hợp lệ: " + ex.Message); }
    }

    void McpAddUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = InputDialog.Ask(this, "Thêm connector bằng URL", "URL của MCP server từ xa (ví dụ https://mcp.example.com/sse):", "https://");
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return;
        var name = InputDialog.Ask(this, "Tên connector", "Đặt tên cho connector:", uri.Host.Split('.').FirstOrDefault(x => x != "mcp" && x != "www") ?? uri.Host);
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
        SetStatus($"Đã thêm \"{name}\" cho mọi profile. Bấm Áp dụng để ghi.");
    }

    void McpApply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var backup = McpSync.Apply(Settings.Profiles, _mcpRows.Select(r => r.Server).ToList());
            var running = _profiles.Where(p => p.IsRunning).Select(p => p.Name).ToList();
            SetStatus("Đã áp dụng MCP. Backup: " + backup);
            MessageBox.Show(this, "Đã ghi cấu hình MCP cho mọi profile." +
                (running.Count > 0 ? $"\n\nKhởi động lại các profile đang chạy để nhận thay đổi: {string.Join(", ", running)}." : ""),
                "MCP dùng chung", MessageBoxButton.OK, MessageBoxImage.Information);
            ReloadMcp();
        }
        catch (Exception ex) { Error(ex.Message); }
    }

    // ---------- Settings ----------

    void LoadSettingsUi()
    {
        _loadingSettings = true;
        StartWithWindows.IsChecked = StartupManager.IsEnabled;
        StartMinimized.IsChecked = Settings.StartMinimized;
        RefreshCombo.ItemsSource = RefreshChoices.Select(s => s < 120 ? $"{s} giây" : $"{s / 60} phút").ToList();
        RefreshCombo.SelectedIndex = Math.Max(0, Array.IndexOf(RefreshChoices, Settings.UsageRefreshSeconds));
        WarnCombo.ItemsSource = WarnChoices.Select(v => $"{v}%").ToList();
        WarnCombo.SelectedIndex = Math.Max(0, Array.IndexOf(WarnChoices, Settings.WarnFiveHourPercent));
        LauncherInfo.Text = $"Claude Desktop: {ClaudeInstall.FindLauncher() ?? "không tìm thấy"}\nDữ liệu profile mặc định: {ClaudeInstall.FindDefaultDataDir()}\nDữ liệu app: {ProfileStore.Root}";
        _loadingSettings = false;
    }

    void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        try { StartupManager.SetEnabled(StartWithWindows.IsChecked == true); SetStatus("Đã lưu cài đặt khởi động."); }
        catch (Exception ex) { Error(ex.Message); }
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
        menu.Items.Add("Mở Claude Switcher", null, (_, _) => ShowFromTray());
        menu.Items.Add(new Forms.ToolStripSeparator());
        foreach (var p in _profiles)
        {
            var item = new Forms.ToolStripMenuItem(p.Name);
            item.DropDownItems.Add("Mở", null, (_, _) => LaunchProfile(p));
            item.DropDownItems.Add("Tắt", null, (_, _) => StopProfile(p, ask: true));
            menu.Items.Add(item);
        }
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Làm mới usage", null, async (_, _) => await RefreshUsageAsync());
        menu.Items.Add("Thoát", null, (_, _) => ExitApp());
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
            _tray.ShowBalloonTip(3000, "Claude Switcher vẫn chạy", "Mở lại từ biểu tượng ở khay hệ thống.", Forms.ToolTipIcon.Info);
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
