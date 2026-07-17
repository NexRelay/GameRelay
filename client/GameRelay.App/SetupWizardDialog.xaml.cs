using GameRelay.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using WinRT.Interop;

namespace GameRelay.App;

/// <summary>
/// First-time server setup with verifiable, testable stages: an automatic SSH
/// path (the app installs the relay) and a manual path. Per-step "Test" buttons
/// check SSH reachability and the end-to-end relay connection, and a generated
/// Oracle Cloud Shell command opens the cloud firewall. Returns host + secret on
/// "Save &amp; connect".
/// </summary>
public sealed partial class SetupWizardDialog : ContentDialog
{
    private readonly nint _hwnd;
    private bool _provisioned;
    private int _resultPort = 7000;

    public string? ResultHost { get; private set; }
    public string? ResultSecret { get; private set; }
    public int ResultPort => _resultPort;

    public SetupWizardDialog(nint hwnd)
    {
        _hwnd = hwnd;
        InitializeComponent();
        AutoRadio.IsChecked = true;
        UpdateManualCommand();
        OciCmd.Text = OciCommand.BuildSecurityListScript();
        PrimaryButtonClick += OnPrimary;
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        bool auto = AutoRadio.IsChecked == true;
        AutoPanel.Visibility = auto ? Visibility.Visible : Visibility.Collapsed;
        ManualPanel.Visibility = auto ? Visibility.Collapsed : Visibility.Visible;
        RefreshEnabled();
    }

    // ---------------------------------------------------------- automatic

    private void AnyAuto_Changed(object sender, TextChangedEventArgs e)
    {
        UpdateManualCommand();
        RefreshEnabled();
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
            RefreshEnabled();
        }
    }

    private async void TestSsh_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(TestSshButton, TestSshSpinner, TestSshIcon, true);
        TestSshResult.IsOpen = false;
        var r = await ConnectivityTester.TestPortAsync(
            AutoHostBox.Text.Trim(), 22, TimeSpan.FromSeconds(8));
        SetBusy(TestSshButton, TestSshSpinner, TestSshIcon, false);
        Show(TestSshResult, r.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error,
            r.Ok ? "The server is reachable — go ahead and install." : r.Message);
    }

    private async void Provision_Click(object sender, RoutedEventArgs e)
    {
        string assetsDir = System.IO.Path.Combine(AppContext.BaseDirectory, "ServerAssets");
        var missing = ServerProvisioner.MissingAssets(assetsDir);
        if (missing.Count > 0)
        {
            Show(AutoResult, InfoBarSeverity.Error, $"bundled server files are missing: {string.Join(", ", missing)}");
            return;
        }

        SetBusy(ProvisionButton, ProvisionSpinner, ProvisionIcon, true);
        ProvisionLabel.Text = "Installing…";
        IsSecondaryButtonEnabled = false;
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

        SetBusy(ProvisionButton, ProvisionSpinner, ProvisionIcon, false);
        ProvisionLabel.Text = "Install relay on server";
        IsSecondaryButtonEnabled = true;

        if (result.Success)
        {
            _provisioned = true;
            _resultPort = result.ControlPort;
            ResultHost = AutoHostBox.Text.Trim();
            ResultSecret = result.Secret;
            Show(AutoResult, InfoBarSeverity.Success,
                "Server installed and running. Now open the cloud firewall below, then Test the connection.");
        }
        else
        {
            Show(AutoResult, InfoBarSeverity.Error, $"Setup failed: {result.Error}");
        }
        RefreshEnabled();
    }

    // ------------------------------------------------------------- manual

    private void AnyManual_Changed(object sender, TextChangedEventArgs e)
    {
        UpdateManualCommand();
        RefreshEnabled();
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

    private void CopyManualCmd_Click(object sender, RoutedEventArgs e) => Copy(ManualCmd.Text);
    private void CopyOci_Click(object sender, RoutedEventArgs e) => Copy(OciCmd.Text);

    private static void Copy(string text)
    {
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
    }

    // ------------------------------------------------------ end-to-end test

    private (string host, int port, string secret)? EffectiveTarget()
    {
        if (AutoRadio.IsChecked == true && _provisioned && ResultHost is not null && ResultSecret is not null)
            return (ResultHost, _resultPort, ResultSecret);
        if (ManualRadio.IsChecked == true)
        {
            string h = ManualHostBox.Text.Trim();
            string s = ManualSecretBox.Text.Trim();
            if (h.Length > 0 && s.Length >= 16) return (h, 7000, s);
        }
        return null;
    }

    private async void TestPorts_Click(object sender, RoutedEventArgs e)
    {
        var t = EffectiveTarget();
        if (t is null) return;
        SetBusy(TestPortsButton, TestPortsSpinner, TestPortsIcon, true);
        TestPortsResult.IsOpen = false;
        var r = await ConnectivityTester.TestRelayAsync(
            t.Value.host, t.Value.port, t.Value.secret, TimeSpan.FromSeconds(10));
        SetBusy(TestPortsButton, TestPortsSpinner, TestPortsIcon, false);
        Show(TestPortsResult, r.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error,
            r.Ok ? "All good — the relay is reachable and the secret is correct. Click \"Save & connect\"."
                 : r.Message);
    }

    // ------------------------------------------------------------ helpers

    private static void SetBusy(Button btn, ProgressRing ring, FontIcon icon, bool busy)
    {
        btn.IsEnabled = !busy;
        ring.IsActive = busy;
        ring.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        icon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void Show(InfoBar bar, InfoBarSeverity sev, string msg)
    {
        bar.Severity = sev;
        bar.Message = msg;
        bar.IsOpen = true;
    }

    private void RefreshEnabled()
    {
        bool haveAutoDetails =
            !string.IsNullOrWhiteSpace(AutoHostBox.Text) &&
            !string.IsNullOrWhiteSpace(AutoUserBox.Text) &&
            !string.IsNullOrWhiteSpace(AutoKeyBox.Text);

        TestSshButton.IsEnabled = !string.IsNullOrWhiteSpace(AutoHostBox.Text);
        ProvisionButton.IsEnabled = haveAutoDetails;
        TestPortsButton.IsEnabled = EffectiveTarget() is not null;

        IsPrimaryButtonEnabled = AutoRadio.IsChecked == true
            ? _provisioned
            : !string.IsNullOrWhiteSpace(ManualHostBox.Text) && ManualSecretBox.Text.Trim().Length >= 16;
    }

    private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (ManualRadio.IsChecked == true)
        {
            ResultHost = ManualHostBox.Text.Trim();
            ResultSecret = ManualSecretBox.Text.Trim();
            _resultPort = 7000;
        }
    }
}
