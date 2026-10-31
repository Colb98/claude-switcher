using System;
using System.IO;
using System.Linq;

namespace ClaudeSwitcher.Core;

/// <summary>Locates the installed Claude Desktop launcher and its default data folder.</summary>
public static class ClaudeInstall
{
    static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>
    /// The MSIX build is started through its app execution alias, which runs inside the package
    /// identity and forwards arguments. The legacy Squirrel build keeps a plain exe.
    /// </summary>
    public static string? FindLauncher()
    {
        var alias = Path.Combine(LocalAppData, "Microsoft", "WindowsApps", "claude-desktop.exe");
        if (File.Exists(alias)) return alias;
        var legacy = Path.Combine(LocalAppData, "AnthropicClaude", "claude.exe");
        if (File.Exists(legacy)) return legacy;
        return null;
    }

    public static string FindDefaultDataDir()
    {
        var root = Path.Combine(LocalAppData, "Claude-Data");
        if (Directory.Exists(root))
        {
            var packaged = Directory.GetDirectories(root, "claude_*")
                .Where(d => File.Exists(Path.Combine(d, "Local State")))
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (packaged != null) return packaged;
        }
        return Path.Combine(RoamingAppData, "Claude");
    }
}
