using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BetterES.Services;
using BetterES.Backends.Keyboard;

namespace BetterES.View.Pages;

public partial class TuningPage : Page
{
    private readonly ConnectionService _connection;
    private readonly TurboService _turboService;
    private readonly SettingsService _settings;
    private KeyboardBackend? _kb => _connection.EsBackend as KeyboardBackend;

    private readonly DispatcherTimer _sendDebounce;
    private readonly DispatcherTimer _afrDebounce;
    private double _targetAfr = 14.7;
    private const int DebounceMs = 80;
    private bool _userDragged = false;

    public TuningPage(ConnectionService connection, TurboService turboService, SettingsService settings)
    {
        _connection = connection;
        _turboService = turboService;
        _settings = settings;
        InitializeComponent();

        _sendDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMs) };
        _sendDebounce.Tick += (_, _) =>
        {
            _sendDebounce.Stop();
            if (_userDragged) SendAdvance();
        };

        _afrDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMs) };
        _afrDebounce.Tick += (_, _) =>
        {
            _afrDebounce.Stop();
            SendTargetAfr();
        };

        _connection.RpmChanged += OnRpmChanged;
        _connection.AdvanceChanged += OnAdvanceChanged;
        _connection.AfrChanged += OnAfrChanged;
        _turboService.BoostChanged += OnBoostChanged;

        Loaded += (s, e) => UpdateBoostDisplay(0);
    }

    private void TargetAfrSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TargetAfrLabel == null || AfrGaugeControl == null) return;
        _targetAfr = e.NewValue;
        TargetAfrLabel.Text = $"{e.NewValue:F1}";
        AfrTargetValue.Text = $"{e.NewValue:F1}";
        AfrGaugeControl.TargetAfr = e.NewValue;
        _afrDebounce.Stop();
        _afrDebounce.Start();
    }

    private void SendTargetAfr()
    {
        _kb?.SendFuelMixture(_targetAfr);
    }

    private void CustomAfrEnabledSwitch_Checked(object sender, RoutedEventArgs e)
    {
        _connection.CustomAfrEnabled = true;
        _kb?.SendAfrToggle(true);
    }

    private void CustomAfrEnabledSwitch_Unchecked(object sender, RoutedEventArgs e)
    {
        _connection.CustomAfrEnabled = false;
        _kb?.SendAfrToggle(false);
    }

    private void AdvanceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AdvanceValue == null) return;
        AdvanceValue.Text = $"{e.NewValue:F1}°";
        _userDragged = true;
        _sendDebounce.Stop();
        _sendDebounce.Start();
    }

    private void SendAdvance()
    {
        _kb?.SendTimingCommand(true, AdvanceSlider?.Value ?? 0, false, 7000, 50, false, 100);
    }

    private void OnAfrChanged(double? afr)
    {
        Dispatcher.Invoke(() =>
        {
            if (AfrLiveValue == null || AfrGaugeControl == null) return;
            if (afr.HasValue && afr.Value > 0)
            {
                AfrLiveValue.Text = $"{afr.Value:F1}";
                AfrGaugeControl.CurrentAfr = afr.Value;
                // Lambda = AFR / 14.7 (stoichiometric)
                LambdaValue.Text = $"{afr.Value / 14.7:F2}";
            }
            else
            {
                AfrLiveValue.Text = "--";
                AfrGaugeControl.CurrentAfr = 0;
                LambdaValue.Text = "--";
            }
        });
    }

    private void OnRpmChanged(double? rpm)
    {
        Dispatcher.Invoke(() =>
        {
            LiveRpm.Text = rpm.HasValue ? $"{(int)rpm.Value}" : "----";
            if (_turboService.IsRunning && rpm is > 0)
                _turboService.UpdateTelemetry(_connection.CurrentThrottle ?? 0, rpm.Value, _connection.Torque ?? 0);
        });
    }

    private void OnAdvanceChanged(double? advance)
    {
        Dispatcher.Invoke(() => LiveAdvance.Text = advance.HasValue ? $"{advance.Value:F1}°" : "--°");
    }

    private void OnBoostChanged(double boostBar)
    {
        Dispatcher.Invoke(() => UpdateBoostDisplay(boostBar));
    }

    private void UpdateBoostDisplay(double boostBar)
    {
        double maxB = _turboService.MaxBoost > 0 ? _turboService.MaxBoost : 1.344;
        boostBar = Math.Max(0, boostBar);

        if (_settings.Units == UnitSystem.Imperial)
        { BoostValueText.Text = $"{boostBar * 14.5038:F1}"; BoostUnitText.Text = "PSI"; }
        else
        { BoostValueText.Text = $"{boostBar:F3}"; BoostUnitText.Text = "BAR"; }

        double gw = BoostBar.Parent is FrameworkElement p ? p.ActualWidth : 300;
        if (gw <= 0) gw = 300;
        double vm = maxB / 0.875;
        double bp = Math.Clamp((boostBar / vm) * 100.0, 0, 100);
        BoostBar.Width = (bp / 100.0) * gw;

        double wg = _turboService.Wastegate > 0 ? Math.Min(_turboService.Wastegate, maxB) : maxB;
        WastegateTick.Margin = new Thickness((wg / vm) * gw - 1, 0, 0, 0);

        Color bs, be;
        if (bp >= 82) { bs = Color.FromRgb(0xDD, 0x22, 0x22); be = Color.FromRgb(0xFF, 0x44, 0x44); }
        else if (bp >= 65) { bs = Color.FromRgb(0xFF, 0x88, 0x00); be = Color.FromRgb(0xFF, 0xBB, 0x00); }
        else { bs = Color.FromRgb(0x22, 0x66, 0xFF); be = Color.FromRgb(0x44, 0x88, 0xFF); }
        BarColorStart.Color = bs; BarColorEnd.Color = be;

        TurboStatusText.Text = _turboService.IsRunning ? "ON" : "OFF";
        TurboStatusText.Foreground = _turboService.IsRunning
            ? new SolidColorBrush(Color.FromRgb(0x80, 0xFF, 0x80))
            : new SolidColorBrush(Color.FromRgb(0xFF, 0x80, 0x80));
    }

    ~TuningPage()
    {
        _connection.RpmChanged -= OnRpmChanged;
        _connection.AdvanceChanged -= OnAdvanceChanged;
        _connection.AfrChanged -= OnAfrChanged;
        _turboService.BoostChanged -= OnBoostChanged;
    }
}
