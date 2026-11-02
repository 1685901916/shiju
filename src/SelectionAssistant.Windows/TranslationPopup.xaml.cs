using System.Windows;
using System.Windows.Input;

namespace SelectionAssistant.Windows;

public partial class TranslationPopup : Window
{
    private readonly App owner;
    internal bool Pinned { get; private set; }
    internal TranslationPopup(App owner)
    {
        this.owner = owner;
        InitializeComponent();
        View.DataContext = owner.Session;
        SourceInitialized += (_, _) => Native.Theme(this);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && !Pinned) { Hide(); e.Handled = true; } };
        Closing += (_, e) => { if (!owner.Exiting) { e.Cancel = true; Hide(); } };
        Deactivated += (_, _) => { if (!Pinned && !owner.SettingsOpen) Hide(); };
    }
    internal void Present(double x, double y)
    {
        Show(); Native.PlaceNear(this, x, y);
    }
    private void Main_Click(object sender, RoutedEventArgs e) => owner.ShowMain();
    private void Close_Click(object sender, RoutedEventArgs e) => Hide();
    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        Pinned = !Pinned;
        PinButton.Background = Pinned ? (System.Windows.Media.Brush)FindResource("AccentSoft") : System.Windows.Media.Brushes.Transparent;
        PinButton.Foreground = (System.Windows.Media.Brush)FindResource(Pinned ? "Accent" : "Ink2");
        PinButton.ToolTip = Pinned ? "已固定，点击取消" : "固定：切到别的窗口也不收起";
    }
}
