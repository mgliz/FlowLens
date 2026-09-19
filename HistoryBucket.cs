using System.Globalization;

namespace FlowLens;

internal static class HistoryBucket
{
    public static DateTime Hour(DateTime time) => new(time.Year, time.Month, time.Day, time.Hour, 0, 0);

    public static string Key(string interfaceId, DateTime time) =>
        $"{interfaceId}|{time.ToString("yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture)}";

    public static bool IsIncluded(string key, string interfaceId, DateTime? start, DateTime? end)
    {
        if (string.IsNullOrWhiteSpace(interfaceId)) return false;
        var prefix = interfaceId + "|";
        if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var text = key[prefix.Length..];
        var daily = text.Length == 10;
        if (!DateTime.TryParseExact(text, daily ? "yyyy-MM-dd" : "yyyy-MM-dd'T'HH",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var bucketStart)) return false;
        // A legacy daily total cannot be split truthfully. Include it only when
        // the requested interval contains the entire calendar day.
        if (bucketStart.Date == DateTime.MaxValue.Date) return false;
        var bucketEnd = daily ? bucketStart.AddDays(1) : bucketStart.AddHours(1);
        return (start is null || bucketStart >= start) && (end is null || bucketEnd <= end);
    }
}
