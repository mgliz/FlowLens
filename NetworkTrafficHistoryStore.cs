using System.IO;
using System.Globalization;
using System.Text.Json;

namespace FlowLens;

public sealed class NetworkTrafficHistoryStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, NetworkTrafficCounters> _buckets = [];
    private readonly string _historyPath;
    private readonly IReadOnlyList<string> _historyPathsToClear;
    private string _loadError = string.Empty;

    public static string HistoryPath => Path.Combine(AppSettings.AppDataDir, "network-history-v7.json");
    public static string PreviousHistoryPath => Path.Combine(AppSettings.AppDataDir, "network-history-v6.json");
    public static string PreviousV5HistoryPath => Path.Combine(AppSettings.AppDataDir, "network-history-v5.json");
    public static string PreviousV4HistoryPath => Path.Combine(AppSettings.AppDataDir, "network-history-v4.json");
    public static string PreviousV3HistoryPath => Path.Combine(AppSettings.AppDataDir, "network-history-v3.json");
    public static string PreviousV2HistoryPath => Path.Combine(AppSettings.AppDataDir, "network-history-v2.json");
    public static string LegacyHistoryPath => Path.Combine(AppSettings.AppDataDir, "network-history.json");
    public static bool HasLegacyHistory => File.Exists(PreviousHistoryPath)
        || File.Exists(PreviousV5HistoryPath)
        || File.Exists(PreviousV4HistoryPath)
        || File.Exists(PreviousV3HistoryPath)
        || File.Exists(PreviousV2HistoryPath)
        || File.Exists(LegacyHistoryPath);

    public NetworkTrafficHistoryStore()
        : this(HistoryPath, ProductionHistoryPaths())
    {
    }

    internal NetworkTrafficHistoryStore(string historyPath)
        : this(historyPath, [historyPath])
    {
    }

    private NetworkTrafficHistoryStore(string historyPath, IReadOnlyList<string> historyPathsToClear)
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

    public static NetworkTrafficHistoryStore Load()
    {
        return Load(HistoryPath, ProductionHistoryPaths());
    }

    internal static NetworkTrafficHistoryStore Load(string historyPath)
    {
        return Load(historyPath, [historyPath]);
    }

    private static NetworkTrafficHistoryStore Load(string historyPath, IReadOnlyList<string> historyPathsToClear)
    {
        var store = new NetworkTrafficHistoryStore(historyPath, historyPathsToClear);
        try
        {
            var document = JsonSerializer.Deserialize<NetworkTrafficHistoryDocument>(File.ReadAllText(historyPath))
                ?? throw new JsonException("The network traffic history document is null.");
            if (document.Version != 7)
            {
                throw new JsonException($"Unsupported network traffic history version {document.Version}.");
            }

            if (document.Buckets is null)
            {
                throw new JsonException("The network traffic history bucket collection is null.");
            }

            var loadedBuckets = new Dictionary<string, NetworkTrafficCounters>();
            foreach (var pair in document.Buckets)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
                {
                    loadedBuckets[pair.Key] = pair.Value;
                }
            }

            foreach (var pair in loadedBuckets)
            {
                store._buckets[pair.Key] = pair.Value;
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
            store._buckets.Clear();
            store._loadError = exception.Message;
        }

        return store;
    }

    public void AddDelta(NetworkTrafficSnapshot snapshot, DateTime timestamp)
    {
        if (!snapshot.IsAvailable || string.IsNullOrWhiteSpace(snapshot.InterfaceId) || (snapshot.ReceivedDelta == 0 && snapshot.SentDelta == 0))
        {
            return;
        }

        lock (_gate)
        {
            var bucketKey = BucketKeyFor(snapshot.InterfaceId, timestamp);
            if (!_buckets.TryGetValue(bucketKey, out var bucket))
            {
                bucket = new NetworkTrafficCounters();
                _buckets[bucketKey] = bucket;
            }

            bucket.Received = AddSaturating(bucket.Received, snapshot.ReceivedDelta);
            bucket.Sent = AddSaturating(bucket.Sent, snapshot.SentDelta);
        }
    }

    public NetworkTrafficCounters GetTotals(TrafficTimeRange range, NetworkTrafficSnapshot current)
    {
        return GetTotals(range, current, DateTime.Today);
    }

    internal NetworkTrafficCounters GetTotals(TrafficTimeRange range, NetworkTrafficSnapshot current, DateTime today,
        DateTime? customStart = null, DateTime? customEnd = null)
    {
        if (range == TrafficTimeRange.Session)
        {
            return new NetworkTrafficCounters
            {
                Received = current.Received,
                Sent = current.Sent
            };
        }

        var (start, endExclusive) = TrafficHistoryStore.GetDateRange(range, today, customStart, customEnd);
        var output = new NetworkTrafficCounters();
        Dictionary<string, NetworkTrafficCounters> buckets;
        lock (_gate)
        {
            buckets = _buckets.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
        }

        foreach (var pair in buckets)
        {
            if (!TryGetBucketDate(pair.Key, current.InterfaceId, out var bucketDate))
            {
                continue;
            }

            if ((start is null || bucketDate.Date >= start.Value.Date)
                && (endExclusive is null || bucketDate.Date < endExclusive.Value.Date))
            {
                output.Received = AddSaturating(output.Received, pair.Value.Received);
                output.Sent = AddSaturating(output.Sent, pair.Value.Sent);
            }
        }

        return output;
    }

    public void Save()
    {
        Dictionary<string, NetworkTrafficCounters> buckets;
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(_loadError))
            {
                throw new InvalidOperationException(
                    $"Network traffic history was not saved because '{Path.GetFileName(_historyPath)}' could not be read. Clear history explicitly before saving. Load error: {_loadError}");
            }

            buckets = _buckets.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
        }

        var document = new NetworkTrafficHistoryDocument
        {
            Version = 7,
            Buckets = buckets
        };
        AtomicFile.WriteAllText(_historyPath, JsonSerializer.Serialize(document));
    }

    public void Clear()
    {
        lock (_gate)
        {
            _buckets.Clear();
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
            PreviousV5HistoryPath,
            PreviousV4HistoryPath,
            PreviousV3HistoryPath,
            PreviousV2HistoryPath,
            LegacyHistoryPath
        ];
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

    private static ulong AddSaturating(ulong left, ulong right)
    {
        return ulong.MaxValue - left < right ? ulong.MaxValue : left + right;
    }
}

public sealed class NetworkTrafficHistoryDocument
{
    public int Version { get; set; } = 7;
    public Dictionary<string, NetworkTrafficCounters> Buckets { get; set; } = [];
}

public sealed class NetworkTrafficCounters
{
    public ulong Received { get; set; }
    public ulong Sent { get; set; }
    public ulong Total => Received + Sent;

    public NetworkTrafficCounters Clone()
    {
        return new NetworkTrafficCounters
        {
            Received = Received,
            Sent = Sent
        };
    }
}
