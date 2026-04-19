using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BetterES.View.Controls
{
    public partial class AfrGauge : UserControl
    {
        public static readonly DependencyProperty CurrentAfrProperty =
            DependencyProperty.Register(nameof(CurrentAfr), typeof(double), typeof(AfrGauge),
                new PropertyMetadata(0.0, OnAfrChanged));

        public static readonly DependencyProperty TargetAfrProperty =
            DependencyProperty.Register(nameof(TargetAfr), typeof(double), typeof(AfrGauge),
                new PropertyMetadata(14.7, OnAfrChanged));

        private double _currentAfr;
        private double _targetAfr;
        private const double AfrMin = 0.0;
        private const double AfrMax = 50.0;

        public double CurrentAfr
        {
            get => (double)GetValue(CurrentAfrProperty);
            set => SetValue(CurrentAfrProperty, value);
        }

        public double TargetAfr
        {
            get => (double)GetValue(TargetAfrProperty);
            set => SetValue(TargetAfrProperty, value);
        }

        public AfrGauge()
        {
            InitializeComponent();
            _currentAfr = 0;
            _targetAfr = 14.7;
            Loaded += (s, e) => UpdateDisplay();
        }

        private static void OnAfrChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AfrGauge gauge)
            {
                if (e.Property == CurrentAfrProperty)
                    gauge._currentAfr = (double)e.NewValue;
                else if (e.Property == TargetAfrProperty)
                    gauge._targetAfr = (double)e.NewValue;
                gauge.UpdateDisplay();
            }
        }

        private void UpdateDisplay()
        {
            AfrTextBlock.Text = _currentAfr > 0 ? $"{_currentAfr:F1}" : "—";

            double barWidth = ActualWidth > 0 ? ActualWidth - 40 : 360;
            if (barWidth <= 0) barWidth = 360;

            double range = AfrMax - AfrMin;

            // Tick position based on current AFR
            double afrClamped = Math.Clamp(_currentAfr, AfrMin, AfrMax);
            double fraction = (afrClamped - AfrMin) / range;
            AfrTick.Margin = new Thickness(fraction * barWidth - 1, 0, 0, 0);

            // Tick color based on position (background gradient)
            // Rich zone: 0-12 AFR (red), Transition: 12-14 (orange), Ideal: 14-16 (green), Lean: 16+ (red)
            Color tickColor;
            if (_currentAfr < 12)
            {
                // Too rich — red
                tickColor = Color.FromRgb(0xFF, 0x44, 0x44);
            }
            else if (_currentAfr < 14)
            {
                // Slightly rich — orange
                tickColor = Color.FromRgb(0xFF, 0x88, 0x00);
            }
            else if (_currentAfr >= 14 && _currentAfr <= 16)
            {
                // Ideal range — green
                tickColor = Color.FromRgb(0x44, 0xFF, 0x88);
            }
            else if (_currentAfr > 16 && _currentAfr <= 20)
            {
                // Slightly lean — orange
                tickColor = Color.FromRgb(0xFF, 0xBB, 0x00);
            }
            else
            {
                // Too lean — red
                tickColor = Color.FromRgb(0xFF, 0x44, 0x44);
            }
            AfrTickColor.Color = tickColor;

            UpdateTickMarks(barWidth, range);
        }

        private void UpdateTickMarks(double barWidth, double range)
        {
            var ticks = new List<TickMark>();

            // Every 5 AFR for the 0-50 range (matching ES-Studio's wider gauge)
            int interval = 5;
            for (double val = interval; val <= AfrMax + 0.01; val += interval)
            {
                double fraction = Math.Min((val - AfrMin) / range, 1.0);
                string label = val.ToString("0");
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
