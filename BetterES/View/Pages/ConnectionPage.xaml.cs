using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterES.Services;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace BetterES.View.Pages;

public partial class ConnectionPage : Page
{
    private readonly ConnectionService _connectionService;
    private readonly ISnackbarService _snackbarService;
    private readonly LogService _logService;

    public ConnectionPage(ConnectionService connectionService, ISnackbarService snackbarService, LogService logService)
    {
        _connectionService = connectionService;
        _snackbarService = snackbarService;
        _logService = logService;
        InitializeComponent();
        UpdateGameSelection();
        _connectionService.StateChanged += OnStateChanged;
        _connectionService.GameRpmChanged += OnGameRpmChanged;
        _connectionService.GameMaxRpmChanged += OnGameMaxRpmChanged;
        _connectionService.GameGearChanged += OnGameGearChanged;
        _connectionService.GameSpeedChanged += OnGameSpeedChanged;
        _connectionService.GameThrottleChanged += OnGameThrottleChanged;
        _connectionService.GameBoostChanged += OnGameBoostChanged;
        TachometerControl.CurrentRpm = _connectionService.GameRpm ?? 0;
        if (_connectionService.GameMaxRpm is > 0) TachometerControl.MaxRpm = _connectionService.GameMaxRpm.Value;
        TachometerControl.CurrentGear = _connectionService.GameGear;
        SpeedGaugeControl.CurrentSpeed = (_connectionService.GameSpeed / 3.6);
        TurboGaugeControl.CurrentBoost = _connectionService.GameBoost ?? 0;
        UpdateUI();
    }

    private void OnGameRpmChanged(double? rpm) => Dispatcher.Invoke(() => TachometerControl.CurrentRpm = rpm ?? 0);
    private void OnGameMaxRpmChanged(double? maxRpm) => Dispatcher.Invoke(() => { if (maxRpm is > 0) TachometerControl.MaxRpm = maxRpm.Value; });
    private void OnGameGearChanged(int gear) => Dispatcher.Invoke(() => TachometerControl.CurrentGear = gear);
    private void OnGameSpeedChanged(double speedKmh) => Dispatcher.Invoke(() => SpeedGaugeControl.CurrentSpeed = speedKmh / 3.6);
    private void OnGameThrottleChanged(double? throttle) { }
    private void OnGameBoostChanged(double? boost) => Dispatcher.Invoke(() => TurboGaugeControl.CurrentBoost = boost ?? 0);

    private void GameSelector_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb) return;
        _connectionService.SelectedGame = rb.Name switch
        {
            "AssettoCorsaRadio" => GameType.AssettoCorsa,
            "BeamNGRadio" => GameType.BeamNG,
            _ => GameType.None
        };
        BeamNGPortRow.Visibility = _connectionService.SelectedGame == GameType.BeamNG ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateGameSelection()
    {
        switch (_connectionService.SelectedGame)
        {
            case GameType.AssettoCorsa: AssettoCorsaRadio.IsChecked = true; break;
            case GameType.BeamNG: BeamNGRadio.IsChecked = true; BeamNGPortRow.Visibility = Visibility.Visible; break;
            default: AssettoCorsaRadio.IsChecked = false; BeamNGRadio.IsChecked = false; break;
        }
        BeamNGPortBox.Text = _connectionService.BeamNGPort.ToString();
    }

    private async void ConnectGameButton_Click(object sender, RoutedEventArgs e)
    {
        var game = _connectionService.SelectedGame;
        if (game == GameType.None)
        {
            _snackbarService.Show("No Game Selected", "Select a game", ControlAppearance.Caution, null, TimeSpan.FromSeconds(3));
            return;
        }
        if (_connectionService.GameState == ConnectionState.Connected)
        {
            ConnectGameButton.IsEnabled = false;
            ConnectGameButton.Content = "Disconnecting...";
            await _connectionService.DisconnectGameAsync();
            ConnectGameButton.IsEnabled = true;
            _snackbarService.Show("Disconnected", "Game connection closed", ControlAppearance.Info, null, TimeSpan.FromSeconds(3));
            return;
        }
        int port = 4444;
        if (game == GameType.BeamNG && !int.TryParse(BeamNGPortBox.Text, out port))
        {
            _snackbarService.Show("Invalid Port", "Enter a valid port", ControlAppearance.Caution, null, TimeSpan.FromSeconds(3));
            return;
        }
        _connectionService.BeamNGPort = port;
        ConnectGameButton.IsEnabled = false;
        ConnectGameButton.Content = "Connecting...";
        AssettoCorsaRadio.IsEnabled = false;
        BeamNGRadio.IsEnabled = false;
        BeamNGPortBox.IsEnabled = false;

        var success = await _connectionService.ConnectGameAsync(game, AppendLog);
        ConnectGameButton.IsEnabled = true;
        UpdateUI();

        if (success)
        {
            var name = game == GameType.AssettoCorsa ? "Assetto Corsa" : "BeamNG.drive";
            _snackbarService.Show("Connected", $"Reading telemetry from {name}", ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
        }
        else
        {
            _snackbarService.Show("Connection Failed", "Check if the game is running", ControlAppearance.Danger, null, TimeSpan.FromSeconds(5));
        }
    }

    private async void ConnectEsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_connectionService.EsState == ConnectionState.Connected)
        {
            ConnectEsButton.IsEnabled = false;
            await _connectionService.DisconnectEsAsync();
            ConnectEsButton.IsEnabled = true;
            _snackbarService.Show("Disconnected", "ES connection closed", ControlAppearance.Info, null, TimeSpan.FromSeconds(3));
            return;
        }
        ConnectEsButton.IsEnabled = false;
        ConnectEsButton.Content = "Connecting...";
        var success = await _connectionService.ConnectEsAsync(AppendLog);
        ConnectEsButton.IsEnabled = true;
        UpdateUI();
        if (success)
            _snackbarService.Show("Connected", "Hooked into Engine Simulator", ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
        else
            _snackbarService.Show("Hook Failed", "Engine Simulator must be running", ControlAppearance.Danger, null, TimeSpan.FromSeconds(5));
    }

    private void OnStateChanged(ConnectionState state) => Dispatcher.Invoke(UpdateUI);

    private void UpdateUI()
    {
        var gameConnected = _connectionService.GameState == ConnectionState.Connected;
        var esConnected = _connectionService.EsState == ConnectionState.Connected;

        if (gameConnected)
        {
            GameStatusBadge.Background = new SolidColorBrush(Color.FromRgb(0x40, 0xFF, 0x40));
            GameStatusText.Text = "Connected"; GameStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0xFF, 0x80));
            ConnectGameButton.Content = "Disconnect Game"; ConnectGameButton.Appearance = ControlAppearance.Danger;
        }
        else
        {
            GameStatusBadge.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x40, 0x40));
            GameStatusText.Text = "Offline"; GameStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x80, 0x80));
            ConnectGameButton.Content = "Connect Game"; ConnectGameButton.Appearance = ControlAppearance.Primary;
        }
        if (esConnected)
        {
            EsStatusBadge.Background = new SolidColorBrush(Color.FromRgb(0x40, 0xFF, 0x40));
            EsStatusText.Text = "Connected"; EsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0xFF, 0x80));
            ConnectEsButton.Content = "Disconnect ES"; ConnectEsButton.Appearance = ControlAppearance.Danger;
        }
        else
        {
            EsStatusBadge.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x40, 0x40));
            EsStatusText.Text = "Offline"; EsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x80, 0x80));
            ConnectEsButton.Content = "Connect ES"; ConnectEsButton.Appearance = ControlAppearance.Secondary;
        }
        AssettoCorsaRadio.IsEnabled = !gameConnected;
        BeamNGRadio.IsEnabled = !gameConnected;
        BeamNGPortBox.IsEnabled = !gameConnected;
        if (gameConnected)
            GameSourceLabel.Text = _connectionService.SelectedGame == GameType.AssettoCorsa ? "Assetto Corsa" : "BeamNG.drive";
        else
            GameSourceLabel.Text = "—";
    }

    private void AppendLog(string message)
    {
        Dispatcher.Invoke(() =>
        {
            var ts = DateTime.Now.ToString("HH:mm:ss");
            var line = $"[{ts}] {message}";
            if (LogTextBlock.Text == "Log messages will appear here...") LogTextBlock.Text = line;
            else LogTextBlock.Text += "\n" + line;
            LogScrollViewer.ScrollToEnd();
        });
    }

    ~ConnectionPage()
    {
        _connectionService.StateChanged -= OnStateChanged;
        _connectionService.GameRpmChanged -= OnGameRpmChanged;
        _connectionService.GameMaxRpmChanged -= OnGameMaxRpmChanged;
        _connectionService.GameGearChanged -= OnGameGearChanged;
        _connectionService.GameSpeedChanged -= OnGameSpeedChanged;
        _connectionService.GameThrottleChanged -= OnGameThrottleChanged;
        _connectionService.GameBoostChanged -= OnGameBoostChanged;
    }
}
