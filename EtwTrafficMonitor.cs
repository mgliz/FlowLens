using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace FlowLens;

public sealed class EtwTrafficMonitor : IDisposable
{
    private static readonly TimeSpan StaleCounterAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RecentFlowAge = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ProcessValidationAge = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CaptureRetryDelay = TimeSpan.FromSeconds(3);
    private const int MaximumFlowsPerProcess = 4096;
    private const int MaximumProcessValidationsPerSnapshot = 32;

    private readonly object _gate = new();
    private readonly object _lifecycleGate = new();
    private readonly object _snapshotGate = new();
    private readonly Dictionary<ProcessInstanceKey, EtwAggregateCounter> _counters = [];
    private readonly Dictionary<ProcessInstanceKey, Dictionary<FlowKey, long>> _recentFlows = [];
    private readonly Dictionary<int, ActiveProcess> _activeProcesses = [];
    private readonly NetworkAdapterSampler _networkSampler = new();
    private readonly string _sessionName = $"FlowLens-KernelNetwork-{Environment.ProcessId}";

    private HashSet<IPAddress> _localAddresses = [];
    private HashSet<IPAddress> _selectedAdapterAddresses = [];
    private CancellationTokenSource? _cancellation;
    private TraceEventSession? _session;
    private Task? _eventTask;
    private Task? _snapshotTask;
    private long _lastSnapshotTimestamp = Stopwatch.GetTimestamp();
    private long _lastAdapterEpoch;
    private long _nextLocalAddressRefreshTimestamp;
    private long _nextGeneratedInstanceId;
    private long _generation;
    private int _lastErrorCount;
    private string _lastErrorText = string.Empty;
    private int _snapshotIntervalSeconds = 1;
    private bool _excludeLocalTraffic = true;
    private bool _trackFlows = true;
    private int _captureActive;
    private int _started;

    public event EventHandler<MonitorSnapshotEventArgs>? SnapshotReady;

    public int LastErrorCount => Volatile.Read(ref _lastErrorCount);
    public string LastErrorText => Volatile.Read(ref _lastErrorText);
    public int SnapshotIntervalSeconds
    {
        get => Volatile.Read(ref _snapshotIntervalSeconds);
        set => Volatile.Write(ref _snapshotIntervalSeconds, Math.Clamp(value, 1, 10));
    }
    public bool ExcludeLocalTraffic
    {
        get => Volatile.Read(ref _excludeLocalTraffic);
        set => Volatile.Write(ref _excludeLocalTraffic, value);
    }
    public bool TrackFlows
    {
        get => Volatile.Read(ref _trackFlows);
        set => Volatile.Write(ref _trackFlows, value);
    }
    public long Generation => Interlocked.Read(ref _generation);
    public string NetworkInterfaceId
    {
        get => _networkSampler.PreferredInterfaceId;
        set
        {
            var interfaceId = value ?? string.Empty;
            if (_networkSampler.PreferredInterfaceId.Equals(interfaceId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _networkSampler.PreferredInterfaceId = interfaceId;
            if (Volatile.Read(ref _started) != 0)
            {
                Reset();
            }
        }
    }

    public void Start()
    {
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _started) != 0
                || _eventTask is { IsCompleted: false }
                || _snapshotTask is { IsCompleted: false })
            {
                return;
            }

            RefreshLocalAddresses();
            var network = _networkSampler.Sample();
            _lastAdapterEpoch = network.AdapterEpoch;
            UpdateSelectedAdapterAddresses();
            _lastSnapshotTimestamp = Stopwatch.GetTimestamp();

            var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            Volatile.Write(ref _started, 1);
            _eventTask = Task.Run(() => RunEtwLoopAsync(cancellation.Token));
            _snapshotTask = Task.Run(() => PublishSnapshotsAsync(cancellation.Token));
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cancellation;
        Task[] tasks;
        lock (_lifecycleGate)
        {
            Volatile.Write(ref _started, 0);
            cancellation = _cancellation;
            tasks = new[] { _eventTask, _snapshotTask }
                .Where(task => task is not null)
                .Cast<Task>()
                .ToArray();
        }

        cancellation?.Cancel();
        try
        {
            _session?.Stop();
        }
        catch
        {
        }

        try
        {
            Task.WaitAll(tasks, TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        lock (_lifecycleGate)
        {
            if (ReferenceEquals(_cancellation, cancellation) && tasks.All(task => task.IsCompleted))
            {
                _cancellation?.Dispose();
                _cancellation = null;
                _eventTask = null;
                _snapshotTask = null;
            }
        }
    }

    public void Reset()
    {
        lock (_snapshotGate)
        {
            lock (_gate)
            {
                _generation++;
                _counters.Clear();
                _recentFlows.Clear();
                _activeProcesses.Clear();
            }

            _networkSampler.Reset();
            var network = _networkSampler.Sample();
            _lastAdapterEpoch = network.AdapterEpoch;
            UpdateSelectedAdapterAddresses();
            _lastSnapshotTimestamp = Stopwatch.GetTimestamp();
        }
    }

    public void Dispose()
    {
        Stop();
        _session?.Dispose();
    }

    private async Task RunEtwLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            RunEtwSession(token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await Task.Delay(CaptureRetryDelay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void RunEtwSession(CancellationToken token)
    {
        Volatile.Write(ref _captureActive, 0);
        try
        {
            token.ThrowIfCancellationRequested();
            using var session = new TraceEventSession(_sessionName)
            {
                StopOnDispose = true,
                BufferSizeMB = 64,
                BufferQuantumKB = 64
            };
            _session = session;
            using var cancellationRegistration = token.Register(() =>
            {
                try
                {
                    session.Stop();
                }
                catch
                {
                }
            });

            // TraceEvent requires the kernel provider before Source.Kernel is accessed.
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP | KernelTraceEventParser.Keywords.Process);
            var parser = session.Source.Kernel;
            parser.ProcessStart += data => RegisterProcessStart(data.ProcessID, data.ImageFileName);
            parser.ProcessStop += data => RegisterProcessStop(data.ProcessID);

            parser.TcpIpSend += data =>
                AddTraffic(data.ProcessID, IpVersion.Ipv4, TransportProtocol.Tcp, TrafficDirection.Send, data.saddr, data.daddr, data.sport, data.dport, Math.Max(0, data.size), data.TimeStamp);
            parser.TcpIpRecv += data =>
                AddTraffic(data.ProcessID, IpVersion.Ipv4, TransportProtocol.Tcp, TrafficDirection.Receive, data.saddr, data.daddr, data.sport, data.dport, Math.Max(0, data.size), data.TimeStamp);
            parser.TcpIpSendIPV6 += data =>
                AddTraffic(data.ProcessID, IpVersion.Ipv6, TransportProtocol.Tcp, TrafficDirection.Send, data.saddr, data.daddr, data.sport, data.dport, Math.Max(0, data.size), data.TimeStamp);
            parser.TcpIpRecvIPV6 += data =>
                AddTraffic(data.ProcessID, IpVersion.Ipv6, TransportProtocol.Tcp, TrafficDirection.Receive, data.saddr, data.daddr, data.sport, data.dport, Math.Max(0, data.size), data.TimeStamp);

            parser.UdpIpSend += data =>
                AddTraffic(data.ProcessID, IpVersion.Ipv4, TransportProtocol.Udp, TrafficDirection.Send, data.saddr, data.daddr, data.sport, data.dport, Math.Max(0, data.size), data.TimeStamp);
            parser.UdpIpRecv += data =>
                AddTraffic(data.ProcessID, IpVersion.Ipv4, TransportProtocol.Udp, TrafficDirection.Receive, data.saddr, data.daddr, data.sport, data.dport, Math.Max(0, data.size), data.TimeStamp);
            parser.UdpIpSendIPV6 += data =>
                AddTraffic(data.ProcessID, IpVersion.Ipv6, TransportProtocol.Udp, TrafficDirection.Send, data.saddr, data.daddr, data.sport, data.dport, Math.Max(0, data.size), data.TimeStamp);
            parser.UdpIpRecvIPV6 += data =>
                AddTraffic(data.ProcessID, IpVersion.Ipv6, TransportProtocol.Udp, TrafficDirection.Receive, data.saddr, data.daddr, data.sport, data.dport, Math.Max(0, data.size), data.TimeStamp);

            ClearError();
            Volatile.Write(ref _captureActive, 1);
            session.Source.Process();
            if (!token.IsCancellationRequested)
            {
                RecordError("The ETW capture session stopped unexpectedly.");
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                RecordError(ex.Message);
            }
        }
        finally
        {
            Volatile.Write(ref _captureActive, 0);
            _session = null;
        }
    }

    private async Task PublishSnapshotsAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(SnapshotIntervalSeconds, 1, 10)), token).ConfigureAwait(false);
                var result = BuildSnapshot();
                var lostEvents = GetLostEventCount();
                SnapshotReady?.Invoke(this, new MonitorSnapshotEventArgs(
                    result.Snapshots,
                    LastErrorCount,
                    LastErrorText,
                    lostEvents,
                    result.Network,
                    result.Generation,
                    Volatile.Read(ref _captureActive) != 0));
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                RecordError(ex.Message);
            }
        }
    }

    private SnapshotBuildResult BuildSnapshot()
    {
        lock (_snapshotGate)
        {
            return BuildSnapshotCore();
        }
    }

    private SnapshotBuildResult BuildSnapshotCore()
    {
        var network = _networkSampler.Sample();
        var adapterChanged = network.IsAvailable
            && _lastAdapterEpoch != 0
            && network.AdapterEpoch != _lastAdapterEpoch;
        if (network.IsAvailable)
        {
            _lastAdapterEpoch = network.AdapterEpoch;
        }

        UpdateSelectedAdapterAddresses();

        if (adapterChanged)
        {
            lock (_gate)
            {
                _generation++;
                _counters.Clear();
                _recentFlows.Clear();
                _activeProcesses.Clear();
            }
        }

        var preparationTimestamp = Stopwatch.GetTimestamp();
        RefreshLocalAddressesIfNeeded(preparationTimestamp);
        RefreshProcessIdentities(preparationTimestamp);

        var snapshotTimestamp = Stopwatch.GetTimestamp();
        var previousTimestamp = Interlocked.Exchange(ref _lastSnapshotTimestamp, snapshotTimestamp);
        var elapsedSeconds = Math.Max(0.001, Stopwatch.GetElapsedTime(previousTimestamp, snapshotTimestamp).TotalSeconds);
        List<TrafficSnapshot> snapshots;
        long generation;

        lock (_gate)
        {
            generation = _generation;
            PruneStaleCounters(snapshotTimestamp);
            snapshots = new List<TrafficSnapshot>(_counters.Count);

            foreach (var pair in _counters)
            {
                var key = pair.Key;
                var counter = pair.Value;
                var (ipv4Flows, ipv6Flows) = PruneAndCountFlows(key, snapshotTimestamp);

                snapshots.Add(new TrafficSnapshot(
                    key.Pid,
                    counter.ProcessName,
                    counter.Path,
                    counter.Ipv4Received,
                    counter.Ipv4Sent,
                    counter.Ipv6Received,
                    counter.Ipv6Sent,
                    NetworkAdapterSampler.ToPerSecond(counter.CurrentIpv4Received, elapsedSeconds),
                    NetworkAdapterSampler.ToPerSecond(counter.CurrentIpv4Sent, elapsedSeconds),
                    NetworkAdapterSampler.ToPerSecond(counter.CurrentIpv6Received, elapsedSeconds),
                    NetworkAdapterSampler.ToPerSecond(counter.CurrentIpv6Sent, elapsedSeconds),
                    ipv4Flows,
                    ipv6Flows,
                    counter.LastSeen,
                    counter.TcpReceived,
                    counter.TcpSent,
                    counter.UdpReceived,
                    counter.UdpSent,
                    key.InstanceId));

                counter.CurrentIpv4Received = 0;
                counter.CurrentIpv4Sent = 0;
                counter.CurrentIpv6Received = 0;
                counter.CurrentIpv6Sent = 0;
            }
        }

        snapshots.Sort(static (left, right) =>
        {
            var rateComparison = TotalRate(right).CompareTo(TotalRate(left));
            return rateComparison != 0
                ? rateComparison
                : StringComparer.CurrentCultureIgnoreCase.Compare(left.ProcessName, right.ProcessName);
        });

        return new SnapshotBuildResult(snapshots, network, generation);
    }

    private static ulong TotalRate(TrafficSnapshot snapshot)
    {
        return snapshot.Ipv4ReceiveRate + snapshot.Ipv4SendRate + snapshot.Ipv6ReceiveRate + snapshot.Ipv6SendRate;
    }

    private void AddTraffic(
        int pid,
        IpVersion ipVersion,
        TransportProtocol protocol,
        TrafficDirection direction,
        IPAddress sourceAddress,
        IPAddress destinationAddress,
        int sourcePort,
        int destinationPort,
        int bytes,
        DateTime timestamp)
    {
        if (!ShouldTrackProcessId(pid) || bytes <= 0)
        {
            return;
        }

        var nowTimestamp = Stopwatch.GetTimestamp();

        lock (_gate)
        {
            if (ShouldExcludeTraffic(direction, sourceAddress, destinationAddress))
            {
                return;
            }

            var process = GetOrCreateActiveProcessLocked(pid);
            if (!_counters.TryGetValue(process.Key, out var counter))
            {
                counter = new EtwAggregateCounter(process.Name, process.Path);
                _counters[process.Key] = counter;
            }
            else
            {
                counter.ProcessName = process.Name;
                counter.Path = process.Path;
            }

            counter.LastSeen = timestamp;
            counter.LastSeenTimestamp = nowTimestamp;
            if (TrackFlows)
            {
                TrackFlow(process.Key, ipVersion, protocol, direction, sourceAddress, destinationAddress, sourcePort, destinationPort, nowTimestamp);
            }

            var byteCount = (ulong)bytes;
            if (ipVersion == IpVersion.Ipv4)
            {
                if (direction == TrafficDirection.Receive)
                {
                    counter.Ipv4Received = AddSaturating(counter.Ipv4Received, byteCount);
                    counter.CurrentIpv4Received = AddSaturating(counter.CurrentIpv4Received, byteCount);
                }
                else
                {
                    counter.Ipv4Sent = AddSaturating(counter.Ipv4Sent, byteCount);
                    counter.CurrentIpv4Sent = AddSaturating(counter.CurrentIpv4Sent, byteCount);
                }
            }
            else if (direction == TrafficDirection.Receive)
            {
                counter.Ipv6Received = AddSaturating(counter.Ipv6Received, byteCount);
                counter.CurrentIpv6Received = AddSaturating(counter.CurrentIpv6Received, byteCount);
            }
            else
            {
                counter.Ipv6Sent = AddSaturating(counter.Ipv6Sent, byteCount);
                counter.CurrentIpv6Sent = AddSaturating(counter.CurrentIpv6Sent, byteCount);
            }

            if (protocol == TransportProtocol.Tcp)
            {
                if (direction == TrafficDirection.Receive)
                {
                    counter.TcpReceived = AddSaturating(counter.TcpReceived, byteCount);
                }
                else
                {
                    counter.TcpSent = AddSaturating(counter.TcpSent, byteCount);
                }
            }
            else if (direction == TrafficDirection.Receive)
            {
                counter.UdpReceived = AddSaturating(counter.UdpReceived, byteCount);
            }
            else
            {
                counter.UdpSent = AddSaturating(counter.UdpSent, byteCount);
            }
        }
    }

    private void TrackFlow(
        ProcessInstanceKey processKey,
        IpVersion ipVersion,
        TransportProtocol protocol,
        TrafficDirection direction,
        IPAddress sourceAddress,
        IPAddress destinationAddress,
        int sourcePort,
        int destinationPort,
        long timestamp)
    {
        if (!_recentFlows.TryGetValue(processKey, out var flows))
        {
            flows = [];
            _recentFlows[processKey] = flows;
        }

        var key = direction == TrafficDirection.Send
            ? new FlowKey(ipVersion, protocol, sourceAddress, sourcePort, destinationAddress, destinationPort)
            : new FlowKey(ipVersion, protocol, destinationAddress, destinationPort, sourceAddress, sourcePort);

        if (flows.Count < MaximumFlowsPerProcess || flows.ContainsKey(key))
        {
            flows[key] = timestamp;
        }
    }

    private (int Ipv4, int Ipv6) PruneAndCountFlows(ProcessInstanceKey processKey, long nowTimestamp)
    {
        if (!_recentFlows.TryGetValue(processKey, out var flows))
        {
            return (0, 0);
        }

        var cutoff = nowTimestamp - ToStopwatchTicks(RecentFlowAge);
        List<FlowKey>? staleFlows = null;
        var ipv4 = 0;
        var ipv6 = 0;
        foreach (var pair in flows)
        {
            if (pair.Value < cutoff)
            {
                (staleFlows ??= []).Add(pair.Key);
            }
            else if (pair.Key.IpVersion == IpVersion.Ipv4)
            {
                ipv4++;
            }
            else
            {
                ipv6++;
            }
        }

        if (staleFlows is not null)
        {
            foreach (var stale in staleFlows)
            {
                flows.Remove(stale);
            }
        }

        if (flows.Count == 0)
        {
            _recentFlows.Remove(processKey);
        }

        return (ipv4, ipv6);
    }

    private ActiveProcess GetOrCreateActiveProcessLocked(int pid)
    {
        if (_activeProcesses.TryGetValue(pid, out var active))
        {
            return active;
        }

        if (pid == 0)
        {
            active = new ActiveProcess(
                new ProcessInstanceKey(0, long.MinValue),
                TrafficHistoryStore.UnattributedProcessName,
                TrafficHistoryStore.UnattributedPath,
                0,
                long.MaxValue);
            _activeProcesses[pid] = active;
            return active;
        }

        var instanceId = Interlocked.Increment(ref _nextGeneratedInstanceId);
        active = new ActiveProcess(
            new ProcessInstanceKey(pid, instanceId),
            $"PID {pid}",
            string.Empty,
            0,
            0);
        _activeProcesses[pid] = active;
        return active;
    }

    private void RefreshProcessIdentities(long nowTimestamp)
    {
        List<ActiveProcess> pending;
        var validationTicks = ToStopwatchTicks(ProcessValidationAge);
        lock (_gate)
        {
            pending = _activeProcesses.Values
                .Where(process => process.Key.Pid > 0)
                .Where(process => nowTimestamp - process.LastValidatedTimestamp >= validationTicks)
                .OrderBy(process => process.LastValidatedTimestamp)
                .Take(MaximumProcessValidationsPerSnapshot)
                .ToList();
        }

        foreach (var candidate in pending)
        {
            var resolved = ResolveProcess(candidate.Key.Pid, candidate.Path);
            lock (_gate)
            {
                if (!_activeProcesses.TryGetValue(candidate.Key.Pid, out var active)
                    || active.Key != candidate.Key)
                {
                    continue;
                }

                if (IsDifferentProcessInstance(active.StartTimeUtcTicks, resolved.StartTimeUtcTicks))
                {
                    _activeProcesses[candidate.Key.Pid] = CreateActiveProcess(candidate.Key.Pid, resolved, nowTimestamp);
                    continue;
                }

                active = active with
                {
                    Name = IsUnknownProcessName(resolved.Name) ? active.Name : resolved.Name,
                    Path = string.IsNullOrWhiteSpace(resolved.Path) ? active.Path : resolved.Path,
                    StartTimeUtcTicks = resolved.StartTimeUtcTicks > 0 ? resolved.StartTimeUtcTicks : active.StartTimeUtcTicks,
                    LastValidatedTimestamp = nowTimestamp
                };
                _activeProcesses[candidate.Key.Pid] = active;

                if (_counters.TryGetValue(active.Key, out var counter))
                {
                    counter.ProcessName = active.Name;
                    counter.Path = active.Path;
                }
            }
        }
    }

    private void RegisterProcessStart(int pid, string imageFileName)
    {
        if (pid <= 0)
        {
            return;
        }

        var path = Path.IsPathRooted(imageFileName) ? imageFileName : string.Empty;
        var name = Path.GetFileNameWithoutExtension(imageFileName);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"PID {pid}";
        }

        lock (_gate)
        {
            var instanceId = Interlocked.Increment(ref _nextGeneratedInstanceId);
            _activeProcesses[pid] = new ActiveProcess(
                new ProcessInstanceKey(pid, instanceId),
                name,
                path,
                0,
                0);
        }
    }

    private void RegisterProcessStop(int pid)
    {
        lock (_gate)
        {
            _activeProcesses.Remove(pid);
        }
    }

    private ActiveProcess CreateActiveProcess(int pid, ResolvedProcess resolved, long nowTimestamp)
    {
        var instanceId = resolved.StartTimeUtcTicks > 0
            ? resolved.StartTimeUtcTicks
            : Interlocked.Increment(ref _nextGeneratedInstanceId);
        return new ActiveProcess(
            new ProcessInstanceKey(pid, instanceId),
            resolved.Name,
            resolved.Path,
            resolved.StartTimeUtcTicks,
            nowTimestamp);
    }

    internal static bool IsDifferentProcessInstance(long existingStartTimeUtcTicks, long currentStartTimeUtcTicks)
    {
        return existingStartTimeUtcTicks > 0
            && currentStartTimeUtcTicks > 0
            && existingStartTimeUtcTicks != currentStartTimeUtcTicks;
    }

    internal static bool ShouldTrackProcessId(int pid) => pid >= 0;

    private static bool IsUnknownProcessName(string name)
    {
        return name.StartsWith("PID ", StringComparison.OrdinalIgnoreCase);
    }

    private static ResolvedProcess ResolveProcess(int pid, string fallbackImageFileName)
    {
        var fallbackPath = Path.IsPathRooted(fallbackImageFileName) ? fallbackImageFileName : string.Empty;
        var fallbackName = Path.GetFileNameWithoutExtension(fallbackImageFileName);

        try
        {
            using var process = Process.GetProcessById(pid);
            var name = process.ProcessName;
            var path = fallbackPath;
            long startTimeUtcTicks = 0;

            try
            {
                path = process.MainModule?.FileName ?? path;
            }
            catch
            {
            }

            try
            {
                startTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
            }
            catch
            {
            }

            return new ResolvedProcess(name, path, startTimeUtcTicks);
        }
        catch
        {
            return new ResolvedProcess(
                string.IsNullOrWhiteSpace(fallbackName) ? $"PID {pid}" : fallbackName,
                fallbackPath,
                0);
        }
    }

    private bool ShouldExcludeTraffic(
        TrafficDirection direction,
        IPAddress sourceAddress,
        IPAddress destinationAddress)
    {
        var normalizedSource = NormalizeAddress(sourceAddress);
        var normalizedDestination = NormalizeAddress(destinationAddress);

        if (!MatchesSelectedAdapterPath(
                direction == TrafficDirection.Send,
                normalizedSource,
                normalizedDestination,
                _selectedAdapterAddresses,
                _localAddresses))
        {
            return true;
        }

        if (!ExcludeLocalTraffic)
        {
            return false;
        }

        if (IPAddress.IsLoopback(normalizedSource) || IPAddress.IsLoopback(normalizedDestination))
        {
            return true;
        }

        return _localAddresses.Contains(normalizedSource) && _localAddresses.Contains(normalizedDestination);
    }

    internal static bool MatchesSelectedAdapterPath(
        bool isSend,
        IPAddress sourceAddress,
        IPAddress destinationAddress,
        IReadOnlySet<IPAddress> selectedAddresses,
        IReadOnlySet<IPAddress> localAddresses)
    {
        // Without a selected interface address, adapter attribution is unknown.
        if (selectedAddresses.Count == 0)
        {
            return false;
        }

        if (isSend)
        {
            return selectedAddresses.Contains(NormalizeAddress(sourceAddress));
        }

        var destination = NormalizeAddress(destinationAddress);
        if (selectedAddresses.Contains(destination))
        {
            return true;
        }

        // WFP/TUN receive events can expose a rewritten destination that is not
        // assigned to any adapter. Only reject a receive that Windows clearly
        // associates with a different local interface.
        return !localAddresses.Contains(destination);
    }

    internal static IPAddress NormalizeAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            && address.ScopeId != 0)
        {
            return new IPAddress(address.GetAddressBytes());
        }

        return address;
    }

    private void UpdateSelectedAdapterAddresses()
    {
        var addresses = _networkSampler.GetSelectedAddresses()
            .Select(NormalizeAddress)
            .ToHashSet();

        lock (_gate)
        {
            _selectedAdapterAddresses = addresses;
        }
    }

    private void RefreshLocalAddressesIfNeeded(long nowTimestamp)
    {
        if (nowTimestamp < Volatile.Read(ref _nextLocalAddressRefreshTimestamp))
        {
            return;
        }

        RefreshLocalAddresses();
        Volatile.Write(ref _nextLocalAddressRefreshTimestamp, nowTimestamp + ToStopwatchTicks(TimeSpan.FromSeconds(15)));
    }

    private void RefreshLocalAddresses()
    {
        var addresses = new HashSet<IPAddress>();
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
                    addresses.Add(NormalizeAddress(address.Address));
                }
            }
        }
        catch
        {
        }

        lock (_gate)
        {
            _localAddresses = addresses;
        }
    }

    private void PruneStaleCounters(long nowTimestamp)
    {
        var cutoff = nowTimestamp - ToStopwatchTicks(StaleCounterAge);
        List<ProcessInstanceKey>? staleKeys = null;
        foreach (var pair in _counters)
        {
            if (pair.Key.Pid == 0)
            {
                continue;
            }

            if (pair.Value.LastSeenTimestamp < cutoff)
            {
                (staleKeys ??= []).Add(pair.Key);
            }
        }

        if (staleKeys is null)
        {
            return;
        }

        foreach (var key in staleKeys)
        {
            _counters.Remove(key);
            _recentFlows.Remove(key);
            if (_activeProcesses.TryGetValue(key.Pid, out var active) && active.Key == key)
            {
                _activeProcesses.Remove(key.Pid);
            }
        }
    }

    private int GetLostEventCount()
    {
        try
        {
            var session = _session;
            return session is null ? 0 : Math.Max(session.EventsLost, session.Source.EventsLost);
        }
        catch
        {
            return 0;
        }
    }

    private void ClearError()
    {
        Volatile.Write(ref _lastErrorText, string.Empty);
        Volatile.Write(ref _lastErrorCount, 0);
    }

    private void RecordError(string message)
    {
        Volatile.Write(ref _lastErrorText, message);
        Interlocked.Increment(ref _lastErrorCount);
    }

    private static long ToStopwatchTicks(TimeSpan duration)
    {
        return (long)(duration.TotalSeconds * Stopwatch.Frequency);
    }

    private static ulong AddSaturating(ulong left, ulong right)
    {
        return ulong.MaxValue - left < right ? ulong.MaxValue : left + right;
    }

    private sealed class EtwAggregateCounter(string processName, string path)
    {
        public string ProcessName = processName;
        public string Path = path;
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
        public long LastSeenTimestamp = Stopwatch.GetTimestamp();
    }

    private sealed record ActiveProcess(
        ProcessInstanceKey Key,
        string Name,
        string Path,
        long StartTimeUtcTicks,
        long LastValidatedTimestamp);
    private sealed record ResolvedProcess(string Name, string Path, long StartTimeUtcTicks);
    private readonly record struct ProcessInstanceKey(int Pid, long InstanceId);
    private readonly record struct FlowKey(
        IpVersion IpVersion,
        TransportProtocol Protocol,
        IPAddress LocalAddress,
        int LocalPort,
        IPAddress RemoteAddress,
        int RemotePort);
    private sealed record SnapshotBuildResult(
        IReadOnlyList<TrafficSnapshot> Snapshots,
        NetworkTrafficSnapshot Network,
        long Generation);

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
