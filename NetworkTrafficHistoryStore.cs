using System.IO;
using System.Globalization;
using System.Text.Json;

namespace FlowLens;

public sealed class NetworkTrafficHistoryStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, NetworkTrafficCounters> _buckets = [];

    public static string HistoryPath => Path.Combine(AppSettings.AppDataDir, "network-history-v5.json");
    public static string PreviousHistoryPath => Path.Combine(AppSettings.AppDataDir, "network-history-v4.json");
    public static string PreviousV3HistoryPath => Path.Combine(AppSettings.AppDataDir, "network-history-v3.json");
    public static string PreviousV2HistoryPath => Path.Combine(AppSettings.AppDataDir, "network-history-v2.json");
    public static string LegacyHistoryPath => Path.Combine(AppSettings.AppDataDir, "network-history.json");

    public static NetworkTrafficHistoryStore Load()
    {
        var store = new NetworkTrafficHistoryStore();
        try
        {
            if (!File.Exists(HistoryPath))
            {
                return store;
            }

            var document = JsonSerializer.Deserialize<NetworkTrafficHistoryDocument>(File.ReadAllText(HistoryPath));
            if (document?.Buckets is not null)
            {
                foreach (var pair in document.Buckets)
                {
                    if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
                    {
                        store._buckets[pair.Key] = pair.Value;
                    }
                }
            }
        }
        catch
        {
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
        if (range == TrafficTimeRange.Session)
        {
            return new NetworkTrafficCounters
            {
                Received = current.Received,
                Sent = current.Sent
            };
        }

        var start = TrafficHistoryStore.GetStartDate(range);
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

            if (start is null || bucketDate.Date >= start.Value.Date)
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
            buckets = _buckets.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
        }

        var document = new NetworkTrafficHistoryDocument
        {
            Version = 5,
            Buckets = buckets
        };
        AtomicFile.WriteAllText(HistoryPath, JsonSerializer.Serialize(document));
    }

    public void Clear()
    {
        lock (_gate)
        {
            _buckets.Clear();
        }

        try
        {
            if (File.Exists(HistoryPath))
            {
                File.Delete(HistoryPath);
            }

            if (File.Exists(PreviousHistoryPath))
            {
                File.Delete(PreviousHistoryPath);
            }

            if (File.Exists(PreviousV3HistoryPath))
            {
                File.Delete(PreviousV3HistoryPath);
            }

            if (File.Exists(PreviousV2HistoryPath))
            {
                File.Delete(PreviousV2HistoryPath);
            }

            if (File.Exists(LegacyHistoryPath))
            {
                File.Delete(LegacyHistoryPath);
            }
        }
        catch
        {
        }
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
    public int Version { get; set; } = 5;
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
