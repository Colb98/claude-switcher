using System;
using System.IO;
using Microsoft.Win32;

namespace ClaudeSwitcher.Core;

/// <summary>Registers the app to start with Windows for the current user (no admin rights needed).</summary>
public static class StartupManager
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "ClaudeSwitcher";
    public const string StartupArg = "--startup";

    static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string v && v.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{ExePath}\" {StartupArg}");
        else key.DeleteValue(ValueName, false);
    }

    /// <summary>Creates a desktop shortcut that opens one profile directly.</summary>
    public static string CreateProfileShortcut(ProfileConfig profile)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var safeName = string.Join("_", profile.Name.Split(Path.GetInvalidFileNameChars()));
        var path = Path.Combine(desktop, $"Claude - {safeName}.lnk");
        var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        var link = shell.CreateShortcut(path);
        link.TargetPath = ExePath;
        link.Arguments = $"--launch {profile.Id}";
        link.IconLocation = ExePath + ",0";
        link.Description = Loc.T("ShortcutDescription", profile.Name);
        link.Save();
        return path;
    }
}
