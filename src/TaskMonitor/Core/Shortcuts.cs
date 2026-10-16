using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TaskMonitor.Core;

// 바로가기 런처 (docs/PLAN_SHORTCUTS_AND_UI.md). WPF 의존 없음.

public enum ShortcutKind { App, Folder, File, Url, Shortcut }

public sealed class ShortcutIcon
{
    /// <summary>"auto" = 대상에서 추출, "file" = Path(ico/png/exe/dll) + Index</summary>
    public string Source { get; set; } = "auto";
    public string? Path { get; set; }
    public int Index { get; set; }
}

public sealed class ShortcutItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public ShortcutKind Kind { get; set; }
    /// <summary>실행·열기 대상 (Shortcut 종류는 .lnk 가 가리키는 대상, 표시용)</summary>
    public string Target { get; set; } = "";
    public string? Args { get; set; }
    public string? Workdir { get; set; }
    /// <summary>.lnk 로 등록했으면 원본 .lnk 경로. 실행은 .lnk 로 해서 원래 설정을 유지</summary>
    public string? Lnk { get; set; }
    public ShortcutIcon Icon { get; set; } = new();
    public string? Group { get; set; }
    public int Order { get; set; }
}

public sealed class ShortcutGroup
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Expanded { get; set; } = true;
}

public sealed class ShortcutDoc
{
    public string Schema { get; set; } = "taskmonitor-shortcuts/1";
    public bool Expanded { get; set; }
    public int IconSize { get; set; } = 32;
    public List<ShortcutGroup> Groups { get; set; } = new();
    public List<ShortcutItem> Items { get; set; } = new();
}

public static class ShortcutStore
{
    public static string FilePath => Path.Combine(Paths.Root, "shortcuts.json");
    private static string BackupPath => FilePath + ".bak";

    public static ShortcutDoc Load()
    {
        foreach (var p in new[] { FilePath, BackupPath })
        {
            try
            {
                if (!File.Exists(p)) continue;
                var doc = JsonSerializer.Deserialize<ShortcutDoc>(File.ReadAllText(p), Json.Options);
                if (doc is not null) { doc.Items ??= new(); doc.Groups ??= new(); return doc; }
            }
            catch { /* 손상: 백업으로 */ }
        }
        return new ShortcutDoc();
    }

    /// <summary>임시 파일에 쓰고 교체. 직전 정상본은 .bak 으로 보관.</summary>
    public static void Save(ShortcutDoc doc)
    {
        Directory.CreateDirectory(Paths.Root);
        string tmp = FilePath + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(doc, Json.Options));
        if (File.Exists(FilePath)) File.Copy(FilePath, BackupPath, true);
        File.Move(tmp, FilePath, true);
    }

    public static string NewId() => "s-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>끌어다 놓은 경로 또는 입력한 주소로 바로가기 항목을 만듦. 판별할 수 없으면 null.</summary>
    public static ShortcutItem? CreateFrom(string input)
    {
        string s = input.Trim().Trim('"');
        if (s.Length == 0) return null;
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            string host = Uri.TryCreate(s, UriKind.Absolute, out var u) ? u.Host : s;
            return new ShortcutItem { Id = NewId(), Kind = ShortcutKind.Url, Target = s, Name = host };
        }
        if (Directory.Exists(s))
        {
            string full = Path.GetFullPath(s);
            string name = Path.GetFileName(full.TrimEnd('\\', '/'));
            return new ShortcutItem { Id = NewId(), Kind = ShortcutKind.Folder, Target = full, Name = name.Length > 0 ? name : full };
        }
        if (!File.Exists(s)) return null;
        string path = Path.GetFullPath(s);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        string stem = Path.GetFileNameWithoutExtension(path);
        switch (ext)
        {
            case ".lnk":
                {
                    var info = ShellLinks.Read(path);
                    return new ShortcutItem
                    {
                        Id = NewId(), Kind = ShortcutKind.Shortcut, Lnk = path, Name = stem,
                        Target = info?.Target ?? "", Args = info?.Args, Workdir = info?.Workdir,
                    };
                }
            case ".url":
                {
                    string? url = ShellLinks.ReadUrlFile(path);
                    return new ShortcutItem { Id = NewId(), Kind = ShortcutKind.Url, Target = url ?? path, Lnk = path, Name = stem };
                }
            case ".exe" or ".bat" or ".cmd" or ".com":
                {
                    string name = stem;
                    try
                    {
                        var vi = FileVersionInfo.GetVersionInfo(path);
                        if (!string.IsNullOrWhiteSpace(vi.FileDescription)) name = vi.FileDescription.Trim();
                    }
                    catch { }
                    return new ShortcutItem { Id = NewId(), Kind = ShortcutKind.App, Target = path, Name = name, Workdir = Path.GetDirectoryName(path) };
                }
            default:
                return new ShortcutItem { Id = NewId(), Kind = ShortcutKind.File, Target = path, Name = Path.GetFileName(path) };
        }
    }

    /// <summary>실행·열기 대상이 지금 있는지 (URL 은 항상 true)</summary>
    public static bool TargetExists(ShortcutItem it) => it.Kind switch
    {
        ShortcutKind.Url => true,
        ShortcutKind.Folder => Directory.Exists(it.Target),
        ShortcutKind.Shortcut => it.Lnk is not null && File.Exists(it.Lnk),
        _ => File.Exists(it.Target),
    };

    /// <summary>아이콘을 뽑을 경로 (.lnk 는 화살표 없는 대상 아이콘을 우선)</summary>
    public static string IconPath(ShortcutItem it)
    {
        if (it.Icon.Source == "file" && !string.IsNullOrEmpty(it.Icon.Path)) return it.Icon.Path!;
        if (it.Kind == ShortcutKind.Shortcut)
        {
            if (it.Lnk is not null && ShellLinks.Read(it.Lnk) is { IconPath: { Length: > 0 } ip } li && File.Exists(ip))
                return $"{ip},{li.IconIndex}"; // 바로가기에 지정된 아이콘
            if (it.Target.Length > 0 && (File.Exists(it.Target) || Directory.Exists(it.Target))) return it.Target;
            return it.Lnk ?? it.Target;
        }
        if (it.Kind == ShortcutKind.Url && it.Lnk is not null) return it.Lnk;
        return it.Target;
    }

    /// <summary>탐색기에서 더블클릭한 것과 같은 방식(ShellExecute)으로 실행할 정보</summary>
    public static ProcessStartInfo BuildStart(ShortcutItem it)
    {
        switch (it.Kind)
        {
            case ShortcutKind.Folder:
                return new ProcessStartInfo("explorer.exe", $"\"{it.Target}\"") { UseShellExecute = true };
            case ShortcutKind.Shortcut:
                return new ProcessStartInfo(it.Lnk ?? it.Target) { UseShellExecute = true };
            case ShortcutKind.App:
                return new ProcessStartInfo(it.Target)
                {
                    UseShellExecute = true,
                    Arguments = it.Args ?? "",
                    WorkingDirectory = !string.IsNullOrEmpty(it.Workdir) && Directory.Exists(it.Workdir)
                        ? it.Workdir! : Path.GetDirectoryName(it.Target) ?? "",
                };
            default: // File, Url
                return new ProcessStartInfo(it.Target) { UseShellExecute = true };
        }
    }

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int pid);

    /// <summary>실행. 대상이 없으면 실행하지 않고 오류 메시지를 돌려줌.</summary>
    public static string? Launch(ShortcutItem it)
    {
        if (!TargetExists(it)) return $"대상이 없습니다: {(it.Kind == ShortcutKind.Shortcut ? it.Lnk : it.Target)}";
        try
        {
            // 바 창은 포커스를 뺏지 않는 창이라, 실행된 앱이 앞으로 올 수 있게 허용
            try { AllowSetForegroundWindow(-1); } catch { }
            Process.Start(BuildStart(it))?.Dispose();
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return null; // 사용자가 UAC 창에서 취소
        }
        catch (Exception ex)
        {
            return "실행하지 못했습니다: " + ex.Message;
        }
    }

    /// <summary>파일 위치 열기 (탐색기에서 선택)</summary>
    public static void RevealInExplorer(ShortcutItem it)
    {
        string p = it.Kind == ShortcutKind.Shortcut && it.Target.Length > 0 ? it.Target : (it.Lnk ?? it.Target);
        if (it.Kind == ShortcutKind.Folder || Directory.Exists(p))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{p}\"") { UseShellExecute = true })?.Dispose();
        else if (File.Exists(p))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{p}\"") { UseShellExecute = true })?.Dispose();
    }
}
