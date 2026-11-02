using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Automation;
using System.Diagnostics;
using System.Text.Json;
using SelectionAssistant.Core;
using Forms = System.Windows.Forms;

namespace SelectionAssistant.Windows;

public partial class App : Application
{
    private Mutex? instance;
    private EventWaitHandle? wake;
    private CancellationTokenSource? lifetime;
    private Forms.NotifyIcon? tray;
    private SelectionMonitor? monitor;
    private SelectionButton? selectionButton;
    private TranslationPopup? popup;
    private MainWindow? main;
    private SettingsWindow? settingsWindow;
    private SettingsStore? settingsStore;
    private LibraryStore? library;
    private CapturedSelection? pending;
    private IntPtr mainHandle;
    internal SessionViewModel Session { get; private set; } = null!;
    internal UserSettings Settings { get; private set; } = new();
    internal List<LearningCard> Cards { get; private set; } = new();
    internal string? LibraryError { get; private set; }
    internal bool Exiting { get; private set; }
    internal bool SettingsOpen => settingsWindow?.IsVisible == true;
    internal string DataDirectory { get; private set; } = "";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--fixture")) { ShowFixture(); return; }
        if (e.Args.Contains("--self-test")) { _ = SelfTestAsync(e.Args); return; }
        var dataIndex = Array.IndexOf(e.Args, "--data-dir");
        DataDirectory = dataIndex >= 0 && dataIndex + 1 < e.Args.Length ? Path.GetFullPath(e.Args[dataIndex + 1]) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SelectionAssistant");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DataDirectory.ToLowerInvariant())))[..20];
        instance = new Mutex(true, "Local\\SelectionAssistant-" + hash, out var isFirst);
        wake = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\SelectionAssistant-Wake-" + hash);
        if (!isFirst) { wake.Set(); Shutdown(); return; }
        lifetime = new();
        _ = Task.Run(() =>
        {
            while (!lifetime.IsCancellationRequested)
                if (wake.WaitOne(600)) Dispatcher.BeginInvoke(ShowMain);
        });
        Directory.CreateDirectory(DataDirectory);
        settingsStore = new(DataDirectory);
        string? warning = null;
        try { Settings = settingsStore.Load(); }
        catch (Exception) { warning = "设置文件无法读取，已保留原文件；请在设置中重新配置。"; }
        library = new(Path.Combine(DataDirectory, "cards.json"));
        try { Cards = library.Load(); }
        catch (Exception ex) { LibraryError = ex is InvalidDataException ? ex.Message : "收藏文件暂时无法读取，已保留原文件。"; warning = LibraryError; }
        Session = new(() => SettingsStore.Api(Settings));
        Session.SaveRequested += SaveCard;
        Session.SettingsRequested += ShowSettings;
        main = new(this);
        MainWindow = main;
        main.Show();
        mainHandle = new WindowInteropHelper(main).Handle;
        HwndSource.FromHwnd(mainHandle)?.AddHook(HotkeyHook);
        bool selectedKey = Native.RegisterHotKey(mainHandle, 1, 0x4000 | 0x1 | 0x4, 0x54);
        bool clipboardKey = Native.RegisterHotKey(mainHandle, 2, 0x4000 | 0x1 | 0x4, 0x56);
        if (!selectedKey || !clipboardKey) warning = "部分快捷键被其他程序占用；仍可点击划词按钮或在主窗口粘贴。";
        selectionButton = new();
        popup = new(this);
        selectionButton.OpenRequested += () => { if (pending is not null) OpenSelection(pending); };
        Action<string>? diagnostic = null;
        if (e.Args.Contains("--diagnostics")) diagnostic = message =>
        {
            try { var path = Path.Combine(DataDirectory, "selection-diagnostics.log"); if (!File.Exists(path) || new FileInfo(path).Length < 100000) File.AppendAllText(path, DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine); } catch (Exception) { }
        };
        monitor = new(diagnostic) { Enabled = Settings.SelectionEnabled };
        monitor.Selected += selection => Dispatcher.BeginInvoke(() => OnSelection(selection));
        monitor.Failed += message => Dispatcher.BeginInvoke(() => { ShowMain(); Session.Status = message; });
        monitor.Dismiss += () => Dispatcher.BeginInvoke(() =>
        {
            if (!Native.IsOurWindow(Native.GetForegroundWindow()))
            {
                selectionButton.Dismiss();
                if (!popup.Pinned) popup.Hide();
            }
        });
        if (!monitor.HookAvailable) warning = "鼠标划词监听不可用，仍可使用快捷键读取选区。";
        CreateTray();
        if (warning is not null) Session.Status = warning;
        if (e.Args.Contains("--demo")) Session.LoadDemo();
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled)
    {
        if (msg != Native.WM_HOTKEY) return IntPtr.Zero;
        handled = true;
        if ((int)wp == 1) monitor?.Capture();
        if ((int)wp == 2)
        {
            var source = Native.Title(Native.GetForegroundWindow());
            try
            {
                if (!Clipboard.ContainsText()) { ShowMain(); Session.Status = "剪贴板没有文字。请先复制想翻译的内容。"; }
                else
                {
                    var text = Clipboard.GetText().Trim();
                    if (text.Length == 0 || text.Length > 12000) { ShowMain(); Session.Status = "请选择 1 到 12,000 个字符的文字。"; }
                    else { Native.GetCursorPos(out var cursor); OpenSelection(new(text, "剪贴板 · " + source, cursor.X, cursor.Y, true)); }
                }
            }
            catch (Exception) { ShowMain(); Session.Status = "无法读取剪贴板，请稍后重试。"; }
        }
        return IntPtr.Zero;
    }

    private void OnSelection(CapturedSelection selection)
    {
        pending = selection;
        if (selection.Explicit || (Settings.AutoTranslate && popup?.Pinned != true)) OpenSelection(selection);
        else selectionButton?.Present(selection.X, selection.Y);
    }

    private void OpenSelection(CapturedSelection selection)
    {
        selectionButton?.Dismiss();
        Session.SetContext(selection.Text, selection.Source);
        main?.ShowTranslation();
        popup?.Present(selection.X, selection.Y);
        _ = Session.TranslateAsync();
    }

    internal void ShowMain()
    {
        if (main is null) return;
        main.Show();
        if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
        main.Activate();
    }

    internal void ShowSettings()
    {
        if (settingsWindow is not null) { settingsWindow.Activate(); return; }
        settingsWindow = new(this) { Owner = main?.IsVisible == true ? main : popup?.IsVisible == true ? popup : null };
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        settingsWindow.Show();
    }

    internal void UpdateSettings(UserSettings settings)
    {
        settingsStore!.Save(settings);
        Settings = settings;
        if (monitor is not null) monitor.Enabled = settings.SelectionEnabled;
        if (!settings.SelectionEnabled) selectionButton?.Dismiss();
        main?.UpdateSelectionToggle();
        Session.Status = "设置已保存，下一次翻译将使用新配置。";
    }

    internal void ToggleSelection(bool enabled)
    {
        try { UpdateSettings(Settings with { SelectionEnabled = enabled }); }
        catch (Exception) { Session.Status = "无法保存划词偏好，请检查数据目录。"; main?.UpdateSelectionToggle(); }
    }

    private void SaveCard()
    {
        if (LibraryError is not null) { Session.Status = LibraryError; return; }
        var card = Session.Card();
        var next = Cards.Where(c => c.Id != card.Id).Append(card).ToList();
        try { library!.Save(next); Cards = next; Session.MarkSaved(card.Id); main?.RefreshLibrary(); }
        catch (Exception ex) { Session.Status = ex is InvalidDataException ? ex.Message : "收藏保存失败，请检查磁盘空间与数据目录权限。"; }
    }

    internal void DeleteCard(Guid id)
    {
        if (LibraryError is not null) { Session.Status = LibraryError; return; }
        var next = Cards.Where(c => c.Id != id).ToList();
        try { library!.Save(next); Cards = next; Session.MarkDeleted(id); }
        catch (Exception) { Session.Status = "删除失败，原收藏已保留。"; }
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开拾句", null, (_, _) => Dispatcher.BeginInvoke(ShowMain));
        menu.Items.Add("模型与偏好", null, (_, _) => Dispatcher.BeginInvoke(ShowSettings));
        menu.Items.Add("启用 / 暂停划词", null, (_, _) => Dispatcher.BeginInvoke(() => ToggleSelection(!Settings.SelectionEnabled)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出拾句", null, (_, _) => Dispatcher.BeginInvoke(() => { Exiting = true; Shutdown(); }));
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Information, Text = "拾句 · 划词英语助手", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowMain);
    }

    private void ShowFixture()
    {
        var window = new Window { Title = "划词测试文档 · 拾句验证", Width = 720, Height = 360, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var panel = new StackPanel { Margin = new Thickness(30) };
        panel.Children.Add(new TextBlock { Text = "跨进程选区测试文档", FontSize = 22, Margin = new Thickness(0, 0, 0, 18) });
        var text = new TextBox { Text = "学习语言最好的方式，是每天用它表达真实的想法。", TextWrapping = TextWrapping.Wrap, FontSize = 22, IsReadOnly = true, Height = 100, Margin = new Thickness(0, 0, 0, 18) };
        panel.Children.Add(text);
        var select = new Button { Content = "选中示例句子", HorizontalAlignment = HorizontalAlignment.Left };
        select.Click += (_, _) => { window.Activate(); text.Focus(); text.SelectAll(); };
        panel.Children.Add(select);
        window.Content = panel;
        window.Closed += (_, _) => Shutdown();
        window.Show();
    }

    private async Task SelfTestAsync(string[] args)
    {
        try
        {
            var key = "local-self-test-密钥";
            var encrypted = SettingsStore.Protect(key);
            if (JsonSerializer.Serialize(new UserSettings(ProtectedKey: encrypted)).Contains(key) || SettingsStore.Api(new(ProtectedKey: encrypted)).ApiKey != key) throw new Exception("DPAPI failed");
            var result = "PASS Windows DPAPI roundtrip; API key absent from serialized settings";
            if (args.Contains("--fixture-probe"))
            {
                string? probeError = null;
                var probe = new Thread(() =>
                {
                    try
                    {
                        var fixture = Process.GetProcessesByName("SelectionFixture").Single();
                        var root = AutomationElement.FromHandle(fixture.MainWindowHandle);
                        var edit = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                        var selected = SelectionTextReader.Read(edit);
                        if (selected?.Text != "学习语言最好的方式，是每天用它表达真实的想法。") throw new Exception("unexpected fixture selection");
                        if (selected.LastBounds is null) throw new Exception("missing selection bounds");
                    }
                    catch (Exception ex) { probeError = ex.GetType().Name; }
                }) { IsBackground = true };
                probe.SetApartmentState(ApartmentState.MTA);
                probe.Start();
                if (!probe.Join(10000) || probeError is not null) throw new Exception("fixture capture failed");
                result += "; PASS cross-process UIA selection text and bounds from controlled fixture";
            }
            using (var model = new SessionViewModel(() => throw new Exception("Self-test must never access an API")))
            {
                model.SetContext("测试示例", "离线演示 · 自动测试", true);
                var oldRequest = model.TranslateAsync();
                await Task.Delay(30);
                model.SetContext("新选区", "自动测试");
                await oldRequest;
                if (model.Original != "新选区" || model.Translation != "" || model.IsBusy || model.IsReady) throw new Exception("Stale translation reached new selection");
                model.SetContext("测试示例", "离线演示 · 自动测试", true);
                await model.TranslateAsync();
                model.Question = "分析语法";
                var answer = model.AskAsync();
                await Task.Delay(40);
                model.StopCommand.Execute(null);
                await answer;
                if (model.Turns.Count != 0 || model.Question != "分析语法" || model.IsBusy) throw new Exception("Canceled question was lost");
                await model.AskAsync();
                if (model.Turns.Count != 2 || !model.Turns.All(t => t.Completed)) throw new Exception("Completed conversation was not retained");
            }
            result += "; PASS selection-switch cancellation, stopped-question recovery and conversation completion";
            var index = Array.IndexOf(args, "--report");
            if (index >= 0 && index + 1 < args.Length) File.WriteAllText(args[index + 1], result);
            Shutdown(0);
        }
        catch (Exception ex)
        {
            var index = Array.IndexOf(args, "--report");
            if (index >= 0 && index + 1 < args.Length) File.WriteAllText(args[index + 1], "FAIL " + ex.Message);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Exiting = true;
        lifetime?.Cancel();
        monitor?.Dispose();
        if (mainHandle != IntPtr.Zero) { Native.UnregisterHotKey(mainHandle, 1); Native.UnregisterHotKey(mainHandle, 2); }
        tray?.Dispose();
        Session?.Dispose();
        instance?.Dispose();
        base.OnExit(e);
    }
}
