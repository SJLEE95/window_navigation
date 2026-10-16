using System.Text.Json;

namespace TaskMonitor.Core;

/// <summary>UI에서 고른 읽기 전용 연결. 자동 감지 기록과 함께 저장한다.</summary>
public sealed class TaskConnection
{
    public string? StatusFile { get; set; }
    public string? LogFile { get; set; }
    public string? StateField { get; set; }
    public string? StageField { get; set; }
    public string? DoneField { get; set; }
    public string? TotalField { get; set; }
    public string? SubjectField { get; set; }
    public double? Total { get; set; }
    public string Unit { get; set; } = "items";
}

public sealed record ConnectionSnapshot(string? State, string? Stage, double? Done, double? Total, string? Subject, DateTimeOffset UpdatedAt);

public static class ConnectionReader
{
    public static Dictionary<string, JsonElement> ReadFields(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 4 * 1024 * 1024) throw new IOException("상태 파일은 4MB 이하만 읽을 수 있습니다.");
        using var json = JsonDocument.Parse(stream);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("상태 파일은 JSON 객체여야 합니다.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        void Walk(JsonElement value, string prefix, int depth)
        {
            if (depth > 5 || fields.Count >= 500) return;
            foreach (var property in value.EnumerateObject())
            {
                if (fields.Count >= 500) return;
                string key = prefix + property.Name;
                if (property.Value.ValueKind == JsonValueKind.Object) Walk(property.Value, key + ".", depth + 1);
                else if (property.Value.ValueKind is not JsonValueKind.Array) fields[key] = property.Value.Clone();
            }
        }
        Walk(json.RootElement, "", 0);
        return fields;
    }

    public static TaskConnection Suggest(string statusFile, string? logFile = null)
    {
        var fields = ReadFields(statusFile);
        string? Pick(params string[] names) => names.FirstOrDefault(fields.ContainsKey);
        var result = new TaskConnection
        {
            StatusFile = statusFile, LogFile = logFile,
            StateField = Pick("state", "status", "current.state", "current.status"),
            StageField = Pick("phase", "stage", "step", "current.phase", "current.stage", "current.step", "condition"),
            SubjectField = Pick("subject", "segment", "sample", "input", "current.subject", "current.segment"),
        };
        // 단위가 맞는 수량끼리만 연결. 현재 입력 번호를 완료 개수로 해석하지 않는다.
        foreach (var (done, total, unit) in new[] { ("done", "total", "items"), ("frame", "total_frames", "frames"),
                    ("local_frame", "total_frames", "frames"), ("completed", "total", "items"), ("current.done", "current.total", "items") })
        {
            if (!fields.TryGetValue(done, out var value) || value.ValueKind != JsonValueKind.Number) continue;
            result.DoneField = done;
            result.TotalField = fields.ContainsKey(total) ? total : null;
            result.Unit = unit;
            break;
        }
        if (fields.TryGetValue("unit", out var u) && u.ValueKind == JsonValueKind.String) result.Unit = u.GetString() ?? result.Unit;
        return result;
    }

    public static ConnectionSnapshot Read(TaskConnection connection)
    {
        if (string.IsNullOrEmpty(connection.StatusFile)) throw new InvalidDataException("상태 파일을 선택하세요.");
        var fields = ReadFields(connection.StatusFile);
        JsonElement? Value(string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (!fields.TryGetValue(name, out var value)) return null; // 단계마다 나타나는 항목이 다를 수 있음
            return value;
        }
        string? Text(string? name) => Value(name) is { } value && value.ValueKind is JsonValueKind.String ? value.GetString() : null;
        double? Number(string? name)
        {
            var value = Value(name);
            if (value is null || value.Value.ValueKind == JsonValueKind.Null) return null;
            if (value.Value.ValueKind != JsonValueKind.Number || !value.Value.TryGetDouble(out double number) || !double.IsFinite(number) || number < 0)
                throw new InvalidDataException($"'{name}' 항목은 0 이상의 숫자여야 합니다.");
            return number;
        }
        double? done = Number(connection.DoneField), total = connection.Total ?? Number(connection.TotalField);
        if (total is double t && (!double.IsFinite(t) || t <= 0 || done > t)) throw new InvalidDataException("전체 수량은 0보다 크고 처리한 수량 이상이어야 합니다.");
        return new ConnectionSnapshot(Text(connection.StateField), Text(connection.StageField), done, total,
                                      Text(connection.SubjectField), File.GetLastWriteTimeUtc(connection.StatusFile));
    }

    public static string? State(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "running" or "started" or "active" or "processing" or "in_progress" or "starting" => "running",
        "waiting" or "waiting_input" or "waiting_inputs" or "waiting_dependency" => "waiting",
        "pending" or "queued" => "pending",
        "validating" or "verifying" => "validating",
        "complete" or "completed" or "done" or "succeeded" or "success" or "ok" or "finished" => "succeeded",
        "failed" or "failure" or "error" => "failed",
        "cancelled" or "canceled" or "stopped" or "aborted" => "cancelled",
        _ => null,
    };

    public static void Apply(TaskDoc doc, TaskConnection connection, ConnectionSnapshot snapshot)
    {
        var work = WorkStatus.Read(connection.StatusFile!);
        if (work is { Stages.Count: > 1 })
        {
            // 계획에 있는 미실행 단계도 보존. 로그에서 읽은 현재 처리량은 다음 읽기 때 유지한다.
            foreach (var stage in work.Stages)
            {
                var old = doc.Stages?.FirstOrDefault(s => s.Key == stage.Key);
                if (old is null || stage.State is not ("running" or "validating")) continue;
                stage.Done = old.Done; stage.Total = old.Total; stage.Unit = old.Unit;
                stage.Rate = old.Rate; stage.ProgressAt = old.ProgressAt;
            }
            doc.Stages = work.Stages;
            doc.CurrentStage = work.Stages.FirstOrDefault(s => s.State is "running" or "validating")?.Key
                               ?? work.Stages.FirstOrDefault(s => s.State == "pending")?.Key ?? work.Stages[^1].Key;
        }
        else if (doc.Stages is not { Count: 1 } || doc.Stages[0].Key != "connected")
        {
            doc.Stages = new() { new StageDoc { Key = "connected", Name = "실행", State = "running" } };
            doc.CurrentStage = "connected";
        }
        var current = doc.Stages!.First(s => s.Key == doc.CurrentStage);
        if (work is not { Stages.Count: > 1 } && snapshot.Stage is { Length: > 0 } stageName) current.Name = stageName;
        if (snapshot.State is { } state && State(state) is { } mapped)
        {
            doc.State = mapped;
            if (work is not { Stages.Count: > 1 }) current.State = mapped == "waiting" ? "pending" : mapped;
            if (mapped is "succeeded" or "failed" or "cancelled")
            {
                doc.FinishedAt = snapshot.UpdatedAt;
                current.FinishedAt = snapshot.UpdatedAt;
                doc.Completion = new() { Basis = "status_file" };
            }
            else
            {
                doc.FinishedAt = null;
                doc.Completion = null;
                current.FinishedAt = null;
            }
        }
        if (snapshot.Subject is not null) doc.Subject = snapshot.Subject;
        if (!string.IsNullOrEmpty(connection.DoneField))
        {
            if (current.Done != snapshot.Done) current.ProgressAt = snapshot.UpdatedAt;
            current.Done = snapshot.Done; current.Total = snapshot.Total; current.Unit = connection.Unit;
            // 연결 전 단계의 속도·ETA를 다른 단위에 재사용하지 않음.
            current.Rate = null; current.RateUnit = null; current.EtaSeconds = null;
        }
        doc.UpdatedAt = snapshot.UpdatedAt;
        doc.Message = string.Join(" · ", new[] { snapshot.Stage, snapshot.Subject, snapshot.State }.Where(v => !string.IsNullOrEmpty(v)));
        doc.ConnectionError = null;
    }
}
