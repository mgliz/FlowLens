namespace FlowLens;

public sealed record NetworkTrafficSnapshot(
    bool IsAvailable,
    bool IsAttributionAvailable,
    string InterfaceId,
    string InterfaceName,
    long AdapterEpoch,
    ulong Received,
    ulong Sent,
    ulong ReceiveRate,
    ulong SendRate,
    ulong ReceivedDelta,
    ulong SentDelta)
{
    public static NetworkTrafficSnapshot Unavailable { get; } = new(
        false,
        false,
        string.Empty,
        string.Empty,
        0,
        0,
        0,
        0,
        0,
        0,
        0);
}
