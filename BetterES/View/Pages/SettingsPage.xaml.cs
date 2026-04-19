using System.Windows;
using System.Windows.Controls;
using BetterES.Services;
using Wpf.Ui.Appearance;

namespace BetterES.View.Pages;

public partial class SettingsPage : Page
{
    private readonly SettingsService _settings;

    public SettingsPage(SettingsService settings)
    {
        _settings = settings;
        InitializeComponent();
        LoadCurrentSettings();
    }

    private void LoadCurrentSettings()
    {
        // Theme
        if (_settings.Theme == Theme.Light)
            LightRadio.IsChecked = true;
        else
            DarkRadio.IsChecked = true;

        // Units
        if (_settings.Units == UnitSystem.Imperial)
            ImperialRadio.IsChecked = true;
        else
            MetricRadio.IsChecked = true;

        // Stay on top
        StayOnTopCheckBox.IsChecked = _settings.StayOnTop;

        UpdateUnitPreview();
    }

    // ── Theme ──────────────────────────────────────────────────────

    private void ThemeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;

        var theme = LightRadio.IsChecked == true ? Theme.Light : Theme.Dark;
        _settings.Theme = theme;

        var appTheme = theme == Theme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark;
        ApplicationThemeManager.Apply(appTheme);
    }

    // ── Units ──────────────────────────────────────────────────────

    private void UnitRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;

        _settings.Units = ImperialRadio.IsChecked == true
            ? UnitSystem.Imperial
            : UnitSystem.Metric;

        UpdateUnitPreview();
    }

    private void UpdateUnitPreview()
    {
        var u = _settings.Units;
        SpeedPreview.Text = Units.Speed(100, u);
        BoostPreview.Text = Units.Boost(1.0, u);
        TempPreview.Text = Units.Temperature(90, u);
    }

    // ── Stay on top ────────────────────────────────────────────────

    private void StayOnTop_Changed(object sender, RoutedEventArgs e)
    {
        _settings.StayOnTop = StayOnTopCheckBox.IsChecked == true;
        if (Application.Current.MainWindow != null)
            Application.Current.MainWindow.Topmost = _settings.StayOnTop;
    }
}
