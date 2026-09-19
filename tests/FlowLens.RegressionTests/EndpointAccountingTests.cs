using System.Collections;
using System.Net;
using System.Reflection;
using FlowLens;

internal static class EndpointAccountingTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(Action<bool, string> check)
    {
        foreach (var version in new[] { IpVersion.Ipv4, IpVersion.Ipv6 })
        {
            foreach (var isTcp in new[] { true, false })
            {
                CheckProtocol(version, isTcp, check);
            }
        }

        var mapped = EtwTrafficMonitor.NormalizeEventEndpoints(true, true,
            IPAddress.Parse("::ffff:192.0.2.10"), IPAddress.Parse("::ffff:198.51.100.20"), 12345, 443);
        check(mapped.SourceAddress.Equals(IPAddress.Parse("198.51.100.20"))
            && mapped.DestinationAddress.Equals(IPAddress.Parse("192.0.2.10"))
            && mapped.SourcePort == 443 && mapped.DestinationPort == 12345,
            "IPv4-mapped TCP receives must normalize addresses and both endpoint ports.");

        var scoped = EtwTrafficMonitor.NormalizeEventEndpoints(false, true,
            IPAddress.Parse("fe80::20%7"), IPAddress.Parse("fe80::10%7"), 53, 12345);
        check(scoped.SourceAddress.Equals(IPAddress.Parse("fe80::20"))
            && scoped.DestinationAddress.Equals(IPAddress.Parse("fe80::10"))
            && scoped.SourcePort == 53 && scoped.DestinationPort == 12345,
            "Scoped IPv6 UDP receive endpoints must keep packet direction while normalizing scope IDs.");

        using var mappedMonitor = CreateMonitor(IPAddress.Parse("192.0.2.10"), IPAddress.Parse("192.0.2.11"));
        AddRawEvent(mappedMonitor, IpVersion.Ipv4, true, false,
            IPAddress.Parse("192.0.2.10"), IPAddress.Parse("198.51.100.20"), 100);
        AddRawEvent(mappedMonitor, IpVersion.Ipv4, true, true,
            IPAddress.Parse("::ffff:192.0.2.10"), IPAddress.Parse("::ffff:198.51.100.20"), 200);
        check(CounterValue(mappedMonitor, "Ipv4Received") == 200 && CounterValue(mappedMonitor, "Ipv4Sent") == 100
            && FlowCount(mappedMonitor) == 1,
            "Mapped and unmapped endpoints of one TCP connection must share one flow and preserve its bytes.");
    }

    private static void CheckProtocol(IpVersion version, bool isTcp, Action<bool, string> check)
    {
        var local = IPAddress.Parse(version == IpVersion.Ipv4 ? "192.0.2.10" : "2001:db8:1::10");
        var otherLocal = IPAddress.Parse(version == IpVersion.Ipv4 ? "192.0.2.11" : "2001:db8:2::10");
        var remote = IPAddress.Parse(version == IpVersion.Ipv4 ? "198.51.100.20" : "2001:db8:3::20");
        var unknown = IPAddress.Parse(version == IpVersion.Ipv4 ? "198.18.0.1" : "2001:db8:4::1");
        var loopback = version == IpVersion.Ipv4 ? IPAddress.Loopback : IPAddress.IPv6Loopback;
        var context = $"{(isTcp ? "TCP" : "UDP")} {version}";

        foreach (var isReceive in new[] { false, true })
        {
            var raw = RawEndpoints(isTcp, isReceive, local, remote);
            var normalized = EtwTrafficMonitor.NormalizeEventEndpoints(isTcp, isReceive,
                raw.Source, raw.Destination, raw.SourcePort, raw.DestinationPort);
            check(normalized.SourceAddress.Equals(isReceive ? remote : local)
                && normalized.DestinationAddress.Equals(isReceive ? local : remote)
                && normalized.SourcePort == (isReceive ? 443 : 12345)
                && normalized.DestinationPort == (isReceive ? 12345 : 443),
                $"{context} {(isReceive ? "receive" : "send")} must normalize to packet direction with correct ports.");
        }

        // Drive the same ingestion method used by every real ETW subscription.
        // No capture session, background task or persistence is started.
        using var monitor = CreateMonitor(local, otherLocal);
        AddRawEvent(monitor, version, isTcp, false, local, remote, 100);
        AddRawEvent(monitor, version, isTcp, true, local, remote, 200);
        var receivedField = version == IpVersion.Ipv4 ? "Ipv4Received" : "Ipv6Received";
        var sentField = version == IpVersion.Ipv4 ? "Ipv4Sent" : "Ipv6Sent";
        check(CounterValue(monitor, receivedField) == 200 && CounterValue(monitor, sentField) == 100,
            $"{context} must retain selected-adapter send and receive bytes, including PID 0.");
        check(FlowCount(monitor) == 1,
            $"{context} send and receive events of one connection must produce one flow key.");

        foreach (var isReceive in new[] { false, true })
        {
            AddRawEvent(monitor, version, isTcp, isReceive, otherLocal, remote, 1_000);
            AddRawEvent(monitor, version, isTcp, isReceive, unknown, remote, 2_000);
            AddRawEvent(monitor, version, isTcp, isReceive, local, loopback, 3_000);
            AddRawEvent(monitor, version, isTcp, isReceive, local, otherLocal, 4_000);
        }
        check(CounterValue(monitor, receivedField) == 200 && CounterValue(monitor, sentField) == 100
            && FlowCount(monitor) == 1,
            $"{context} must reject other-interface, unknown, loopback and local-to-local events before accounting.");
    }

    private static EtwTrafficMonitor CreateMonitor(IPAddress local, IPAddress otherLocal)
    {
        var monitor = new EtwTrafficMonitor();
        typeof(EtwTrafficMonitor).GetField("_selectedAdapterAddresses", PrivateInstance)!
            .SetValue(monitor, new HashSet<IPAddress> { local });
        typeof(EtwTrafficMonitor).GetField("_localAddresses", PrivateInstance)!
            .SetValue(monitor, new HashSet<IPAddress> { local, otherLocal, IPAddress.Loopback, IPAddress.IPv6Loopback });
        return monitor;
    }

    private static void AddRawEvent(EtwTrafficMonitor monitor, IpVersion version, bool isTcp, bool isReceive,
        IPAddress local, IPAddress remote, int bytes)
    {
        var raw = RawEndpoints(isTcp, isReceive, local, remote);
        var method = typeof(EtwTrafficMonitor).GetMethod("AddTraffic", PrivateInstance)!;
        var parameters = method.GetParameters();
        method.Invoke(monitor,
        [
            0, version, Enum.Parse(parameters[2].ParameterType, isTcp ? "Tcp" : "Udp"),
            Enum.Parse(parameters[3].ParameterType, isReceive ? "Receive" : "Send"),
            raw.Source, raw.Destination, raw.SourcePort, raw.DestinationPort, bytes, DateTime.Now
        ]);
    }

    private static (IPAddress Source, IPAddress Destination, int SourcePort, int DestinationPort) RawEndpoints(
        bool isTcp, bool isReceive, IPAddress local, IPAddress remote)
    {
        // TCP ETW records keep local/remote order; UDP records follow packet direction.
        return !isTcp && isReceive ? (remote, local, 443, 12345) : (local, remote, 12345, 443);
    }

    private static ulong CounterValue(EtwTrafficMonitor monitor, string fieldName)
    {
        var counters = (IDictionary)typeof(EtwTrafficMonitor).GetField("_counters", PrivateInstance)!.GetValue(monitor)!;
        var counter = counters.Values.Cast<object>().Single();
        return (ulong)counter.GetType().GetField(fieldName)!.GetValue(counter)!;
    }

    private static int FlowCount(EtwTrafficMonitor monitor)
    {
        var recentFlows = (IDictionary)typeof(EtwTrafficMonitor).GetField("_recentFlows", PrivateInstance)!.GetValue(monitor)!;
        return recentFlows.Values.Cast<IDictionary>().Sum(flows => flows.Count);
    }
}
