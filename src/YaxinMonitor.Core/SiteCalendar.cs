namespace YaxinMonitor.Core;

/// <summary>网站的账务日从北京时间中午 12:00 开始；投注记录按"账务日 → 次日"查询。</summary>
public static class SiteCalendar
{
    private static readonly TimeSpan ChinaOffset = TimeSpan.FromHours(8);

    public static DateOnly BusinessDate(DateTimeOffset now)
    {
        DateTimeOffset local = now.ToOffset(ChinaOffset);
        DateOnly date = DateOnly.FromDateTime(local.DateTime);
        return local.Hour < 12 ? date.AddDays(-1) : date;
    }

    /// <summary>本周一所在的账务日（周一 12:00 起算）。</summary>
    public static DateOnly WeekStart(DateOnly businessDate) =>
        businessDate.AddDays(-(((int)businessDate.DayOfWeek + 6) % 7));

    public static string Format(DateOnly date) => $"{date.Year}-{date.Month}-{date.Day}";
}
