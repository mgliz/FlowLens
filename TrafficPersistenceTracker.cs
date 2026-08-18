namespace FlowLens;

internal sealed class TrafficPersistenceTracker
{
    private readonly Dictionary<string, TrafficSnapshot> _baselines = [];

    public TrafficCounters? Observe(string instanceKey, TrafficSnapshot snapshot, bool persistenceEnabled)
    {
        if (!persistenceEnabled)
        {
            _baselines[instanceKey] = snapshot;
            return null;
        }

        if (!TrafficHistoryStore.IsPersistable(snapshot.ProcessName, snapshot.Path))
        {
            return null;
        }

        _baselines.TryGetValue(instanceKey, out var previous);
        var delta = TrafficCounters.Delta(snapshot, previous);
        _baselines[instanceKey] = snapshot;
        return delta;
    }

    public void Remove(string instanceKey)
    {
        _baselines.Remove(instanceKey);
    }

    public void Clear()
    {
        _baselines.Clear();
    }
}
