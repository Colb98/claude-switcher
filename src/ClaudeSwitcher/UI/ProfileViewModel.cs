using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using ClaudeSwitcher.Core;

namespace ClaudeSwitcher.UI;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

public sealed class UsageBar
{
    public UsageBar(UsageWindow w)
    {
        Label = w.Label;
        Percent = Math.Clamp(w.Percent, 0, 100);
        Value = $"{w.Percent:0}%";
        Reset = w.ResetsAt is { } t ? Loc.T("ResetAt", FormatReset(t)) : "";
        Brush = Percent >= 90 ? Danger : Percent >= 70 ? Warn : Ok;
    }

    public string Label { get; }
    public double Percent { get; }
    public string Value { get; }
    public string Reset { get; }
    public Brush Brush { get; }

    static readonly Brush Ok = Freeze("#4C8C5A");
    static readonly Brush Warn = Freeze("#C98A2B");
    static readonly Brush Danger = Freeze("#B8453A");

    static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    static string FormatReset(DateTimeOffset t)
    {
        var local = t.LocalDateTime;
        var left = t - DateTimeOffset.Now;
        var rel = left.TotalMinutes < 1 ? Loc.T("ResetSoon") : left.TotalHours < 1 ? Loc.T("ResetMinutes", left.Minutes)
            : left.TotalDays < 1 ? Loc.T("ResetHours", (int)left.TotalHours, left.Minutes) : Loc.T("ResetDays", (int)left.TotalDays);
        var when = local.Date == DateTime.Today ? local.ToString("HH:mm") : local.ToString("HH:mm dd/MM");
        return $"{when} ({rel})";
    }
}

public sealed class ProfileViewModel : Observable
{
    public ProfileViewModel(ProfileConfig config) => Config = config;

    public ProfileConfig Config { get; }
    public string Id => Config.Id;
    /// <summary>The Default profile follows the UI language until the user renames it.</summary>
    public string Name => Config.IsDefault && Config.Name is "Default" or "Mặc định" ? Loc.T("DefaultProfile") : Config.Name;
    public bool IsDefault => Config.IsDefault;
    public string DataDir => Config.ResolveDataDir();
    public Brush ColorBrush
    {
        get
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(Config.Color)); }
            catch (FormatException) { return Brushes.Gray; }
        }
    }

    bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        set { if (_isRunning == value) return; _isRunning = value; Raise(); Raise(nameof(RunState)); Raise(nameof(RunBrush)); }
    }
    public string RunState => IsRunning ? Loc.T("Running") : Loc.T("Stopped");
    public Brush RunBrush => IsRunning ? Brushes.SeaGreen : Brushes.Gray;

    public UsageReport? Usage { get; private set; }
    public string Account => Usage?.Email ?? (Usage?.Status == UsageStatus.SignedOut ? Loc.T("SignedOut") : "…");
    public string Plan => Usage?.PlanName ?? "";
    public string UsageMessage => Usage switch
    {
        { Status: UsageStatus.SignedOut } => Loc.T("SignInHint"),
        { Status: not UsageStatus.Ok } u => u.Message ?? "",
        _ => "",
    };
    public bool HasUsageMessage => UsageMessage.Length > 0;
    public string FetchedText => Usage == null ? "" : Loc.T("Updated", Usage.FetchedAt.LocalDateTime.ToString("HH:mm:ss"));
    public IReadOnlyList<UsageBar> Bars { get; private set; } = Array.Empty<UsageBar>();
    public double FiveHourPercent => Usage?.FiveHour?.Percent ?? -1;

    public string TrayLine
    {
        get
        {
            var five = Usage?.FiveHour is { } f ? $"{f.Percent:0}%" : "?";
            var week = Usage?.Weekly is { } w ? $"{w.Percent:0}%" : "?";
            return Loc.T("TrayLine", Name, five, week);
        }
    }

    public void SetUsage(UsageReport report)
    {
        // Keep the last good numbers visible when a refresh fails transiently.
        if (report.Status == UsageStatus.Failed && Usage?.Status == UsageStatus.Ok)
            report = Usage with { Message = report.Message };
        Usage = report;
        Bars = new[] { report.FiveHour, report.Weekly }.Concat(report.ScopedWeekly)
            .Where(w => w != null).Select(w => new UsageBar(w!)).ToList();
        RaiseAll();
    }

    public void Refresh() => RaiseAll();
}
