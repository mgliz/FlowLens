using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FlowLens;

public sealed class TrafficRow : INotifyPropertyChanged
{
    private TrafficSnapshot _snapshot;

    public static bool UseBitsPerSecond { get; set; }

    public TrafficRow(TrafficSnapshot snapshot)
    {
        _snapshot = snapshot;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Pid => _snapshot.Pid;
    public string ProcessName => _snapshot.ProcessName;
    public string Path => _snapshot.Path;
    public ulong Ipv4Received => _snapshot.Ipv4Received;
    public ulong Ipv4Sent => _snapshot.Ipv4Sent;
    public ulong Ipv6Received => _snapshot.Ipv6Received;
    public ulong Ipv6Sent => _snapshot.Ipv6Sent;
    public ulong TcpReceived => _snapshot.TcpReceived;
    public ulong TcpSent => _snapshot.TcpSent;
    public ulong UdpReceived => _snapshot.UdpReceived;
    public ulong UdpSent => _snapshot.UdpSent;
    public ulong TotalReceived => Ipv4Received + Ipv6Received;
    public ulong TotalSent => Ipv4Sent + Ipv6Sent;
    public ulong Ipv4ReceiveRate => _snapshot.Ipv4ReceiveRate;
    public ulong Ipv4SendRate => _snapshot.Ipv4SendRate;
    public ulong Ipv6ReceiveRate => _snapshot.Ipv6ReceiveRate;
    public ulong Ipv6SendRate => _snapshot.Ipv6SendRate;
    public ulong TotalRate => Ipv4ReceiveRate + Ipv4SendRate + Ipv6ReceiveRate + Ipv6SendRate;
    public int Ipv4Connections => _snapshot.Ipv4Connections;
    public int Ipv6Connections => _snapshot.Ipv6Connections;
    public int Connections => Ipv4Connections + Ipv6Connections;
    public DateTime LastSeen => _snapshot.LastSeen;

    public string TotalRateText => FormatRate(TotalRate);
    public string Ipv4RateText => $"{FormatRate(Ipv4ReceiveRate)} ↓  {FormatRate(Ipv4SendRate)} ↑";
    public string Ipv6RateText => $"{FormatRate(Ipv6ReceiveRate)} ↓  {FormatRate(Ipv6SendRate)} ↑";
    public string TotalReceivedText => FormatBytes(TotalReceived);
    public string TotalSentText => FormatBytes(TotalSent);
    public string Ipv4ReceivedText => FormatBytes(Ipv4Received);
    public string Ipv4SentText => FormatBytes(Ipv4Sent);
    public string Ipv6ReceivedText => FormatBytes(Ipv6Received);
    public string Ipv6SentText => FormatBytes(Ipv6Sent);
    public string Ipv4TotalText => $"{FormatBytes(Ipv4Received)} ↓  {FormatBytes(Ipv4Sent)} ↑";
    public string Ipv6TotalText => $"{FormatBytes(Ipv6Received)} ↓  {FormatBytes(Ipv6Sent)} ↑";
    public string TcpTotalText => $"{FormatBytes(TcpReceived)} ↓  {FormatBytes(TcpSent)} ↑";
    public string UdpTotalText => $"{FormatBytes(UdpReceived)} ↓  {FormatBytes(UdpSent)} ↑";

    public void Update(TrafficSnapshot snapshot)
    {
        _snapshot = snapshot;
        OnPropertyChanged(string.Empty);
    }

    public void RefreshDisplay()
    {
        OnPropertyChanged(string.Empty);
    }

    public static string FormatRate(ulong bytesPerSecond)
    {
        if (!UseBitsPerSecond)
        {
            return $"{FormatBytes(bytesPerSecond)}/s";
        }

        string[] units = ["bit", "Kbit", "Mbit", "Gbit", "Tbit"];
        double value = bytesPerSecond * 8d;
        var unit = 0;

        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}/s" : $"{value:0.0} {units[unit]}/s";
    }

    public static string FormatBytes(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
