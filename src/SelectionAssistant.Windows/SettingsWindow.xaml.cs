using System.Windows;
using System.Windows.Controls;
using SelectionAssistant.Core;

namespace SelectionAssistant.Windows;

public partial class SettingsWindow : Window
{
    private const string PreviewSentence = "The best way to learn a language is to use it every day to say what you really think.";
    private static readonly (int Rate, string Label)[] Rates = { (0, "正常"), (-10, "稍慢"), (-20, "慢一点") };
    private readonly App owner;
    public SettingsWindow(App owner)
    {
        this.owner = owner;
        InitializeComponent();
        SourceInitialized += (_, _) => Native.Theme(this);
        var settings = owner.Settings;
        EndpointBox.Text = settings.Endpoint;
        ModelBox.Text = settings.Model;
        SelectionBox.IsChecked = settings.SelectionEnabled;
        AutoBox.IsChecked = settings.AutoTranslate;
        KeyHint.Text = settings.ProtectedKey.Length > 0 ? "已保存密钥。留空会继续使用原来的密钥。" : "还没有密钥。在服务商控制台创建后粘贴到这里。";
        RegionBox.Text = settings.SpeechRegion;
        SpeechKeyHint.Text = settings.ProtectedSpeechKey.Length > 0
            ? "已保存语音密钥，留空继续使用。区域填资源所在地区的英文代号，如 eastasia。"
            : "在 Azure 门户创建“语音”资源（定价层选 F0 免费），把“密钥和终结点”页的密钥与区域填到这里。";
        ShowAzure(settings.SpeechRegion.Length > 0);
        foreach (var voice in SpeechVoices.All)
            VoicePanel.Children.Add(new RadioButton { Content = voice.Label, Tag = voice.Key, GroupName = "Voice", Style = (Style)FindResource("Choice"), IsChecked = voice.Key == SpeechVoices.Find(settings.SpeechVoice).Key });
        foreach (var (rate, label) in Rates)
            RatePanel.Children.Add(new RadioButton { Content = label, Tag = rate, GroupName = "Rate", Style = (Style)FindResource("Choice"), IsChecked = rate == (Rates.Any(r => r.Rate == settings.SpeechRate) ? settings.SpeechRate : 0) });
    }

    private void AzureToggle_Click(object sender, RoutedEventArgs e) => ShowAzure(AzurePanel.Visibility != Visibility.Visible);
    private void ShowAzure(bool show)
    {
        AzurePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        AzureToggle.Content = (show ? "▾" : "▸") + "  Azure 神经语音（可选，需要自己的 Azure 账号）";
    }

    private string Chosen(Panel panel, string fallback) => panel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag?.ToString() ?? fallback;
    private string SpeechKey() => SpeechKeyBox.Password.Length > 0 ? SpeechKeyBox.Password.Trim() : SettingsStore.Unprotect(owner.Settings.ProtectedSpeechKey);
    private SpeechSettings FormSpeech() => new(RegionBox.Text.Trim(), SpeechKey(), Chosen(VoicePanel, SpeechVoices.Default), int.Parse(Chosen(RatePanel, "0")));

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var speech = FormSpeech();
            if (speech.Configured) speech.GetUri();
            PreviewText.Text = "正在获取…";
            var result = await owner.Session.Preview(PreviewSentence, speech);
            PreviewText.Text = result.Length > 0 ? result : speech.Configured ? "Azure 神经语音可用。" : EdgeVoice.Executable is not null ? "Edge 自然语音可用。" : "在线发音正常。";
        }
        catch (Exception ex) { PreviewText.Text = ex is ArgumentException or InvalidOperationException ? ex.Message : "试听失败，请检查区域和密钥。"; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var validated = new ApiSettings(EndpointBox.Text, ModelBox.Text).GetUri();
            var region = RegionBox.Text.Trim().ToLowerInvariant();
            if (region.Length > 0) new SpeechSettings(region, "-").GetUri();
            var key = ClearKeyBox.IsChecked == true ? "" : KeyBox.Password.Length > 0 ? SettingsStore.Protect(KeyBox.Password.Trim()) : owner.Settings.ProtectedKey;
            var speechKey = SpeechKeyBox.Password.Length > 0 ? SettingsStore.Protect(SpeechKeyBox.Password.Trim()) : owner.Settings.ProtectedSpeechKey;
            owner.UpdateSettings(owner.Settings with
            {
                Endpoint = validated.AbsoluteUri, Model = ModelBox.Text.Trim(), ProtectedKey = key,
                SelectionEnabled = SelectionBox.IsChecked == true, AutoTranslate = AutoBox.IsChecked == true,
                SpeechRegion = region, ProtectedSpeechKey = speechKey,
                SpeechVoice = Chosen(VoicePanel, SpeechVoices.Default), SpeechRate = int.Parse(Chosen(RatePanel, "0"))
            });
            KeyBox.Clear();
            SpeechKeyBox.Clear();
            Close();
            owner.Session.RetryAfterSettings();
        }
        catch (Exception ex) { ResultText.Visibility = Visibility.Visible; ResultText.Text = ex is ArgumentException or InvalidOperationException ? ex.Message : "无法保存设置，请检查数据目录是否可写。"; }
    }
}
