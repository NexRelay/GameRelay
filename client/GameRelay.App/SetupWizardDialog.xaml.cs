using GameRelay.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using WinRT.Interop;

namespace GameRelay.App;

/// <summary>
/// First-time server setup: an automatic SSH path (the app installs the relay)
/// and a manual path (the user runs the commands). Both end by saving the
/// server address + secret. Returns the resulting values on "Save &amp; connect".
/// </summary>
public sealed partial class SetupWizardDialog : ContentDialog
{
    private readonly nint _hwnd;
    private bool _provisioned;
    private int _resultPort = 7000;

    /// <summary>Set when the user confirms; consumed by the caller.</summary>
    public string? ResultHost { get; private set; }
    public string? ResultSecret { get; private set; }
    public int ResultPort => _resultPort;

    public SetupWizardDialog(nint hwnd)
    {
        _hwnd = hwnd;
        InitializeComponent();
        // Set the default mode after the panels exist — doing it in XAML fires
        // Checked during parse, before AutoPanel/ManualPanel are created.
        AutoRadio.IsChecked = true;
        UpdateManualCommand();
        PrimaryButtonClick += OnPrimary;
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        bool auto = AutoRadio.IsChecked == true;
        AutoPanel.Visibility = auto ? Visibility.Visible : Visibility.Collapsed;
        ManualPanel.Visibility = auto ? Visibility.Collapsed : Visibility.Visible;
        RefreshPrimaryEnabled();
    }

    // ---------------------------------------------------------- automatic

    private void AnyAuto_Changed(object sender, TextChangedEventArgs e)
    {
        ProvisionButton.IsEnabled =
            !string.IsNullOrWhiteSpace(AutoHostBox.Text) &&
            !string.IsNullOrWhiteSpace(AutoUserBox.Text) &&
            !string.IsNullOrWhiteSpace(AutoKeyBox.Text);
        UpdateManualCommand();
        RefreshPrimaryEnabled();
    }

    private async void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop,
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, _hwnd);
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            AutoKeyBox.Text = file.Path;
            AnyAuto_Changed(sender, null!);
        }
    }

    private async void Provision_Click(object sender, RoutedEventArgs e)
    {
        string assetsDir = System.IO.Path.Combine(AppContext.BaseDirectory, "ServerAssets");
        var missing = ServerProvisioner.MissingAssets(assetsDir);
        if (missing.Count > 0)
        {
            ShowAutoResult(InfoBarSeverity.Error, $"bundled server files are missing: {string.Join(", ", missing)}");
            return;
        }

        SetProvisioning(true);
        LogText.Text = "";
        LogBorder.Visibility = Visibility.Visible;
        AutoResult.IsOpen = false;

        void Log(string line) => DispatcherQueue.TryEnqueue(() =>
        {
            LogText.Text += (LogText.Text.Length == 0 ? "" : "\n") + line;
            LogScroll.UpdateLayout();
            LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, disableAnimation: true);
        });

        var result = await ServerProvisioner.ProvisionAsync(
            AutoHostBox.Text.Trim(), AutoUserBox.Text.Trim(), AutoKeyBox.Text.Trim(),
            assetsDir, 7000, Log, CancellationToken.None);

        SetProvisioning(false);
        if (result.Success)
        {
            _provisioned = true;
            _resultPort = result.ControlPort;
            ResultHost = AutoHostBox.Text.Trim();
            ResultSecret = result.Secret;
            ShowAutoResult(InfoBarSeverity.Success,
                "Server installed and running. Click \"Save & connect\" — don't forget the firewall step below.");
        }
        else
        {
            ShowAutoResult(InfoBarSeverity.Error, $"Setup failed: {result.Error}");
        }
        RefreshPrimaryEnabled();
    }

    private void SetProvisioning(bool busy)
    {
        ProvisionButton.IsEnabled = !busy;
        ProvisionSpinner.IsActive = busy;
        ProvisionSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ProvisionIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        ProvisionLabel.Text = busy ? "Installing…" : "Install relay on server";
        IsSecondaryButtonEnabled = !busy;
    }

    private void ShowAutoResult(InfoBarSeverity sev, string msg)
    {
        AutoResult.Severity = sev;
        AutoResult.Message = msg;
        AutoResult.IsOpen = true;
    }

    // ------------------------------------------------------------- manual

    private void AnyManual_Changed(object sender, TextChangedEventArgs e)
    {
        UpdateManualCommand();
        RefreshPrimaryEnabled();
    }

    private void UpdateManualCommand()
    {
        string host = (ManualRadio.IsChecked == true ? ManualHostBox.Text : AutoHostBox.Text);
        host = string.IsNullOrWhiteSpace(host) ? "YOUR_SERVER_IP" : host.Trim();
        ManualCmd.Text =
            $"ssh -i \"path\\to\\your-key.key\" ubuntu@{host} \"mkdir -p ~/gamerelay\"\n" +
            $"scp -i \"path\\to\\your-key.key\" Server\\* ubuntu@{host}:~/gamerelay/\n" +
            $"ssh -i \"path\\to\\your-key.key\" ubuntu@{host} \"cd ~/gamerelay && sudo bash install.sh\"";
    }

    private void CopyManualCmd_Click(object sender, RoutedEventArgs e)
    {
        var dp = new DataPackage();
        dp.SetText(ManualCmd.Text);
        Clipboard.SetContent(dp);
    }

    // ------------------------------------------------------------ confirm

    private void RefreshPrimaryEnabled()
    {
        IsPrimaryButtonEnabled = AutoRadio.IsChecked == true
            ? _provisioned
            : !string.IsNullOrWhiteSpace(ManualHostBox.Text) &&
              ManualSecretBox.Text.Trim().Length >= 16;
    }

    private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (ManualRadio.IsChecked == true)
        {
            ResultHost = ManualHostBox.Text.Trim();
            ResultSecret = ManualSecretBox.Text.Trim();
            _resultPort = 7000;
        }
        // Automatic path already set ResultHost/ResultSecret during provisioning.
    }
}
