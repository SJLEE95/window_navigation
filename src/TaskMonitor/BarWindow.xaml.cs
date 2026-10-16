using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using TaskMonitor.Core;

namespace TaskMonitor;

/// <summary>시안 B: 화면 위 가운데(기본)에 떠 있는 다크 캡슐. 바로가기 패널과 작업 진행 링.</summary>
public partial class BarWindow : Window
{
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
        if (c.Settings.BarX is double x && c.Settings.BarY is double y && IsOnSomeScreen(x, y))
        {
            Left = x;
            Top = y;
        }
        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => Reposition();
        c.Updated += OnUpdated;
        Closing += (_, e) => { if (!_closing) e.Cancel = true; };
        AppsPopup.Closed += (_, _) => _c.Shortcuts.RefreshExists();
    }

    private static bool IsOnSomeScreen(double x, double y) =>
        x >= SystemParameters.VirtualScreenLeft && y >= SystemParameters.VirtualScreenTop &&
        x < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
        y < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;

    private BarPlacement P => _c.Settings.Placement;
    private bool IsBottom => P is BarPlacement.BottomLeft or BarPlacement.BottomCenter or BarPlacement.BottomRight or BarPlacement.DockBottom;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.MakeToolWindow(this, noActivate: true); // Alt+Tab·작업 표시줄에 안 보이고 포커스를 빼앗지 않음
        _appBar = new AppBar(this);
        _appBar.Undock(); // 캡슐 디자인은 공간 예약(AppBar)을 쓰지 않음
        if (P is BarPlacement.DockTop) _c.Settings.Placement = BarPlacement.TopCenter;
        if (P is BarPlacement.DockBottom) _c.Settings.Placement = BarPlacement.BottomCenter;
        Topmost = _c.Settings.Topmost;
        Reposition();
    }

    private Rect WorkArea() => Native.ToDip(this, Native.MonitorOf(_hwnd).rcWork);

    private void Reposition()
    {
        if (_hwnd == IntPtr.Zero) return;
        var wa = WorkArea();
        MaxWidth = Math.Max(240, wa.Width * 0.95);
        double w = ActualWidth > 0 ? ActualWidth : 360;
        double h = ActualHeight > 0 ? ActualHeight : 62;
        Left = P switch
        {
            BarPlacement.TopLeft or BarPlacement.BottomLeft => wa.Left,
            BarPlacement.TopRight or BarPlacement.BottomRight => wa.Right - w,
            _ => wa.Left + (wa.Width - w) / 2,
        };
        Top = IsBottom ? wa.Bottom - h : wa.Top;
        AppsPopup.Placement = IsBottom ? PlacementMode.Top : PlacementMode.Bottom;
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
            AppsPopup.IsOpen = false;
            Hide();
        }
        else if (!fs && _hiddenForFullscreen)
        {
            _hiddenForFullscreen = false;
            Show();
            Reposition();
        }
    }

    // 캡슐의 빈 곳을 끌면 이동, 놓으면 가장 가까운 자리(왼쪽/가운데/오른쪽 × 위/아래)에 붙음
    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { return; }
        var wa = WorkArea();
        double cx = Left + ActualWidth / 2, cy = Top + ActualHeight / 2;
        double third = wa.Width / 3;
        int col = cx < wa.Left + third ? 0 : cx < wa.Left + 2 * third ? 1 : 2;
        bool top = cy < wa.Top + wa.Height / 2;
        _c.Settings.Placement = (top, col) switch
        {
            (true, 0) => BarPlacement.TopLeft,
            (true, 1) => BarPlacement.TopCenter,
            (true, _) => BarPlacement.TopRight,
            (false, 0) => BarPlacement.BottomLeft,
            (false, 1) => BarPlacement.BottomCenter,
            _ => BarPlacement.BottomRight,
        };
        Reposition();
        _c.Settings.Save();
    }

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id }) App.Current.ShowMain(id);
    }

    private void OpenMain_Click(object sender, RoutedEventArgs e) => App.Current.ShowMain(null);

    private void ManageShortcuts_Click(object sender, RoutedEventArgs e)
    {
        AppsPopup.IsOpen = false;
        App.Current.ShowMain(null, page: "shortcuts");
    }

    // ---------------------------------------------------------------- 바로가기 패널
    private void AppsButton_Click(object sender, RoutedEventArgs e)
    {
        AppsPopup.StaysOpen = _c.Shortcuts.Pinned;
        AppsPopup.IsOpen = !AppsPopup.IsOpen;
    }

    private static ShortcutVm? VmOf(object sender) => (sender as FrameworkElement)?.Tag as ShortcutVm;

    private void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        if (VmOf(sender) is not { } vm) return;
        if (_c.Shortcuts.Launch(vm) is string err)
        {
            MessageBox.Show(err, "TaskMonitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!_c.Shortcuts.Pinned) AppsPopup.IsOpen = false;
    }

    private void ShortcutOpen_Click(object sender, RoutedEventArgs e) => Shortcut_Click(sender, e);

    private void ShortcutReveal_Click(object sender, RoutedEventArgs e)
    {
        if (VmOf(sender) is { } vm) ShortcutStore.RevealInExplorer(vm.Item);
    }

    private void ShortcutLeft_Click(object sender, RoutedEventArgs e)
    {
        if (VmOf(sender) is { } vm) _c.Shortcuts.Move(vm, -1);
    }

    private void ShortcutRight_Click(object sender, RoutedEventArgs e)
    {
        if (VmOf(sender) is { } vm) _c.Shortcuts.Move(vm, +1);
    }

    private void ShortcutRemove_Click(object sender, RoutedEventArgs e)
    {
        if (VmOf(sender) is { } vm) _c.Shortcuts.Remove(vm); // 바로가기만 지움. 대상 파일은 그대로
    }

    private void AddShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.ContextMenu is { } m)
        {
            m.PlacementTarget = b;
            m.IsOpen = true;
        }
    }

    private void AddFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "바로가기로 등록할 파일·앱",
            Multiselect = true,
            DereferenceLinks = false, // .lnk 를 그대로 등록 (원래 인자·시작 폴더 유지)
            Filter = "앱·바로가기 (*.exe;*.lnk;*.url;*.bat;*.cmd)|*.exe;*.lnk;*.url;*.bat;*.cmd|모든 파일 (*.*)|*.*",
        };
        if (dlg.ShowDialog() == true) AddPaths(dlg.FileNames);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "바로가기로 등록할 폴더", Multiselect = true };
        if (dlg.ShowDialog() == true) AddPaths(dlg.FolderNames);
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        var failed = _c.Shortcuts.Add(paths);
        if (failed.Count > 0)
            MessageBox.Show("등록하지 못했습니다 (경로 없음):\n" + string.Join("\n", failed), "TaskMonitor");
    }

    // 파일·폴더·.lnk·주소를 캡슐이나 패널에 끌어다 놓기
    private void Root_DragEnter(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText);
        e.Effects = ok ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void Root_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            AddPaths(files);
        else if (e.Data.GetData(DataFormats.UnicodeText) is string text &&
                 (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            AddPaths(new[] { text.Trim() });
        AppsPopup.StaysOpen = _c.Shortcuts.Pinned;
        AppsPopup.IsOpen = true;
    }

    // ---------------------------------------------------------------- 우클릭 메뉴
    private void Menu_Opened(object sender, RoutedEventArgs e)
    {
        PosTopCenter.IsChecked = P == BarPlacement.TopCenter;
        PosTopLeft.IsChecked = P == BarPlacement.TopLeft;
        PosTopRight.IsChecked = P == BarPlacement.TopRight;
        PosBottomCenter.IsChecked = P == BarPlacement.BottomCenter;
        PosBottomLeft.IsChecked = P == BarPlacement.BottomLeft;
        PosBottomRight.IsChecked = P == BarPlacement.BottomRight;
        MiTopmost.IsChecked = _c.Settings.Topmost;
        MiFullscreen.IsChecked = _c.Settings.HideOnFullscreen;
        MiAuto.IsChecked = _c.Settings.AutoDetect;
        MiAutostart.IsChecked = Autostart.IsEnabled;
    }

    private void Placement_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && Enum.TryParse<BarPlacement>(tag, out var p)) SetPlacement(p);
    }

    public void SetPlacement(BarPlacement p)
    {
        _c.Settings.Placement = p;
        Reposition();
        _c.Settings.Save();
    }

    private void Topmost_Click(object sender, RoutedEventArgs e) => SetTopmost(!_c.Settings.Topmost);

    public void SetTopmost(bool on)
    {
        _c.Settings.Topmost = on;
        Topmost = on;
        _c.Settings.Save();
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        _c.Settings.HideOnFullscreen = !_c.Settings.HideOnFullscreen;
        _c.Settings.Save();
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

    private void Exit_Click(object sender, RoutedEventArgs e) => App.Current.ExitApp();

    public void Shutdown()
    {
        if (_closing) return;
        _closing = true;
        AppsPopup.IsOpen = false;
        _appBar?.Dispose();
        Close();
    }
}
