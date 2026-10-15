using System.Text;
using System.Text.RegularExpressions;

namespace TaskMonitor.Core;

public enum StatusKind { Pending, Waiting, Running, Validating, Succeeded, Failed, Cancelled, Warning, Exited, Unknown }

public sealed class StageView
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string StatusLabel { get; init; } = "";
    public string Symbol { get; init; } = "";
    public StatusKind Kind { get; init; }
    public string Amount { get; init; } = "";
    public string RateText { get; init; } = "";
    public string PercentText { get; init; } = "";
    public double? Percent { get; init; }
    public string TimeText { get; init; } = "";
    public bool IsCurrent { get; init; }
}

public sealed class ResultView
{
    public string Label { get; init; } = "";
    public string Path { get; init; } = "";
    public bool IsDir { get; init; }
    public bool Exists { get; init; }
    public bool Incomplete { get; init; }
    public string Note { get; init; } = "";
}

public sealed class TaskView
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Project { get; init; } = "";
    public string Subject { get; init; } = "";
    public bool IsAuto { get; init; }
    public string CommandLine { get; init; } = "";
    public string ActivityText { get; init; } = "";
    public string Workdir { get; init; } = "";
    public StatusKind Kind { get; init; }
    public string Symbol { get; init; } = "";
    public string StatusLabel { get; init; } = "";
    /// <summary>상단 바용 짧은 상태 이름</summary>
    public string StatusShort { get; init; } = "";
    /// <summary>프로세스는 끝났는데 완료·실패 기록이 없음</summary>
    public bool ExitedNoRecord { get; init; }
    public List<string> Warnings { get; init; } = new();
    public string Message { get; init; } = "";
    public string StageText { get; init; } = "";
    public string StageShort { get; init; } = "";
    public string ProgressText { get; init; } = "";
    public double? StagePercent { get; init; }
    public double? OverallPercent { get; init; }
    public string OverallProgressText { get; init; } = "";
    public string EtaShort { get; init; } = "";
    public string StageEtaText { get; init; } = "";
    public string OverallEtaText { get; init; } = "";
    public string TimesText { get; init; } = "";
    public string UpdatedText { get; init; } = "";
    public string WaitingText { get; init; } = "";
    public string ErrorText { get; init; } = "";
    public string CompletionText { get; init; } = "";
    public string CancelText { get; init; } = "";
    public bool CancelSupported { get; init; }
    public bool CancelRequested { get; init; }
    public string? LogPath { get; init; }
    public List<StageView> Stages { get; init; } = new();
    public List<ResultView> Results { get; init; } = new();
    public bool IsTerminal { get; init; }
    public bool IsActive { get; init; }
    public bool NeedsAttention { get; init; }
    public bool CanArchive { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public int SortGroup { get; init; }
    public DateTimeOffset SortTime { get; init; }
}

public static class Presenter
{
    public static string Symbol(StatusKind k) => k switch
    {
        StatusKind.Pending => "◌",
        StatusKind.Waiting => "⏸",
        StatusKind.Running => "▶",
        StatusKind.Validating => "◎",
        StatusKind.Succeeded => "✔",
        StatusKind.Failed => "✕",
        StatusKind.Cancelled => "■",
        StatusKind.Exited => "◼",
        StatusKind.Warning => "⚠",
        _ => "?",
    };

    public static (StatusKind kind, string label) StageState(string? s) => s switch
    {
        "running" => (StatusKind.Running, "실행 중"),
        "validating" => (StatusKind.Validating, "검증 중"),
        "succeeded" => (StatusKind.Succeeded, "완료"),
        "failed" => (StatusKind.Failed, "실패"),
        "skipped" => (StatusKind.Pending, "건너뜀"),
        "cancelled" => (StatusKind.Cancelled, "중단"),
        "exited" => (StatusKind.Exited, "종료"),
        null or "" or "pending" => (StatusKind.Pending, "대기"),
        _ => (StatusKind.Unknown, s!),
    };

    public static TaskView Build(StoreEntry entry, Func<string, TaskDoc?> lookup, ProcessProbe probe,
                                 EtaEstimator eta, DateTimeOffset now)
    {
        var doc = entry.Doc;
        if (doc is null)
        {
            return new TaskView
            {
                Id = entry.Id, Name = entry.Id, Kind = StatusKind.Unknown, Symbol = "?",
                StatusLabel = "파일 오류", Warnings = { entry.ReadError ?? "상태 파일을 읽지 못했습니다" },
                IsTerminal = true, CanArchive = true, NeedsAttention = true, SortGroup = 2, SortTime = now,
            };
        }

        var warnings = new List<string>();
        string state = doc.State ?? "";
        bool terminal = state is "succeeded" or "failed" or "cancelled" or "exited";
        bool auto = doc.Source == "auto";
        var stages = doc.Stages ?? new List<StageDoc>();
        int curIdx = stages.FindIndex(s => s.Key == doc.CurrentStage);
        var cur = curIdx >= 0 ? stages[curIdx] : null;
        bool cancelRequested = TaskFiles.CancelRequested(doc.Id);
        bool cancelSupported = doc.Cancel?.Supported == true;

        // ---- 기본 상태
        StatusKind kind;
        string label;
        var wf = doc.WaitingFor;
        bool waitTasks = wf?.Tasks is { Count: > 0 };
        bool waitInputs = wf?.Inputs is { Count: > 0 };
        switch (state)
        {
            case "pending": kind = StatusKind.Pending; label = "대기"; break;
            case "waiting":
                kind = StatusKind.Waiting;
                label = waitTasks && waitInputs ? "선행 작업·입력 대기" : waitTasks ? "선행 작업 대기" : waitInputs ? "입력 대기" : "선행 작업·입력 대기";
                break;
            case "running": kind = StatusKind.Running; label = "실행 중"; break;
            case "validating": kind = StatusKind.Validating; label = "검증 중"; break;
            case "succeeded": kind = StatusKind.Succeeded; label = "완료"; break;
            case "failed": kind = StatusKind.Failed; label = "실패"; break;
            case "cancelled": kind = StatusKind.Cancelled; label = "중단"; break;
            case "exited":
                // 자동 감지 작업: 종료 코드만 앎. 0 이어도 '완료'로 보지 않음
                int code = doc.Completion?.ExitCode ?? -1;
                kind = code == 0 ? StatusKind.Exited : doc.Completion?.ExitCode is null ? StatusKind.Warning : StatusKind.Failed;
                label = code == 0 ? "종료 · 코드 0" : doc.Completion?.ExitCode is null ? "종료 · 코드 모름" : $"오류 종료 · 코드 {code}";
                break;
            default: kind = StatusKind.Unknown; label = $"알 수 없는 상태({state})"; break;
        }

        bool exitedNoRecord = false;
        bool stale = false;
        double staleAfter = doc.StaleAfterS is > 0 ? doc.StaleAfterS.Value : 120;
        var updated = doc.UpdatedAt ?? new DateTimeOffset(entry.LastWriteUtc, TimeSpan.Zero);

        if (!terminal)
        {
            var ps = probe.Check(doc, now.UtcDateTime);
            if (ps == ProcState.Dead)
            {
                exitedNoRecord = true;
                kind = StatusKind.Warning;
                label = "종료 기록 없음";
                warnings.Add($"프로세스(PID {doc.Pid})가 끝났지만 완료·실패 기록이 없습니다. 정상 완료로 보지 않습니다. " +
                             $"마지막 기록 상태: {StateKo(state)}, 마지막 갱신 {Fmt.Clock(updated, now, true)}");
            }
            else if ((now - updated).TotalSeconds > staleAfter)
            {
                stale = true;
                kind = StatusKind.Warning;
                label += auto ? " · 로그 갱신 없음" : " · 상태 갱신 없음";
                string proc = ps == ProcState.Alive ? "프로세스는 실행 중" : "프로세스 확인 불가";
                warnings.Add($"{(auto ? "로그" : "상태")} 갱신 없음: 마지막 갱신 {Fmt.Clock(updated, now, true)} ({Fmt.Ago(updated, now)}), " +
                             $"기준 {staleAfter:0}초, {proc}. 실패로 판단하지 않고 계속 지켜봅니다.");
            }
            else if (auto && doc.Activity?.IdleSince is DateTimeOffset idle && (now - idle).TotalSeconds > staleAfter)
            {
                stale = true;
                kind = StatusKind.Warning;
                label += " · 활동 없음";
                warnings.Add($"CPU 사용이 {Fmt.Span(now - idle)}째 거의 없습니다 ({Fmt.Clock(idle, now, true)}부터). " +
                             "입력·다른 작업을 기다리거나 GPU 작업 중일 수도 있고, 멈췄을 수도 있습니다 (자동 감지, 확정 아님).");
            }
            if (cancelRequested && cancelSupported) label += " · 중단 요청됨";
        }
        else if (state == "succeeded")
        {
            var notDone = stages.Where(s => s.State is not ("succeeded" or "skipped")).ToList();
            if (notDone.Count > 0)
            {
                kind = StatusKind.Warning;
                label = "완료 기록 불일치";
                warnings.Add("완료로 기록됐지만 끝나지 않은 단계가 있습니다: " + string.Join(", ", notDone.Select(s => s.Name ?? s.Key)));
            }
        }

        string statusShort = exitedNoRecord ? "종료 기록 없음"
            : stale ? "갱신 없음"
            : label.Replace(" · 중단 요청됨", " · 중단 요청");

        // ---- 결과
        var results = new List<ResultView>();
        foreach (var r in doc.Results ?? new List<ResultDoc>())
        {
            if (string.IsNullOrEmpty(r.Path)) continue;
            bool exists = TaskFiles.PathExists(r.Path);
            bool incomplete = TaskFiles.IsIncompletePath(r.Path);
            string note = !exists ? "없음" : incomplete ? "미완성(.partial)" : "";
            results.Add(new ResultView
            {
                Label = r.Label ?? System.IO.Path.GetFileName(r.Path), Path = r.Path,
                IsDir = r.Kind == "dir" || Directory.Exists(r.Path), Exists = exists, Incomplete = incomplete, Note = note,
            });
        }
        if (state == "succeeded" && kind == StatusKind.Succeeded && results.Any(r => !r.Exists || r.Incomplete))
        {
            kind = StatusKind.Warning;
            label = "완료 · 결과 확인 필요";
            statusShort = "결과 확인";
            warnings.Add("결과 경로가 없거나 미완성입니다: " + string.Join(", ", results.Where(r => !r.Exists || r.Incomplete).Select(r => r.Label)));
        }

        // ---- 대기 정보
        var wait = new StringBuilder();
        if (state == "waiting" || state == "pending" || (doc.DependsOn?.Count ?? 0) > 0)
        {
            var deps = (wf?.Tasks ?? new List<string>()).Concat(doc.DependsOn ?? new List<string>()).Distinct().ToList();
            foreach (var dep in deps)
            {
                var dd = lookup(dep);
                string ds = dd is null ? "기록 없음" : StateKo(dd.State);
                wait.Append($"선행 작업 {(dd?.Name is { } n ? $"{n} ({dep})" : dep)}: {ds}\n");
                if (state == "waiting" && dd?.State is "failed" or "cancelled")
                    warnings.Add($"선행 작업 {dd?.Name ?? dep} 이(가) {StateKo(dd?.State)} 상태입니다. 이 작업이 스스로 멈추지 않으면 계속 대기합니다.");
            }
            foreach (var inp in wf?.Inputs ?? new List<string>())
            {
                string st = TaskFiles.PathExists(inp) ? "있음" :
                    TaskFiles.PathExists(inp + ".partial") ? "아직 없음 (.partial 작성 중 — 완성본 아님)" : "아직 없음";
                wait.Append($"입력 {inp}: {st}\n");
            }
            if (!string.IsNullOrEmpty(wf?.Reason) && state == "waiting") wait.Insert(0, wf!.Reason + "\n");
        }

        // ---- 단계
        var stageViews = new List<StageView>();
        for (int i = 0; i < stages.Count; i++)
        {
            var s = stages[i];
            var (sk, sl) = StageState(s.State);
            double? frac = Fraction(s);
            string amount = s.Done is double d
                ? (s.Total is double t && t > 0 ? $"{Fmt.Num(d)} / {Fmt.Num(t)}" : Fmt.Num(d)) + (string.IsNullOrEmpty(s.Unit) ? "" : " " + s.Unit)
                : "";
            string rate = "";
            if (s.Rate is double rr && s.State is "running" or "validating")
                rate = Fmt.Rate(rr, s.RateUnit ?? (s.Unit is { } u ? u + "/s" : "/s"));
            else if (s.State is "running" or "validating" && s.Done is double d2 && d2 > 0 && s.StartedAt is DateTimeOffset st0)
            {
                double el = ((s.ProgressAt ?? now) - st0).TotalSeconds;
                if (el >= EtaEstimator.MinElapsedSeconds) rate = Fmt.Rate(d2 / el, (s.Unit ?? "") + "/s") + " (평균)";
            }
            string time = "";
            if (s.StartedAt is DateTimeOffset sa)
                time = s.FinishedAt is DateTimeOffset fa ? $"소요 {Fmt.Elapsed(fa - sa)}"
                     : exitedNoRecord ? $"{Fmt.Clock(sa, now, true)} 시작 · 기록 중단"
                     : $"{Fmt.Clock(sa, now, true)} 시작 · {Fmt.Elapsed(now - sa)} 경과";
            else if (s.ExpectedSeconds is double ex)
                time = $"평소 약 {Fmt.Span(TimeSpan.FromSeconds(ex))}";
            if (!string.IsNullOrEmpty(s.Note)) time = string.IsNullOrEmpty(time) ? s.Note! : time + " · " + s.Note;
            stageViews.Add(new StageView
            {
                Index = i + 1, Name = s.Name ?? s.Key, StatusLabel = sl, Kind = sk, Symbol = Symbol(sk),
                Amount = amount, RateText = rate, Percent = frac is double f ? f * 100 : null,
                PercentText = frac is double f2 ? Fmt.Pct(f2) : "", TimeText = time, IsCurrent = i == curIdx,
            });
        }

        // ---- 진행률
        double? stageFrac = cur is null ? null : Fraction(cur);
        double? overall = OverallFraction(stages);
        string stageOf = stages.Count > 0 && curIdx >= 0 ? $"{curIdx + 1}/{stages.Count}단계" : "";
        string progress;
        if (state == "succeeded" && kind != StatusKind.Warning) progress = "100%";
        else
        {
            var parts = new List<string>();
            if (stageFrac is double sf) parts.Add(stages.Count > 1 ? $"단계 {Fmt.Pct(sf)}" : Fmt.Pct(sf));
            if (overall is double of && stages.Count > 1) parts.Add($"전체 {Fmt.Pct(of)}");
            else if (stageOf.Length > 0 && stages.Count > 1) parts.Add(stageOf);
            progress = parts.Count > 0 ? string.Join(" · ", parts) : "-";
        }
        int doneStages = stages.Count(s => s.State is "succeeded" or "skipped");
        string overallText = state == "succeeded" && doneStages == stages.Count ? "100% (모든 단계 완료)"
            : overall is double o2 ? $"{Fmt.Pct(o2)} (단계 가중치 기준)"
            : stages.Count > 1 ? $"근거 부족 — {doneStages}/{stages.Count}단계 완료" + (curIdx >= 0 && !terminal ? $", 지금 {curIdx + 1}단계" : "")
            : stageFrac is double sf3 ? $"{Fmt.Pct(sf3)} (단일 단계)" : "근거 부족";

        // ---- ETA
        var e = (exitedNoRecord || terminal) ? EtaInfo.None : eta.Compute(doc, now);
        if (terminal || exitedNoRecord) eta.Forget(doc.Id);
        string etaShort;
        if (terminal)
            etaShort = (doc.FinishedAt is DateTimeOffset fin ? $"{StateKo(state)} {Fmt.Clock(fin, now)}" : StateKo(state));
        else if (exitedNoRecord) etaShort = "-";
        else if (state is "pending" or "waiting") etaShort = "-";
        else if (e.OverallFinish is DateTimeOffset of2) etaShort = $"≈{Fmt.Clock(of2, now)}";
        else if (e.StageFinish is DateTimeOffset sf2) etaShort = $"≈{Fmt.Clock(sf2, now)} (단계)";
        else etaShort = e.StageText.StartsWith("계산") ? "계산 중" : "정보 부족";

        // ---- 시간
        var times = new StringBuilder();
        if (doc.StartedAt is DateTimeOffset started)
        {
            times.Append($"시작 {Fmt.Clock(started, now, true)} · ");
            times.Append(doc.FinishedAt is DateTimeOffset f3 ? $"소요 {Fmt.Elapsed(f3 - started)} · 종료 {Fmt.Clock(f3, now, true)}"
                : exitedNoRecord ? $"마지막 기록까지 {Fmt.Elapsed(updated - started)} (프로세스 종료됨)"
                : $"경과 {Fmt.Elapsed(now - started)}");
        }
        else if (doc.CreatedAt is DateTimeOffset created) times.Append($"등록 {Fmt.Clock(created, now, true)} · 아직 시작 전");
        string updatedText = $"마지막 상태 갱신 {Fmt.Clock(updated, now, true)} ({Fmt.Ago(updated, now)})" + (stale ? " — 상태 갱신 없음" : "");

        // ---- 오류
        string err = "";
        if (doc.Error is { } er)
        {
            var sb = new StringBuilder();
            sb.Append($"[{er.Type ?? "Error"}] {er.Message}");
            if (!string.IsNullOrEmpty(er.Stage)) sb.Append($"  (단계: {stages.FirstOrDefault(s => s.Key == er.Stage)?.Name ?? er.Stage})");
            if (!string.IsNullOrEmpty(er.Detail))
            {
                var lines = er.Detail!.Replace("\r\n", "\n").TrimEnd().Split('\n');
                sb.Append('\n').Append(string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 12))));
            }
            err = sb.ToString();
        }

        string completion = state switch
        {
            "exited" => doc.Completion?.ExitCode is int xc
                ? $"자동 감지: 프로세스가 종료 코드 {xc}(으)로 끝났습니다. 스크립트의 완료 기록은 없으므로 결과는 직접 확인하세요."
                : "자동 감지: 종료 코드를 알 수 없습니다 (모니터가 꺼져 있는 동안 종료).",
            "succeeded" => doc.Completion?.Basis == "exit_code"
                ? $"완료 근거: 래퍼가 종료 코드 {doc.Completion.ExitCode} 확인 (스크립트 자체 완료 기록 아님)"
                : "완료 근거: 작업이 완료를 직접 기록",
            "failed" when doc.Completion?.ExitCode is int ec => $"종료 코드 {ec}",
            _ => "",
        };

        string cancelText = !cancelSupported ? "중단 미지원 — 이 작업은 모니터에서 중단할 수 없습니다"
            : terminal ? "중단 지원 작업 (이미 종료됨)"
            : cancelRequested ? "중단 요청됨 — 작업이 정리 후 멈출 때까지 기다리는 중"
            : "중단 지원 — 요청하면 작업이 스스로 정리한 뒤 멈춥니다";

        // 정렬: 진행 중인 작업이 위, 그 안에서는 등록 순서(최근 먼저). 상태가 바뀌어도 줄이 흔들리지 않도록 고정 키 사용
        int group = !terminal && !exitedNoRecord ? 0 : 1;

        return new TaskView
        {
            Id = doc.Id,
            Name = doc.Name ?? doc.Id,
            Project = doc.Project ?? "",
            Subject = doc.Subject ?? "",
            IsAuto = auto,
            CommandLine = doc.CommandLine ?? "",
            ActivityText = !auto ? "" : string.Join(" · ", new[]
            {
                "자동 감지(상태 기록 없는 프로세스)",
                !terminal && doc.Activity?.CpuPercent is double cpu ? $"CPU {cpu:0}%" : "",
                doc.Activity?.LogGuess is { Length: > 0 } lg ? "로그(추정): " + lg : !terminal ? "로그 파일을 찾지 못함 — 진행률은 알 수 없음" : "",
            }.Where(x => x.Length > 0)),
            Workdir = doc.Workdir ?? "",
            Kind = kind,
            Symbol = Symbol(kind),
            StatusLabel = label,
            StatusShort = kind == StatusKind.Warning && label == "완료 기록 불일치" ? "기록 불일치" : statusShort,
            ExitedNoRecord = exitedNoRecord,
            Warnings = warnings,
            Message = doc.Message ?? "",
            StageText = cur is null ? (terminal && stages.Count > 0 ? "" : "-") : $"{cur.Name ?? cur.Key}" + (stages.Count > 1 ? $" ({curIdx + 1}/{stages.Count})" : ""),
            StageShort = cur?.Name ?? cur?.Key ?? "",
            ProgressText = progress,
            StagePercent = stageFrac * 100,
            OverallPercent = overall * 100,
            OverallProgressText = overallText,
            EtaShort = etaShort,
            StageEtaText = terminal ? "-" : e.StageText,
            OverallEtaText = terminal ? "-" : e.OverallText,
            TimesText = times.ToString(),
            UpdatedText = updatedText,
            WaitingText = wait.ToString().TrimEnd(),
            ErrorText = err,
            CompletionText = completion,
            CancelText = cancelText,
            CancelSupported = cancelSupported && !terminal && !exitedNoRecord,
            CancelRequested = cancelRequested,
            LogPath = doc.LogPath,
            Stages = stageViews,
            Results = results,
            IsTerminal = terminal,
            IsActive = !terminal && !exitedNoRecord,
            NeedsAttention = kind is StatusKind.Warning or StatusKind.Failed or StatusKind.Unknown,
            CanArchive = terminal || exitedNoRecord,
            FinishedAt = doc.FinishedAt,
            SortGroup = group,
            SortTime = doc.CreatedAt ?? doc.StartedAt ?? updated,
        };
    }

    public static string StateKo(string? s) => s switch
    {
        "pending" => "대기",
        "waiting" => "선행 작업·입력 대기",
        "running" => "실행 중",
        "validating" => "검증 중",
        "succeeded" => "완료",
        "failed" => "실패",
        "cancelled" => "중단",
        "exited" => "종료",
        null or "" => "알 수 없음",
        _ => s!,
    };

    private static double? Fraction(StageDoc s)
    {
        if (s.State is "succeeded" or "skipped") return 1;
        if (s.Total is double t && t > 0 && s.Done is double d) return Math.Clamp(d / t, 0, 1);
        if (s.State is null or "pending") return 0;
        return null;
    }

    /// <summary>모든 단계에 weight 가 있고 진행 중 단계의 처리량을 알 때만 전체 진행률 계산.</summary>
    private static double? OverallFraction(List<StageDoc> stages)
    {
        if (stages.Count == 0 || stages.Any(s => s.Weight is not > 0)) return null;
        double sum = 0, wsum = 0;
        foreach (var s in stages)
        {
            double? f = Fraction(s);
            if (f is null) return null;
            sum += s.Weight!.Value * f.Value;
            wsum += s.Weight!.Value;
        }
        return wsum > 0 ? sum / wsum : null;
    }

    private static readonly Regex ErrLine = new(@"(error|exception|traceback|fatal|failed|실패|오류)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string ErrorLinesFromLog(string logTail, int max = 8)
    {
        var lines = logTail.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => ErrLine.IsMatch(l)).ToList();
        return string.Join("\n", lines.Skip(Math.Max(0, lines.Count - max)));
    }
}
