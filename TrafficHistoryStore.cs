using System.IO;
using System.Text.Json;

namespace FlowLens;

public sealed class TrafficHistoryStore
{
    private readonly Dictionary<string, ProcessTrafficHistory> _records = [];

    public static string HistoryPath => Path.Combine(AppSettings.AppDataDir, "history.json");

    public IEnumerable<ProcessTrafficHistory> Records => _records.Values;

    public static TrafficHistoryStore Load()
    {
        var store = new TrafficHistoryStore();
        try
        {
            if (!File.Exists(HistoryPath))
            {
                return store;
            }

            var records = JsonSerializer.Deserialize<List<ProcessTrafficHistory>>(File.ReadAllText(HistoryPath)) ?? [];
            foreach (var record in records)
            {
                if (!string.IsNullOrWhiteSpace(record.StableKey) && IsPersistable(record.ProcessName, record.Path))
                {
                    store._records[record.StableKey] = record;
                }
            }
        }
        catch
        {
        }

        return store;
    }

    public void Save()
    {
        Directory.CreateDirectory(AppSettings.AppDataDir);
        var records = _records.Values
            .Where(record => record.Buckets.Count > 0)
            .OrderBy(record => record.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        File.WriteAllText(HistoryPath, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Clear()
    {
        _records.Clear();
        try
        {
            if (File.Exists(HistoryPath))
            {
                File.Delete(HistoryPath);
            }
        }
        catch
        {
        }
    }

    public void AddDelta(string processName, string path, TrafficCounters delta, DateTime timestamp)
    {
        if (delta.IsZero || !IsPersistable(processName, path))
        {
            return;
        }

        var key = TrafficStatsStore.KeyFor(processName, path);
        if (!_records.TryGetValue(key, out var record))
        {
            record = new ProcessTrafficHistory
            {
                StableKey = key,
                ProcessName = processName,
                Path = path
            };
            _records[key] = record;
        }

        record.ProcessName = processName;
        record.Path = path;
        record.LastSeen = timestamp;
        var bucketKey = timestamp.Date.ToString("yyyy-MM-dd");

        if (!record.Buckets.TryGetValue(bucketKey, out var bucket))
        {
            bucket = new TrafficCounters();
            record.Buckets[bucketKey] = bucket;
        }

        bucket.Add(delta);
    }

    public static bool IsPersistable(string processName, string path)
    {
        return !string.IsNullOrWhiteSpace(processName)
            && !processName.StartsWith("PID ", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(path);
    }

    public List<TrafficSnapshot> BuildSnapshots(TrafficTimeRange range, IEnumerable<TrafficSnapshot> current)
    {
        if (range == TrafficTimeRange.Session)
        {
            return current.ToList();
        }

        var start = GetStartDate(range);
        var output = new Dictionary<string, TrafficSnapshot>();

        foreach (var record in _records.Values)
        {
            var counters = new TrafficCounters();
            foreach (var pair in record.Buckets)
            {
                if (!DateTime.TryParse(pair.Key, out var bucketDate))
                {
                    continue;
                }

                if (start is null || bucketDate.Date >= start.Value.Date)
                {
                    counters.Add(pair.Value);
                }
            }

            if (!counters.IsZero)
            {
                output[record.StableKey] = counters.ToSnapshot(0, record.ProcessName, record.Path, 0, 0, 0, 0, 0, 0, record.LastSeen);
            }
        }

        foreach (var liveSnapshot in AggregateCurrentByStableKey(current))
        {
            var key = TrafficStatsStore.KeyFor(liveSnapshot.ProcessName, liveSnapshot.Path);
            output[key] = output.TryGetValue(key, out var existing)
                ? OverlayLive(existing, liveSnapshot)
                : ZeroTotalsWithLiveRates(liveSnapshot);
        }

        return output.Values.ToList();
    }

    private static IEnumerable<TrafficSnapshot> AggregateCurrentByStableKey(IEnumerable<TrafficSnapshot> current)
    {
        return current
            .GroupBy(snapshot => TrafficStatsStore.KeyFor(snapshot.ProcessName, snapshot.Path))
            .Select(group =>
            {
                var first = group.First();
                return new TrafficSnapshot(
                    group.Count() == 1 ? first.Pid : 0,
                    first.ProcessName,
                    first.Path,
                    Sum(group, snapshot => snapshot.Ipv4Received),
                    Sum(group, snapshot => snapshot.Ipv4Sent),
                    Sum(group, snapshot => snapshot.Ipv6Received),
                    Sum(group, snapshot => snapshot.Ipv6Sent),
                    Sum(group, snapshot => snapshot.Ipv4ReceiveRate),
                    Sum(group, snapshot => snapshot.Ipv4SendRate),
                    Sum(group, snapshot => snapshot.Ipv6ReceiveRate),
                    Sum(group, snapshot => snapshot.Ipv6SendRate),
                    group.Sum(snapshot => snapshot.Ipv4Connections),
                    group.Sum(snapshot => snapshot.Ipv6Connections),
                    group.Max(snapshot => snapshot.LastSeen),
                    Sum(group, snapshot => snapshot.TcpReceived),
                    Sum(group, snapshot => snapshot.TcpSent),
                    Sum(group, snapshot => snapshot.UdpReceived),
                    Sum(group, snapshot => snapshot.UdpSent));
            });
    }

    private static ulong Sum(IEnumerable<TrafficSnapshot> snapshots, Func<TrafficSnapshot, ulong> selector)
    {
        ulong total = 0;
        foreach (var snapshot in snapshots)
        {
            total += selector(snapshot);
        }

        return total;
    }

    private static DateTime? GetStartDate(TrafficTimeRange range)
    {
        var today = DateTime.Today;
        return range switch
        {
            TrafficTimeRange.Today => today,
            TrafficTimeRange.Last7Days => today.AddDays(-6),
            TrafficTimeRange.Last30Days => today.AddDays(-29),
            TrafficTimeRange.All => null,
            _ => today
        };
    }

    private static TrafficSnapshot OverlayLive(TrafficSnapshot oldSnapshot, TrafficSnapshot liveSnapshot)
    {
        return new TrafficSnapshot(
            liveSnapshot.Pid,
            liveSnapshot.ProcessName,
            liveSnapshot.Path,
            oldSnapshot.Ipv4Received,
            oldSnapshot.Ipv4Sent,
            oldSnapshot.Ipv6Received,
            oldSnapshot.Ipv6Sent,
            liveSnapshot.Ipv4ReceiveRate,
            liveSnapshot.Ipv4SendRate,
            liveSnapshot.Ipv6ReceiveRate,
            liveSnapshot.Ipv6SendRate,
            liveSnapshot.Ipv4Connections,
            liveSnapshot.Ipv6Connections,
            liveSnapshot.LastSeen,
            oldSnapshot.TcpReceived,
            oldSnapshot.TcpSent,
            oldSnapshot.UdpReceived,
            oldSnapshot.UdpSent);
    }

    private static TrafficSnapshot ZeroTotalsWithLiveRates(TrafficSnapshot liveSnapshot)
    {
        return new TrafficSnapshot(
            liveSnapshot.Pid,
            liveSnapshot.ProcessName,
            liveSnapshot.Path,
            0,
            0,
            0,
            0,
            liveSnapshot.Ipv4ReceiveRate,
            liveSnapshot.Ipv4SendRate,
            liveSnapshot.Ipv6ReceiveRate,
            liveSnapshot.Ipv6SendRate,
            liveSnapshot.Ipv4Connections,
            liveSnapshot.Ipv6Connections,
            liveSnapshot.LastSeen);
    }
}

public sealed class ProcessTrafficHistory
{
    public string StableKey { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public DateTime LastSeen { get; set; }
    public Dictionary<string, TrafficCounters> Buckets { get; set; } = [];
}

public sealed class TrafficCounters
{
    public ulong Ipv4Received { get; set; }
    public ulong Ipv4Sent { get; set; }
    public ulong Ipv6Received { get; set; }
    public ulong Ipv6Sent { get; set; }
    public ulong TcpReceived { get; set; }
    public ulong TcpSent { get; set; }
    public ulong UdpReceived { get; set; }
    public ulong UdpSent { get; set; }

    public bool IsZero => Ipv4Received + Ipv4Sent + Ipv6Received + Ipv6Sent + TcpReceived + TcpSent + UdpReceived + UdpSent == 0;

    public void Add(TrafficCounters other)
    {
        Ipv4Received += other.Ipv4Received;
        Ipv4Sent += other.Ipv4Sent;
        Ipv6Received += other.Ipv6Received;
        Ipv6Sent += other.Ipv6Sent;
        TcpReceived += other.TcpReceived;
        TcpSent += other.TcpSent;
        UdpReceived += other.UdpReceived;
        UdpSent += other.UdpSent;
    }

    public TrafficSnapshot ToSnapshot(
        int pid,
        string processName,
        string path,
        ulong ipv4ReceiveRate,
        ulong ipv4SendRate,
        ulong ipv6ReceiveRate,
        ulong ipv6SendRate,
        int ipv4Connections,
        int ipv6Connections,
        DateTime lastSeen)
    {
        return new TrafficSnapshot(
            pid,
            processName,
            path,
            Ipv4Received,
            Ipv4Sent,
            Ipv6Received,
            Ipv6Sent,
            ipv4ReceiveRate,
            ipv4SendRate,
            ipv6ReceiveRate,
            ipv6SendRate,
            ipv4Connections,
            ipv6Connections,
            lastSeen,
            TcpReceived,
            TcpSent,
            UdpReceived,
            UdpSent);
    }

    public static TrafficCounters Delta(TrafficSnapshot current, TrafficSnapshot? previous)
    {
        return new TrafficCounters
        {
            Ipv4Received = Subtract(current.Ipv4Received, previous?.Ipv4Received ?? 0),
            Ipv4Sent = Subtract(current.Ipv4Sent, previous?.Ipv4Sent ?? 0),
            Ipv6Received = Subtract(current.Ipv6Received, previous?.Ipv6Received ?? 0),
            Ipv6Sent = Subtract(current.Ipv6Sent, previous?.Ipv6Sent ?? 0),
            TcpReceived = Subtract(current.TcpReceived, previous?.TcpReceived ?? 0),
            TcpSent = Subtract(current.TcpSent, previous?.TcpSent ?? 0),
            UdpReceived = Subtract(current.UdpReceived, previous?.UdpReceived ?? 0),
            UdpSent = Subtract(current.UdpSent, previous?.UdpSent ?? 0)
        };
    }

    private static ulong Subtract(ulong current, ulong previous) => current >= previous ? current - previous : current;
}
