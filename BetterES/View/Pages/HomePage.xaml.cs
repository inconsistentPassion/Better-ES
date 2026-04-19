using System;
using System.Windows;
using System.Windows.Controls;
using BetterES.Services;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace BetterES.View.Pages;

public partial class HomePage : Page
{
    private readonly ConnectionService _connectionService;
    private readonly ISnackbarService _snackbarService;
    private readonly LogService? _logService;
    private readonly SettingsService _settings;

    public HomePage(ConnectionService connectionService, ISnackbarService snackbarService, LogService logService, SettingsService settings)
    {
        _connectionService = connectionService;
        _snackbarService = snackbarService;
        _logService = logService;
        _settings = settings;

        // Set initial units on gauges
        var u = settings.Units;

        InitializeComponent();

        // Apply initial units to gauges
        SpeedGaugeControl.Unit = u;
        AirflowGaugeControl.Unit = u;

        _connectionService.StateChanged += OnStateChanged;
        _connectionService.StatusMessageChanged += OnStatusChanged;
        _connectionService.LogMessage += OnLogMessage;
        _settings.UnitsChanged += OnUnitsChanged;

        // Single batched telemetry handler — replaces 6 individual event handlers
        _connectionService.StateUpdated += OnStateUpdated;

        // Initialize gauge values to current backend state
        SpeedGaugeControl.CurrentSpeed = _connectionService.VehicleSpeed;
        AfrGaugeControl.CurrentAfr = _connectionService.CurrentAfr ?? 0;
        AirflowGaugeControl.CurrentFlow = _connectionService.CurrentIntakeFlow ?? 0;
        TachometerControl.CurrentRpm = _connectionService.CurrentRpm ?? 0;
        if (_connectionService.MaxRpm is > 0) TachometerControl.MaxRpm = _connectionService.MaxRpm.Value;
        TachometerControl.CurrentGear = _connectionService.CurrentGear;

        UpdateUI();
    }

    // ── Game selector ──────────────────────────────────────────────

    private void GameSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GameSelector.SelectedItem is not ComboBoxItem item) return;

        var tag = item.Tag?.ToString();
        
        _connectionService.SelectedGame = tag switch
        {
            
            "AssettoCorsa" => GameType.AssettoCorsa,
            "BeamNG"       => GameType.BeamNG,
            _              => GameType.None
        };

        BeamNGPortRow.Visibility = tag == "BeamNG" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── ES Connect ─────────────────────────────────────────────────

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_connectionService.EsState == ConnectionState.Connected)
        {
            await _connectionService.DisconnectEsAsync();
        }
        else
        {
            ConnectButton.IsEnabled = false;
            var success = await _connectionService.ConnectEsAsync(LogMessage);
            if (!success)
                _snackbarService.Show("Connection Failed", "Could not connect to Engine Simulator",
                    ControlAppearance.Danger, null, TimeSpan.FromSeconds(5));
            ConnectButton.IsEnabled = true;
        }
    }

    // ── Game Connect (auto-bridges with ES) ─────────────────────────

    private async void GameConnectButton_Click(object sender, RoutedEventArgs e)
    {
        // If already connected, disconnect everything
        if (_connectionService.GameState == ConnectionState.Connected)
        {
            GameConnectButton.IsEnabled = false;
            GameConnectButton.Content = "Disconnecting...";
            await _connectionService.DisconnectBridgeAsync();
            await _connectionService.DisconnectEsAsync();
            GameConnectButton.Content = "Connect Game";
            GameConnectButton.IsEnabled = true;
            GameSelector.IsEnabled = true;
            _snackbarService.Show("Disconnected", "Game bridge stopped",
                ControlAppearance.Info, null, TimeSpan.FromSeconds(3));
            return;
        }

        var game = _connectionService.SelectedGame;
        if (game == GameType.None)
        {
            _snackbarService.Show("No Game Selected", "Select a game from the dropdown first",
                ControlAppearance.Caution, null, TimeSpan.FromSeconds(3));
            return;
        }

        if (game == GameType.BeamNG && !int.TryParse(BeamNGPortBox.Text, out _))
        {
            _snackbarService.Show("Invalid Port", "Enter a valid port number",
                ControlAppearance.Caution, null, TimeSpan.FromSeconds(3));
            return;
        }

        GameConnectButton.IsEnabled = false;
        GameConnectButton.Content = "Connecting...";
        GameSelector.IsEnabled = false;

        var success = await _connectionService.ConnectBridgeAsync(game, LogMessage);

        if (success)
        {
            GameConnectButton.Content = "Disconnect Game";
            GameConnectButton.Appearance = ControlAppearance.Danger;
            GameConnectButton.IsEnabled = true;
            var name = game == GameType.AssettoCorsa ? "Assetto Corsa" : "BeamNG.drive";
            _snackbarService.Show("Game Connected", $"{name} → Engine Simulator",
                ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
        }
        else
        {
            GameConnectButton.Content = "Connect Game";
            GameConnectButton.IsEnabled = true;
            GameSelector.IsEnabled = true;
            var name = game == GameType.AssettoCorsa ? "Assetto Corsa" : "BeamNG.drive";
            _snackbarService.Show("Connection Failed", $"Could not bridge {name} with ES",
                ControlAppearance.Danger, null, TimeSpan.FromSeconds(5));
        }
    }


    // ── Events ─────────────────────────────────────────────────────

    private void OnStateChanged(ConnectionState state)
    {
        Dispatcher.Invoke(() => UpdateUI());
    }

    private void OnStatusChanged(string message)
    {
        Dispatcher.Invoke(() => StatusTextBlock.Text = $"Status: {message}");
    }

    private void OnLogMessage(string message)
    {
        Dispatcher.Invoke(() =>
            _snackbarService.Show("Info", message, ControlAppearance.Info, null, TimeSpan.FromSeconds(3)));
    }

    private void OnUnitsChanged(UnitSystem unit)
    {
        Dispatcher.Invoke(() =>
        {
            SpeedGaugeControl.Unit = unit;
            AirflowGaugeControl.Unit = unit;
        });
    }

    /// <summary>
    /// Single batched UI update — replaces 6 individual Dispatcher.Invoke calls
    /// (RpmChanged, MaxRpmChanged, GearChanged, SpeedChanged, AfrChanged, IntakeFlowChanged)
    /// with ONE dispatch that updates all gauges at once. Reduces WPF layout passes.
    /// </summary>
    private void OnStateUpdated()
    {
        Dispatcher.Invoke(() =>
        {
            TachometerControl.CurrentRpm = _connectionService.CurrentRpm ?? 0;
            if (_connectionService.MaxRpm is > 0)
                TachometerControl.MaxRpm = _connectionService.MaxRpm.Value;
            TachometerControl.CurrentGear = _connectionService.CurrentGear;
            SpeedGaugeControl.CurrentSpeed = _connectionService.VehicleSpeed;
            AfrGaugeControl.CurrentAfr = _connectionService.CurrentAfr ?? 0;
            AirflowGaugeControl.CurrentFlow = _connectionService.CurrentIntakeFlow ?? 0;
        });
    }

    private void UpdateUI()
    {
        // ES connection card
        var esConnected = _connectionService.EsState == ConnectionState.Connected;
        var esConnecting = _connectionService.EsState == ConnectionState.Connecting;

        if (esConnected)
        {
            StatusBadge.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x40, 0xFF, 0x40));
            StatusText.Text = "ES Connected";
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x80, 0xFF, 0x80));
            ConnectButton.Content = "Disconnect";
            ConnectButton.Appearance = ControlAppearance.Danger;
        }
        else if (esConnecting)
        {
            StatusBadge.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0x40));
            StatusText.Text = "Connecting...";
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0x80));
            ConnectButton.Content = "Connecting...";
            ConnectButton.IsEnabled = false;
        }
        else
        {
            StatusBadge.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xFF, 0x40, 0x40));
            StatusText.Text = "ES Disconnected";
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xFF, 0x80, 0x80));
            ConnectButton.Content = "Connect ES";
            ConnectButton.Appearance = ControlAppearance.Primary;
            ConnectButton.IsEnabled = true;
        }

        // Game button
        var gameConnected = _connectionService.GameState == ConnectionState.Connected;
        var gameConnecting = _connectionService.GameState == ConnectionState.Connecting;

        if (gameConnected)
        {
            GameConnectButton.Content = "Disconnect Game";
            GameConnectButton.Appearance = ControlAppearance.Danger;
            GameSelector.IsEnabled = false;
        }
        else if (gameConnecting)
        {
            GameConnectButton.Content = "Connecting...";
            GameConnectButton.IsEnabled = false;
            GameSelector.IsEnabled = false;
        }
        else
        {
            GameConnectButton.Content = "Connect Game";
            GameConnectButton.Appearance = ControlAppearance.Primary;
            GameConnectButton.IsEnabled = true;
            GameSelector.IsEnabled = true;
        }
    }

    private void LogMessage(string message)
    {
        _logService?.Info("UI", message);
    }

    ~HomePage()
    {
        _connectionService.StateChanged -= OnStateChanged;
        _connectionService.StatusMessageChanged -= OnStatusChanged;
        _connectionService.LogMessage -= OnLogMessage;
        _settings.UnitsChanged -= OnUnitsChanged;
        _connectionService.StateUpdated -= OnStateUpdated;
    }

    private void TachometerControl_Loaded(object sender, RoutedEventArgs e) { }
}
