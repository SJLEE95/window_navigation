using System.Threading;
using System.Windows;
using TaskMonitor.Core;

namespace TaskMonitor;

public partial class App : Application
{
    private const string MutexName = @"Local\TaskMonitor.SingleInstance";
    private const string ShowEventName = @"Local\TaskMonitor.ShowMain";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private MainWindow? _main;
    private BarWindow? _bar;

    public MonitorController Controller { get; private set; } = null!;

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? dir = null;
        bool show = false;
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--dir" && i + 1 < e.Args.Length) dir = e.Args[++i];
            else if (e.Args[i] == "--show") show = true;
        }
        Paths.SetRoot(dir);

        // 상태 폴더마다 하나만 실행. 이미 떠 있으면 그 창을 보여주고 종료.
        string suffix = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(Paths.Root.ToLowerInvariant())))[..12];
        _mutex = new Mutex(true, MutexName + "." + suffix, out bool first);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName + "." + suffix);
        if (!first)
        {
            _showEvent.Set();
            Shutdown();
            return;
        }
        var t = new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.InvokeAsync(() => ShowMain(null));
        }) { IsBackground = true, Name = "ShowMainListener" };
        t.Start();

        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show("예상하지 못한 오류가 발생했습니다. 모니터만 영향을 받으며 작업은 계속 실행됩니다.\n\n" + ex.Exception.Message,
                            "TaskMonitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            ex.Handled = true;
        };

        Directory.CreateDirectory(Paths.Tasks);
        Controller = new MonitorController(AppSettings.Load());
        _bar = new BarWindow(Controller);
        _bar.Show();
        Controller.Start();
        if (show) ShowMain(null);
    }

    public BarWindow? Bar => _bar;

    /// <param name="page">"tasks"(기본) | "shortcuts" | "settings"</param>
    public void ShowMain(string? selectTaskId, string? page = null)
    {
        _main ??= new MainWindow(Controller);
        if (!_main.IsVisible) _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        if (page is not null) _main.ShowPage(page);
        if (selectTaskId is not null) _main.Select(selectTaskId);
    }

    public void ExitApp()
    {
        Controller.Settings.Save();
        _bar?.Shutdown();
        _main?.ForceClose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _bar?.Shutdown();
        try { _mutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
