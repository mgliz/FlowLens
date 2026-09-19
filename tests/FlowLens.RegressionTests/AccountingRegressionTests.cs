using System.Reflection;
using System.Runtime.CompilerServices;
using FlowLens;

internal static class AccountingRegressionTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string InterfaceId = "accounting-regression-interface";

    public static void Run(Action<bool, string> check)
    {
        // Exercise the real source callback with UI dispatch deliberately held pending.
        // No Window constructor, capture Start, or disk persistence is invoked.
        var fixture = CreateFixture();
        var now = DateTime.Now;
        var first = Event(Process(100, now), 100);
        var second = Event(Process(300, now), 200);
        Receive(fixture.Window, first);
        Receive(fixture.Window, second);
        check(ReferenceEquals(Get(fixture.Window, "_pendingUiSnapshot"), second),
            "Rendering must still coalesce pending snapshots.");
        check(ProcessBytes(fixture.ProcessHistory) == 300 && NetworkBytes(fixture.NetworkHistory) == 300,
            "Every source interval must reach both histories even when UI rendering drops an earlier snapshot.");

        fixture = CreateFixture();
        const ulong staleBytes = 100 * 1024 * 1024;
        var stale = Process(staleBytes, now.AddMinutes(-11)) with
        {
            Pid = 0,
            ProcessInstanceId = long.MinValue,
            ProcessName = TrafficHistoryStore.UnattributedProcessName,
            Path = TrafficHistoryStore.UnattributedPath
        };
        var liveRows = (Dictionary<string, TrafficSnapshot>)Get(fixture.Window, "_liveSnapshots")!;
        for (var iteration = 0; iteration < 3; iteration++)
        {
            Receive(fixture.Window, Event(stale, 1));
            liveRows[MainWindow.RawKeyFor(stale)] = stale;
            Call(fixture.Window, "PruneLiveSnapshots", now);
        }
        check(liveRows.Count == 0 && ProcessBytes(fixture.ProcessHistory) == staleBytes,
            "Expiring an idle PID 0 UI row must not replay its cumulative bytes on later source snapshots.");
        Receive(fixture.Window, Event(stale with { Ipv4Received = staleBytes + 10, TcpReceived = staleBytes + 10, LastSeen = now }, 10));
        check(ProcessBytes(fixture.ProcessHistory) == staleBytes + 10,
            "An idle retained source counter must add only new bytes when its traffic resumes.");

        fixture = CreateFixture();
        Receive(fixture.Window, Event(Process(100, now), 100, complete: false));
        Receive(fixture.Window, Event(Process(160, now), 60));
        check(ProcessBytes(fixture.ProcessHistory) == 60 && NetworkBytes(fixture.NetworkHistory) == 60,
            "A capture gap must establish both baselines without backfilling an incomplete interval.");
        Receive(fixture.Window, Event(Process(170, now), 10, active: false, final: true));
        check(ProcessBytes(fixture.ProcessHistory) == 70 && NetworkBytes(fixture.NetworkHistory) == 70,
            "A complete final interval must be saved before shutdown even though capture has stopped.");
        Receive(fixture.Window, Event(Process(180, now), 10, active: false, final: true, complete: false));
        check(ProcessBytes(fixture.ProcessHistory) == 70 && NetworkBytes(fixture.NetworkHistory) == 70,
            "An incomplete final interval must not be represented as complete history.");

        fixture = CreateFixture();
        Set(fixture.Window, "_persistenceEnabled", false);
        Receive(fixture.Window, Event(Process(100, now), 100));
        Set(fixture.Window, "_persistenceEnabled", true);
        Receive(fixture.Window, Event(Process(150, now), 50));
        check(ProcessBytes(fixture.ProcessHistory) == 50 && NetworkBytes(fixture.NetworkHistory) == 50,
            "Turning persistence back on must not include disabled intervals, including with pending UI work.");

        fixture = CreateFixture();
        var accumulator = (TrafficHistoryAccumulator)Get(fixture.Window, "_historyAccumulator")!;
        accumulator.Observe(Event(Process(10, now), 10), true);
        accumulator.Reset(1);
        accumulator.Observe(Event(Process(900, now), 890), true);
        accumulator.Observe(Event(Process(20, now), 20, generation: 1), true);
        check(ProcessBytes(fixture.ProcessHistory) == 30 && NetworkBytes(fixture.NetworkHistory) == 30,
            "A reset must reject delayed old-generation snapshots and give new counters a fresh baseline.");

        var tracker = new TrafficPersistenceTracker();
        var key = MainWindow.RawKeyFor(Process(10, now));
        tracker.Observe(key, Process(10, now), true);
        tracker.RetainInstances(new HashSet<string>());
        check(tracker.Observe(key, Process(7, now), true)?.Ipv4Received == 7,
            "Baselines for counters actually absent from the source must remain reclaimable.");
    }

    private static (MainWindow Window, TrafficHistoryStore ProcessHistory, NetworkTrafficHistoryStore NetworkHistory) CreateFixture()
    {
        var window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        var processHistory = new TrafficHistoryStore();
        var networkHistory = new NetworkTrafficHistoryStore();
        Set(window, "_monitor", new EtwTrafficMonitor());
        Set(window, "_snapshotDispatchGate", new object());
        Set(window, "_snapshotDispatchScheduled", true);
        Set(window, "_persistenceEnabled", true);
        Set(window, "_nextStatsSaveTimestamp", long.MaxValue);
        Set(window, "_historyAccumulator", new TrafficHistoryAccumulator(processHistory, networkHistory));
        Set(window, "_liveSnapshots", new Dictionary<string, TrafficSnapshot>());
        return (window, processHistory, networkHistory);
    }

    private static TrafficSnapshot Process(ulong received, DateTime lastSeen) => new(
        42, "AccountingFixture", @"C:\fixtures\AccountingFixture.exe",
        received, 0, 0, 0, 0, 0, 0, 0, 0, 0, lastSeen,
        TcpReceived: received, ProcessInstanceId: 42);

    private static NetworkTrafficSnapshot Network(ulong delta) => new(
        true, true, InterfaceId, "Fixture", 1, delta, 0, 0, 0, delta, 0);

    private static MonitorSnapshotEventArgs Event(TrafficSnapshot process, ulong delta,
        bool complete = true, bool active = true, bool final = false, long generation = 0) => new(
        [process], 0, "", network: Network(delta), generation: generation, captureActive: active,
        isCaptureIntervalComplete: complete, capturedAt: DateTime.Now, isFinalSnapshot: final);

    private static ulong ProcessBytes(TrafficHistoryStore history) => history.Records
        .SelectMany(record => record.Buckets.Values).Aggregate(0UL, (sum, bucket) => sum + bucket.Ipv4Received);
    private static ulong NetworkBytes(NetworkTrafficHistoryStore history) => history.GetTotals(TrafficTimeRange.All, Network(0)).Received;
    private static void Receive(MainWindow window, MonitorSnapshotEventArgs snapshot) => Call(window, "Monitor_SnapshotReady", null, snapshot);
    private static object? Get(object instance, string name) => instance.GetType().GetField(name, PrivateInstance)!.GetValue(instance);
    private static void Set(object instance, string name, object value) => instance.GetType().GetField(name, PrivateInstance)!.SetValue(instance, value);
    private static void Call(object instance, string name, params object?[] arguments) => instance.GetType().GetMethod(name, PrivateInstance)!.Invoke(instance, arguments);
}
