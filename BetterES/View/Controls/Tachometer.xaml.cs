using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BetterES.View.Controls
{
    public class TickMark
    {
        public string Label { get; set; } = "";
        public double Position { get; set; }
    }

    public partial class Tachometer : UserControl
    {
        public static readonly DependencyProperty CurrentRpmProperty =
            DependencyProperty.Register(nameof(CurrentRpm), typeof(double), typeof(Tachometer),
                new PropertyMetadata(0.0, OnRpmChanged));

        public static readonly DependencyProperty MaxRpmProperty =
            DependencyProperty.Register(nameof(MaxRpm), typeof(double), typeof(Tachometer),
                new PropertyMetadata(8000.0, OnRpmChanged));

        public static readonly DependencyProperty CurrentGearProperty =
            DependencyProperty.Register(nameof(CurrentGear), typeof(int), typeof(Tachometer),
                new PropertyMetadata(0, OnGearChanged));

        private double _currentRpm;
        private double _maxRpm;
        private int _currentGear;

        public double CurrentRpm
        {
            get => (double)GetValue(CurrentRpmProperty);
            set => SetValue(CurrentRpmProperty, value);
        }

        public double MaxRpm
        {
            get => (double)GetValue(MaxRpmProperty);
            set => SetValue(MaxRpmProperty, value);
        }

        public int CurrentGear
        {
            get => (int)GetValue(CurrentGearProperty);
            set => SetValue(CurrentGearProperty, value);
        }

        public Tachometer()
        {
            InitializeComponent();
            _currentRpm = 0;
            _maxRpm = 8000;
            _currentGear = 0;
            Loaded += (s, e) => UpdateDisplay();
        }

        private static void OnRpmChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is Tachometer tach)
            {
                if (e.Property == CurrentRpmProperty)
                    tach._currentRpm = (double)e.NewValue;
                else if (e.Property == MaxRpmProperty)
                    tach._maxRpm = (double)e.NewValue;

                tach.UpdateDisplay();
            }
        }

        private static void OnGearChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is Tachometer tach)
            {
                tach._currentGear = (int)e.NewValue;
                tach.UpdateDisplay();
            }
        }

        private void UpdateDisplay()
        {
            RpmTextBlock.Text = $"{_currentRpm:F0}";
            // Correct mapping: -1=R, 0=N, 1+=gear
            if (_currentGear == 0) GearTextBlock.Text = "N";
            else if (_currentGear == -1) GearTextBlock.Text = "R";
            else if (_currentGear > 0) GearTextBlock.Text = _currentGear.ToString();
            else GearTextBlock.Text = "—";

            double barWidth = ActualWidth > 0 ? ActualWidth - 40 : 360;
            if (barWidth <= 0) barWidth = 360;

            // Shared visual max — redline sits at 87.5% of this value
            double visualMax = ComputeVisualMax();

            // Bar fill
            double rpmPercent = visualMax > 0 ? (_currentRpm / visualMax) * 100.0 : 0;
            rpmPercent = Math.Clamp(rpmPercent, 0, 100);
            ProgressBar.Width = (rpmPercent / 100.0) * barWidth;

            // Redline tick & zone
            double redlinePercent = (_maxRpm / visualMax) * 100.0;
            RedlineTick.Margin = new Thickness((redlinePercent / 100.0) * barWidth - 1, 0, 0, 0);
            RedlineZone.Width = barWidth - (redlinePercent / 100.0) * barWidth;

            // Bar color
            Color barStart, barEnd;
            if (rpmPercent >= 82)
            {
                barStart = Color.FromRgb(0xDD, 0x22, 0x22);
                barEnd = Color.FromRgb(0xFF, 0x44, 0x44);
            }
            else if (rpmPercent >= 72)
            {
                barStart = Color.FromRgb(0xFF, 0x88, 0x00);
                barEnd = Color.FromRgb(0xFF, 0xBB, 0x00);
            }
            else
            {
                barStart = Color.FromRgb(0x22, 0x66, 0xFF);
                barEnd = Color.FromRgb(0x44, 0x88, 0xFF);
            }
            BarColorStart.Color = barStart;
            BarColorEnd.Color = barEnd;

            UpdateTickMarks(barWidth, visualMax);
        }

        /// <summary>
        /// Computes visual max RPM so that redline = 87.5% of the bar,
        /// rounded up to a clean tick interval (500/1000/2000/2500/5000).
        /// </summary>
        private double ComputeVisualMax()
        {
            double raw = _maxRpm / 0.875;
            if (raw < 5000) raw = 5000;

            int[] candidates = { 500, 1000, 2000, 2500, 5000 };
            int interval = 1000;
            foreach (var c in candidates)
            {
                int count = (int)(raw / c);
                if (count >= 5 && count <= 12) { interval = c; break; }
            }
            if ((int)(raw / interval) < 5 || (int)(raw / interval) > 12)
                interval = Math.Max(500, (int)Math.Round(raw / 8.0 / 500.0) * 500);

            return Math.Ceiling(raw / interval) * interval;
        }

        /// <summary>
        /// Places tick marks on the canvas. Each tick label = its actual RPM / 1000.
        /// Recalculates whenever MaxRpm changes.
        /// </summary>
        private void UpdateTickMarks(double barWidth, double visualMax)
        {
            var ticks = new List<TickMark>();
            if (_maxRpm <= 0 || visualMax <= 0) return;

            // Determine interval from the visualMax
            int[] candidates = { 500, 1000, 2000, 2500, 5000 };
            int interval = 1000;
            foreach (var c in candidates)
            {
                int count = (int)(visualMax / c);
                if (count >= 5 && count <= 12) { interval = c; break; }
            }

            for (double rpm = 0; rpm <= visualMax + 0.5; rpm += interval)
            {
                double fraction = Math.Min(rpm / visualMax, 1.0);

                string label;
                if (interval >= 1000 && rpm % 1000 == 0)
                    label = (rpm / 1000).ToString("0");
                else
                    label = (rpm / 1000.0).ToString("0.#");

                ticks.Add(new TickMark { Label = label, Position = fraction * barWidth });
            }

            TickMarks.Items.Clear();
            foreach (var tick in ticks)
            {
                var item = new ContentPresenter
                {
                    Content = tick,
                    ContentTemplate = (DataTemplate)FindResource("TickMarkTemplate")
                };
                TickMarks.Items.Add(item);
                item.Loaded += (s, e) =>
                {
                    Canvas.SetLeft(item, tick.Position - 4);
                    Canvas.SetTop(item, 0);
                };
            }
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            UpdateDisplay();
        }
    }
}
