namespace TaskMonitor.Core;

public sealed class EtaInfo
{
    public string StageText { get; init; } = "-";
    public DateTimeOffset? StageFinish { get; init; }
    public string OverallText { get; init; } = "-";
    public DateTimeOffset? OverallFinish { get; init; }
    /// <summary>ETA 계산에 쓴 처리 속도 (단위/초)</summary>
    public double? Rate { get; init; }
    public static readonly EtaInfo None = new();
}

/// <summary>
/// ETA 는 근거가 있을 때만 계산합니다.
/// - 현재 단계: done/total 과 진행 관측(단계 평균 속도 + 최근 속도 EWMA) 또는 작업이 보고한 eta_seconds.
///   표시 시각은 새 관측이 들어올 때마다 30%씩만 따라가서 순간 속도 변화에 덜 흔들립니다.
/// - 전체: 현재 단계 ETA + 이후 단계 expected_seconds 합. 하나라도 모르면 "정보 부족".
/// </summary>
public sealed class EtaEstimator
{
    private sealed class Track
    {
        public string StageKey = "";
        public double LastDone = double.NaN;
        public DateTimeOffset LastAt;
        public double? Ewma;
        public DateTimeOffset? Shown;
        public string LastObsKey = "";
    }

    private readonly Dictionary<string, Track> _tracks = new(StringComparer.OrdinalIgnoreCase);

    public const double MinElapsedSeconds = 5;
    public const double SmoothAlpha = 0.3;
    public const double EwmaAlpha = 0.3;

    public void Forget(string id) => _tracks.Remove(id);

    public EtaInfo Compute(TaskDoc doc, DateTimeOffset now)
    {
        string state = doc.State ?? "";
        if (state is not ("running" or "validating")) return EtaInfo.None;

        var stages = doc.Stages ?? new List<StageDoc>();
        int idx = stages.FindIndex(s => s.Key == doc.CurrentStage);
        var cur = idx >= 0 ? stages[idx] : null;
        if (cur is null || cur.State is not ("running" or "validating"))
            return new EtaInfo { StageText = "정보 부족", OverallText = "정보 부족" };

        if (!_tracks.TryGetValue(doc.Id, out var tr) || tr.StageKey != cur.Key)
        {
            tr = new Track { StageKey = cur.Key };
            _tracks[doc.Id] = tr;
        }

        DateTimeOffset? candidate = null;
        string stageText;
        double? usedRate = null;
        bool basedOnExpected = false;

        if (cur.Total is double total && total > 0 && cur.Done is double done)
        {
            var pAt = cur.ProgressAt ?? doc.UpdatedAt ?? now;
            string obsKey = $"{done}|{pAt.ToUnixTimeMilliseconds()}|{cur.EtaSeconds}";
            bool newObs = obsKey != tr.LastObsKey;
            if (newObs)
            {
                if (!double.IsNaN(tr.LastDone) && done > tr.LastDone && pAt > tr.LastAt)
                {
                    double inst = (done - tr.LastDone) / (pAt - tr.LastAt).TotalSeconds;
                    tr.Ewma = tr.Ewma is double e ? EwmaAlpha * inst + (1 - EwmaAlpha) * e : inst;
                }
                if (double.IsNaN(tr.LastDone) || done != tr.LastDone)
                {
                    tr.LastDone = done;
                    tr.LastAt = pAt;
                }
            }

            double elapsed = cur.StartedAt is DateTimeOffset st ? (pAt - st).TotalSeconds : double.NaN;
            double remainingUnits = total - done;
            if (remainingUnits <= 0)
            {
                candidate = pAt;
            }
            else if (cur.EtaSeconds is double rep && rep >= 0 && done > 0)
            {
                candidate = pAt.AddSeconds(rep);
                usedRate = cur.Rate;
            }
            else if (done > 0 && elapsed >= MinElapsedSeconds)
            {
                double avg = done / elapsed;
                double rate = tr.Ewma is double e2 ? 0.5 * avg + 0.5 * e2 : avg;
                if (rate > 0)
                {
                    candidate = pAt.AddSeconds(remainingUnits / rate);
                    usedRate = rate;
                }
            }

            if (candidate is DateTimeOffset c)
            {
                if (tr.Shown is null) tr.Shown = c;
                else if (newObs)
                {
                    double diff = (c - tr.Shown.Value).TotalSeconds;
                    double rem = Math.Max(1, (c - now).TotalSeconds);
                    // 크게 어긋나면(예: 정체 후 재개) 바로 맞추고, 아니면 완만하게 따라감
                    tr.Shown = Math.Abs(diff) > Math.Max(rem, 120)
                        ? c
                        : tr.Shown.Value.AddSeconds(SmoothAlpha * diff);
                }
            }
            if (newObs) tr.LastObsKey = obsKey;

            if (remainingUnits <= 0) stageText = "마무리 중";
            else if (tr.Shown is DateTimeOffset shown && candidate is not null) stageText = Fmt.EtaText(shown, now);
            else stageText = "계산 중";
        }
        else if (cur.ExpectedSeconds is double exp && cur.StartedAt is DateTimeOffset st2)
        {
            candidate = st2.AddSeconds(exp);
            tr.Shown = candidate;
            basedOnExpected = true;
            stageText = Fmt.EtaText(candidate.Value, now) + " · 평소 소요 기준";
        }
        else
        {
            stageText = "정보 부족";
        }

        DateTimeOffset? stageFinish = candidate is null ? null : tr.Shown;

        // 전체 ETA
        string overallText;
        DateTimeOffset? overallFinish = null;
        var later = stages.Skip(idx + 1).Where(s => s.State is null or "pending" or "running" or "validating").ToList();
        if (stageFinish is null)
        {
            overallText = later.Count == 0 ? stageText : (stageText == "계산 중" ? "계산 중" : "정보 부족");
        }
        else if (later.Any(s => s.ExpectedSeconds is null))
        {
            overallText = "정보 부족 (이후 단계 소요 시간 모름)";
        }
        else
        {
            var baseT = stageFinish.Value > now ? stageFinish.Value : now;
            overallFinish = baseT.AddSeconds(later.Sum(s => s.ExpectedSeconds!.Value));
            overallText = Fmt.EtaText(overallFinish.Value, now) + (later.Count > 0 || basedOnExpected ? " · 이후 단계 평소 소요 기준" : "");
        }

        return new EtaInfo
        {
            StageText = stageText,
            StageFinish = stageFinish,
            OverallText = overallText,
            OverallFinish = overallFinish,
            Rate = usedRate,
        };
    }
}
