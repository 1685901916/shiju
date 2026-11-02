using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace SelectionAssistant.Windows;

internal static class Native
{
    internal const int WM_HOTKEY = 0x0312, WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;
    internal delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseData { public Point Pt; public uint Mouse, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern IntPtr SetWindowsHookEx(int hook, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] internal static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // Windows 11 only (ignored elsewhere): rounded corners and a title bar that blends into the app canvas.
    internal static void Theme(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var canvas = (System.Windows.Media.Color)Application.Current.FindResource("CanvasColor");
        void Set(int attribute, int value) => DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
        Set(33, 2);                                                  // DWMWA_WINDOW_CORNER_PREFERENCE = round
        Set(34, 0xDEE3E4);                                           // DWMWA_BORDER_COLOR (0x00BBGGRR)
        Set(35, canvas.R | canvas.G << 8 | canvas.B << 16);          // DWMWA_CAPTION_COLOR
        Set(36, 0x1D1E1C);                                           // DWMWA_TEXT_COLOR
    }

    [StructLayout(LayoutKind.Sequential)] private struct JobLimits { public long UserTime, JobUserTime; public uint Flags; public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcesses; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct JobExtendedLimits { public JobLimits Basic; public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobExtendedLimits info, int length);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    private static IntPtr exitJob;

    // Puts this process in a kill-on-close job: everything it launches afterwards (the hidden Edge and its
    // helpers) inherits the job and is ended by Windows when the app exits, even after a crash.
    internal static void EndChildrenWithApp()
    {
        if (exitJob != IntPtr.Zero) return;
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return;
        var limits = new JobExtendedLimits { Basic = new JobLimits { Flags = 0x2000 } }; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<JobExtendedLimits>()) && AssignProcessToJobObject(job, GetCurrentProcess())) exitJob = job;
    }

    internal static bool IsOurWindow(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == Environment.ProcessId;
    }

    internal static string Title(IntPtr hwnd)
    {
        var text = new StringBuilder(512);
        GetWindowText(hwnd, text, text.Capacity);
        return text.ToString();
    }

    internal static void NoActivate(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000000 | 0x80);
        HwndSource.FromHwnd(handle)?.AddHook((IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
        {
            if (msg == WM_MOUSEACTIVATE) { handled = true; return (IntPtr)MA_NOACTIVATE; }
            return IntPtr.Zero;
        });
    }

    internal static void PlaceNear(Window window, double screenX, double screenY)
    {
        var point = new Point { X = (int)screenX, Y = (int)screenY };
        var monitor = MonitorFromPoint(point, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(monitor, ref info);
        GetDpiForMonitor(monitor, 0, out var dpi, out _);
        var scale = Math.Max(1, dpi / 96d);
        var width = (int)(window.Width * scale);
        var height = (int)(window.Height * scale);
        width = Math.Min(width, Math.Max(80, info.Work.Right - info.Work.Left - 16));
        height = Math.Min(height, Math.Max(40, info.Work.Bottom - info.Work.Top - 16));
        window.MinWidth = Math.Min(window.MinWidth, width / scale);
        window.MinHeight = Math.Min(window.MinHeight, height / scale);
        int x = Math.Clamp(point.X + 12, info.Work.Left + 8, Math.Max(info.Work.Left + 8, info.Work.Right - width - 8));
        int y = point.Y + 12;
        if (y + height > info.Work.Bottom - 8) y = point.Y - height - 12;
        y = Math.Max(info.Work.Top + 8, y);
        SetWindowPos(new WindowInteropHelper(window).Handle, new IntPtr(-1), x, y, width, height, 0x0010);
    }
}
