using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BetterES.Services;
using ControlAppearance = Wpf.Ui.Controls.ControlAppearance;

namespace BetterES.View.Pages
{
    public partial class DragPage : Page
    {
        private readonly ConnectionService _conn;

        private enum DragState { Idle, Staged, WaitingLaunch, Running, Finished }
        private enum GraphMode { Time, Distance, Accel }

        private DragState _state = DragState.Idle;
        private GraphMode _graphMode = GraphMode.Time;
        private readonly Stopwatch _timer = new();
        private double _distanceMeters;
        private double _lastSpeedMps;
        private double _lastTimestamp;
        private double _peakG;
        private double? _timeAt60Mph;

        private readonly (string Name, double Meters, TextBlock Label)[] _distSplits;
        private readonly (string Name, double StartKmh, double EndKmh, TextBlock Label)[] _speedSplits;

        // Sample: (elapsed_s, distance_m, speed_kmh, accel_g)
        private readonly List<(double T, double D, double S, double G)> _currentSamples = new();
        private readonly List<(double T, double D, double S, double G)> _bestSamples = new();
        private readonly object _graphLock = new();
        private double _bestQuarterTime = -1;
        private int _runCount;

        private readonly DispatcherTimer _pollTimer;

        private const double GL = 48, GR = 12, GT = 8, GB = 28;

        private readonly SettingsService _settings;

        public DragPage(ConnectionService connectionService, SettingsService settings)
        {
            _conn = connectionService;
            _settings = settings;
            InitializeComponent();

            _settings.UnitsChanged += _ => UpdateUnitLabels();

            // Distance splits — always stored in meters
            _distSplits = new[]
            {
                ("60 ft",   18.29, Split60ft),
                ("330 ft",  100.58, Split330ft),
                ("1/8 mi",  201.17, Split8th),
                ("1000 ft", 304.8, Split1000ft),
                ("1/4 mi",  402.34, SplitQuarter),
                ("1/2 mi",  804.67, SplitHalf),
                ("1 mi",    1609.34, SplitMile),
            };

            // Speed splits — thresholds always in km/h internally
            _speedSplits = _settings.Units == UnitSystem.Imperial
                ? new[]
                {
                    ("0-60",    0.0, 96.56, Split060),      // 60 mph = 96.56 km/h
                    ("0-100",   0.0, 160.93, Split0100),    // 100 mph = 160.93 km/h
                    ("0-130",   0.0, 209.21, Split0130),    // 130 mph = 209.21 km/h
                    ("60-130",  96.56, 209.21, Split60130),
                }
                : new[]
                {
                    ("0-100",   0.0, 100.0, Split060),       // 0-100 km/h
                    ("0-160",   0.0, 160.0, Split0100),      // 0-160 km/h
                    ("0-200",   0.0, 200.0, Split0130),      // 0-200 km/h
                    ("100-200", 100.0, 200.0, Split60130),
                };

            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _pollTimer.Tick += PollTick;

            Loaded += (_, _) => _pollTimer.Start();
            Unloaded += (_, _) => _pollTimer.Stop();

            GraphCanvas.SizeChanged += (_, _) => RedrawGraph();
        }

        private void PollTick(object? sender, EventArgs e)
        {
            if (_state != DragState.Staged && _state != DragState.WaitingLaunch && _state != DragState.Running)
                return;
            Tick(_conn.VehicleSpeed);
        }

        private void Tick(double speedMps)
        {
            double speedDisplay = _settings.Units == UnitSystem.Imperial
                ? speedMps * 2.23694  // mph
                : speedMps * 3.6;     // km/h
            SpeedText.Text = $"{speedDisplay:F0}";

            string gear = _conn.CurrentGear == 0 ? "N" : (_conn.CurrentGear == -1 ? "R" : _conn.CurrentGear.ToString());
            RpmSmallText.Text = $"{_conn.CurrentRpm ?? 0:F0} RPM · {gear}";

            if (_state == DragState.WaitingLaunch && Math.Abs(speedMps) > 0.05)
            {
                _state = DragState.Running;
                _timer.Restart();
                _lastTimestamp = 0;
                _distanceMeters = 0;
                _lastSpeedMps = speedMps;
                RunStatusText.Text = "Running\u2026";
                RunStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0xBB, 0x44));
            }

            if (_state != DragState.Running) return;

            double elapsed = _timer.Elapsed.TotalSeconds;
            double dt = elapsed - _lastTimestamp;
            _lastTimestamp = elapsed;
            if (dt <= 0 || dt > 0.5) return;

            _distanceMeters += speedMps * dt;

            double accelG = (speedMps - _lastSpeedMps) / (dt * 9.80665);
            accelG = Math.Clamp(accelG, -10, 10);
            _lastSpeedMps = speedMps;
            if (accelG > _peakG) _peakG = accelG;

            GForceText.Text = $"{accelG:F2}";
            GForceText.Foreground = accelG > 0.5
                ? new SolidColorBrush(Color.FromRgb(0x44, 0xBB, 0x44))
                : accelG < -0.3
                    ? new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x44))
                    : new SolidColorBrush(Color.FromRgb(0xFF, 0x88, 0x00));

            TimerText.Text = $"{elapsed:F3}";
            PeakGText.Text = $"{_peakG:F2} g";

            foreach (var (_, meters, lbl) in _distSplits)
                if (lbl.Text == "\u2014" && _distanceMeters >= meters)
                    lbl.Text = $"{elapsed:F3}s";

            double speedKmh = speedMps * 3.6;
            foreach (var (name, startKmh, endKmh, lbl) in _speedSplits)
            {
                if (lbl.Text != "\u2014") continue;
                if (name == "60-130" || name == "100-200")
                {
                    if (speedKmh >= startKmh && !_timeAt60Mph.HasValue) _timeAt60Mph = elapsed;
                    if (speedKmh >= endKmh && _timeAt60Mph.HasValue)
                        lbl.Text = $"{(elapsed - _timeAt60Mph.Value):F3}s";
                }
                else if (speedKmh >= endKmh)
                    lbl.Text = $"{elapsed:F3}s";
            }

            if (SplitQuarter.Text != "\u2014" && TrapSpeed.Text == "\u2014")
            {
                double trapSpeed = _settings.Units == UnitSystem.Imperial
                    ? speedMps * 2.23694 : speedMps * 3.6;
                string unit = _settings.Units == UnitSystem.Imperial ? "mph" : "km/h";
                TrapSpeed.Text = $"{trapSpeed:F0} {unit}";
            }

            lock (_graphLock) { _currentSamples.Add((elapsed, _distanceMeters, speedKmh, accelG)); }
            RedrawGraph();

            if (_distanceMeters >= 1609.34) Finish();
        }

        private void Finish()
        {
            _state = DragState.Finished;
            _timer.Stop();
            RunStatusText.Text = "Finished";
            RunStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x99, 0xFF));
            StageBtn.IsEnabled = true;
            GoBtn.IsEnabled = false;
            StopBtn.IsEnabled = false;

            _runCount++;
            string quarter = SplitQuarter.Text != "\u2014" ? SplitQuarter.Text.TrimEnd('s') : "\u2014";
            string o60 = Split060.Text != "\u2014" ? Split060.Text.TrimEnd('s') : "\u2014";

            if (quarter != "\u2014" && double.TryParse(quarter, out double qt))
            {
                if (_bestQuarterTime < 0 || qt < _bestQuarterTime)
                {
                    _bestQuarterTime = qt;
                    lock (_graphLock) { _bestSamples.Clear(); _bestSamples.AddRange(_currentSamples); }
                }
            }

            HistoryEmpty.Visibility = Visibility.Collapsed;
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            sp.Children.Add(new TextBlock { Text = $"#{_runCount}", FontSize = 11, Opacity = 0.5, Width = 30, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = $"0-60: {o60}s", FontSize = 11, FontWeight = FontWeights.SemiBold, Width = 100, VerticalAlignment = VerticalAlignment.Center });
            string qLabel = _settings.Units == UnitSystem.Imperial ? $"1/4mi: {quarter}s" : $"400m: {quarter}s";
            sp.Children.Add(new TextBlock { Text = qLabel, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0xBB, 0x44)), Width = 100, VerticalAlignment = VerticalAlignment.Center });
            string distLabel = _settings.Units == UnitSystem.Imperial
                ? $"{_distanceMeters * 3.28084 / 5280:F2} mi"
                : $"{_distanceMeters:F0} m";
            sp.Children.Add(new TextBlock { Text = distLabel, FontSize = 11, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center });
            ((StackPanel)HistoryList.Parent).Children.Add(sp);
        }

        private void UpdateUnitLabels()
        {
            bool imperial = _settings.Units == UnitSystem.Imperial;
            if (SpeedUnitText != null)
                SpeedUnitText.Text = imperial ? "mph" : "km/h";

            // Update speed split labels
            if (Label060 != null) Label060.Text = imperial ? "0–60 mph" : "0–100 km/h";
            if (Label0100 != null) Label0100.Text = imperial ? "0–100 mph" : "0–160 km/h";
            if (Label0130 != null) Label0130.Text = imperial ? "0–130 mph" : "0–200 km/h";
            if (Label60130 != null) Label60130.Text = imperial ? "60–130 mph" : "100–200 km/h";
            if (LabelTrap != null) LabelTrap.Text = imperial ? "¼ mile trap" : "400m trap";
        }

        // ── Graph mode switching ──────────────────────────────────────

        private void GraphMode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement btn || btn.Tag is not string tag) return;

            _graphMode = tag switch
            {
                "Time" => GraphMode.Time,
                "Distance" => GraphMode.Distance,
                "Accel" => GraphMode.Accel,
                _ => GraphMode.Time
            };

            // Update button appearances
            BtnGraphTime.Appearance = _graphMode == GraphMode.Time ? ControlAppearance.Primary : ControlAppearance.Secondary;
            BtnGraphDist.Appearance = _graphMode == GraphMode.Distance ? ControlAppearance.Primary : ControlAppearance.Secondary;
            BtnGraphAccel.Appearance = _graphMode == GraphMode.Accel ? ControlAppearance.Primary : ControlAppearance.Secondary;

            GraphTitle.Text = _graphMode switch
            {
                GraphMode.Time => "Speed vs Time",
                GraphMode.Distance => "Speed vs Distance",
                GraphMode.Accel => "Acceleration vs Time",
                _ => "Speed vs Time"
            };

            LegendCurrent.Text = _graphMode == GraphMode.Accel ? "G-force" : "Current";
            LegendCurrent.Foreground = _graphMode == GraphMode.Accel
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0x88, 0x00))
                : new SolidColorBrush(Color.FromRgb(0x44, 0x88, 0xFF));

            RedrawGraph();
        }

        // ── Controls ──────────────────────────────────────────────────

        private void Stage_Click(object sender, RoutedEventArgs e)
        {
            ResetSplits();
            _state = DragState.Staged;
            _distanceMeters = 0;
            _peakG = 0;
            _timeAt60Mph = null;
            lock (_graphLock) { _currentSamples.Clear(); }
            RunStatusText.Text = "Staged";
            RunStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xCC, 0x00));
            StageBtn.IsEnabled = false;
            GoBtn.IsEnabled = true;
            StopBtn.IsEnabled = false;
        }

        private void Go_Click(object sender, RoutedEventArgs e)
        {
            if (_state != DragState.Staged) return;
            _state = DragState.WaitingLaunch;
            RunStatusText.Text = "Waiting for launch\u2026";
            RunStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xCC, 0x00));
            GoBtn.IsEnabled = false;
            StopBtn.IsEnabled = true;
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            if (_state == DragState.Running) Finish();
            else { _state = DragState.Idle; StageBtn.IsEnabled = true; StopBtn.IsEnabled = false; }
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            _state = DragState.Idle;
            _timer.Reset();
            _distanceMeters = 0;
            _peakG = 0;
            _timeAt60Mph = null;
            ResetSplits();
            TimerText.Text = "0.000";
            GForceText.Text = "0.00";
            GForceText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x88, 0x00));
            PeakGText.Text = "\u2014";
            RunStatusText.Text = "Ready";
            RunStatusText.Foreground = new SolidColorBrush(Colors.White);
            StageBtn.IsEnabled = true;
            GoBtn.IsEnabled = false;
            StopBtn.IsEnabled = false;
            lock (_graphLock) { _currentSamples.Clear(); }
            RedrawGraph();
        }

        private void ResetSplits()
        {
            foreach (var (_, _, lbl) in _distSplits) lbl.Text = "\u2014";
            foreach (var (_, _, _, lbl) in _speedSplits) lbl.Text = "\u2014";
            TrapSpeed.Text = "\u2014";
        }

        // ── Graph ────────────────────────────────────────────────────

        private void RedrawGraph()
        {
            GraphCanvas.Children.Clear();
            double w = GraphCanvas.ActualWidth, h = GraphCanvas.ActualHeight;
            if (w < 80 || h < 80) return;
            double pW = w - GL - GR, pH = h - GT - GB;

            List<(double T, double D, double S, double G)> cur, best;
            lock (_graphLock) { cur = _currentSamples.ToList(); best = _bestSamples.ToList(); }

            switch (_graphMode)
            {
                case GraphMode.Time: DrawSpeedVsTime(cur, best, pW, pH); break;
                case GraphMode.Distance: DrawSpeedVsDist(cur, best, pW, pH); break;
                case GraphMode.Accel: DrawAccelVsTime(cur, best, pW, pH); break;
            }
        }

        private void DrawSpeedVsTime(List<(double T, double D, double S, double G)> cur,
            List<(double T, double D, double S, double G)> best, double pW, double pH)
        {
            double maxT = 15, maxS = 150;
            foreach (var src in new[] { cur, best })
                foreach (var (t, _, s, _) in src)
                { if (t > maxT) maxT = Math.Ceiling(t / 5) * 5; if (s > maxS) maxS = Math.Ceiling(s / 30) * 30; }

            double tStep = maxT <= 20 ? 2 : 5;
            for (double t = 0; t <= maxT + 0.1; t += tStep)
            { double x = GL + (t / maxT) * pW; AddLine(x, GT, x, GT + pH, 0x18); AddLabel($"{t:F0}s", x - 6, GT + pH + 4, 9, 0x60); }
            for (double s = 0; s <= maxS + 1; s += 30)
            { double y = GT + pH - (s / maxS) * pH; AddLine(GL, y, GL + pW, y, 0x18); AddLabel($"{s:F0}", 4, y - 7, 9, 0x60); }
            bool imp1 = _settings.Units == UnitSystem.Imperial;
            AddLabel(imp1 ? "mph" : "km/h", 4, GT - 6, 9, 0x60);

            if (best.Count >= 2) DrawCurve(best.Select(p => (p.T, p.S)).ToList(), maxT, maxS, pW, pH, 0x55, 0x77, 0x77, 0x77, 1.5);
            if (cur.Count >= 2) DrawCurve(cur.Select(p => (p.T, p.S)).ToList(), maxT, maxS, pW, pH, 0xFF, 0x44, 0x88, 0xFF, 2.5);

            // Reference line: 60 mph (96.56 km/h) or 100 km/h
            double refSpeed = imp1 ? 96.56 : 100.0;
            string refLabel = imp1 ? "60mph" : "100km/h";
            double yRef = GT + pH - (refSpeed / maxS) * pH;
            if (yRef > GT) { AddLine(GL, yRef, GL + pW, yRef, 0x40, 0x44, 0xBB, 0x44); AddLabel(refLabel, GL + pW + 2, yRef - 7, 8, 0x80, 0x44, 0xBB, 0x44); }
        }

        private void DrawSpeedVsDist(List<(double T, double D, double S, double G)> cur,
            List<(double T, double D, double S, double G)> best, double pW, double pH)
        {
            double maxD = 500, maxS = 150;
            foreach (var src in new[] { cur, best })
                foreach (var (_, d, s, _) in src)
                { if (d > maxD) maxD = Math.Ceiling(d / 100) * 100; if (s > maxS) maxS = Math.Ceiling(s / 30) * 30; }

            bool imp3 = _settings.Units == UnitSystem.Imperial;
            double dStep = maxD <= 1000 ? 100 : 200;
            for (double d = 0; d <= maxD + 1; d += dStep)
            {
                double x = GL + (d / maxD) * pW;
                AddLine(x, GT, x, GT + pH, 0x18);
                if (imp3)
                    AddLabel($"{d * 3.28084:F0}ft", x - 12, GT + pH + 4, 9, 0x60);
                else
                    AddLabel($"{d:F0}m", x - 10, GT + pH + 4, 9, 0x60);
            }
            for (double s = 0; s <= maxS + 1; s += 30)
            { double y = GT + pH - (s / maxS) * pH; AddLine(GL, y, GL + pW, y, 0x18); AddLabel($"{s:F0}", 4, y - 7, 9, 0x60); }
            bool imp2 = _settings.Units == UnitSystem.Imperial;
            AddLabel(imp2 ? "mph" : "km/h", 4, GT - 6, 9, 0x60);

            if (best.Count >= 2) DrawCurve(best.Select(p => (p.D, p.S)).ToList(), maxD, maxS, pW, pH, 0x55, 0x77, 0x77, 0x77, 1.5);
            if (cur.Count >= 2) DrawCurve(cur.Select(p => (p.D, p.S)).ToList(), maxD, maxS, pW, pH, 0xFF, 0x44, 0x88, 0xFF, 2.5);

            // Reference line: 1/4 mi (402.34m) or 400m
            double refDist = imp2 ? 402.34 : 400.0;
            string refDistLabel = imp2 ? "1/4mi" : "400m";
            double xQ = GL + (refDist / maxD) * pW;
            if (xQ < GL + pW) { AddLine(xQ, GT, xQ, GT + pH, 0x40, 0x44, 0xBB, 0x44); AddLabel(refDistLabel, xQ + 2, GT + 4, 8, 0x80, 0x44, 0xBB, 0x44); }
        }

        private void DrawAccelVsTime(List<(double T, double D, double S, double G)> cur,
            List<(double T, double D, double S, double G)> best, double pW, double pH)
        {
            double maxT = 15, maxG = 2;
            foreach (var src in new[] { cur, best })
                foreach (var (t, _, _, g) in src)
                { if (t > maxT) maxT = Math.Ceiling(t / 5) * 5; if (Math.Abs(g) > maxG) maxG = Math.Ceiling(Math.Abs(g) * 2) / 2.0; }
            if (maxG < 1) maxG = 1;

            double tStep = maxT <= 20 ? 2 : 5;
            for (double t = 0; t <= maxT + 0.1; t += tStep)
            { double x = GL + (t / maxT) * pW; AddLine(x, GT, x, GT + pH, 0x18); AddLabel($"{t:F0}s", x - 6, GT + pH + 4, 9, 0x60); }

            // G-force axis (centered at 0)
            double gStep = maxG <= 1 ? 0.25 : 0.5;
            for (double g = -maxG; g <= maxG + 0.01; g += gStep)
            {
                double y = GT + pH / 2 - (g / maxG) * (pH / 2);
                byte alpha = Math.Abs(g) < 0.01 ? (byte)0x40 : (byte)0x18;
                AddLine(GL, y, GL + pW, y, alpha);
                AddLabel($"{g:F1}", 4, y - 7, 9, 0x60);
            }
            AddLabel("g", 4, GT - 6, 9, 0x60);

            // Smooth the G data with 3-point moving average
            var curSmooth = SmoothG(cur.Select(p => (p.T, p.G)).ToList());
            var bestSmooth = SmoothG(best.Select(p => (p.T, p.G)).ToList());

            // Draw zero line
            double y0 = GT + pH / 2;
            AddLine(GL, y0, GL + pW, y0, 0x40);

            if (bestSmooth.Count >= 2) DrawCurveG(bestSmooth, maxT, maxG, pW, pH, 0x55, 0x77, 0x77, 0x77, 1.5);
            if (curSmooth.Count >= 2) DrawCurveG(curSmooth, maxT, maxG, pW, pH, 0xFF, 0xFF, 0x88, 0x00, 2.5);
        }

        private List<(double T, double G)> SmoothG(List<(double T, double G)> pts)
        {
            if (pts.Count < 3) return pts;
            var result = new List<(double, double)> { pts[0] };
            for (int i = 1; i < pts.Count - 1; i++)
            {
                double t = (pts[i - 1].T + pts[i].T + pts[i + 1].T) / 3.0;
                double g = (pts[i - 1].G + pts[i].G + pts[i + 1].G) / 3.0;
                result.Add((t, g));
            }
            result.Add(pts[^1]);
            return result;
        }

        private void DrawCurveG(List<(double T, double G)> pts, double maxT, double maxG, double pW, double pH,
            byte a, byte r, byte g, byte b, double thick)
        {
            if (pts.Count < 2) return;
            var pl = new Polyline { Stroke = new SolidColorBrush(Color.FromArgb(a, r, g, b)), StrokeThickness = thick, StrokeLineJoin = PenLineJoin.Round };
            foreach (var (t, accel) in pts)
            {
                double x = Math.Clamp(GL + (t / maxT) * pW, GL, GL + pW);
                double y = Math.Clamp(GT + pH / 2 - (accel / maxG) * (pH / 2), GT, GT + pH);
                pl.Points.Add(new Point(x, y));
            }
            GraphCanvas.Children.Add(pl);
        }

        private void DrawCurve(List<(double X, double Y)> pts, double maxX, double maxY, double pW, double pH,
            byte a, byte r, byte g, byte b, double thick)
        {
            if (pts.Count < 2) return;
            var pl = new Polyline { Stroke = new SolidColorBrush(Color.FromArgb(a, r, g, b)), StrokeThickness = thick, StrokeLineJoin = PenLineJoin.Round };
            foreach (var (xv, yv) in pts)
            {
                double x = Math.Clamp(GL + (xv / maxX) * pW, GL, GL + pW);
                double y = Math.Clamp(GT + pH - (yv / maxY) * pH, GT, GT + pH);
                pl.Points.Add(new Point(x, y));
            }
            GraphCanvas.Children.Add(pl);
        }

        private void AddLine(double x1, double y1, double x2, double y2, byte a, byte r = 0xFF, byte g = 0xFF, byte b = 0xFF)
            => GraphCanvas.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = new SolidColorBrush(Color.FromArgb(a, r, g, b)), StrokeThickness = 1 });

        private void AddLabel(string text, double x, double y, double size, byte a, byte r = 0xFF, byte g = 0xFF, byte b = 0xFF)
        {
            var tb = new TextBlock { Text = text, FontSize = size, Foreground = new SolidColorBrush(Color.FromArgb(a, r, g, b)) };
            Canvas.SetLeft(tb, x); Canvas.SetTop(tb, y);
            GraphCanvas.Children.Add(tb);
        }
    }
}
