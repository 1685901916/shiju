using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;

namespace SelectionAssistant.Windows;

internal sealed record CapturedSelection(string Text, string Source, double X, double Y, bool Explicit);

internal sealed class SelectionMonitor : IDisposable
{
    private sealed record CaptureRequest(bool Explicit, bool Deep);
    private readonly BlockingCollection<CaptureRequest> requests = new(1);
    private readonly Native.HookProc hookCallback;
    private readonly System.Threading.Timer debounce;
    private readonly System.Threading.Timer poll;
    private readonly Action<string>? diagnostic;
    private readonly Thread worker;
    private IntPtr hook;
    private volatile bool disposed;
    private string last = "";
    internal bool Enabled { get; set; } = true;
    internal bool HookAvailable => hook != IntPtr.Zero;
    internal event Action<CapturedSelection>? Selected;
    internal event Action? Dismiss;
    internal event Action<string>? Failed;

    internal SelectionMonitor(Action<string>? diagnostic = null)
    {
        this.diagnostic = diagnostic;
        debounce = new(_ => Queue(false, true), null, Timeout.Infinite, Timeout.Infinite);
        poll = new(_ => Queue(false, false), null, 650, 650);
        hookCallback = MouseHook;
        hook = Native.SetWindowsHookEx(14, hookCallback, Native.GetModuleHandle(null), 0);
        worker = new Thread(Work) { IsBackground = true, Name = "Selection UI Automation" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
    }

    private IntPtr MouseHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && !disposed && Enabled)
        {
            if ((int)message == 0x0202) debounce.Change(180, Timeout.Infinite); // Mouse up: preserve selection before showing UI.
            if ((int)message == 0x0201)
            {
                var mouse = Marshal.PtrToStructure<Native.MouseData>(data);
                if (!Native.IsOurWindow(Native.WindowFromPoint(mouse.Pt))) Dismiss?.Invoke();
            }
        }
        return Native.CallNextHookEx(hook, code, message, data);
    }

    internal void Capture() => Queue(true, true);
    private void Queue(bool explicitRequest, bool deep)
    {
        if (disposed || (!Enabled && !explicitRequest)) return;
        try { requests.TryAdd(new(explicitRequest, deep)); } catch (InvalidOperationException) { }
    }

    private void Work()
    {
        diagnostic?.Invoke("worker-ready");
        foreach (var request in requests.GetConsumingEnumerable())
        {
            if (disposed) break;
            try { ReadSelection(request.Explicit, request.Deep); }
            catch (Exception ex)
            {
                diagnostic?.Invoke("capture-error:" + ex.GetType().Name);
                if (request.Explicit) Failed?.Invoke("此应用未提供可读取的选区。请复制文字后按 Alt+Shift+V，或粘贴到主窗口。");
            }
        }
    }

    private void ReadSelection(bool explicitRequest, bool deep)
    {
        var foreground = Native.GetForegroundWindow();
        if (Native.IsOurWindow(foreground)) return;
        Native.GetWindowThreadProcessId(foreground, out var foregroundPid);
        diagnostic?.Invoke("capture-foreground:" + foregroundPid);
        Native.GetCursorPos(out var cursor);
        // Capture the foreground identity first; discard the read if the user switches apps mid-read.
        var focus = AutomationElement.FocusedElement;
        var candidates = new List<AutomationElement>();
        if (focus is not null) candidates.Add(focus);
        try { candidates.Add(AutomationElement.FromPoint(new Point(cursor.X, cursor.Y))); } catch (Exception) { }
        for (int i = 0; i < candidates.Count && i < 10; i++)
        {
            var item = candidates[i];
            if (item.Current.ProcessId != foregroundPid || item.Current.IsPassword) continue;
            if (TryRead(item, explicitRequest, foreground, out var capture)) { Emit(capture!); return; }
            var parent = TreeWalker.ControlViewWalker.GetParent(item);
            if (parent is not null && parent.Current.ProcessId == foregroundPid && !candidates.Contains(parent)) candidates.Add(parent);
        }
        // Some browsers expose selection on a Document rather than the focused child.
        if (deep)
        {
            var root = AutomationElement.FromHandle(foreground);
            var documents = root.FindAll(TreeScope.Descendants, new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));
            foreach (AutomationElement item in documents.Cast<AutomationElement>().Take(24))
                if (!item.Current.IsPassword && TryRead(item, explicitRequest, foreground, out var capture)) { Emit(capture!); return; }
        }
        last = "";
        diagnostic?.Invoke("no-selection");
        if (explicitRequest) Failed?.Invoke("没有读到选中文字。请先选择文字，或复制后按 Alt+Shift+V。");
    }

    private static bool TryRead(AutomationElement element, bool explicitRequest, IntPtr foreground, out CapturedSelection? captured)
    {
        captured = null;
        var selected = SelectionTextReader.Read(element);
        if (selected is null || Native.GetForegroundWindow() != foreground) return false;
        Native.GetCursorPos(out var cursor);
        double x = cursor.X, y = cursor.Y;
        if (selected.LastBounds is { } bounds) { x = bounds.Right; y = bounds.Bottom; }
        captured = new(selected.Text, Native.Title(foreground), x, y, explicitRequest);
        return true;
    }

    private void Emit(CapturedSelection captured)
    {
        if (!captured.Explicit && (!Enabled || captured.Text + captured.Source == last)) return;
        last = captured.Text + captured.Source;
        diagnostic?.Invoke("selection-read:" + captured.Text.Length);
        Selected?.Invoke(captured);
    }

    public void Dispose()
    {
        disposed = true;
        debounce.Dispose();
        poll.Dispose();
        if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
        requests.CompleteAdding();
    }
}
