using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows.Threading;

namespace SelectionAssistant.Windows;

public partial class TranslateView : UserControl
{
    private SessionViewModel? subscribed;
    public TranslateView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Subscribe();
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
    }
    private void Subscribe()
    {
        if (ReferenceEquals(subscribed, DataContext)) return;
        Unsubscribe();
        if (DataContext is not SessionViewModel model) return;
        subscribed = model;
        model.Turns.CollectionChanged += TurnsChanged;
        model.PropertyChanged += ModelChanged;
    }
    private void Unsubscribe()
    {
        if (subscribed is null) return;
        subscribed.Turns.CollectionChanged -= TurnsChanged;
        subscribed.PropertyChanged -= ModelChanged;
        subscribed = null;
    }
    private void TurnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            Dispatcher.BeginInvoke(() => ContentScroll.ScrollToEnd(), DispatcherPriority.Background);
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionViewModel.Original)) ContentScroll.ScrollToTop();
        if (e.PropertyName == nameof(SessionViewModel.IsBusy) && !Model.IsBusy && Model.Turns.Count > 0)
            Dispatcher.BeginInvoke(() => ContentScroll.ScrollToEnd(), DispatcherPriority.Background);
    }
    private SessionViewModel Model => (SessionViewModel)DataContext;
    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Clipboard.ContainsText()) { Model.Status = "剪贴板没有文字。"; return; }
            var text = Clipboard.GetText();
            if (text.Length > 12000) { Model.Status = "文字太长，请选择 12,000 个字符以内的内容。"; return; }
            Model.SetContext(text, "剪贴板");
        }
        catch (Exception) { Model.Status = "暂时无法读取剪贴板，请稍后重试。"; }
    }
    private void QuickAsk_Click(object sender, RoutedEventArgs e)
    {
        if (Model.IsBusy) return;
        Model.Question = (string)((Button)sender).Tag;
        if (Model.AskCommand.CanExecute(null)) Model.AskCommand.Execute(null);
    }
    private void SpeakChunk_Click(object sender, RoutedEventArgs e) => Model.SpeakText(((SelectionAssistant.Core.Expression)((Button)sender).DataContext).Phrase);
    private void Source_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            if (Model.TranslateCommand.CanExecute(null)) Model.TranslateCommand.Execute(null);
        }
    }
    private void Question_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            if (Model.AskCommand.CanExecute(null)) Model.AskCommand.Execute(null);
        }
    }
}
