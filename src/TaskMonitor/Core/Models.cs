using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskMonitor.Core;

// taskmonitor/1 계약의 JSON 모델 (docs/STATE_CONTRACT.md). 필드 이름은 snake_case.
public sealed class TaskDoc
{
    public string? Schema { get; set; }
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? Project { get; set; }
    public string? Workdir { get; set; }
    public string? Subject { get; set; }
    public string? State { get; set; }
    public string? Message { get; set; }
    public int? Pid { get; set; }
    public int? ChildPid { get; set; }
    public string? Host { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? ProcessExitedAt { get; set; }
    public double? StaleAfterS { get; set; }
    public string? CurrentStage { get; set; }
    public List<StageDoc>? Stages { get; set; }
    public List<string>? DependsOn { get; set; }
    public WaitingForDoc? WaitingFor { get; set; }
    public string? LogPath { get; set; }
    public List<ResultDoc>? Results { get; set; }
    public ErrorDoc? Error { get; set; }
    public CancelDoc? Cancel { get; set; }
    public CompletionDoc? Completion { get; set; }
    public long? Seq { get; set; }
}

public sealed class StageDoc
{
    public string Key { get; set; } = "";
    public string? Name { get; set; }
    public string? State { get; set; }
    public double? Done { get; set; }
    public double? Total { get; set; }
    public string? Unit { get; set; }
    public double? Rate { get; set; }
    public string? RateUnit { get; set; }
    public double? EtaSeconds { get; set; }
    public double? ExpectedSeconds { get; set; }
    public double? Weight { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? ProgressAt { get; set; }
    public string? Note { get; set; }
}

public sealed class WaitingForDoc
{
    public List<string>? Tasks { get; set; }
    public List<string>? Inputs { get; set; }
    public string? Reason { get; set; }
}

public sealed class ResultDoc
{
    public string? Label { get; set; }
    public string Path { get; set; } = "";
    public string? Kind { get; set; }
}

public sealed class ErrorDoc
{
    public string? Type { get; set; }
    public string? Message { get; set; }
    public string? Detail { get; set; }
    public string? Stage { get; set; }
}

public sealed class CancelDoc
{
    public bool Supported { get; set; }
    public string? Method { get; set; }
}

public sealed class CompletionDoc
{
    public string? Basis { get; set; }
    public int? ExitCode { get; set; }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
