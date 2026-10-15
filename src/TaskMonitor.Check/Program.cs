// TaskMonitor.Check: 모니터와 같은 판정 로직(Core)을 콘솔에 출력하는 확인 도구.
//   dotnet run --project src/TaskMonitor.Check -- [--dir <상태루트>] [--watch <초>] [--count <회>] [--detail]
using System.Text;
using TaskMonitor.Core;

Console.OutputEncoding = Encoding.UTF8;
string? dir = null;
double watch = 0;
int count = int.MaxValue;
bool detail = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--dir": dir = args[++i]; break;
        case "--watch": watch = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--count": count = int.Parse(args[++i]); break;
        case "--detail": detail = true; break;
    }
}
Paths.SetRoot(dir);
var store = new TaskStore();
var probe = new ProcessProbe();
var eta = new EtaEstimator();
Console.WriteLine($"상태 폴더: {Paths.Tasks}");
for (int n = 0; n < count; n++)
{
    store.Refresh();
    var now = DateTimeOffset.Now;
    TaskDoc? Lookup(string id) => store.Entries.TryGetValue(id, out var e) ? e.Doc : null;
    var views = store.Entries.Values.Select(e => Presenter.Build(e, Lookup, probe, eta, now))
        .OrderBy(v => v.SortGroup).ThenByDescending(v => v.SortTime).ToList();
    Console.WriteLine($"--- {now:HH:mm:ss}  작업 {views.Count}개");
    foreach (var v in views)
    {
        Console.WriteLine($"{v.Symbol} {Pad(v.StatusLabel, 22)} | {Pad(v.Name, 34)} | {Pad(v.StageText, 26)} | {Pad(v.ProgressText, 20)} | {v.EtaShort}");
        foreach (var w in v.Warnings) Console.WriteLine($"      ! {w}");
        if (detail)
        {
            Console.WriteLine($"      {v.TimesText} | {v.UpdatedText}");
            Console.WriteLine($"      단계 ETA: {v.StageEtaText} | 전체 ETA: {v.OverallEtaText} | {v.OverallProgressText}");
            foreach (var s in v.Stages)
                Console.WriteLine($"        {s.Index}. {s.Symbol} {Pad(s.StatusLabel, 6)} {Pad(s.Name, 24)} {Pad(s.Amount, 20)} {Pad(s.RateText, 22)} {s.PercentText} {s.TimeText}");
            if (v.WaitingText.Length > 0) Console.WriteLine("      대기: " + v.WaitingText.Replace("\n", " / "));
            if (v.ErrorText.Length > 0) Console.WriteLine("      오류: " + v.ErrorText.Split('\n')[0]);
            foreach (var r in v.Results) Console.WriteLine($"      결과: {r.Label} {r.Path} {(r.Exists ? "" : "[없음]")}{(r.Incomplete ? "[미완성]" : "")}");
            if (v.CompletionText.Length > 0) Console.WriteLine("      " + v.CompletionText);
            Console.WriteLine("      " + v.CancelText);
        }
    }
    if (watch <= 0) break;
    Thread.Sleep(TimeSpan.FromSeconds(watch));
}

static string Pad(string s, int w)
{
    // 한글은 콘솔에서 두 칸을 차지
    int width = s.Sum(c => c >= 0x1100 ? 2 : 1);
    if (width > w)
    {
        var sb = new StringBuilder();
        int acc = 0;
        foreach (var c in s) { int cw = c >= 0x1100 ? 2 : 1; if (acc + cw > w - 1) break; sb.Append(c); acc += cw; }
        return sb.Append('…').Append(' ', Math.Max(0, w - acc - 1)).ToString();
    }
    return s + new string(' ', w - width);
}
