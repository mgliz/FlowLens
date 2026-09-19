using FlowLens;
using System.Text.Json;

internal static class CustomRangeTests
{
    public static void Run(Action<bool, string> check)
    {
        var today = new DateTime(2026, 9, 19);
        const string adapter = "custom-range-adapter";
        var process = new TrafficHistoryStore();
        var physical = new NetworkTrafficHistoryStore();
        var network = new NetworkTrafficSnapshot(true, true, adapter, "Test", 1, 0, 0, 0, 0, 10, 20);
        for (var date = new DateTime(2026, 8, 29); date <= today; date = date.AddDays(1))
        {
            process.AddDelta("CustomTest", @"C:\CustomTest.exe",
                new TrafficCounters { Ipv4Received = 10, Ipv4Sent = 20, Ipv6Received = 3 }, date.AddHours(23), adapter);
            physical.AddDelta(network, date.AddHours(23));
            process.AddDelta("CustomTest", @"C:\CustomTest.exe",
                new TrafficCounters { Ipv4Received = 999 }, date, "other-adapter");
            physical.AddDelta(network with { InterfaceId = "other-adapter", ReceivedDelta = 999 }, date);
        }

        foreach (var (start, end, days) in new[]
        {
            (new DateTime(2026, 8, 31), new DateTime(2026, 9, 2, 23, 0, 0), 3),
            (today, today.AddHours(23), 1),
            (new DateTime(2026, 9, 1, 18, 0, 0), new DateTime(2026, 9, 1, 23, 0, 0), 1),
            (new DateTime(2026, 7, 1), new DateTime(2026, 7, 31, 23, 0, 0), 0)
        })
        {
            var rows = process.BuildSnapshots(TrafficTimeRange.Custom, [], adapter, today, start, end);
            var totals = physical.GetTotals(TrafficTimeRange.Custom, network, today, start, end);
            check((ulong)rows.Sum(row => (decimal)row.Ipv4Received) == (ulong)days * 10 &&
                  (ulong)rows.Sum(row => (decimal)row.Ipv4Sent) == (ulong)days * 20 &&
                  (ulong)rows.Sum(row => (decimal)row.Ipv6Received) == (ulong)days * 3,
                $"Custom {start:d} to {end:d} includes both days and excludes other dates/adapters.");
            check(totals.Received == (ulong)days * 10 && totals.Sent == (ulong)days * 20,
                "Physical custom totals must use the same inclusive calendar dates.");
        }

        foreach (var (start, end) in new (DateTime?, DateTime?)[]
        {
            (null, today), (today, null), (today, today.AddDays(-1)), (today, today.AddDays(1))
        })
        {
            var rejected = false;
            try { process.BuildSnapshots(TrafficTimeRange.Custom, [], adapter, today, start, end); }
            catch (ArgumentException) { rejected = true; }
            check(rejected, "Invalid custom process ranges must fail rather than show all history.");
            rejected = false;
            try { physical.GetTotals(TrafficTimeRange.Custom, network, today, start, end); }
            catch (ArgumentException) { rejected = true; }
            check(rejected, "Invalid custom physical ranges must fail rather than show all history.");
        }

        var settings = new AppSettings
        {
            TimeRange = TrafficTimeRange.Custom,
            CustomRangeStart = today.AddDays(-10), CustomRangeEnd = today.AddDays(-2)
        };
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings));
        check((int)TrafficTimeRange.Custom == 7 && restored?.TimeRange == TrafficTimeRange.Custom &&
              restored.CustomRangeStart == settings.CustomRangeStart && restored.CustomRangeEnd == settings.CustomRangeEnd,
            "Custom dates and selection must survive settings serialization without renumbering old ranges.");
        foreach (var key in new[] { "RangeCustom", "RangeStart", "RangeEnd", "RangeApply", "RangeHint", "RangeInvalid", "RangeApplied" })
            check(Localizer.T("en-US", key) != key && Localizer.T("zh-CN", key) != key,
                $"Custom range text {key} must be translated in both languages.");
    }
}
