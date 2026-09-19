using System.IO;
using System.Globalization;
using System.Text.Json;

namespace FlowLens;

public sealed class TrafficHistoryStore
{
    public const string UnattributedProcessName = "Unattributed";
    public const string UnattributedPath = "flowlens://unattributed";
    private readonly object _gate = new();
    private readonly Dictionary<string, ProcessTrafficHistory> _records = [];
    private readonly string _historyPath;
    private readonly IReadOnlyList<string> _historyPathsToClear;
    private string _loadError = string.Empty;

    public static string HistoryPath => Path.Combine(AppSettings.AppDataDir, "history-v8.json");
    public static string PreviousHistoryPath => Path.Combine(AppSettings.AppDataDir, "history-v7.json");
    public static string PreviousV6HistoryPath => Path.Combine(AppSettings.AppDataDir, "history-v6.json");
    public static string PreviousV5HistoryPath => Path.Combine(AppSettings.AppDataDir, "history-v5.json");
    public static string PreviousV4HistoryPath => Path.Combine(AppSettings.AppDataDir, "history-v4.json");
    public static string PreviousV3HistoryPath => Path.Combine(AppSettings.AppDataDir, "history-v3.json");
    public static string PreviousV2HistoryPath => Path.Combine(AppSettings.AppDataDir, "history-v2.json");
    public static string LegacyHistoryPath => Path.Combine(AppSettings.AppDataDir, "history.json");
    public static bool HasLegacyHistory => File.Exists(PreviousHistoryPath)
        || File.Exists(PreviousV6HistoryPath)
        || File.Exists(PreviousV5HistoryPath)
        || File.Exists(PreviousV4HistoryPath)
        || File.Exists(PreviousV3HistoryPath)
        || File.Exists(PreviousV2HistoryPath)
        || File.Exists(LegacyHistoryPath);

    public TrafficHistoryStore()
        : this(HistoryPath, ProductionHistoryPaths())
    {
    }

    internal TrafficHistoryStore(string historyPath)
        : this(historyPath, [historyPath])
    {
    }

    private TrafficHistoryStore(string historyPath, IReadOnlyList<string> historyPathsToClear)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(historyPath);
        _historyPath = historyPath;
        _historyPathsToClear = historyPathsToClear;
    }

    public string LoadError
    {
        get
        {
            lock (_gate)
            {
                return _loadError;
            }
        }
    }

    public IEnumerable<ProcessTrafficHistory> Records
    {
        get
        {
            lock (_gate)
            {
                return _records.Values.Select(CloneRecord).ToList();
            }
        }
    }

    public static TrafficHistoryStore Load()
    {
        return Load(HistoryPath, ProductionHistoryPaths());
    }

    internal static TrafficHistoryStore Load(string historyPath)
    {
        return Load(historyPath, [historyPath]);
    }

    private static TrafficHistoryStore Load(string historyPath, IReadOnlyList<string> historyPathsToClear)
    {
        var store = new TrafficHistoryStore(historyPath, historyPathsToClear);
        try
        {
            var records = JsonSerializer.Deserialize<List<ProcessTrafficHistory>>(File.ReadAllText(historyPath))
                ?? throw new JsonException("The process traffic history document is null.");
            var loadedRecords = new Dictionary<string, ProcessTrafficHistory>();
            foreach (var record in records)
            {
                if (record is null)
                {
                    throw new JsonException("The process traffic history contains a null record.");
                }

                if (!IsPersistable(record.ProcessName, record.Path))
                {
                    continue;
                }

                record.Buckets ??= [];
                foreach (var invalidKey in record.Buckets
                             .Where(pair => pair.Value is null)
                             .Select(pair => pair.Key)
                             .ToList())
                {
                    record.Buckets.Remove(invalidKey);
                }

                record.StableKey = TrafficStatsStore.KeyFor(record.ProcessName, record.Path);
                if (loadedRecords.TryGetValue(record.StableKey, out var existing))
                {
                    MergeRecord(existing, record);
                }
                else
                {
                    loadedRecords[record.StableKey] = record;
                }
            }

            foreach (var pair in loadedRecords)
            {
                store._records[pair.Key] = pair.Value;
            }
        }
        catch (FileNotFoundException)
        {
            return store;
        }
        catch (DirectoryNotFoundException)
        {
            return store;
        }
        catch (Exception exception)
        {
            store._records.Clear();
            store._loadError = exception.Message;
        }

        return store;
    }

    private static void MergeRecord(ProcessTrafficHistory target, ProcessTrafficHistory source)
    {
        if (source.LastSeen >= target.LastSeen)
        {
            target.ProcessName = source.ProcessName;
            target.Path = source.Path;
            target.LastSeen = source.LastSeen;
        }

        foreach (var pair in source.Buckets)
        {
            if (pair.Value is null)
            {
                continue;
            }

            if (!target.Buckets.TryGetValue(pair.Key, out var bucket))
            {
                bucket = new TrafficCounters();
                target.Buckets[pair.Key] = bucket;
            }

            bucket.Add(pair.Value);
        }
    }

    private static ProcessTrafficHistory CloneRecord(ProcessTrafficHistory record)
    {
        var clone = new ProcessTrafficHistory
        {
            StableKey = record.StableKey,
            ProcessName = record.ProcessName,
            Path = record.Path,
            LastSeen = record.LastSeen
        };

        foreach (var pair in record.Buckets)
        {
            if (pair.Value is not null)
            {
                clone.Buckets[pair.Key] = pair.Value.Clone();
            }
        }

        return clone;
    }

    public void Save()
    {
        List<ProcessTrafficHistory> records;
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(_loadError))
            {
                throw new InvalidOperationException(
                    $"Process traffic history was not saved because '{Path.GetFileName(_historyPath)}' could not be read. Clear history explicitly before saving. Load error: {_loadError}");
            }

            records = _records.Values
                .Where(record => record.Buckets.Count > 0)
                .OrderBy(record => record.ProcessName, StringComparer.CurrentCultureIgnoreCase)
                .Select(CloneRecord)
                .ToList();
        }

        AtomicFile.WriteAllText(_historyPath, JsonSerializer.Serialize(records));
    }

    public void Clear()
    {
        lock (_gate)
        {
            _records.Clear();
            _loadError = string.Empty;
        }

        try
        {
            foreach (var path in _historyPathsToClear)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
        catch
        {
        }
    }

    private static IReadOnlyList<string> ProductionHistoryPaths()
    {
        return
        [
            HistoryPath,
            PreviousHistoryPath,
            PreviousV6HistoryPath,
            PreviousV5HistoryPath,
            PreviousV4HistoryPath,
            PreviousV3HistoryPath,
            PreviousV2HistoryPath,
            LegacyHistoryPath
        ];
    }

    public void AddDelta(
        string processName,
        string path,
        TrafficCounters delta,
        DateTime timestamp,
        string interfaceId)
    {
        if (delta.IsZero || !IsPersistable(processName, path) || string.IsNullOrWhiteSpace(interfaceId))
        {
            return;
        }

        lock (_gate)
        {
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
            var bucketKey = BucketKeyFor(interfaceId, timestamp);

            if (!record.Buckets.TryGetValue(bucketKey, out var bucket))
            {
                bucket = new TrafficCounters();
                record.Buckets[bucketKey] = bucket;
            }

            bucket.Add(delta);
        }
    }

    public static bool IsPersistable(string processName, string path)
    {
        return !string.IsNullOrWhiteSpace(processName)
            && !processName.StartsWith("PID ", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(path);
    }

    public static bool IsUnattributedPath(string? path)
    {
        return UnattributedPath.Equals(path, StringComparison.OrdinalIgnoreCase);
    }

    public List<TrafficSnapshot> BuildSnapshots(
        TrafficTimeRange range,
        IEnumerable<TrafficSnapshot> current,
        string interfaceId)
    {
        return BuildSnapshots(range, current, interfaceId, DateTime.Today);
    }

    internal List<TrafficSnapshot> BuildSnapshots(
        TrafficTimeRange range,
        IEnumerable<TrafficSnapshot> current,
        string interfaceId,
        DateTime today)
    {
        if (range == TrafficTimeRange.Session)
        {
            return current.ToList();
        }

        var (start, endExclusive) = GetDateRange(range, today);
        var output = new Dictionary<string, TrafficSnapshot>();
        List<ProcessTrafficHistory> records;
        lock (_gate)
        {
            records = _records.Values.Select(CloneRecord).ToList();
        }

        foreach (var record in records)
        {
            var counters = new TrafficCounters();
            foreach (var pair in record.Buckets)
            {
                if (!TryGetBucketDate(pair.Key, interfaceId, out var bucketDate))
                {
                    continue;
                }

                if ((start is null || bucketDate.Date >= start.Value.Date)
                    && (endExclusive is null || bucketDate.Date < endExclusive.Value.Date))
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

    private static string BucketKeyFor(string interfaceId, DateTime timestamp)
    {
        return $"{interfaceId}|{timestamp.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
    }

    private static bool TryGetBucketDate(string key, string interfaceId, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(interfaceId))
        {
            return false;
        }

        var prefix = interfaceId + "|";
        if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return DateTime.TryParseExact(
            key[prefix.Length..],
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
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
            total = AddSaturating(total, selector(snapshot));
        }

        return total;
    }

    internal static (DateTime? Start, DateTime? EndExclusive) GetDateRange(TrafficTimeRange range, DateTime today)
    {
        today = today.Date;
        var monthStart = new DateTime(today.Year, today.Month, 1);
        return range switch
        {
            TrafficTimeRange.Today => (today, null),
            TrafficTimeRange.Last7Days => (today.AddDays(-6), null),
            TrafficTimeRange.Last30Days => (today.AddDays(-29), null),
            TrafficTimeRange.ThisMonth => (monthStart, today.AddDays(1)),
            TrafficTimeRange.LastMonth => (monthStart.AddMonths(-1), monthStart),
            TrafficTimeRange.All => (null, null),
            _ => (today, null)
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

    private static ulong AddSaturating(ulong left, ulong right)
    {
        return ulong.MaxValue - left < right ? ulong.MaxValue : left + right;
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

    public bool IsZero => Ipv4Received == 0
        && Ipv4Sent == 0
        && Ipv6Received == 0
        && Ipv6Sent == 0
        && TcpReceived == 0
        && TcpSent == 0
        && UdpReceived == 0
        && UdpSent == 0;

    public void Add(TrafficCounters other)
    {
        Ipv4Received = AddSaturating(Ipv4Received, other.Ipv4Received);
        Ipv4Sent = AddSaturating(Ipv4Sent, other.Ipv4Sent);
        Ipv6Received = AddSaturating(Ipv6Received, other.Ipv6Received);
        Ipv6Sent = AddSaturating(Ipv6Sent, other.Ipv6Sent);
        TcpReceived = AddSaturating(TcpReceived, other.TcpReceived);
        TcpSent = AddSaturating(TcpSent, other.TcpSent);
        UdpReceived = AddSaturating(UdpReceived, other.UdpReceived);
        UdpSent = AddSaturating(UdpSent, other.UdpSent);
    }

    public TrafficCounters Clone()
    {
        return new TrafficCounters
        {
            Ipv4Received = Ipv4Received,
            Ipv4Sent = Ipv4Sent,
            Ipv6Received = Ipv6Received,
            Ipv6Sent = Ipv6Sent,
            TcpReceived = TcpReceived,
            TcpSent = TcpSent,
            UdpReceived = UdpReceived,
            UdpSent = UdpSent
        };
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

    private static ulong AddSaturating(ulong left, ulong right)
    {
        return ulong.MaxValue - left < right ? ulong.MaxValue : left + right;
    }
}
