using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClaudeSwitcher.Core;
using Forms = System.Windows.Forms;

namespace ClaudeSwitcher.UI;

/// <summary>Small borderless panel shown on a left click of the tray icon: one click opens a profile.</summary>
public sealed class TrayPopup : Window
{
    const double Gap = 8;

    readonly StackPanel _rows = new();
    readonly Action<ProfileViewModel> _launch;
    readonly Action _openMain;
    DateTime _closedAt;

    public TrayPopup(Action<ProfileViewModel> launch, Action openMain)
    {
        _launch = launch;
        _openMain = openMain;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = 300;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        SetResourceReference(ForegroundProperty, "Ink");

        var title = new TextBlock { Text = Loc.T("PopupTitle"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 2, 0, 8) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        var open = new Button { Content = Loc.T("TrayOpenApp"), Style = (Style)Application.Current.FindResource("Small"), HorizontalAlignment = HorizontalAlignment.Stretch };
        open.Click += (_, _) => { Hide(); _openMain(); };

        var panel = new StackPanel();
        panel.Children.Add(title);
        panel.Children.Add(new ScrollViewer { Content = _rows, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var rule = new Border { Height = 1, Margin = new Thickness(4, 8, 4, 8) };
        rule.SetResourceReference(Border.BackgroundProperty, "Border");
        panel.Children.Add(rule);
        panel.Children.Add(open);

        var frame = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            Margin = new Thickness(Gap),
            Child = panel,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.25 },
        };
        frame.SetResourceReference(Border.BackgroundProperty, "Card");
        frame.SetResourceReference(Border.BorderBrushProperty, "Border");
        Content = frame;

        Deactivated += (_, _) => Hide();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) _closedAt = DateTime.Now; };
    }

    /// <summary>Opens beside the tray, or closes when it is already open.</summary>
    public void Toggle(IEnumerable<ProfileViewModel> profiles)
    {
        // Clicking the icon while open first deactivates (hides) the popup; don't reopen it on the same click.
        if (IsVisible || (DateTime.Now - _closedAt).TotalMilliseconds < 300) { Hide(); return; }
        BuildRows(profiles);
        Opacity = 0;
        Show();
        UpdateLayout();
        PlaceNearCursor();
        Opacity = 1;
        Activate();
    }

    void BuildRows(IEnumerable<ProfileViewModel> profiles)
    {
        _rows.Children.Clear();
        foreach (var p in profiles) _rows.Children.Add(Row(p));
    }

    UIElement Row(ProfileViewModel p)
    {
        var dot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = p.ColorBrush, VerticalAlignment = VerticalAlignment.Center };

        var name = new TextBlock { Text = p.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var account = new TextBlock { Text = p.Account, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        account.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var text = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(name);
        text.Children.Add(account);

        var usage = UsageColumn(p);
        var running = new Border
        {
            Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Visibility = p.IsRunning ? Visibility.Visible : Visibility.Hidden,
            ToolTip = Loc.T("Running"),
        };
        running.SetResourceReference(Border.BackgroundProperty, "Ok");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(usage, 2);
        Grid.SetColumn(running, 3);
        grid.Children.Add(dot);
        grid.Children.Add(text);
        grid.Children.Add(usage);
        grid.Children.Add(running);

        var row = new Border
        {
            Child = grid,
            Padding = new Thickness(10, 7, 10, 7),
            CornerRadius = new CornerRadius(7),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = p.IsRunning ? Loc.T("PopupFocus", p.Name) : Loc.T("PopupOpen", p.Name),
        };
        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "Hover");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => { Hide(); _launch(p); };
        return row;
    }

    /// <summary>
    /// 5-hour percent with its reset time underneath. When the weekly limit is used up it takes over the
    /// column, since that is the wait that actually matters.
    /// </summary>
    static UIElement UsageColumn(ProfileViewModel p)
    {
        var five = p.Usage?.FiveHour;
        var week = p.Usage?.Weekly;
        var weekFull = week is { Percent: >= 100 };
        var shown = weekFull ? week : five;

        var column = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MinWidth = 44 };
        if (shown == null) return column;

        var percent = new TextBlock
        {
            Text = weekFull ? Loc.T("PopupWeek", $"{shown.Percent:0}%") : $"{shown.Percent:0}%",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        percent.SetResourceReference(TextBlock.ForegroundProperty, shown.Percent >= 90 ? "Danger" : shown.Percent >= 70 ? "Warn" : "Muted");
        column.Children.Add(percent);

        // A window that hasn't started (no reset time) or whose reset already passed shows no time.
        if (shown.ResetsAt is { } at && at > DateTimeOffset.Now)
        {
            var reset = new TextBlock { Text = "↻ " + ShortTime(at), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
            reset.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            column.Children.Add(reset);
            var when = Loc.T("ResetAt", UsageBar.FormatReset(at));
            column.ToolTip = weekFull ? Loc.T("PopupWeekTip", when) : Loc.T("PopupResetTip", when);
        }
        else column.ToolTip = weekFull ? Loc.T("WeekAll") : Loc.T("PopupUsageTip");
        return column;
    }

    static string ShortTime(DateTimeOffset t)
    {
        var local = t.LocalDateTime;
        return local - DateTime.Now < TimeSpan.FromHours(20) ? local.ToString("HH:mm") : local.ToString(@"dd\/MM");
    }

    /// <summary>Sits in the corner of the work area closest to the cursor, i.e. next to the taskbar's tray.</summary>
    void PlaceNearCursor()
    {
        var cursor = Forms.Cursor.Position;
        var area = Forms.Screen.FromPoint(cursor).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        double w = ActualWidth * dpi.DpiScaleX, h = ActualHeight * dpi.DpiScaleY;
        var x = Math.Clamp(cursor.X - w / 2, area.Left, area.Right - w);
        var y = Math.Clamp(cursor.Y - h, area.Top, area.Bottom - h);
        Left = x / dpi.DpiScaleX;
        Top = y / dpi.DpiScaleY;
    }
}
