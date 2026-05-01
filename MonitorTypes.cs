namespace FlowLens;

public sealed class MonitorSnapshotEventArgs(
    IReadOnlyList<TrafficSnapshot> snapshots,
    int errorCount,
    string errorText) : EventArgs
{
    public IReadOnlyList<TrafficSnapshot> Snapshots { get; } = snapshots;
    public int ErrorCount { get; } = errorCount;
    public string ErrorText { get; } = errorText;
}

public enum IpVersion
{
    Ipv4,
    Ipv6
}
