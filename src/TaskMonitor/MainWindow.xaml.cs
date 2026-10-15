using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using TaskMonitor.Core;

namespace TaskMonitor;

/// <summary>작업 목록 + 선택한 작업 상세. 닫으면 숨기기만 하고 상단 바와 작업은 그대로.</summary>
public partial class MainWindow : Window
{
    private readonly MonitorController _c;
    private bool _forceClose;
    private string? _logKey;

    public MainWindow(MonitorController c)
    {
        InitializeComponent();
        _c = c;
        DataContext = c;
        var s = c.Settings;
        if (s.MainWidth is > 400 && s.MainHeight is > 300)
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
        StateDirRun.Text = Paths.Tasks;
        c.Updated += OnUpdated;
        Closing += OnClosing;
        Loaded += (_, _) => { if (List.SelectedItem is null && List.Items.Count > 0) List.SelectedIndex = 0; };
    }

    private TaskItem? Selected => List.SelectedItem as TaskItem;

    public void Select(string id)
    {
        var item = _c.Items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
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

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _logKey = null;
        RefreshDetail();
    }

    private void OnUpdated()
    {
        ErrorRun.Text = _c.LastError.Length > 0 ? "  " + _c.LastError : "";
        if (IsVisible) RefreshDetail();
    }

    private void RefreshDetail()
    {
        var item = Selected;
        bool has = item is not null;
        Detail.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
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
        LogPathRun.Text = string.IsNullOrEmpty(v.LogPath) ? "(로그 경로 기록 없음)" : v.LogPath;
        string tail = TaskFiles.ReadTail(v.LogPath, 80, out bool exists);
        if (!string.IsNullOrEmpty(v.LogPath) && !exists) tail = "(로그 파일이 아직 없습니다)";
        bool atEnd = LogBox.VerticalOffset + LogBox.ViewportHeight >= LogBox.ExtentHeight - 4;
        LogBox.Text = tail;
        if (atEnd) LogBox.ScrollToEnd();
        string errs = Presenter.ErrorLinesFromLog(tail);
        LogErrors.Text = errs.Length > 0 ? "로그의 오류 관련 줄:\n" + errs : "";
        LogErrors.Visibility = errs.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- actions
    private static void ShellOpen(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            if (File.Exists(path))
            {
                try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true }); return; }
                catch { }
            }
            MessageBox.Show($"열지 못했습니다: {path}\n{ex.Message}", "TaskMonitor");
        }
    }

    private static void ShowInFolder(string path)
    {
        if (Directory.Exists(path) && !File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        else if (File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        else
        {
            string? parent = System.IO.Path.GetDirectoryName(path);
            if (parent is not null && Directory.Exists(parent))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{parent}\"") { UseShellExecute = true });
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
}
