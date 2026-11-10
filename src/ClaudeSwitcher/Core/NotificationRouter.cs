using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeSwitcher.Core;

/// <summary>
/// Brings the right profile forward when a Claude notification is clicked. Every profile shares one Windows
/// app identity, so a click also makes Windows start Claude without --user-data-dir, and that hands over to
/// the default profile, which takes the focus. The profile that owns the notification does get the click and
/// opens the chat, but behind the default window. The click carries nothing the switcher can read, so it is
/// recognised afterwards: the toast host takes the foreground, then the owning profile reacts. Its log records
/// a session change if the chat was not already open, and its taskbar button flashes when Windows refuses its
/// own attempt to take the focus. A click in Notification Center never reaches the owning profile (the toast's
/// handler is gone by then), so it shows neither sign and is left alone.
/// </summary>
public sealed class NotificationRouter : IDisposable
{
    const string ToastHostProcess = "ShellExperienceHost";
    const uint EventSystemForeground = 3;
    const uint WinEventOutOfContext = 0;
    const uint WinEventSkipOwnProcess = 2;
    const int WatchMs = 2500;
    const int PollMs = 100;
    const int FocusMs = 2000;
    const int FocusPollMs = 150;
    const int SteadyPolls = 3;
    const int MaxLogTail = 64 * 1024;
    const int BlankAreaDip = 320;
    const uint WmLButtonDown = 0x0201;
    const uint WmLButtonUp = 0x0202;
    const int MkLButton = 1;

    static readonly Regex SessionFocused = new(@"LocalSessions\.setFocusedSession: sessionId=(?!null\b)", RegexOptions.Compiled);

    readonly Func<AppSettings> _settings;
    readonly WinEventProc _callback;
    readonly IntPtr _hook;
    int _generation;
    int _routedGeneration;
    long _toastAt = long.MinValue / 2;

    /// <summary>Call on a thread with a message loop: Windows delivers the foreground events to it.</summary>
    public NotificationRouter(Func<AppSettings> settings)
    {
        _settings = settings;
        _callback = OnForegroundChanged;
        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _callback, 0, 0,
            WinEventOutOfContext | WinEventSkipOwnProcess);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
    }

    void OnForegroundChanged(IntPtr hook, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time)
    {
        var settings = _settings();
        if (!settings.RouteNotifications || !IsToastHost(hwnd)) return;
        _toastAt = Environment.TickCount64;
        var profiles = OtherProfiles(settings);
        var generation = Interlocked.Increment(ref _generation);
        Task.Run(() => Watch(profiles, generation));
    }

    /// <summary>Told by the UI when a window's taskbar button starts flashing. Only counts right after a toast.</summary>
    public void OnWindowFlashed(IntPtr window)
    {
        var settings = _settings();
        if (!settings.RouteNotifications || Environment.TickCount64 - _toastAt > WatchMs) return;
        var profiles = OtherProfiles(settings);
        var generation = _generation;
        GetWindowThreadProcessId(window, out var pid);
        Task.Run(() =>
        {
            try
            {
                if (ProfileForProcess((int)pid, profiles, InstanceManager.RunningInstances()) is { } target)
                    Route(target, generation, "flash");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        });
    }

    static List<ProfileConfig> OtherProfiles(AppSettings settings) => settings.Profiles.Where(p => !p.IsDefault).ToList();

    /// <summary>The profile whose instance runs as this process, among those given.</summary>
    public static ProfileConfig? ProfileForProcess(int pid, IEnumerable<ProfileConfig> profiles, IReadOnlyDictionary<string, int> running) =>
        profiles.FirstOrDefault(p => running.TryGetValue(InstanceManager.Normalize(p.ResolveDataDir()), out var main) && main == pid);

    /// <summary>Both signs can appear for one click, and only the first one acts.</summary>
    void Route(ProfileConfig target, int generation, string sign)
    {
        if (Interlocked.Exchange(ref _routedGeneration, generation) == generation) return;
        LinkRouter.Log($"notification click ({sign}) -> {target.Name}");
        Focus(target);
    }

    static bool IsToastHost(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName.Equals(ToastHostProcess, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    void Watch(List<ProfileConfig> profiles, int generation)
    {
        try
        {
            var offsets = profiles.ToDictionary(p => p, p => LogLength(LogPath(p)));
            var deadline = Environment.TickCount64 + WatchMs;
            while (Environment.TickCount64 < deadline && generation == Volatile.Read(ref _generation))
            {
                var target = profiles.FirstOrDefault(p => SessionFocusedSince(LogPath(p), offsets[p]));
                if (target != null)
                {
                    Route(target, generation, "log");
                    return;
                }
                Thread.Sleep(PollMs);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    static string LogPath(ProfileConfig profile) => Path.Combine(profile.DataDir!, "logs", "main.log");

    /// <summary>The length read from an open handle; the directory entry of a file being written can lag behind.</summary>
    public static long LogLength(string path)
    {
        if (!File.Exists(path)) return 0;
        using var stream = OpenShared(path);
        return stream.Length;
    }

    /// <summary>Whether text added after <paramref name="offset"/> records a chat being opened.</summary>
    public static bool SessionFocusedSince(string path, long offset)
    {
        if (!File.Exists(path)) return false;
        using var stream = OpenShared(path);
        var length = stream.Length;
        if (length == offset) return false;
        // A smaller file means the log was rotated, so all of it is new.
        var start = length < offset ? 0 : offset;
        stream.Position = Math.Max(start, length - MaxLogTail);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return SessionFocused.IsMatch(reader.ReadToEnd());
    }

    static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>
    /// Keeps the profile's window in front until it stays there, since the default window takes the focus at
    /// about the same moment. A minimized window goes through Launch, because Electron must restore it itself.
    /// </summary>
    static void Focus(ProfileConfig profile)
    {
        if (InstanceManager.FindMainProcess(profile) is not { } pid) return;
        var launched = false;
        var steady = 0;
        var deadline = Environment.TickCount64 + FocusMs;
        while (Environment.TickCount64 < deadline && steady < SteadyPolls)
        {
            Thread.Sleep(FocusPollMs);
            var window = MainWindowOf(pid);
            if (window == IntPtr.Zero || IsIconic(window))
            {
                if (!launched) InstanceManager.Launch(profile);
                launched = true;
                continue;
            }
            if (GetForegroundWindow() == window) { steady++; continue; }
            steady = 0;
            ForceForeground(window);
        }
        if (steady >= SteadyPolls) ClickBlankArea(MainWindowOf(pid));
    }

    /// <summary>
    /// After a toast click the page stops showing updates until it is clicked, even once it has the focus. This
    /// posts a click to the margin left of the chat, past the sidebar. The real mouse pointer does not move.
    /// </summary>
    static void ClickBlankArea(IntPtr window)
    {
        if (window == IntPtr.Zero || !GetClientRect(window, out var client)) return;
        var x = Math.Min(BlankAreaDip * (int)GetDpiForWindow(window) / 96, client.Right / 2);
        var y = client.Bottom / 2;
        var position = (IntPtr)((y << 16) | (x & 0xFFFF));
        PostMessage(window, WmLButtonDown, (IntPtr)MkLButton, position);
        PostMessage(window, WmLButtonUp, IntPtr.Zero, position);
    }

    static IntPtr MainWindowOf(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.MainWindowHandle; }
        catch (ArgumentException) { return IntPtr.Zero; }
    }

    /// <summary>
    /// Windows only lets the process that received the last input take the foreground (a refused call just
    /// flashes the taskbar button), so an empty mouse input comes first. It moves and presses nothing.
    /// </summary>
    static void ForceForeground(IntPtr window)
    {
        SendInput(1, new Input[1], InputSize);
        SetForegroundWindow(window);
    }

    /// <summary>SendInput rejects every input unless this is the exact size of the native INPUT.</summary>
    public static int InputSize => Marshal.SizeOf<Input>();

    /// <summary>INPUT with its mouse member, the union's largest. Nesting it gives the padding the native struct has.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Input
    {
        public uint Type;
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MouseInput
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time);

    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
