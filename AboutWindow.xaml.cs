using System.IO;
using System.Windows;

namespace FlowLens;

public partial class AboutWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action? _shutdownForUpdate;
    private CancellationTokenSource? _updateCancellation;
    private GitHubUpdateResult? _update;
    private bool _busy;

    public AboutWindow(AppSettings settings, Action? shutdownForUpdate = null, GitHubUpdateResult? cachedUpdate = null)
    {
        InitializeComponent();
        _settings = settings;
        _shutdownForUpdate = shutdownForUpdate;
        ThemeManager.Apply(this, settings);
        ApplyLocalization();
        if (cachedUpdate is not null) ShowUpdate(cachedUpdate);
        Closed += (_, _) => _updateCancellation?.Cancel();
    }

    private string L(string key) => Localizer.T(_settings.Language, key);

    private void ApplyLocalization()
    {
        Title = L("AboutTitle");
        TitleText.Text = "FlowLens";
        SubtitleText.Text = L("AboutSubtitle");
        VersionText.Text = $"{AppBuildInfo.Version}  ·  {L(AppBuildInfo.IsTestBuild ? "TestBuild" : "StableBuild")}";
        UpdatesTitleText.Text = L("UpdateTitle");
        UpdatesSourceText.Text = L("UpdateSource");
        CheckUpdatesButton.Content = L("CheckUpdates");
        FeatureText.Text = L("AboutFeatures");
        RuntimeText.Text = L("AboutRuntime");
        if (TrafficHistoryStore.HasLegacyHistory)
        {
            RuntimeText.Text += Environment.NewLine + L("LegacyHistoryPreserved");
        }
        DataLabelText.Text = L("AboutData");
        DataPathText.Text = AppSettings.AppDataDir;
        LicenseLabelText.Text = L("AboutLicense");
        LicenseText.Text = L("AboutLicenseValue");
        AiNoteText.Text = L("AboutAi");
        CloseButton.Content = L("Close");
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) _updateCancellation?.Cancel();
        else Close();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        CheckUpdatesButton.IsEnabled = InstallUpdateButton.IsEnabled = !busy;
        CloseButton.Content = L(busy ? "Cancel" : "Close");
    }

    private void ShowUpdate(GitHubUpdateResult? result)
    {
        _update = result;
        UpdateStatusText.Text = result is null ? L("UpdateNoRelease") :
            result.CanInstall ? string.Format(L(AppBuildInfo.IsTestBuild ? "StableAvailable" : "UpdateAvailable"), result.Release.Version.Core) : L("NoUpdates");
        InstallUpdateButton.Visibility = result?.CanInstall == true ? Visibility.Visible : Visibility.Collapsed;
        InstallUpdateText.Text = L(AppBuildInfo.IsTestBuild ? "InstallStable" : "InstallUpdate");
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _updateCancellation = new CancellationTokenSource();
        SetBusy(true);
        UpdateStatusText.Text = L("CheckingUpdates");
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        try
        {
            using var service = new GitHubUpdateService();
            ShowUpdate(await service.CheckAsync(AppBuildInfo.Version, AppBuildInfo.IsTestBuild, _updateCancellation.Token));
        }
        catch (OperationCanceledException) { UpdateStatusText.Text = L("UpdateCancelled"); }
        catch (Exception ex) { UpdateStatusText.Text = string.Format(L("UpdateFailed"), ex.Message); }
        finally { SetBusy(false); _updateCancellation.Dispose(); _updateCancellation = null; }
    }

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _update?.CanInstall != true || _shutdownForUpdate is null) return;
        if (System.Windows.MessageBox.Show(this,
                string.Format(L(AppBuildInfo.IsTestBuild ? "UpdateConfirm" : "UpdateConfirmStable"), _update.Release.Version.Core),
                L("UpdateTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;

        _updateCancellation = new CancellationTokenSource();
        var token = _updateCancellation.Token;
        SetBusy(true);
        UpdateProgressBar.Value = 0;
        UpdateProgressBar.Visibility = Visibility.Visible;
        var staging = Path.Combine(AppSettings.AppDataDir, "updates", Guid.NewGuid().ToString("N"));
        try
        {
            using var service = new GitHubUpdateService();
            var progress = new Progress<GitHubUpdateProgress>(value =>
            {
                var percent = value.TotalBytes > 0 ? 100.0 * value.DownloadedBytes / value.TotalBytes : 0;
                UpdateProgressBar.Value = percent;
                UpdateStatusText.Text = string.Format(L("UpdateDownloading"), percent);
            });
            var downloaded = await service.DownloadAndPrepareAsync(_update.Release, staging, progress, token);
            token.ThrowIfCancellationRequested();
            UpdateStatusText.Text = L("UpdatePreparing");
            var prepared = await Task.Run(() => UpdateInstaller.Prepare(downloaded.ExecutablePath,
                Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable."),
                Environment.ProcessId, staging, downloaded.ExecutableSha256), token);
            using var helper = await UpdateInstaller.LaunchAsync(prepared, token);
            Close();
            _shutdownForUpdate();
        }
        catch (OperationCanceledException) { UpdateStatusText.Text = L("UpdateCancelled"); }
        catch (Exception ex) { UpdateStatusText.Text = string.Format(L("UpdateFailed"), ex.Message); }
        finally
        {
            UpdateProgressBar.Visibility = Visibility.Collapsed;
            SetBusy(false);
            _updateCancellation.Dispose();
            _updateCancellation = null;
        }
    }
}
