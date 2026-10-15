using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TaskMonitor.Core;

public sealed record LogProgress(
    double Done, double? Total, double? Rate, string? Unit, double? EtaSeconds,
    string? Sample, string? Stage, string Format, string Line);

/// <summary>
/// 로그 끝부분에서 마지막 진행 표시를 찾음. 지원 형식은 두 가지뿐:
///  1) INFER_PROGRESS_JSON {"frame":462,"total":599,"fps":0.78,"eta_seconds":176}
///  2) tqdm 진행 표시줄  "sample2:  77%|███████▋  | 462/599 [09:52&lt;02:56,  0.78it/s]"
/// </summary>
public static class LogProgressParser
{
    private static readonly Regex Infer = new(@"INFER_PROGRESS_JSON\s+(\{.*\})", RegexOptions.Compiled);

    private static readonly Regex Tqdm = new(
        @"(?:(?<desc>[^|\r\n]{1,60}?):\s*)?(?<pct>\d{1,3})%\|[^|]*\|\s*(?<n>[\d.]+[kMG]?)/(?<tot>[\d.]+[kMG]?)\s*\[(?<el>[\d:]+)<(?<rem>[\d:?]+)(?:,\s*(?:(?<rate>[\d.]+)(?<unit>[A-Za-z]+)/s|(?<spi>[\d.]+)s/(?<unit2>[A-Za-z]+)|\?[A-Za-z]*/s))?",
        RegexOptions.Compiled);

    public static LogProgress? ParseTail(string tail)
    {
        if (string.IsNullOrEmpty(tail)) return null;
        // tqdm 은 \r 로 같은 줄을 덮어씀 → \r, \n 모두로 나눠 마지막부터 확인
        var lines = tail.Split('\n', '\r');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (line.Length < 5) continue;
            var p = TryInfer(line) ?? TryTqdm(line);
            if (p is not null) return p;
        }
        return null;
    }

    private static LogProgress? TryInfer(string line)
    {
        var m = Infer.Match(line);
        if (!m.Success) return null;
        try
        {
            using var doc = JsonDocument.Parse(m.Groups[1].Value);
            var r = doc.RootElement;
            double? Num(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
            string? Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var frame = Num("frame");
            if (frame is null) return null;
            return new LogProgress(frame.Value, Num("total"), Num("fps"), "frames", Num("eta_seconds"),
                                   Str("sample"), Str("stage"), "INFER_PROGRESS_JSON", line.Trim());
        }
        catch (JsonException) { return null; }
    }

    private static LogProgress? TryTqdm(string line)
    {
        var m = Tqdm.Match(line);
        if (!m.Success) return null;
        double n = Scaled(m.Groups["n"].Value), tot = Scaled(m.Groups["tot"].Value);
        if (tot <= 0) return null;
        double? rate = null;
        string unit = "it";
        if (m.Groups["rate"].Success)
        {
            rate = double.Parse(m.Groups["rate"].Value, CultureInfo.InvariantCulture);
            unit = m.Groups["unit"].Value;
        }
        else if (m.Groups["spi"].Success)
        {
            double spi = double.Parse(m.Groups["spi"].Value, CultureInfo.InvariantCulture);
            if (spi > 0) rate = 1 / spi;
            unit = m.Groups["unit2"].Value;
        }
        double? eta = ParseClock(m.Groups["rem"].Value);
        string? desc = m.Groups["desc"].Success ? m.Groups["desc"].Value.Trim() : null;
        return new LogProgress(n, tot, rate, unit, eta, null, string.IsNullOrEmpty(desc) ? null : desc, "tqdm", line.Trim());
    }

    private static double Scaled(string s)
    {
        double mul = 1;
        if (s.EndsWith('k')) { mul = 1e3; s = s[..^1]; }
        else if (s.EndsWith('M')) { mul = 1e6; s = s[..^1]; }
        else if (s.EndsWith('G')) { mul = 1e9; s = s[..^1]; }
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v * mul : 0;
    }

    private static double? ParseClock(string s)
    {
        if (s.Contains('?')) return null;
        var parts = s.Split(':');
        double total = 0;
        foreach (var p in parts)
        {
            if (!int.TryParse(p, out var v)) return null;
            total = total * 60 + v;
        }
        return total;
    }
}
