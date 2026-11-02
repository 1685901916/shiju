using System.Windows;
using SelectionAssistant.Core;

namespace SelectionAssistant.Windows;

public partial class SettingsWindow : Window
{
    private readonly App owner;
    public SettingsWindow(App owner)
    {
        this.owner = owner;
        InitializeComponent();
        var settings = owner.Settings;
        EndpointBox.Text = settings.Endpoint;
        ModelBox.Text = settings.Model;
        SelectionBox.IsChecked = settings.SelectionEnabled;
        AutoBox.IsChecked = settings.AutoTranslate;
        KeyHint.Text = settings.ProtectedKey.Length > 0 ? "本机已有加密密钥；留空会保留原密钥。" : "输入服务商提供的 API Key。";
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var validated = new ApiSettings(EndpointBox.Text, ModelBox.Text).GetUri();
            var key = ClearKeyBox.IsChecked == true ? "" : KeyBox.Password.Length > 0 ? SettingsStore.Protect(KeyBox.Password.Trim()) : owner.Settings.ProtectedKey;
            owner.UpdateSettings(new(validated.AbsoluteUri, ModelBox.Text.Trim(), key, SelectionBox.IsChecked == true, AutoBox.IsChecked == true));
            KeyBox.Clear();
            Close();
        }
        catch (Exception ex) { ResultText.Text = ex is ArgumentException or InvalidOperationException ? ex.Message : "无法保存设置，请检查数据目录是否可写。"; }
    }
}
