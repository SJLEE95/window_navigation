using System.Globalization;

namespace TaskMonitor.Core;

public static class Fmt
{
    public static string Clock(DateTimeOffset t, DateTimeOffset now, bool seconds = false)
    {
        var lt = t.ToLocalTime();
        var ln = now.ToLocalTime();
        string time = lt.ToString(seconds ? "HH:mm:ss" : "HH:mm", CultureInfo.InvariantCulture);
        return lt.Date == ln.Date ? time : lt.ToString("M/d ", CultureInfo.InvariantCulture) + time;
    }

    public static string Span(TimeSpan s)
    {
        if (s < TimeSpan.Zero) s = TimeSpan.Zero;
        if (s.TotalDays >= 1) return $"{(int)s.TotalDays}일 {s.Hours}시간";
        if (s.TotalHours >= 1) return $"{(int)s.TotalHours}시간 {s.Minutes}분";
        if (s.TotalMinutes >= 1) return $"{s.Minutes}분 {s.Seconds}초";
        return $"{Math.Max(0, (int)s.TotalSeconds)}초";
    }

    public static string Elapsed(TimeSpan s)
    {
        if (s < TimeSpan.Zero) s = TimeSpan.Zero;
        return s.TotalHours >= 1
            ? $"{(int)s.TotalHours}:{s.Minutes:00}:{s.Seconds:00}"
            : $"{s.Minutes}:{s.Seconds:00}";
    }

    public static string Ago(DateTimeOffset t, DateTimeOffset now)
    {
        var d = now - t;
        if (d.TotalSeconds < 2) return "방금";
        return Span(d) + " 전";
    }

    public static string EtaText(DateTimeOffset finish, DateTimeOffset now)
    {
        return finish >= now
            ? $"≈ {Clock(finish, now)} 예상 (약 {Span(finish - now)} 남음)"
            : $"≈ {Clock(finish, now)} 예상 시각 지남";
    }

    public static string Num(double v) =>
        Math.Abs(v - Math.Round(v)) < 1e-9 ? ((long)Math.Round(v)).ToString("N0", CultureInfo.InvariantCulture)
                                           : v.ToString("0.##", CultureInfo.InvariantCulture);

    public static string Rate(double rate, string? unit)
    {
        string u = string.IsNullOrEmpty(unit) ? "/s" : unit!;
        string r = rate >= 100 ? rate.ToString("0", CultureInfo.InvariantCulture)
                 : rate >= 1 ? rate.ToString("0.0#", CultureInfo.InvariantCulture)
                 : rate.ToString("0.00#", CultureInfo.InvariantCulture);
        string extra = rate > 0 && rate < 1 ? $" ({(1 / rate).ToString("0.#", CultureInfo.InvariantCulture)}초/개)" : "";
        return r + " " + u + extra;
    }

    public static string Pct(double fraction) =>
        (Math.Clamp(fraction, 0, 1) * 100).ToString(fraction < 0.1 ? "0.#" : "0", CultureInfo.InvariantCulture) + "%";

    public static string Trim(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s!.Length <= max ? s : s[..(max - 1)] + "…";
}
