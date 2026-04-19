using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterES.Services;

namespace BetterES.View.Controls
{
    public partial class AirflowGauge : UserControl
    {
        public static readonly DependencyProperty CurrentFlowProperty =
            DependencyProperty.Register(nameof(CurrentFlow), typeof(double), typeof(AirflowGauge),
                new PropertyMetadata(0.0, OnChanged));

        public static readonly DependencyProperty MaxFlowProperty =
            DependencyProperty.Register(nameof(MaxFlow), typeof(double), typeof(AirflowGauge),
                new PropertyMetadata(6.0, OnChanged));

        public static readonly DependencyProperty UnitProperty =
            DependencyProperty.Register(nameof(Unit), typeof(UnitSystem), typeof(AirflowGauge),
                new PropertyMetadata(UnitSystem.Metric, OnChanged));

        private double _currentFlowRaw; // SCFM from ES hook (direct, like ES-Studio's airSCFM)
        private double _maxFlowRaw;

        public double CurrentFlow
        {
            get => (double)GetValue(CurrentFlowProperty);
            set => SetValue(CurrentFlowProperty, value);
        }

        public double MaxFlow
        {
            get => (double)GetValue(MaxFlowProperty);
            set => SetValue(MaxFlowProperty, value);
        }

        public UnitSystem Unit
        {
            get => (UnitSystem)GetValue(UnitProperty);
            set => SetValue(UnitProperty, value);
        }

        public AirflowGauge()
        {
            InitializeComponent();
            Loaded += (s, e) => UpdateDisplay();
        }

        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AirflowGauge g)
            {
                if (e.Property == CurrentFlowProperty) g._currentFlowRaw = (double)e.NewValue;
                else if (e.Property == MaxFlowProperty) g._maxFlowRaw = (double)e.NewValue;
                else if (e.Property == UnitProperty) { /* SCFM is always shown */ }
                g.UpdateDisplay();
            }
        }

        private void UpdateDisplay()
        {
            // The hook (C++ bridge) now handles the m³/s to SCFM conversion directly.
            // Result is stored in State::intakeFlowRate as SCFM.
            double displayFlow = _currentFlowRaw;
            double displayMax = _maxFlowRaw;

            // Format: whole numbers for large values, 1 decimal for small
            if (displayFlow >= 100)
                FlowTextBlock.Text = $"{displayFlow:F0}";
            else
                FlowTextBlock.Text = $"{displayFlow:F1}";

            UnitTextBlock.Text = "SCFM";

            double barWidth = ActualWidth > 0 ? ActualWidth - 40 : 360;
            if (barWidth <= 0) barWidth = 360;

            // Match ES-Studio: use 500/2000/10000 max options
            double visualMax = ComputeVisualMax(displayMax);

            double flowPercent = visualMax > 0 ? (displayFlow / visualMax) * 100.0 : 0;
            flowPercent = Math.Clamp(flowPercent, 0, 100);
            ProgressBar.Width = (flowPercent / 100.0) * barWidth;

            Color barStart, barEnd;
            if (flowPercent >= 80)
            {
                barStart = Color.FromRgb(0xDD, 0x22, 0x22);
                barEnd = Color.FromRgb(0xFF, 0x44, 0x44);
            }
            else if (flowPercent >= 60)
            {
                barStart = Color.FromRgb(0xFF, 0x88, 0x00);
                barEnd = Color.FromRgb(0xFF, 0xBB, 0x00);
            }
            else
            {
                barStart = Color.FromRgb(0x22, 0x88, 0xFF);
                barEnd = Color.FromRgb(0x44, 0xBB, 0xFF);
            }
            BarColorStart.Color = barStart;
            BarColorEnd.Color = barEnd;

            UpdateTickMarks(barWidth, visualMax);
        }

        private double ComputeVisualMax(double max)
        {
            // Fixed max of 1200 SCFM
            return 1200;
        }

        private void UpdateTickMarks(double barWidth, double visualMax)
        {
            var ticks = new List<TickMark>();
            if (visualMax <= 0) return;

            // Fixed interval of 100 for 1200 max
            double interval = 100;

            for (double val = interval; val <= visualMax + 0.001; val += interval)
            {
                double fraction = Math.Min(val / visualMax, 1.0);
                string label;
                if (val >= 1000)
                    label = $"{val / 1000:0.#}k";
                else
                    label = val.ToString("0");

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
