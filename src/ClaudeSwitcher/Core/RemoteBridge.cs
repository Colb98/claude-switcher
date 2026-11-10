using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace ClaudeSwitcher.Core;

/// <summary>
/// Runs Claude Remote's bridge (remote/bridge, a Node server) while Claude Switcher runs, so the phone web app
/// is up whenever the PC is. The bridge listens on 127.0.0.1 only; Tailscale serves it to the phone.
/// Restarts it when it stops, and stops it (with the Claude Code processes it started) on exit.
/// </summary>
public sealed class RemoteBridge : IDisposable
{
    public const int DefaultPort = 7878;
    readonly object _lock = new();
    Process? _process;
    bool _wanted;
    int _quickFailures;
    DateTime _startedAt;

    public event Action<string>? StatusChanged;
    public string Status { get; private set; } = "";
    public string? Script { get; private set; }

    static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude-remote", "bridge.log");

    /// <summary>remote\bridge\dist\server.js beside the exe, or above it (the repo's dist\ folder).</summary>
    public static string? FindScript(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var exeDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(exeDir, "remote", "bridge", "dist", "server.js"),
            Path.Combine(exeDir, "..", "remote", "bridge", "dist", "server.js"),
        };
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    public static string? FindNode()
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"));
        return dirs.Select(d => Path.Combine(d.Trim('"'), "node.exe")).FirstOrDefault(File.Exists);
    }

    static bool PortInUse(int port)
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync("127.0.0.1", port).Wait(300) && c.Connected;
        }
        catch { return false; }
    }

    public void Start(string? configuredScript)
    {
        lock (_lock)
        {
            _wanted = true;
            _quickFailures = 0;
            if (_process is { HasExited: false }) return;
            Script = FindScript(configuredScript);
            Launch();
        }
    }

    void Launch()
    {
        if (!_wanted) return;
        var node = FindNode();
        if (Script == null) { SetStatus(Loc.T("RemoteNoScript")); return; }
        if (node == null) { SetStatus(Loc.T("RemoteNoNode")); return; }
        if (PortInUse(DefaultPort)) { SetStatus(Loc.T("RemoteExternal", DefaultPort)); return; }

        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 5 * 1024 * 1024) File.Move(LogPath, LogPath + ".old", true);

        var psi = new ProcessStartInfo(node, $"\"{Script}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(Script)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Use this exe for --api, and keep this app's own Claude settings out of the bridge.
        psi.Environment["CLAUDE_SWITCHER_EXE"] = Environment.ProcessPath ?? "";
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("CLAUDE_CODE", StringComparison.OrdinalIgnoreCase) || k.StartsWith("ANTHROPIC", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(key);

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var log = new StreamWriter(LogPath, append: true) { AutoFlush = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (log) log.WriteLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) log.WriteLine(e.Data); };
        p.Exited += (_, _) => { lock (log) log.Dispose(); OnExited(p); };
        try
        {
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }
        catch (Exception e) { SetStatus(e.Message); log.Dispose(); return; }
        _process = p;
        _startedAt = DateTime.UtcNow;
        SetStatus(Loc.T("RemoteRunning", DefaultPort));
    }

    void OnExited(Process p)
    {
        lock (_lock)
        {
            if (_process != p) return;
            _process = null;
            if (!_wanted) { SetStatus(Loc.T("RemoteStopped")); return; }
            _quickFailures = DateTime.UtcNow - _startedAt < TimeSpan.FromSeconds(20) ? _quickFailures + 1 : 0;
            if (_quickFailures >= 4) { SetStatus(Loc.T("RemoteCrashed", LogPath)); return; }
            SetStatus(Loc.T("RemoteRestarting"));
        }
        var delay = TimeSpan.FromSeconds(Math.Min(60, 2 << _quickFailures));
        Task.Delay(delay).ContinueWith(_ => { lock (_lock) { if (_wanted && _process == null) Launch(); } });
    }

    public void Stop()
    {
        lock (_lock)
        {
            _wanted = false;
            var p = _process;
            _process = null;
            if (p is { HasExited: false })
            {
                try { p.Kill(entireProcessTree: true); p.WaitForExit(3000); } catch { /* already gone */ }
            }
            SetStatus(Loc.T("RemoteStopped"));
        }
    }

    void SetStatus(string s)
    {
        Status = s;
        StatusChanged?.Invoke(s);
    }

    public void Dispose() => Stop();
}
