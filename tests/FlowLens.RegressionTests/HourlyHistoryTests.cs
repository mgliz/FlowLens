using FlowLens;
using System.Text.Json;

internal static class HourlyHistoryTests
{
    public static void Run(Action<bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "FlowLens-hours-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            const string nic = "hourly-adapter";
            var day = new DateTime(2026, 9, 19);
            var dailyProcess = Path.Combine(dir, "daily-process.json");
            var hourlyProcess = Path.Combine(dir, "hourly-process.json");
            var dailyNetwork = Path.Combine(dir, "daily-network.json");
            var hourlyNetwork = Path.Combine(dir, "hourly-network.json");
            var dailyKey = nic + "|2026-09-19";
            File.WriteAllText(dailyProcess, JsonSerializer.Serialize(new[] { new ProcessTrafficHistory
            {
                ProcessName = "Test", Path = @"C:\Test.exe",
                Buckets = new() { [dailyKey] = new TrafficCounters { Ipv4Received = 100 } }
            }}));
            File.WriteAllText(dailyNetwork, JsonSerializer.Serialize(new NetworkTrafficHistoryDocument
            {
                Version = 7, Buckets = new() { [dailyKey] = new NetworkTrafficCounters { Received = 100 } }
            }));
            var originalProcess = File.ReadAllBytes(dailyProcess);
            var originalNetwork = File.ReadAllBytes(dailyNetwork);
            var process = TrafficHistoryStore.LoadWithDailyHistory(hourlyProcess, dailyProcess);
            var network = NetworkTrafficHistoryStore.LoadWithDailyHistory(hourlyNetwork, dailyNetwork);
            var sample = new NetworkTrafficSnapshot(true, true, nic, "Test", 1, 0, 0, 0, 0, 10, 0);
            foreach (var hour in new[] { 9, 10, 11, 23 })
            {
                process.AddDelta("Test", @"C:\Test.exe", new TrafficCounters { Ipv4Received = 10 }, day.AddHours(hour).AddMinutes(59), nic);
                network.AddDelta(sample, day.AddHours(hour).AddMinutes(59));
            }
            process.AddDelta("Test", @"C:\Test.exe", new TrafficCounters { Ipv4Received = 999 }, day.AddHours(10), "other");
            network.AddDelta(sample with { InterfaceId = "other", ReceivedDelta = 999 }, day.AddHours(10));

            void CheckRange(DateTime start, DateTime end, ulong expected, string context)
            {
                var rows = process.BuildSnapshots(TrafficTimeRange.Custom, [], nic, day, start, end);
                var total = network.GetTotals(TrafficTimeRange.Custom, sample, day, start, end);
                check((ulong)rows.Sum(row => (decimal)row.Ipv4Received) == expected && total.Received == expected, context);
            }
            CheckRange(day.AddHours(9), day.AddHours(10), 20, "Both selected hours included; adjacent hours, daily legacy and other NIC excluded.");
            CheckRange(day.AddHours(10), day.AddHours(10), 10, "A single selected hour must include exactly its bucket.");
            CheckRange(day, day.AddHours(23), 140, "A full day includes legacy totals once plus new hourly deltas.");
            CheckRange(day.AddDays(-1).AddHours(23), day.AddHours(0), 0, "Cross-midnight partial range must not invent legacy hourly data.");
            process.Save(); network.Save();
            check(originalProcess.SequenceEqual(File.ReadAllBytes(dailyProcess)) &&
                  originalNetwork.SequenceEqual(File.ReadAllBytes(dailyNetwork)), "Migration must preserve source files byte for byte.");
            check(process.Records.Single().Buckets.ContainsKey(nic + "|2026-09-19T10"), "New history keys retain the hour.");
            process = TrafficHistoryStore.LoadWithDailyHistory(hourlyProcess, dailyProcess);
            network = NetworkTrafficHistoryStore.LoadWithDailyHistory(hourlyNetwork, dailyNetwork);
            CheckRange(day, day.AddHours(23), 140, "Restart must not import daily data a second time.");
            File.WriteAllText(hourlyProcess, "broken"); File.WriteAllText(hourlyNetwork, "broken");
            process = TrafficHistoryStore.LoadWithDailyHistory(hourlyProcess, dailyProcess);
            network = NetworkTrafficHistoryStore.LoadWithDailyHistory(hourlyNetwork, dailyNetwork);
            check(process.LoadError.Length > 0 && network.LoadError.Length > 0, "Corrupt new files must not fall back to daily history.");
            foreach (Action save in new Action[] { process.Save, network.Save })
            {
                var blocked = false;
                try { save(); } catch (InvalidOperationException) { blocked = true; }
                check(blocked, "Corrupt history must block overwrite.");
            }
            check(File.ReadAllText(hourlyProcess) == "broken" && File.ReadAllText(hourlyNetwork) == "broken",
                "Corrupt new history must remain unchanged.");
            var settings = JsonSerializer.Deserialize<AppSettings>("{\"TimeRange\":7,\"CustomRangeStart\":\"2026-09-19\",\"CustomRangeEnd\":\"2026-09-19\"}")!;
            check(settings.CustomStartHour == 0 && settings.CustomEndHour == 23, "Old date-only settings must retain a full-day selection.");
            settings.CustomStartHour = 9; settings.CustomEndHour = 10;
            var roundTrip = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
            check(roundTrip.CustomStartTime == day.AddHours(9) && roundTrip.CustomEndTime == day.AddHours(10), "Hours must survive settings roundtrip.");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
