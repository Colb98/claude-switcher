using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ClaudeSwitcher.Core;
using Microsoft.Win32;

namespace ClaudeSwitcher.UI;

/// <summary>Light/dark palette swapping. XAML references every theme brush with DynamicResource.</summary>
public static class Theme
{
    public const string System = "System";
    public const string Light = "Light";
    public const string Dark = "Dark";

    static readonly Dictionary<string, string> LightPalette = new()
    {
        ["Bg"] = "#F5F3EE", ["Card"] = "#FFFFFF", ["CardAlt"] = "#FCFBF8", ["Input"] = "#FFFFFF",
        ["Hover"] = "#F0EDE6", ["Selection"] = "#F6E3DA", ["Border"] = "#E4E0D6", ["Ink"] = "#22201C",
        ["Muted"] = "#6F6A60", ["Accent"] = "#C9653F", ["OnAccent"] = "#FFFFFF", ["Track"] = "#EDEAE2",
        ["Thumb"] = "#C9C4B8", ["Ok"] = "#4C8C5A", ["Warn"] = "#B9791C", ["Danger"] = "#B8453A",
    };

    static readonly Dictionary<string, string> DarkPalette = new()
    {
        ["Bg"] = "#1C1B19", ["Card"] = "#262522", ["CardAlt"] = "#2B2A27", ["Input"] = "#2F2E2B",
        ["Hover"] = "#3A3834", ["Selection"] = "#4A3329", ["Border"] = "#3B3935", ["Ink"] = "#ECE9E2",
        ["Muted"] = "#A29D92", ["Accent"] = "#D97757", ["OnAccent"] = "#FFFFFF", ["Track"] = "#3A3834",
        ["Thumb"] = "#5A5751", ["Ok"] = "#6FB27E", ["Warn"] = "#D9A441", ["Danger"] = "#E06A5C",
    };

    public static bool IsDark { get; private set; }
    public static event Action? Changed;

    public static void Apply(string mode)
    {
        IsDark = mode.ToLowerInvariant() switch
        {
            "dark" => true,
            "light" => false,
            _ => SystemPrefersDark(),
        };
        var palette = IsDark ? DarkPalette : LightPalette;
        var resources = Application.Current.Resources;
        foreach (var (key, hex) in palette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            resources[key] = brush;
        }
        foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);
        Changed?.Invoke();
    }

    /// <summary>Sets the UI language and publishes every string as resource "T.&lt;key&gt;" for XAML.</summary>
    public static void ApplyLanguage(string language)
    {
        Loc.SetLanguage(language);
        var resources = Application.Current.Resources;
        foreach (var (key, _) in Loc.All) resources["T." + key] = Loc.T(key);
    }

    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>Windows 10 20H1+ / 11: ask DWM for a dark caption to match the content.</summary>
    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            window.SourceInitialized -= OnSourceInitialized;
            window.SourceInitialized += OnSourceInitialized;
            return;
        }
        var on = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref on, sizeof(int));
    }

    static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window w) ApplyTitleBar(w);
    }

    static bool SystemPrefersDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }

    const int DwmUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
