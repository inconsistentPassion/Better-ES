using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BetterES.View.Controls
{
    public class BoostTickMark
    {
        public string Label { get; set; } = "";
        public double Position { get; set; }
    }

    public partial class BoostGauge : UserControl
    {
        public static readonly DependencyProperty CurrentBoostProperty =
            DependencyProperty.Register(nameof(CurrentBoost), typeof(double), typeof(BoostGauge),
                new PropertyMetadata(0.0, OnBoostChanged));

        public static readonly DependencyProperty MaxBoostProperty =
            DependencyProperty.Register(nameof(MaxBoost), typeof(double), typeof(BoostGauge),
                new PropertyMetadata(2.0, OnBoostChanged));

        public static readonly DependencyProperty WastegateProperty =
            DependencyProperty.Register(nameof(Wastegate), typeof(double), typeof(BoostGauge),
                new PropertyMetadata(1.5, OnBoostChanged));

        private double _currentBoost;
        private double _maxBoost = 2.0;
        private double _wastegate = 1.5;

        public double CurrentBoost
        {
            get => (double)GetValue(CurrentBoostProperty);
            set => SetValue(CurrentBoostProperty, value);
        }

        public double MaxBoost
        {
            get => (double)GetValue(MaxBoostProperty);
            set => SetValue(MaxBoostProperty, value);
        }

        public double Wastegate
        {
            get => (double)GetValue(WastegateProperty);
            set => SetValue(WastegateProperty, value);
        }

        public BoostGauge()
        {
            InitializeComponent();
            Loaded += (s, e) => UpdateDisplay();
        }

        private static void OnBoostChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is BoostGauge gauge)
            {
                if (e.Property == CurrentBoostProperty) gauge._currentBoost = (double)e.NewValue;
                else if (e.Property == MaxBoostProperty) gauge._maxBoost = (double)e.NewValue;
                else if (e.Property == WastegateProperty) gauge._wastegate = (double)e.NewValue;
                gauge.UpdateDisplay();
            }
        }

        private void UpdateDisplay()
        {
            double boost = _currentBoost;
            double max = _maxBoost;
            double wg = _wastegate;
            double psi = boost * 14.5038;

            BoostBarText.Text = $"{boost:F2}";
            BoostPsiText.Text = $"{psi:F1} psi";

            double barWidth = ActualWidth > 0 ? ActualWidth - 40 : 360;
            if (barWidth <= 0) barWidth = 360;

            // Visual max: maxBoost * 1.2 so the bar has headroom
            double visualMax = Math.Max(max * 1.2, 0.5);

            // Bar fill — boost can be negative (vacuum), so offset
            double minDisplay = -0.5; // show vacuum down to -0.5 bar
            double range = visualMax - minDisplay;
            double boostPercent = ((boost - minDisplay) / range) * 100.0;
            boostPercent = Math.Clamp(boostPercent, 0, 100);
            ProgressBar.Width = (boostPercent / 100.0) * barWidth;

            // Wastegate tick
            double wgPercent = ((wg - minDisplay) / range) * 100.0;
            WastegateTick.Margin = new Thickness((wgPercent / 100.0) * barWidth - 1, 0, 0, 0);

            // Bar color based on boost level
            Color barStart, barEnd;
            string pillColorHex;
            if (boost > wg * 1.05) // Over wastegate
            {
                barStart = Color.FromRgb(0xDD, 0x22, 0x22);
                barEnd = Color.FromRgb(0xFF, 0x44, 0x44);
                pillColorHex = "#FFFF4444";
            }
            else if (boost > wg * 0.8) // Near wastegate
            {
                barStart = Color.FromRgb(0xFF, 0x88, 0x00);
                barEnd = Color.FromRgb(0xFF, 0xBB, 0x00);
                pillColorHex = "#FFFF8800";
            }
            else if (boost > 0) // Building boost
            {
                barStart = Color.FromRgb(0x22, 0xCC, 0x44);
                barEnd = Color.FromRgb(0x44, 0xFF, 0x66);
                pillColorHex = "#FF44FF88";
            }
            else // Vacuum / no boost
            {
                barStart = Color.FromRgb(0x44, 0x88, 0xFF);
                barEnd = Color.FromRgb(0x66, 0xAA, 0xFF);
                pillColorHex = "#FF4488FF";
            }
            BarColorStart.Color = barStart;
            BarColorEnd.Color = barEnd;

            // Update pill colors
            PillBorder.BorderBrush = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(pillColorHex.Replace("FF", "25")));
            PillColorTop.Color = (Color)ColorConverter.ConvertFromString(
                pillColorHex.Replace("#FF", "#18"));
            PillColorBot.Color = (Color)ColorConverter.ConvertFromString(
                pillColorHex.Replace("#FF", "#08"));
            PillText.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(pillColorHex));

            UpdateTickMarks(barWidth, visualMax, minDisplay, range);
        }

        private void UpdateTickMarks(double barWidth, double visualMax, double minDisplay, double range)
        {
            var ticks = new List<BoostTickMark>();

            // Tick from -0.5 to visualMax in 0.5 bar steps
            double step = 0.5;
            for (double bar = Math.Ceiling(minDisplay / step) * step; bar <= visualMax; bar += step)
            {
                double fraction = (bar - minDisplay) / range;
                if (fraction < 0 || fraction > 1) continue;

                string label = bar == 0 ? "0" : $"{bar:F1}";
                ticks.Add(new BoostTickMark { Label = label, Position = fraction * barWidth });
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
