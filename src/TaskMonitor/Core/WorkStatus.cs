using System.Text.Json;

namespace TaskMonitor.Core;

/// <summary>작업 폴더의 상태 기록에서 읽은 결과 (자동 감지 작업용)</summary>
public sealed class WorkStatusResult
{
    public string Dir { get; init; } = "";
    public string StatusFile { get; init; } = "";
    public DateTime LatestWriteUtc { get; set; }
    /// <summary>작업 전체 상태 문자열 (예: running, complete). 원문 그대로</summary>
    public string? JobState { get; init; }
    /// <summary>지금 진행 중인 단위 작업의 로그 (상태 기록의 "log" 항목)</summary>
    public string? CurrentLog { get; init; }
    public string? CurrentGroup { get; init; }
    public string? CurrentTitle { get; init; }
    public List<StageDoc> Stages { get; init; } = new();
    public int Done => Stages.Count(s => s.State == "succeeded");
    public int Failed => Stages.Count(s => s.State == "failed");
    /// <summary>계획(manifest 등)에서 전체 개수를 알았는지</summary>
    public bool FromPlan { get; init; }
}

/// <summary>
/// 상태를 taskstate 로 기록하지 않는 작업이라도, 작업 폴더에 스스로 상태 파일을 남기는 경우가 많다.
/// (예: 배치 폴더의 status.json + 단위 작업 폴더마다 status.json + manifest.json 계획)
/// 그 파일들을 읽기만 해서 "무엇이 끝났고 무엇이 진행 중인지"를 단계 목록으로 만든다.
///  - 폴더의 status.json / progress.json / state.json → 작업 전체 상태, 현재 단위 작업, 현재 로그("log")
///  - 하위 폴더(깊이 3)의 status.json 중 status + (condition_key|stage|step|key|title) 가 있는 것 → 단위 작업
///  - manifest.json / plan.json / request.json 의 segments·samples·inputs × conditions·variants·steps → 전체 계획(아직 안 한 것)
/// 형식이 맞지 않으면 아무것도 만들지 않는다(추측으로 채우지 않음).
/// </summary>
public static class WorkStatus
{
    private static readonly string[] StatusNames = { "status.json", "progress.json", "state.json" };
    private static readonly string[] PlanNames = { "manifest.json", "plan.json", "request.json" };
    private static readonly string[] KeyProps = { "condition_key", "condition", "stage", "step", "task", "key", "variant" };
    private static readonly string[] TitleProps = { "title", "name", "label", "description" };
    private static readonly string[] GroupProps = { "segment", "sample", "subject", "input", "video", "item", "clip" };
    private static readonly string[] LogProps = { "log", "log_path", "logfile", "log_file", "stdout" };
    private static readonly string[] GroupLists = { "segments", "samples", "inputs", "videos", "items", "clips" };
    private static readonly string[] CondLists = { "conditions", "variants", "steps", "stages", "cases", "tasks" };

    /// <summary>후보 폴더에서 상태 파일을 찾음 (폴더 바로 아래만)</summary>
    public static string? FindStatusFile(IEnumerable<string> dirs)
    {
        foreach (var dir in dirs)
            foreach (var n in StatusNames)
            {
                string p = Path.Combine(dir, n);
                try { if (File.Exists(p) && LooksLikeStatus(p)) return p; } catch { }
            }
        return null;
    }

    private static bool LooksLikeStatus(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(ReadShared(path));
            var r = doc.RootElement;
            return r.ValueKind == JsonValueKind.Object && (Str(r, "status", "state") is not null || r.TryGetProperty("current", out _));
        }
        catch { return false; }
    }

    public static WorkStatusResult? Read(string statusFile)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(ReadShared(statusFile)); }
        catch { return null; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string dir = Path.GetDirectoryName(statusFile)!;
            var cur = root.TryGetProperty("current", out var c) && c.ValueKind == JsonValueKind.Object ? c : root;
            string? curLog = ExistingFile(Str(cur, LogProps) ?? Str(root, LogProps), dir);
            DateTime latest = SafeWrite(statusFile);

            // ---- 단위 작업 (하위 폴더의 status.json)
            var units = new Dictionary<string, Unit>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in EnumerateStatusFiles(dir, 3))
            {
                if (string.Equals(f, statusFile, StringComparison.OrdinalIgnoreCase)) continue;
                var u = ReadUnit(f, dir);
                if (u is null) continue;
                latest = Max(latest, SafeWrite(f));
                if (u.Log is not null) latest = Max(latest, SafeWrite(u.Log));
                if (!units.TryGetValue(u.Id, out var old) || (u.Started ?? DateTimeOffset.MinValue) >= (old.Started ?? DateTimeOffset.MinValue))
                    units[u.Id] = u; // 다시 실행한 경우 최근 시도
            }
            // 상위 상태의 current 가 아직 하위 파일로 없으면 추가
            if (Str(cur, KeyProps) is not null)
            {
                var u = UnitFrom(cur, dir, statusFile);
                if (u is not null && !units.ContainsKey(u.Id)) units[u.Id] = u;
            }
            if (curLog is not null) latest = Max(latest, SafeWrite(curLog));
            latest = Max(latest, LatestDirWrite(dir, 3)); // 파일이 새로 생기는 폴더 = 활동 흔적

            // ---- 계획
            var plan = ReadPlan(dir);
            if (units.Count == 0 && plan.Count == 0) return null;

            var ordered = new List<Unit>();
            if (plan.Count > 0)
            {
                foreach (var p in plan)
                    ordered.Add(units.TryGetValue(p.Id, out var u) ? u with { Title = u.Title ?? p.Title } : p);
                foreach (var u in units.Values.Where(u => !plan.Any(p => p.Id.Equals(u.Id, StringComparison.OrdinalIgnoreCase)))
                                              .OrderBy(u => u.Started ?? DateTimeOffset.MaxValue))
                    ordered.Add(u);
            }
            else ordered.AddRange(units.Values.OrderBy(u => u.Started ?? DateTimeOffset.MaxValue));

            // ---- 평소 소요 (같은 조건의 완료 평균 → 없으면 전체 완료 평균)
            var doneUnits = ordered.Where(u => u.State == "succeeded" && u.Elapsed is > 0).ToList();
            double? allMean = doneUnits.Count > 0 ? doneUnits.Average(u => u.Elapsed!.Value) : null;
            double? MeanFor(string key)
            {
                var same = doneUnits.Where(u => string.Equals(u.Key, key, StringComparison.OrdinalIgnoreCase)).ToList();
                return same.Count > 0 ? same.Average(u => u.Elapsed!.Value) : allMean;
            }

            var stages = new List<StageDoc>();
            foreach (var u in ordered)
            {
                string title = u.Title ?? u.Key;
                stages.Add(new StageDoc
                {
                    Key = u.Id,
                    Name = u.Group is { Length: > 0 } g ? $"{g} · {title}" : title,
                    State = u.State,
                    Weight = 1,
                    StartedAt = u.Started,
                    FinishedAt = u.State is "succeeded" or "failed" or "cancelled" ? u.Finished ?? (u.Started is { } s && u.Elapsed is { } e ? s.AddSeconds(e) : null) : null,
                    ExpectedSeconds = u.State is "succeeded" or "failed" or "cancelled" ? null : MeanFor(u.Key),
                    Note = u.State == "failed" && u.Error is { Length: > 0 } er ? Fmt.Trim(er, 80) : null,
                });
            }

            return new WorkStatusResult
            {
                Dir = dir,
                StatusFile = statusFile,
                LatestWriteUtc = latest,
                JobState = Str(root, "status", "state"),
                CurrentLog = curLog,
                CurrentGroup = Str(cur, GroupProps),
                CurrentTitle = Str(cur, TitleProps) ?? Str(cur, KeyProps),
                Stages = stages,
                FromPlan = plan.Count > 0,
            };
        }
    }

    // ------------------------------------------------------------ 단위 작업
    private sealed record Unit(string Id, string Key, string? Group, string? Title, string State,
                               DateTimeOffset? Started, DateTimeOffset? Finished, double? Elapsed, string? Log, string? Error);

    private static string UnitId(string? group, string key) => string.IsNullOrEmpty(group) ? key : group + "/" + key;

    private static Unit? ReadUnit(string file, string baseDir)
    {
        try
        {
            using var doc = JsonDocument.Parse(ReadShared(file));
            return UnitFrom(doc.RootElement, baseDir, file);
        }
        catch { return null; }
    }

    private static Unit? UnitFrom(JsonElement e, string baseDir, string file)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        string? state = Str(e, "status", "state");
        string? key = Str(e, KeyProps);
        if (state is null || key is null) return null;
        string? group = Str(e, GroupProps);
        string? err = e.TryGetProperty("error", out var ev)
            ? ev.ValueKind == JsonValueKind.String ? ev.GetString() : ev.ValueKind == JsonValueKind.Object ? Str(ev, "message", "type") : null
            : null;
        return new Unit(UnitId(group, key), key, group, Str(e, TitleProps), MapState(state),
                        Time(e, "started", "started_at", "start"), Time(e, "finished", "finished_at", "ended", "end"),
                        Num(e, "elapsed_seconds", "elapsed_s", "elapsed", "duration_seconds"),
                        ExistingFile(Str(e, LogProps), Path.GetDirectoryName(file) ?? baseDir), err);
    }

    public static string MapState(string s) => s.Trim().ToLowerInvariant() switch
    {
        "complete" or "completed" or "done" or "succeeded" or "success" or "ok" or "passed" or "finished" => "succeeded",
        "running" or "started" or "in_progress" or "active" or "processing" => "running",
        "validating" or "verifying" => "validating",
        "failed" or "error" or "fail" => "failed",
        "cancelled" or "canceled" or "stopped" or "aborted" => "cancelled",
        "skipped" => "skipped",
        _ => "pending",
    };

    // ------------------------------------------------------------ 계획 (조건 × 구간)
    private static List<Unit> ReadPlan(string dir)
    {
        foreach (var n in PlanNames)
        {
            string p = Path.Combine(dir, n);
            if (!File.Exists(p)) continue;
            try
            {
                using var doc = JsonDocument.Parse(ReadShared(p));
                var groups = FindList(doc.RootElement, GroupLists, 2);
                var conds = FindConds(doc.RootElement, 2);
                if (groups.Count == 0 && conds.Count == 0) continue;
                var list = new List<Unit>();
                if (groups.Count > 0 && conds.Count > 0)
                {
                    foreach (var g in groups)
                        foreach (var (k, t) in conds)
                            list.Add(new Unit(UnitId(g, k), k, g, t, "pending", null, null, null, null, null));
                }
                else if (conds.Count > 0)
                    list.AddRange(conds.Select(c => new Unit(c.key, c.key, null, c.title, "pending", null, null, null, null, null)));
                else
                    continue; // 구간 목록만으로는 단위 작업을 알 수 없음
                if (list.Count is > 0 and <= 500) return list;
            }
            catch { }
        }
        return new List<Unit>();
    }

    /// <summary>segments 같은 배열 → 각 항목의 이름</summary>
    private static List<string> FindList(JsonElement e, string[] names, int depth)
    {
        if (e.ValueKind != JsonValueKind.Object) return new();
        foreach (var n in names)
            if (e.TryGetProperty(n, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                var res = new List<string>();
                foreach (var it in arr.EnumerateArray())
                {
                    string? name = it.ValueKind == JsonValueKind.String ? it.GetString()
                        : it.ValueKind == JsonValueKind.Object ? Str(it, GroupProps) ?? Str(it, "name", "id") : null;
                    if (!string.IsNullOrEmpty(name)) res.Add(name);
                }
                if (res.Count > 0) return res;
            }
        if (depth > 0)
            foreach (var p in e.EnumerateObject())
            {
                var r = FindList(p.Value, names, depth - 1);
                if (r.Count > 0) return r;
            }
        return new();
    }

    /// <summary>conditions 같은 객체(키 → {title}) 또는 배열 → (키, 제목)</summary>
    private static List<(string key, string? title)> FindConds(JsonElement e, int depth)
    {
        if (e.ValueKind != JsonValueKind.Object) return new();
        foreach (var n in CondLists)
            if (e.TryGetProperty(n, out var v))
            {
                var res = new List<(string, string?)>();
                if (v.ValueKind == JsonValueKind.Object)
                    foreach (var p in v.EnumerateObject())
                        res.Add((p.Name, p.Value.ValueKind == JsonValueKind.Object ? Str(p.Value, TitleProps) : null));
                else if (v.ValueKind == JsonValueKind.Array)
                    foreach (var it in v.EnumerateArray())
                    {
                        string? k = it.ValueKind == JsonValueKind.String ? it.GetString()
                            : it.ValueKind == JsonValueKind.Object ? Str(it, KeyProps) ?? Str(it, "name", "id") : null;
                        if (!string.IsNullOrEmpty(k))
                            res.Add((k, it.ValueKind == JsonValueKind.Object ? Str(it, "title", "label", "description") : null));
                    }
                if (res.Count > 0) return res;
            }
        if (depth > 0)
            foreach (var p in e.EnumerateObject())
            {
                var r = FindConds(p.Value, depth - 1);
                if (r.Count > 0) return r;
            }
        return new();
    }

    // ------------------------------------------------------------ 도우미
    private static IEnumerable<string> EnumerateStatusFiles(string dir, int depth)
    {
        var stack = new Stack<(string d, int lv)>();
        stack.Push((dir, 0));
        int seen = 0;
        while (stack.Count > 0 && seen < 3000)
        {
            var (d, lv) = stack.Pop();
            string[] subs;
            try { subs = Directory.GetDirectories(d); } catch { continue; }
            foreach (var s in subs)
            {
                seen++;
                foreach (var n in StatusNames)
                {
                    string f = Path.Combine(s, n);
                    if (File.Exists(f)) yield return f;
                }
                if (lv + 1 < depth) stack.Push((s, lv + 1));
            }
        }
    }

    private static DateTime LatestDirWrite(string dir, int depth)
    {
        DateTime latest = DateTime.MinValue;
        var stack = new Stack<(string d, int lv)>();
        stack.Push((dir, 0));
        int seen = 0;
        while (stack.Count > 0 && seen < 3000)
        {
            var (d, lv) = stack.Pop();
            string[] subs;
            try { subs = Directory.GetDirectories(d); } catch { continue; }
            foreach (var sd in subs)
            {
                seen++;
                try { var w = Directory.GetLastWriteTimeUtc(sd); if (w > latest) latest = w; } catch { }
                if (lv + 1 < depth) stack.Push((sd, lv + 1));
            }
        }
        return latest;
    }

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length > 4 * 1024 * 1024) throw new IOException("too large");
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    private static string? ExistingFile(string? p, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        try
        {
            string full = Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(baseDir, p));
            return File.Exists(full) ? full : null;
        }
        catch { return null; }
    }

    private static DateTime SafeWrite(string p)
    {
        try { return File.GetLastWriteTimeUtc(p); } catch { return DateTime.MinValue; }
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static string? Str(JsonElement e, params string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names)
            if (e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s)
                return s;
        return null;
    }

    private static double? Num(JsonElement e, params string[] names)
    {
        foreach (var n in names)
            if (e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        return null;
    }

    private static DateTimeOffset? Time(JsonElement e, params string[] names)
    {
        foreach (var n in names)
            if (e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), out var t))
                return t;
        return null;
    }
}
