// TaskMonitor.Check: 모니터와 같은 판정 로직(Core)을 콘솔에 출력하는 확인 도구.
//   dotnet run --project src/TaskMonitor.Check -- [--dir <상태루트>] [--watch <초>] [--count <회>] [--detail]
using System.Text;
using TaskMonitor;
using TaskMonitor.Core;

Console.OutputEncoding = Encoding.UTF8;
if (args is ["--connection-worker", var workerRoot])
{
    var deadline = DateTime.UtcNow.AddSeconds(25);
    while (!File.Exists(Path.Combine(workerRoot, "stop-worker")) && DateTime.UtcNow < deadline) Thread.Sleep(50);
    return;
}
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
        case "--progress-selftest":
            Environment.Exit(ProgressSelfTest.Run());
            return;
        case "--connection-selftest":
            Environment.Exit(ConnectionSelfTest.Run(args[++i]));
            return;
        case "--shortcut-selftest":
            Environment.Exit(ShortcutSelfTest.Run(args[++i]));
            return;
        case "--discover":
            {
                // 진단: 보이는 python 프로세스와, 자동 감지 코드가 무엇을 기록하는지 출력
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) Paths.SetRoot(args[++i]);
                foreach (var pr in System.Diagnostics.Process.GetProcesses())
                {
                    string nm; try { nm = pr.ProcessName; } catch { continue; }
                    if (!nm.Contains("py", StringComparison.OrdinalIgnoreCase)) continue;
                    string st; try { st = pr.StartTime.ToString("HH:mm:ss"); } catch (Exception ex) { st = "StartTime 오류: " + ex.Message; }
                    Console.WriteLine($"process pid={pr.Id} name={nm} session={pr.SessionId} start={st}");
                }
                var settings = AppSettings.Load();
                Console.WriteLine($"auto_detect={settings.AutoDetect} names={string.Join(",", settings.AutoDetectProcessNames)} min_age={settings.AutoDetectMinAgeSeconds} root={Paths.Root}");
                using var disc = new ProcessDiscovery();
                var st2 = new TaskStore();
                for (int k = 0; k < 3; k++)
                {
                    st2.Refresh();
                    bool wrote = disc.Scan(st2.Entries, settings, DateTimeOffset.Now);
                    Console.WriteLine($"scan {k}: wrote={wrote} error='{disc.LastError}'");
                    Thread.Sleep(3500);
                }
                st2.Refresh();
                foreach (var e in st2.Entries.Values.Where(e => e.Doc?.Source == "auto"))
                    Console.WriteLine($"auto: {e.Id} state={e.Doc!.State} name={e.Doc.Name} log={e.Doc.LogPath}");
                string tr = Path.Combine(Paths.Auto, "_discovery.log");
                if (File.Exists(tr)) Console.WriteLine(File.ReadAllText(tr));
                return;
            }
        case "--workstatus":
            {
                // 진단: 작업 폴더의 상태 기록을 단계 목록으로 읽은 결과
                string wd = args[++i];
                string? sf = WorkStatus.FindStatusFile(new[] { wd });
                Console.WriteLine($"상태 기록: {sf ?? "(없음)"}");
                if (sf is null) return;
                var ws = WorkStatus.Read(sf);
                if (ws is null) { Console.WriteLine("읽지 못함"); return; }
                Console.WriteLine($"작업 상태={ws.JobState} 현재={ws.CurrentGroup} · {ws.CurrentTitle} 계획={ws.FromPlan} 완료={ws.Done}/{ws.Stages.Count} 실패={ws.Failed}");
                Console.WriteLine($"현재 로그={ws.CurrentLog}");
                Console.WriteLine($"마지막 활동={ws.LatestWriteUtc.ToLocalTime():HH:mm:ss}");
                foreach (var s in ws.Stages)
                    Console.WriteLine($"  [{s.State,-9}] {s.Name}  시작={s.StartedAt:HH:mm:ss} 끝={s.FinishedAt:HH:mm:ss} 평소={(s.ExpectedSeconds is double e ? Math.Round(e) + "s" : "-")}");
                if (ws.CurrentLog is not null)
                {
                    var lp = LogProgressParser.ParseTail(TaskFiles.ReadTail(ws.CurrentLog, 60, out _));
                    Console.WriteLine(lp is null ? "현재 로그 진행 표시 없음" : $"현재 단계 진행: {lp.Done}/{lp.Total} {lp.Unit} rate={lp.Rate:0.###} eta={lp.EtaSeconds:0}s");
                }
                return;
            }
        case "--parse-log":
            {
                string tail = TaskFiles.ReadTail(args[++i], 60, out _);
                var p = LogProgressParser.ParseTail(tail);
                Console.OutputEncoding = Encoding.UTF8;
                Console.WriteLine(p is null ? "진행 표시 없음" : $"{p.Format}: {p.Done}/{p.Total} {p.Unit} rate={p.Rate} eta={p.EtaSeconds}s stage={p.Stage} sample={p.Sample}");
                return;
            }
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
    var views = store.Entries.Values.Select(e =>
        {
            var v = Presenter.Build(e, Lookup, probe, eta, now);
            if (v.ExitedNoRecord && store.Reload(e.Id)) v = Presenter.Build(e, Lookup, probe, eta, now);
            return v;
        })
        .OrderBy(v => v.SortGroup).ThenByDescending(v => v.SortTime).ToList();
    Console.WriteLine($"--- {now:HH:mm:ss}  작업 {views.Count}개");
    foreach (var v in views)
    {
        Console.WriteLine($"{v.Symbol} {Pad(v.StatusLabel, 22)} | {Pad(v.Name, 34)} | {Pad(v.StageText, 26)} | {Pad(v.ProgressText, 20)} | {v.EtaShort}");
        foreach (var w in v.Warnings) Console.WriteLine($"      ! {w}");
        if (detail)
        {
            Console.WriteLine($"      {v.TimesText} | {v.UpdatedText}");
            if (v.IsAuto) Console.WriteLine($"      {v.ActivityText}\n      명령: {v.CommandLine}");
            if (v.Message.Length > 0) Console.WriteLine($"      메시지: {v.Message}");
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
