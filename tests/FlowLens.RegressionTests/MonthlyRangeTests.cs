using FlowLens;
using System.Text.Json;

internal static class MonthlyRangeTests
{
    private const string InterfaceId = "monthly-range-interface";

    public static void Run(Action<bool, string> check)
    {
        var persistedRanges = new[]
        {
            TrafficTimeRange.Session,
            TrafficTimeRange.Today,
            TrafficTimeRange.Last7Days,
            TrafficTimeRange.Last30Days,
            TrafficTimeRange.All,
            TrafficTimeRange.ThisMonth,
            TrafficTimeRange.LastMonth
        };
        for (var value = 0; value < persistedRanges.Length; value++)
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>($"{{\"TimeRange\":{value}}}");
            check((int)persistedRanges[value] == value && loaded?.TimeRange == persistedRanges[value],
                $"Persisted time-range value {value} must retain its intended meaning.");
        }

        foreach (var (range, key) in new[]
                 {
                     (TrafficTimeRange.ThisMonth, "RangeThisMonth"),
                     (TrafficTimeRange.LastMonth, "RangeLastMonth")
                 })
        {
            var settings = new AppSettings { TimeRange = range };
            check(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))?.TimeRange == range,
                $"The {range} selection must survive settings serialization.");
            check(Localizer.T("en-US", key) != key && Localizer.T("zh-CN", key) != key,
                $"The monthly range label '{key}' must exist in English and Chinese.");
        }

        foreach (var (today, currentStart, currentEnd, previousStart, previousEnd) in new[]
                 {
                     (new DateTime(2026, 9, 1), new DateTime(2026, 9, 1), new DateTime(2026, 9, 2), new DateTime(2026, 8, 1), new DateTime(2026, 9, 1)),
                     (new DateTime(2026, 9, 5, 18, 30, 0), new DateTime(2026, 9, 1), new DateTime(2026, 9, 6), new DateTime(2026, 8, 1), new DateTime(2026, 9, 1)),
                     (new DateTime(2026, 9, 30), new DateTime(2026, 9, 1), new DateTime(2026, 10, 1), new DateTime(2026, 8, 1), new DateTime(2026, 9, 1)),
                     (new DateTime(2027, 1, 1), new DateTime(2027, 1, 1), new DateTime(2027, 1, 2), new DateTime(2026, 12, 1), new DateTime(2027, 1, 1)),
                     (new DateTime(2024, 2, 29), new DateTime(2024, 2, 1), new DateTime(2024, 3, 1), new DateTime(2024, 1, 1), new DateTime(2024, 2, 1)),
                     (new DateTime(2024, 3, 31), new DateTime(2024, 3, 1), new DateTime(2024, 4, 1), new DateTime(2024, 2, 1), new DateTime(2024, 3, 1))
                 })
        {
            CheckRange(TrafficTimeRange.ThisMonth, today, currentStart, currentEnd, check);
            CheckRange(TrafficTimeRange.LastMonth, today, previousStart, previousEnd, check);
        }
    }

    private static void CheckRange(
        TrafficTimeRange range,
        DateTime today,
        DateTime expectedStart,
        DateTime expectedEnd,
        Action<bool, string> check)
    {
        var context = $"{range} on {today:yyyy-MM-dd}";
        var bounds = TrafficHistoryStore.GetDateRange(range, today);
        check(bounds.Start == expectedStart && bounds.EndExclusive == expectedEnd,
            $"{context} must use the correct inclusive start and exclusive end dates.");

        // Exercise both stores with daily buckets immediately outside either boundary,
        // including future dates and a second adapter whose traffic must stay separate.
        // These stores are never loaded or saved, so production history is untouched.
        var processHistory = new TrafficHistoryStore();
        var networkHistory = new NetworkTrafficHistoryStore();
        var network = new NetworkTrafficSnapshot(true, true, InterfaceId, "Monthly test", 1, 0, 0, 0, 0, 1, 2);
        for (var day = expectedStart.AddDays(-1); day <= expectedEnd.AddDays(1); day = day.AddDays(1))
        {
            processHistory.AddDelta("MonthlyTest", @"C:\MonthlyTest.exe",
                new TrafficCounters { Ipv4Received = 1, Ipv4Sent = 2 }, day.AddHours(23), InterfaceId);
            networkHistory.AddDelta(network, day.AddHours(23));
        }
        processHistory.AddDelta("MonthlyTest", @"C:\MonthlyTest.exe",
            new TrafficCounters { Ipv4Received = 10_000, Ipv4Sent = 20_000 }, expectedStart, "other-interface");
        networkHistory.AddDelta(network with { InterfaceId = "other-interface", ReceivedDelta = 10_000, SentDelta = 20_000 }, expectedStart);

        var rows = processHistory.BuildSnapshots(range, [], InterfaceId, today);
        var totals = networkHistory.GetTotals(range, network, today);
        var expectedReceived = (ulong)(expectedEnd - expectedStart).Days;
        check(rows.Count == 1 && rows[0].Ipv4Received == expectedReceived && rows[0].Ipv4Sent == expectedReceived * 2,
            $"{context} process history must include every selected day and reject adjacent and other-adapter buckets.");
        check(totals.Received == expectedReceived && totals.Sent == expectedReceived * 2,
            $"{context} physical history must include every selected day and reject adjacent and other-adapter buckets.");
        check(rows.Count == 1 && rows[0].Ipv4Received == totals.Received && rows[0].Ipv4Sent == totals.Sent,
            $"{context} must select the same date buckets in process and physical histories.");
    }
}
