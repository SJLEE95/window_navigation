using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace TaskMonitor;

internal static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_APPWINDOW = 0x00040000;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    public const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3;
    public const uint ABE_TOP = 1, ABE_BOTTOM = 3;
    public const int ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string s);
    [DllImport("shell32.dll")] public static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    public static void MakeToolWindow(Window w, bool noActivate)
    {
        var h = new WindowInteropHelper(w).Handle;
        long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_TOOLWINDOW;
        ex &= ~WS_EX_APPWINDOW;
        if (noActivate) ex |= WS_EX_NOACTIVATE;
        SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex));
    }

    public static MONITORINFO MonitorOf(IntPtr hwnd)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref mi);
        return mi;
    }

    /// <summary>장치 픽셀 사각형을 WPF 논리 단위(DIP)로 변환</summary>
    public static Rect ToDip(Window w, RECT r)
    {
        var src = PresentationSource.FromVisual(w);
        var m = src?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var tl = m.Transform(new Point(r.Left, r.Top));
        var br = m.Transform(new Point(r.Right, r.Bottom));
        return new Rect(tl, br);
    }

    public static double DeviceScaleY(Window w)
    {
        var src = PresentationSource.FromVisual(w);
        return src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
    }

    /// <summary>바가 있는 모니터에서 다른 프로그램이 전체 화면(영상 재생 등)인지</summary>
    public static bool IsOtherAppFullscreen(IntPtr ourHwnd)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == ourHwnd) return false;
        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;
        var sb = new StringBuilder(64);
        GetClassName(fg, sb, sb.Capacity);
        string cls = sb.ToString();
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (!GetWindowRect(fg, out var r)) return false;
        var mon = MonitorOf(ourHwnd).rcMonitor;
        var fgMon = MonitorOf(fg).rcMonitor;
        if (fgMon.Left != mon.Left || fgMon.Top != mon.Top) return false;
        return r.Left <= mon.Left && r.Top <= mon.Top && r.Right >= mon.Right && r.Bottom >= mon.Bottom;
    }
}

/// <summary>화면 위/아래 가장자리 공간을 예약하는 AppBar (작업 표시줄처럼 다른 창이 가리지 않음)</summary>
internal sealed class AppBar : IDisposable
{
    private readonly Window _w;
    private readonly IntPtr _hwnd;
    private readonly uint _callback;
    private bool _registered;
    private uint _edge;
    private double _heightDip;
    public event Action<bool>? FullscreenChanged;

    public AppBar(Window w)
    {
        _w = w;
        _hwnd = new WindowInteropHelper(w).Handle;
        _callback = Native.RegisterWindowMessage("TaskMonitor.AppBar.Callback");
        HwndSource.FromHwnd(_hwnd)?.AddHook(Hook);
    }

    public bool IsRegistered => _registered;

    public void Dock(bool top, double heightDip)
    {
        _edge = top ? Native.ABE_TOP : Native.ABE_BOTTOM;
        _heightDip = heightDip;
        if (!_registered)
        {
            var abd = NewData();
            abd.uCallbackMessage = _callback;
            Native.SHAppBarMessage(Native.ABM_NEW, ref abd);
            _registered = true;
        }
        SetPos();
    }

    public void Undock()
    {
        if (!_registered) return;
        var abd = NewData();
        Native.SHAppBarMessage(Native.ABM_REMOVE, ref abd);
        _registered = false;
    }

    private Native.APPBARDATA NewData() => new() { cbSize = Marshal.SizeOf<Native.APPBARDATA>(), hWnd = _hwnd };

    private void SetPos()
    {
        var mon = Native.MonitorOf(_hwnd).rcMonitor;
        int hPx = (int)Math.Ceiling(_heightDip * Native.DeviceScaleY(_w));
        var abd = NewData();
        abd.uEdge = _edge;
        abd.rc = mon;
        if (_edge == Native.ABE_TOP) abd.rc.Bottom = mon.Top + hPx; else abd.rc.Top = mon.Bottom - hPx;
        Native.SHAppBarMessage(Native.ABM_QUERYPOS, ref abd);
        if (_edge == Native.ABE_TOP) abd.rc.Bottom = abd.rc.Top + hPx; else abd.rc.Top = abd.rc.Bottom - hPx;
        Native.SHAppBarMessage(Native.ABM_SETPOS, ref abd);
        Native.SetWindowPos(_hwnd, IntPtr.Zero, abd.rc.Left, abd.rc.Top, abd.rc.Width, abd.rc.Height,
                            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_registered && msg == (int)_callback)
        {
            int code = wParam.ToInt32();
            if (code == Native.ABN_POSCHANGED) SetPos();
            else if (code == Native.ABN_FULLSCREENAPP) FullscreenChanged?.Invoke(lParam != IntPtr.Zero);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose() => Undock();
}
