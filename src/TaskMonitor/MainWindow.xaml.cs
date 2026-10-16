using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using TaskMonitor.Core;

namespace TaskMonitor;

/// <summary>작업 창 (시안 B, 다크). 사이드바: 작업 / 바로가기 / 설정. 닫으면 숨기기만 하고 캡슐과 작업은 그대로.</summary>
public partial class MainWindow : Window
{
    private readonly MonitorController _c;
    private bool _forceClose;
    private string? _logKey;
    private bool _autoSelect;
    private bool _loadingSettings;

    public MainWindow(MonitorController c)
    {
        InitializeComponent();
        _c = c;
        DataContext = c;
        var s = c.Settings;
        if (s.MainWidth is > 600 && s.MainHeight is > 400)
        {
            Width = s.MainWidth.Value;
            Height = s.MainHeight.Value;
        }
        if (s.MainLeft is double l && s.MainTop is double t &&
            l >= SystemParameters.VirtualScreenLeft && t >= SystemParameters.VirtualScreenTop &&
            l < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
            t < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l;
            Top = t;
        }
        StateDirText.Text = Paths.Tasks;
        c.Updated += OnUpdated;
        c.Shortcuts.Items.CollectionChanged += (_, _) => UpdateShortcutsEmpty();
        UpdateShortcutsEmpty();
        Closing += OnClosing;
        SourceInitialized += (_, _) => UseDarkTitleBar();
        _autoSelect = true;
    }

    // ---------------------------------------------------------------- 다크 제목 표시줄 (Windows 10 20H1+ / 11)
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void UseDarkTitleBar()
    {
        try
        {
            var h = new WindowInteropHelper(this).Handle;
            int on = 1;
            if (DwmSetWindowAttribute(h, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(h, 19, ref on, sizeof(int));
            int caption = 0x00241E1B; // COLORREF(BGR) = #1B1E24 (Windows 11 에서만 적용)
            DwmSetWindowAttribute(h, 35, ref caption, sizeof(int));
        }
        catch { /* 지원하지 않는 Windows: 기본 제목 표시줄 */ }
    }

    // ---------------------------------------------------------------- 페이지
    /// <param name="page">"tasks" | "shortcuts" | "settings"</param>
    public void ShowPage(string page)
    {
        var nav = page switch { "shortcuts" => NavShortcuts, "settings" => NavSettings, _ => NavTasks };
        if (nav.IsChecked == true) ApplyPage(page);
        else nav.IsChecked = true; // Nav_Checked → ApplyPage
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string page }) ApplyPage(page);
    }

    private void ApplyPage(string page)
    {
        if (PageTasks is null || PageShortcuts is null || PageSettings is null) return; // InitializeComponent 도중
        PageTasks.Visibility = page == "tasks" ? Visibility.Visible : Visibility.Collapsed;
        PageShortcuts.Visibility = page == "shortcuts" ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "shortcuts") _c.Shortcuts.RefreshExists();
        if (page == "settings") LoadSettings();
        if (page == "tasks") RefreshDetail();
    }

    private TaskItem? Selected => List.SelectedItem as TaskItem;

    public void Select(string id)
    {
        var item = _c.Items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
        ShowPage("tasks");
        _autoSelect = false;
        List.SelectedItem = item;
        List.ScrollIntoView(item);
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var s = _c.Settings;
        if (WindowState == WindowState.Normal)
        {
            s.MainLeft = Left; s.MainTop = Top; s.MainWidth = Width; s.MainHeight = Height;
        }
        s.Save();
        if (!_forceClose)
        {
            e.Cancel = true;
            Hide();
        }
    }

    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    // ---------------------------------------------------------------- 작업
    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is not null) _autoSelect = false;
        _logKey = null;
        RefreshDetail();
    }

    private void OnUpdated()
    {
        // 처음 작업이 보이면 맨 위 작업을 자동 선택 (사용자가 고른 뒤에는 건드리지 않음)
        if (_autoSelect && List.SelectedItem is null && List.Items.Count > 0)
        {
            List.SelectedIndex = 0;
            _autoSelect = false;
        }
        LastErrorText.Text = _c.LastError;
        LastErrorText.Visibility = _c.LastError.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (IsVisible && PageTasks.Visibility == Visibility.Visible) RefreshDetail();
    }

    private void RefreshDetail()
    {
        var item = Selected;
        bool has = item is not null;
        Detail.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        EmptyHint.Text = _c.Items.Count == 0 ? "작업 없음" : "작업을 고르세요";
        if (!ReferenceEquals(Detail.DataContext, item)) Detail.DataContext = item;
        if (item is null) return;
        var v = item.V;

        NoResults.Visibility = v.Results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnOpenResult.IsEnabled = v.Results.Any(r => !r.IsDir && r.Exists);
        BtnOpenFolder.IsEnabled = v.Results.Any(r => r.Exists) || Directory.Exists(v.Workdir);
        BtnWorkdir.IsEnabled = Directory.Exists(v.Workdir);
        BtnOpenLog.IsEnabled = !string.IsNullOrEmpty(v.LogPath) && File.Exists(v.LogPath);
        BtnCancel.IsEnabled = v.CancelSupported && !v.CancelRequested;
        BtnCancel.Content = !v.CancelSupported && !v.IsTerminal ? "중단 요청 (미지원)" : v.CancelRequested ? "중단 요청됨" : "중단 요청";
        BtnCancel.Visibility = v.IsTerminal ? Visibility.Collapsed : Visibility.Visible;
        BtnArchive.IsEnabled = v.CanArchive;

        // 로그는 파일이 바뀌었을 때만 다시 읽음
        string key = v.LogPath ?? "";
        try
        {
            if (File.Exists(key)) { var fi = new FileInfo(key); key += "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks; }
        }
        catch { }
        if (key == _logKey) return;
        _logKey = key;
        LogTitleRun.ToolTip = string.IsNullOrEmpty(v.LogPath) ? "로그 경로 기록 없음" : v.LogPath;
        string tail = TaskFiles.ReadTail(v.LogPath, 80, out bool exists);
        if (!string.IsNullOrEmpty(v.LogPath) && !exists) tail = "(로그 파일이 아직 없습니다)";
        bool atEnd = LogBox.VerticalOffset + LogBox.ViewportHeight >= LogBox.ExtentHeight - 4;
        LogBox.Text = tail;
        if (atEnd) LogBox.ScrollToEnd();
        string errs = Presenter.ErrorLinesFromLog(tail);
        LogErrors.Text = errs.Length > 0 ? "로그의 오류 관련 줄:\n" + errs : "";
        LogErrors.Visibility = errs.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // 상세 영역 안의 오류·로그 상자가 마우스 휠을 가로채지 않도록 (로그 상자 자체 스크롤은 유지)
    private void Detail_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (LogBox.IsMouseOver) return;
        Detail.ScrollToVerticalOffset(Detail.VerticalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    private static void ShellOpen(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex)
        {
            if (File.Exists(path))
            {
                try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose(); return; }
                catch { }
            }
            MessageBox.Show($"열지 못했습니다: {path}\n{ex.Message}", "TaskMonitor");
        }
    }

    private static void ShowInFolder(string path)
    {
        if (Directory.Exists(path) && !File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
        else if (File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
        else
        {
            string? parent = System.IO.Path.GetDirectoryName(path);
            if (parent is not null && Directory.Exists(parent))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{parent}\"") { UseShellExecute = true })?.Dispose();
            else MessageBox.Show($"경로가 없습니다: {path}", "TaskMonitor");
        }
    }

    private void OpenResult_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string p }) ShellOpen(p);
    }

    private void OpenResultFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string p }) ShowInFolder(p);
    }

    private void OpenFirstResult_Click(object sender, RoutedEventArgs e)
    {
        var r = Selected?.V.Results.FirstOrDefault(x => !x.IsDir && x.Exists);
        if (r is not null) ShellOpen(r.Path);
    }

    private void OpenFirstFolder_Click(object sender, RoutedEventArgs e)
    {
        var v = Selected?.V;
        if (v is null) return;
        var dir = v.Results.FirstOrDefault(x => x.IsDir && x.Exists);
        var file = v.Results.FirstOrDefault(x => x.Exists);
        if (dir is not null) ShowInFolder(dir.Path);
        else if (file is not null) ShowInFolder(file.Path);
        else if (Directory.Exists(v.Workdir)) ShowInFolder(v.Workdir);
    }

    private void OpenWorkdir_Click(object sender, RoutedEventArgs e)
    {
        var v = Selected?.V;
        if (v is not null && Directory.Exists(v.Workdir)) ShowInFolder(v.Workdir);
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        var p = Selected?.V.LogPath;
        if (!string.IsNullOrEmpty(p)) ShellOpen(p);
    }

    private void OpenStateFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Paths.Tasks);
        ShowInFolder(Paths.Tasks);
    }

    private void ExitMonitor_Click(object sender, RoutedEventArgs e)
    {
        var ok = MessageBox.Show(this, "모니터를 종료할까요? (실행 중인 작업은 계속 돕니다)",
            "모니터 종료", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok == MessageBoxResult.Yes) App.Current.ExitApp();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        var v = Selected?.V;
        if (v is null || !v.CancelSupported) return;
        var ok = MessageBox.Show(this,
            $"'{v.Name}' 작업에 중단을 요청할까요?\n\n모니터는 중단 요청 파일만 만듭니다. 작업이 요청을 확인하고 스스로 정리한 뒤 멈추며, 프로세스를 강제로 종료하지 않습니다.",
            "중단 요청", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;
        try { TaskFiles.RequestCancel(v.Id); }
        catch (Exception ex) { MessageBox.Show("중단 요청 파일을 만들지 못했습니다: " + ex.Message, "TaskMonitor"); }
        _c.Tick();
    }

    private void Archive_Click(object sender, RoutedEventArgs e)
    {
        var v = Selected?.V;
        if (v is null || !v.CanArchive) return;
        var ok = MessageBox.Show(this,
            $"'{v.Name}' 기록을 목록에서 숨길까요?\n\n상태 파일은 지우지 않고 archive 폴더로 옮깁니다. 결과 파일과 로그는 그대로입니다.",
            "목록에서 숨기기", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;
        try { TaskFiles.Archive(v.Id); }
        catch (Exception ex) { MessageBox.Show("옮기지 못했습니다: " + ex.Message, "TaskMonitor"); }
        _c.Tick();
    }

    // ---------------------------------------------------------------- 바로가기
    private void UpdateShortcutsEmpty() =>
        ShortcutsEmpty.Visibility = _c.Shortcuts.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private static ShortcutVm? VmOf(object sender) => (sender as FrameworkElement)?.Tag as ShortcutVm;

    private void AddPaths(IEnumerable<string> paths)
    {
        var failed = _c.Shortcuts.Add(paths);
        if (failed.Count > 0)
            MessageBox.Show(this, "등록하지 못했습니다 (경로 없음 또는 지원하지 않는 형식):\n" + string.Join("\n", failed), "TaskMonitor");
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
        if (dlg.ShowDialog(this) == true) AddPaths(dlg.FileNames);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "바로가기로 등록할 폴더", Multiselect = true };
        if (dlg.ShowDialog(this) == true) AddPaths(dlg.FolderNames);
    }

    private void AddUrl_Click(object sender, RoutedEventArgs e)
    {
        string u = UrlBox.Text.Trim();
        if (u.Length == 0) return;
        if (!u.Contains("://")) u = "https://" + u;
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            MessageBox.Show(this, "http:// 또는 https:// 웹 주소만 등록할 수 있습니다.", "TaskMonitor");
            return;
        }
        AddPaths(new[] { u });
        UrlBox.Text = "";
    }

    private void Shortcuts_DragEnter(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText);
        e.Effects = ok ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void Shortcuts_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            AddPaths(files);
        else if (e.Data.GetData(DataFormats.UnicodeText) is string text &&
                 (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            AddPaths(new[] { text.Trim() });
    }

    private void ShortcutRun_Click(object sender, RoutedEventArgs e)
    {
        if (VmOf(sender) is { } vm && _c.Shortcuts.Launch(vm) is string err)
            MessageBox.Show(this, err, "TaskMonitor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShortcutReveal_Click(object sender, RoutedEventArgs e)
    {
        if (VmOf(sender) is { } vm) ShortcutStore.RevealInExplorer(vm.Item);
    }

    private void ShortcutUp_Click(object sender, RoutedEventArgs e)
    {
        if (VmOf(sender) is { } vm) _c.Shortcuts.Move(vm, -1);
    }

    private void ShortcutDown_Click(object sender, RoutedEventArgs e)
    {
        if (VmOf(sender) is { } vm) _c.Shortcuts.Move(vm, +1);
    }

    private void ShortcutRemove_Click(object sender, RoutedEventArgs e)
    {
        if (VmOf(sender) is { } vm) _c.Shortcuts.Remove(vm); // 바로가기만 지움. 대상 파일·폴더는 그대로
    }

    // ---------------------------------------------------------------- 설정
    private void LoadSettings()
    {
        _loadingSettings = true;
        try
        {
            var s = _c.Settings;
            foreach (var rb in PlacementGrid.Children.OfType<RadioButton>())
                rb.IsChecked = rb.Tag is string t && t == s.Placement.ToString();
            ChkTopmost.IsChecked = s.Topmost;
            ChkFullscreen.IsChecked = s.HideOnFullscreen;
            ChkAuto.IsChecked = s.AutoDetect;
            ChkAutostart.IsChecked = Autostart.IsEnabled;
        }
        finally { _loadingSettings = false; }
    }

    private void Placement_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<BarPlacement>(tag, out var p))
        {
            if (App.Current.Bar is { } bar) bar.SetPlacement(p);
            else { _c.Settings.Placement = p; _c.Settings.Save(); }
        }
    }

    private void Setting_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        var s = _c.Settings;
        if (sender == ChkTopmost)
        {
            bool on = ChkTopmost.IsChecked == true;
            if (App.Current.Bar is { } bar) bar.SetTopmost(on);
            else { s.Topmost = on; s.Save(); }
        }
        else if (sender == ChkFullscreen)
        {
            s.HideOnFullscreen = ChkFullscreen.IsChecked == true;
            s.Save();
        }
        else if (sender == ChkAuto)
        {
            s.AutoDetect = ChkAuto.IsChecked == true;
            s.Save();
            _c.Tick();
        }
        else if (sender == ChkAutostart)
        {
            try { Autostart.Set(ChkAutostart.IsChecked == true); }
            catch (Exception ex) { MessageBox.Show(this, "시작 프로그램 등록에 실패했습니다: " + ex.Message, "TaskMonitor"); }
            ChkAutostart.IsChecked = Autostart.IsEnabled;
        }
    }
}
