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

public static class Theme
{
    private static SolidColorBrush B(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    // 색은 보조 수단. 항상 기호 + 상태 이름 글자를 함께 표시.
    public static readonly Brush Pending = B("#8A8F98");
    public static readonly Brush Waiting = B("#A78BFA");
    public static readonly Brush Running = B("#4C9AFF");
    public static readonly Brush Validating = B("#2DD4BF");
    public static readonly Brush Succeeded = B("#3FB950");
    public static readonly Brush Failed = B("#F85149");
    public static readonly Brush Cancelled = B("#F0883E");
    public static readonly Brush Warning = B("#E3B341");

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
            Raise();
            Raise(nameof(Brush));
            Raise(nameof(ProgressValue));
            Raise(nameof(HasProgress));
            Raise(nameof(HasWarnings));
            Raise(nameof(WarningsText));
            Raise(nameof(HasError));
            Raise(nameof(HasWaiting));
            Raise(nameof(ProjectLine));
        }
    }

    public Brush Brush => Theme.For(_v.Kind);
    public double ProgressValue => _v.OverallPercent ?? _v.StagePercent ?? (_v.Kind == StatusKind.Succeeded ? 100 : 0);
    public bool HasProgress => _v.OverallPercent is not null || _v.StagePercent is not null || _v.Kind == StatusKind.Succeeded;
    public bool HasWarnings => _v.Warnings.Count > 0;
    public string WarningsText => string.Join("\n", _v.Warnings.Select(w => "⚠ " + w));
    public bool HasError => _v.ErrorText.Length > 0;
    public bool HasWaiting => _v.WaitingText.Length > 0;
    public string ProjectLine => string.Join("   ", new[]
    {
        _v.Project.Length > 0 ? "프로젝트: " + _v.Project : "",
        _v.Subject.Length > 0 ? "입력: " + _v.Subject : "",
        "ID: " + _v.Id,
    }.Where(s => s.Length > 0));
}

public sealed class BarChip
{
    public string TaskId { get; init; } = "";
    public string Symbol { get; init; } = "";
    public string Label { get; init; } = "";
    public string Text { get; init; } = "";
    public string Tooltip { get; init; } = "";
    public Brush Brush { get; init; } = Theme.Pending;
}

public sealed class SummaryPart
{
    public string Text { get; init; } = "";
    public Brush Brush { get; init; } = Theme.Pending;
}

/// <summary>1초마다 상태 폴더를 확인하고 목록·상단 바 데이터를 갱신.</summary>
public sealed class MonitorController : Observable
{
    private readonly TaskStore _store = new();
    private readonly ProcessProbe _probe = new();
    private readonly EtaEstimator _eta = new();
    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private string _lastError = "";

    public ObservableCollection<TaskItem> Items { get; } = new();
    public ObservableCollection<BarChip> Chips { get; } = new();
    public ObservableCollection<SummaryPart> Summary { get; } = new();
    public AppSettings Settings { get; }
    public event Action? Updated;

    private string _overflow = "";
    public string Overflow { get => _overflow; private set => Set(ref _overflow, value); }

    public string LastError { get => _lastError; private set => Set(ref _lastError, value); }

    public MonitorController(AppSettings settings)
    {
        Settings = settings;
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        Tick();
        _timer.Start();
    }

    public void Tick()
    {
        try
        {
            _store.Refresh();
            LastError = "";
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
            try { views.Add(Presenter.Build(e, Lookup, _probe, _eta, now)); }
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

    private void BuildBar(List<TaskView> views, DateTimeOffset now)
    {
        int running = views.Count(v => v.IsActive && v.Kind is StatusKind.Running or StatusKind.Validating);
        int waiting = views.Count(v => v.IsActive && v.Kind is StatusKind.Waiting or StatusKind.Pending);
        int warn = views.Count(v => v.Kind == StatusKind.Warning);
        int failed = views.Count(v => v.Kind == StatusKind.Failed);
        int done = views.Count(v => v.Kind == StatusKind.Succeeded);
        var parts = new List<SummaryPart>();
        if (running > 0) parts.Add(new SummaryPart { Text = $"▶ 실행 {running}", Brush = Theme.Running });
        if (waiting > 0) parts.Add(new SummaryPart { Text = $"⏸ 대기 {waiting}", Brush = Theme.Waiting });
        if (warn > 0) parts.Add(new SummaryPart { Text = $"⚠ 확인 {warn}", Brush = Theme.Warning });
        if (failed > 0) parts.Add(new SummaryPart { Text = $"✕ 실패 {failed}", Brush = Theme.Failed });
        if (done > 0) parts.Add(new SummaryPart { Text = $"✔ 완료 {done}", Brush = Theme.Succeeded });
        if (parts.Count == 0) parts.Add(new SummaryPart { Text = "작업 없음", Brush = Theme.Pending });
        Replace(Summary, parts, (a, b) => a.Text == b.Text);

        var recent = TimeSpan.FromMinutes(Math.Max(1, Settings.RecentFinishedMinutes));
        var chipViews = views.Where(v =>
            !v.IsTerminal || v.Kind == StatusKind.Warning ||
            (v.FinishedAt is DateTimeOffset f && now - f < recent &&
             (Settings.ShowRecentFinished || v.Kind != StatusKind.Succeeded))).ToList();
        int max = Math.Max(1, Settings.MaxChips);
        var chips = chipViews.Take(max).Select(v => new BarChip
        {
            TaskId = v.Id,
            Symbol = v.Symbol,
            Label = v.StatusLabel,
            Brush = Theme.For(v.Kind),
            Text = ChipText(v),
            Tooltip = $"{v.Name}\n{v.StatusLabel}\n{v.StageText}\n진행률: {v.ProgressText}\n예상 완료: {v.EtaShort}\n{v.UpdatedText}"
                      + (v.Warnings.Count > 0 ? "\n" + string.Join("\n", v.Warnings) : "") + "\n\n클릭: 상세 보기",
        }).ToList();
        Replace(Chips, chips, (a, b) => a.TaskId == b.TaskId && a.Text == b.Text && a.Label == b.Label && a.Tooltip == b.Tooltip);
        Overflow = chipViews.Count > max ? $"+{chipViews.Count - max}" : "";
    }

    private static string ChipText(TaskView v)
    {
        var parts = new List<string> { Fmt.Trim(v.Name, 22) };
        if (v.IsActive && v.StageShort.Length > 0) parts.Add(Fmt.Trim(v.StageShort, 18));
        if (v.IsActive && v.ProgressText != "-") parts.Add(v.ProgressText.Replace("단계 ", ""));
        if (v.EtaShort is { Length: > 0 } and not "-") parts.Add(v.EtaShort);
        return string.Join(" · ", parts);
    }

    private static void Replace<T>(ObservableCollection<T> col, List<T> items, Func<T, T, bool> same)
    {
        if (col.Count == items.Count && col.Zip(items).All(p => same(p.First, p.Second))) return;
        col.Clear();
        foreach (var i in items) col.Add(i);
    }
}
