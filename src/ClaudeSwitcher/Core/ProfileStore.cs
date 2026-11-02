using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeSwitcher.Core;

public sealed class ProfileConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "Profile";
    public string Color { get; set; } = "#D97757";
    /// <summary>Null for the original Claude Desktop data folder, which launches without --user-data-dir.</summary>
    public string? DataDir { get; set; }
    public bool AutoStart { get; set; }

    [JsonIgnore] public bool IsDefault => DataDir == null;
    public string ResolveDataDir() => DataDir ?? ClaudeInstall.FindDefaultDataDir();
}

public sealed class AppSettings
{
    public List<ProfileConfig> Profiles { get; set; } = new();
    public bool StartMinimized { get; set; } = true;
    public bool RouteNotifications { get; set; } = true;
    public int UsageRefreshSeconds { get; set; } = 120;
    public int WarnFiveHourPercent { get; set; } = 90;
    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";
    /// <summary>"en" or "vi".</summary>
    public string Language { get; set; } = Loc.English;
}

/// <summary>
/// Persists profiles and settings under %USERPROFILE%\.claude-switcher. This must stay outside AppData:
/// the MSIX Claude Desktop has file-system write virtualization, so files it creates under AppData land in
/// its private Packages\...\LocalCache copy, where this app would never see the profile's sign-in.
/// </summary>
public static class ProfileStore
{
    /// <summary>CLAUDE_SWITCHER_HOME overrides the location (used by tests so they never touch real data).</summary>
    public static string Root =>
        Environment.GetEnvironmentVariable("CLAUDE_SWITCHER_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude-switcher");
    public static string ProfilesRoot => Path.Combine(Root, "profiles");
    public static string BackupsRoot => Path.Combine(Root, "backups");
    static string SettingsPath => Path.Combine(Root, "settings.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Json) ?? new AppSettings()
                : new AppSettings();
        }
        catch (JsonException)
        {
            File.Copy(SettingsPath, SettingsPath + ".broken", true);
            settings = new AppSettings();
        }
        if (!settings.Profiles.Any(p => p.IsDefault))
            settings.Profiles.Insert(0, new ProfileConfig { Id = "default", Name = Loc.T("DefaultProfile"), Color = "#D97757" });
        return settings;
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Root);
        var tmp = SettingsPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, SettingsPath, true);
    }

    public static ProfileConfig CreateProfile(AppSettings settings, string name, string color)
    {
        var profile = new ProfileConfig { Name = name, Color = color };
        profile.DataDir = Path.Combine(ProfilesRoot, profile.Id);
        Directory.CreateDirectory(profile.DataDir);
        settings.Profiles.Add(profile);
        Save(settings);
        return profile;
    }

    /// <summary>Copies files into a timestamped backup folder before Claude data is modified.</summary>
    public static string Backup(string label, IEnumerable<string> files)
    {
        var dir = Path.Combine(BackupsRoot, $"{DateTime.Now:yyyyMMdd-HHmmss}-{label}");
        Directory.CreateDirectory(dir);
        var i = 0;
        foreach (var file in files.Where(File.Exists))
            File.Copy(file, Path.Combine(dir, $"{i++:D2}-{Path.GetFileName(Path.GetDirectoryName(file))}-{Path.GetFileName(file)}"), true);
        return dir;
    }
}
