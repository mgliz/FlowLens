using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace FlowLens;

public sealed class EtwTrafficMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<int, EtwAggregateCounter> _counters = [];
    private readonly Dictionary<int, HashSet<string>> _recentIpv4Flows = [];
    private readonly Dictionary<int, HashSet<string>> _recentIpv6Flows = [];
    private readonly HashSet<IPAddress> _localAddresses = [];
    private readonly ConcurrentDictionary<int, EtwProcessInfo> _processCache = [];
    private readonly string _sessionName = $"FlowLens-KernelNetwork-{Environment.ProcessId}";

    private CancellationTokenSource? _cancellation;
    private TraceEventSession? _session;
    private Task? _eventTask;
    private Task? _snapshotTask;
    private DateTime _lastSnapshotTime = DateTime.Now;
    private DateTime _nextLocalAddressRefresh = DateTime.MinValue;

    public event EventHandler<MonitorSnapshotEventArgs>? SnapshotReady;

    public int LastErrorCount { get; private set; }
    public string LastErrorText { get; private set; } = string.Empty;
    public int SnapshotIntervalSeconds { get; set; } = 1;
    public bool ExcludeLocalTraffic { get; set; } = true;

    public void Start()
    {
        if (_eventTask is { IsCompleted: false })
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        _eventTask = Task.Run(() => RunEtwSession(_cancellation.Token));
        _snapshotTask = Task.Run(() => PublishSnapshotsAsync(_cancellation.Token));
    }

    public void Stop()
    {
        _cancellation?.Cancel();
        _session?.Stop();
    }

    public void Reset()
    {
        lock (_gate)
        {
            _counters.Clear();
            _recentIpv4Flows.Clear();
            _recentIpv6Flows.Clear();
        }
    }

    public void Dispose()
    {
        Stop();
        _session?.Dispose();
        _cancellation?.Dispose();
    }

    private void RunEtwSession(CancellationToken token)
    {
        try
        {
            using var session = new TraceEventSession(_sessionName);
            _session = session;
            session.StopOnDispose = true;
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

            session.Source.Kernel.TcpIpSend += data =>
                AddTraffic(data.ProcessID, IpVersion.Ipv4, TransportProtocol.Tcp, TrafficDirection.Send, data.saddr, data.daddr, Math.Max(0, data.size), $"tcp4:{data.saddr}:{data.sport}>{data.daddr}:{data.dport}");
            session.Source.Kernel.TcpIpRecv += data =>
                AddTraffic(data.ProcessID, IpVersion.Ipv4, TransportProtocol.Tcp, TrafficDirection.Receive, data.saddr, data.daddr, Math.Max(0, data.size), $"tcp4:{data.saddr}:{data.sport}>{data.daddr}:{data.dport}");
            session.Source.Kernel.TcpIpSendIPV6 += data =>
                AddTraffic(data.ProcessID, IpVersion.Ipv6, TransportProtocol.Tcp, TrafficDirection.Send, data.saddr, data.daddr, Math.Max(0, data.size), $"tcp6:{data.saddr}:{data.sport}>{data.daddr}:{data.dport}");
            session.Source.Kernel.TcpIpRecvIPV6 += data =>
                AddTraffic(data.ProcessID, IpVersion.Ipv6, TransportProtocol.Tcp, TrafficDirection.Receive, data.saddr, data.daddr, Math.Max(0, data.size), $"tcp6:{data.saddr}:{data.sport}>{data.daddr}:{data.dport}");

            session.Source.Kernel.UdpIpSend += data =>
                AddTraffic(data.ProcessID, IpVersion.Ipv4, TransportProtocol.Udp, TrafficDirection.Send, data.saddr, data.daddr, Math.Max(0, data.size), $"udp4:{data.saddr}:{data.sport}>{data.daddr}:{data.dport}");
            session.Source.Kernel.UdpIpRecv += data =>
                AddTraffic(data.ProcessID, IpVersion.Ipv4, TransportProtocol.Udp, TrafficDirection.Receive, data.saddr, data.daddr, Math.Max(0, data.size), $"udp4:{data.saddr}:{data.sport}>{data.daddr}:{data.dport}");
            session.Source.Kernel.UdpIpSendIPV6 += data =>
                AddTraffic(data.ProcessID, IpVersion.Ipv6, TransportProtocol.Udp, TrafficDirection.Send, data.saddr, data.daddr, Math.Max(0, data.size), $"udp6:{data.saddr}:{data.sport}>{data.daddr}:{data.dport}");
            session.Source.Kernel.UdpIpRecvIPV6 += data =>
                AddTraffic(data.ProcessID, IpVersion.Ipv6, TransportProtocol.Udp, TrafficDirection.Receive, data.saddr, data.daddr, Math.Max(0, data.size), $"udp6:{data.saddr}:{data.sport}>{data.daddr}:{data.dport}");

            session.Source.Process();
        }
        catch (Exception ex)
        {
            LastErrorCount++;
            LastErrorText = ex.Message;
        }
    }

    private async Task PublishSnapshotsAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(SnapshotIntervalSeconds, 1, 10)), token).ConfigureAwait(false);
                SnapshotReady?.Invoke(this, new MonitorSnapshotEventArgs(BuildSnapshot(), LastErrorCount, LastErrorText));
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LastErrorCount++;
                LastErrorText = ex.Message;
            }
        }
    }

    private IReadOnlyList<TrafficSnapshot> BuildSnapshot()
    {
        var now = DateTime.Now;

        lock (_gate)
        {
            var elapsedSeconds = Math.Max(0.001, (now - _lastSnapshotTime).TotalSeconds);
            _lastSnapshotTime = now;

            return _counters
                .Where(pair => now - pair.Value.LastSeen < TimeSpan.FromMinutes(10))
                .Select(pair =>
                {
                    var pid = pair.Key;
                    var counter = pair.Value;
                    var process = GetProcessInfo(pid);

                    _recentIpv4Flows.TryGetValue(pid, out var ipv4Flows);
                    _recentIpv6Flows.TryGetValue(pid, out var ipv6Flows);

                    var snapshot = new TrafficSnapshot(
                        pid,
                        process.Name,
                        process.Path,
                        counter.Ipv4Received,
                        counter.Ipv4Sent,
                        counter.Ipv6Received,
                        counter.Ipv6Sent,
                        ToPerSecond(counter.CurrentIpv4Received, elapsedSeconds),
                        ToPerSecond(counter.CurrentIpv4Sent, elapsedSeconds),
                        ToPerSecond(counter.CurrentIpv6Received, elapsedSeconds),
                        ToPerSecond(counter.CurrentIpv6Sent, elapsedSeconds),
                        ipv4Flows?.Count ?? 0,
                        ipv6Flows?.Count ?? 0,
                        counter.LastSeen,
                        counter.TcpReceived,
                        counter.TcpSent,
                        counter.UdpReceived,
                        counter.UdpSent);

                    counter.CurrentIpv4Received = 0;
                    counter.CurrentIpv4Sent = 0;
                    counter.CurrentIpv6Received = 0;
                    counter.CurrentIpv6Sent = 0;

                    return snapshot;
                })
                .OrderByDescending(item => item.Ipv4ReceiveRate + item.Ipv4SendRate + item.Ipv6ReceiveRate + item.Ipv6SendRate)
                .ThenBy(item => item.ProcessName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    private static ulong ToPerSecond(ulong bytes, double elapsedSeconds)
    {
        return (ulong)Math.Round(bytes / elapsedSeconds, MidpointRounding.AwayFromZero);
    }

    private void AddTraffic(int pid, IpVersion ipVersion, TransportProtocol protocol, TrafficDirection direction, IPAddress sourceAddress, IPAddress destinationAddress, int bytes, string flowKey)
    {
        if (pid <= 0 || bytes <= 0)
        {
            return;
        }

        lock (_gate)
        {
            if (ShouldExcludeTraffic(sourceAddress, destinationAddress))
            {
                return;
            }

            if (!_counters.TryGetValue(pid, out var counter))
            {
                counter = new EtwAggregateCounter();
                _counters[pid] = counter;
            }

            counter.LastSeen = DateTime.Now;

            if (ipVersion == IpVersion.Ipv4)
            {
                AddFlow(_recentIpv4Flows, pid, flowKey);

                if (direction == TrafficDirection.Receive)
                {
                    counter.Ipv4Received += (ulong)bytes;
                    counter.CurrentIpv4Received += (ulong)bytes;
                }
                else
                {
                    counter.Ipv4Sent += (ulong)bytes;
                    counter.CurrentIpv4Sent += (ulong)bytes;
                }
            }
            else
            {
                AddFlow(_recentIpv6Flows, pid, flowKey);

                if (direction == TrafficDirection.Receive)
                {
                    counter.Ipv6Received += (ulong)bytes;
                    counter.CurrentIpv6Received += (ulong)bytes;
                }
                else
                {
                    counter.Ipv6Sent += (ulong)bytes;
                    counter.CurrentIpv6Sent += (ulong)bytes;
                }
            }

            if (protocol == TransportProtocol.Tcp)
            {
                if (direction == TrafficDirection.Receive)
                {
                    counter.TcpReceived += (ulong)bytes;
                }
                else
                {
                    counter.TcpSent += (ulong)bytes;
                }
            }
            else
            {
                if (direction == TrafficDirection.Receive)
                {
                    counter.UdpReceived += (ulong)bytes;
                }
                else
                {
                    counter.UdpSent += (ulong)bytes;
                }
            }
        }
    }

    private bool ShouldExcludeTraffic(IPAddress sourceAddress, IPAddress destinationAddress)
    {
        if (!ExcludeLocalTraffic)
        {
            return false;
        }

        if (IPAddress.IsLoopback(sourceAddress) || IPAddress.IsLoopback(destinationAddress))
        {
            return true;
        }

        RefreshLocalAddressesIfNeeded();
        return _localAddresses.Contains(sourceAddress) && _localAddresses.Contains(destinationAddress);
    }

    private void RefreshLocalAddressesIfNeeded()
    {
        var now = DateTime.Now;
        if (now < _nextLocalAddressRefresh)
        {
            return;
        }

        _localAddresses.Clear();
        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                foreach (var address in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    _localAddresses.Add(address.Address);
                }
            }
        }
        catch
        {
        }

        _nextLocalAddressRefresh = now.AddSeconds(10);
    }

    private static void AddFlow(Dictionary<int, HashSet<string>> flows, int pid, string flowKey)
    {
        if (!flows.TryGetValue(pid, out var set))
        {
            set = [];
            flows[pid] = set;
        }

        set.Add(flowKey);

        if (set.Count > 256)
        {
            set.Clear();
            set.Add(flowKey);
        }
    }

    private EtwProcessInfo GetProcessInfo(int pid)
    {
        return _processCache.GetOrAdd(pid, static processId =>
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                var name = process.ProcessName;
                var path = string.Empty;

                try
                {
                    path = process.MainModule?.FileName ?? string.Empty;
                }
                catch
                {
                    path = string.Empty;
                }

                return new EtwProcessInfo(name, path);
            }
            catch
            {
                return new EtwProcessInfo($"PID {processId}", string.Empty);
            }
        });
    }

    private sealed class EtwAggregateCounter
    {
        public ulong Ipv4Received;
        public ulong Ipv4Sent;
        public ulong Ipv6Received;
        public ulong Ipv6Sent;
        public ulong CurrentIpv4Received;
        public ulong CurrentIpv4Sent;
        public ulong CurrentIpv6Received;
        public ulong CurrentIpv6Sent;
        public ulong TcpReceived;
        public ulong TcpSent;
        public ulong UdpReceived;
        public ulong UdpSent;
        public DateTime LastSeen = DateTime.Now;
    }

    private sealed record EtwProcessInfo(string Name, string Path);

    private enum TrafficDirection
    {
        Receive,
        Send
    }

    private enum TransportProtocol
    {
        Tcp,
        Udp
    }
}
