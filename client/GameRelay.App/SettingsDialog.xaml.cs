using GameRelay.Core;
using Microsoft.UI.Xaml.Controls;

namespace GameRelay.App;

/// <summary>Server address / secret settings dialog.</summary>
public sealed partial class SettingsDialog : ContentDialog
{
    public SettingsDialog()
    {
        InitializeComponent();
    }

    private bool Validate()
    {
        string? problem = null;
        if (string.IsNullOrWhiteSpace(HostBox.Text))
            problem = "Server address is required.";
        else if (PortBox.Value is < 1 or > 65535 || double.IsNaN(PortBox.Value))
            problem = "Control port must be between 1 and 65535.";
        else if (SecretBox.Password.Length < 16)
            problem = "The shared secret must be at least 16 characters.";

        ValidationBar.IsOpen = problem is not null;
        ValidationBar.Message = problem ?? "";
        return problem is null;
    }

    /// <summary>Shows the dialog; returns the updated config or null.</summary>
    public async Task<AppConfig?> ShowForAsync(AppConfig current)
    {
        HostBox.Text = current.ServerHost;
        PortBox.Value = current.ControlPort;
        SecretBox.Password = current.Secret;
        AutoConnectToggle.IsOn = current.AutoConnect;

        while (true)
        {
            if (await ShowAsync() != ContentDialogResult.Primary) return null;
            if (Validate()) break;
        }

        return new AppConfig
        {
            ServerHost = HostBox.Text.Trim(),
            ControlPort = (int)PortBox.Value,
            Secret = SecretBox.Password,
            AutoConnect = AutoConnectToggle.IsOn,
            Tunnels = current.Tunnels,
        };
    }
}
