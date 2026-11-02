using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SelectionAssistant.Core;
using Expression = SelectionAssistant.Core.Expression;

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
    public string Content { get => content; set { content = value; Notify(); } }
    public bool Completed { get; set; }
}

public sealed class SessionViewModel : Observable, IDisposable
{
    private const string DemoOriginal = "学习语言最好的方式，是每天用它表达真实的想法。";
    private const string DemoTranslation = "The best way to learn a language is to use it every day to say what you really think.\n【语块】\nThe best way to ... is to ... | ……最好的办法是……\nsay what you really think | 说出你真实的想法\nuse it every day | 每天都用它";
    private const string DemoAnswer = "这句的骨架是 The best way to do A is to do B，「做 A 最好的办法是做 B」，比直译「最好的方式是……」更像英语母语者的说法。\n\n· say what you really think：这里用 say 比 express 更口语；express 偏书面。\n· what you really think 是名词性从句，作 say 的宾语。\n\n换个说法：Use it every day to say what's on your mind.\n每天用它说说你心里的想法。\n\n这是离线示例；配置 API Key 后可以针对任意句子自由追问。";
    private readonly Func<ApiSettings> settings;
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(120) };
    private readonly Dispatcher dispatcher = Application.Current.Dispatcher;
    private CancellationTokenSource? cancellation;
    private int version;
    private string original = "", raw = "", translation = "", question = "", source = "手动输入", status = "在任意应用里选中文字，或在上面输入。";
    private string translateError = "";
    private bool busy, ready, isDemo, needsSettings;
    private readonly SpeechPlayer speech;
    private bool speaking;
    public Guid? SavedId { get; private set; }
    public ObservableCollection<ChatTurn> Turns { get; } = new();
    public ObservableCollection<Expression> Expressions { get; } = new();
    public string Original { get => original; set { if (original == value) return; SetContext(value, "手动输入"); } }
    public string Translation { get => translation; private set { translation = value; Notify(); } }
    public string Question { get => question; set { question = value; Notify(); CommandManager.InvalidateRequerySuggested(); } }
    public string Source { get => source; private set { source = value; Notify(); } }
    public string Status { get => status; set { status = value; Notify(); } }
    public bool IsBusy { get => busy; private set { busy = value; Notify(); CommandManager.InvalidateRequerySuggested(); } }
    public bool IsReady { get => ready; private set { ready = value; Notify(); CommandManager.InvalidateRequerySuggested(); } }
    public bool IsDemo { get => isDemo; private set { isDemo = value; Notify(); } }
    public string TranslateError { get => translateError; private set { translateError = value; Notify(); Notify(nameof(HasTranslateError)); } }
    public bool HasTranslateError => TranslateError.Length > 0;
    public bool NeedsSettings { get => needsSettings; private set { needsSettings = value; Notify(); } }
    public bool IsSpeaking { get => speaking; private set { speaking = value; Notify(); CommandManager.InvalidateRequerySuggested(); } }
    public bool HasExpressions => Expressions.Count > 0;
    public bool HasTurns => Turns.Count > 0;
    public bool ToEnglish => Language.IsChinese(Original);
    public string Direction => string.IsNullOrWhiteSpace(Original) ? "自动识别" : ToEnglish ? "中文 → 英文" : "英文 → 中文";
    public string Length => Original.Length == 0 ? "" : $"{Original.Length:N0} 字";
    public bool IsSaved => SavedId.HasValue;
    public string SaveLabel => SavedId.HasValue ? "更新卡片" : "收藏";
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

    public SessionViewModel(Func<ApiSettings> settings, SpeechPlayer? speech = null)
    {
        this.settings = settings;
        this.speech = speech ?? new SpeechPlayer(() => new SpeechSettings(), Path.Combine(Path.GetTempPath(), "shiju-audio-cache"));
        this.speech.SpeakingChanged += value => dispatcher.BeginInvoke(() => IsSpeaking = value);
        Turns.CollectionChanged += (_, _) => Notify(nameof(HasTurns));
        TranslateCommand = new RelayCommand(() => _ = TranslateAsync(), () => !IsBusy && !string.IsNullOrWhiteSpace(Original));
        AskCommand = new RelayCommand(() => _ = AskAsync(), () => !IsBusy && IsReady && !string.IsNullOrWhiteSpace(Question));
        StopCommand = new RelayCommand(() => cancellation?.Cancel(), () => IsBusy);
        CopyCommand = new RelayCommand(() => { try { Clipboard.SetText(Translation); Status = "已复制译文。"; } catch (Exception) { Status = "剪贴板正被其他程序使用，请稍后再试。"; } }, () => IsReady);
        SpeakCommand = new RelayCommand(() => { if (IsSpeaking) speech.Stop(); else SpeakText(ToEnglish ? Translation : Original); }, () => IsReady || IsSpeaking);
        SaveCommand = new RelayCommand(() => SaveRequested?.Invoke(), () => IsReady && !IsBusy);
        DemoCommand = new RelayCommand(LoadDemo, () => !IsBusy);
        SettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke());
    }

    public void SetContext(string text, string sourceName, bool demo = false)
    {
        CancelCurrent();
        speech.Stop();
        original = text;
        Notify(nameof(Original)); Notify(nameof(Direction)); Notify(nameof(ToEnglish)); Notify(nameof(Length));
        Source = sourceName;
        SetRaw("", complete: true);
        SetError(null);
        IsReady = false;
        IsDemo = demo;
        SetSaved(null);
        Question = "";
        Turns.Clear();
        Status = demo ? "离线演示：本地示例，不会发送网络请求。" : "原文已就绪，按 Ctrl+Enter 或点击“翻译”。";
    }

    public void LoadDemo()
    {
        SetContext(DemoOriginal, "离线演示 · 本地示例", true);
        _ = TranslateAsync();
    }

    public void OpenCard(LearningCard card)
    {
        SetContext(card.Original, card.Source, card.Source.StartsWith("离线演示", StringComparison.Ordinal));
        Translation = card.Translation;
        foreach (var expression in card.Expressions ?? new()) Expressions.Add(expression);
        Notify(nameof(HasExpressions));
        IsReady = true;
        SetSaved(card.Id);
        foreach (var turn in card.Messages) Turns.Add(new(turn.Role, turn.Content) { Completed = true });
        Status = "已打开收藏，可以继续追问或更新卡片。";
        speech.WarmUp();
    }

    public LearningCard Card() => new(SavedId ?? Guid.NewGuid(), Original.Trim(), Translation, Source, DateTimeOffset.Now, Turns.Where(t => t.Completed).Select(t => new ChatMessage(t.Role, t.Content)).ToList(), Expressions.ToList());
    public void MarkSaved(Guid id) { SetSaved(id); Status = "已收藏到学习卡片，原文、译文、语块和对话都会保留。"; }
    public void MarkDeleted(Guid id) { if (SavedId == id) SetSaved(null); }

    public async Task TranslateAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(Original)) return;
        if (Original.Length > 12000) { Status = "一次最多翻译 12,000 个字符，请缩短选区。"; return; }
        var current = ++version;
        using var tokenSource = new CancellationTokenSource();
        cancellation = tokenSource;
        IsBusy = true;
        IsReady = false;
        SetRaw("", complete: true);
        SetError(null);
        Turns.Clear();
        Status = IsDemo ? "正在播放离线演示……" : "正在翻译……";
        try
        {
            if (IsDemo)
                await DemoStream(DemoTranslation, s => SetRaw(raw + s, complete: false), tokenSource.Token);
            else
                await new ChatClient(http).CompleteAsync(settings(), Prompts.Translation(Original.Trim()), s => dispatcher.Invoke(() => { if (version == current) SetRaw(raw + s, complete: false); }), tokenSource.Token);
            if (version != current) return;
            SetRaw(raw, complete: true);
            IsReady = true;
            speech.WarmUp();
            Status = IsDemo ? "离线示例已就绪，可以体验追问、朗读和收藏。" : "翻译完成。可以朗读、收藏，或者在下面继续追问。";
        }
        catch (OperationCanceledException) { if (version == current) { SetRaw("", complete: true); Status = tokenSource.IsCancellationRequested ? "已停止翻译，可以重新开始。" : "响应超时，请重试。"; } }
        catch (Exception ex) { if (version == current) { SetRaw("", complete: true); SetError(ex); Status = "翻译没有完成。"; } }
        finally { if (version == current) { IsBusy = false; cancellation = null; } }
    }

    // After the user fixes the key or endpoint, pick up where the failed translation left off.
    public void RetryAfterSettings()
    {
        if (NeedsSettings && !IsBusy && !string.IsNullOrWhiteSpace(Original)) _ = TranslateAsync();
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
        Status = "正在回答……";
        try
        {
            var text = "";
            if (IsDemo) await DemoStream(DemoAnswer, s => answer.Content = Prompts.Plain(text += s), tokenSource.Token);
            else text = await new ChatClient(http).CompleteAsync(settings(), Prompts.Conversation(Original, Translation, history, asked), s => dispatcher.Invoke(() => { if (version == current) answer.Content = Prompts.Plain(text += s); }), tokenSource.Token);
            if (version != current) return;
            answer.Content = Prompts.Plain(text).Trim();
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

    private void SetRaw(string value, bool complete)
    {
        raw = value;
        var (text, expressions) = TranslationFormat.Parse(raw, complete);
        Translation = text;
        if (expressions.SequenceEqual(Expressions)) return;
        Expressions.Clear();
        foreach (var expression in expressions) Expressions.Add(expression);
        Notify(nameof(HasExpressions));
    }

    private void SetError(Exception? ex)
    {
        TranslateError = ex is null ? "" : Error(ex);
        NeedsSettings = ex is ArgumentException or InvalidOperationException || (ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.BadRequest });
    }

    private void SetSaved(Guid? id) { SavedId = id; Notify(nameof(SaveLabel)); Notify(nameof(IsSaved)); }

    private static async Task DemoStream(string text, Action<string> onDelta, CancellationToken token)
    {
        for (int i = 0; i < text.Length; i += 6)
        {
            await Task.Delay(18, token);
            onDelta(text.Substring(i, Math.Min(6, text.Length - i)));
        }
    }
    private static string Error(Exception ex) => ex switch
    {
        ArgumentException or InvalidDataException or InvalidOperationException => ex.Message,
        HttpRequestException request => request.StatusCode.HasValue ? request.Message : "连接失败，请检查网络和接口地址。",
        _ => "请求失败，请检查接口配置或稍后重试。"
    };
    // Always read the English side: the translation for Chinese selections, the selection itself otherwise.
    public async void SpeakText(string text)
    {
        try
        {
            var result = await speech.SpeakAsync(text);
            if (result.Length > 0) Status = result;
        }
        catch (Exception) { Status = "朗读失败，请稍后再试。"; }
    }
    public Task<string> Preview(string text, SpeechSettings trial) => speech.SpeakAsync(text, trial);
    private void CancelCurrent() { ++version; cancellation?.Cancel(); cancellation = null; IsBusy = false; }
    public void Dispose() { CancelCurrent(); http.Dispose(); speech.Dispose(); }
}
