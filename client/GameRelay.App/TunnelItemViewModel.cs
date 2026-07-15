using System.ComponentModel;
using GameRelay.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace GameRelay.App;

/// <summary>List-item view model for one tunnel.</summary>
public sealed class TunnelItemViewModel : INotifyPropertyChanged
{
    public TunnelConfig Config { get; private set; }

    public TunnelItemViewModel(TunnelConfig config)
    {
        Config = config;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public string Id => Config.Id;
    public string Name => string.IsNullOrWhiteSpace(Config.Name) ? "(unnamed)" : Config.Name;
    public string ProtoBadge => Config.Protocol.ToUpperInvariant();
    public string PortsText => $"Public :{Config.PublicPort}  →  {Config.LocalHost}:{Config.LocalPort}";

    private static readonly Brush TcpBrush = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129));
    private static readonly Brush UdpBrush = new SolidColorBrush(Color.FromArgb(255, 56, 189, 248));
    public Brush ProtoBrush => Config.Protocol == "udp" ? UdpBrush : TcpBrush;

    public bool Enabled
    {
        get => Config.Enabled;
        set
        {
            if (Config.Enabled == value) return;
            Config.Enabled = value;
            Raise(nameof(Enabled));
            EnabledChanged?.Invoke(this, value);
        }
    }

    /// <summary>Raised when the user flips the toggle.</summary>
    public event Action<TunnelItemViewModel, bool>? EnabledChanged;

    private string _statusText = "Stopped";
    public string StatusText
    {
        get => _statusText;
        private set { if (_statusText != value) { _statusText = value; Raise(nameof(StatusText)); } }
    }

    private Brush _statusBrush = new SolidColorBrush(Colors.Gray);
    public Brush StatusBrush
    {
        get => _statusBrush;
        private set { _statusBrush = value; Raise(nameof(StatusBrush)); }
    }

    private string _trafficText = "";
    public string TrafficText
    {
        get => _trafficText;
        private set { if (_trafficText != value) { _trafficText = value; Raise(nameof(TrafficText)); } }
    }

    /// <summary>Replaces the config (after an edit) and refreshes labels.</summary>
    public void ReplaceConfig(TunnelConfig config)
    {
        Config = config;
        Raise(nameof(Name));
        Raise(nameof(ProtoBadge));
        Raise(nameof(PortsText));
        Raise(nameof(Enabled));
    }

    /// <summary>Pulls live state from the relay client runtime.</summary>
    public void UpdateFrom(TunnelRuntime? rt)
    {
        if (rt is null)
        {
            StatusText = "Stopped";
            StatusBrush = new SolidColorBrush(Colors.Gray);
            TrafficText = "";
            return;
        }
        (StatusText, StatusBrush) = rt.Status switch
        {
            TunnelStatus.Active => ("Active", new SolidColorBrush(Colors.MediumSeaGreen)),
            TunnelStatus.Starting => ("Starting…", new SolidColorBrush(Colors.Orange)),
            TunnelStatus.Error => ($"Error: {rt.ErrorReason}", new SolidColorBrush(Colors.IndianRed)),
            _ => ("Stopped", new SolidColorBrush(Colors.Gray)),
        };
        string conns = rt.ActiveConnections == 1 ? "1 connection" : $"{rt.ActiveConnections} connections";
        TrafficText = $"↓ {FormatBytes(rt.BytesIn)}   ↑ {FormatBytes(rt.BytesOut)}   •   {conns}";
    }

    public static string FormatBytes(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{b / (1024.0 * 1024):0.#} MB",
        _ => $"{b / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
