using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using TaskMonitor.Core;

namespace TaskMonitor;

/// <summary>화면 귀퉁이(또는 위/아래 가장자리)에 붙는 한 줄짜리 상태 바.</summary>
public partial class BarWindow : Window
{
    private const double DockHeight = 30;
    private readonly MonitorController _c;
    private AppBar? _appBar;
    private IntPtr _hwnd;
    private bool _hiddenForFullscreen;
    private bool _closing;

    public BarWindow(MonitorController c)
    {
        InitializeComponent();
        _c = c;
        DataContext = c;
        // 지난번 위치의 모니터에 다시 뜨도록 대략적인 위치를 먼저 지정
        if (c.Settings.BarX is double x && c.Settings.BarY is double y && IsOnSomeScreen(x, y))
        {
            Left = x;
            Top = y;
        }
        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => { if (!IsDocked) Reposition(); };
        c.Updated += OnUpdated;
        Closing += (_, e) => { if (!_closing) e.Cancel = true; };
    }

    private static bool IsOnSomeScreen(double x, double y) =>
        x >= SystemParameters.VirtualScreenLeft && y >= SystemParameters.VirtualScreenTop &&
        x < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
        y < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;

    private bool IsDocked => _c.Settings.Placement is BarPlacement.DockTop or BarPlacement.DockBottom;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.MakeToolWindow(this, noActivate: true); // Alt+Tab·작업 표시줄에 안 보이고 포커스를 빼앗지 않음
        _appBar = new AppBar(this);
        Topmost = _c.Settings.Topmost;
        ApplyPlacement();
    }

    private void ApplyPlacement()
    {
        if (_appBar is null) return;
        if (IsDocked)
        {
            SizeToContent = SizeToContent.Manual;
            Root.CornerRadius = new CornerRadius(0);
            Height = DockHeight;
            _appBar.Dock(_c.Settings.Placement == BarPlacement.DockTop, DockHeight);
        }
        else
        {
            _appBar.Undock();
            Root.CornerRadius = new CornerRadius(6);
            SizeToContent = SizeToContent.WidthAndHeight;
            Reposition();
        }
    }

    private Rect WorkArea() => Native.ToDip(this, Native.MonitorOf(_hwnd).rcWork);

    private void Reposition()
    {
        if (_hwnd == IntPtr.Zero || IsDocked) return;
        var wa = WorkArea();
        MaxWidth = Math.Max(200, wa.Width * 0.95);
        double w = ActualWidth > 0 ? ActualWidth : 300;
        double h = ActualHeight > 0 ? ActualHeight : 30;
        var p = _c.Settings.Placement;
        Left = p is BarPlacement.TopLeft or BarPlacement.BottomLeft ? wa.Left : wa.Right - w;
        Top = p is BarPlacement.TopLeft or BarPlacement.TopRight ? wa.Top : wa.Bottom - h;
        _c.Settings.BarX = Left;
        _c.Settings.BarY = Top;
    }

    private void OnUpdated()
    {
        if (_hwnd == IntPtr.Zero) return;
        bool fs = _c.Settings.HideOnFullscreen && Native.IsOtherAppFullscreen(_hwnd);
        if (fs && IsVisible)
        {
            _hiddenForFullscreen = true;
            Hide();
        }
        else if (!fs && _hiddenForFullscreen)
        {
            _hiddenForFullscreen = false;
            Show();
            if (!IsDocked) Reposition();
        }
    }

    private void Handle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsDocked || e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { return; }
        // 놓은 위치에서 가장 가까운 귀퉁이에 붙임 (다른 모니터로 옮기면 그 모니터 기준)
        var wa = WorkArea();
        double cx = Left + ActualWidth / 2, cy = Top + ActualHeight / 2;
        bool left = cx < wa.Left + wa.Width / 2;
        bool top = cy < wa.Top + wa.Height / 2;
        _c.Settings.Placement = top ? (left ? BarPlacement.TopLeft : BarPlacement.TopRight)
                                    : (left ? BarPlacement.BottomLeft : BarPlacement.BottomRight);
        Reposition();
        _c.Settings.Save();
    }

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id }) App.Current.ShowMain(id);
    }

    private void OpenMain_Click(object sender, RoutedEventArgs e) => App.Current.ShowMain(null);

    private void Menu_Opened(object sender, RoutedEventArgs e)
    {
        var p = _c.Settings.Placement;
        PosTopRight.IsChecked = p == BarPlacement.TopRight;
        PosTopLeft.IsChecked = p == BarPlacement.TopLeft;
        PosBottomRight.IsChecked = p == BarPlacement.BottomRight;
        PosBottomLeft.IsChecked = p == BarPlacement.BottomLeft;
        PosDockTop.IsChecked = p == BarPlacement.DockTop;
        PosDockBottom.IsChecked = p == BarPlacement.DockBottom;
        MiTopmost.IsChecked = _c.Settings.Topmost;
        MiFullscreen.IsChecked = _c.Settings.HideOnFullscreen;
        MiRecent.IsChecked = _c.Settings.ShowRecentFinished;
        MiAuto.IsChecked = _c.Settings.AutoDetect;
        MiAutostart.IsChecked = Autostart.IsEnabled;
    }

    private void Placement_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && Enum.TryParse<BarPlacement>(tag, out var p))
        {
            _c.Settings.Placement = p;
            ApplyPlacement();
            _c.Settings.Save();
        }
    }

    private void Topmost_Click(object sender, RoutedEventArgs e)
    {
        _c.Settings.Topmost = !_c.Settings.Topmost;
        Topmost = _c.Settings.Topmost;
        _c.Settings.Save();
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        _c.Settings.HideOnFullscreen = !_c.Settings.HideOnFullscreen;
        _c.Settings.Save();
    }

    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        _c.Settings.ShowRecentFinished = !_c.Settings.ShowRecentFinished;
        _c.Settings.Save();
        _c.Tick();
    }

    private void Auto_Click(object sender, RoutedEventArgs e)
    {
        _c.Settings.AutoDetect = !_c.Settings.AutoDetect;
        _c.Settings.Save();
        _c.Tick();
    }

    private void Autostart_Click(object sender, RoutedEventArgs e)
    {
        try { Autostart.Set(!Autostart.IsEnabled); }
        catch (Exception ex) { MessageBox.Show("시작 프로그램 등록에 실패했습니다: " + ex.Message, "TaskMonitor"); }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Paths.Tasks);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Paths.Root}\"") { UseShellExecute = true });
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => App.Current.ExitApp();

    public void Shutdown()
    {
        if (_closing) return;
        _closing = true;
        _appBar?.Dispose();
        Close();
    }
}
