namespace FlowLens;

// Consumes every source snapshot before the UI is allowed to coalesce updates.
internal sealed class TrafficHistoryAccumulator(
    TrafficHistoryStore processHistory,
    NetworkTrafficHistoryStore networkHistory)
{
    private readonly object _gate = new();
    private readonly TrafficPersistenceTracker _tracker = new();
    private long _generation = -1;

    public void Observe(MonitorSnapshotEventArgs snapshot, bool persistenceEnabled)
    {
        lock (_gate)
        {
            if (snapshot.Generation < _generation)
            {
                return;
            }

            if (snapshot.Generation != _generation)
            {
                _tracker.Clear();
                _generation = snapshot.Generation;
            }

            var persist = snapshot.IsCaptureIntervalComplete && MainWindow.ShouldPersistSnapshot(
                persistenceEnabled,
                snapshot.CaptureActive || snapshot.IsFinalSnapshot,
                snapshot.Network);
            if (persist)
            {
                networkHistory.AddDelta(snapshot.Network, snapshot.CapturedAt);
            }

            var sourceInstances = new HashSet<string>(StringComparer.Ordinal);
            foreach (var process in snapshot.Snapshots)
            {
                var key = MainWindow.RawKeyFor(process);
                sourceInstances.Add(key);
                var delta = _tracker.Observe(key, process, persist);
                if (delta is not null)
                {
                    processHistory.AddDelta(
                        process.ProcessName, process.Path, delta, snapshot.CapturedAt,
                        snapshot.Network.InterfaceId);
                }
            }

            // UI row expiry is independent of source counter lifetime (notably PID 0).
            _tracker.RetainInstances(sourceInstances);
        }
    }

    public void Reset(long generation)
    {
        lock (_gate)
        {
            _tracker.Clear();
            _generation = generation;
        }
    }
}
