using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;

namespace ClaudeSwitcher.Core;

/// <summary>Starts, detects and stops Claude Desktop instances per data folder.</summary>
public static class InstanceManager
{
    static readonly Regex UserDataDirArg = new("--user-data-dir=(\"[^\"]+\"|\\S+)", RegexOptions.IgnoreCase);

    /// <summary>
    /// Starts the profile, or brings its window back when it is already running: the new process loses the
    /// single-instance lock for that data folder and hands over to the running instance, whose own
    /// second-instance handler restores, shows and focuses the window. Showing the window natively instead
    /// (ShowWindow) leaves Electron believing it is hidden, so it stops repainting and looks frozen.
    /// </summary>
    public static void Launch(ProfileConfig profile, string? url = null)
    {
        var launcher = ClaudeInstall.FindLauncher()
            ?? throw new InvalidOperationException("Không tìm thấy Claude Desktop. Hãy cài Claude Desktop trước.");
        var args = new List<string>();
        if (!profile.IsDefault)
        {
            Directory.CreateDirectory(profile.DataDir!);
            args.Add($"--user-data-dir=\"{profile.DataDir}\"");
        }
        // A claude:// link is passed the way Windows passes it; a running instance gets it through second-instance.
        if (url != null) args.Add($"\"{url.Replace("\"", "")}\"");
        Process.Start(new ProcessStartInfo(launcher, string.Join(" ", args)) { UseShellExecute = true });
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

    public static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    static bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }
}
