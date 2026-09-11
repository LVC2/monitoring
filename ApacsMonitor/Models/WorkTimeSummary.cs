namespace ApacsMonitor.Models;

public sealed class WorkTimeSummary
{
    public string FullName { get; init; } = "";
    public string CardNumber { get; init; } = "";
    public TimeSpan Today { get; init; }
    public TimeSpan Week { get; init; }
    public TimeSpan Month { get; init; }

    public string TodayText => Format(Today);
    public string WeekText => Format(Week);
    public string MonthText => Format(Month);

    private static string Format(TimeSpan value)
    {
        var totalHours = (int)value.TotalHours;
        return $"{totalHours} ч {value.Minutes:00} мин";
    }
}
