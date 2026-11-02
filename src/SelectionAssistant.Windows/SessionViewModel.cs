using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SelectionAssistant.Core;

namespace SelectionAssistant.Windows;

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class ChatTurn(string role, string text) : Observable
{
    private string content = text;
    public string Role { get; } = role;
    public string Label => Role == "user" ? "你" : "DeepSeek";
    public string Content { get => content; set { content = value; Notify(); } }
    public bool Completed { get; set; }
}

public sealed class SessionViewModel : Observable, IDisposable
{
    private readonly Func<ApiSettings> settings;
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(120) };
    private readonly Dispatcher dispatcher = Application.Current.Dispatcher;
    private CancellationTokenSource? cancellation;
    private int version;
    private string original = "", translation = "", question = "", source = "手动输入", status = "选择一句话，开始你的英语学习。";
    private bool busy, ready, isDemo;
    private dynamic? voice;
    public Guid? SavedId { get; private set; }
    public ObservableCollection<ChatTurn> Turns { get; } = new();
    public string Original { get => original; set { if (original == value) return; SetContext(value, "手动输入"); } }
    public string Translation { get => translation; private set { translation = value; Notify(); } }
    public string Question { get => question; set { question = value; Notify(); CommandManager.InvalidateRequerySuggested(); } }
    public string Source { get => source; private set { source = value; Notify(); } }
    public string Status { get => status; set { status = value; Notify(); } }
    public bool IsBusy { get => busy; private set { busy = value; Notify(); CommandManager.InvalidateRequerySuggested(); } }
    public bool IsReady { get => ready; private set { ready = value; Notify(); CommandManager.InvalidateRequerySuggested(); } }
    public bool IsDemo { get => isDemo; private set { isDemo = value; Notify(); } }
    public string SaveLabel => SavedId.HasValue ? "更新卡片" : "☆ 收藏卡片";
    public ICommand TranslateCommand { get; }
    public ICommand AskCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand SpeakCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DemoCommand { get; }
    public ICommand SettingsCommand { get; }
    public event Action? SaveRequested;
    public event Action? SettingsRequested;

    public SessionViewModel(Func<ApiSettings> settings)
    {
        this.settings = settings;
        TranslateCommand = new RelayCommand(() => _ = TranslateAsync(), () => !IsBusy && !string.IsNullOrWhiteSpace(Original));
        AskCommand = new RelayCommand(() => _ = AskAsync(), () => !IsBusy && IsReady && !string.IsNullOrWhiteSpace(Question));
        StopCommand = new RelayCommand(() => cancellation?.Cancel(), () => IsBusy);
        CopyCommand = new RelayCommand(() => { try { Clipboard.SetText(Translation); Status = "已复制英文译文。"; } catch (Exception) { Status = "剪贴板正被其他程序使用，请稍后再试。"; } }, () => IsReady);
        SpeakCommand = new RelayCommand(Speak, () => IsReady);
        SaveCommand = new RelayCommand(() => SaveRequested?.Invoke(), () => IsReady && !IsBusy);
        DemoCommand = new RelayCommand(LoadDemo, () => !IsBusy);
        SettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke());
    }

    public void SetContext(string text, string sourceName, bool demo = false)
    {
        CancelCurrent();
        original = text;
        Notify(nameof(Original));
        Source = sourceName;
        Translation = "";
        IsReady = false;
        IsDemo = demo;
        SavedId = null;
        Notify(nameof(SaveLabel));
        Question = "";
        Turns.Clear();
        Status = demo ? "离线演示：本地示例，不会发送网络请求。" : "已准备好原文，点击“翻译成英文”。";
    }

    public void LoadDemo()
    {
        SetContext("学习语言最好的方式，是每天用它表达真实的想法。", "离线演示 · 本地示例", true);
        _ = TranslateAsync();
    }

    public void OpenCard(LearningCard card)
    {
        SetContext(card.Original, card.Source, card.Source.StartsWith("离线演示", StringComparison.Ordinal));
        Translation = card.Translation;
        IsReady = true;
        SavedId = card.Id;
        Notify(nameof(SaveLabel));
        foreach (var turn in card.Messages) Turns.Add(new(turn.Role, turn.Content) { Completed = true });
        Status = "已打开收藏，可以继续追问或更新卡片。";
    }

    public LearningCard Card() => new(SavedId ?? Guid.NewGuid(), Original.Trim(), Translation, Source, DateTimeOffset.Now, Turns.Where(t => t.Completed).Select(t => new ChatMessage(t.Role, t.Content)).ToList());
    public void MarkSaved(Guid id) { SavedId = id; Notify(nameof(SaveLabel)); Status = "已保存到学习卡片，原文、译文和对话都会保留。"; }
    public void MarkDeleted(Guid id) { if (SavedId != id) return; SavedId = null; Notify(nameof(SaveLabel)); }

    public async Task TranslateAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(Original)) return;
        if (Original.Length > 12000) { Status = "一次最多翻译 12,000 个字符，请缩短选区。"; return; }
        var current = ++version;
        using var tokenSource = new CancellationTokenSource();
        cancellation = tokenSource;
        IsBusy = true;
        IsReady = false;
        Translation = "";
        Turns.Clear();
        Status = IsDemo ? "正在播放离线演示……" : "正在翻译成英文……";
        try
        {
            string result;
            if (IsDemo)
                result = await DemoStream("The best way to learn a language is to use it every day to express what you really think.", s => Translation += s, tokenSource.Token);
            else
                result = await new ChatClient(http).CompleteAsync(settings(), Prompts.Translation(Original.Trim()), s => dispatcher.Invoke(() => { if (version == current) Translation += s; }), tokenSource.Token);
            if (version != current) return;
            Translation = result;
            IsReady = true;
            Status = IsDemo ? "离线示例已就绪，可以体验追问、朗读和收藏。" : "翻译完成，可以复制、收藏，或继续问 DeepSeek。";
        }
        catch (OperationCanceledException) { if (version == current) { Translation = ""; Status = tokenSource.IsCancellationRequested ? "已停止翻译，可以重新开始。" : "响应超时，请重试。"; } }
        catch (Exception ex) { if (version == current) { Translation = ""; Status = Error(ex); } }
        finally { if (version == current) { IsBusy = false; cancellation = null; } }
    }

    public async Task AskAsync()
    {
        if (IsBusy || !IsReady || string.IsNullOrWhiteSpace(Question)) return;
        if (Question.Length > 4000) { Status = "问题最多 4,000 个字符。"; return; }
        var asked = Question.Trim();
        var history = Turns.Where(t => t.Completed).Select(t => new ChatMessage(t.Role, t.Content)).ToList();
        var current = ++version;
        using var tokenSource = new CancellationTokenSource();
        cancellation = tokenSource;
        IsBusy = true;
        Question = "";
        var user = new ChatTurn("user", asked);
        var answer = new ChatTurn("assistant", "");
        Turns.Add(user); Turns.Add(answer);
        Status = "DeepSeek 正在回答……";
        try
        {
            string result;
            if (IsDemo)
            {
                const string demoAnswer = "【离线演示回答】\n这句用 The best way to… 表示“做……最好的方式”，后面的 is to… 说明具体做法。\n\nuse it every day 中 it 指 a language；what you really think 表示“你真实的想法”。\n\n你也可以说：Try expressing one real thought in English every day.\n试着每天用英语表达一个真实想法。\n\n这是预置示例；配置 API Key 后可针对任意句子自由追问。";
                result = await DemoStream(demoAnswer, s => answer.Content += s, tokenSource.Token);
            }
            else result = await new ChatClient(http).CompleteAsync(settings(), Prompts.Conversation(Original, Translation, history, asked), s => dispatcher.Invoke(() => { if (version == current) answer.Content += s; }), tokenSource.Token);
            if (version != current) return;
            answer.Content = result;
            user.Completed = answer.Completed = true;
            Status = "回答完成，可以继续追问；收藏会保留本次对话。";
        }
        catch (Exception ex)
        {
            if (version == current)
            {
                Turns.Remove(user); Turns.Remove(answer);
                Question = asked;
                Status = ex is OperationCanceledException ? (tokenSource.IsCancellationRequested ? "已停止回答，问题已保留。" : "响应超时，问题已保留。") : Error(ex);
            }
        }
        finally { if (version == current) { IsBusy = false; cancellation = null; } }
    }

    private async Task<string> DemoStream(string text, Action<string> onDelta, CancellationToken token)
    {
        for (int i = 0; i < text.Length; i += 6)
        {
            await Task.Delay(18, token);
            onDelta(text.Substring(i, Math.Min(6, text.Length - i)));
        }
        return text;
    }
    private static string Error(Exception ex) => ex switch
    {
        ArgumentException or InvalidDataException or InvalidOperationException => ex.Message,
        HttpRequestException request => request.StatusCode.HasValue ? request.Message : "连接失败，请检查网络和接口地址。",
        _ => "请求失败，请检查接口配置或稍后重试。"
    };
    private void Speak()
    {
        try
        {
            voice ??= Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice") ?? throw new InvalidOperationException());
            voice!.Speak(Translation, 3);
            Status = "正在使用 Windows 系统语音朗读。";
        }
        catch (Exception) { Status = "系统语音不可用，请在 Windows 设置中安装英语语音。"; }
    }
    private void CancelCurrent() { ++version; cancellation?.Cancel(); cancellation = null; IsBusy = false; }
    public void Dispose() { CancelCurrent(); http.Dispose(); if (voice is not null) { try { voice.Speak("", 3); System.Runtime.InteropServices.Marshal.FinalReleaseComObject(voice); } catch (Exception) { } } }
}
