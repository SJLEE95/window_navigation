using System.Text.Json;
using Microsoft.Win32;
using TaskMonitor.Core;

namespace TaskMonitor;

public enum BarPlacement { TopRight, TopLeft, BottomRight, BottomLeft, DockTop, DockBottom, TopCenter, BottomCenter }

public sealed class AppSettings
{
    /// <summary>화면 구성 버전. 2 = 시안 B(다크 캡슐). 예전 설정 파일은 처음 한 번 캡슐 기본값으로 옮김.</summary>
    public int UiVersion { get; set; }  // 파일에 없으면 0 → Load 에서 마이그레이션
    public const int CurrentUiVersion = 2;
    public BarPlacement Placement { get; set; } = BarPlacement.TopCenter;
    public bool Topmost { get; set; } = true;
    public bool HideOnFullscreen { get; set; } = true;
    public bool ShowRecentFinished { get; set; } = true;
    public int RecentFinishedMinutes { get; set; } = 10;
    public int MaxChips { get; set; } = 3;
    public ShortcutBrowser WebBrowser { get; set; } = ShortcutBrowser.Chrome;
    /// <summary>상태 파일을 쓰지 않는 Python 작업도 자동으로 찾기</summary>
    public bool AutoDetect { get; set; } = true;
    public List<string> AutoDetectProcessNames { get; set; } = new() { "python", "pythonw" };
    public int AutoDetectMinAgeSeconds { get; set; } = 15;
    public int AutoDetectStaleSeconds { get; set; } = 300;
    public List<string> AutoDetectExclude { get; set; } = new()
    {
        "ipykernel", "jupyter", "debugpy", "pydevd", " -m pip", "\\pip\\", "language_server", "pylsp", "jedi",
        " -m black", " -m ruff", " -m mypy", " -m flake8", " -m isort", " -m pylint", "multiprocessing",
        "taskstate.py", "\\.vscode\\", "\\extensions\\ms-python", "\\Scripts\\conda", "streamlit",
    };
    public double? BarX { get; set; }
    public double? BarY { get; set; }
    public double? MainLeft { get; set; }
    public double? MainTop { get; set; }
    public double? MainWidth { get; set; }
    public double? MainHeight { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Paths.Settings))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.Settings), Json.Options) ?? new();
                // ui_version 이 없던 예전 파일 → 0. 캡슐 디자인 기본 자리(위 가운데)로 한 번만 옮김
                if (s.UiVersion < CurrentUiVersion)
                {
                    s.Placement = BarPlacement.TopCenter;
                    s.BarX = s.BarY = null;
                    s.MainWidth = s.MainHeight = null;
                    s.UiVersion = CurrentUiVersion;
                }
                return s;
            }
        }
        catch { /* 손상된 설정은 무시하고 기본값 */ }
        return new AppSettings { UiVersion = CurrentUiVersion };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.Root);
            string tmp = Paths.Settings + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json.Options));
            File.Move(tmp, Paths.Settings, true);
        }
        catch { /* 설정 저장 실패는 모니터링에 영향 없음 */ }
    }
}

/// <summary>Windows 시작 시 실행 (HKCU\...\Run). 사용자가 메뉴에서 켤 때만 기록.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TaskMonitor";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                return k?.GetValue(ValueName) is string s && s.Length > 0;
            }
            catch { return false; }
        }
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(ValueName, Command);
        else k.DeleteValue(ValueName, false);
    }
}
