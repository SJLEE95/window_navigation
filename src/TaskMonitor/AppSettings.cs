using System.Text.Json;
using Microsoft.Win32;
using TaskMonitor.Core;

namespace TaskMonitor;

public enum BarPlacement { TopRight, TopLeft, BottomRight, BottomLeft, DockTop, DockBottom }

public sealed class AppSettings
{
    public BarPlacement Placement { get; set; } = BarPlacement.BottomRight;
    public bool Topmost { get; set; } = true;
    public bool HideOnFullscreen { get; set; } = true;
    public bool ShowRecentFinished { get; set; } = true;
    public int RecentFinishedMinutes { get; set; } = 10;
    public int MaxChips { get; set; } = 3;
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
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.Settings), Json.Options) ?? new();
        }
        catch { /* 손상된 설정은 무시하고 기본값 */ }
        return new AppSettings();
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
