using ApacsMonitor.Models;

namespace ApacsMonitor.Services;

public static class WorkTimeCalculator
{
    public static IReadOnlyList<WorkTimeSummary> Calculate(IEnumerable<EventRecord> events, DateTime now)
    {
        var monthStart = new DateTime(now.Year, now.Month, 1);
        var weekStart = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
        var todayStart = now.Date;

        var groups = events
            .Where(x => x.RealTime != DateTime.MinValue && !string.IsNullOrWhiteSpace(x.FullName))
            .GroupBy(x => x.FullName, StringComparer.CurrentCultureIgnoreCase);

        var result = new List<WorkTimeSummary>();

        foreach (var group in groups)
        {
            var ordered = group
                .Where(IsPostEvent)
                .OrderBy(x => x.RealTime)
                .ToList();

            var sessions = BuildSessions(ordered);

            var today = SumForPeriod(sessions, todayStart, now);
            var week = SumForPeriod(sessions, weekStart, now);
            var month = SumForPeriod(sessions, monthStart, now);

            result.Add(new WorkTimeSummary
            {
                FullName = group.Key,
                CardNumber = group
                    .OrderByDescending(x => x.RealTime)
                    .Select(x => x.CardNumber)
                    .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "",
                Today = today,
                Week = week,
                Month = month
            });
        }

        return result.OrderBy(x => x.FullName).ToList();
    }

    private static bool IsPostEvent(EventRecord item)
    {
        return item.RawObjectName.StartsWith("Turn1_", StringComparison.OrdinalIgnoreCase)
            || item.RawObjectName.StartsWith("Turn2_", StringComparison.OrdinalIgnoreCase);
    }

    private static List<(DateTime Start, DateTime End)> BuildSessions(IReadOnlyList<EventRecord> events)
    {
        var sessions = new List<(DateTime Start, DateTime End)>();
        DateTime? openEntry = null;

        foreach (var item in events)
        {
            var isEntry = item.RawObjectName.StartsWith("Turn1_", StringComparison.OrdinalIgnoreCase);
            var isExit = item.RawObjectName.StartsWith("Turn2_", StringComparison.OrdinalIgnoreCase);

            if (isEntry)
            {
                if (openEntry is null)
                    openEntry = item.RealTime;
                continue;
            }

            if (isExit && openEntry is not null && item.RealTime > openEntry.Value)
            {
                var duration = item.RealTime - openEntry.Value;
                if (duration <= TimeSpan.FromHours(24))
                    sessions.Add((openEntry.Value, item.RealTime));

                openEntry = null;
            }
        }

        return sessions;
    }

    private static TimeSpan SumForPeriod(IEnumerable<(DateTime Start, DateTime End)> sessions, DateTime from, DateTime to)
    {
        var total = TimeSpan.Zero;

        foreach (var session in sessions)
        {
            var start = session.Start > from ? session.Start : from;
            var end = session.End < to ? session.End : to;
            if (end > start)
                total += end - start;
        }

        return total;
    }
}
