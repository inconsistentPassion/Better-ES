using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using BetterES.Services;

namespace BetterES.View.Pages;

public partial class LogPage : Page
{
    private readonly LogService _logService;
    private bool _autoScroll = true;
    private string _currentFilter = "All";
    private string _searchText = "";

    public LogPage(LogService logService)
    {
        _logService = logService;
        DataContext = _logService;
        InitializeComponent();

        _logService.EntryAdded += OnEntryAdded;
        ApplyFilter();
    }

    // ── Auto-scroll ────────────────────────────────────────────────

    private void OnEntryAdded(LogEntry entry)
    {
        if (_autoScroll)
            Dispatcher.BeginInvoke(() => LogScroller.ScrollToEnd());
    }

    private void LogScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        // Track whether user is near bottom — only auto-scroll when they are
        if (e.ExtentHeightChange > 0)
        {
            // New content added
            if (_autoScroll)
                sv.ScrollToEnd();
        }
        else
        {
            // User scrolled manually
            _autoScroll = sv.VerticalOffset >= sv.ScrollableHeight - 50;
        }
    }

    // ── Filter by level ────────────────────────────────────────────

    private void FilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (FilterCombo.SelectedItem is ComboBoxItem item)
        {
            _currentFilter = item.Tag?.ToString() ?? "All";
            ApplyFilter();
        }
    }

    // ── Text search ────────────────────────────────────────────────

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text?.Trim() ?? "";
        ApplyFilter();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SearchBox.Text = "";
            _searchText = "";
            ApplyFilter();
        }
    }

    // ── Combined filter ────────────────────────────────────────────

    private void ApplyFilter()
    {
        var view = CollectionViewSource.GetDefaultView(_logService.Entries);
        string filter = _currentFilter;
        string search = _searchText;

        if (string.IsNullOrEmpty(search))
        {
            view.Filter = filter switch
            {
                "Info"    => o => o is LogEntry e && e.Level == LogLevel.Info,
                "Warning" => o => o is LogEntry e && e.Level == LogLevel.Warning,
                "Error"   => o => o is LogEntry e && e.Level == LogLevel.Error,
                "Debug"   => o => o is LogEntry e && e.Level == LogLevel.Debug,
                _         => null
            };
        }
        else
        {
            view.Filter = o => o is LogEntry e
                && MatchesFilter(e, filter)
                && MatchesSearch(e, search);
        }
    }

    private bool MatchesFilter(LogEntry entry, string filter) => filter switch
    {
        "Info"    => entry.Level == LogLevel.Info,
        "Warning" => entry.Level == LogLevel.Warning,
        "Error"   => entry.Level == LogLevel.Error,
        "Debug"   => entry.Level == LogLevel.Debug,
        _         => true
    };

    private bool MatchesSearch(LogEntry entry, string search)
    {
        if (string.IsNullOrEmpty(search)) return true;
        return entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
            || entry.Source.Contains(search, StringComparison.OrdinalIgnoreCase)
            || entry.Formatted.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    // ── Copy ──────────────────────────────────────────────────────

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var lines = new System.Text.StringBuilder();
        foreach (LogEntry entry in _logService.Entries)
        {
            if (!ShouldShow(entry)) continue;
            lines.AppendLine(entry.Formatted);
        }
        if (lines.Length > 0)
            Clipboard.SetText(lines.ToString());
    }

    private void CopyCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        // Ctrl+C: copy filtered entries as plain text
        CopyButton_Click(sender, e);
        e.Handled = true;
    }

    private bool ShouldShow(LogEntry entry)
    {
        // Apply current filter
        bool matchesFilter = _currentFilter switch
        {
            "Info"    => entry.Level == LogLevel.Info,
            "Warning" => entry.Level == LogLevel.Warning,
            "Error"   => entry.Level == LogLevel.Error,
            "Debug"   => entry.Level == LogLevel.Debug,
            _         => true
        };
        if (!matchesFilter) return false;

        // Apply search
        if (string.IsNullOrEmpty(_searchText)) return true;
        return entry.Message.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
            || entry.Source.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
            || entry.Formatted.Contains(_searchText, StringComparison.OrdinalIgnoreCase);
    }

    // ── Clear ──────────────────────────────────────────────────────

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _logService.Clear();
    }

    ~LogPage()
    {
        _logService.EntryAdded -= OnEntryAdded;
    }
}
