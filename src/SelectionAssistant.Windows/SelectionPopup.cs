using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SelectionAssistant.Windows;

internal sealed class SelectionButton : Window
{
    private readonly DispatcherTimer expiry = new() { Interval = TimeSpan.FromSeconds(8) };
    internal event Action? OpenRequested;
    internal SelectionButton()
    {
        Title = "翻译选中文字 · 拾句";
        Width = 96; Height = 40;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        var button = new Button { Content = "译为英文 ↗", ToolTip = "翻译当前选中的文字", Style = (Style)Application.Current.FindResource("PrimaryButton"), Padding = new Thickness(10, 8, 10, 8) };
        button.Click += (_, _) => { expiry.Stop(); Hide(); OpenRequested?.Invoke(); };
        Content = button;
        SourceInitialized += (_, _) => Native.NoActivate(this);
        expiry.Tick += (_, _) => { expiry.Stop(); Hide(); };
    }
    internal void Present(double x, double y)
    {
        Show(); Native.PlaceNear(this, x, y);
        expiry.Stop(); expiry.Start();
    }
    internal void Dismiss() { expiry.Stop(); Hide(); }
}

internal sealed class TranslationPopup : Window
{
    private bool pinned;
    internal bool Pinned => pinned;
    internal TranslationPopup(App owner)
    {
        Title = "划词翻译 · 拾句";
        Width = 470; Height = 740; MinWidth = 400; MinHeight = 520;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        WindowStyle = WindowStyle.ToolWindow; ResizeMode = ResizeMode.CanResizeWithGrip;
        var grid = new Grid { Margin = new Thickness(16, 12, 8, 14) };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new());
        var top = new DockPanel { Margin = new Thickness(0, 0, 8, 12) };
        var heading = new TextBlock { Text = "随选 · 随学", FontSize = 17, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var main = new Button { Content = "主窗口", Padding = new Thickness(10, 6, 10, 6), Background = Brushes.Transparent };
        main.Click += (_, _) => owner.ShowMain();
        var pin = new Button { Content = "固定", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(4, 0, 0, 0) };
        pin.Click += (_, _) => { pinned = !pinned; pin.Content = pinned ? "取消固定" : "固定"; };
        actions.Children.Add(main); actions.Children.Add(pin);
        top.Children.Add(heading); top.Children.Add(actions);
        grid.Children.Add(top);
        var view = new TranslateView { DataContext = owner.Session };
        Grid.SetRow(view, 1); grid.Children.Add(view);
        Content = grid;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && !pinned) { Hide(); e.Handled = true; } };
        Closing += (_, e) => { if (!owner.Exiting) { e.Cancel = true; Hide(); } };
        Deactivated += (_, _) => { if (!pinned && !owner.SettingsOpen) Hide(); };
    }
    internal void Present(double x, double y)
    {
        Show(); Native.PlaceNear(this, x, y);
    }
}
