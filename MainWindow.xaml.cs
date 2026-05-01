using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WinForms = System.Windows.Forms;

namespace FlowLens;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<TrafficRow> _rows = [];
    private readonly Dictionary<string, TrafficRow> _rowMap = [];
    private readonly Dictionary<string, TrafficSnapshot> _liveSnapshots = [];
    private readonly Dictionary<string, TrafficSnapshot> _previousRawSnapshots = [];
    private readonly AppSettings _settings;
    private readonly TrafficHistoryStore _historyStore;
    private readonly EtwTrafficMonitor _monitor = new();
    private readonly ICollectionView _view;
    private readonly WinForms.NotifyIcon _trayIcon;
    private bool _isExiting;
    private DateTime _nextStatsSave = DateTime.MinValue;
    private string _sortMember = nameof(TrafficRow.TotalRate);
    private ListSortDirection _sortDirection = ListSortDirection.Descending;

    public MainWindow()
    {
        _settings = AppSettings.Load();
        InitializeComponent();
        ApplyDisplaySettings();
        ThemeManager.Apply(this, _settings);
        _historyStore = TrafficHistoryStore.Load();
        _monitor.SnapshotIntervalSeconds = _settings.RefreshIntervalSeconds;
        _monitor.ExcludeLocalTraffic = _settings.ExcludeLocalTraffic;

        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = FilterRows;
        _view.SortDescriptions.Add(new SortDescription(nameof(TrafficRow.TotalRate), ListSortDirection.Descending));
        TrafficGrid.ItemsSource = _view;

        _monitor.SnapshotReady += Monitor_SnapshotReady;
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        _trayIcon = CreateTrayIcon();

        ApplyLocalization();
        ApplyColumnVisibility();
        ApplySort();
        RebuildDisplayedRows();
    }

    private string L(string key) => Localizer.T(_settings.Language, key);

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        StatusText.Text = IsAdministrator() ? L("Collecting") : L("NotAdmin");
        _monitor.Start();

        if (_settings.StartMinimized || Environment.GetCommandLineArgs().Any(arg => arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase)))
        {
            HideToTray();
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isExiting && _settings.CloseToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        SaveHistoryIfNeeded(force: true);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _monitor.Dispose();
        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _settings.CloseToTray)
        {
            HideToTray();
        }
    }

    private void Monitor_SnapshotReady(object? sender, MonitorSnapshotEventArgs e)
    {
        Dispatcher.Invoke(() => ApplySnapshot(e));
    }

    private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_settings.Theme != AppTheme.System)
        {
            return;
        }

        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
        {
            Dispatcher.BeginInvoke(() => ThemeManager.Apply(this, _settings));
        }
    }

    private void ApplySnapshot(MonitorSnapshotEventArgs e)
    {
        foreach (var snapshot in e.Snapshots)
        {
            var key = RawKeyFor(snapshot);

            if (_settings.PersistStats)
            {
                _previousRawSnapshots.TryGetValue(key, out var previous);
                var delta = TrafficCounters.Delta(snapshot, previous);
                if (TrafficHistoryStore.IsPersistable(snapshot.ProcessName, snapshot.Path))
                {
                    _historyStore.AddDelta(snapshot.ProcessName, snapshot.Path, delta, snapshot.LastSeen);
                }
            }

            _previousRawSnapshots[key] = snapshot;
            _liveSnapshots[key] = snapshot;
        }

        RebuildDisplayedRows();
        UpdateMetrics(e);
        SaveHistoryIfNeeded(force: false);
    }

    private void RebuildDisplayedRows()
    {
        var snapshots = _settings.TimeRange == TrafficTimeRange.Session
            ? _liveSnapshots.Values.ToList()
            : _historyStore.BuildSnapshots(_settings.TimeRange, _liveSnapshots.Values);
        var liveKeys = new HashSet<string>();

        foreach (var snapshot in snapshots)
        {
            var key = DisplayKeyFor(snapshot);
            liveKeys.Add(key);

            if (_rowMap.TryGetValue(key, out var row))
            {
                row.Update(snapshot);
            }
            else
            {
                row = new TrafficRow(snapshot);
                _rowMap[key] = row;
                _rows.Add(row);
            }
        }

        foreach (var staleKey in _rowMap.Keys.Where(key => !liveKeys.Contains(key)).ToList())
        {
            var row = _rowMap[staleKey];
            _rowMap.Remove(staleKey);
            _rows.Remove(row);
        }

        _view.Refresh();
    }

    private void UpdateMetrics(MonitorSnapshotEventArgs e)
    {
        ulong totalRate = 0;
        ulong ipv4Rate = 0;
        ulong ipv6Rate = 0;
        ulong ipv4Bytes = 0;
        ulong ipv6Bytes = 0;
        var flows = 0;

        foreach (var row in _rows.Where(row => FilterRows(row)))
        {
            totalRate += row.TotalRate;
            ipv4Rate += row.Ipv4ReceiveRate + row.Ipv4SendRate;
            ipv6Rate += row.Ipv6ReceiveRate + row.Ipv6SendRate;
            ipv4Bytes += row.Ipv4Received + row.Ipv4Sent;
            ipv6Bytes += row.Ipv6Received + row.Ipv6Sent;
            flows += row.Connections;
        }

        TotalRateText.Text = TrafficRow.FormatRate(totalRate);
        TotalBytesText.Text = TrafficRow.FormatBytes(ipv4Bytes + ipv6Bytes);
        Ipv4RateText.Text = TrafficRow.FormatRate(ipv4Rate);
        Ipv4BytesText.Text = TrafficRow.FormatBytes(ipv4Bytes);
        Ipv6RateText.Text = TrafficRow.FormatRate(ipv6Rate);
        Ipv6BytesText.Text = TrafficRow.FormatBytes(ipv6Bytes);
        ProcessCountText.Text = $"{_view.Cast<object>().Count()} / {flows}";

        StatusText.Text = e.ErrorCount == 0
            ? $"{L("Collecting")} - {DateTime.Now:HH:mm:ss}"
            : $"{L("CaptureWarning")}: {e.ErrorText} - {DateTime.Now:HH:mm:ss}";
    }

    private bool FilterRows(object item)
    {
        if (item is not TrafficRow row)
        {
            return false;
        }

        if (_settings.HideIdleRows && row.TotalReceived + row.TotalSent < _settings.MinimumVisibleBytes)
        {
            return false;
        }

        var text = SearchBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        return row.ProcessName.Contains(text, StringComparison.CurrentCultureIgnoreCase)
            || row.Pid.ToString().Contains(text, StringComparison.Ordinal)
            || row.Path.Contains(text, StringComparison.CurrentCultureIgnoreCase);
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _view.Refresh();
        UpdateMetrics(new MonitorSnapshotEventArgs([], 0, string.Empty));
    }

    private void TimeRangeBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TimeRangeBox.SelectedItem is not TimeRangeItem item)
        {
            return;
        }

        _settings.TimeRange = item.Range;
        _settings.Save();
        RebuildDisplayedRows();
        UpdateMetrics(new MonitorSnapshotEventArgs([], 0, string.Empty));
    }

    private void ResetAllStats()
    {
        _monitor.Reset();
        _rows.Clear();
        _rowMap.Clear();
        _liveSnapshots.Clear();
        _previousRawSnapshots.Clear();
        _historyStore.Clear();
        TrafficStatsStore.Clear();
        TotalRateText.Text = TrafficRow.FormatRate(0);
        TotalBytesText.Text = "0 B";
        Ipv4RateText.Text = TrafficRow.FormatRate(0);
        Ipv4BytesText.Text = "0 B";
        Ipv6RateText.Text = TrafficRow.FormatRate(0);
        Ipv6BytesText.Text = "0 B";
        ProcessCountText.Text = "0 / 0";
        StatusText.Text = L("ResetComplete");
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSettings();
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        OpenAbout();
    }

    private void OpenSettings()
    {
        var window = new SettingsWindow(_settings, ResetAllStats) { Owner = this };
        if (window.ShowDialog() == true)
        {
            _monitor.SnapshotIntervalSeconds = _settings.RefreshIntervalSeconds;
            _monitor.ExcludeLocalTraffic = _settings.ExcludeLocalTraffic;
            ApplyDisplaySettings();
            ThemeManager.Apply(this, _settings);
            ApplyLocalization();
            ApplyColumnVisibility();
            foreach (var row in _rows)
            {
                row.RefreshDisplay();
            }
            RebuildDisplayedRows();
            SaveHistoryIfNeeded(force: true);
        }
    }

    private void OpenAbout()
    {
        var window = new AboutWindow(_settings) { Owner = this };
        window.ShowDialog();
    }

    private void ApplyDisplaySettings()
    {
        Topmost = _settings.AlwaysOnTop;
        TrafficRow.UseBitsPerSecond = _settings.UseBitsPerSecond;
    }

    private void ApplyLocalization()
    {
        SubtitleText.Text = L("AppSubtitle");
        SearchBox.ToolTip = L("SearchPlaceholder");
        SettingsButton.Content = L("Settings");
        AboutButton.Content = L("About");
        TotalLabelText.Text = L("TotalCurrent");
        ProcessesLabelText.Text = L("ProcessesFlows");
        StatusText.Text = L("Ready");
        TrayHintText.Text = L("TrayHint");

        ApplySortHeaders();

        TimeRangeBox.ItemsSource = new[]
        {
            new TimeRangeItem(TrafficTimeRange.Session, L("RangeSession")),
            new TimeRangeItem(TrafficTimeRange.Today, L("RangeToday")),
            new TimeRangeItem(TrafficTimeRange.Last7Days, L("Range7Days")),
            new TimeRangeItem(TrafficTimeRange.Last30Days, L("Range30Days")),
            new TimeRangeItem(TrafficTimeRange.All, L("RangeAll"))
        };
        TimeRangeBox.SelectedItem = TimeRangeBox.Items.Cast<TimeRangeItem>().First(item => item.Range == _settings.TimeRange);

        RefreshTrayMenuText();
    }

    private void SaveHistoryIfNeeded(bool force)
    {
        if (!_settings.PersistStats)
        {
            return;
        }

        if (!force && DateTime.Now < _nextStatsSave)
        {
            return;
        }

        _historyStore.Save();
        _nextStatsSave = DateTime.Now.AddSeconds(10);
    }

    private WinForms.NotifyIcon CreateTrayIcon()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => ShowFromTray());
        menu.Items.Add("Settings", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        menu.Items.Add("About", null, (_, _) => Dispatcher.Invoke(OpenAbout));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitApplication));

        var icon = new WinForms.NotifyIcon
        {
            Text = "FlowLens",
            Icon = LoadTrayIcon(),
            ContextMenuStrip = menu,
            Visible = true
        };

        icon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        return icon;
    }

    private void RefreshTrayMenuText()
    {
        if (_trayIcon.ContextMenuStrip is null || _trayIcon.ContextMenuStrip.Items.Count < 5)
        {
            return;
        }

        _trayIcon.ContextMenuStrip.Items[0].Text = L("Show");
        _trayIcon.ContextMenuStrip.Items[1].Text = L("Settings");
        _trayIcon.ContextMenuStrip.Items[2].Text = L("About");
        _trayIcon.ContextMenuStrip.Items[4].Text = L("Exit");
    }

    private void TrafficGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var member = e.Column.SortMemberPath;
        if (string.IsNullOrWhiteSpace(member))
        {
            return;
        }

        if (_sortMember == member)
        {
            _sortDirection = _sortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        }
        else
        {
            _sortMember = member;
            _sortDirection = ListSortDirection.Descending;
        }

        ApplySort();
    }

    private void ApplySort()
    {
        _view.SortDescriptions.Clear();
        _view.SortDescriptions.Add(new SortDescription(_sortMember, _sortDirection));
        ApplySortHeaders();
        _view.Refresh();
    }

    private void ApplySortHeaders()
    {
        foreach (var column in TrafficGrid.Columns)
        {
            var arrow = column.SortMemberPath == _sortMember
                ? (_sortDirection == ListSortDirection.Ascending ? " ^" : " v")
                : string.Empty;
            column.Header = HeaderFor(column) + arrow;
        }
    }

    private string HeaderFor(DataGridColumn column)
    {
        if (column == ProcessColumn) return L("Process");
        if (column == PidColumn) return "PID";
        if (column == RateColumn) return L("Rate");
        if (column == Ipv4RateColumn) return L("Ipv4Rate");
        if (column == Ipv6RateColumn) return L("Ipv6Rate");
        if (column == ReceivedColumn) return L("Received");
        if (column == SentColumn) return L("Sent");
        if (column == Ipv4Column) return L("Ipv4Rs");
        if (column == Ipv6Column) return L("Ipv6Rs");
        if (column == TcpColumn) return L("TcpRs");
        if (column == UdpColumn) return L("UdpRs");
        if (column == FlowsColumn) return L("Flows");
        if (column == PathColumn) return L("Path");
        return column.Header?.ToString() ?? string.Empty;
    }

    private void ApplyColumnVisibility()
    {
        PidColumn.Visibility = _settings.ShowPidColumn ? Visibility.Visible : Visibility.Collapsed;
        RateColumn.Visibility = _settings.ShowRateColumns ? Visibility.Visible : Visibility.Collapsed;
        Ipv4RateColumn.Visibility = _settings.ShowRateColumns ? Visibility.Visible : Visibility.Collapsed;
        Ipv6RateColumn.Visibility = _settings.ShowRateColumns ? Visibility.Visible : Visibility.Collapsed;
        ReceivedColumn.Visibility = _settings.ShowTotalColumns ? Visibility.Visible : Visibility.Collapsed;
        SentColumn.Visibility = _settings.ShowTotalColumns ? Visibility.Visible : Visibility.Collapsed;
        Ipv4Column.Visibility = _settings.ShowIpSplitColumns ? Visibility.Visible : Visibility.Collapsed;
        Ipv6Column.Visibility = _settings.ShowIpSplitColumns ? Visibility.Visible : Visibility.Collapsed;
        TcpColumn.Visibility = _settings.ShowProtocolColumns ? Visibility.Visible : Visibility.Collapsed;
        UdpColumn.Visibility = _settings.ShowProtocolColumns ? Visibility.Visible : Visibility.Collapsed;
        FlowsColumn.Visibility = _settings.ShowFlowsColumn ? Visibility.Visible : Visibility.Collapsed;
        PathColumn.Visibility = _settings.ShowPathColumn ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    public void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _isExiting = true;
        Close();
    }

    private string DisplayKeyFor(TrafficSnapshot snapshot)
    {
        return _settings.TimeRange == TrafficTimeRange.Session
            ? RawKeyFor(snapshot)
            : StableKeyFor(snapshot);
    }

    private static string RawKeyFor(TrafficSnapshot snapshot)
    {
        return $"{snapshot.Pid}|{StableKeyFor(snapshot)}";
    }

    private static string StableKeyFor(TrafficSnapshot snapshot)
    {
        return TrafficStatsStore.KeyFor(snapshot.ProcessName, snapshot.Path);
    }

    private static Icon LoadTrayIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/FlowLens.ico"));
        return resource is not null ? new Icon(resource.Stream) : SystemIcons.Application;
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
