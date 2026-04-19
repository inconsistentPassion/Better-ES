using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BetterES.Services;

namespace BetterES.View.Pages;

public partial class DynoPage : Page
{
    private readonly ConnectionService _connectionService;

    private double _peakHp;
    private double _peakHpRpm;
    private double _peakTorque;
    private double _peakTorqueRpm;

    // Graph data
    private readonly List<(double Rpm, double Torque, double Hp)> _currentSamples = new();
    private List<(double Rpm, double Torque, double Hp)> _baselineSamples = new();
    private readonly object _graphLock = new();
    private bool _graphComplete = false;

    // Graph layout
    private const double GraphML = 52;
    private const double GraphMR = 16;
    private const double GraphMT = 8;
    private const double GraphMB = 32;

    public DynoPage(ConnectionService connectionService)
    {
        _connectionService = connectionService;
        InitializeComponent();

        _connectionService.RpmChanged += OnRpmChanged;
        _connectionService.TorqueChanged += OnTorqueChanged;
        GraphCanvas.SizeChanged += OnGraphSizeChanged;
    }

    private void OnRpmChanged(double? rpm)
    {
        Dispatcher.Invoke(() =>
        {
            double r = rpm ?? 0;
            double t = _connectionService.Torque ?? 0;
            RpmText.Text = $"{r:F0}";
            UpdateDyno(r, t);
        });
    }

    private void OnTorqueChanged(double? torque)
    {
        Dispatcher.Invoke(() =>
        {
            double t = torque ?? 0;
            double r = _connectionService.CurrentRpm ?? 0;
            UpdateDyno(r, t);
        });
    }

    private void UpdateDyno(double rpm, double torque)
    {
        double hp = rpm > 0 ? torque * rpm / 5252.0 : 0;

        HpText.Text = $"{hp:F0}";
        TorqueText.Text = $"{torque:F0}";

        // Feed graph — stop at 97% of redline
        if (rpm > 200 && torque > 0 && !_graphComplete)
        {
            lock (_graphLock) { _currentSamples.Add((rpm, torque, hp)); }

            double? maxRpm = _connectionService.MaxRpm;
            if (maxRpm.HasValue && maxRpm.Value > 0 && rpm >= maxRpm.Value * 0.97)
            {
                _graphComplete = true;
            }
        }

        // Always redraw (even after complete, for live readout)
        RedrawGraph();

        // Peaks
        if (hp > _peakHp && rpm > 500) { _peakHp = hp; _peakHpRpm = rpm; }
        if (torque > _peakTorque && rpm > 500) { _peakTorque = torque; _peakTorqueRpm = rpm; }

        PeakHpText.Text = $"{_peakHp:F0} hp @ {_peakHpRpm:F0} RPM";
        PeakTorqueText.Text = $"{_peakTorque:F0} lb-ft @ {_peakTorqueRpm:F0} RPM";
    }

    private void SaveBaseline_Click(object sender, RoutedEventArgs e)
    {
        lock (_graphLock)
        {
            _baselineSamples = _currentSamples.ToList();
        }
        RedrawGraph();
    }

    private void ClearBaseline_Click(object sender, RoutedEventArgs e)
    {
        lock (_graphLock) { _baselineSamples.Clear(); }
        RedrawGraph();
    }

    private void ResetPeaks_Click(object sender, RoutedEventArgs e)
    {
        _peakHp = 0; _peakHpRpm = 0;
        _peakTorque = 0; _peakTorqueRpm = 0;
        _graphComplete = false;
        PeakHpText.Text = "0 hp @ 0 RPM";
        PeakTorqueText.Text = "0 lb-ft @ 0 RPM";
        lock (_graphLock) { _currentSamples.Clear(); }
        RedrawGraph();
    }

    private void OnGraphSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RedrawGraph();
    }

    // ── Smoothing: 3-point moving average ────────────────────────────

    private static List<(double Rpm, double Val)> Smooth2(List<(double Rpm, double Val)> pts)
    {
        if (pts.Count < 3) return pts;
        var result = new List<(double, double)> { pts[0] };
        for (int i = 1; i < pts.Count - 1; i++)
        {
            double rpm = (pts[i - 1].Rpm + pts[i].Rpm + pts[i + 1].Rpm) / 3.0;
            double val = (pts[i - 1].Val + pts[i].Val + pts[i + 1].Val) / 3.0;
            result.Add((rpm, val));
        }
        result.Add(pts[^1]);
        return result;
    }

    // ── Drawing ──────────────────────────────────────────────────────

    private void RedrawGraph()
    {
        GraphCanvas.Children.Clear();

        double w = GraphCanvas.ActualWidth;
        double h = GraphCanvas.ActualHeight;
        if (w < 80 || h < 80) return;

        double pW = w - GraphML - GraphMR;
        double pH = h - GraphMT - GraphMB;

        List<(double Rpm, double Torque, double Hp)> currentSnap;
        List<(double Rpm, double Torque, double Hp)> baselineSnap;
        lock (_graphLock)
        {
            currentSnap = _currentSamples.ToList();
            baselineSnap = _baselineSamples.ToList();
        }

        // Find actual data bounds across BOTH datasets
        double dataMaxRpm = 0;
        double dataMaxVal = 0;

        foreach (var src in new[] { currentSnap, baselineSnap })
        {
            foreach (var s in src)
            {
                if (s.Rpm > dataMaxRpm) dataMaxRpm = s.Rpm;
                if (s.Hp > dataMaxVal) dataMaxVal = s.Hp;
                if (s.Torque > dataMaxVal) dataMaxVal = s.Torque;
            }
        }

        // Round up to nice grid boundaries
        double maxRpm = dataMaxRpm > 0 ? Math.Ceiling(dataMaxRpm / 1000.0) * 1000.0 : 8000;
        if (maxRpm < 4000) maxRpm = 4000;
        double maxVal = dataMaxVal > 0 ? Math.Ceiling(dataMaxVal / 100.0) * 100.0 : 500;
        if (maxVal < 200) maxVal = 200;

        // Grid — vertical RPM lines
        double rpmStep = maxRpm <= 8000 ? 1000 : 2000;
        for (double rpm = 0; rpm <= maxRpm + 1; rpm += rpmStep)
        {
            double x = GraphML + (rpm / maxRpm) * pW;
            AddLine(x, GraphMT, x, GraphMT + pH, 0x18, 0xFF, 0xFF, 0xFF);
            AddLabel($"{rpm / 1000:F0}k", x - 8, GraphMT + pH + 4, 10, 0x60);
        }

        // Grid — horizontal value lines
        double valStep = maxVal <= 500 ? 100 : maxVal <= 1000 ? 200 : 500;
        for (double val = 0; val <= maxVal + 1; val += valStep)
        {
            double y = GraphMT + pH - (val / maxVal) * pH;
            AddLine(GraphML, y, GraphML + pW, y, 0x18, 0xFF, 0xFF, 0xFF);
            AddLabel($"{val:F0}", 4, y - 7, 10, 0x60);
        }
        AddLabel("RPM", GraphML + pW / 2 - 12, GraphMT + pH + 18, 10, 0x60);

        // Draw BASELINE first (behind current)
        if (baselineSnap.Count >= 2)
        {
            var bs = baselineSnap.OrderBy(s => s.Rpm).ToList();
            DrawCurve(bs.Select(s => (s.Rpm, s.Hp)).ToList(), maxRpm, maxVal, pW, pH,
                0x55, 0x77, 0x77, 0x77, 1.5);
            DrawCurve(bs.Select(s => (s.Rpm, s.Torque)).ToList(), maxRpm, maxVal, pW, pH,
                0x55, 0x99, 0x77, 0x44, 1.5);
        }

        // Draw CURRENT on top
        if (currentSnap.Count >= 2)
        {
            var sorted = currentSnap.OrderBy(s => s.Rpm).ToList();
            DrawCurve(sorted.Select(s => (s.Rpm, s.Hp)).ToList(), maxRpm, maxVal, pW, pH,
                0xFF, 0x44, 0x88, 0xFF, 2.5);
            DrawCurve(sorted.Select(s => (s.Rpm, s.Torque)).ToList(), maxRpm, maxVal, pW, pH,
                0xFF, 0xFF, 0x88, 0x00, 2.5);
        }
    }

    private void DrawCurve(List<(double Rpm, double Val)> rawPoints,
        double maxRpm, double maxVal, double pW, double pH,
        byte a, byte r, byte g, byte b, double thickness)
    {
        if (rawPoints.Count < 2) return;

        var smoothed = Smooth2(rawPoints);

        var polyline = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromArgb(a, r, g, b)),
            StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };

        foreach (var (rpm, val) in smoothed)
        {
            double x = GraphML + (rpm / maxRpm) * pW;
            double y = GraphMT + pH - (val / maxVal) * pH;
            // Clamp to plot area
            x = Math.Clamp(x, GraphML, GraphML + pW);
            y = Math.Clamp(y, GraphMT, GraphMT + pH);
            polyline.Points.Add(new Point(x, y));
        }

        GraphCanvas.Children.Add(polyline);
    }

    private void AddLine(double x1, double y1, double x2, double y2, byte a, byte r, byte g, byte b)
    {
        GraphCanvas.Children.Add(new Line
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            Stroke = new SolidColorBrush(Color.FromArgb(a, r, g, b)),
            StrokeThickness = 1
        });
    }

    private void AddLabel(string text, double x, double y, double size, byte alpha,
        byte cr = 0xFF, byte cg = 0xFF, byte cb = 0xFF)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = new SolidColorBrush(Color.FromArgb(alpha, cr, cg, cb))
        };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        GraphCanvas.Children.Add(tb);
    }

    ~DynoPage()
    {
        _connectionService.RpmChanged -= OnRpmChanged;
        _connectionService.TorqueChanged -= OnTorqueChanged;
    }
}
