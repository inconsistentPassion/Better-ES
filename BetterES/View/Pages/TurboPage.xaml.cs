using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BetterES.Services;
using BetterES.View.Controls;

namespace BetterES.View.Pages;

public partial class TurboPage : Page
{
    private readonly TurboService _turboService;
    private readonly ConnectionService _connectionService;
    private readonly SettingsService _settings;
    private bool _eventsSubscribed;

    public TurboPage(TurboService turboService, ConnectionService connectionService, SettingsService settings)
    {
        _turboService = turboService;
        _connectionService = connectionService;
        _settings = settings;
        InitializeComponent();

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (_eventsSubscribed) return;
        _eventsSubscribed = true;

        _turboService.BoostChanged += OnBoostChanged;
        _connectionService.RpmChanged += OnRpmChanged;

        UpdateBoostDisplay(0);
        UpdateMultiplierDisplay();

        TurboFunctionalSwitch.IsChecked = _turboService.IsFunctional;
    }

    private void TurboFunctionalSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        _turboService.IsFunctional = TurboFunctionalSwitch.IsChecked == true;
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_eventsSubscribed) return;
        _eventsSubscribed = false;

        _turboService.BoostChanged -= OnBoostChanged;
        _connectionService.RpmChanged -= OnRpmChanged;
    }

    private void TurboToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_turboService.IsRunning)
        {
            _turboService.Stop();
            TurboToggleButton.Content = "Start";
            TurboToggleButton.Appearance = Wpf.Ui.Controls.ControlAppearance.Primary;
            TurboStatusText.Text = "OFF";
            TurboStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x80, 0x80));
        }
        else
        {
            _turboService.Start();
            TurboToggleButton.Content = "Stop";
            TurboToggleButton.Appearance = Wpf.Ui.Controls.ControlAppearance.Danger;
            TurboStatusText.Text = "ON";
            TurboStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0xFF, 0x80));
        }
    }

    // ── Hook telemetry ─────────────────────────────────────────────

    private void OnRpmChanged(double? rpm)
    {
        if (!_turboService.IsRunning) return;

        double r = rpm ?? 0;
        if (r <= 0) return;

        double throttle = _connectionService.CurrentThrottle ?? 0;
        double torque = _connectionService.Torque ?? 0;
        _turboService.UpdateTelemetry(throttle, r, torque);
    }

    // ── Boost display ──────────────────────────────────────────────

    private void OnBoostChanged(double boostBar)
    {
        Dispatcher.BeginInvoke(() => UpdateBoostDisplay(boostBar));
    }

    private void UpdateBoostDisplay(double boostBar)
    {
        double maxB = _turboService.MaxBoost > 0 ? _turboService.MaxBoost : 1.344;
        double wg = _turboService.Wastegate > 0 ? Math.Min(_turboService.Wastegate, maxB) : maxB;

        boostBar = Math.Max(0, boostBar);
        double psi = boostBar * 14.5038;

        if (_settings.Units == UnitSystem.Imperial)
        {
            BoostValueText.Text = $"{psi:F1}";
            BoostUnitText.Text = "PSI";
        }
        else
        {
            BoostValueText.Text = $"{boostBar:F3}";
            BoostUnitText.Text = "BAR";
        }

        double gaugeWidth = BoostBar.Parent is FrameworkElement parent ? parent.ActualWidth : 300;
        if (gaugeWidth <= 0) gaugeWidth = 300;

        double visualMax = maxB / 0.875;
        double boostPercent = Math.Clamp((boostBar / visualMax) * 100.0, 0, 100);
        BoostBar.Width = (boostPercent / 100.0) * gaugeWidth;

        double wgPercent = (wg / visualMax) * 100.0;
        WastegateTick.Margin = new Thickness((wgPercent / 100.0) * gaugeWidth - 1, 0, 0, 0);
        WastegateZone.Width = gaugeWidth - (wgPercent / 100.0) * gaugeWidth;

        Color barStart, barEnd;
        if (boostPercent >= 82)
        { barStart = Color.FromRgb(0xDD, 0x22, 0x22); barEnd = Color.FromRgb(0xFF, 0x44, 0x44); }
        else if (boostPercent >= 65)
        { barStart = Color.FromRgb(0xFF, 0x88, 0x00); barEnd = Color.FromRgb(0xFF, 0xBB, 0x00); }
        else
        { barStart = Color.FromRgb(0x22, 0x66, 0xFF); barEnd = Color.FromRgb(0x44, 0x88, 0xFF); }
        BarColorStart.Color = barStart;
        BarColorEnd.Color = barEnd;

        if (boostBar / maxB > 0.7)
            BoostValueText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x44));
        else if (boostBar / maxB > 0.4)
            BoostValueText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xBB, 0x00));
        else
            BoostValueText.Foreground = new SolidColorBrush(Colors.White);

        UpdateTickMarks(gaugeWidth, visualMax);
    }

    private void UpdateTickMarks(double barWidth, double visualMax)
    {
        var ticks = new List<TickMark>();
        if (visualMax <= 0) return;

        double[] candidates = { 0.1, 0.2, 0.25, 0.5, 1.0 };
        double interval = 0.25;
        foreach (var c in candidates)
        {
            int count = (int)(visualMax / c);
            if (count >= 5 && count <= 12) { interval = c; break; }
        }

        for (double boost = 0; boost <= visualMax + 0.001; boost += interval)
        {
            double fraction = Math.Min(boost / visualMax, 1.0);
            ticks.Add(new TickMark { Label = boost.ToString("0.#"), Position = fraction * barWidth });
        }

        BoostTickMarks.Items.Clear();
        foreach (var tick in ticks)
        {
            var item = new ContentPresenter
            {
                Content = tick,
                ContentTemplate = (DataTemplate)FindResource("BoostTickTemplate")
            };
            BoostTickMarks.Items.Add(item);
            item.Loaded += (s, e) =>
            {
                Canvas.SetLeft(item, tick.Position - 4);
                Canvas.SetTop(item, 0);
            };
        }
    }

    // ── Power multiplier ───────────────────────────────────────────

    private void UpdateMultiplierDisplay()
    {
        double maxB = _turboService.MaxBoost;
        double wg = Math.Min(_turboService.Wastegate, maxB);
        double eff = 1.0;

        PeakMultiplierText.Text = $"{1.0 + maxB * eff:F2}×";
        WgMultiplierText.Text = $"{1.0 + wg * eff:F2}×";
        PeakBoostLabel.Text = $"{maxB:F3} bar";
        WgBoostLabel.Text = $"{wg:F3} bar";

        DrawCurve();
    }

    private void DrawCurve()
    {
        CurveCanvas.Children.Clear();

        double maxB = _turboService.MaxBoost;
        double eff = 1.0;
        if (maxB <= 0 || eff <= 0) return;

        double canvasW = CurveCanvas.ActualWidth > 0 ? CurveCanvas.ActualWidth : 250;
        double canvasH = CurveCanvas.ActualHeight > 0 ? CurveCanvas.ActualHeight : 60;

        double peakMult = 1.0 + maxB;
        double range = peakMult - 1.0;
        if (range <= 0) range = 1;

        for (double m = 1.0; m <= peakMult + 0.01; m += 0.5)
        {
            double y = canvasH - ((m - 1.0) / range) * canvasH;
            CurveCanvas.Children.Add(new Line
            {
                X1 = 0, X2 = canvasW, Y1 = y, Y2 = y,
                Stroke = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 0.5
            });
            var label = new TextBlock
            {
                Text = $"{m:F1}×", FontSize = 7,
                Foreground = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF))
            };
            Canvas.SetLeft(label, 2);
            Canvas.SetTop(label, y - 8);
            CurveCanvas.Children.Add(label);
        }

        var points = new PointCollection();
        for (int i = 0; i <= 50; i++)
        {
            double frac = (double)i / 50;
            double boost = frac * maxB;
            double mult = 1.0 + boost;
            points.Add(new Point(frac * canvasW, canvasH - ((mult - 1.0) / range) * canvasH));
        }
        CurveCanvas.Children.Add(new Polyline
        {
            Points = points,
            Stroke = new SolidColorBrush(Color.FromRgb(0x44, 0xFF, 0x88)),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round
        });

        double wgX = Math.Min(_turboService.Wastegate / maxB, 1.0) * canvasW;
        CurveCanvas.Children.Add(new Line
        {
            X1 = wgX, X2 = wgX, Y1 = 0, Y2 = canvasH,
            Stroke = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0x44, 0x44)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 2 }
        });
        var wgLabel = new TextBlock
        {
            Text = "WG", FontSize = 7,
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0x44, 0x44))
        };
        Canvas.SetLeft(wgLabel, wgX + 2);
        Canvas.SetTop(wgLabel, 2);
        CurveCanvas.Children.Add(wgLabel);
    }

    // ── Parameter sliders ──────────────────────────────────────────

    private void ParamSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized) return;

        _turboService.MaxBoost = MaxBoostSlider.Value;
        _turboService.Wastegate = WastegateSlider.Value;
        _turboService.ReferenceRpm = RefRpmSlider.Value;
        _turboService.Gamma = GammaSlider.Value;
        _turboService.LagUp = LagUpSlider.Value;
        _turboService.LagDown = LagDnSlider.Value;

        MaxBoostText.Text = $"{MaxBoostSlider.Value:F3}";
        WastegateText.Text = $"{WastegateSlider.Value:F3}";
        RefRpmText.Text = $"{RefRpmSlider.Value:F0}";
        GammaText.Text = $"{GammaSlider.Value:F1}";
        LagUpText.Text = $"{LagUpSlider.Value:F3}";
        LagDnText.Text = $"{LagDnSlider.Value:F3}";

        UpdateMultiplierDisplay();
    }
}
