using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeSwitcher.Core;

/// <summary>Starts, detects and stops Claude Desktop instances per data folder.</summary>
public static class InstanceManager
{
    static readonly Regex UserDataDirArg = new("--user-data-dir=(\"[^\"]+\"|\\S+)", RegexOptions.IgnoreCase);

    public static void Launch(ProfileConfig profile)
    {
        var launcher = ClaudeInstall.FindLauncher()
            ?? throw new InvalidOperationException("Không tìm thấy Claude Desktop. Hãy cài Claude Desktop trước.");
        var psi = new ProcessStartInfo(launcher) { UseShellExecute = true };
        if (!profile.IsDefault)
        {
            Directory.CreateDirectory(profile.DataDir!);
            psi.Arguments = $"--user-data-dir=\"{profile.DataDir}\"";
        }
        Process.Start(psi);
    }

    /// <summary>
    /// Maps each normalised data folder to the main process of the instance using it. Chromium
    /// passes --user-data-dir to every child process, so a child's parent is that instance's main process.
    /// </summary>
    public static Dictionary<string, int> RunningInstances()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, CommandLine FROM Win32_Process WHERE Name='claude.exe'");
            foreach (ManagementObject p in searcher.Get())
            {
                var cmd = p["CommandLine"] as string;
                if (cmd == null || !cmd.Contains("--type=")) continue;
                var m = UserDataDirArg.Match(cmd);
                if (!m.Success) continue;
                var dir = Normalize(m.Groups[1].Value.Trim('"'));
                var parent = Convert.ToInt32(p["ParentProcessId"]);
                if (!result.ContainsKey(dir) && IsAlive(parent)) result[dir] = parent;
            }
        }
        catch (ManagementException) { }
        return result;
    }

    public static int? FindMainProcess(ProfileConfig profile) =>
        RunningInstances().TryGetValue(Normalize(profile.ResolveDataDir()), out var pid) ? pid : null;

    public static bool Stop(ProfileConfig profile)
    {
        var pid = FindMainProcess(profile);
        if (pid == null) return false;
        try
        {
            using var main = Process.GetProcessById(pid.Value);
            main.Kill(entireProcessTree: true);
            main.WaitForExit(5000);
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    /// <summary>
    /// Shows and focuses a running profile's main window. Claude hides its window when closed to the tray, and
    /// with several instances its tray icons and taskbar button do not reliably reach the right one, so the
    /// window is found by process: the largest top-level Chromium window (the small one is the quick-entry box).
    /// </summary>
    public static bool BringToFront(ProfileConfig profile)
    {
        if (FindMainProcess(profile) is not { } pid) return false;
        var best = IntPtr.Zero;
        long bestArea = 0;
        var cls = new StringBuilder(64);
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != pid || GetWindowTextLength(hwnd) == 0) return true;
            cls.Clear();
            GetClassName(hwnd, cls, cls.Capacity);
            // Use the restored-size rectangle: a minimized window's live rect is a tiny caption off-screen.
            var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
            if (cls.ToString() != "Chrome_WidgetWin_1" || !GetWindowPlacement(hwnd, ref placement)) return true;
            var r = placement.NormalPosition;
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = hwnd; }
            return true;
        }, IntPtr.Zero);
        if (best == IntPtr.Zero) return false;

        ShowWindow(best, IsIconic(best) ? SwRestore : SwShow);
        SetForegroundWindow(best);
        return true;
    }

    public static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    static bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }

    const int SwShow = 5;
    const int SwRestore = 9;

    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct WindowPlacement
    {
        public int Length, Flags, ShowCmd;
        public int MinX, MinY, MaxX, MaxY;
        public Rect NormalPosition;
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
}
