using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Win32;

namespace ClaudeSwitcher.Core;

/// <summary>
/// Sends claude:// links to the right profile. Windows hands every claude:// link to Claude Desktop without
/// --user-data-dir, so it always lands in the default profile; a sign-in link meant for another profile
/// then leaves the default window stuck on a "use verification code" page. When enabled, the switcher
/// takes over the scheme and forwards sign-in links to the running profile that is still signed out.
/// Claude reads the link from its command line (second-instance), so forwarding is just a launch.
/// </summary>
public static class LinkRouter
{
    public const string OpenUrlArg = "--open-url";
    const string Scheme = "claude";
    const string ProgId = "ClaudeSwitcher.Url";
    const string CapabilitiesKey = @"Software\ClaudeSwitcher\Capabilities";
    const string AppName = "Claude Switcher";

    /// <summary>Claude Desktop's sign-in callbacks (claude://magic-link#…, claude://sso-callback?…).</summary>
    static readonly HashSet<string> SignInRoutes = new(StringComparer.OrdinalIgnoreCase)
        { "magic-link", "sso-callback", "login", "google-auth", "oauth" };

    static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");
    static string Command => $"\"{ExePath}\" {OpenUrlArg} \"%1\"";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{Scheme}\shell\open\command");
            return key?.GetValue(null) is string v && v.Contains(OpenUrlArg) && v.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Registers (or removes) the switcher as a claude:// handler for the current user. Besides the scheme's
    /// own command it registers a ProgID and capabilities, so Windows lists "Claude Switcher" when it asks
    /// which app should open the link.
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        var cu = Registry.CurrentUser;
        if (enabled)
        {
            using (var scheme = cu.CreateSubKey($@"Software\Classes\{Scheme}"))
            {
                if (scheme.GetValue(null) == null) scheme.SetValue(null, "URL:claude");
                scheme.SetValue("URL Protocol", "");
                using var cmd = scheme.CreateSubKey(@"shell\open\command");
                cmd.SetValue(null, Command);
            }
            using (var prog = cu.CreateSubKey($@"Software\Classes\{ProgId}"))
            {
                prog.SetValue(null, "Claude link (Claude Switcher)");
                prog.SetValue("URL Protocol", "");
                using (var icon = prog.CreateSubKey("DefaultIcon")) icon.SetValue(null, $"\"{ExePath}\",0");
                using var cmd = prog.CreateSubKey(@"shell\open\command");
                cmd.SetValue(null, Command);
            }
            using (var caps = cu.CreateSubKey(CapabilitiesKey))
            {
                caps.SetValue("ApplicationName", AppName);
                caps.SetValue("ApplicationDescription", Loc.T("LinkDescription"));
                using var urls = caps.CreateSubKey("URLAssociations");
                urls.SetValue(Scheme, ProgId);
            }
            using (var apps = cu.CreateSubKey(@"Software\RegisteredApplications"))
                apps.SetValue(AppName, CapabilitiesKey);
        }
        else
        {
            // Only remove the scheme's command when it is ours; the claude key itself belongs to Claude.
            if (IsEnabled) cu.DeleteSubKeyTree($@"Software\Classes\{Scheme}\shell", false);
            cu.DeleteSubKeyTree($@"Software\Classes\{ProgId}", false);
            cu.DeleteSubKeyTree(@"Software\ClaudeSwitcher", false);
            using var apps = cu.OpenSubKey(@"Software\RegisteredApplications", true);
            apps?.DeleteValue(AppName, false);
        }
    }

    /// <summary>
    /// Whether Windows' saved choice for claude:// is the switcher. Windows protects that choice with a
    /// hash only it can write, so apps cannot set it themselves; the user picks it in Settings.
    /// </summary>
    public static bool IsWindowsDefault
    {
        get
        {
            // Newer Windows 11 builds keep the choice in UserChoiceLatest\ProgId instead of UserChoice.
            var root = $@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\{Scheme}";
            foreach (var path in new[] { $@"{root}\UserChoiceLatest\ProgId", $@"{root}\UserChoice" })
            {
                using var key = Registry.CurrentUser.OpenSubKey(path);
                if (key?.GetValue("ProgId") is string id) return id.Equals(ProgId, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }
    }

    /// <summary>Opens Settings → Default apps on the switcher's own page, where claude:// can be assigned to it.</summary>
    public static void OpenDefaultAppsSettings() =>
        Process.Start(new ProcessStartInfo($"ms-settings:defaultapps?registeredAppUser={Uri.EscapeDataString(AppName)}")
            { UseShellExecute = true });

    public static bool IsSignInLink(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase))
            return false;
        var firstSegment = uri.AbsolutePath.Trim('/').Split('/')[0];
        return SignInRoutes.Contains(uri.Host) || SignInRoutes.Contains(firstSegment);
    }

    /// <summary>
    /// Picks the profile that should receive the link: for a sign-in link, the most recently started
    /// running profile that has no usable token yet; otherwise (or when there is none) the default profile,
    /// which is where Windows would have sent it.
    /// </summary>
    public static ProfileConfig PickTarget(string url, IReadOnlyList<ProfileConfig> profiles,
        Func<ProfileConfig, DateTime?> runningSince, Func<ProfileConfig, bool> isSignedIn)
    {
        var fallback = profiles.First(p => p.IsDefault);
        if (!IsSignInLink(url)) return fallback;
        return profiles
            .Select(p => (Profile: p, Since: runningSince(p)))
            .Where(x => x.Since != null && !isSignedIn(x.Profile))
            .OrderByDescending(x => x.Since)
            .Select(x => x.Profile)
            .FirstOrDefault() ?? fallback;
    }

    public static void Open(string url, IReadOnlyList<ProfileConfig> profiles)
    {
        var running = InstanceManager.RunningInstances();
        var target = PickTarget(url, profiles, p => StartTime(running, p), IsSignedIn);
        Log($"{Describe(url)} -> {target.Name}");
        InstanceManager.Launch(target, url);
    }

    /// <summary>The link without its query or fragment, which carry sign-in secrets.</summary>
    public static string Describe(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath.TrimEnd('/')}" : Loc.T("BadLink");

    public static string LogPath => System.IO.Path.Combine(ProfileStore.Root, "links.log");

    /// <summary>Keeps a short history of where links went, so "it opened the wrong window" can be checked.</summary>
    public static void Log(string line)
    {
        try
        {
            System.IO.Directory.CreateDirectory(ProfileStore.Root);
            var lines = System.IO.File.Exists(LogPath) ? System.IO.File.ReadAllLines(LogPath).TakeLast(199) : Enumerable.Empty<string>();
            System.IO.File.WriteAllLines(LogPath, lines.Append($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}"));
        }
        catch (Exception) { }
    }

    static DateTime? StartTime(Dictionary<string, int> running, ProfileConfig profile)
    {
        if (!running.TryGetValue(InstanceManager.Normalize(profile.ResolveDataDir()), out var pid)) return null;
        try { using var p = Process.GetProcessById(pid); return p.StartTime; }
        catch (Exception) { return DateTime.MinValue; }
    }

    static bool IsSignedIn(ProfileConfig profile)
    {
        try { return DesktopSecrets.ReadCredentials(profile.ResolveDataDir()).Any(c => c.ExpiresAt > DateTimeOffset.UtcNow); }
        catch (Exception) { return false; }
    }
}
