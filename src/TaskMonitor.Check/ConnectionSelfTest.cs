using System.Diagnostics;
using System.Reflection;
using TaskMonitor;
using TaskMonitor.Core;

internal static class ConnectionSelfTest
{
    private static int _failed;
    private static void Check(bool ok, string name)
    {
        Console.WriteLine($"{(ok ? "OK" : "FAIL")} {name}");
        if (!ok) _failed++;
    }
    private static bool Rejects(Action action)
    {
        try { action(); return false; } catch { return true; }
    }
    public static int Run(string directory)
    {
        Paths.SetRoot(Path.Combine(Path.GetFullPath(directory), "run-" + Guid.NewGuid().ToString("N")[..8]));
        Directory.CreateDirectory(Paths.Root);
        string source = Path.Combine(Paths.Root, "source");
        Directory.CreateDirectory(source);
        string status = Path.Combine(source, "status.json");
        File.WriteAllText(status, """{"status":"running","phase":"measure","local_frame":12,"pair_index":15,"pair_count":15,"segment":"batch-A"}""");
        var connection = ConnectionReader.Suggest(status);
        Check(connection.DoneField == "local_frame" && connection.TotalField is null && connection.Unit == "frames", "다른 단위의 입력 개수를 프레임 총량으로 사용하지 않음");
        var snapshot = ConnectionReader.Read(connection);
        Check(snapshot is { Done: 12, Total: null, Stage: "measure", Subject: "batch-A" }, "자체 JSON 항목 읽기");
        connection.Total = 100;
        Check(ConnectionReader.Read(connection).Total == 100, "전체 수량 직접 지정");
        connection.Total = 5;
        Check(Rejects(() => ConnectionReader.Read(connection)), "처리량보다 작은 전체 수량 거부");
        connection.Total = null;
        File.WriteAllText(status, """{"current":{"status":"running","stage":"convert","done":3,"total":10}}""");
        var nested = ConnectionReader.Suggest(status);
        Check(ConnectionReader.Read(nested) is { Done: 3, Total: 10, Stage: "convert" }, "중첩 JSON 항목 자동 선택");
        File.WriteAllText(status, """{"current":{"status":"complete","stage":"save"}}""");
        Check(ConnectionReader.Read(nested) is { State: "complete", Done: null }, "단계 전환 시 사라진 수량 항목 허용");
        var doc = new TaskDoc { State = "running" };
        ConnectionReader.Apply(doc, nested, ConnectionReader.Read(nested));
        Check(doc.State == "succeeded" && doc.Completion?.Basis == "status_file" && doc.Stages?.Single().State == "succeeded", "명시적인 완료 상태만 완료로 반영");
        File.WriteAllText(status, """{"current":{"status":"running","done":true}}""");
        Check(Rejects(() => ConnectionReader.Read(nested)), "잘못된 수량 형식 거부");
        File.WriteAllText(status, "{writing");
        Check(Rejects(() => ConnectionReader.Read(nested)), "불완전한 JSON 거부");
        string planned = Path.Combine(source, "planned");
        Directory.CreateDirectory(planned);
        string plannedStatus = Path.Combine(planned, "status.json");
        File.WriteAllText(plannedStatus, """{"status":"running","current":{"step":"convert","status":"running"}}""");
        File.WriteAllText(Path.Combine(planned, "plan.json"), """{"steps":["prepare","convert","verify"]}""");
        var planConnection = ConnectionReader.Suggest(plannedStatus);
        var plannedDoc = new TaskDoc { State = "running" };
        ConnectionReader.Apply(plannedDoc, planConnection, ConnectionReader.Read(planConnection));
        Check(plannedDoc.Stages?.Count == 3 && plannedDoc.CurrentStage == "convert" && plannedDoc.Stages.Count(s => s.State == "pending") == 2,
              "지원하는 전체 계획의 실행 중·미실행 단계 보존");

        Console.WriteLine("실제 테스트 프로세스에 연결 (다른 작업은 조회·변경하지 않음)");
        string stop = Path.Combine(Paths.Root, "stop-worker");
        if (File.Exists(stop)) File.Delete(stop);
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--connection-worker");
        start.ArgumentList.Add(Paths.Root);
        using var child = Process.Start(start)!;
        try
        {
            var process = ProcessDiscovery.ConnectionProcesses().Single(p => p.Pid == child.Id);
            File.WriteAllText(status, """{"status":"waiting_input","phase":"prepare","local_frame":0}""");
            connection = ConnectionReader.Suggest(status);
            var settings = new AppSettings { AutoDetect = false };
            var store = new TaskStore();
            using (var discovery = new ProcessDiscovery())
            {
                discovery.Scan(store.Entries, settings, DateTimeOffset.Now);
                string id = discovery.Connect(process, connection, settings, store.Entries);
                store.Refresh();
                Check(store.Entries[id].Doc is { State: "waiting", Connection: not null }, "자동 감지를 꺼도 UI 연결과 대기 상태 기록");
                Check(discovery.Connect(process, connection, settings, store.Entries) == id && Directory.GetFiles(Paths.Auto, "*.json").Length == 1,
                      "같은 프로세스의 연결 변경은 기존 항목 갱신");
                string log = Path.Combine(source, "worker.log");
                File.WriteAllText(log, """TASK_PROGRESS_JSON {"done":2,"total":10,"unit":"files"}""");
                discovery.Connect(process, new TaskConnection { LogFile = log }, settings, store.Entries);
                Check(discovery.FindDoc(child.Id)?.Stages?.Single() is { Done: 2, Total: 10, Unit: "files" }, "로그만 선택해도 공통 진행률 읽기");
                discovery.Connect(process, connection, settings, store.Entries);
                Check(Rejects(() => discovery.Connect(process with { CreatedUtc = process.CreatedUtc.AddSeconds(1) }, connection, settings, store.Entries)), "PID 재사용·프로세스 교체 방지");
                var known = new Dictionary<string, StoreEntry> { ["recorded"] = new() { Id = "recorded", FilePath = "", Doc = new TaskDoc { Pid = child.Id, State = "running" } } };
                Check(Rejects(() => discovery.Connect(process, connection, settings, known)), "상태 기록 중인 작업의 중복 등록 방지");
            }
            store.Refresh();
            using var restored = new ProcessDiscovery();
            restored.Scan(store.Entries, settings, DateTimeOffset.Now);
            Check(restored.FindDoc(child.Id)?.Connection?.StatusFile == status, "모니터 재시작 후 대기 작업과 연결 복원");
            File.WriteAllText(status, "{writing");
            Thread.Sleep(2100);
            restored.Scan(store.Entries, settings, DateTimeOffset.Now);
            Check(restored.FindDoc(child.Id) is { State: "waiting", ConnectionError.Length: > 0 }, "잘못된 JSON이면 직전 상태 보존·연결 오류 표시");
            File.WriteAllText(status, """{"status":"running","phase":"measure","local_frame":4}""");
            Thread.Sleep(2100);
            restored.Scan(store.Entries, settings, DateTimeOffset.Now);
            Check(restored.FindDoc(child.Id) is { State: "running", ConnectionError: null } recovered && recovered.Stages!.Single().Done == 4,
                  "다음 정상 기록에서 자동 복구");
            File.WriteAllText(status, """{"status":"complete","phase":"save"}""");
            File.WriteAllText(stop, "stop");
            child.WaitForExit(5000);
            restored.Scan(store.Entries, settings, DateTimeOffset.Now);
            store.Refresh();
            Check(store.Entries.Values.Single().Doc is { State: "succeeded", Completion.Basis: "status_file" }, "정상 프로세스 종료 후에도 명시적 완료 기록 유지");
        }
        finally
        {
            File.WriteAllText(stop, "stop");
            child.WaitForExit(5000);
        }
        Console.WriteLine($"연결 검증: 실패 {_failed}개");
        return _failed == 0 ? 0 : 1;
    }
}
