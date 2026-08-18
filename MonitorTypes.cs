namespace FlowLens;

public sealed class MonitorSnapshotEventArgs(
    IReadOnlyList<TrafficSnapshot> snapshots,
    int errorCount,
    string errorText,
    int lostEventCount = 0,
    NetworkTrafficSnapshot? network = null,
    long generation = 0,
    bool captureActive = false) : EventArgs
{
    public IReadOnlyList<TrafficSnapshot> Snapshots { get; } = snapshots;
    public int ErrorCount { get; } = errorCount;
    public string ErrorText { get; } = errorText;
    public int LostEventCount { get; } = lostEventCount;
    public NetworkTrafficSnapshot Network { get; } = network ?? NetworkTrafficSnapshot.Unavailable;
    public long Generation { get; } = generation;
    public bool CaptureActive { get; } = captureActive;
}

public enum IpVersion
{
    Ipv4,
    Ipv6
}
