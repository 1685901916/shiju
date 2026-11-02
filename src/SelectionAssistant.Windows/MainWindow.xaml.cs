using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SelectionAssistant.Core;

namespace SelectionAssistant.Windows;

public partial class MainWindow : Window
{
    private readonly App owner;
    public MainWindow(App owner)
    {
        this.owner = owner;
        InitializeComponent();
        TranslationView.DataContext = owner.Session;
        SelectionToggle.IsChecked = owner.Settings.SelectionEnabled;
    }
    internal void ShowTranslation()
    {
        TranslationView.Visibility = Visibility.Visible;
        LibraryPanel.Visibility = Visibility.Collapsed;
        PageTitle.Text = "从一句话开始";
        PageSubtitle.Text = "选中文字、翻成英文，再多问一句为什么。";
        TranslateNav.Background = (Brush)FindResource("Accent"); TranslateNav.Foreground = Brushes.White;
        CardsNav.Background = Brushes.Transparent; CardsNav.Foreground = (Brush)FindResource("Ink");
    }
    private void TranslateNav_Click(object sender, RoutedEventArgs e) => ShowTranslation();
    private void CardsNav_Click(object sender, RoutedEventArgs e)
    {
        TranslationView.Visibility = Visibility.Collapsed;
        LibraryPanel.Visibility = Visibility.Visible;
        PageTitle.Text = "我的学习卡片";
        TranslateNav.Background = Brushes.Transparent; TranslateNav.Foreground = (Brush)FindResource("Ink");
        CardsNav.Background = (Brush)FindResource("Accent"); CardsNav.Foreground = Brushes.White;
        RefreshLibrary();
    }
    internal void RefreshLibrary()
    {
        if (CardsList is null) return;
        var query = SearchBox.Text.Trim();
        var cards = owner.Cards.Where(c => query.Length == 0 || (c.Original + c.Translation + c.Source).Contains(query, StringComparison.OrdinalIgnoreCase)).OrderByDescending(c => c.CreatedAt).ToList();
        CardsList.ItemsSource = cards;
        EmptyLibrary.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyLibrary.Text = owner.LibraryError ?? (query.Length == 0 ? "还没有学习卡片。翻译一句话，点击“收藏卡片”保存到这里。" : "没有找到匹配的卡片，换个关键词试试。" );
        if (LibraryPanel.Visibility == Visibility.Visible) PageSubtitle.Text = $"共 {owner.Cards.Count} 张卡片 · 原文、译文和你的每一次追问。";
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (IsInitialized) RefreshLibrary(); }
    private void Settings_Click(object sender, RoutedEventArgs e) => owner.ShowSettings();
    private void Demo_Click(object sender, RoutedEventArgs e) { ShowTranslation(); owner.Session.LoadDemo(); }
    private void SelectionToggle_Click(object sender, RoutedEventArgs e) => owner.ToggleSelection(SelectionToggle.IsChecked == true);
    internal void UpdateSelectionToggle() => SelectionToggle.IsChecked = owner.Settings.SelectionEnabled;
    private void OpenCard_Click(object sender, RoutedEventArgs e) { owner.Session.OpenCard((LearningCard)((Button)sender).DataContext); ShowTranslation(); }
    private void DeleteCard_Click(object sender, RoutedEventArgs e)
    {
        var card = (LearningCard)((Button)sender).DataContext;
        owner.DeleteCard(card.Id);
        RefreshLibrary();
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!owner.Exiting) { e.Cancel = true; Hide(); owner.Session.Status = "应用已留在系统托盘，全局划词继续运行。"; }
        base.OnClosing(e);
    }
}
