using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudeSwitcher.Core;

namespace ClaudeSwitcher.UI;

/// <summary>Create or edit a profile's name, colour and start-up behaviour.</summary>
public sealed class ProfileDialog : Window
{
    static readonly string[] Palette = { "#D97757", "#4C7BD9", "#4C8C5A", "#9A5BC9", "#C98A2B", "#2E9AA6", "#C94F7C", "#6F6A60" };

    readonly TextBox _name = new() { FontSize = 14, Padding = new Thickness(6, 4, 6, 4) };
    readonly CheckBox _autoStart = new() { Content = "Tự mở khi khởi động cùng Windows", Margin = new Thickness(0, 14, 0, 0) };
    readonly WrapPanel _swatches = new() { Margin = new Thickness(0, 6, 0, 0) };

    public string ProfileName => _name.Text.Trim();
    public string ProfileColor { get; private set; }
    public bool AutoStart => _autoStart.IsChecked == true;

    public ProfileDialog(ProfileConfig? existing)
    {
        Title = existing == null ? "Profile mới" : "Sửa profile";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Ink");
        Theme.ApplyTitleBar(this);
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        _name.Text = existing?.Name ?? "";
        _autoStart.IsChecked = existing?.AutoStart ?? false;
        ProfileColor = existing?.Color ?? Palette[1];
        BuildSwatches();

        var ok = new Button { Content = existing == null ? "Tạo" : "Lưu", IsDefault = true, Style = (Style)Application.Current.FindResource("Primary"), Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) =>
        {
            if (ProfileName.Length == 0) { MessageBox.Show(this, "Hãy nhập tên profile."); return; }
            DialogResult = true;
        };
        var cancel = new Button { Content = "Huỷ", IsCancel = true };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Tên profile" });
        panel.Children.Add(_name);
        panel.Children.Add(new TextBlock { Text = "Màu", Margin = new Thickness(0, 14, 0, 0) });
        panel.Children.Add(_swatches);
        panel.Children.Add(_autoStart);
        if (existing == null)
            panel.Children.Add(new TextBlock
            {
                Text = "Profile mới có thư mục dữ liệu riêng. Lần đầu mở, hãy đăng nhập tài khoản Claude muốn dùng cho profile này.",
                TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.FindResource("Muted"), Margin = new Thickness(0, 12, 0, 0)
            });
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => _name.Focus();
    }

    void BuildSwatches()
    {
        _swatches.Children.Clear();
        foreach (var hex in Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var selected = hex == ProfileColor;
            var swatch = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(color), Cursor = System.Windows.Input.Cursors.Hand,
                BorderBrush = selected ? Brushes.Black : Brushes.Transparent, BorderThickness = new Thickness(selected ? 3 : 0),
            };
            swatch.MouseLeftButtonUp += (_, _) => { ProfileColor = hex; BuildSwatches(); };
            _swatches.Children.Add(swatch);
        }
    }
}

/// <summary>Minimal text prompt.</summary>
public sealed class InputDialog : Window
{
    readonly TextBox _box;

    InputDialog(string title, string prompt, string initial, bool multiline)
    {
        Title = title;
        Width = multiline ? 520 : 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Ink");
        Theme.ApplyTitleBar(this);
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        _box = new TextBox
        {
            Text = initial, Padding = new Thickness(6, 4, 6, 4), AcceptsReturn = multiline,
            Height = multiline ? 180 : double.NaN, FontFamily = multiline ? new FontFamily("Consolas") : FontFamily,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
        };
        var ok = new Button { Content = "OK", IsDefault = !multiline, Style = (Style)Application.Current.FindResource("Primary"), Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Huỷ", IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(_box);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public static string? Ask(Window owner, string title, string prompt, string initial = "", bool multiline = false)
    {
        var dlg = new InputDialog(title, prompt, initial, multiline) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg._box.Text : null;
    }
}
