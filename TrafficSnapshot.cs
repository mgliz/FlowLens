namespace FlowLens;

public sealed record TrafficSnapshot(
    int Pid,
    string ProcessName,
    string Path,
    ulong Ipv4Received,
    ulong Ipv4Sent,
    ulong Ipv6Received,
    ulong Ipv6Sent,
    ulong Ipv4ReceiveRate,
    ulong Ipv4SendRate,
    ulong Ipv6ReceiveRate,
    ulong Ipv6SendRate,
    int Ipv4Connections,
    int Ipv6Connections,
    DateTime LastSeen,
    ulong TcpReceived = 0,
    ulong TcpSent = 0,
    ulong UdpReceived = 0,
    ulong UdpSent = 0);
