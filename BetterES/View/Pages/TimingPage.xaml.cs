using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using BetterES.Services;
using BetterES.Backends.Keyboard;

namespace BetterES.View.Pages;

public partial class TimingPage : Page
{
    private readonly ConnectionService _connection;
    private KeyboardBackend? _kb => _connection.EsBackend as KeyboardBackend;

    // Debounce timer: delays the bridge send until the user stops dragging
    private readonly DispatcherTimer _sendDebounce;
    private const int DebounceMs = 80;

    public TimingPage(ConnectionService connection)
    {
        InitializeComponent();
        _connection = connection;

        _sendDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMs) };
        _sendDebounce.Tick += (_, _) =>
        {
            _sendDebounce.Stop();
            SendTimingState();
        };

        _connection.RpmChanged += OnRpmChanged;
        _connection.AdvanceChanged += OnAdvanceChanged;
    }

    // Toggles fire immediately (no debounce needed — they're not dragged)
    private void TimingEnabled_Changed(object sender, RoutedEventArgs e) => SendTimingState();
    private void RevLimiter_Changed(object sender, RoutedEventArgs e) => SendTimingState();
    private void IgnitionCut_Changed(object sender, RoutedEventArgs e) => SendTimingState();

    // Sliders: update label immediately, debounce the bridge send
    private void AdvanceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AdvanceValue == null) return;
        AdvanceValue.Text = $"{e.NewValue:F1}°";
        ScheduleSend();
    }

    private void RevLimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RevLimitValue == null) return;
        RevLimitValue.Text = $"{(int)e.NewValue}";
        ScheduleSend();
    }

    private void CutTimeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CutTimeValue == null) return;
        CutTimeValue.Text = $"{(int)e.NewValue}";
        ScheduleSend();
    }

    private void IgnitionCutSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IgnitionCutValue == null) return;
        IgnitionCutValue.Text = $"{(int)e.NewValue}%";
        ScheduleSend();
    }

    // Restart the debounce window on every slider tick
    private void ScheduleSend()
    {
        _sendDebounce.Stop();
        _sendDebounce.Start();
    }

    private void SendTimingState()
    {
        if (_kb == null) return;

        bool timingEnabled = TimingEnabled.IsChecked == true;
        double advanceOffset = AdvanceSlider?.Value ?? 0;
        bool revLimiter = RevLimiterEnabled.IsChecked == true;
        double revLimit = RevLimitSlider?.Value ?? 7000;
        double cutTimeMs = CutTimeSlider?.Value ?? 50;
        bool ignitionCut = IgnitionCutEnabled.IsChecked == true;
        double cutPercent = IgnitionCutSlider?.Value ?? 100;

        _kb.SendTimingCommand(timingEnabled, advanceOffset, revLimiter, revLimit, cutTimeMs, ignitionCut, cutPercent);
    }

    private void OnRpmChanged(double? rpm)
    {
        Dispatcher.Invoke(() =>
        {
            LiveRpm.Text = rpm.HasValue ? $"{(int)rpm.Value}" : "----";
        });
    }

    private void OnAdvanceChanged(double? advance)
    {
        Dispatcher.Invoke(() =>
        {
            LiveAdvance.Text = advance.HasValue ? $"{advance.Value:F1}°" : "--°";
        });
    }
}
