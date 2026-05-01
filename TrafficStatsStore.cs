using System.IO;
using System.Text.Json;

namespace FlowLens;

public static class TrafficStatsStore
{
    public static string StatsPath => Path.Combine(AppSettings.AppDataDir, "stats.json");

    public static List<PersistedTrafficStats> Load()
    {
        try
        {
            if (!File.Exists(StatsPath))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<PersistedTrafficStats>>(File.ReadAllText(StatsPath)) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void Save(IEnumerable<TrafficRow> rows)
    {
        Directory.CreateDirectory(AppSettings.AppDataDir);
        var records = rows
            .Where(row => row.TotalReceived + row.TotalSent > 0)
            .Select(PersistedTrafficStats.FromRow)
            .OrderBy(record => record.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var json = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(StatsPath, json);
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(StatsPath))
            {
                File.Delete(StatsPath);
            }
        }
        catch
        {
        }
    }

    public static string KeyFor(string processName, string path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? $"name:{processName}"
            : $"path:{path}";
    }
}

public sealed class PersistedTrafficStats
{
    public string ProcessName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public ulong Ipv4Received { get; set; }
    public ulong Ipv4Sent { get; set; }
    public ulong Ipv6Received { get; set; }
    public ulong Ipv6Sent { get; set; }
    public ulong TcpReceived { get; set; }
    public ulong TcpSent { get; set; }
    public ulong UdpReceived { get; set; }
    public ulong UdpSent { get; set; }
    public DateTime LastSeen { get; set; }

    public string StableKey => TrafficStatsStore.KeyFor(ProcessName, Path);

    public TrafficSnapshot ToSnapshot()
    {
        return new TrafficSnapshot(
            0,
            ProcessName,
            Path,
            Ipv4Received,
            Ipv4Sent,
            Ipv6Received,
            Ipv6Sent,
            0,
            0,
            0,
            0,
            0,
            0,
            LastSeen,
            TcpReceived,
            TcpSent,
            UdpReceived,
            UdpSent);
    }

    public static PersistedTrafficStats FromRow(TrafficRow row)
    {
        return new PersistedTrafficStats
        {
            ProcessName = row.ProcessName,
            Path = row.Path,
            Ipv4Received = row.Ipv4Received,
            Ipv4Sent = row.Ipv4Sent,
            Ipv6Received = row.Ipv6Received,
            Ipv6Sent = row.Ipv6Sent,
            TcpReceived = row.TcpReceived,
            TcpSent = row.TcpSent,
            UdpReceived = row.UdpReceived,
            UdpSent = row.UdpSent,
            LastSeen = row.LastSeen
        };
    }
}
