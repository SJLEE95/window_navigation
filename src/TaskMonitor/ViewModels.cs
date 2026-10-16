using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using TaskMonitor.Core;

namespace TaskMonitor;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>시안 B (다크 캡슐) 색. 색은 보조 수단이고 항상 기호·글자를 함께 표시.</summary>
public static class Theme
{
    private static SolidColorBrush B(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static readonly Brush Pending = B("#8A93A3");
    public static readonly Brush Waiting = B("#B4A2F5");
    public static readonly Brush Running = B("#8AB4FF");
    public static readonly Brush Validating = B("#5FD4C4");
    public static readonly Brush Succeeded = B("#9ECE6A");
    public static readonly Brush Failed = B("#FF8FA3");
    public static readonly Brush Cancelled = B("#F0A35E");
    public static readonly Brush Warning = B("#F2C66D");
    public static readonly Brush Exited = B("#8A93A3");
    public static readonly Brush WarningBg = B("#3A2E16");
    public static readonly Brush FailedBg = B("#3B1C22");
    public static readonly Brush Track = B("#2F343E");

    public static Brush For(StatusKind k) => k switch
    {
        StatusKind.Pending => Pending,
        StatusKind.Waiting => Waiting,
        StatusKind.Running => Running,
        StatusKind.Validating => Validating,
        StatusKind.Succeeded => Succeeded,
        StatusKind.Failed => Failed,
        StatusKind.Cancelled => Cancelled,
        StatusKind.Warning => Warning,
        StatusKind.Exited => Exited,
        _ => Pending,
    };
}

/// <summary>목록 한 줄. 인스턴스를 유지한 채 View 만 교체해서 선택 상태가 유지됨.</summary>
public sealed class TaskItem : Observable
{
    private TaskView _v;

    public TaskItem(TaskView v) => _v = v;

    public string Id => _v.Id;

    public TaskView V
    {
        get => _v;
        set
        {
            _v = value;
            Raise(string.Empty); // 모든 파생 속성 갱신
        }
    }

    public Brush Brush => Theme.For(_v.Kind);
    public double ProgressValue => _v.OverallPercent ?? _v.StagePercent ?? (_v.Kind == StatusKind.Succeeded ? 100 : 0);
    public bool HasProgress => _v.OverallPercent is not null || _v.StagePercent is not null || _v.Kind == StatusKind.Succeeded;

    /// <summary>카드의 진행 링 값. 근거가 없으면 NaN (바탕 원만)</summary>
    public double RingValue => _v.Kind == StatusKind.Succeeded ? 100
        : (_v.OverallPercent ?? _v.StagePercent) is double p ? p : double.NaN;

    /// <summary>링 가운데 글자: 진행률 또는 상태 기호</summary>
    public string RingText => _v.Kind switch
    {
        StatusKind.Succeeded => "✓",
        StatusKind.Failed => "✕",
        StatusKind.Cancelled or StatusKind.Exited => "■",
        _ when (_v.OverallPercent ?? _v.StagePercent) is double p => Fmt.Pct(p / 100),
        StatusKind.Waiting or StatusKind.Pending => "⏸",
        StatusKind.Warning => "!",
        _ => "…",
    };

    /// <summary>화면에 보이는 이름: 자동 감지 작업은 프로그램 이름만 (+ 입력 이름)</summary>
    public string DisplayName => Names.Display(_v);
    public string CardTitle => DisplayName;
    /// <summary>마우스를 올리면 뜨는 전체 이름·명령·경로</summary>
    public string Hint => Names.Hint(_v);

    /// <summary>카드 둘째 줄: 상태 + 현재 단계·처리량</summary>
    public string CardLine2
    {
        get
        {
            var cur = _v.Stages.FirstOrDefault(s => s.IsCurrent);
            var parts = new List<string> { _v.StatusLabel };
            if (!_v.IsTerminal && _v.StageShort.Length > 0 && _v.StageShort != "실행") parts.Add(_v.StageShort);
            if (cur is not null && cur.Amount.Length > 0) parts.Add(cur.Amount);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>카드 셋째 줄: 예상 완료 또는 끝난 시각</summary>
    public string CardLine3 => _v.IsTerminal || !_v.IsActive
        ? (_v.FinishedAt is DateTimeOffset f ? $"{f.ToLocalTime():HH:mm} · {_v.TimesText}" : _v.UpdatedText)
        : _v.EtaShort.StartsWith('≈') ? $"{_v.EtaShort} 완료 예상" : $"예상 완료: {_v.EtaShort}";

    public bool IsDimmed => _v.IsTerminal && _v.Kind is not (StatusKind.Failed or StatusKind.Warning);
    public double CardOpacity => IsDimmed ? 0.72 : 1.0;
    public string CurrentAmount => _v.Stages.FirstOrDefault(s => s.IsCurrent)?.Amount is { Length: > 0 } a ? a : "-";
    public string CurrentRate => _v.Stages.FirstOrDefault(s => s.IsCurrent)?.RateText ?? "";
    public bool HasWarnings => _v.Warnings.Count > 0;
    public string WarningsText => string.Join("\n", _v.Warnings);
    public bool HasError => _v.ErrorText.Length > 0;
    public bool HasWaiting => _v.WaitingText.Length > 0;
    public bool HasMessage => _v.Message.Length > 0;
    public string ProjectLine => string.Join("  ·  ", new[]
    {
        _v.IsAuto ? "자동 감지" : "",
        _v.Project,
        _v.Subject.Length > 0 ? "입력 " + _v.Subject : "",
    }.Where(s => s.Length > 0));
}

/// <summary>작업 이름 표시: 화면에는 짧게, 경로·명령은 힌트(툴팁)로</summary>
public static class Names
{
    /// <summary>프로그램 이름. "-m pkg.run_x --a b" → run_x, "C:\a\job.py x" → job. 상태 기록 작업은 기록된 이름 그대로.</summary>
    public static string Program(TaskView v)
    {
        if (!v.IsAuto) return v.Name;
        string n = v.Name.Trim();
        string prog;
        if (n.StartsWith("-m ", StringComparison.Ordinal))
            prog = n[3..].Split(' ', 2)[0].Split('.')[^1];
        else
        {
            int i = n.IndexOf(".py", StringComparison.OrdinalIgnoreCase);
            prog = System.IO.Path.GetFileName((i > 0 ? n[..i] : n.Split(' ')[0]).Trim('"'));
        }
        return prog.Length > 0 ? prog : n;
    }

    public static string Display(TaskView v) =>
        v.IsAuto && v.Subject.Length > 0 ? $"{Program(v)} · {v.Subject}" : Program(v);

    public static string Hint(TaskView v)
    {
        var lines = new List<string> { v.Name };
        if (v.IsAuto)
        {
            if (v.CommandLine.Length > 0) lines.Add(Fmt.Trim(v.CommandLine, 400));
            if (v.ActivityText.Length > 0) lines.Add(v.ActivityText);
        }
        if (v.Workdir.Length > 0) lines.Add("작업 폴더: " + v.Workdir);
        if (!string.IsNullOrEmpty(v.LogPath)) lines.Add("로그: " + v.LogPath);
        return string.Join("\n", lines);
    }
}

/// <summary>캡슐 바의 작업 표시 한 개 (진행 링 또는 경고·실패 알약)</summary>
public sealed class BarChip
{
    public string TaskId { get; init; } = "";
    public bool IsRing { get; init; }
    public bool IsPill => !IsRing;
    public double Ring { get; init; } = double.NaN;
    public string Title { get; init; } = "";
    public string Sub { get; init; } = "";
    public string Eta { get; init; } = "";
    public string PillText { get; init; } = "";
    public Brush Brush { get; init; } = Theme.Pending;
    public Brush PillBg { get; init; } = Theme.WarningBg;
    public string Tooltip { get; init; } = "";
    public string Key => $"{TaskId}|{IsRing}|{Math.Round(Ring)}|{Title}|{Sub}|{Eta}|{PillText}|{Tooltip}";
}

/// <summary>1초마다 상태 폴더를 확인하고 목록·바 데이터를 갱신.</summary>
public sealed class MonitorController : Observable
{
    private readonly TaskStore _store = new();
    private readonly ProcessProbe _probe = new();
    private readonly EtaEstimator _eta = new();
    private readonly ProcessDiscovery _discovery = new();
    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private string _lastError = "";

    public ObservableCollection<TaskItem> Items { get; } = new();
    public ObservableCollection<BarChip> Chips { get; } = new();
    public ShortcutsController Shortcuts { get; } = new();
    public AppSettings Settings { get; }
    public event Action? Updated;

    private string _overflow = "";
    public string Overflow { get => _overflow; private set => Set(ref _overflow, value); }

    private string _summaryText = "";
    /// <summary>"실행 2 · 대기 1 · 확인 1 · 실패 1"</summary>
    public string SummaryText { get => _summaryText; private set => Set(ref _summaryText, value); }

    private bool _isIdle;
    /// <summary>바에 보여줄 작업이 없음</summary>
    public bool IsIdle { get => _isIdle; private set => Set(ref _isIdle, value); }

    public string LastError { get => _lastError; private set => Set(ref _lastError, value); }

    public MonitorController(AppSettings settings)
    {
        Settings = settings;
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        Shortcuts.Load();
        Tick();
        _timer.Start();
    }

    public void Tick()
    {
        try
        {
            _store.Refresh();
            if (_discovery.Scan(_store.Entries, Settings, DateTimeOffset.Now)) _store.Refresh();
            LastError = _discovery.LastError;
        }
        catch (Exception ex)
        {
            LastError = "상태 폴더를 읽지 못했습니다: " + ex.Message;
        }
        var now = DateTimeOffset.Now;
        TaskDoc? Lookup(string id) => _store.Entries.TryGetValue(id, out var e) ? e.Doc : null;
        var views = new List<TaskView>();
        foreach (var e in _store.Entries.Values)
        {
            try
            {
                var v = Presenter.Build(e, Lookup, _probe, _eta, now);
                // 프로세스 종료를 봤다면 그 사이에 쓴 마지막 기록(완료/실패)이 있는지 한 번 더 읽고 판정
                if (v.ExitedNoRecord && _store.Reload(e.Id)) v = Presenter.Build(e, Lookup, _probe, _eta, now);
                views.Add(v);
            }
            catch (Exception ex)
            {
                views.Add(new TaskView { Id = e.Id, Name = e.Id, Kind = StatusKind.Unknown, Symbol = "?", StatusLabel = "표시 오류",
                                         Warnings = { ex.Message }, IsTerminal = true, CanArchive = true, SortGroup = 2, SortTime = now });
            }
        }
        views = views.OrderBy(v => v.SortGroup).ThenByDescending(v => v.SortTime).ThenBy(v => v.Id).ToList();
        SyncItems(views);
        BuildBar(views, now);
        Updated?.Invoke();
    }

    private void SyncItems(List<TaskView> views)
    {
        var byId = Items.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);
        var wanted = new HashSet<string>(views.Select(v => v.Id), StringComparer.OrdinalIgnoreCase);
        for (int i = Items.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Items[i].Id)) Items.RemoveAt(i);
        for (int i = 0; i < views.Count; i++)
        {
            var v = views[i];
            if (byId.TryGetValue(v.Id, out var item))
            {
                item.V = v;
                int cur = Items.IndexOf(item);
                if (cur != i) Items.Move(cur, i);
            }
            else Items.Insert(i, new TaskItem(v));
        }
    }

    private static string ChipTitle(TaskView v) => Fmt.Trim(Names.Program(v), v.IsAuto ? 18 : 20);

    private static string TooltipOf(TaskView v) =>
        $"{Names.Display(v)}\n{v.StatusLabel}\n{v.StageText}\n진행률: {v.ProgressText}\n예상 완료: {v.EtaShort}\n{v.UpdatedText}"
        + (v.Warnings.Count > 0 ? "\n" + string.Join("\n", v.Warnings) : "")
        + (v.IsAuto && v.CommandLine.Length > 0 ? "\n\n" + Fmt.Trim(v.CommandLine, 300) : "");

    private void BuildBar(List<TaskView> views, DateTimeOffset now)
    {
        int running = views.Count(v => v.IsActive && v.Kind is StatusKind.Running or StatusKind.Validating);
        int waiting = views.Count(v => v.IsActive && v.Kind is StatusKind.Waiting or StatusKind.Pending);
        var warnList = views.Where(v => v.Kind == StatusKind.Warning && (v.IsActive || v.ExitedNoRecord ||
                                        (v.FinishedAt is DateTimeOffset wf && now - wf < TimeSpan.FromHours(12)))).ToList();
        var failList = views.Where(v => v.Kind == StatusKind.Failed &&
                                        v.FinishedAt is DateTimeOffset ff && now - ff < TimeSpan.FromHours(12)).ToList();
        int done = views.Count(v => v.Kind == StatusKind.Succeeded);
        var parts = new List<string>();
        if (running > 0) parts.Add($"실행 {running}");
        if (waiting > 0) parts.Add($"대기 {waiting}");
        if (warnList.Count > 0) parts.Add($"확인 {warnList.Count}");
        if (failList.Count > 0) parts.Add($"실패 {failList.Count}");
        if (done > 0) parts.Add($"완료 {done}");
        SummaryText = parts.Count == 0 ? "작업 없음" : string.Join(" · ", parts);

        // 진행 링: 실행·검증·대기 중인 작업 (최근 시작 순)
        var ringViews = views.Where(v => v.IsActive && v.Kind is StatusKind.Running or StatusKind.Validating
                                                                 or StatusKind.Waiting or StatusKind.Pending)
                             .OrderBy(v => v.Kind is StatusKind.Waiting or StatusKind.Pending ? 1 : 0)
                             .ThenByDescending(v => v.SortTime).ToList();
        int max = Math.Max(1, Settings.MaxChips);
        var chips = ringViews.Take(max).Select(v => new BarChip
        {
            TaskId = v.Id,
            IsRing = true,
            Ring = v.OverallPercent ?? v.StagePercent ?? double.NaN,
            Title = ChipTitle(v),
            Sub = v.IsAuto ? v.Subject : "",
            Eta = v.Kind is StatusKind.Waiting or StatusKind.Pending ? "대기"
                : v.EtaShort.StartsWith('≈') ? v.EtaShort.Replace(" (단계)", "") : "",
            Brush = Theme.For(v.Kind),
            Tooltip = TooltipOf(v),
        }).ToList();

        // 경고·실패는 개수 알약으로 (누르면 첫 항목 상세)
        if (warnList.Count > 0)
            chips.Add(new BarChip
            {
                TaskId = warnList[0].Id, IsRing = false, Brush = Theme.Warning, PillBg = Theme.WarningBg,
                PillText = warnList.Count == 1 ? $"{warnList[0].StatusShort} 1" : $"확인 필요 {warnList.Count}",
                Tooltip = string.Join("\n\n", warnList.Take(5).Select(TooltipOf)),
            });
        if (failList.Count > 0)
            chips.Add(new BarChip
            {
                TaskId = failList[0].Id, IsRing = false, Brush = Theme.Failed, PillBg = Theme.FailedBg,
                PillText = $"실패 {failList.Count}",
                Tooltip = string.Join("\n\n", failList.Take(5).Select(TooltipOf)),
            });
        Replace(Chips, chips, (a, b) => a.Key == b.Key);
        Overflow = ringViews.Count > max ? $"+{ringViews.Count - max}" : "";
        IsIdle = chips.Count == 0;
    }

    private static void Replace<T>(ObservableCollection<T> col, List<T> items, Func<T, T, bool> same)
    {
        if (col.Count == items.Count && col.Zip(items).All(p => same(p.First, p.Second))) return;
        col.Clear();
        foreach (var i in items) col.Add(i);
    }
}
