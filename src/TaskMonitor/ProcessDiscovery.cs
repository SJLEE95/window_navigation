using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using TaskMonitor.Core;

namespace TaskMonitor;

/// <summary>
/// 상태 파일을 쓰지 않는 Python 작업을 자동으로 찾아 기록(auto/*.json)한다.
/// - 알 수 있는 것: 실행 명령, 작업 폴더, 시작 시각, CPU 사용, 종료 코드(모니터가 실행 중에 감지한 경우)
/// - 로그 파일을 찾으면 INFER_PROGRESS_JSON / tqdm 진행 표시를 읽어 진행률 표시
/// - 프로세스를 종료하거나 건드리지 않는다. 읽기 전용 핸들(조회·동기화)만 연다.
/// </summary>
internal sealed class ProcessDiscovery : IDisposable
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint SYNCHRONIZE = 0x00100000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle h, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle h, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(SafeProcessHandle h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(SafeProcessHandle h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle h, int cls, IntPtr buf, int len, out int retLen);
    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string cmd, out int argc);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr p);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING { public ushort Length, MaximumLength; public IntPtr Buffer; }

    private sealed class Tracked
    {
        public required int Pid;
        public required SafeProcessHandle Handle;
        public required TaskDoc Doc;
        public long LastCpu;
        public DateTime LastCpuAt;
        public long LogLen = -1;
        public DateTime LogWrite;
        public DateTime NextLogSearch;
        public DateTime LastSave;
        public bool Dirty = true;
        /// <summary>작업 폴더의 상태 기록 (status.json 등). 있으면 단계 목록·현재 로그를 거기서 가져옴</summary>
        public string? StatusFile;
        public DateTime NextStatusSearch;
        public DateTime NextStatusRead;
        public DateTime StatusSig;
        public bool HasWork;
        /// <summary>venv 런처의 실제 python, multiprocessing 작업자 등 (CPU 합산용)</summary>
        public List<SafeProcessHandle> Children = new();
    }

    private readonly Dictionary<int, Tracked> _tracked = new();
    private readonly HashSet<string> _ignored = new();
    private bool _loaded;
    private DateTime _nextScan;

    public string LastError { get; private set; } = "";

    /// <returns>auto 기록을 쓰거나 바꿨으면 true</returns>
    public bool Scan(IReadOnlyDictionary<string, StoreEntry> entries, AppSettings s, DateTimeOffset now)
    {
        bool wrote = false;
        try
        {
            if (!_loaded)
            {
                _loaded = true;
                wrote |= ReattachSaved(entries, now);
            }
            if (s.AutoDetect && DateTime.UtcNow >= _nextScan)
            {
                _nextScan = DateTime.UtcNow.AddSeconds(3);
                FindNew(entries, s, now);
            }
            foreach (var t in _tracked.Values.ToList())
                wrote |= Update(t, s, now);
            LastError = "";
        }
        catch (Exception ex) { LastError = "자동 감지 오류: " + ex.Message; Trace("ERROR " + ex); }
        return wrote;
    }

    // ------------------------------------------------------------ 새 프로세스 찾기
    private sealed record Cand(int Pid, DateTime CreatedUtc, SafeProcessHandle Handle, string Cmd, int Parent, string Key);

    private void FindNew(IReadOnlyDictionary<string, StoreEntry> entries, AppSettings s, DateTimeOffset now)
    {
        // 이미 상태 파일로 기록 중인 작업(taskstate)의 PID 와 그 자식은 제외
        var known = new HashSet<int>();
        foreach (var e in entries.Values)
        {
            var d = e.Doc;
            if (d is null || d.Source == "auto") continue;
            if (d.State is "succeeded" or "failed" or "cancelled") continue;
            if (d.Pid is int p) known.Add(p);
            if (d.ChildPid is int c) known.Add(c);
        }

        var names = new HashSet<string>(s.AutoDetectProcessNames.Select(n => n.ToLowerInvariant().Replace(".exe", "")));
        var cands = new Dictionary<int, Cand>();
        var liveKeys = new HashSet<string>();
        foreach (var p in Process.GetProcesses())
        {
            int pid;
            string name;
            try { pid = p.Id; name = p.ProcessName.ToLowerInvariant(); }
            catch { p.Dispose(); continue; }
            p.Dispose();
            if (!IsWatchedName(name, names)) continue;
            if (_tracked.ContainsKey(pid)) continue;
            var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, false, pid);
            if (h.IsInvalid) { h.Dispose(); continue; }
            if (!GetProcessTimes(h, out long cr, out _, out _, out _)) { h.Dispose(); continue; }
            var created = DateTime.FromFileTimeUtc(cr);
            string key = $"{pid}:{cr}";
            liveKeys.Add(key);
            if (_ignored.Contains(key) || (DateTime.UtcNow - created).TotalSeconds < s.AutoDetectMinAgeSeconds)
            {
                h.Dispose();
                continue;
            }
            cands[pid] = new Cand(pid, created, h, GetCommandLine(h) ?? "", GetParentPid(h), key);
        }
        _ignored.IntersectWith(liveKeys);

        var children = new List<Cand>();
        foreach (var c in cands.Values)
        {
            string? reason = null;
            if (known.Contains(c.Pid) || known.Contains(c.Parent)) reason = "taskstate";
            else if (cands.ContainsKey(c.Parent) || _tracked.ContainsKey(c.Parent)) reason = "child"; // venv 런처의 자식, multiprocessing 작업자
            else if (c.Cmd.Length == 0) reason = "nocmd";
            else if (s.AutoDetectExclude.Any(x => c.Cmd.Contains(x, StringComparison.OrdinalIgnoreCase))) reason = "excluded";
            var parsed = reason is null ? ParsePython(c.Cmd) : null;
            if (reason is null && parsed is null) reason = "noscript"; // 대화형, -c 등
            Trace($"{(reason ?? "TRACK")} pid={c.Pid} parent={c.Parent} cmd={c.Cmd}");
            if (reason == "child") { children.Add(c); continue; }
            if (reason is not null)
            {
                _ignored.Add(c.Key);
                c.Handle.Dispose();
                continue;
            }
            string? cwd = GetCurrentDirectory(c.Pid);
            var doc = NewDoc(c, parsed!.Value, cwd, s);
            _tracked[c.Pid] = new Tracked { Pid = c.Pid, Handle = c.Handle, Doc = doc, NextLogSearch = DateTime.MinValue };
        }
        foreach (var c in children)
        {
            _ignored.Add(c.Key);
            // 부모(또는 조상)가 추적 중이면 CPU 합산에 포함
            var owner = _tracked.TryGetValue(c.Parent, out var t) ? t
                : cands.TryGetValue(c.Parent, out var pc) && _tracked.TryGetValue(pc.Parent, out var t2) ? t2 : null;
            if (owner is not null) owner.Children.Add(c.Handle);
            else c.Handle.Dispose();
        }
    }

    /// <summary>
    /// "python" 은 python.exe 뿐 아니라 python3.exe, python3.12.exe(py 런처·스토어판이 실행하는 이름)도 포함.
    /// </summary>
    internal static bool IsWatchedName(string name, HashSet<string> names)
    {
        if (names.Contains(name)) return true;
        foreach (var n in names)
            if (name.Length > n.Length && name.StartsWith(n, StringComparison.Ordinal) &&
                name.AsSpan(n.Length).IndexOfAnyExcept("0123456789.") < 0)
                return true;
        return false;
    }

    private static TaskDoc NewDoc(Cand c, (string script, List<string> args, bool module) p, string? cwd, AppSettings s)
    {
        string scriptPath = p.script;
        if (!p.module && cwd is not null && !Path.IsPathRooted(scriptPath))
        {
            try { scriptPath = Path.GetFullPath(Path.Combine(cwd, scriptPath)); } catch { }
        }
        string scriptName = p.module ? "-m " + p.script : Path.GetFileName(scriptPath);
        string? scriptDir = p.module ? null : Path.GetDirectoryName(scriptPath);
        string workdir = cwd?.TrimEnd('\\') ?? scriptDir ?? "";
        string project = new DirectoryInfo(string.IsNullOrEmpty(scriptDir) ? (workdir.Length > 0 ? workdir : ".") : scriptDir).Name;
        var created = new DateTimeOffset(c.CreatedUtc).ToLocalTime();
        string argText = string.Join(" ", p.args);
        return new TaskDoc
        {
            Schema = "taskmonitor/1",
            Id = $"auto-{c.Pid}-{created:yyyyMMdd-HHmmss}",
            Name = Fmt.Trim((scriptName + " " + argText).Trim(), 90),
            Project = project,
            Workdir = workdir,
            Subject = GuessSubject(p.args),
            State = "running",
            Message = "자동 감지한 프로세스",
            Pid = c.Pid,
            Host = Environment.MachineName,
            CreatedAt = created,
            StartedAt = created,
            UpdatedAt = DateTimeOffset.Now,
            StaleAfterS = s.AutoDetectStaleSeconds,
            CurrentStage = "run",
            Stages = new List<StageDoc> { new() { Key = "run", Name = "실행", State = "running", StartedAt = created } },
            Cancel = new CancelDoc { Supported = false },
            Source = "auto",
            CommandLine = c.Cmd,
            Activity = new ActivityDoc(),
            Results = GuessOutputDirs(p.args, cwd),
        };
    }

    /// <summary>명령 인자 중 실제로 있는 폴더(--batch, --out 등)를 '출력 폴더(추정)'로 표시해 바로 열 수 있게 함</summary>
    private static List<ResultDoc> GuessOutputDirs(List<string> args, string? cwd)
    {
        var list = new List<ResultDoc>();
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            string? key = null;
            if (a.StartsWith('-') && a.Contains('=')) { key = a[..a.IndexOf('=')]; a = a[(a.IndexOf('=') + 1)..]; }
            else if (a.StartsWith('-')) continue;
            else if (i > 0 && args[i - 1].StartsWith('-')) key = args[i - 1];
            string full;
            try { full = Path.IsPathRooted(a) || cwd is null ? a : Path.GetFullPath(Path.Combine(cwd, a)); }
            catch { continue; }
            if (a.Length < 3 || !Directory.Exists(full)) continue;
            if (cwd is not null && string.Equals(full.TrimEnd('\\'), cwd.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
            if (list.Any(r => string.Equals(r.Path, full, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(new ResultDoc { Label = key is null ? "인자 폴더(추정)" : $"{key} 폴더(추정)", Path = full, Kind = "dir" });
            if (list.Count >= 4) break;
        }
        return list;
    }

    private static string? GuessSubject(List<string> args)
    {
        string[] keys = { "--input", "-i", "--sample", "--src", "--source", "--video", "--name" };
        for (int i = 0; i < args.Count - 1; i++)
            if (keys.Contains(args[i], StringComparer.OrdinalIgnoreCase))
                return Path.GetFileNameWithoutExtension(args[i + 1].TrimEnd('\\', '/'));
        foreach (var a in args)
            if (a.StartsWith("--input=", StringComparison.OrdinalIgnoreCase) || a.StartsWith("--sample=", StringComparison.OrdinalIgnoreCase))
                return Path.GetFileNameWithoutExtension(a[(a.IndexOf('=') + 1)..]);
        return null;
    }

    /// <summary>python 명령줄에서 스크립트(또는 -m 모듈)와 인자를 꺼냄. 스크립트가 없으면 null.</summary>
    private static (string script, List<string> args, bool module)? ParsePython(string cmd)
    {
        var argv = SplitArgs(cmd);
        if (argv.Count < 2) return null;
        for (int i = 1; i < argv.Count; i++)
        {
            string a = argv[i];
            if (a == "-m" && i + 1 < argv.Count) return (argv[i + 1], argv.Skip(i + 2).ToList(), true);
            if (a == "-c") return null;
            if (a is "-X" or "-W" or "-Q") { i++; continue; }
            if (a.StartsWith('-')) continue;
            return (a, argv.Skip(i + 1).ToList(), false);
        }
        return null;
    }

    private static List<string> SplitArgs(string cmd)
    {
        var list = new List<string>();
        IntPtr p = CommandLineToArgvW(cmd, out int argc);
        if (p == IntPtr.Zero) return list;
        try
        {
            for (int i = 0; i < argc; i++)
                list.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, i * IntPtr.Size)) ?? "");
        }
        finally { LocalFree(p); }
        return list;
    }

    // ------------------------------------------------------------ 갱신
    private bool Update(Tracked t, AppSettings s, DateTimeOffset now)
    {
        var d = t.Doc;
        bool exited = WaitForSingleObject(t.Handle, 0) == 0;

        // 작업 폴더가 스스로 남기는 상태 기록(status.json 등)이 있으면 그걸로 단계·현재 로그를 잡음
        if (DateTime.UtcNow >= t.NextStatusRead)
        {
            t.NextStatusRead = DateTime.UtcNow.AddSeconds(4);
            t.Dirty |= RefreshWorkStatus(t, now);
        }

        // 로그 찾기 / 읽기. 샘플마다 로그를 새로 만드는 스크립트가 많아서 주기적으로 다시 찾고,
        // 더 최근에 갱신된 로그가 생기면 그쪽으로 옮겨 감
        if (!t.HasWork && DateTime.UtcNow >= t.NextLogSearch)
        {
            var st = d.Stages![0];
            t.NextLogSearch = DateTime.UtcNow.AddSeconds(15);
            var log = FindLog(t);
            if (log is not null && !string.Equals(log, d.LogPath, StringComparison.OrdinalIgnoreCase))
            {
                d.LogPath = log;
                d.Activity!.LogGuess = log;
                t.LogLen = -1;
                st.Done = null; st.Total = null; st.Rate = null; st.EtaSeconds = null;
                t.Dirty = true;
            }
        }
        if (d.LogPath is not null) t.Dirty |= ReadLog(t, now);

        if (exited)
        {
            var st = CurrentStage(d);
            GetExitCodeProcess(t.Handle, out uint code);
            DateTimeOffset end = now;
            if (GetProcessTimes(t.Handle, out _, out long ex, out _, out _) && ex > 0)
                end = new DateTimeOffset(DateTime.FromFileTimeUtc(ex)).ToLocalTime();
            d.State = "exited";
            d.FinishedAt = end;
            d.UpdatedAt = now;
            foreach (var s2 in d.Stages!.Where(x => x.State is "running" or "validating"))
            {
                s2.State = "exited";
                s2.FinishedAt = end;
                s2.EtaSeconds = null;
            }
            if (!t.HasWork) { st.State = "exited"; st.FinishedAt = end; st.EtaSeconds = null; }
            d.Activity!.CpuPercent = null;
            d.Activity.IdleSince = null;
            d.Completion = new CompletionDoc { Basis = "exit_code", ExitCode = unchecked((int)code) };
            if (code != 0)
            {
                string tail = TaskFiles.ReadTail(d.LogPath, 15, out _);
                d.Error = new ErrorDoc { Type = "ExitCode", Message = $"프로세스가 종료 코드 {unchecked((int)code)}(으)로 끝났습니다", Detail = tail, Stage = st.Key };
            }
            d.Message = code == 0 ? "프로세스 종료 (코드 0)" : $"프로세스 오류 종료 (코드 {unchecked((int)code)})";
            if (t.HasWork && d.Stages!.Count > 1)
                d.Message += $" · 단위 작업 {d.Stages.Count(x => x.State == "succeeded")}/{d.Stages.Count} 완료 기록";
            Save(d);
            t.Handle.Dispose();
            foreach (var ch in t.Children) ch.Dispose();
            _tracked.Remove(t.Pid);
            return true;
        }

        // CPU 사용률 (활동 여부 참고용)
        if (GetProcessTimes(t.Handle, out _, out _, out long k, out long u))
        {
            long cpu = k + u;
            foreach (var ch in t.Children)
                if (GetProcessTimes(ch, out _, out _, out long ck, out long cu)) cpu += ck + cu;
            var at = DateTime.UtcNow;
            if (t.LastCpuAt != default)
            {
                double wall = (at - t.LastCpuAt).TotalSeconds;
                if (wall > 0.5)
                {
                    double pct = (cpu - t.LastCpu) / 1e7 / wall / Environment.ProcessorCount * 100;
                    double rounded = Math.Round(Math.Max(0, pct), 1);
                    if (d.Activity!.CpuPercent is not double old || Math.Abs(old - rounded) >= 1) t.Dirty = true;
                    d.Activity.CpuPercent = rounded;
                    if (rounded < 0.5) d.Activity.IdleSince ??= now;
                    else if (d.Activity.IdleSince is not null) { d.Activity.IdleSince = null; t.Dirty = true; }
                    t.LastCpu = cpu;
                    t.LastCpuAt = at;
                }
            }
            else { t.LastCpu = cpu; t.LastCpuAt = at; }
        }

        if (d.LogPath is null)
        {
            d.Message = "로그 파일을 찾지 못함 — 진행률은 알 수 없음";
            // 로그가 없으면 '살아 있음'만 알 수 있으므로 갱신 시각은 감지 시각
            if ((DateTime.UtcNow - t.LastSave).TotalSeconds >= 20) { d.UpdatedAt = now; t.Dirty = true; }
        }

        if (t.Dirty && (DateTime.UtcNow - t.LastSave).TotalSeconds >= 2)
        {
            Save(d);
            t.LastSave = DateTime.UtcNow;
            t.Dirty = false;
            return true;
        }
        return false;
    }

    private bool ReadLog(Tracked t, DateTimeOffset now)
    {
        var d = t.Doc;
        FileInfo fi;
        try { fi = new FileInfo(d.LogPath!); if (!fi.Exists) return false; }
        catch { return false; }
        if (fi.Length == t.LogLen && fi.LastWriteTimeUtc == t.LogWrite) return false;
        t.LogLen = fi.Length;
        t.LogWrite = fi.LastWriteTimeUtc;
        var writeTime = new DateTimeOffset(fi.LastWriteTimeUtc).ToLocalTime();
        d.UpdatedAt = writeTime;

        string tail = TaskFiles.ReadTail(d.LogPath, 60, out _);
        var st = CurrentStage(d);
        var p = LogProgressParser.ParseTail(tail);
        if (p is not null)
        {
            if (st.Done != p.Done) st.ProgressAt = writeTime;
            st.Done = p.Done;
            st.Total = p.Total;
            st.Unit = p.Unit;
            st.Rate = p.Rate;
            st.RateUnit = p.Rate is null ? null : p.Unit + "/s";
            st.EtaSeconds = p.EtaSeconds;
            if (!t.HasWork && !string.IsNullOrEmpty(p.Stage)) st.Name = p.Stage;
            if (!t.HasWork && !string.IsNullOrEmpty(p.Sample)) d.Subject = p.Sample;
        }
        if (t.HasWork) return true; // 메시지는 상태 기록 요약을 유지
        // 기계용 진행 줄(…_PROGRESS_JSON)은 건너뛰고 사람이 읽을 마지막 줄
        string last = tail.Split('\n', '\r').Select(x => x.Trim())
                          .LastOrDefault(x => x.Length > 0 && !x.Contains("_PROGRESS_JSON", StringComparison.Ordinal)) ?? "";
        d.Message = last.Length > 0 ? Fmt.Trim(last, 160) : "로그 비어 있음";
        return true;
    }

    private static StageDoc CurrentStage(TaskDoc d) =>
        d.Stages!.FirstOrDefault(s => s.State is "running" or "validating")
        ?? d.Stages!.FirstOrDefault(s => s.Key == d.CurrentStage) ?? d.Stages![0];

    /// <summary>명령 인자 중 실제로 있는 폴더 (--batch, --out 등)</summary>
    private static List<string> ArgDirs(TaskDoc d)
    {
        var res = new List<string>();
        string cwd = d.Workdir ?? "";
        foreach (var raw in SplitArgs(d.CommandLine ?? "").Skip(1))
        {
            string a = raw.Contains('=') && raw.StartsWith('-') ? raw[(raw.IndexOf('=') + 1)..] : raw;
            if (a.Length < 3 || a.StartsWith('-')) continue;
            try
            {
                string full = Path.IsPathRooted(a) || cwd.Length == 0 ? a : Path.GetFullPath(Path.Combine(cwd, a));
                if (Directory.Exists(full) && !string.Equals(full.TrimEnd('\\'), cwd.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    res.Add(full);
            }
            catch { }
        }
        return res;
    }

    /// <summary>
    /// 작업 폴더의 상태 기록을 읽어 단계 목록(끝난 것 / 진행 중 / 남은 것)과 현재 로그를 반영.
    /// 진행 중 단계의 처리량(로그에서 읽은 값)은 같은 단계면 유지.
    /// </summary>
    private bool RefreshWorkStatus(Tracked t, DateTimeOffset now)
    {
        var d = t.Doc;
        if (t.StatusFile is null)
        {
            if (DateTime.UtcNow < t.NextStatusSearch) return false;
            t.NextStatusSearch = DateTime.UtcNow.AddSeconds(30);
            t.StatusFile = WorkStatus.FindStatusFile(ArgDirs(d));
            if (t.StatusFile is null) return false;
            Trace($"pid={t.Pid} 작업 상태 기록 사용: {t.StatusFile}");
        }
        var ws = WorkStatus.Read(t.StatusFile);
        if (ws is null || ws.Stages.Count == 0) return false;
        if (t.HasWork && ws.LatestWriteUtc == t.StatusSig && ws.CurrentLog == d.LogPath) return false;
        t.StatusSig = ws.LatestWriteUtc;

        var old = d.Stages ?? new List<StageDoc>();
        foreach (var ns in ws.Stages)
        {
            var os = old.FirstOrDefault(o => o.Key == ns.Key);
            if (os is null || ns.State is not ("running" or "validating")) continue;
            ns.Done = os.Done; ns.Total = os.Total; ns.Unit = os.Unit; ns.Rate = os.Rate;
            ns.RateUnit = os.RateUnit; ns.EtaSeconds = os.EtaSeconds; ns.ProgressAt = os.ProgressAt;
        }
        d.Stages = ws.Stages;
        var cur = ws.Stages.FirstOrDefault(s => s.State is "running" or "validating")
                  ?? ws.Stages.FirstOrDefault(s => s.State is "pending");
        d.CurrentStage = cur?.Key ?? ws.Stages[^1].Key;
        if (ws.CurrentGroup is { Length: > 0 } g) d.Subject = g;
        if (ws.CurrentLog is not null && !string.Equals(ws.CurrentLog, d.LogPath, StringComparison.OrdinalIgnoreCase))
        {
            d.LogPath = ws.CurrentLog;
            d.Activity!.LogGuess = ws.CurrentLog;
            t.LogLen = -1; // 새 로그에서 처리량을 다시 읽음
        }
        var write = new DateTimeOffset(ws.LatestWriteUtc).ToLocalTime();
        if (d.UpdatedAt is not DateTimeOffset u || write > u) d.UpdatedAt = write;

        int total = ws.Stages.Count, done = ws.Done, failed = ws.Failed;
        var parts = new List<string> { ws.FromPlan ? $"완료 {done}/{total}" : $"완료 {done}" };
        if (failed > 0) parts.Add($"실패 {failed}");
        if (cur is not null && cur.State is "running" or "validating") parts.Add("진행: " + (cur.Name ?? cur.Key));
        d.Message = string.Join(" · ", parts);
        t.HasWork = true;
        return true;
    }

    /// <summary>
    /// 로그 파일 추정: 명령 인자로 준 .log/.txt 파일 → 작업 폴더·스크립트 폴더(와 log/logs/output 하위)에서
    /// 프로세스 시작 이후 수정된 .log. 표준 출력이 콘솔로만 나가면 찾을 수 없음.
    /// 같은 폴더에서 여러 작업이 돌 때 남의 로그를 가져오지 않도록:
    ///  - 다른 작업이 명령 인자로 지정한 로그는 제외
    ///  - 파일 이름에 이 작업의 인자(예: sample10)가 들어간 로그를 우선
    ///  - 그래도 같은 폴더에 다른 작업이 있어 구분할 수 없으면 추정하지 않음
    /// </summary>
    private string? FindLog(Tracked self)
    {
        var d = self.Doc;
        var start = (d.StartedAt ?? DateTimeOffset.Now).UtcDateTime.AddSeconds(-5);
        string cwd = d.Workdir ?? "";
        var argv = SplitArgs(d.CommandLine ?? "");
        string Resolve(string a, string baseDir)
        {
            try { return Path.IsPathRooted(a) || baseDir.Length == 0 ? a : Path.GetFullPath(Path.Combine(baseDir, a)); }
            catch { return a; }
        }
        var dirs = new List<string>();
        var tokens = new List<string>();
        foreach (var raw in argv.Skip(1))
        {
            string a = raw.Contains('=') && raw.StartsWith('-') ? raw[(raw.IndexOf('=') + 1)..] : raw;
            if (a.Length < 3 || a.StartsWith('-')) continue;
            string full = Resolve(a, cwd);
            string ext = Path.GetExtension(full).ToLowerInvariant();
            if ((ext is ".log" or ".txt") && File.Exists(full)) return full;
            if (Directory.Exists(full)) dirs.Add(full);
            else if (ext == ".py" && File.Exists(full)) dirs.Add(Path.GetDirectoryName(full)!);
            if (ext != ".py")
            {
                string tok = Directory.Exists(full) ? Path.GetFileName(full.TrimEnd('\\', '/'))
                                                    : Path.GetFileNameWithoutExtension(a.TrimEnd('\\', '/'));
                if (tok.Length >= 3 && tok.Any(char.IsLetter)) tokens.Add(tok);
            }
        }
        if (cwd.Length > 0) dirs.Insert(0, cwd);

        // 다른 작업이 인자로 지정한 로그와, 다른 작업이 쓰는 폴더
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var otherDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in _tracked.Values)
        {
            if (ReferenceEquals(o, self)) continue;
            string ocwd = o.Doc.Workdir ?? "";
            if (ocwd.Length > 0) otherDirs.Add(ocwd.TrimEnd('\\'));
            foreach (var raw in SplitArgs(o.Doc.CommandLine ?? "").Skip(1))
            {
                string a = raw.Contains('=') && raw.StartsWith('-') ? raw[(raw.IndexOf('=') + 1)..] : raw;
                if (Path.GetExtension(a).ToLowerInvariant() is ".log" or ".txt") claimed.Add(Resolve(a, ocwd));
            }
        }

        var search = new List<string>();
        foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            search.Add(dir);
            foreach (var sub in new[] { "log", "logs", "output", "outputs", "out", "result", "results" })
            {
                string sd = Path.Combine(dir, sub);
                if (Directory.Exists(sd)) search.Add(sd);
            }
        }
        var cands = new List<FileInfo>();
        foreach (var dir in search.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*.log").Take(500))
                    if (f.LastWriteTimeUtc >= start && !claimed.Contains(f.FullName)) cands.Add(f);
            }
            catch { }
        }
        if (cands.Count == 0) return null;
        var named = cands.Where(f => tokens.Any(t => f.Name.Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
        if (named.Count > 0) return named.MaxBy(f => f.LastWriteTimeUtc)!.FullName;
        // 이름으로 구분할 수 없음: 같은 폴더에 다른 자동 감지 작업이 있으면 추정하지 않음
        bool shared = search.Any(s => otherDirs.Contains(s.TrimEnd('\\')));
        return shared ? null : cands.MaxBy(f => f.LastWriteTimeUtc)!.FullName;
    }

    /// <summary>감지 판단 기록 (프로세스마다 한 번). "왜 안 잡히지?"를 확인할 때 auto\_discovery.log 를 보면 됨</summary>
    private static void Trace(string line)
    {
        try
        {
            Directory.CreateDirectory(Paths.Auto);
            string p = Path.Combine(Paths.Auto, "_discovery.log");
            if (File.Exists(p) && new FileInfo(p).Length > 512 * 1024) File.Move(p, p + ".old", true);
            File.AppendAllText(p, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch { }
    }

    // ------------------------------------------------------------ 저장 / 재연결
    private static void Save(TaskDoc d)
    {
        d.Seq = (d.Seq ?? 0) + 1;
        Directory.CreateDirectory(Paths.Auto);
        string path = Path.Combine(Paths.Auto, d.Id + ".json");
        string tmp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(d, Json.Options));
        for (int i = 0; ; i++)
        {
            try { File.Move(tmp, path, true); return; }
            catch (IOException) when (i < 20) { Thread.Sleep(25); }
            catch (UnauthorizedAccessException) when (i < 20) { Thread.Sleep(25); }
        }
    }

    /// <summary>모니터를 다시 켰을 때: 아직 실행 중인 프로세스는 다시 추적, 그 사이 끝난 것은 '종료 코드 모름'.</summary>
    private bool ReattachSaved(IReadOnlyDictionary<string, StoreEntry> entries, DateTimeOffset now)
    {
        bool wrote = false;
        foreach (var e in entries.Values)
        {
            var d = e.Doc;
            if (d is null || d.Source != "auto" || d.State != "running" || d.Pid is not int pid) continue;
            var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, false, pid);
            bool same = false;
            if (!h.IsInvalid && GetProcessTimes(h, out long cr, out _, out _, out _) && d.StartedAt is DateTimeOffset sa)
                same = Math.Abs((DateTime.FromFileTimeUtc(cr) - sa.UtcDateTime).TotalSeconds) < 2 && WaitForSingleObject(h, 0) != 0;
            if (same)
            {
                d.Activity ??= new ActivityDoc();
                _tracked[pid] = new Tracked { Pid = pid, Handle = h, Doc = d, NextLogSearch = DateTime.MinValue };
            }
            else
            {
                h.Dispose();
                d.State = "exited";
                d.FinishedAt = d.UpdatedAt;
                d.Completion = new CompletionDoc { Basis = "exit_code", ExitCode = null };
                d.Message = "모니터가 꺼져 있는 동안 종료됨 — 종료 코드 모름";
                if (d.Stages is { Count: > 0 }) d.Stages[0].State = "exited";
                Save(d);
                wrote = true;
            }
        }
        return wrote;
    }

    // ------------------------------------------------------------ Win32 조회
    private static string? GetCommandLine(SafeProcessHandle h)
    {
        const int ProcessCommandLineInformation = 60;
        NtQueryInformationProcess(h, ProcessCommandLineInformation, IntPtr.Zero, 0, out int len);
        if (len <= 0 || len > 1 << 20) return null;
        IntPtr buf = Marshal.AllocHGlobal(len);
        try
        {
            if (NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, len, out _) != 0) return null;
            var us = Marshal.PtrToStructure<UNICODE_STRING>(buf);
            return us.Buffer == IntPtr.Zero ? null : Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static int GetParentPid(SafeProcessHandle h)
    {
        int size = Marshal.SizeOf<PROCESS_BASIC_INFORMATION>();
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (NtQueryInformationProcess(h, 0, buf, size, out _) != 0) return 0;
            return Marshal.PtrToStructure<PROCESS_BASIC_INFORMATION>(buf).InheritedFromUniqueProcessId.ToInt32();
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>다른 프로세스의 현재 폴더 (64비트 프로세스만, 실패하면 null)</summary>
    private static string? GetCurrentDirectory(int pid)
    {
        if (!Environment.Is64BitProcess) return null;
        using var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid);
        if (h.IsInvalid) return null;
        try
        {
            int size = Marshal.SizeOf<PROCESS_BASIC_INFORMATION>();
            IntPtr buf = Marshal.AllocHGlobal(size);
            IntPtr peb;
            try
            {
                if (NtQueryInformationProcess(h, 0, buf, size, out _) != 0) return null;
                peb = Marshal.PtrToStructure<PROCESS_BASIC_INFORMATION>(buf).PebBaseAddress;
            }
            finally { Marshal.FreeHGlobal(buf); }
            var ptr = new byte[8];
            if (!ReadProcessMemory(h, peb + 0x20, ptr, 8, out _)) return null;       // PEB.ProcessParameters
            IntPtr pp = (IntPtr)BitConverter.ToInt64(ptr, 0);
            var us = new byte[16];
            if (!ReadProcessMemory(h, pp + 0x38, us, 16, out _)) return null;       // CurrentDirectory.DosPath
            int len = BitConverter.ToUInt16(us, 0);
            IntPtr strPtr = (IntPtr)BitConverter.ToInt64(us, 8);
            if (len <= 0 || len > 4096) return null;
            var chars = new byte[len];
            if (!ReadProcessMemory(h, strPtr, chars, len, out _)) return null;
            return System.Text.Encoding.Unicode.GetString(chars);
        }
        catch { return null; }
    }

    public void Dispose()
    {
        foreach (var t in _tracked.Values)
        {
            t.Handle.Dispose();
            foreach (var ch in t.Children) ch.Dispose();
        }
        _tracked.Clear();
    }
}
