using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using WinForms = System.Windows.Forms;

namespace FlowLens;

public partial class MainWindow : Window
{
    private static readonly TimeSpan StaleSnapshotAge = TimeSpan.FromMinutes(10);
    private readonly object _snapshotDispatchGate = new();
    private readonly ObservableCollection<TrafficRow> _rows = [];
    private readonly Dictionary<string, TrafficRow> _rowMap = [];
    private readonly Dictionary<string, TrafficSnapshot> _liveSnapshots = [];
    private readonly TrafficHistoryAccumulator _historyAccumulator;
    private readonly AppSettings _settings;
    private readonly TrafficHistoryStore _historyStore;
    private readonly NetworkTrafficHistoryStore _networkHistoryStore;
    private readonly HistorySaveCoordinator _historySaver;
    private readonly EtwTrafficMonitor _monitor = new();
    private readonly ICollectionView _view;
    private readonly WinForms.NotifyIcon _trayIcon;
    private bool _isExiting;
    private bool _updatingTimeRange;
    private bool _customDateValidationFailed;
    private long _nextStatsSaveTimestamp;
    private NetworkTrafficSnapshot _latestNetworkSnapshot = NetworkTrafficSnapshot.Unavailable;
    private int _lastCaptureErrorCount;
    private string _lastCaptureErrorText = string.Empty;
    private int _lastLostEventCount;
    private long _lastAppliedGeneration = -1;
    private bool _networkSampleUnavailable;
    private string _lastPersistenceErrorText = string.Empty;
    private string _startupRegistrationErrorText = string.Empty;
    private bool _startupRepairInProgress;
    private string _sortMember = nameof(TrafficRow.TotalRate);
    private ListSortDirection _sortDirection = ListSortDirection.Descending;
    private MonitorSnapshotEventArgs? _pendingUiSnapshot;
    private bool _snapshotDispatchScheduled;
    private volatile bool _persistenceEnabled;

    public MainWindow()
    {
        _settings = AppSettings.Load();
        _persistenceEnabled = _settings.PersistStats;
        InitializeComponent();
        ApplyDisplaySettings();
        ThemeManager.Apply(this, _settings);
        _historyStore = TrafficHistoryStore.Load();
        _networkHistoryStore = NetworkTrafficHistoryStore.Load();
        _historyAccumulator = new TrafficHistoryAccumulator(_historyStore, _networkHistoryStore);
        _historySaver = new HistorySaveCoordinator(_historyStore, _networkHistoryStore);
        _monitor.SnapshotIntervalSeconds = _settings.RefreshIntervalSeconds;
        _monitor.ExcludeLocalTraffic = _settings.ExcludeLocalTraffic;
        _monitor.NetworkInterfaceId = _settings.NetworkInterfaceId;
        _monitor.TrackFlows = _settings.ShowFlowsColumn;

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
        ApplyResponsiveLayout(Width);
    }

    private string L(string key) => Localizer.T(_settings.Language, key);

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SetStatus(
            IsAdministrator() ? L("Collecting") : L("NotAdmin"),
            IsAdministrator() ? StatusSeverity.Success : StatusSeverity.Warning);
        _monitor.Start();
        _ = RepairStartupRegistrationAsync();

        if (_settings.StartMinimized || Environment.GetCommandLineArgs().Any(arg => arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase)))
        {
            HideToTray();
        }
    }

    private async Task RepairStartupRegistrationAsync()
    {
        if (_startupRepairInProgress)
        {
            return;
        }

        if (!_settings.StartWithWindows)
        {
            _startupRegistrationErrorText = string.Empty;
            return;
        }

        _startupRepairInProgress = true;
        try
        {
            var needsRefresh = await Task.Run(_settings.StartupRegistrationNeedsRefresh);
            if (!needsRefresh)
            {
                _startupRegistrationErrorText = string.Empty;
                return;
            }

            var result = await Task.Run(_settings.ApplyStartupRegistration);
            if (result.Succeeded)
            {
                _startupRegistrationErrorText = string.Empty;
                _settings.Save();
            }
            else
            {
                _startupRegistrationErrorText = result.ErrorMessage;
                UpdateMetrics();
            }
        }
        catch (Exception ex)
        {
            _startupRegistrationErrorText = ex.Message;
            UpdateMetrics();
        }
        finally
        {
            _startupRepairInProgress = false;
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

        // Stop publishes the last collected interval. Never wait for the monitor
        // while holding _snapshotDispatchGate: its callbacks acquire that lock.
        _isExiting = true;
        _monitor.Stop();
        SaveHistoryIfNeeded(force: true);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _monitor.SnapshotReady -= Monitor_SnapshotReady;
        _monitor.Dispose();
        _historySaver.Dispose();
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

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        ApplyResponsiveLayout(e.NewSize.Width);
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.OemComma)
        {
            OpenSettings();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F1)
        {
            OpenAbout();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && !string.IsNullOrEmpty(SearchBox.Text))
        {
            SearchBox.Clear();
            e.Handled = true;
        }
    }

    private void ApplyResponsiveLayout(double width)
    {
        var compact = width < 1060;
        Grid.SetRow(ToolbarPanel, compact ? 1 : 0);
        Grid.SetColumn(ToolbarPanel, compact ? 0 : 1);
        Grid.SetColumnSpan(ToolbarPanel, compact ? 2 : 1);
        ToolbarPanel.HorizontalAlignment = compact
            ? System.Windows.HorizontalAlignment.Left
            : System.Windows.HorizontalAlignment.Right;
        ToolbarPanel.Margin = compact ? new Thickness(0, 12, 0, 0) : new Thickness(0);
        TrayHintText.Visibility = width < 1120 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Monitor_SnapshotReady(object? sender, MonitorSnapshotEventArgs e)
    {
        var schedule = false;
        lock (_snapshotDispatchGate)
        {
            if (e.Generation != _monitor.Generation)
            {
                return;
            }

            // Accounting and reset share this boundary. Only rendering may skip snapshots.
            _historyAccumulator.Observe(e, _persistenceEnabled);
            SaveHistoryIfNeeded(force: false);
            _pendingUiSnapshot = e;
            if (!_snapshotDispatchScheduled)
            {
                _snapshotDispatchScheduled = true;
                schedule = true;
            }
        }

        if (schedule)
        {
            ScheduleSnapshotDispatch();
        }
    }

    private void ScheduleSnapshotDispatch()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            lock (_snapshotDispatchGate)
            {
                _pendingUiSnapshot = null;
                _snapshotDispatchScheduled = false;
            }
            return;
        }

        try
        {
            Dispatcher.BeginInvoke(new Action(DrainPendingSnapshot));
        }
        catch (InvalidOperationException)
        {
            lock (_snapshotDispatchGate)
            {
                _pendingUiSnapshot = null;
                _snapshotDispatchScheduled = false;
            }
        }
    }

    private void DrainPendingSnapshot()
    {
        MonitorSnapshotEventArgs? snapshot;
        lock (_snapshotDispatchGate)
        {
            snapshot = _pendingUiSnapshot;
            _pendingUiSnapshot = null;
        }

        if (snapshot is not null)
        {
            ApplySnapshot(snapshot);
        }

        lock (_snapshotDispatchGate)
        {
            if (_pendingUiSnapshot is null)
            {
                _snapshotDispatchScheduled = false;
                return;
            }
        }

        ScheduleSnapshotDispatch();
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
        if (e.Generation != _monitor.Generation)
        {
            return;
        }

        if (_lastAppliedGeneration >= 0 && _lastAppliedGeneration != e.Generation)
        {
            _rows.Clear();
            _rowMap.Clear();
            _liveSnapshots.Clear();
        }

        _lastAppliedGeneration = e.Generation;

        var now = DateTime.Now;
        _lastCaptureErrorCount = e.ErrorCount;
        _lastCaptureErrorText = e.ErrorText;
        _lastLostEventCount = e.LostEventCount;
        _networkSampleUnavailable = !e.Network.IsAvailable || !e.Network.IsAttributionAvailable;
        if (e.Network.IsAvailable)
        {
            _latestNetworkSnapshot = e.Network;
        }
        else if (_latestNetworkSnapshot.IsAvailable)
        {
            _latestNetworkSnapshot = _latestNetworkSnapshot with
            {
                ReceiveRate = 0,
                SendRate = 0,
                ReceivedDelta = 0,
                SentDelta = 0
            };
        }

        foreach (var snapshot in e.Snapshots)
        {
            var key = RawKeyFor(snapshot);
            _liveSnapshots[key] = snapshot;
        }

        PruneLiveSnapshots(now);

        if (IsUiRefreshSuppressed())
        {
            return;
        }

        RebuildDisplayedRows();
        UpdateMetrics();
    }

    private void PruneLiveSnapshots(DateTime now)
    {
        foreach (var staleKey in _liveSnapshots
                     .Where(pair => now - pair.Value.LastSeen > StaleSnapshotAge)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _liveSnapshots.Remove(staleKey);
        }
    }

    private bool IsUiRefreshSuppressed()
    {
        return !IsVisible || WindowState == WindowState.Minimized;
    }

    private void RebuildDisplayedRows()
    {
        var snapshots = _settings.TimeRange == TrafficTimeRange.Session
            ? _liveSnapshots.Values.ToList()
            : _historyStore.BuildSnapshots(
                _settings.TimeRange,
                _liveSnapshots.Values,
                _latestNetworkSnapshot.InterfaceId, DateTime.Today,
                _settings.CustomRangeStart, _settings.CustomRangeEnd);
        var liveKeys = new HashSet<string>();

        foreach (var snapshot in snapshots)
        {
            var key = DisplayKeyFor(snapshot);
            var displaySnapshot = TrafficHistoryStore.IsUnattributedPath(snapshot.Path)
                ? snapshot with { ProcessName = L("Unattributed") }
                : snapshot;
            liveKeys.Add(key);

            if (_rowMap.TryGetValue(key, out var row))
            {
                row.Update(displaySnapshot);
            }
            else
            {
                row = new TrafficRow(displaySnapshot);
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

    private void UpdateMetrics()
    {
        _lastPersistenceErrorText = _historySaver.LastError;
        ulong logicalTotalRate = 0;
        ulong ipv4Rate = 0;
        ulong ipv6Rate = 0;
        ulong ipv4Bytes = 0;
        ulong ipv6Bytes = 0;
        var flows = 0;

        foreach (var row in _rows)
        {
            logicalTotalRate = AddSaturating(logicalTotalRate, row.TotalRate);
            ipv4Rate = AddSaturating(ipv4Rate, AddSaturating(row.Ipv4ReceiveRate, row.Ipv4SendRate));
            ipv6Rate = AddSaturating(ipv6Rate, AddSaturating(row.Ipv6ReceiveRate, row.Ipv6SendRate));
            ipv4Bytes = AddSaturating(ipv4Bytes, AddSaturating(row.Ipv4Received, row.Ipv4Sent));
            ipv6Bytes = AddSaturating(ipv6Bytes, AddSaturating(row.Ipv6Received, row.Ipv6Sent));
            flows += row.Connections;
        }

        var logicalTotalBytes = AddSaturating(ipv4Bytes, ipv6Bytes);
        TotalLabelText.Text = L("LogicalTotalCurrent");
        TotalRateText.Text = TrafficRow.FormatRate(logicalTotalRate);
        TotalBytesText.Text = TrafficRow.FormatBytes(logicalTotalBytes);

        NetworkRateText.Text = TrafficRow.FormatRate(0);
        NetworkReceiveRateText.Text = TrafficRow.FormatRate(0);
        NetworkSendRateText.Text = TrafficRow.FormatRate(0);
        NetworkBytesText.Text = TrafficRow.FormatBytes(0);
        NetworkAdapterText.Text = L("AdapterUnavailable");

        if (_latestNetworkSnapshot.IsAvailable)
        {
            var physicalTotals = _networkHistoryStore.GetTotals(_settings.TimeRange, _latestNetworkSnapshot,
                DateTime.Today, _settings.CustomRangeStart, _settings.CustomRangeEnd);
            NetworkRateText.Text = TrafficRow.FormatRate(
                AddSaturating(_latestNetworkSnapshot.ReceiveRate, _latestNetworkSnapshot.SendRate));
            NetworkReceiveRateText.Text = TrafficRow.FormatRate(_latestNetworkSnapshot.ReceiveRate);
            NetworkSendRateText.Text = TrafficRow.FormatRate(_latestNetworkSnapshot.SendRate);
            NetworkBytesText.Text = TrafficRow.FormatBytes(AddSaturating(physicalTotals.Received, physicalTotals.Sent));
            NetworkAdapterText.Text = _latestNetworkSnapshot.InterfaceName;
        }

        Ipv4RateText.Text = TrafficRow.FormatRate(ipv4Rate);
        Ipv4BytesText.Text = TrafficRow.FormatBytes(ipv4Bytes);
        Ipv6RateText.Text = TrafficRow.FormatRate(ipv6Rate);
        Ipv6BytesText.Text = TrafficRow.FormatBytes(ipv6Bytes);
        ProcessCountText.Text = $"{_rows.Count(row => !row.IsUnattributed)} / {flows}";

        if (_lastLostEventCount > 0)
        {
            SetStatus(
                $"{L("CaptureWarning")}: {L("LostEvents")} {_lastLostEventCount:N0} - {DateTime.Now:HH:mm:ss}",
                StatusSeverity.Warning);
        }
        else if (_lastCaptureErrorCount > 0)
        {
            SetStatus(
                $"{L("CaptureWarning")}: {_lastCaptureErrorText} - {DateTime.Now:HH:mm:ss}",
                StatusSeverity.Error);
        }
        else if (_networkSampleUnavailable)
        {
            SetStatus(
                $"{L("AdapterUnavailableWarning")} - {DateTime.Now:HH:mm:ss}",
                StatusSeverity.Warning);
        }
        else if (!string.IsNullOrWhiteSpace(_lastPersistenceErrorText))
        {
            SetStatus(
                $"{L("StorageWarning")}: {_lastPersistenceErrorText} - {DateTime.Now:HH:mm:ss}",
                StatusSeverity.Error);
        }
        else if (!string.IsNullOrWhiteSpace(_startupRegistrationErrorText))
        {
            SetStatus(
                $"{L("StartupRegistrationFailed")} {_startupRegistrationErrorText}",
                StatusSeverity.Warning);
        }
        else
        {
            SetStatus($"{L("Collecting")} - {DateTime.Now:HH:mm:ss}", StatusSeverity.Success);
        }
    }

    private void SetStatus(string message, StatusSeverity severity)
    {
        StatusText.Text = message;
        StatusIconText.Text = severity == StatusSeverity.Success ? "\uE73E" : "\uE7BA";
        StatusIconText.SetResourceReference(
            TextBlock.ForegroundProperty,
            severity switch
            {
                StatusSeverity.Success => "SuccessBrush",
                StatusSeverity.Warning => "WarningBrush",
                _ => "DangerBrush"
            });
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
        var hasSearch = !string.IsNullOrEmpty(SearchBox.Text);
        SearchPlaceholderText.Visibility = hasSearch ? Visibility.Collapsed : Visibility.Visible;
        SearchClearButton.Visibility = hasSearch ? Visibility.Visible : Visibility.Collapsed;
        _view.Refresh();
        UpdateMetrics();
    }

    private void SearchClearButton_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void TimeRangeBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingTimeRange)
        {
            return;
        }

        if (TimeRangeBox.SelectedItem is not TimeRangeItem item)
        {
            return;
        }

        _settings.TimeRange = item.Range;
        UpdateCustomRangeControls();
        _settings.Save();
        RebuildDisplayedRows();
        UpdateMetrics();
    }

    private void UpdateCustomRangeControls()
    {
        CustomRangePanel.Visibility = _settings.TimeRange == TrafficTimeRange.Custom
            ? Visibility.Visible : Visibility.Collapsed;
        var dateLanguage = System.Windows.Markup.XmlLanguage.GetLanguage(
            System.Globalization.CultureInfo.CurrentCulture.IetfLanguageTag);
        CustomStartPicker.Language = CustomEndPicker.Language = dateLanguage;
        CustomStartPicker.DisplayDateEnd = CustomEndPicker.DisplayDateEnd = DateTime.Today;
        CustomStartPicker.SelectedDate = _settings.CustomRangeStart;
        CustomEndPicker.SelectedDate = _settings.CustomRangeEnd;
        CustomRangeAppliedText.Text = string.Format(L("RangeApplied"),
            _settings.CustomRangeStart, _settings.CustomRangeEnd);
        CustomRangeError.Visibility = Visibility.Collapsed;
        _customDateValidationFailed = false;
    }

    private void CustomDate_ValidationError(object? sender, DatePickerDateValidationErrorEventArgs e)
    {
        e.ThrowException = false;
        _customDateValidationFailed = true;
        CustomRangeError.Text = L("RangeInvalid");
        CustomRangeError.Visibility = Visibility.Visible;
    }

    private void CustomRangeApply_Click(object sender, RoutedEventArgs e)
    {
        // DatePicker may restore its old text after rejecting a typed date.
        // Do not silently apply that restored value on the same click.
        if (_customDateValidationFailed)
        {
            _customDateValidationFailed = false;
            CustomRangeError.Text = L("RangeInvalid");
            CustomRangeError.Visibility = Visibility.Visible;
            return;
        }

        if (!DateTime.TryParse(CustomStartPicker.Text, out var start) ||
            !DateTime.TryParse(CustomEndPicker.Text, out var end) ||
            start.Date > end.Date || end.Date > DateTime.Today)
        {
            CustomRangeError.Text = L("RangeInvalid");
            CustomRangeError.Visibility = Visibility.Visible;
            return;
        }

        _settings.CustomRangeStart = start.Date;
        _settings.CustomRangeEnd = end.Date;
        _settings.Save();
        UpdateCustomRangeControls();
        RebuildDisplayedRows();
        UpdateMetrics();
    }

    private void ResetAllStats()
    {
        lock (_snapshotDispatchGate)
        {
            _historySaver.Flush();
            _monitor.Reset();
            _historyAccumulator.Reset(_monitor.Generation);
            _historyStore.Clear();
            _networkHistoryStore.Clear();
            _pendingUiSnapshot = null;
        }
        _rows.Clear();
        _rowMap.Clear();
        _liveSnapshots.Clear();
        TrafficStatsStore.Clear();
        _latestNetworkSnapshot = NetworkTrafficSnapshot.Unavailable;
        _lastLostEventCount = 0;
        _networkSampleUnavailable = false;
        _lastPersistenceErrorText = string.Empty;
        NetworkRateText.Text = TrafficRow.FormatRate(0);
        NetworkReceiveRateText.Text = TrafficRow.FormatRate(0);
        NetworkSendRateText.Text = TrafficRow.FormatRate(0);
        NetworkBytesText.Text = "0 B";
        NetworkAdapterText.Text = L("AdapterUnavailable");
        TotalRateText.Text = TrafficRow.FormatRate(0);
        TotalBytesText.Text = "0 B";
        Ipv4RateText.Text = TrafficRow.FormatRate(0);
        Ipv4BytesText.Text = "0 B";
        Ipv6RateText.Text = TrafficRow.FormatRate(0);
        Ipv6BytesText.Text = "0 B";
        ProcessCountText.Text = "0 / 0";
        SetStatus(L("ResetComplete"), StatusSeverity.Success);
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
            var wasPersisting = _persistenceEnabled;
            _persistenceEnabled = _settings.PersistStats;
            if (wasPersisting && !_persistenceEnabled)
            {
                SaveHistoryIfNeeded(force: true);
            }
            _monitor.SnapshotIntervalSeconds = _settings.RefreshIntervalSeconds;
            _monitor.ExcludeLocalTraffic = _settings.ExcludeLocalTraffic;
            _monitor.NetworkInterfaceId = _settings.NetworkInterfaceId;
            _monitor.TrackFlows = _settings.ShowFlowsColumn && !IsUiRefreshSuppressed();
            ApplyDisplaySettings();
            ThemeManager.Apply(this, _settings);
            ApplyLocalization();
            ApplyColumnVisibility();
            foreach (var row in _rows)
            {
                row.RefreshDisplay();
            }
            RebuildDisplayedRows();
            SaveHistoryIfNeeded(force: false);
            _ = RepairStartupRegistrationAsync();
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
        SearchPlaceholderText.Text = L("SearchPlaceholder");
        SearchBox.ToolTip = L("SearchPlaceholder");
        SearchClearButton.ToolTip = L("ClearSearch");
        SettingsButton.ToolTip = L("Settings");
        AboutButton.ToolTip = L("About");
        AutomationProperties.SetName(SearchBox, L("SearchPlaceholder"));
        AutomationProperties.SetName(SearchClearButton, L("ClearSearch"));
        AutomationProperties.SetName(SettingsButton, L("Settings"));
        AutomationProperties.SetName(AboutButton, L("About"));
        AutomationProperties.SetName(TimeRangeBox, L("TimeRange"));
        CustomStartLabel.Text = L("RangeStart");
        CustomEndLabel.Text = L("RangeEnd");
        CustomRangeApplyButton.Content = L("RangeApply");
        CustomRangeHint.Text = L("RangeHint");
        AutomationProperties.SetName(CustomStartPicker, L("RangeStart"));
        AutomationProperties.SetName(CustomEndPicker, L("RangeEnd"));
        UpdateCustomRangeControls();
        NetworkSectionTitleText.Text = L("PhysicalNetworkTraffic");
        AttributionSectionTitleText.Text = L("AttributedProcessTraffic");
        ProcessTableTitleText.Text = L("ProcessDetails");
        NetworkCurrentLabelText.Text = L("CurrentRate");
        NetworkReceiveLabelText.Text = L("ReceiveRate");
        NetworkSendLabelText.Text = L("SendRate");
        NetworkPeriodLabelText.Text = L("PeriodTotal");
        TotalLabelText.Text = L("LogicalTotalCurrent");
        Ipv4LabelText.Text = L("Ipv4Logical");
        Ipv6LabelText.Text = L("Ipv6Logical");
        ProcessesLabelText.Text = L("ProcessesFlows");
        StatusText.Text = L("Ready");
        TrayHintText.Text = L("TrayHint");

        ApplySortHeaders();

        _updatingTimeRange = true;
        try
        {
            TimeRangeBox.ItemsSource = new[]
            {
                new TimeRangeItem(TrafficTimeRange.Session, L("RangeSession")),
                new TimeRangeItem(TrafficTimeRange.Today, L("RangeToday")),
                new TimeRangeItem(TrafficTimeRange.ThisMonth, L("RangeThisMonth")),
                new TimeRangeItem(TrafficTimeRange.LastMonth, L("RangeLastMonth")),
                new TimeRangeItem(TrafficTimeRange.Last7Days, L("Range7Days")),
                new TimeRangeItem(TrafficTimeRange.Last30Days, L("Range30Days")),
                new TimeRangeItem(TrafficTimeRange.All, L("RangeAll")),
                new TimeRangeItem(TrafficTimeRange.Custom, L("RangeCustom"))
            };
            TimeRangeBox.SelectedItem = TimeRangeBox.Items.Cast<TimeRangeItem>().First(item => item.Range == _settings.TimeRange);
        }
        finally
        {
            _updatingTimeRange = false;
        }

        RefreshTrayMenuText();
        UpdateMetrics();
    }

    private void SaveHistoryIfNeeded(bool force)
    {
        lock (_snapshotDispatchGate)
        {
            if (!force && (!_persistenceEnabled || Stopwatch.GetTimestamp() < _nextStatsSaveTimestamp))
            {
                return;
            }

            if (force)
            {
                _historySaver.Flush();
            }
            else
            {
                _historySaver.RequestSave();
            }

            _nextStatsSaveTimestamp = Stopwatch.GetTimestamp() + (long)(TimeSpan.FromMinutes(1).TotalSeconds * Stopwatch.Frequency);
        }
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
        if (_sortMember != nameof(TrafficRow.ProcessName))
        {
            _view.SortDescriptions.Add(new SortDescription(nameof(TrafficRow.ProcessName), ListSortDirection.Ascending));
        }
        ApplySortHeaders();
        _view.Refresh();
    }

    private void ApplySortHeaders()
    {
        DataGridColumn? sortedColumn = null;
        foreach (var column in TrafficGrid.Columns)
        {
            column.Header = HeaderFor(column);
            column.SortDirection = column.SortMemberPath == _sortMember ? _sortDirection : null;
            if (column.SortDirection is not null)
            {
                sortedColumn = column;
            }
        }

        if (sortedColumn is not null)
        {
            var direction = _sortDirection == ListSortDirection.Ascending
                ? L("Ascending")
                : L("Descending");
            SortSummaryText.Text = $"{L("SortBy")}: {HeaderFor(sortedColumn)} · {direction}";
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

        var sortedColumn = TrafficGrid.Columns.FirstOrDefault(column => column.SortMemberPath == _sortMember);
        if (sortedColumn?.Visibility != Visibility.Visible)
        {
            _sortMember = nameof(TrafficRow.ProcessName);
            _sortDirection = ListSortDirection.Ascending;
            ApplySort();
        }
    }

    private void HideToTray()
    {
        _monitor.TrackFlows = false;
        Hide();
        ShowInTaskbar = false;
    }

    public void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        _monitor.TrackFlows = _settings.ShowFlowsColumn;
        RebuildDisplayedRows();
        UpdateMetrics();
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

    internal static string RawKeyFor(TrafficSnapshot snapshot)
    {
        return snapshot.ProcessInstanceId != 0
            ? $"{snapshot.Pid}:{snapshot.ProcessInstanceId}"
            : $"{snapshot.Pid}:legacy|{StableKeyFor(snapshot)}";
    }

    internal static bool ShouldPersistSnapshot(
        bool persistenceEnabled,
        bool captureActive,
        NetworkTrafficSnapshot network)
    {
        return persistenceEnabled
            && captureActive
            && network.IsAvailable
            && network.IsAttributionAvailable
            && !string.IsNullOrWhiteSpace(network.InterfaceId);
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

    private static ulong AddSaturating(ulong left, ulong right)
    {
        return ulong.MaxValue - left < right ? ulong.MaxValue : left + right;
    }

    private enum StatusSeverity
    {
        Success,
        Warning,
        Error
    }
}
