using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace FlowLens;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action _resetStats;
    private readonly AppTheme _originalTheme;
    private bool _settingsSaved;

    public SettingsWindow(AppSettings settings, Action resetStats)
    {
        _settings = settings;
        _resetStats = resetStats;
        _originalTheme = settings.Theme;
        InitializeComponent();
        ThemeManager.Apply(this, settings);

        StartWithWindowsBox.IsChecked = settings.StartWithWindows;
        StartMinimizedBox.IsChecked = settings.StartMinimized;
        CloseToTrayBox.IsChecked = settings.CloseToTray;
        AlwaysOnTopBox.IsChecked = settings.AlwaysOnTop;
        PersistStatsBox.IsChecked = settings.PersistStats;
        HideIdleRowsBox.IsChecked = settings.HideIdleRows;
        UseBitsPerSecondBox.IsChecked = settings.UseBitsPerSecond;
        ExcludeLocalTrafficBox.IsChecked = settings.ExcludeLocalTraffic;
        ShowPidColumnBox.IsChecked = settings.ShowPidColumn;
        ShowRateColumnsBox.IsChecked = settings.ShowRateColumns;
        ShowTotalColumnsBox.IsChecked = settings.ShowTotalColumns;
        ShowIpSplitColumnsBox.IsChecked = settings.ShowIpSplitColumns;
        ShowProtocolColumnsBox.IsChecked = settings.ShowProtocolColumns;
        ShowFlowsColumnBox.IsChecked = settings.ShowFlowsColumn;
        ShowPathColumnBox.IsChecked = settings.ShowPathColumn;
        RefreshIntervalBox.ItemsSource = Enumerable.Range(1, 10);
        RefreshIntervalBox.SelectedItem = settings.RefreshIntervalSeconds;
        MinimumBytesBox.Text = settings.MinimumVisibleBytes.ToString();
        PopulateNetworkInterfaces();

        foreach (ComboBoxItem item in LanguageBox.Items)
        {
            if ((item.Tag as string) == settings.Language)
            {
                LanguageBox.SelectedItem = item;
                break;
            }
        }

        foreach (ComboBoxItem item in ThemeBox.Items)
        {
            if ((item.Tag as string) == settings.Theme.ToString())
            {
                ThemeBox.SelectedItem = item;
                break;
            }
        }

        ApplyLocalization();
    }

    private string L(string key) => Localizer.T(_settings.Language, key);

    private void ApplyLocalization()
    {
        Title = L("SettingsTitle");
        TitleText.Text = L("SettingsTitle");
        SubtitleText.Text = L("SettingsSubtitle");
        GeneralSectionText.Text = L("GeneralSettings");
        CaptureSectionText.Text = L("CaptureSettings");
        AppearanceSectionText.Text = L("AppearanceSettings");
        DataSectionText.Text = L("DataSettings");
        StartWithWindowsBox.Content = L("StartWithWindows");
        StartMinimizedBox.Content = L("StartMinimized");
        CloseToTrayBox.Content = L("CloseToTray");
        AlwaysOnTopBox.Content = L("AlwaysOnTop");
        PersistStatsBox.Content = L("PersistStats");
        HideIdleRowsBox.Content = L("HideIdleRows");
        UseBitsPerSecondBox.Content = L("UseBitsPerSecond");
        ExcludeLocalTrafficBox.Content = L("ExcludeLocalTraffic");
        NetworkInterfaceLabel.Text = L("NetworkInterface");
        ThemeLabel.Text = L("Theme");
        SystemThemeItem.Content = L("ThemeSystem");
        DarkThemeItem.Content = L("ThemeDark");
        LightThemeItem.Content = L("ThemeLight");
        LanguageLabel.Text = L("Language");
        RefreshIntervalLabel.Text = L("RefreshInterval");
        MinimumBytesLabel.Text = L("MinimumBytes");
        StartupNoteText.Text = L("StartupNote");
        VisibleColumnsLabel.Text = L("VisibleColumns");
        ShowPidColumnBox.Content = L("ShowPidColumn");
        ShowRateColumnsBox.Content = L("ShowRateColumns");
        ShowTotalColumnsBox.Content = L("ShowTotalColumns");
        ShowIpSplitColumnsBox.Content = L("ShowIpSplitColumns");
        ShowProtocolColumnsBox.Content = L("ShowProtocolColumns");
        ShowFlowsColumnBox.Content = L("ShowFlowsColumn");
        ShowPathColumnBox.Content = L("ShowPathColumn");
        ResetStatsButton.Content = L("ResetStatistics");
        CancelButton.Content = L("Cancel");
        SaveButton.Content = L("Save");
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        ValidationText.Visibility = Visibility.Collapsed;
        MinimumBytesBox.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);

        if (RefreshIntervalBox.SelectedItem is not int interval)
        {
            ShowValidation(L("InvalidRefreshInterval"), RefreshIntervalBox);
            return;
        }

        if (!ulong.TryParse(MinimumBytesBox.Text.Trim(), out var minimumBytes))
        {
            ShowValidation(L("InvalidMinimumBytes"), MinimumBytesBox);
            return;
        }

        var previousStartWithWindows = _settings.StartWithWindows;
        var previousStartupTaskVersion = _settings.StartupTaskVersion;
        var previousStartupExecutablePath = _settings.StartupExecutablePath;

        _settings.StartWithWindows = StartWithWindowsBox.IsChecked == true;
        _settings.StartMinimized = StartMinimizedBox.IsChecked == true;
        _settings.CloseToTray = CloseToTrayBox.IsChecked == true;
        _settings.AlwaysOnTop = AlwaysOnTopBox.IsChecked == true;
        _settings.PersistStats = PersistStatsBox.IsChecked == true;
        _settings.HideIdleRows = HideIdleRowsBox.IsChecked == true;
        _settings.UseBitsPerSecond = UseBitsPerSecondBox.IsChecked == true;
        _settings.ExcludeLocalTraffic = ExcludeLocalTrafficBox.IsChecked == true;
        _settings.NetworkInterfaceId = (NetworkInterfaceBox.SelectedItem as NetworkInterfaceOption)?.Id ?? string.Empty;
        _settings.ShowPidColumn = ShowPidColumnBox.IsChecked == true;
        _settings.ShowRateColumns = ShowRateColumnsBox.IsChecked == true;
        _settings.ShowTotalColumns = ShowTotalColumnsBox.IsChecked == true;
        _settings.ShowIpSplitColumns = ShowIpSplitColumnsBox.IsChecked == true;
        _settings.ShowProtocolColumns = ShowProtocolColumnsBox.IsChecked == true;
        _settings.ShowFlowsColumn = ShowFlowsColumnBox.IsChecked == true;
        _settings.ShowPathColumn = ShowPathColumnBox.IsChecked == true;
        _settings.RefreshIntervalSeconds = interval;
        _settings.MinimumVisibleBytes = minimumBytes;
        _settings.Language = Localizer.NormalizeLanguage((LanguageBox.SelectedItem as ComboBoxItem)?.Tag as string);
        if (Enum.TryParse<AppTheme>((ThemeBox.SelectedItem as ComboBoxItem)?.Tag as string, out var theme))
        {
            _settings.Theme = theme;
        }

        SaveButton.IsEnabled = false;
        try
        {
            var startupResult = await Task.Run(_settings.ApplyStartupRegistration);
            if (!startupResult.Succeeded)
            {
                _settings.StartWithWindows = previousStartWithWindows;
                _settings.StartupTaskVersion = previousStartupTaskVersion;
                _settings.StartupExecutablePath = previousStartupExecutablePath;
            }

            _settings.Save();

            if (!startupResult.Succeeded)
            {
                System.Windows.MessageBox.Show(
                    this,
                    $"{L("StartupRegistrationFailed")}\n{startupResult.ErrorMessage}",
                    L("SettingsTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            _settingsSaved = true;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            SaveButton.IsEnabled = true;
            System.Windows.MessageBox.Show(this, ex.Message, L("SettingsTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PopulateNetworkInterfaces()
    {
        var options = new List<NetworkInterfaceOption>
        {
            new(string.Empty, L("AdapterAutomatic"), string.Empty)
        };
        options.AddRange(NetworkAdapterSampler.GetInterfaceOptions());
        NetworkInterfaceBox.ItemsSource = options;
        NetworkInterfaceBox.SelectedValue = _settings.NetworkInterfaceId;
        if (NetworkInterfaceBox.SelectedItem is null)
        {
            NetworkInterfaceBox.SelectedIndex = 0;
        }
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeBox.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<AppTheme>(item.Tag as string, out var theme))
        {
            ThemeManager.ApplyApplication(theme);
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_settingsSaved)
        {
            return;
        }

        _settings.Theme = _originalTheme;
        ThemeManager.ApplyApplication(_originalTheme);
    }

    private void ShowValidation(string message, System.Windows.Controls.Control control)
    {
        ValidationText.Text = message;
        ValidationText.Visibility = Visibility.Visible;
        control.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "DangerBrush");
        control.Focus();
        if (control is System.Windows.Controls.TextBox textBox)
        {
            textBox.SelectAll();
        }
    }

    private void ResetStatsButton_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(this, L("ResetWarning"), L("ResetStatistics"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _resetStats();
        System.Windows.MessageBox.Show(this, L("ResetDone"), L("ResetStatistics"), MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
