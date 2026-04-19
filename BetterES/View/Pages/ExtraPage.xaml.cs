using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Shapes;
using Path = System.IO.Path;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterES.Backends.Keyboard;
using BetterES.Services;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Wpf.Ui.Controls;

namespace BetterES.View.Pages;

public partial class ExtraPage : Page
{
    private readonly ConnectionService _connection;
    private readonly LogService? _log;

    // MIDI state
    private MidiFile? _midiFile;
    private TempoMap? _tempoMap;
    private List<MidiNoteData> _parsedNotes = new();
    private bool _midiLoaded;
    private int _minMidiNote = 127;
    private int _maxMidiNote;

    // Playback state
    private CancellationTokenSource? _playbackCts;
    private Task? _playbackTask;
    private volatile bool _isPlaying;
    private volatile bool _isPaused;
    private readonly ManualResetEventSlim _pauseEvent = new(true);
    private long _totalDurationUs;

    // Piano roll
    private readonly List<Rectangle> _pianoRollNotes = new();
    private Rectangle? _playheadLine;

    public ExtraPage(ConnectionService connection, LogService log)
    {
        _connection = connection;
        _log = log;

        InitializeComponent();

        MidiTempoSlider.ValueChanged += (_, _) =>
        {
            MidiTempoLabel.Text = $"{MidiTempoSlider.Value:F2}x";
        };

        PianoRollContainer.SizeChanged += (_, _) => DrawPianoRoll();
        PianoRollCanvas.SizeChanged += (_, _) => DrawPianoRoll();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  MIDI File Loading
    // ═══════════════════════════════════════════════════════════════════

    private void LoadMidi_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "MIDI files (*.mid;*.midi)|*.mid;*.midi|All files (*.*)|*.*",
            Title = "Load MIDI File"
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            _midiFile = MidiFile.Read(dlg.FileName);
            _tempoMap = _midiFile.GetTempoMap();

            // Parse and validate notes
            if (!ParseMidiNotes())
            {
                _midiFile = null;
                _tempoMap = null;
                return;
            }

            // Update UI
            MidiFilePath.Text = Path.GetFileName(dlg.FileName);
            _midiLoaded = true;

            // Stats
            var trackCount = _midiFile.GetTrackChunks().Count();
            MidiTrackCount.Text = trackCount.ToString();
            MidiNoteCount.Text = _parsedNotes.Count.ToString();

            // Duration
            var lastNote = _parsedNotes.LastOrDefault();
            if (lastNote != null)
            {
                _totalDurationUs = lastNote.StartUs + lastNote.DurationUs;
                MidiDuration.Text = FormatDuration(_totalDurationUs);
                MidiTimeTotal.Text = FormatDuration(_totalDurationUs);
            }

            // Tempo (BPM from first tempo event)
            var firstTempo = _tempoMap.GetTempoAtTime(new MetricTimeSpan(0));
            double bpm = 60_000_000.0 / firstTempo.MicrosecondsPerQuarterNote;
            MidiTempo.Text = $"{bpm:F0} BPM";

            // Update status
            MidiStatusBadge.Background = new SolidColorBrush(Color.FromRgb(0x40, 0xFF, 0x40));
            MidiStatusText.Text = "Ready";
            MidiStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0xFF, 0x80));

            MidiInfoPanel.Visibility = Visibility.Visible;
            PlayButton.IsEnabled = true;
            StopButton.IsEnabled = true;

            // Draw piano roll
            DrawPianoRoll();

            _log?.Info("MIDI", $"Loaded: {Path.GetFileName(dlg.FileName)} — {trackCount} tracks, {_parsedNotes.Count} notes, {FormatDuration(_totalDurationUs)}");
        }
        catch (Exception ex)
        {
            ShowError($"Failed to load MIDI file:\n{ex.Message}");
            _log?.Info("MIDI", $"Load error: {ex.Message}");
        }
    }

    private bool ParseMidiNotes()
    {
        _parsedNotes.Clear();
        _minMidiNote = 127;
        _maxMidiNote = 0;

        // Check for chords (multiple notes at same time)
        var notesAtTime = new Dictionary<long, int>();
        foreach (var trackChunk in _midiFile!.GetTrackChunks())
        {
            foreach (var timedEvent in trackChunk.GetTimedEvents())
            {
                if (timedEvent.Event is not NoteOnEvent) continue;
                var timeUs = timedEvent.TimeAs<MetricTimeSpan>(_tempoMap!).TotalMicroseconds;
                if (!notesAtTime.ContainsKey(timeUs))
                    notesAtTime[timeUs] = 0;
                notesAtTime[timeUs]++;
            }
        }

        bool hasChords = notesAtTime.Any(kvp => kvp.Value > 1);

        if (hasChords)
        {
            ShowError("This MIDI file contains chords.\nOnly monophonic (single-note) melodies are supported.\nPlease use a single-track melody.");
            return false;
        }

        // Extract notes
        foreach (var trackChunk in _midiFile.GetTrackChunks())
        {
            foreach (var note in trackChunk.GetNotes())
            {
                int noteNumber = note.NoteNumber;
                long startUs = note.TimeAs<MetricTimeSpan>(_tempoMap).TotalMicroseconds;
                long endUs = note.EndTimeAs<MetricTimeSpan>(_tempoMap).TotalMicroseconds;
                long durationUs = endUs - startUs;

                if (noteNumber < _minMidiNote) _minMidiNote = noteNumber;
                if (noteNumber > _maxMidiNote) _maxMidiNote = noteNumber;

                _parsedNotes.Add(new MidiNoteData
                {
                    NoteNumber = noteNumber,
                    StartUs = startUs,
                    DurationUs = durationUs,
                    NoteName = GetNoteName(noteNumber)
                });
            }
        }

        if (_parsedNotes.Count == 0)
        {
            ShowError("No notes found in MIDI file.");
            return false;
        }

        // Sort by start time
        _parsedNotes.Sort((a, b) => a.StartUs.CompareTo(b.StartUs));

        // Transpose to two octaves if needed
        int range = _maxMidiNote - _minMidiNote;
        if (range > 24)
        {
            _log?.Info("MIDI", $"Range is {range} notes — transposing to two octaves");
            foreach (var n in _parsedNotes)
            {
                while (n.NoteNumber < _minMidiNote)
                    n.NoteNumber += 12;
                while (n.NoteNumber > _minMidiNote + 24)
                    n.NoteNumber -= 12;
                n.NoteName = GetNoteName(n.NoteNumber);
            }
            _maxMidiNote = _minMidiNote + 24;
        }

        // Recalculate gaps between notes (fill gaps with rests)
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Playback
    // ═══════════════════════════════════════════════════════════════════

    private void PlayMidi_Click(object sender, RoutedEventArgs e)
    {
        if (!_midiLoaded || _parsedNotes.Count == 0) return;

        if (_isPaused)
        {
            // Resume — unblock the existing PlaybackLoop
            _isPaused = false;
            _pauseEvent.Set();
            PlayButton.IsEnabled = false;
            PauseButton.IsEnabled = true;
            _log?.Info("MIDI", "Resumed playback");
            return;
        }

        if (_isPlaying) return;

        // Check backend connection
        if (_connection.EsState != ConnectionState.Connected || _connection.EsBackend == null)
        {
            ShowError("Engine Simulator must be connected to play MIDI.\nConnect ES first from the Home page.");
            _log?.Info("MIDI", $"Play blocked — EsState={_connection.EsState}, EsBackend={(_connection.EsBackend != null ? "ok" : "null")}");
            return;
        }

        // Ensure previous playback task is fully finished before starting new one
        if (_playbackTask != null && !_playbackTask.IsCompleted)
        {
            _playbackCts?.Cancel();
            _pauseEvent.Set();
            try { _playbackTask.Wait(2000); } catch { }
        }

        _playbackCts = new CancellationTokenSource();
        _isPlaying = true;
        _isPaused = false;
        _pauseEvent.Set();

        PlayButton.IsEnabled = false;
        PauseButton.IsEnabled = true;
        StopButton.IsEnabled = true;
        LoadMidiButton.IsEnabled = false;
        MidiNowPlaying.Visibility = Visibility.Visible;

        MidiStatusBadge.Background = new SolidColorBrush(Color.FromRgb(0x40, 0x80, 0xFF));
        MidiStatusText.Text = "Playing";
        MidiStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0xBF, 0xFF));

        _playbackTask = Task.Run(() => PlaybackLoop(_playbackCts.Token));
    }

    private void PauseMidi_Click(object sender, RoutedEventArgs e)
    {
        if (!_isPlaying || _isPaused) return;

        try
        {
            _isPaused = true;
            _pauseEvent.Reset();

            PlayButton.IsEnabled = true;
            PauseButton.IsEnabled = false;

            // Send idle RPM when paused and RELEASE control
            if (_connection.EsBackend is KeyboardBackend kb) 
            {
                kb.DisableTargetRpm();
            }

            MidiStatusBadge.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0x40));
            MidiStatusText.Text = "Paused";
            MidiStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0x80));

            _log?.Info("MIDI", "Playback paused");
        }
        catch (Exception ex)
        {
            _log?.Info("MIDI", $"Pause error: {ex.Message}");
            ShowError($"Pause failed:\n{ex.Message}");
        }
    }

    private void StopMidi_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
    }

    private void StopPlayback()
    {
        _playbackCts?.Cancel();
        _pauseEvent.Set(); // Unblock if paused

        // Wait for the playback task to finish so it doesn't reset state after us
        var task = _playbackTask;
        if (task != null && !task.IsCompleted)
        {
            try { task.Wait(3000); } catch { }
        }

        _isPlaying = false;
        _isPaused = false;
        _playbackTask = null;

        try
        {
            Dispatcher.Invoke(() =>
            {
                PlayButton.IsEnabled = _midiLoaded;
                PauseButton.IsEnabled = false;
                StopButton.IsEnabled = _midiLoaded;
                LoadMidiButton.IsEnabled = true;
                MidiNowPlaying.Visibility = Visibility.Collapsed;

                if (_midiLoaded)
                {
                    MidiStatusBadge.Background = new SolidColorBrush(Color.FromRgb(0x40, 0xFF, 0x40));
                    MidiStatusText.Text = "Ready";
                    MidiStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0xFF, 0x80));
                }

                MidiProgressSlider.Value = 0;
                MidiTimeElapsed.Text = "0:00";
            });
        }
        catch (Exception ex)
        {
            _log?.Info("MIDI", $"Stop UI update error: {ex.Message}");
        }

        // Reset engine and RELEASE control (always do this at the end of stop)
        try 
        { 
            if (_connection.EsBackend is KeyboardBackend kb)
            {
                kb.DisableTargetRpm();
            }
        } 
        catch { }

        _log?.Info("MIDI", "Playback stopped");
    }

    private async Task PlaybackLoop(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        double tempoMultiplier = 1.0;
        double lowRpm = 1000;
        double highRpm = 8000;
        string lengthMode = "Full";

        _log?.Info("MIDI", $"PlaybackLoop thread starting — ID: {Thread.CurrentThread.ManagedThreadId}");

        // CRITICAL FIX: Pre-read ALL UI values on the UI thread before starting the loop logic
        // Accessing UI elements like Slider.Value or TextBox.Text from a background thread
        // throws an InvalidOperationException and stops the loop.
        try 
        { 
            await Dispatcher.InvokeAsync(() => 
            {
                tempoMultiplier = MidiTempoSlider.Value;
                lowRpm = GetLowRpm();
                highRpm = GetHighRpm();
                if (MidiNoteLengthMode.SelectedItem is ComboBoxItem item)
                    lengthMode = item.Tag?.ToString() ?? "Full";
            }); 
        }
        catch (Exception ex) 
        { 
            _log?.Info("MIDI", $"Dispatcher error pre-reading UI: {ex.Message}"); 
            return;
        }

        _log?.Info("MIDI", $"Config — low={lowRpm:F0} high={highRpm:F0} tempo={tempoMultiplier:F2}x mode={lengthMode} notes={_parsedNotes.Count}");

        try
        {
            foreach (var note in _parsedNotes)
            {
                ct.ThrowIfCancellationRequested();

                // Wait for pause to clear
                _pauseEvent.Wait(ct);
                ct.ThrowIfCancellationRequested();

                // Recalculate tempo multiplier in case user changed it during playback
                try { await Dispatcher.InvokeAsync(() => tempoMultiplier = MidiTempoSlider.Value); } catch { }

                // Calculate adjusted timing
                long adjustedStartUs = (long)(note.StartUs / tempoMultiplier);
                long adjustedDurationUs = (long)(note.DurationUs / tempoMultiplier);

                // Wait until it's time for this note
                long elapsedUs = sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
                long waitUs = adjustedStartUs - elapsedUs;

                if (waitUs > 0)
                {
                    // Sleep in small chunks so we can respond to pause/stop quickly
                    long waitEnd = elapsedUs + waitUs;
                    while (sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency < waitEnd)
                    {
                        ct.ThrowIfCancellationRequested();
                        _pauseEvent.Wait();
                        ct.ThrowIfCancellationRequested();
                        await Task.Delay(1, ct);
                    }
                }

                // Map note to RPM
                double rpm = MapNoteToRpm(note.NoteNumber, lowRpm, highRpm, _minMidiNote, _maxMidiNote);
                SendRpmToEngine(rpm);
                if (note == _parsedNotes[0])
                    _log?.Info("MIDI", $"First note: {note.NoteName} (#{note.NoteNumber}) → {rpm:F0} RPM at {adjustedStartUs / 1_000_000.0:F2}s");

                // Update UI
                try
                {
                    Dispatcher.Invoke(() =>
                    {
                        MidiCurrentNote.Text = note.NoteName;
                        MidiCurrentRpm.Text = $"{rpm:F0}";
                        MidiCurrentOctave.Text = (note.NoteNumber / 12 - 1).ToString();

                        long currentUs = sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
                        MidiTimeElapsed.Text = FormatDuration(currentUs);
                        if (_totalDurationUs > 0)
                            MidiProgressSlider.Value = (double)currentUs / _totalDurationUs * 100.0;

                        // Highlight active note on piano roll
                        HighlightPianoRollNote(note);
                    });
                }
                catch { }

                // Wait for note duration
                long noteEndUs = adjustedStartUs + adjustedDurationUs;

                // Apply note length mode
                long actualDurationUs = lengthMode switch
                {
                    "Staccato" => Math.Min(adjustedDurationUs, 100_000), // Max 100ms blip
                    "Legato" => (long)(adjustedDurationUs * 1.1),       // 110% for overlap
                    _ => adjustedDurationUs
                };

                long noteWaitEnd = sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency + actualDurationUs;
                while (sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency < noteWaitEnd)
                {
                    ct.ThrowIfCancellationRequested();
                    _pauseEvent.Wait();
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(1, ct);
                }
            }

            // Playback complete — return to idle RPM and release control
            try 
            { 
                if (_connection.EsBackend is KeyboardBackend kbFin) kbFin.DisableTargetRpm();
            } 
            catch { }

            try
            {
                Dispatcher.Invoke(() =>
                {
                    MidiStatusBadge.Background = new SolidColorBrush(Color.FromRgb(0x40, 0xFF, 0x40));
                    MidiStatusText.Text = "Done";
                    MidiStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0xFF, 0x80));
                    MidiCurrentNote.Text = "—";
                    MidiCurrentRpm.Text = "—";
                    MidiCurrentOctave.Text = "—";
                });
            }
            catch { }

            _log?.Info("MIDI", $"Playback finished — played {_parsedNotes.Count} notes in {sw.ElapsedMilliseconds}ms");
        }
        catch (OperationCanceledException)
        {
            // Normal stop
        }
        catch (Exception ex)
        {
            try { Dispatcher.Invoke(() => ShowError($"Playback error:\n{ex.Message}")); } catch { }
            _log?.Info("MIDI", $"Playback error: {ex.Message}");
        }
        finally
        {
            // Only update UI if StopPlayback hasn't already done it
            // (checked by seeing if _isPlaying is still true — StopPlayback sets it to false)
            if (_isPlaying)
            {
                _isPlaying = false;
                _isPaused = false;
                try
                {
                    Dispatcher.Invoke(() =>
                    {
                        PlayButton.IsEnabled = _midiLoaded;
                        PauseButton.IsEnabled = false;
                        LoadMidiButton.IsEnabled = true;
                    });
                }
                catch { }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Engine Control
    // ═══════════════════════════════════════════════════════════════════

    private void SendRpmToEngine(double rpm)
    {
        if (_connection.EsBackend is KeyboardBackend kb)
        {
            if (!kb.SendTargetRpm(rpm))
            {
                _log?.Info("MIDI", $"SendTargetRpm({rpm:F0}) failed — MMF bridge not connected?");
            }
        }
        else
        {
            _log?.Info("MIDI", $"SendRpmToEngine skipped — EsBackend is null or not KeyboardBackend (state={_connection.EsState})");
        }
    }

    private double MapNoteToRpm(int midiNote, double minRpm, double maxRpm, int minNote, int maxNote)
    {
        if (maxNote == minNote) return minRpm;

        // Linear interpolation (reverted from logarithmic as per user preference)
        double normalized = (double)(midiNote - minNote) / (double)(maxNote - minNote);
        return minRpm + normalized * (maxRpm - minRpm);
    }

    private double GetLowRpm()
    {
        if (double.TryParse(MidiLowRpm.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double v))
            return Math.Max(100, v);
        return 1000;
    }

    private double GetHighRpm()
    {
        if (double.TryParse(MidiHighRpm.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double v))
            return Math.Max(GetLowRpm() + 500, v);
        return 8000;
    }

    private void MidiLowRpm_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (MidiHighRpm == null) return;
        // Auto-set high RPM to double the low RPM (like ES-Studio)
        if (double.TryParse(MidiLowRpm.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double low))
        {
            MidiHighRpm.Text = (low * 8).ToString(CultureInfo.InvariantCulture);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Piano Roll Visualization
    // ═══════════════════════════════════════════════════════════════════

    private void DrawPianoRoll()
    {
        PianoRollCanvas.Children.Clear();
        _pianoRollNotes.Clear();

        if (_parsedNotes.Count == 0 || _totalDurationUs == 0) return;

        // Use container width so roll stretches with available space.
        double canvasWidth = PianoRollContainer.ActualWidth;
        double canvasHeight = PianoRollContainer.ActualHeight;

        if (canvasWidth <= 0 || canvasHeight <= 0)
        {
            canvasWidth = 500;
            canvasHeight = 60;
        }

        // Ensure the canvas size matches container for consistent measurements
        PianoRollCanvas.Width = canvasWidth;
        PianoRollCanvas.Height = canvasHeight;

        int noteRange = Math.Max(1, _maxMidiNote - _minMidiNote);
        double noteHeight = canvasHeight / (noteRange + 1);

        foreach (var note in _parsedNotes)
        {
            double x = (double)note.StartUs / _totalDurationUs * canvasWidth;
            double w = Math.Max(2, (double)note.DurationUs / _totalDurationUs * canvasWidth);
            double y = canvasHeight - ((note.NoteNumber - _minMidiNote + 1) * noteHeight);

            var rect = new Rectangle
            {
                Width = w,
                Height = Math.Max(2, noteHeight - 1),
                Fill = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA)),
                RadiusX = 1,
                RadiusY = 1,
                Opacity = 0.6,
                Tag = note
            };

            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            PianoRollCanvas.Children.Add(rect);
            _pianoRollNotes.Add(rect);
        }

        // Playhead line
        _playheadLine = new Rectangle
        {
            Width = 2,
            Height = canvasHeight,
            Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x40, 0x40))
        };
        Canvas.SetLeft(_playheadLine, 0);
        Canvas.SetTop(_playheadLine, 0);
        PianoRollCanvas.Children.Add(_playheadLine);
    }

    private void HighlightPianoRollNote(MidiNoteData note)
    {
        // Reset all notes to default opacity
        foreach (var rect in _pianoRollNotes)
        {
            rect.Opacity = 0.4;
            rect.Fill = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA));
        }

        // Find and highlight the current note
        foreach (var rect in _pianoRollNotes)
        {
            if (rect.Tag is MidiNoteData n && n == note)
            {
                rect.Opacity = 1.0;
                rect.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xD0, 0x40));

                // Move playhead — use ActualWidth so it matches current visual size after resizes.
                if (_playheadLine != null && _totalDurationUs > 0)
                {
                    double canvasWidth = PianoRollCanvas.ActualWidth;
                    if (canvasWidth <= 0) canvasWidth = PianoRollCanvas.Width;
                    double x = (double)note.StartUs / _totalDurationUs * canvasWidth;
                    Canvas.SetLeft(_playheadLine, x);
                }
                break;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static string GetNoteName(int midiNote)
    {
        string[] names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        int octave = midiNote / 12 - 1;
        string name = names[midiNote % 12];
        return $"{name}{octave}";
    }

    private static string FormatDuration(long microseconds)
    {
        long totalSeconds = microseconds / 1_000_000;
        int minutes = (int)(totalSeconds / 60);
        int seconds = (int)(totalSeconds % 60);
        return $"{minutes}:{seconds:D2}";
    }

    private void ShowError(string message)
    {
        var card = this.FindName("MidiStatusBadge") as Border;
        if (card != null)
            card.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x40, 0x40));
        MidiStatusText.Text = "Error";
        MidiStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x80, 0x80));

        System.Windows.MessageBox.Show(message, "MIDI Player", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
    }
}

// ═══════════════════════════════════════════════════════════════════════
//  Data Model
// ═══════════════════════════════════════════════════════════════════════

internal class MidiNoteData
{
    public int NoteNumber { get; set; }
    public long StartUs { get; set; }
    public long DurationUs { get; set; }
    public string NoteName { get; set; } = "";
}
