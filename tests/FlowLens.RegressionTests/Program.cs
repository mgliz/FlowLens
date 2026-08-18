using FlowLens;
using System.Globalization;
using System.Net;

var failures = new List<string>();
const string TestInterfaceId = "test-interface";

foreach (var key in new[]
         {
             "PhysicalNetworkTraffic",
             "AttributedProcessTraffic",
             "ProcessDetails",
             "SortBy",
             "Ascending",
             "Descending",
             "CurrentRate",
             "ReceiveRate",
             "SendRate",
             "PeriodTotal",
             "ClearSearch",
             "GeneralSettings",
             "CaptureSettings",
             "AppearanceSettings",
             "DataSettings",
             "InvalidMinimumBytes"
         })
{
    Check(
        Localizer.T("en-US", key) != key && Localizer.T("zh-CN", key) != key,
        $"The UI localization key '{key}' must exist in English and Chinese.");
}

Check(ThemeManager.Resolve(AppTheme.Dark) == AppTheme.Dark, "An explicit dark theme must remain dark.");
Check(ThemeManager.Resolve(AppTheme.Light) == AppTheme.Light, "An explicit light theme must remain light.");

Check(
    EtwTrafficMonitor.IsDifferentProcessInstance(100, 200),
    "A reused PID with a different process start time must create a new instance.");
Check(
    !EtwTrafficMonitor.IsDifferentProcessInstance(100, 100),
    "The same process start time must keep the existing instance.");
Check(
    !EtwTrafficMonitor.IsDifferentProcessInstance(0, 200),
    "An unavailable old start time must not discard an active instance by itself.");
Check(
    EtwTrafficMonitor.ShouldTrackProcessId(0),
    "PID 0 kernel traffic must be retained as unattributed traffic.");
Check(
    !EtwTrafficMonitor.ShouldTrackProcessId(-1),
    "An invalid negative process ID must still be rejected.");

using (var resetMonitor = new EtwTrafficMonitor())
{
    var generation = resetMonitor.Generation;
    resetMonitor.Reset();
    Check(
        resetMonitor.Generation == generation + 1,
        "Reset must advance the snapshot generation so queued pre-reset snapshots are rejected.");
}

Check(
    NetworkAdapterSampler.SubtractCounter(1500, 1000) == 500,
    "Adapter deltas must use the counter difference.");
Check(
    NetworkAdapterSampler.SubtractCounter(10, 1000) == 0,
    "A reset adapter counter must not wrap to an enormous ulong delta.");
Check(
    NetworkAdapterSampler.ToPerSecond(1000, 2) == 500,
    "Rates must use the measured elapsed interval.");

var sortableRow = new TrafficRow(Snapshot(ipv4Received: 100, instanceId: 1) with
{
    Ipv4Sent = 50,
    Ipv6Received = 20,
    Ipv6Sent = 30,
    Ipv4ReceiveRate = 10,
    Ipv4SendRate = 15,
    Ipv6ReceiveRate = 4,
    Ipv6SendRate = 6,
    TcpReceived = 80,
    TcpSent = 40,
    UdpReceived = 40,
    UdpSent = 60
});
Check(
    sortableRow.Ipv4Rate == 25 && sortableRow.Ipv6Rate == 10 && sortableRow.TotalRate == 35,
    "Displayed IP rate columns must sort by their receive and send sum.");
Check(
    sortableRow.Ipv4Total == 150 && sortableRow.Ipv6Total == 50 &&
    sortableRow.TcpTotal == 120 && sortableRow.UdpTotal == 100,
    "Displayed IP and protocol total columns must sort by their receive and send sum.");

var adapterAddress = IPAddress.Parse("10.70.105.171");
var remoteAddress = IPAddress.Parse("1.1.1.1");
var rewrittenAddress = IPAddress.Parse("198.18.0.1");
var otherAdapterAddress = IPAddress.Parse("172.29.176.1");
var selectedAddresses = new HashSet<IPAddress> { adapterAddress };
var localAddresses = new HashSet<IPAddress> { adapterAddress, otherAdapterAddress };
Check(
    EtwTrafficMonitor.MatchesSelectedAdapterPath(
        isSend: true,
        adapterAddress,
        remoteAddress,
        selectedAddresses,
        localAddresses),
    "A send whose source belongs to the selected adapter must be counted.");
Check(
    EtwTrafficMonitor.MatchesSelectedAdapterPath(
        isSend: false,
        remoteAddress,
        adapterAddress,
        selectedAddresses,
        localAddresses),
    "A receive whose destination belongs to the selected adapter must be counted.");
Check(
    !EtwTrafficMonitor.MatchesSelectedAdapterPath(
        isSend: true,
        rewrittenAddress,
        remoteAddress,
        selectedAddresses,
        localAddresses),
    "A proxy or TUN inner send must not be mixed with the selected adapter's outer send.");
Check(
    EtwTrafficMonitor.MatchesSelectedAdapterPath(
        isSend: false,
        remoteAddress,
        rewrittenAddress,
        selectedAddresses,
        localAddresses),
    "A WFP-rewritten receive destination must be retained when it is not assigned to another adapter.");
Check(
    !EtwTrafficMonitor.MatchesSelectedAdapterPath(
        isSend: false,
        remoteAddress,
        otherAdapterAddress,
        selectedAddresses,
        localAddresses),
    "A receive explicitly bound to another local adapter must be excluded.");
Check(
    EtwTrafficMonitor.MatchesSelectedAdapterPath(
        isSend: true,
        IPAddress.Parse("::ffff:10.70.105.171"),
        remoteAddress,
        selectedAddresses,
        localAddresses),
    "IPv4-mapped IPv6 endpoints must match the equivalent selected IPv4 address.");
var linkLocal = IPAddress.Parse("fe80::1234");
var scopedLinkLocal = new IPAddress(linkLocal.GetAddressBytes(), 12);
Check(
    EtwTrafficMonitor.NormalizeAddress(scopedLinkLocal).Equals(linkLocal),
    "IPv6 scope IDs must not prevent selected-adapter address matching.");
Check(
    !EtwTrafficMonitor.MatchesSelectedAdapterPath(
        isSend: true,
        adapterAddress,
        remoteAddress,
        new HashSet<IPAddress>(),
        localAddresses),
    "Traffic matching must fail closed while adapter attribution is unavailable.");

var previous = Snapshot(ipv4Received: 1000, instanceId: 10);
var current = Snapshot(ipv4Received: 1300, instanceId: 10);
Check(
    TrafficCounters.Delta(current, previous).Ipv4Received == 300,
    "History must persist only the new bytes from a cumulative snapshot.");

var restarted = Snapshot(ipv4Received: 80, instanceId: 11);
Check(
    TrafficCounters.Delta(restarted, previous).Ipv4Received == 80,
    "A restarted process counter must use its new baseline instead of underflowing.");

var unresolvedIdentity = Snapshot(
    ipv4Received: 40,
    instanceId: 12,
    processName: "PID 42",
    path: string.Empty);
var resolvedIdentity = Snapshot(
    ipv4Received: 90,
    instanceId: 12,
    processName: "FlowLensTest",
    path: @"C:\FlowLensTest.exe");
Check(
    MainWindow.RawKeyFor(unresolvedIdentity) == MainWindow.RawKeyFor(resolvedIdentity),
    "Resolving a process name or path must not reset its cumulative-history baseline.");
Check(
    MainWindow.RawKeyFor(resolvedIdentity) != MainWindow.RawKeyFor(restarted),
    "Different process instances sharing a PID must keep separate history baselines.");

var persistenceTracker = new TrafficPersistenceTracker();
var identityKey = MainWindow.RawKeyFor(unresolvedIdentity);
Check(
    persistenceTracker.Observe(identityKey, unresolvedIdentity, persistenceEnabled: true) is null,
    "An unresolved process must wait for a persistable identity without advancing its baseline.");
var resolvedDelta = persistenceTracker.Observe(identityKey, resolvedIdentity, persistenceEnabled: true);
Check(
    resolvedDelta?.Ipv4Received == resolvedIdentity.Ipv4Received,
    "Resolving process metadata must backfill the instance bytes collected while identity was unknown.");
var renamedIdentity = Snapshot(
    ipv4Received: 120,
    instanceId: 12,
    processName: "FlowLensTest",
    path: @"c:\flowlenstest.exe");
var renamedDelta = persistenceTracker.Observe(identityKey, renamedIdentity, persistenceEnabled: true);
Check(
    renamedDelta?.Ipv4Received == 30,
    "Changing metadata on a persisted instance must add only bytes since its last baseline.");
persistenceTracker.Observe(
    identityKey,
    Snapshot(ipv4Received: 150, instanceId: 12),
    persistenceEnabled: false);
var reenabledDelta = persistenceTracker.Observe(
    identityKey,
    Snapshot(ipv4Received: 180, instanceId: 12),
    persistenceEnabled: true);
Check(
    reenabledDelta?.Ipv4Received == 30,
    "Re-enabling persistence must not backfill bytes collected while persistence was disabled.");
var validNetwork = new NetworkTrafficSnapshot(
    true,
    true,
    TestInterfaceId,
    "Test adapter",
    1,
    0,
    0,
    0,
    0,
    0,
    0);
Check(
    !MainWindow.ShouldPersistSnapshot(persistenceEnabled: true, captureActive: false, validNetwork),
    "Physical history must not advance while logical ETW capture is unavailable.");
Check(
    MainWindow.ShouldPersistSnapshot(persistenceEnabled: true, captureActive: true, validNetwork),
    "Aligned physical and logical history must advance while ETW capture is active.");
Check(
    !MainWindow.ShouldPersistSnapshot(true, true, validNetwork with { IsAttributionAvailable = false }),
    "Adapter-aligned history must pause while process attribution is unavailable.");

var unattributed = Snapshot(
    ipv4Received: 512,
    instanceId: long.MinValue,
    processName: TrafficHistoryStore.UnattributedProcessName,
    path: TrafficHistoryStore.UnattributedPath) with
{ Pid = 0 };
Check(
    TrafficHistoryStore.IsPersistable(unattributed.ProcessName, unattributed.Path),
    "Unattributed kernel traffic must be persistable under a stable synthetic identity.");

Check(
    TrafficStatsStore.KeyFor("svchost", @"C:\WINDOWS\system32\svchost.exe")
        == TrafficStatsStore.KeyFor("svchost", @"c:\windows\System32\svchost.exe"),
    "Windows paths that differ only by case must share one stable history key.");

var saturated = new TrafficCounters { Ipv4Received = ulong.MaxValue };
saturated.Add(new TrafficCounters { Ipv4Received = 1 });
Check(
    saturated.Ipv4Received == ulong.MaxValue,
    "Long-running counters must saturate instead of wrapping to zero.");

var history = new TrafficHistoryStore();
history.AddDelta(
    "FlowLensTest",
    @"C:\FlowLensTest.exe",
    new TrafficCounters { Ipv4Received = 123, TcpReceived = 123 },
    DateTime.Now,
    TestInterfaceId);
history.AddDelta(
    "FlowLensTest",
    @"C:\FlowLensTest.exe",
    new TrafficCounters { Ipv4Received = 9_999, TcpReceived = 9_999 },
    DateTime.Now,
    "other-interface");
var rows = history.BuildSnapshots(TrafficTimeRange.Today, [], TestInterfaceId);
Check(
    rows.Count == 1 && rows[0].Ipv4Received == 123 && rows[0].TcpReceived == 123,
    "A daily bucket must preserve matching IP/protocol totals without mixing adapters.");

var caseInsensitiveHistory = new TrafficHistoryStore();
caseInsensitiveHistory.AddDelta(
    "svchost",
    @"C:\WINDOWS\system32\svchost.exe",
    new TrafficCounters { Ipv4Received = 10, TcpReceived = 10 },
    DateTime.Now,
    TestInterfaceId);
caseInsensitiveHistory.AddDelta(
    "svchost",
    @"c:\windows\System32\svchost.exe",
    new TrafficCounters { Ipv4Received = 20, TcpReceived = 20 },
    DateTime.Now,
    TestInterfaceId);
var mergedRows = caseInsensitiveHistory.BuildSnapshots(TrafficTimeRange.Today, [], TestInterfaceId);
Check(
    mergedRows.Count == 1 && mergedRows[0].Ipv4Received == 30,
    "Case-only path differences must merge without dropping or duplicating traffic.");

var originalCulture = CultureInfo.CurrentCulture;
try
{
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
    var cultureIndependentHistory = new TrafficHistoryStore();
    cultureIndependentHistory.AddDelta(
        "FlowLensTest",
        @"C:\FlowLensTest.exe",
        new TrafficCounters { Ipv6Received = 7, UdpReceived = 7 },
        DateTime.Now,
        TestInterfaceId);
    var cultureRows = cultureIndependentHistory.BuildSnapshots(TrafficTimeRange.Today, [], TestInterfaceId);
    Check(
        cultureRows.Count == 1 && cultureRows[0].Ipv6Received == 7,
        "Daily history buckets must not depend on the Windows display culture.");
}
finally
{
    CultureInfo.CurrentCulture = originalCulture;
}

var networkHistory = new NetworkTrafficHistoryStore();
networkHistory.AddDelta(
    new NetworkTrafficSnapshot(
        true,
        true,
        TestInterfaceId,
        "Test adapter",
        1,
        10_000,
        20_000,
        100,
        200,
        400,
        600),
    DateTime.Now);
networkHistory.AddDelta(
    validNetwork with { InterfaceId = "other-interface", ReceivedDelta = 9_000, SentDelta = 9_000 },
    DateTime.Now);
var networkTotals = networkHistory.GetTotals(
    TrafficTimeRange.Today,
    validNetwork);
Check(
    networkTotals.Received == 400 && networkTotals.Sent == 600,
    "Physical history must persist interval deltas and isolate different adapters.");

var adapterSampler = new NetworkAdapterSampler();
var adapter = adapterSampler.Sample();
if (adapter.IsAvailable)
{
    Console.WriteLine($"Adapter sampler: {adapter.InterfaceName}");
}

if (failures.Count == 0)
{
    Console.WriteLine("FlowLens regression tests passed.");
    return 0;
}

foreach (var failure in failures)
{
    Console.Error.WriteLine($"FAIL: {failure}");
}

return 1;

void Check(bool condition, string message)
{
    if (!condition)
    {
        failures.Add(message);
    }
}

static TrafficSnapshot Snapshot(
    ulong ipv4Received,
    long instanceId,
    string processName = "FlowLensTest",
    string path = @"C:\FlowLensTest.exe")
{
    return new TrafficSnapshot(
        42,
        processName,
        path,
        ipv4Received,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        DateTime.Now,
        ipv4Received,
        0,
        0,
        0,
        instanceId);
}
