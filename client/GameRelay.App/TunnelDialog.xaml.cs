using GameRelay.Core;
using Microsoft.UI.Xaml.Controls;

namespace GameRelay.App;

/// <summary>Add/edit dialog for one tunnel, with game presets.</summary>
public sealed partial class TunnelDialog : ContentDialog
{
    private bool _editMode;

    public TunnelDialog()
    {
        InitializeComponent();
        foreach (var preset in GamePresets.All)
            PresetBox.Items.Add($"{preset.Icon}  {preset.Name}");
    }

    private GamePreset? SelectedPreset =>
        PresetBox.SelectedIndex >= 0 ? GamePresets.All[PresetBox.SelectedIndex] : null;

    private void PresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var preset = SelectedPreset;
        if (preset is null || _editMode) return;

        var primary = preset.Ports[0];
        NameBox.Text = preset.Name.StartsWith("Custom") ? "" : preset.Name;
        if (primary.Port > 0)
        {
            PublicPortBox.Value = primary.Port;
            LocalPortBox.Value = primary.Port;
        }
        string proto = primary.Protocol == "both" ? "tcp" : primary.Protocol;
        ProtocolBox.SelectedIndex = proto == "tcp" ? 0 : 1;
        ProtocolBox.IsEnabled = primary.Protocol != "both";

        int extra = GamePresets.Expand(preset).Count - 1;
        ExtraPortsInfo.IsOpen = extra > 0;
        if (extra > 0)
        {
            ExtraPortsInfo.Message = primary.Protocol == "both"
                ? "This game needs both TCP and UDP — two tunnels will be created."
                : $"This game needs {extra + 1} ports — {extra + 1} tunnels will be created.";
        }
    }

    private bool Validate()
    {
        string? problem = null;
        if (PublicPortBox.Value is < 1 or > 65535 || double.IsNaN(PublicPortBox.Value))
            problem = "Public port must be between 1 and 65535.";
        else if (LocalPortBox.Value is < 1 or > 65535 || double.IsNaN(LocalPortBox.Value))
            problem = "Local port must be between 1 and 65535.";
        else if (string.IsNullOrWhiteSpace(LocalHostBox.Text))
            problem = "Local address is required.";

        ValidationBar.IsOpen = problem is not null;
        ValidationBar.Message = problem ?? "";
        return problem is null;
    }

    private string SelectedProtocol =>
        (string)((ComboBoxItem)ProtocolBox.SelectedItem).Tag;

    /// <summary>Shows the dialog in add mode; returns the new tunnels.</summary>
    public async Task<List<TunnelConfig>> ShowForAddAsync()
    {
        _editMode = false;
        PresetBox.SelectedIndex = 0;

        while (true)
        {
            if (await ShowAsync() != ContentDialogResult.Primary) return [];
            if (Validate()) break;
        }

        var preset = SelectedPreset;
        string localHost = LocalHostBox.Text.Trim();
        string name = NameBox.Text.Trim();

        // Multi-port / dual-protocol presets expand into several tunnels;
        // the dialog fields override the primary one.
        if (preset is not null && GamePresets.Expand(preset).Count > 1)
        {
            var list = GamePresets.Expand(preset, (int)PublicPortBox.Value);
            foreach (var cfg in list)
            {
                cfg.LocalHost = localHost;
                if (cfg.PublicPort == (int)PublicPortBox.Value)
                    cfg.LocalPort = (int)LocalPortBox.Value;
                if (!string.IsNullOrEmpty(name))
                    cfg.Name = cfg.Name.Replace(preset.Name, name);
            }
            return list;
        }

        return
        [
            new TunnelConfig
            {
                Name = string.IsNullOrEmpty(name) ? $"{SelectedProtocol.ToUpper()} {(int)PublicPortBox.Value}" : name,
                Protocol = SelectedProtocol,
                PublicPort = (int)PublicPortBox.Value,
                LocalHost = localHost,
                LocalPort = (int)LocalPortBox.Value,
            },
        ];
    }

    /// <summary>Shows the dialog pre-filled; returns the edited config or null.</summary>
    public async Task<TunnelConfig?> ShowForEditAsync(TunnelConfig existing)
    {
        _editMode = true;
        Title = "Edit tunnel";
        PrimaryButtonText = "Save";
        PresetBox.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        ExtraPortsInfo.IsOpen = false;

        NameBox.Text = existing.Name;
        ProtocolBox.SelectedIndex = existing.Protocol == "tcp" ? 0 : 1;
        PublicPortBox.Value = existing.PublicPort;
        LocalHostBox.Text = existing.LocalHost;
        LocalPortBox.Value = existing.LocalPort;

        while (true)
        {
            if (await ShowAsync() != ContentDialogResult.Primary) return null;
            if (Validate()) break;
        }

        var updated = existing.Clone();
        updated.Name = NameBox.Text.Trim();
        updated.Protocol = SelectedProtocol;
        updated.PublicPort = (int)PublicPortBox.Value;
        updated.LocalHost = LocalHostBox.Text.Trim();
        updated.LocalPort = (int)LocalPortBox.Value;
        return updated;
    }
}
