using FlowLens;
using System.Text;
using System.Text.Json;

internal static class HistoryRecoveryTests
{
    private const string InterfaceId = "history-recovery-interface";
    private const string TemporaryDirectoryPrefix = "FlowLens.HistoryRecoveryTests.";

    public static void Run(Action<bool, string> check)
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"{TemporaryDirectoryPrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            CheckVersionedPaths(check);
            CheckProcessHistoryRecovery(temporaryDirectory, check);
            CheckNetworkHistoryRecovery(temporaryDirectory, check);
        }
        catch (Exception exception)
        {
            check(false, $"History recovery tests failed unexpectedly: {exception}");
        }
        finally
        {
            try
            {
                var fullTemporaryDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(temporaryDirectory));
                var fullTemporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
                var directoryName = Path.GetFileName(fullTemporaryDirectory);
                var isOwnedFixtureDirectory = string.Equals(
                        Path.GetDirectoryName(fullTemporaryDirectory),
                        fullTemporaryRoot,
                        StringComparison.OrdinalIgnoreCase)
                    && directoryName.StartsWith(TemporaryDirectoryPrefix, StringComparison.Ordinal)
                    && Guid.TryParseExact(directoryName[TemporaryDirectoryPrefix.Length..], "N", out _);

                if (isOwnedFixtureDirectory)
                {
                    Directory.Delete(fullTemporaryDirectory, recursive: true);
                }
                else
                {
                    check(false, $"Refused to recursively delete an unexpected test path: {fullTemporaryDirectory}");
                }
            }
            catch (Exception exception)
            {
                check(false, $"History recovery test cleanup failed: {exception.Message}");
            }
        }
    }

    private static void CheckVersionedPaths(Action<bool, string> check)
    {
        check(
            TrafficHistoryStore.HistoryPath.EndsWith("history-v8.json", StringComparison.OrdinalIgnoreCase) &&
            TrafficHistoryStore.PreviousHistoryPath.EndsWith("history-v7.json", StringComparison.OrdinalIgnoreCase) &&
            TrafficHistoryStore.PreviousV6HistoryPath.EndsWith("history-v6.json", StringComparison.OrdinalIgnoreCase) &&
            TrafficHistoryStore.PreviousV5HistoryPath.EndsWith("history-v5.json", StringComparison.OrdinalIgnoreCase),
            "Corrected process history must use v8 while retaining all previous paths.");
        check(
            NetworkTrafficHistoryStore.HistoryPath.EndsWith("network-history-v7.json", StringComparison.OrdinalIgnoreCase) &&
            NetworkTrafficHistoryStore.PreviousHistoryPath.EndsWith("network-history-v6.json", StringComparison.OrdinalIgnoreCase) &&
            NetworkTrafficHistoryStore.PreviousV5HistoryPath.EndsWith("network-history-v5.json", StringComparison.OrdinalIgnoreCase) &&
            NetworkTrafficHistoryStore.PreviousV4HistoryPath.EndsWith("network-history-v4.json", StringComparison.OrdinalIgnoreCase),
            "Corrected network history must use v7 while retaining all previous paths.");
    }

    private static void CheckProcessHistoryRecovery(string temporaryDirectory, Action<bool, string> check)
    {
        var corruptPath = Path.Combine(temporaryDirectory, "process-corrupt.json");
        var corruptBytes = Encoding.UTF8.GetBytes(
            """
            [
              {
                "ProcessName": "ValidBeforeFailure",
                "Path": "C:\\ValidBeforeFailure.exe",
                "LastSeen": "2026-09-05T00:00:00",
                "Buckets": {
                  "history-recovery-interface|2026-09-05": { "Ipv4Received": 123 }
                }
              },
              {
                "ProcessName": "InvalidRecord",
                "Path": "C:\\InvalidRecord.exe",
                "Buckets": {
                  "history-recovery-interface|2026-09-05": { "Ipv4Received": "not-a-number" }
                }
              }
            ]
            """);
        File.WriteAllBytes(corruptPath, corruptBytes);

        var failedStore = TrafficHistoryStore.Load(corruptPath);
        check(!string.IsNullOrWhiteSpace(failedStore.LoadError), "A process history read failure must remain visible on the loaded store.");
        check(!failedStore.Records.Any(), "A process history read failure must not retain records parsed before the failure.");

        failedStore.AddDelta(
            "NewTraffic",
            @"C:\NewTraffic.exe",
            new TrafficCounters { Ipv4Received = 50, TcpReceived = 50 },
            DateTime.Now,
            InterfaceId);
        var saveException = CaptureSaveException(failedStore.Save);
        check(
            saveException is InvalidOperationException && saveException.Message.Contains("could not be read", StringComparison.OrdinalIgnoreCase),
            "Saving process history after a read failure must report a clear recovery error.");
        check(
            File.ReadAllBytes(corruptPath).SequenceEqual(corruptBytes),
            "A blocked process history save must preserve every byte of the unreadable file.");

        CheckLockedProcessHistory(temporaryDirectory, check);

        failedStore.Clear();
        check(string.IsNullOrEmpty(failedStore.LoadError), "Explicitly clearing process history must remove the read-failure save protection.");
        failedStore.AddDelta(
            "RecoveredTraffic",
            @"C:\RecoveredTraffic.exe",
            new TrafficCounters { Ipv4Received = 75, TcpReceived = 75 },
            DateTime.Now,
            InterfaceId);
        var recoveredSaveException = CaptureSaveException(failedStore.Save);
        check(recoveredSaveException is null, "Process history must be saveable after an explicit clear.");

        var loadedStore = TrafficHistoryStore.Load(corruptPath);
        var snapshots = loadedStore.BuildSnapshots(TrafficTimeRange.All, [], InterfaceId);
        check(string.IsNullOrEmpty(loadedStore.LoadError), "A valid process history roundtrip must load without an error.");
        check(
            snapshots.Count == 1 && snapshots[0].Ipv4Received == 75 && snapshots[0].TcpReceived == 75,
            "A valid process history roundtrip must preserve its counters.");
    }

    private static void CheckLockedProcessHistory(string temporaryDirectory, Action<bool, string> check)
    {
        var lockedPath = Path.Combine(temporaryDirectory, "process-locked.json");
        var originalBytes = Encoding.UTF8.GetBytes("[]");
        File.WriteAllBytes(lockedPath, originalBytes);

        TrafficHistoryStore lockedStore;
        using (new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            lockedStore = TrafficHistoryStore.Load(lockedPath);
        }

        check(
            !string.IsNullOrWhiteSpace(lockedStore.LoadError),
            "A sharing violation while reading process history must enable save protection.");
        lockedStore.AddDelta(
            "LockedTraffic",
            @"C:\LockedTraffic.exe",
            new TrafficCounters { Ipv4Received = 10 },
            DateTime.Now,
            InterfaceId);
        var saveException = CaptureSaveException(lockedStore.Save);
        check(
            saveException is InvalidOperationException,
            "Process history must remain protected after the sharing lock is released.");
        check(
            File.ReadAllBytes(lockedPath).SequenceEqual(originalBytes),
            "Process history blocked by an earlier sharing violation must retain its original bytes.");
    }

    private static void CheckNetworkHistoryRecovery(string temporaryDirectory, Action<bool, string> check)
    {
        var corruptPath = Path.Combine(temporaryDirectory, "network-corrupt.json");
        var corruptBytes = Encoding.UTF8.GetBytes(
            """
            {
              "Version": 7,
              "Buckets": {
                "history-recovery-interface|2026-09-05": { "Received": 11, "Sent": 22 },
                "history-recovery-interface|2026-09-06": { "Received": "not-a-number", "Sent": 33 }
              }
            }
            """);
        File.WriteAllBytes(corruptPath, corruptBytes);

        var failedStore = NetworkTrafficHistoryStore.Load(corruptPath);
        var current = Snapshot(receivedDelta: 0, sentDelta: 0);
        var failedTotals = failedStore.GetTotals(TrafficTimeRange.All, current);
        check(!string.IsNullOrWhiteSpace(failedStore.LoadError), "A network history read failure must remain visible on the loaded store.");
        check(
            failedTotals.Received == 0 && failedTotals.Sent == 0,
            "A network history read failure must not retain buckets parsed before the failure.");

        failedStore.AddDelta(Snapshot(receivedDelta: 50, sentDelta: 60), DateTime.Now);
        var saveException = CaptureSaveException(failedStore.Save);
        check(
            saveException is InvalidOperationException && saveException.Message.Contains("could not be read", StringComparison.OrdinalIgnoreCase),
            "Saving network history after a read failure must report a clear recovery error.");
        check(
            File.ReadAllBytes(corruptPath).SequenceEqual(corruptBytes),
            "A blocked network history save must preserve every byte of the unreadable file.");

        CheckLockedNetworkHistory(temporaryDirectory, check);

        failedStore.Clear();
        check(string.IsNullOrEmpty(failedStore.LoadError), "Explicitly clearing network history must remove the read-failure save protection.");
        failedStore.AddDelta(Snapshot(receivedDelta: 125, sentDelta: 250), DateTime.Now);
        var recoveredSaveException = CaptureSaveException(failedStore.Save);
        check(recoveredSaveException is null, "Network history must be saveable after an explicit clear.");

        var document = JsonSerializer.Deserialize<NetworkTrafficHistoryDocument>(File.ReadAllText(corruptPath));
        check(document?.Version == 7, "Saved network history documents must declare version 7.");

        var loadedStore = NetworkTrafficHistoryStore.Load(corruptPath);
        var totals = loadedStore.GetTotals(TrafficTimeRange.All, current);
        check(string.IsNullOrEmpty(loadedStore.LoadError), "A valid network history roundtrip must load without an error.");
        check(
            totals.Received == 125 && totals.Sent == 250,
            "A valid network history roundtrip must preserve its counters.");
    }

    private static void CheckLockedNetworkHistory(string temporaryDirectory, Action<bool, string> check)
    {
        var lockedPath = Path.Combine(temporaryDirectory, "network-locked.json");
        var originalBytes = Encoding.UTF8.GetBytes("{\"Version\":7,\"Buckets\":{}}");
        File.WriteAllBytes(lockedPath, originalBytes);

        NetworkTrafficHistoryStore lockedStore;
        using (new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            lockedStore = NetworkTrafficHistoryStore.Load(lockedPath);
        }

        check(
            !string.IsNullOrWhiteSpace(lockedStore.LoadError),
            "A sharing violation while reading network history must enable save protection.");
        lockedStore.AddDelta(Snapshot(receivedDelta: 10, sentDelta: 20), DateTime.Now);
        var saveException = CaptureSaveException(lockedStore.Save);
        check(
            saveException is InvalidOperationException,
            "Network history must remain protected after the sharing lock is released.");
        check(
            File.ReadAllBytes(lockedPath).SequenceEqual(originalBytes),
            "Network history blocked by an earlier sharing violation must retain its original bytes.");
    }

    private static Exception? CaptureSaveException(Action save)
    {
        try
        {
            save();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static NetworkTrafficSnapshot Snapshot(ulong receivedDelta, ulong sentDelta)
    {
        return new NetworkTrafficSnapshot(
            true,
            true,
            InterfaceId,
            "History recovery adapter",
            1,
            0,
            0,
            0,
            0,
            receivedDelta,
            sentDelta);
    }
}
