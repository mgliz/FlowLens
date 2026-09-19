using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using FlowLens;

internal static class CaptureLifecycleTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(Action<bool, string> check)
    {
        var tracker = new CaptureContinuityTracker();
        var network = Network();
        bool Sample(NetworkTrafficSnapshot? sample = null) => tracker.CompleteSample(tracker.BeginSample(), sample ?? network);
        check(!Sample(), "Inactive capture must not establish a valid physical interval.");
        tracker.CaptureStarted();
        check(!Sample() && Sample(), "Capture startup must establish one baseline before the first complete interval.");
        tracker.CaptureStopped(expected: false);
        tracker.CaptureStarted();
        check(!Sample() && Sample(), "A stop/restart entirely between two samples must invalidate the recovery interval.");
        var interruptedRead = tracker.BeginSample();
        tracker.CaptureStopped(expected: false);
        tracker.CaptureStarted();
        check(!tracker.CompleteSample(interruptedRead, network), "A capture restart during the physical reading must invalidate that reading.");
        check(!Sample() && Sample(), "An interrupted physical reading must require a fresh stable baseline.");
        check(!Sample(NetworkTrafficSnapshot.Unavailable) && !Sample() && Sample(), "Adapter unavailability must invalidate both the gap and the recovery interval.");
        check(!Sample(network with { IsAttributionAvailable = false }) && !Sample() && Sample(), "Missing attribution addresses must also invalidate the recovery interval.");
        network = network with { AdapterEpoch = 2, InterfaceId = "capture-lifecycle-second" };
        check(!Sample() && Sample(), "An adapter change must start a new comparison baseline.");
        tracker.ResetSampleBaseline();
        check(!Sample() && Sample(), "An explicit reset must discard the old physical sampling baseline.");
        var finalBoundary = tracker.BeginSample();
        check(tracker.CompleteSample(finalBoundary, network), "The final physical endpoint must be read while capture is still active.");
        tracker.CaptureStopped(expected: true);
        check(tracker.IsExpectedFinalBoundary(finalBoundary), "Normal capture shutdown must retain eligibility for its frozen final physical interval.");
        check(!Sample(), "Ordinary snapshots after a normal stop must remain incomplete.");
        tracker.CaptureStarted();
        Sample();
        var failedBoundary = tracker.BeginSample();
        tracker.CompleteSample(failedBoundary, network);
        tracker.CaptureStopped(expected: false);
        check(!tracker.IsExpectedFinalBoundary(failedBoundary), "Unexpected capture failure must never qualify as a complete final interval.");
        tracker.CaptureStarted();
        tracker.CaptureStopped(expected: true);
        check(!tracker.IsExpectedFinalBoundary(failedBoundary), "A later successful stop cannot validate an older failed session endpoint.");

        using (var neverStarted = new EtwTrafficMonitor())
        {
            var count = 0;
            neverStarted.SnapshotReady += (_, _) => count++;
            neverStarted.Stop();
            neverStarted.Dispose();
            check(count == 0, "Stopping or disposing a monitor which never started must not publish a snapshot.");
        }

        VerifyFinalDrain(check);
        VerifyStopTimeout(check);
    }

    private static void VerifyFinalDrain(Action<bool, string> check)
    {
        using var monitor = new EtwTrafficMonitor();
        using var captureCancellation = new CancellationTokenSource();
        using var snapshotCancellation = new CancellationTokenSource();
        var adapter = new FakeAdapter();
        var sampler = (NetworkAdapterSampler)Get(monitor, "_networkSampler");
        Set(sampler, "_selectedInterface", adapter);
        Set(sampler, "_selectedAddresses", new HashSet<IPAddress> { IPAddress.Parse("192.0.2.1") });
        Set(sampler, "_nextSelectionRefreshTimestamp", long.MaxValue);
        var network = sampler.Sample();
        Set(monitor, "_lastAdapterEpoch", network.AdapterEpoch);
        Set(monitor, "_selectedAdapterAddresses", new HashSet<IPAddress> { IPAddress.Parse("192.0.2.1") });
        Set(monitor, "_nextLocalAddressRefreshTimestamp", long.MaxValue);
        var tracker = (CaptureContinuityTracker)Get(monitor, "_captureContinuity");
        tracker.CaptureStarted();
        tracker.CompleteSample(tracker.BeginSample(), network);
        adapter.Received = 100;
        var order = new ConcurrentQueue<string>();
        var snapshotTask = Task.Run(async () =>
        {
            try { await Task.Delay(Timeout.Infinite, snapshotCancellation.Token); }
            catch (OperationCanceledException) { }
            order.Enqueue("snapshots-finished");
        });
        var eventTask = Task.Run(async () =>
        {
            try { await Task.Delay(Timeout.Infinite, captureCancellation.Token); }
            catch (OperationCanceledException) { }
            AddBufferedTraffic(monitor);
            tracker.CaptureStopped(expected: true);
            Set(monitor, "_captureActive", 0);
            order.Enqueue("events-drained");
        });
        InstallProductionTasks(monitor, captureCancellation, snapshotCancellation, eventTask, snapshotTask);
        MonitorSnapshotEventArgs? final = null;
        var published = 0;
        monitor.SnapshotReady += (_, snapshot) =>
        {
            final = snapshot;
            published++;
            order.Enqueue("final-published");
            check(!Monitor.IsEntered(Get(monitor, "_snapshotGate"))
                && !Monitor.IsEntered(Get(monitor, "_gate"))
                && !Monitor.IsEntered(Get(monitor, "_lifecycleGate")),
                "The final consumer must be invoked outside all monitor locks.");
        };
        monitor.Stop();
        monitor.Stop();
        check(order.SequenceEqual(new[] { "snapshots-finished", "events-drained", "final-published" }),
            "Stop must finish ordinary snapshots, drain ETW events, and only then publish its final snapshot.");
        check(published == 1 && final is { IsFinalSnapshot: true, CaptureActive: false, IsCaptureIntervalComplete: true },
            "A normal stop must publish exactly one complete final event with the real inactive state.");
        check(final?.Network.ReceivedDelta == 100 && final.Snapshots.Single().Ipv4Received == 100,
            "The final snapshot must include the frozen physical delta and events delivered during capture shutdown.");
        check(adapter.ReadCount == 2, "Stop must use the frozen endpoint and repeated Stop must not sample again.");
    }

    private static void VerifyStopTimeout(Action<bool, string> check)
    {
        using var monitor = new EtwTrafficMonitor();
        using var captureCancellation = new CancellationTokenSource();
        using var snapshotCancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        InstallProductionTasks(monitor, captureCancellation, snapshotCancellation, Task.CompletedTask, pending.Task);
        var count = 0;
        monitor.SnapshotReady += (_, _) => count++;
        var started = Stopwatch.GetTimestamp();
        monitor.Stop();
        check(count == 0 && monitor.LastErrorCount > 0 && monitor.LastErrorText.Contains("Timed out", StringComparison.Ordinal),
            "A producer stop timeout must report the lost final interval without publishing a final snapshot.");
        check(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(8), "Both producer waits must share one five-second budget.");
        pending.SetResult();
        monitor.Stop();
        check(count == 0, "Later disposal after a timed-out stop must not claim a complete final sample.");
    }

    private static void InstallProductionTasks(EtwTrafficMonitor monitor, CancellationTokenSource captureCancellation,
        CancellationTokenSource snapshotCancellation, Task eventTask, Task snapshotTask)
    {
        Set(monitor, "_started", 1);
        Set(monitor, "_captureActive", 1);
        Set(monitor, "_finalSnapshotPending", true);
        Set(monitor, "_cancellation", captureCancellation);
        Set(monitor, "_snapshotCancellation", snapshotCancellation);
        Set(monitor, "_eventTask", eventTask);
        Set(monitor, "_snapshotTask", snapshotTask);
    }

    private static void AddBufferedTraffic(EtwTrafficMonitor monitor)
    {
        var method = typeof(EtwTrafficMonitor).GetMethod("AddTraffic", Fields)!;
        var parameters = method.GetParameters();
        method.Invoke(monitor, new object[]
        {
            0, IpVersion.Ipv4, Enum.Parse(parameters[2].ParameterType, "Tcp"),
            Enum.Parse(parameters[3].ParameterType, "Receive"), IPAddress.Parse("192.0.2.1"),
            IPAddress.Parse("198.51.100.1"), 12345, 443, 100, DateTime.Now
        });
    }

    private static NetworkTrafficSnapshot Network() => new(true, true, "capture-lifecycle-first", "Fixture", 1, 0, 0, 0, 0, 0, 0);
    private static object Get(object instance, string field) => instance.GetType().GetField(field, Fields)!.GetValue(instance)!;
    private static void Set(object instance, string field, object value) => instance.GetType().GetField(field, Fields)!.SetValue(instance, value);

    private sealed class FakeAdapter : NetworkInterface
    {
        public long Received;
        public int ReadCount;
        public override string Id => "capture-lifecycle-fake-adapter";
        public override string Name => "In-memory adapter";
        public override string Description => Name;
        public override OperationalStatus OperationalStatus => OperationalStatus.Up;
        public override NetworkInterfaceType NetworkInterfaceType => NetworkInterfaceType.Ethernet;
        public override long Speed => 1_000_000;
        public override bool IsReceiveOnly => false;
        public override bool SupportsMulticast => false;
        public override IPInterfaceProperties GetIPProperties() => throw new NotSupportedException();
        public override IPv4InterfaceStatistics GetIPv4Statistics() => throw new NotSupportedException();
        public override PhysicalAddress GetPhysicalAddress() => PhysicalAddress.None;
        public override bool Supports(NetworkInterfaceComponent networkInterfaceComponent) => true;
        public override IPInterfaceStatistics GetIPStatistics()
        {
            ReadCount++;
            return new FakeStatistics(Received);
        }
    }

    private sealed class FakeStatistics(long received) : IPInterfaceStatistics
    {
        public override long BytesReceived => received;
        public override long BytesSent => 0;
        public override long IncomingPacketsDiscarded => 0;
        public override long IncomingPacketsWithErrors => 0;
        public override long IncomingUnknownProtocolPackets => 0;
        public override long NonUnicastPacketsReceived => 0;
        public override long NonUnicastPacketsSent => 0;
        public override long OutgoingPacketsDiscarded => 0;
        public override long OutgoingPacketsWithErrors => 0;
        public override long OutputQueueLength => 0;
        public override long UnicastPacketsReceived => 0;
        public override long UnicastPacketsSent => 0;
    }
}
