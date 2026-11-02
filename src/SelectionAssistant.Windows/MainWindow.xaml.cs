using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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
        SourceInitialized += (_, _) => Native.Theme(this);
        RefreshLibrary();
    }
    internal void ShowTranslation()
    {
        TranslationView.Visibility = Visibility.Visible;
        LibraryPanel.Visibility = Visibility.Collapsed;
        DemoButton.Visibility = Visibility.Visible;
        PageTitle.Text = "翻译";
        PageSubtitle.Text = "中文译成地道英文，英文译成通顺中文，顺手记下值得学的语块。";
        TranslateNav.IsChecked = true;
    }
    // Checked (not Click) so keyboard and UI Automation selection switch pages too.
    private void TranslateNav_Checked(object sender, RoutedEventArgs e) { if (LibraryPanel is not null) ShowTranslation(); }
    private void CardsNav_Checked(object sender, RoutedEventArgs e)
    {
        TranslationView.Visibility = Visibility.Collapsed;
        LibraryPanel.Visibility = Visibility.Visible;
        DemoButton.Visibility = Visibility.Collapsed;
        PageTitle.Text = "学习卡片";
        CardsNav.IsChecked = true;
        RefreshLibrary();
    }
    internal void RefreshLibrary()
    {
        if (CardsList is null) return;
        var query = SearchBox.Text.Trim();
        var cards = owner.Cards.Where(c => query.Length == 0 || (c.Original + c.Translation + c.Source + string.Concat((c.Expressions ?? new()).Select(x => x.Phrase + x.Meaning))).Contains(query, StringComparison.OrdinalIgnoreCase)).OrderByDescending(c => c.CreatedAt).ToList();
        CardsList.ItemsSource = cards;
        SearchHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyLibrary.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = owner.LibraryError ?? (query.Length == 0 ? "还没有学习卡片。\n翻译一句话后点“收藏”，原文、译文、语块和追问都会存到这里。" : "没有找到匹配的卡片，换个关键词试试。");
        CardCount.Text = owner.Cards.Count == 0 ? "" : owner.Cards.Count.ToString();
        if (LibraryPanel.Visibility == Visibility.Visible) PageSubtitle.Text = owner.Cards.Count == 0 ? "收藏的句子会出现在这里。" : $"共 {owner.Cards.Count} 张 · 点开可以接着追问。";
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
