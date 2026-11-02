using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace SelectionAssistant.Windows;

internal sealed class SelectionButton : Window
{
    private readonly DispatcherTimer expiry = new() { Interval = TimeSpan.FromSeconds(8) };
    internal event Action? OpenRequested;
    internal SelectionButton()
    {
        Style = (Style)Application.Current.FindResource("AppWindow");
        Title = "翻译选中文字 · 拾句";
        Width = 92; Height = 48;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        var logo = new Border
        {
            Style = (Style)Application.Current.FindResource("Logo"), Width = 20, Height = 20, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 7, 0),
            Child = new TextBlock { Text = "拾", Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(logo);
        label.Children.Add(new TextBlock { Text = "翻译", FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Content = label, ToolTip = "翻译选中的文字", Style = (Style)Application.Current.FindResource("OutlineButton"),
            Padding = new Thickness(8, 5, 12, 5), Margin = new Thickness(6), BorderBrush = (Brush)Application.Current.FindResource("Line"),
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Direction = 270, Opacity = 0.16 }
        };
        AutomationProperties.SetName(button, "翻译选中的文字");
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
