using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace BetterES.Services;

/// <summary>
/// Self-contained MIDI playback engine ported from ES-Studio.
/// Supports load, play, true pause/resume, and stop.
/// Notes from all tracks are merged into a single timeline.
/// Timing is Stopwatch-based for sub-millisecond accuracy.
/// </summary>
public class MidiPlayer
{
    // ── State ──────────────────────────────────────────────────────────────
    public enum PlaybackState { Stopped, Playing, Paused }
    public PlaybackState State { get; private set; } = PlaybackState.Stopped;

    public bool IsLoaded => _notes != null && _notes.Count > 0;
    public string LoadedFileName { get; private set; } = string.Empty;
    public int NoteCount { get; private set; }
    public int TrackCount { get; private set; }
    public long TotalDurationUs { get; private set; }
    public double BPM { get; private set; }

    // ── Playback parameters ────────────────────────────────────────────────
    public double LowRPM  { get; set; } = 1000.0;
    public double HighRPM { get; set; } = 8000.0;
    /// <summary>Tempo multiplier: 0.25 = quarter speed, 2.0 = double speed.</summary>
    public double TempoMultiplier { get; set; } = 1.0;

    // ── Note range (for piano roll) ────────────────────────────────────────
    public int MinNote { get; private set; }
    public int MaxNote { get; private set; }

    // ── Callbacks ─────────────────────────────────────────────────────────
    /// <summary>Called on the playback thread each time the target RPM changes.</summary>
    public Action<double>? OnRpmChanged;
    /// <summary>Called on the playback thread each tick with (noteNumber, noteStartUs, elapsedUs).</summary>
    public Action<int, long, long>? OnNoteTick;
    /// <summary>Called on the playback thread when playback ends naturally.</summary>
    public Action? OnPlaybackEnded;

    // ── Internals ─────────────────────────────────────────────────────────
    private List<NoteEvent> _notes = new();
    private int _minNote;
    private int _maxNote;

    private Thread? _thread;
    private volatile bool _stopRequested;
    private readonly ManualResetEventSlim _pauseGate = new ManualResetEventSlim(true);

    // For pause/resume: record how far into the timeline we are.
    private long _resumeOffsetUs;
    private long _pauseStopwatchUs;

    // ── Nested helper ─────────────────────────────────────────────────────
    public struct NoteEvent
    {
        public long StartUs;
        public long DurationUs;
        public int  NoteNumber;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Load
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Load a MIDI file.
    /// Returns null on success, or an error message string.
    /// Notes spanning more than 2 octaves are automatically transposed into 2 octaves.
    /// Chord detection uses exact-microsecond comparison (same as ES-Studio).
    /// </summary>
    public string? Load(string filePath)
    {
        Stop();

        MidiFile midiFile;
        try
        {
            midiFile = MidiFile.Read(filePath);
        }
        catch (Exception ex)
        {
            return "Failed to read MIDI file: " + ex.Message;
        }

        TempoMap tempoMap = midiFile.GetTempoMap();

        // ── Collect all notes from all tracks ──────────────────────────
        var rawNotes = new List<NoteEvent>();
        int trackCount = 0;

        foreach (var chunk in midiFile.GetTrackChunks())
        {
            trackCount++;
            foreach (var note in chunk.GetNotes())
            {
                rawNotes.Add(new NoteEvent
                {
                    StartUs    = note.TimeAs<MetricTimeSpan>(tempoMap).TotalMicroseconds,
                    DurationUs = note.LengthAs<MetricTimeSpan>(tempoMap).TotalMicroseconds,
                    NoteNumber = note.NoteNumber
                });
            }
        }

        if (rawNotes.Count == 0)
            return "MIDI file contains no notes.";

        // ── Chord check (two notes starting at the exact same microsecond) ─
        var startGroups = rawNotes.GroupBy(n => n.StartUs);
        if (startGroups.Any(g => g.Count() > 1))
            return "This MIDI file contains chords (multiple simultaneous notes).\nOnly monophonic (single-note) melodies are supported.";

        // ── Sort by start time ─────────────────────────────────────────
        rawNotes.Sort((a, b) => a.StartUs.CompareTo(b.StartUs));

        // ── Find range ─────────────────────────────────────────────────
        int minNote = rawNotes.Min(n => n.NoteNumber);
        int maxNote = rawNotes.Max(n => n.NoteNumber);
        int range   = maxNote - minNote;

        // ── Transpose to 2 octaves if needed (BetterES uses 24 semitones) ─
        if (range > 24)
        {
            for (int i = 0; i < rawNotes.Count; i++)
            {
                var n = rawNotes[i];
                while (n.NoteNumber < minNote)        n.NoteNumber += 12;
                while (n.NoteNumber > minNote + 24)   n.NoteNumber -= 12;
                rawNotes[i] = n;
            }
            maxNote = minNote + 24;
        }

        // ── BPM ────────────────────────────────────────────────────────
        var firstTempo = tempoMap.GetTempoAtTime(new MetricTimeSpan(0));
        BPM = 60_000_000.0 / firstTempo.MicrosecondsPerQuarterNote;

        _minNote = minNote;
        _maxNote = maxNote == minNote ? minNote + 24 : maxNote; // avoid div/0
        _notes   = rawNotes;

        MinNote          = _minNote;
        MaxNote          = _maxNote;
        NoteCount        = rawNotes.Count;
        TrackCount       = trackCount;
        TotalDurationUs  = rawNotes[^1].StartUs + rawNotes[^1].DurationUs;
        LoadedFileName   = Path.GetFileName(filePath);

        return null; // success
    }

    /// <summary>Returns a read-only view of the loaded notes (for piano roll drawing).</summary>
    public IReadOnlyList<NoteEvent> GetNotes() => _notes;

    // ──────────────────────────────────────────────────────────────────────
    // Playback control
    // ──────────────────────────────────────────────────────────────────────

    public void Play()
    {
        if (!IsLoaded) return;
        if (State == PlaybackState.Playing) return;

        if (State == PlaybackState.Paused)
        {
            // Resume — unblock the waiting thread
            _pauseGate.Set();
            State = PlaybackState.Playing;
            return;
        }

        // Fresh start
        _stopRequested  = false;
        _resumeOffsetUs = 0;
        _pauseGate.Set();
        State = PlaybackState.Playing;

        _thread = new Thread(PlaybackThreadProc)
        {
            IsBackground = true,
            Name = "MidiPlayback"
        };
        _thread.Start();
    }

    public void Pause()
    {
        if (State != PlaybackState.Playing) return;
        State = PlaybackState.Paused;
        _pauseGate.Reset();
        // Playback thread will block on _pauseGate.Wait() at next note boundary.
        // _resumeOffsetUs is set by the thread just before blocking.
    }

    public void Stop()
    {
        if (State == PlaybackState.Stopped) return;
        _stopRequested = true;
        _pauseGate.Set();   // unblock if paused
        State = PlaybackState.Stopped;

        if (_thread != null && _thread.IsAlive)
        {
            _thread.Join(500);
        }
        _thread = null;
        _resumeOffsetUs = 0;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Playback thread
    // ──────────────────────────────────────────────────────────────────────

    private void PlaybackThreadProc()
    {
        // Snapshot tempo multiplier at start (don't let mid-playback slider changes
        // corrupt the absolute timeline; changing tempo requires a restart).
        double tempo = Math.Max(0.1, TempoMultiplier);

        var sw = new Stopwatch();
        sw.Start();

        long timelineUs = _resumeOffsetUs;

        // Find the first note index that hasn't been played yet
        int startIndex = 0;
        if (timelineUs > 0)
        {
            while (startIndex < _notes.Count && _notes[startIndex].StartUs < timelineUs)
                startIndex++;
        }

        for (int i = startIndex; i < _notes.Count; i++)
        {
            if (_stopRequested) break;

            var note = _notes[i];

            // ── Wait until note start (absolute timeline, tempo-adjusted) ──
            long targetStartUs = (long)((note.StartUs - timelineUs) / tempo);
            SpinWaitUntil(sw, targetStartUs);

            if (_stopRequested) break;

            // ── Handle pause: block here ─────────────────────────────────
            if (State == PlaybackState.Paused)
            {
                _pauseStopwatchUs = MicrosecondsElapsed(sw);
                _resumeOffsetUs   = timelineUs + (long)(_pauseStopwatchUs * tempo);

                _pauseGate.Wait(); // blocks until Play() is called again

                if (_stopRequested) break;

                // Restart stopwatch; re-read tempo from property
                tempo = Math.Max(0.1, TempoMultiplier);
                sw.Restart();
                timelineUs = _resumeOffsetUs;
                targetStartUs = (long)((note.StartUs - timelineUs) / tempo);
                if (targetStartUs > 0) SpinWaitUntil(sw, targetStartUs);
            }

            if (_stopRequested) break;

            // ── Fire RPM callback ────────────────────────────────────────
            double rpm = MapNoteToRpm(note.NoteNumber);
            try { OnRpmChanged?.Invoke(rpm); } catch { }

            // ── Fire UI tick callback ────────────────────────────────────
            long elapsedUs = (long)(MicrosecondsElapsed(sw) * tempo) + timelineUs;
            try { OnNoteTick?.Invoke(note.NoteNumber, note.StartUs, elapsedUs); } catch { }

            // ── Hold for note duration ───────────────────────────────────
            long targetEndUs = targetStartUs + (long)(note.DurationUs / tempo);
            SpinWaitUntil(sw, targetEndUs);
        }

        if (!_stopRequested)
        {
            State = PlaybackState.Stopped;
            try { OnPlaybackEnded?.Invoke(); } catch { }
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────

    private double MapNoteToRpm(int noteNumber)
    {
        if (_maxNote == _minNote) return LowRPM;
        double t = (double)(noteNumber - _minNote) / (double)(_maxNote - _minNote);
        return LowRPM + t * (HighRPM - LowRPM);
    }

    private static long MicrosecondsElapsed(Stopwatch sw)
        => sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;

    /// <summary>Busy-waits until <paramref name="targetUs"/> microseconds have elapsed on <paramref name="sw"/>.</summary>
    private static void SpinWaitUntil(Stopwatch sw, long targetUs)
    {
        if (targetUs <= 0) return;

        long remainingUs = targetUs - MicrosecondsElapsed(sw);

        // Coarse sleep down to ~2 ms remaining
        while (remainingUs > 2_000)
        {
            Thread.Sleep((int)(remainingUs / 1_000) - 1);
            remainingUs = targetUs - MicrosecondsElapsed(sw);
        }

        // Spin for the last < 2 ms
        while (MicrosecondsElapsed(sw) < targetUs)
            Thread.SpinWait(10);
    }
}
