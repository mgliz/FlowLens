using System.Windows;
using System.Windows.Controls;

namespace FlowLens;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action _resetStats;

    public SettingsWindow(AppSettings settings, Action resetStats)
    {
        InitializeComponent();
        _settings = settings;
        _resetStats = resetStats;
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
        RefreshIntervalBox.Text = settings.RefreshIntervalSeconds.ToString();
        MinimumBytesBox.Text = settings.MinimumVisibleBytes.ToString();

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
        TitleText.Text = L("SettingsTitle");
        SubtitleText.Text = L("SettingsSubtitle");
        StartWithWindowsBox.Content = L("StartWithWindows");
        StartMinimizedBox.Content = L("StartMinimized");
        CloseToTrayBox.Content = L("CloseToTray");
        AlwaysOnTopBox.Content = L("AlwaysOnTop");
        PersistStatsBox.Content = L("PersistStats");
        HideIdleRowsBox.Content = L("HideIdleRows");
        UseBitsPerSecondBox.Content = L("UseBitsPerSecond");
        ExcludeLocalTrafficBox.Content = L("ExcludeLocalTraffic");
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

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RefreshIntervalBox.Text.Trim(), out var interval))
        {
            interval = 1;
        }

        if (!ulong.TryParse(MinimumBytesBox.Text.Trim(), out var minimumBytes))
        {
            minimumBytes = 0;
        }

        _settings.StartWithWindows = StartWithWindowsBox.IsChecked == true;
        _settings.StartMinimized = StartMinimizedBox.IsChecked == true;
        _settings.CloseToTray = CloseToTrayBox.IsChecked == true;
        _settings.AlwaysOnTop = AlwaysOnTopBox.IsChecked == true;
        _settings.PersistStats = PersistStatsBox.IsChecked == true;
        _settings.HideIdleRows = HideIdleRowsBox.IsChecked == true;
        _settings.UseBitsPerSecond = UseBitsPerSecondBox.IsChecked == true;
        _settings.ExcludeLocalTraffic = ExcludeLocalTrafficBox.IsChecked == true;
        _settings.ShowPidColumn = ShowPidColumnBox.IsChecked == true;
        _settings.ShowRateColumns = ShowRateColumnsBox.IsChecked == true;
        _settings.ShowTotalColumns = ShowTotalColumnsBox.IsChecked == true;
        _settings.ShowIpSplitColumns = ShowIpSplitColumnsBox.IsChecked == true;
        _settings.ShowProtocolColumns = ShowProtocolColumnsBox.IsChecked == true;
        _settings.ShowFlowsColumn = ShowFlowsColumnBox.IsChecked == true;
        _settings.ShowPathColumn = ShowPathColumnBox.IsChecked == true;
        _settings.RefreshIntervalSeconds = Math.Clamp(interval, 1, 10);
        _settings.MinimumVisibleBytes = minimumBytes;
        _settings.Language = Localizer.NormalizeLanguage((LanguageBox.SelectedItem as ComboBoxItem)?.Tag as string);
        if (Enum.TryParse<AppTheme>((ThemeBox.SelectedItem as ComboBoxItem)?.Tag as string, out var theme))
        {
            _settings.Theme = theme;
        }
        _settings.Save();

        DialogResult = true;
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeBox.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<AppTheme>(item.Tag as string, out var theme))
        {
            ThemeManager.Apply(this, theme);
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
