using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterES.Services;
using Wpf.Ui.Controls;

namespace BetterES.View.Pages;

public enum ConnectionMode { Standalone, Bridge, Passthrough }

public partial class ModePage : Page
{
    private readonly ConnectionService _connection;
    private bool _initialized;
    public ConnectionMode ActiveMode { get; private set; } = ConnectionMode.Standalone;

    public ModePage(ConnectionService connection)
    {
        _connection = connection;
        InitializeComponent();
        switch (_connection.Mode)
        {
            case BridgeMode.Standalone: ModeStandalone.IsChecked = true; break;
            case BridgeMode.Bridge: ModeBridge.IsChecked = true; break;
            case BridgeMode.Passthrough: ModePassthrough.IsChecked = true; break;
        }
        switch (_connection.Source)
        {
            case BridgeSource.Throttle: SourceThrottle.IsChecked = true; break;
            case BridgeSource.Turbo: SourceTurbo.IsChecked = true; break;
        }

        switch (_connection.TargetRpmBridgeMethod)
        {
            case RpmBridgeMethod.DynoHold: MethodDynoHold.IsChecked = true; break;
            case RpmBridgeMethod.DirectVelocity: MethodDirect.IsChecked = true; break;
        }

        RpmOverrideToggle.IsChecked = _connection.RpmOverrideEnabled;
        ThrottleOverrideToggle.IsChecked = _connection.ThrottleOverrideEnabled;
        _connection.StateChanged += OnStateChanged;
        _connection.BridgeMethodChanged += OnBridgeMethodChanged;
        _initialized = true;
        UpdateLiveStatus();
        UpdateBridgeSettingsVisibility();
    }

    private void ModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || sender is not RadioButton rb || _connection == null) return;
        var mode = rb.Name switch
        {
            "ModeStandalone" => BridgeMode.Standalone,
            "ModeBridge" => BridgeMode.Bridge,
            "ModePassthrough" => BridgeMode.Passthrough,
            _ => BridgeMode.Standalone
        };
        _connection.Mode = mode;
        ActiveMode = (ConnectionMode)(int)mode;
        if (ModeBadgeText != null) ModeBadgeText.Text = mode.ToString();
        if (ModeBadge != null) ModeBadge.Background = mode switch
        {
            BridgeMode.Standalone => new SolidColorBrush(Color.FromRgb(0x40, 0x44, 0xAA)),
            BridgeMode.Bridge => new SolidColorBrush(Color.FromRgb(0x44, 0xAA, 0x44)),
            BridgeMode.Passthrough => new SolidColorBrush(Color.FromRgb(0x44, 0xAA, 0xAA)),
            _ => new SolidColorBrush(Color.FromRgb(0x40, 0x44, 0xAA))
        };
        UpdateBridgeSettingsVisibility();
        UpdateBridgeStatus();
    }

    private void SourceRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || _connection == null) return;
        _connection.Source = rb.Name switch
        {
            "SourceThrottle" => BridgeSource.Throttle,
            "SourceTurbo" => BridgeSource.Turbo,
            _ => BridgeSource.Throttle
        };

    }

    private void MethodRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || sender is not RadioButton rb || _connection == null) return;
        _connection.TargetRpmBridgeMethod = rb.Name switch
        {
            "MethodDynoHold" => RpmBridgeMethod.DynoHold,
            "MethodDirect" => RpmBridgeMethod.DirectVelocity,
            _ => RpmBridgeMethod.DynoHold
        };
    }

    private void UpdateBridgeSettingsVisibility()
    {
        if (BridgeSettingsCard == null) return;
        BridgeSettingsCard.Visibility = _connection.Mode == BridgeMode.Bridge ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ThrottleOverride_Changed(object sender, RoutedEventArgs e)
    {
        if (ThrottleOverrideToggle == null || _connection == null) return;
        _connection.ThrottleOverrideEnabled = ThrottleOverrideToggle.IsChecked == true;
    }

    private void RpmOverride_Changed(object sender, RoutedEventArgs e)
    {
        if (RpmOverrideToggle == null || _connection == null) return;
        _connection.RpmOverrideEnabled = RpmOverrideToggle.IsChecked == true;
    }

    private void OnBridgeMethodChanged(BridgeMethodStatus method) => Dispatcher.Invoke(() => UpdateBridgeMethodStatus(method));

    private void UpdateBridgeMethodStatus(BridgeMethodStatus method)
    {
        if (BridgeMethodStatusText == null) return;
        if (method == BridgeMethodStatus.Uninitialized) {
            BridgeMethodStatusText.Text = "Not initialized";
            BridgeMethodStatusText.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty);
            BridgeMethodStatusText.Opacity = 0.5;
            return;
        }

        BridgeMethodStatusText.Text = method switch
        {
            BridgeMethodStatus.DynoHold => "Active: Dyno Hold ✓",
            BridgeMethodStatus.DirectVelocity => "Active: Direct Velocity Write",
            BridgeMethodStatus.Failed => "Active: Failed (Fallback to Direct)",
            _ => "Unknown"
        };
        
        BridgeMethodStatusText.Foreground = method switch
        {
            BridgeMethodStatus.DynoHold => new SolidColorBrush(Color.FromRgb(0x80, 0xFF, 0x80)),
            BridgeMethodStatus.Failed => new SolidColorBrush(Color.FromRgb(0xFF, 0x80, 0x80)),
            _ => new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA))
        };
        BridgeMethodStatusText.Opacity = 1.0;
    }

    private void OnStateChanged(ConnectionState state) => Dispatcher.Invoke(UpdateLiveStatus);

    private void UpdateLiveStatus()
    {
        if (GameStatusLabel == null) return;
        GameStatusLabel.Text = _connection.GameState switch
        {
            ConnectionState.Connected => _connection.SelectedGame == GameType.AssettoCorsa ? "AC ✓" : "BeamNG ✓",
            ConnectionState.Connecting => "Connecting",
            _ => "Offline"
        };
        GameStatusLabel.Foreground = _connection.GameState == ConnectionState.Connected
            ? new SolidColorBrush(Color.FromRgb(0x80, 0xFF, 0x80))
            : new SolidColorBrush(Color.FromRgb(0xFF, 0x80, 0x80));
        EsStatusLabel.Text = _connection.EsState switch
        {
            ConnectionState.Connected => "Connected",
            ConnectionState.Connecting => "Connecting",
            _ => "Offline"
        };
        EsStatusLabel.Foreground = _connection.EsState == ConnectionState.Connected
            ? new SolidColorBrush(Color.FromRgb(0x80, 0xFF, 0x80))
            : new SolidColorBrush(Color.FromRgb(0xFF, 0x80, 0x80));
        UpdateBridgeStatus();
    }

    private void UpdateBridgeStatus()
    {
        if (BridgeStatusLabel == null) return;
        bool gc = _connection.GameState == ConnectionState.Connected;
        bool ec = _connection.EsState == ConnectionState.Connected;
        if (ActiveMode == ConnectionMode.Bridge && gc && ec)
        { BridgeStatusLabel.Text = "Active"; BridgeStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0xFF, 0x80)); }
        else if (ActiveMode == ConnectionMode.Bridge && (gc || ec))
        { BridgeStatusLabel.Text = "Waiting"; BridgeStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0x80)); }
        else if (ActiveMode == ConnectionMode.Passthrough)
        { BridgeStatusLabel.Text = "Bypassed"; BridgeStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA)); }
        else
        { BridgeStatusLabel.Text = "Inactive"; BridgeStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x44)); }
    }

    ~ModePage() { 
        _connection.StateChanged -= OnStateChanged; 
        _connection.BridgeMethodChanged -= OnBridgeMethodChanged;
    }
}
