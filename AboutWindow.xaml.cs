using System.Reflection;
using System.Windows;

namespace FlowLens;

public partial class AboutWindow : Window
{
    private readonly AppSettings _settings;

    public AboutWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        ThemeManager.Apply(this, settings);
        ApplyLocalization();
    }

    private string L(string key) => Localizer.T(_settings.Language, key);

    private void ApplyLocalization()
    {
        Title = L("AboutTitle");
        TitleText.Text = "FlowLens";
        SubtitleText.Text = L("AboutSubtitle");
        VersionText.Text = $"{L("AboutVersion")}: {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0"}";
        FeatureText.Text = L("AboutFeatures");
        RuntimeText.Text = L("AboutRuntime");
        DataLabelText.Text = L("AboutData");
        DataPathText.Text = AppSettings.AppDataDir;
        LicenseLabelText.Text = L("AboutLicense");
        LicenseText.Text = L("AboutLicenseValue");
        AiNoteText.Text = L("AboutAi");
        CloseButton.Content = L("Close");
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
