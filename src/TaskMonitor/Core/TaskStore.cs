using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TaskMonitor.Core;

public static class Paths
{
    private static string? _override;

    public static void SetRoot(string? dir) => _override = string.IsNullOrWhiteSpace(dir) ? null : System.IO.Path.GetFullPath(dir);

    public static string Root =>
        _override
        ?? (Environment.GetEnvironmentVariable("TASKMONITOR_DIR") is { Length: > 0 } env
            ? System.IO.Path.GetFullPath(env)
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskMonitor"));

    public static string Tasks => System.IO.Path.Combine(Root, "tasks");
    /// <summary>자동 감지한 프로세스 기록 (모니터가 씀)</summary>
    public static string Auto => System.IO.Path.Combine(Root, "auto");
    public static string Control => System.IO.Path.Combine(Root, "control");
    public static string Archive => System.IO.Path.Combine(Root, "archive");
    public static string Settings => System.IO.Path.Combine(Root, "settings.json");
}

public sealed class StoreEntry
{
    public required string Id { get; init; }
    public required string FilePath { get; init; }
    public TaskDoc? Doc { get; set; }
    public string? ReadError { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public long Length { get; set; }
    public int Version { get; set; }
}

/// <summary>상태 폴더를 읽기 전용으로 스캔. 바뀐 파일만 다시 읽고, 읽기 실패 시 직전 정상본 유지.</summary>
public sealed class TaskStore
{
    private readonly Dictionary<string, StoreEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, StoreEntry> Entries => _entries;

    /// <returns>목록이나 내용이 바뀌었으면 true</returns>
    public bool Refresh()
    {
        bool changed = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in new[] { Paths.Tasks, Paths.Auto })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var path in Directory.EnumerateFiles(dir, "*.json"))
            {
                string id = System.IO.Path.GetFileNameWithoutExtension(path);
                seen.Add(id);
                FileInfo fi;
                try { fi = new FileInfo(path); if (!fi.Exists) continue; }
                catch { continue; }
                if (!_entries.TryGetValue(id, out var e))
                {
                    e = new StoreEntry { Id = id, FilePath = path };
                    _entries[id] = e;
                    changed = true;
                }
                else if (e.LastWriteUtc == fi.LastWriteTimeUtc && e.Length == fi.Length && e.ReadError is null)
                {
                    continue;
                }
                if (TryRead(path, out var doc, out var err))
                {
                    if (string.IsNullOrEmpty(doc!.Id)) doc.Id = id;
                    e.Doc = doc;
                    e.ReadError = null;
                    e.LastWriteUtc = fi.LastWriteTimeUtc;
                    e.Length = fi.Length;
                    e.Version++;
                    changed = true;
                }
                else
                {
                    // 교체 중이거나 잠시 잠긴 경우: 직전 정상본 유지, 다음 주기에 다시 시도
                    if (e.ReadError != err) changed = true;
                    e.ReadError = err;
                }
            }
        }
        foreach (var id in _entries.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _entries.Remove(id);
            changed = true;
        }
        return changed;
    }

    /// <summary>파일을 즉시 다시 읽음. 프로세스 종료를 감지했을 때 마지막 기록을 놓치지 않기 위해 사용.</summary>
    public bool Reload(string id)
    {
        if (!_entries.TryGetValue(id, out var e)) return false;
        try
        {
            var fi = new FileInfo(e.FilePath);
            if (!fi.Exists) return false;
            if (!TryRead(e.FilePath, out var doc, out var err)) return false;
            if (string.IsNullOrEmpty(doc!.Id)) doc.Id = id;
            e.Doc = doc;
            e.ReadError = null;
            e.LastWriteUtc = fi.LastWriteTimeUtc;
            e.Length = fi.Length;
            e.Version++;
            return true;
        }
        catch { return false; }
    }

    private static bool TryRead(string path, out TaskDoc? doc, out string? error)
    {
        doc = null;
        error = null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            doc = JsonSerializer.Deserialize<TaskDoc>(fs, Json.Options);
            if (doc is null) { error = "빈 파일"; return false; }
            return true;
        }
        catch (JsonException ex) { error = "JSON 형식 오류: " + ex.Message; }
        catch (IOException ex) { error = "읽기 실패: " + ex.Message; }
        catch (UnauthorizedAccessException ex) { error = "권한 없음: " + ex.Message; }
        return false;
    }
}

public enum ProcState { Alive, Dead, Unknown }

/// <summary>PID 생존 확인. PID 재사용은 프로세스 시작 시각으로 구분. 다른 PC에서 기록한 작업은 Unknown.</summary>
public sealed class ProcessProbe
{
    private readonly Dictionary<int, (DateTime at, DateTime? start, bool exists)> _cache = new();
    private static readonly string[] LocalNames = GetLocalNames();

    private static string[] GetLocalNames()
    {
        var names = new List<string> { Environment.MachineName };
        try { names.Add(System.Net.Dns.GetHostName()); } catch { }
        return names.ToArray();
    }

    public static bool IsLocalHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return true;
        string h = host.Split('.')[0];
        foreach (var n in LocalNames)
        {
            string m = n.Split('.')[0];
            if (string.Equals(h, m, StringComparison.OrdinalIgnoreCase)) return true;
            // NetBIOS 이름은 15자로 잘림
            if (m.Length == 15 && h.StartsWith(m, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public ProcState Check(TaskDoc doc, DateTime nowUtc)
    {
        if (doc.Pid is not int pid || pid <= 0) return ProcState.Unknown;
        if (!IsLocalHost(doc.Host)) return ProcState.Unknown;
        if (!_cache.TryGetValue(pid, out var c) || (nowUtc - c.at).TotalSeconds > 2)
        {
            c = (nowUtc, null, false);
            try
            {
                using var p = Process.GetProcessById(pid);
                bool exited = false;
                try { exited = p.HasExited; } catch { /* 접근 거부: 살아 있다고 봄 */ }
                DateTime? start = null;
                try { start = p.StartTime.ToUniversalTime(); } catch { /* 접근 거부 */ }
                c = (nowUtc, start, !exited);
            }
            catch (ArgumentException) { c = (nowUtc, null, false); }
            catch (InvalidOperationException) { c = (nowUtc, null, false); }
            _cache[pid] = c;
        }
        if (!c.exists) return ProcState.Dead;
        var created = doc.CreatedAt ?? doc.StartedAt;
        if (c.start is DateTime s && created is DateTimeOffset cr && s > cr.UtcDateTime.AddSeconds(10))
            return ProcState.Dead; // 같은 PID를 다른 프로세스가 재사용
        return ProcState.Alive;
    }
}

public static class TaskFiles
{
    public static string ReadTail(string? path, int maxLines, out bool exists)
    {
        exists = false;
        if (string.IsNullOrEmpty(path)) return "";
        try
        {
            if (!File.Exists(path)) return "";
            exists = true;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long len = fs.Length;
            int take = (int)Math.Min(len, 96 * 1024);
            fs.Seek(len - take, SeekOrigin.Begin);
            var buf = new byte[take];
            int read = 0;
            while (read < take) { int n = fs.Read(buf, read, take - read); if (n <= 0) break; read += n; }
            string text = DecodeLog(buf, read);
            var lines = text.Replace("\r\n", "\n").Split('\n');
            int start = Math.Max(0, lines.Length - maxLines - 1);
            if (take < len && start == 0) start = 1; // 잘린 첫 줄 버림
            return string.Join(Environment.NewLine, lines.Skip(start)).TrimEnd();
        }
        catch (Exception ex) { return "(로그를 읽지 못했습니다: " + ex.Message + ")"; }
    }

    private static string DecodeLog(byte[] buf, int count)
    {
        var utf8 = new UTF8Encoding(false, true);
        try { return utf8.GetString(buf, 0, count); }
        catch (DecoderFallbackException)
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(949).GetString(buf, 0, count); // 한국어 Windows 콘솔 출력
            }
            catch { return Encoding.UTF8.GetString(buf, 0, count); }
        }
    }

    public static bool IsIncompletePath(string path)
    {
        var p = path.TrimEnd('\\', '/').ToLowerInvariant();
        return p.EndsWith(".partial") || p.EndsWith(".tmp") || p.EndsWith(".part") || p.EndsWith(".incomplete");
    }

    public static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    public static string CancelFile(string id) => System.IO.Path.Combine(Paths.Control, id + ".cancel");

    public static bool CancelRequested(string id) => File.Exists(CancelFile(id));

    /// <summary>중단 요청 파일만 만든다. 프로세스는 건드리지 않는다.</summary>
    public static void RequestCancel(string id)
    {
        Directory.CreateDirectory(Paths.Control);
        File.WriteAllText(CancelFile(id), DateTimeOffset.Now.ToString("o"));
    }

    /// <summary>목록에서 숨기기: 상태 파일을 archive 폴더로 옮긴다(삭제하지 않음).</summary>
    public static void Archive(string id)
    {
        string src = System.IO.Path.Combine(Paths.Tasks, id + ".json");
        if (!File.Exists(src)) src = System.IO.Path.Combine(Paths.Auto, id + ".json");
        Directory.CreateDirectory(Paths.Archive);
        string dst = System.IO.Path.Combine(Paths.Archive, id + ".json");
        if (File.Exists(dst)) dst = System.IO.Path.Combine(Paths.Archive, $"{id}.{DateTime.Now:yyyyMMddHHmmss}.json");
        File.Move(src, dst);
    }
}
