using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace FlowLens;

public sealed class NetworkAdapterSampler
{
    private static readonly long SelectionRefreshTicks = (long)(TimeSpan.FromSeconds(30).TotalSeconds * Stopwatch.Frequency);
    private readonly object _sampleGate = new();
    private readonly object _selectionGate = new();
    private readonly Dictionary<string, AdapterReading> _previous = [];
    private NetworkInterface? _selectedInterface;
    private HashSet<IPAddress> _selectedAddresses = [];
    private long _nextSelectionRefreshTimestamp;
    private long _adapterEpoch;
    private string _preferredInterfaceId = string.Empty;
    private string _sampledInterfaceId = string.Empty;
    private ulong _sessionReceived;
    private ulong _sessionSent;

    public string PreferredInterfaceId
    {
        get
        {
            lock (_selectionGate)
            {
                return _preferredInterfaceId;
            }
        }
        set
        {
            lock (_selectionGate)
            {
                value ??= string.Empty;
                if (_preferredInterfaceId.Equals(value, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _preferredInterfaceId = value;
                _selectedInterface = null;
                _selectedAddresses = [];
                _nextSelectionRefreshTimestamp = 0;
            }
        }
    }

    public NetworkTrafficSnapshot Sample()
    {
        lock (_sampleGate)
        {
            var selected = SelectInterface();
            if (selected is null || !TryRead(selected, out var reading))
            {
                _previous.Clear();
                return NetworkTrafficSnapshot.Unavailable;
            }

            if (!_sampledInterfaceId.Equals(selected.Id, StringComparison.OrdinalIgnoreCase))
            {
                _sampledInterfaceId = selected.Id;
                _adapterEpoch++;
                _previous.Clear();
                _sessionReceived = 0;
                _sessionSent = 0;
            }

            var readingTimestamp = Stopwatch.GetTimestamp();
            reading = reading with { Timestamp = readingTimestamp };
            ulong receivedDelta = 0;
            ulong sentDelta = 0;
            double elapsedSeconds = 0;

            if (_previous.TryGetValue(selected.Id, out var previous))
            {
                receivedDelta = SubtractCounter(reading.Received, previous.Received);
                sentDelta = SubtractCounter(reading.Sent, previous.Sent);
                elapsedSeconds = Math.Max(
                    0.001,
                    Stopwatch.GetElapsedTime(previous.Timestamp, readingTimestamp).TotalSeconds);
                _sessionReceived = AddSaturating(_sessionReceived, receivedDelta);
                _sessionSent = AddSaturating(_sessionSent, sentDelta);
            }

            _previous.Clear();
            _previous[selected.Id] = reading;

            return new NetworkTrafficSnapshot(
                true,
                HasSelectedAddresses(),
                selected.Id,
                selected.Name,
                _adapterEpoch,
                _sessionReceived,
                _sessionSent,
                ToPerSecond(receivedDelta, elapsedSeconds),
                ToPerSecond(sentDelta, elapsedSeconds),
                receivedDelta,
                sentDelta);
        }
    }

    public IReadOnlySet<IPAddress> GetSelectedAddresses()
    {
        lock (_selectionGate)
        {
            return new HashSet<IPAddress>(_selectedAddresses);
        }
    }

    private bool HasSelectedAddresses()
    {
        lock (_selectionGate)
        {
            return _selectedAddresses.Count > 0;
        }
    }

    public void Reset()
    {
        lock (_sampleGate)
        {
            _previous.Clear();
            _sessionReceived = 0;
            _sessionSent = 0;
        }
    }

    public static IReadOnlyList<NetworkInterfaceOption> GetInterfaceOptions()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(IsUsable)
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => new NetworkInterfaceOption(item.Id, item.Name, item.Description))
            .ToList();
    }

    private NetworkInterface? SelectInterface()
    {
        lock (_selectionGate)
        {
            var nowTimestamp = Stopwatch.GetTimestamp();
            if (_selectedInterface is not null
                && nowTimestamp < _nextSelectionRefreshTimestamp
                && _selectedInterface.OperationalStatus == OperationalStatus.Up)
            {
                return _selectedInterface;
            }

            _selectedInterface = SelectInterfaceCore();
            _selectedAddresses = ReadInterfaceAddresses(_selectedInterface);
            _nextSelectionRefreshTimestamp = nowTimestamp + SelectionRefreshTicks;
            return _selectedInterface;
        }
    }

    private static HashSet<IPAddress> ReadInterfaceAddresses(NetworkInterface? item)
    {
        var addresses = new HashSet<IPAddress>();
        if (item is null)
        {
            return addresses;
        }

        try
        {
            foreach (var unicast in item.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;
                if (!address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any))
                {
                    addresses.Add(address);
                }
            }
        }
        catch
        {
        }

        return addresses;
    }

    private NetworkInterface? SelectInterfaceCore()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(IsUsable)
            .ToList();

        if (!string.IsNullOrWhiteSpace(_preferredInterfaceId))
        {
            var preferred = interfaces.FirstOrDefault(item =>
                item.Id.Equals(_preferredInterfaceId, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
            {
                return preferred;
            }
        }

        var bestIndex = GetDefaultIpv4InterfaceIndex();
        var best = bestIndex > 0
            ? interfaces.FirstOrDefault(item => GetIpv4Index(item) == bestIndex)
            : null;

        var physical = interfaces
            .Where(HasDefaultGateway)
            .Where(IsLikelyPhysical)
            .Where(item => !IsObviouslyVirtual(item))
            .OrderByDescending(item => ReferenceEquals(item, best))
            .ThenByDescending(item => item.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .ThenByDescending(item => item.Speed)
            .FirstOrDefault();
        if (physical is not null)
        {
            return physical;
        }

        if (bestIndex > 0)
        {
            if (best is not null)
            {
                return best;
            }
        }

        return interfaces
            .OrderByDescending(HasDefaultGateway)
            .ThenByDescending(IsLikelyPhysical)
            .ThenByDescending(item => item.Speed)
            .FirstOrDefault();
    }

    private static bool IsUsable(NetworkInterface item)
    {
        return item.OperationalStatus == OperationalStatus.Up
            && item.NetworkInterfaceType is not NetworkInterfaceType.Loopback
            && item.NetworkInterfaceType is not NetworkInterfaceType.Unknown;
    }

    private static bool HasDefaultGateway(NetworkInterface item)
    {
        try
        {
            return item.GetIPProperties().GatewayAddresses.Any(gateway =>
                !gateway.Address.Equals(IPAddress.Any)
                && !gateway.Address.Equals(IPAddress.IPv6Any));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLikelyPhysical(NetworkInterface item)
    {
        return item.NetworkInterfaceType is NetworkInterfaceType.Wireless80211
            or NetworkInterfaceType.Ethernet
            or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetFx
            or NetworkInterfaceType.FastEthernetT;
    }

    private static bool IsObviouslyVirtual(NetworkInterface item)
    {
        var identity = $"{item.Name} {item.Description}";
        string[] markers =
        [
            "virtual",
            "vmware",
            "hyper-v",
            "vethernet",
            "wsl",
            "loopback",
            "wireguard",
            "openvpn",
            "tap-",
            "tun "
        ];
        return markers.Any(marker => identity.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static int GetIpv4Index(NetworkInterface item)
    {
        try
        {
            return item.GetIPProperties().GetIPv4Properties()?.Index ?? -1;
        }
        catch
        {
            return -1;
        }
    }

    private static int GetDefaultIpv4InterfaceIndex()
    {
        try
        {
            var destination = BitConverter.ToUInt32(IPAddress.Parse("1.1.1.1").GetAddressBytes(), 0);
            return GetBestInterface(destination, out var index) == 0 ? (int)index : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static bool TryRead(NetworkInterface item, out AdapterReading reading)
    {
        try
        {
            var stats = item.GetIPStatistics();
            reading = new AdapterReading(ToUnsigned(stats.BytesReceived), ToUnsigned(stats.BytesSent), 0);
            return true;
        }
        catch
        {
            reading = default;
            return false;
        }
    }

    private static ulong ToUnsigned(long value) => value > 0 ? (ulong)value : 0;

    internal static ulong SubtractCounter(ulong current, ulong previous)
    {
        return current >= previous ? current - previous : 0;
    }

    internal static ulong ToPerSecond(ulong bytes, double elapsedSeconds)
    {
        if (elapsedSeconds <= 0)
        {
            return 0;
        }

        return (ulong)Math.Round(bytes / elapsedSeconds, MidpointRounding.AwayFromZero);
    }

    private static ulong AddSaturating(ulong left, ulong right)
    {
        return ulong.MaxValue - left < right ? ulong.MaxValue : left + right;
    }

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern uint GetBestInterface(uint destinationAddress, out uint bestInterfaceIndex);

    private readonly record struct AdapterReading(ulong Received, ulong Sent, long Timestamp);
}

public sealed record NetworkInterfaceOption(string Id, string Name, string Description)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Description) || Name.Equals(Description, StringComparison.CurrentCultureIgnoreCase)
        ? Name
        : $"{Name} - {Description}";
}
