using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterES.Services;

namespace BetterES.View.Controls
{
    public partial class SpeedGauge : UserControl
    {
        public static readonly DependencyProperty CurrentSpeedProperty =
            DependencyProperty.Register(nameof(CurrentSpeed), typeof(double), typeof(SpeedGauge),
                new PropertyMetadata(0.0, OnChanged));

        public static readonly DependencyProperty MaxSpeedProperty =
            DependencyProperty.Register(nameof(MaxSpeed), typeof(double), typeof(SpeedGauge),
                new PropertyMetadata(300.0, OnChanged));

        public static readonly DependencyProperty UnitProperty =
            DependencyProperty.Register(nameof(Unit), typeof(UnitSystem), typeof(SpeedGauge),
                new PropertyMetadata(UnitSystem.Metric, OnChanged));

        private double _currentSpeedMps;
        private double _maxSpeedKmh;
        private UnitSystem _unit;

        public double CurrentSpeed
        {
            get => (double)GetValue(CurrentSpeedProperty);
            set => SetValue(CurrentSpeedProperty, value);
        }

        public double MaxSpeed
        {
            get => (double)GetValue(MaxSpeedProperty);
            set => SetValue(MaxSpeedProperty, value);
        }

        public UnitSystem Unit
        {
            get => (UnitSystem)GetValue(UnitProperty);
            set => SetValue(UnitProperty, value);
        }

        public SpeedGauge()
        {
            InitializeComponent();
            Loaded += (s, e) => UpdateDisplay();
        }

        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SpeedGauge g)
            {
                if (e.Property == CurrentSpeedProperty) g._currentSpeedMps = (double)e.NewValue;
                else if (e.Property == MaxSpeedProperty) g._maxSpeedKmh = (double)e.NewValue;
                else if (e.Property == UnitProperty) g._unit = (UnitSystem)e.NewValue;
                g.UpdateDisplay();
            }
        }

        private void UpdateDisplay()
        {
            bool imperial = _unit == UnitSystem.Imperial;
            // Input is m/s from ES hook
            double speedKmh = _currentSpeedMps * 3.6;
            double displaySpeed = imperial ? speedKmh * 0.621371 : speedKmh;

            SpeedTextBlock.Text = $"{displaySpeed:F0}";
            UnitTextBlock.Text = imperial ? "mph" : "km/h";

            double barWidth = ActualWidth > 0 ? ActualWidth - 40 : 360;
            if (barWidth <= 0) barWidth = 360;

            var ticks = GetTickValues(imperial);
            double visualFraction = SpeedToVisualFraction(displaySpeed, ticks);

            ProgressBar.Width = visualFraction * barWidth;

            Color barStart, barEnd;
            double speedPercent = visualFraction * 100.0;
            if (speedPercent >= 80)
            {
                barStart = Color.FromRgb(0xDD, 0x22, 0x22);
                barEnd = Color.FromRgb(0xFF, 0x44, 0x44);
            }
            else if (speedPercent >= 60)
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

            UpdateTickMarks(barWidth, ticks);
        }

        private List<double> GetTickValues(bool isImperial)
        {
            var result = new List<double>();
            if (isImperial)
            {
                // mph: 0-60 (step 30), 60-310 (step 50)
                for (double v = 0; v <= 60; v += 30) result.Add(v);
                for (double v = 110; v <= 310; v += 50) result.Add(v);
            }
            else
            {
                // km/h: 0-120 (step 30), 120-420 (step 50)
                for (double v = 0; v <= 120; v += 30) result.Add(v);
                for (double v = 170; v <= 420; v += 50) result.Add(v);
            }
            return result;
        }

        private double SpeedToVisualFraction(double speed, List<double> ticks)
        {
            if (speed <= 0) return 0;
            int numSegments = ticks.Count - 1;
            if (numSegments <= 0) return 0;

            for (int i = 0; i < numSegments; i++)
            {
                double start = ticks[i];
                double end = ticks[i + 1];
                if (speed >= start && speed <= end)
                {
                    double segmentFrac = (speed - start) / (end - start);
                    return (i + segmentFrac) / numSegments;
                }
            }

            if (speed > ticks[ticks.Count - 1]) return 1.0;
            return 0;
        }

        private void UpdateTickMarks(double barWidth, List<double> ticks)
        {
            int numSegments = ticks.Count - 1;
            if (numSegments <= 0) return;

            TickMarks.Items.Clear();
            for (int i = 1; i < ticks.Count; i++) // Start from first tick after 0
            {
                double visualFraction = (double)i / numSegments;
                double position = visualFraction * barWidth;
                string label = ticks[i].ToString("0");

                var tickData = new TickMark { Label = label, Position = position };
                var item = new ContentPresenter
                {
                    Content = tickData,
                    ContentTemplate = (DataTemplate)FindResource("TickMarkTemplate")
                };
                TickMarks.Items.Add(item);

                item.Loaded += (s, e) =>
                {
                    Canvas.SetLeft(item, tickData.Position - 4);
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
