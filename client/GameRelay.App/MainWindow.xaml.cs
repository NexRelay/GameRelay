using System.Collections.ObjectModel;
using GameRelay.Core;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace GameRelay.App;

public sealed partial class MainWindow : Window
{
    private readonly RelayClient _client = new();
    private AppConfig _config;
    private readonly bool _configLoadFailed;
    private readonly DispatcherQueueTimer _statsTimer;
    private bool _userWantsConnection;

    public ObservableCollection<TunnelItemViewModel> Tunnels { get; } = [];
    private readonly Queue<string> _logLines = new();

    public MainWindow()
    {
        var loaded = ConfigStore.Load();
        _config = loaded.Config;
        _configLoadFailed = loaded.LoadFailed;

        InitializeComponent();
        AppWindow.Resize(new SizeInt32(1000, 720));

        // Modern chrome: our own title bar row + the app icon in the taskbar.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "gamerelay.ico")); }
        catch { }

        var ver = typeof(MainWindow).Assembly.GetName().Version;
        if (ver is not null) VersionChip.Text = $"v{ver.Major}.{ver.Minor}";

        if (_configLoadFailed)
        {
            AppendLog($"WARNING: could not read your saved settings ({loaded.Error}).");
            AppendLog("Saving is disabled to protect the existing settings file — close and reopen the app.");
            Activated += ShowLoadErrorOnce;
        }
        else if (loaded.RestoredFromBackup)
        {
            AppendLog("settings were restored from the backup copy (the main file was briefly unreadable)");
        }
        // x:Bind with a Window as the binding root is unreliable in the
        // XAML compiler; bind the list in code instead.
        TunnelList.ItemsSource = Tunnels;
        UpdateServerLabel();

        foreach (var t in _config.Tunnels)
            AddTunnelItem(t);
        UpdateEmptyHint();

        _client.StateChanged += (state, detail) =>
            DispatcherQueue.TryEnqueue(() => OnRelayStateChanged(state, detail));
        _client.Log += line =>
            DispatcherQueue.TryEnqueue(() => AppendLog(line));
        _client.TunnelChanged += rt =>
            DispatcherQueue.TryEnqueue(() => RefreshTunnel(rt.Config.Id));

        _statsTimer = DispatcherQueue.CreateTimer();
        _statsTimer.Interval = TimeSpan.FromSeconds(1);
        _statsTimer.Tick += (_, _) => RefreshAllTunnels();
        _statsTimer.Start();

        Closed += async (_, _) =>
        {
            _statsTimer.Stop();
            await _client.StopAsync();
        };

        AppendLog("GameRelay started");
        if (_config.AutoConnect && IsConfigValid())
            StartClient();
        else if (!IsConfigValid())
            AppendLog("configure the server address and secret in Settings to begin");
    }

    private bool IsConfigValid() =>
        !string.IsNullOrWhiteSpace(_config.ServerHost) &&
        _config.ControlPort is > 0 and < 65536 &&
        _config.Secret.Length >= 16;

    // -------------------------------------------------------------- relay

    private void StartClient()
    {
        _userWantsConnection = true;
        _client.Start(_config.ServerHost, _config.ControlPort, _config.Secret, _config.Tunnels);
        ConnectLabel.Text = "Disconnect";
        ConnectIcon.Glyph = ""; // stop
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_userWantsConnection)
        {
            _userWantsConnection = false;
            await _client.StopAsync();
            ConnectLabel.Text = "Connect";
            ConnectIcon.Glyph = ""; // play
        }
        else
        {
            if (!IsConfigValid())
            {
                await ShowSettingsAsync();
                if (!IsConfigValid()) return;
            }
            StartClient();
        }
    }

    private void UpdateServerLabel() =>
        ServerText.Text = string.IsNullOrWhiteSpace(_config.ServerHost)
            ? "No relay configured — click “Set up server” to get started"
            : $"Relay: {_config.ServerHost}:{_config.ControlPort}";

    // -------------------------------------------------------- setup wizard

    private async void SetupButton_Click(object sender, RoutedEventArgs e) =>
        await ShowSetupWizardAsync();

    private async Task ShowSetupWizardAsync()
    {
        SetupWizardDialog dialog;
        ContentDialogResult result;
        try
        {
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            dialog = new SetupWizardDialog(hwnd) { XamlRoot = Content.XamlRoot };
            result = await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"setup wizard error: {ex.Message}");
            try
            {
                string p = Path.Combine(ConfigStore.LocalDir, "wizard-error.log");
                File.WriteAllText(p, $"{DateTime.Now}\n{ex}");
            }
            catch { }
            return;
        }
        if (result != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(dialog.ResultHost) || string.IsNullOrWhiteSpace(dialog.ResultSecret))
            return;

        _config.ServerHost = dialog.ResultHost;
        _config.ControlPort = dialog.ResultPort;
        _config.Secret = dialog.ResultSecret;
        SaveConfig();
        UpdateServerLabel();
        UpdateEmptyHint();
        AppendLog($"server configured: {_config.ServerHost}:{_config.ControlPort}");

        if (_userWantsConnection)
        {
            await _client.StopAsync();
            StartClient();
        }
        else if (IsConfigValid())
        {
            StartClient();
        }
    }

    private void OnRelayStateChanged(RelayState state, string? detail)
    {
        (string text, var color) = state switch
        {
            RelayState.Connected => ("Connected", Colors.MediumSeaGreen),
            RelayState.Connecting => (detail is null ? "Connecting…" : $"Connecting… ({detail})", Colors.Orange),
            _ => ("Disconnected", Colors.Gray),
        };
        StatusText.Text = text;
        StatusDot.Fill = new SolidColorBrush(color);
        if (state != RelayState.Connected) LatencyText.Text = "";
    }

    // ------------------------------------------------------------- tunnels

    private void AddTunnelItem(TunnelConfig cfg)
    {
        var vm = new TunnelItemViewModel(cfg);
        vm.EnabledChanged += async (item, enabled) =>
        {
            await _client.SetTunnelEnabledAsync(item.Id, enabled);
            SaveConfig();
        };
        Tunnels.Add(vm);
    }

    private void RefreshTunnel(string id)
    {
        var vm = Tunnels.FirstOrDefault(t => t.Id == id);
        var rt = _client.Tunnels.FirstOrDefault(t => t.Config.Id == id);
        vm?.UpdateFrom(rt);
    }

    private void RefreshAllTunnels()
    {
        LatencyText.Text = _client.LatencyMs >= 0 ? $"·  {_client.LatencyMs} ms" : "";
        foreach (var vm in Tunnels)
            vm.UpdateFrom(_client.Tunnels.FirstOrDefault(t => t.Config.Id == vm.Id));
    }

    private void UpdateEmptyHint()
    {
        EmptyHint.Visibility = Tunnels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TunnelCountBadge.Visibility = Tunnels.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        TunnelCountText.Text = Tunnels.Count.ToString();

        // First run without a server: steer the user to setup, not add-tunnel.
        bool configured = IsConfigValid();
        EmptyTitle.Text = configured ? "No tunnels yet" : "Let's get you online";
        EmptySubtitle.Text = configured
            ? "Expose your first game server to the internet in a few clicks."
            : "First, set up your free relay server. The app can install it for you over SSH, or guide you through it manually.";
        EmptySetupButton.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        EmptyAddButton.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ShowLoadErrorOnce(object sender, WindowActivatedEventArgs e)
    {
        Activated -= ShowLoadErrorOnce;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Settings could not be loaded",
            Content = "Your saved settings file exists but could not be read right now "
                    + "(it was probably locked briefly by antivirus or Windows indexing).\n\n"
                    + "Your settings are safe. Close the app and open it again.\n\n"
                    + "Saving is disabled in this session so nothing overwrites your file.",
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync();
    }

    private void SaveConfig()
    {
        if (_configLoadFailed)
        {
            AppendLog("not saving: settings were not loaded correctly this session");
            return;
        }
        _config.Tunnels = Tunnels.Select(t => t.Config).ToList();
        try { ConfigStore.Save(_config); }
        catch (Exception ex) { AppendLog($"failed to save config: {ex.Message}"); }
    }

    private async void AddTunnelButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TunnelDialog { XamlRoot = Content.XamlRoot };
        var created = await dialog.ShowForAddAsync();
        foreach (var cfg in created)
        {
            AddTunnelItem(cfg);
            await _client.UpsertTunnelAsync(cfg);
        }
        if (created.Count > 0)
        {
            SaveConfig();
            UpdateEmptyHint();
        }
    }

    private async void EditTunnel_Click(object sender, RoutedEventArgs e)
    {
        string id = (string)((FrameworkElement)sender).Tag;
        var vm = Tunnels.FirstOrDefault(t => t.Id == id);
        if (vm is null) return;

        var dialog = new TunnelDialog { XamlRoot = Content.XamlRoot };
        var edited = await dialog.ShowForEditAsync(vm.Config);
        if (edited is null) return;

        vm.ReplaceConfig(edited);
        await _client.UpsertTunnelAsync(edited);
        SaveConfig();
    }

    private async void DeleteTunnel_Click(object sender, RoutedEventArgs e)
    {
        string id = (string)((FrameworkElement)sender).Tag;
        var vm = Tunnels.FirstOrDefault(t => t.Id == id);
        if (vm is null) return;

        var confirm = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"Delete tunnel “{vm.Name}”?",
            Content = "Players will no longer be able to reach this server through the relay.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        Tunnels.Remove(vm);
        await _client.RemoveTunnelAsync(id);
        SaveConfig();
        UpdateEmptyHint();
    }

    // --------------------------------------------------------------- share

    private void ShareTunnel_Click(object sender, RoutedEventArgs e)
    {
        string id = (string)((FrameworkElement)sender).Tag;
        var vm = Tunnels.FirstOrDefault(t => t.Id == id);
        if (vm is null) return;
        if (string.IsNullOrWhiteSpace(_config.ServerHost))
        {
            AppendLog("set up your server first, then share a tunnel address");
            return;
        }

        // Minecraft Java on the default port doesn't need the port typed.
        string address = vm.Config.PublicPort == 25565 && vm.Config.Protocol == "tcp"
            ? _config.ServerHost
            : $"{_config.ServerHost}:{vm.Config.PublicPort}";

        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(address);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        AppendLog($"copied address for '{vm.Name}': {address}");
        _ = ShowFlyoutAsync((FrameworkElement)sender, $"Copied  {address}");
    }

    private static async Task ShowFlyoutAsync(FrameworkElement anchor, string text)
    {
        var flyout = new Flyout
        {
            Content = new TextBlock { Text = text, FontSize = 13 },
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom,
        };
        flyout.ShowAt(anchor);
        await Task.Delay(1400);
        flyout.Hide();
    }

    // ------------------------------------------------------ game detection

    private async void ScanGamesButton_Click(object sender, RoutedEventArgs e)
    {
        var existing = Tunnels.Select(t => t.Config).ToList();
        var detected = GameDetector.Detect(existing);
        if (detected.Count == 0)
        {
            var none = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "No new game servers found",
                Content = "GameRelay didn't find any known game server listening on this PC that "
                        + "isn't already tunneled. Start your game server first, then scan again — "
                        + "or add a tunnel manually.",
                CloseButtonText = "OK",
            };
            await none.ShowAsync();
            return;
        }

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "These game servers are running on this PC. Pick which to expose:",
            TextWrapping = TextWrapping.Wrap,
        });
        var checks = new List<(CheckBox box, DetectedGame game)>();
        foreach (var g in detected)
        {
            var cb = new CheckBox
            {
                IsChecked = true,
                Content = $"{g.Name}  —  port {g.Port}/{g.Protocol.ToUpperInvariant()}",
            };
            checks.Add((cb, g));
            panel.Children.Add(cb);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Game servers detected",
            Content = panel,
            PrimaryButtonText = "Add selected",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        int added = 0;
        foreach (var (box, g) in checks)
        {
            if (box.IsChecked != true) continue;
            var cfg = new TunnelConfig
            {
                Name = g.Name,
                Protocol = g.Protocol,
                PublicPort = g.Port,
                LocalPort = g.Port,
                LocalHost = "127.0.0.1",
            };
            AddTunnelItem(cfg);
            await _client.UpsertTunnelAsync(cfg);
            added++;
        }
        if (added > 0)
        {
            SaveConfig();
            UpdateEmptyHint();
            AppendLog($"added {added} detected game tunnel(s)");
        }
    }

    // ------------------------------------------------------------ settings

    private async void SettingsButton_Click(object sender, RoutedEventArgs e) =>
        await ShowSettingsAsync();

    private async Task ShowSettingsAsync()
    {
        var dialog = new SettingsDialog { XamlRoot = Content.XamlRoot };
        var updated = await dialog.ShowForAsync(_config);
        if (updated is null) return;

        bool serverChanged = updated.ServerHost != _config.ServerHost ||
                             updated.ControlPort != _config.ControlPort ||
                             updated.Secret != _config.Secret;
        _config = updated;
        SaveConfig();
        UpdateServerLabel();
        AppendLog("settings saved");

        if (serverChanged && _userWantsConnection)
        {
            AppendLog("server settings changed — reconnecting");
            await _client.StopAsync();
            StartClient();
        }
    }

    // ----------------------------------------------------------------- log

    private void AppendLog(string line)
    {
        _logLines.Enqueue($"[{DateTime.Now:HH:mm:ss}] {line}");
        while (_logLines.Count > 300)
            _logLines.Dequeue();
        LogText.Text = string.Join('\n', _logLines);
        LogScroll.UpdateLayout();
        LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, disableAnimation: true);
    }
}
