using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterES.Services
{
    public enum UnitSystem
    {
        Metric,
        Imperial
    }

    public enum Theme
    {
        Dark,
        Light
    }

    public class SettingsData
    {
        [JsonPropertyName("theme")]
        public Theme Theme { get; set; } = Theme.Dark;

        [JsonPropertyName("units")]
        public UnitSystem Units { get; set; } = UnitSystem.Imperial;

        [JsonPropertyName("stayOnTop")]
        public bool StayOnTop { get; set; } = false;

        [JsonPropertyName("beamngPort")]
        public int BeamNGPort { get; set; } = 4444;
    }

    /// <summary>
    /// Persists user settings to a local JSON file.
    /// Fires events when settings change so pages can update live.
    /// </summary>
    public class SettingsService
    {
        private static readonly string SettingsDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BetterES");
        private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        public SettingsData Data { get; private set; }

        public event Action<Theme>? ThemeChanged;
        public event Action<UnitSystem>? UnitsChanged;

        public SettingsService()
        {
            Data = Load();
        }

        // ── Theme ──────────────────────────────────────────────────

        public Theme Theme
        {
            get => Data.Theme;
            set
            {
                if (Data.Theme == value) return;
                Data.Theme = value;
                Save();
                ThemeChanged?.Invoke(value);
            }
        }

        // ── Units ──────────────────────────────────────────────────

        public UnitSystem Units
        {
            get => Data.Units;
            set
            {
                if (Data.Units == value) return;
                Data.Units = value;
                Save();
                UnitsChanged?.Invoke(value);
            }
        }

        // ── Stay on top ────────────────────────────────────────────

        public bool StayOnTop
        {
            get => Data.StayOnTop;
            set { Data.StayOnTop = value; Save(); }
        }

        public int BeamNGPort
        {
            get => Data.BeamNGPort;
            set { Data.BeamNGPort = value; Save(); }
        }

        // ── Persistence ────────────────────────────────────────────

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                var json = JsonSerializer.Serialize(Data, JsonOpts);
                File.WriteAllText(SettingsPath, json);
            }
            catch { /* best effort */ }
        }

        private static SettingsData Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    return JsonSerializer.Deserialize<SettingsData>(json) ?? new SettingsData();
                }
            }
            catch { /* corrupt file, use defaults */ }
            return new SettingsData();
        }
    }

    /// <summary>
    /// Unit conversion helpers. All internal values are metric (km/h, bar, °C).
    /// </summary>
    public static class Units
    {
        public static string Speed(double kmh, UnitSystem sys) =>
            sys == UnitSystem.Imperial
                ? $"{kmh * 0.621371:F0} mph"
                : $"{kmh:F0} km/h";

        public static double SpeedValue(double kmh, UnitSystem sys) =>
            sys == UnitSystem.Imperial ? kmh * 0.621371 : kmh;

        public static string SpeedLabel(UnitSystem sys) =>
            sys == UnitSystem.Imperial ? "mph" : "km/h";

        public static string Boost(double bar, UnitSystem sys) =>
            sys == UnitSystem.Imperial
                ? $"{bar * 14.5038:F1} PSI"
                : $"{bar:F2} bar";

        public static string Temperature(double celsius, UnitSystem sys) =>
            sys == UnitSystem.Imperial
                ? $"{celsius * 9.0 / 5.0 + 32:F0}°F"
                : $"{celsius:F0}°C";

        public static string Torque(double lbft, UnitSystem sys) =>
            sys == UnitSystem.Imperial
                ? $"{lbft:F0} lb-ft"
                : $"{lbft * 1.35582:F0} Nm";

        public static string Power(double hp, UnitSystem sys) =>
            sys == UnitSystem.Imperial
                ? $"{hp:F0} hp"
                : $"{hp * 0.7457:F0} kW";

        public static string Distance(double meters, UnitSystem sys) =>
            sys == UnitSystem.Imperial
                ? $"{meters * 3.28084:F0} ft"
                : $"{meters:F0} m";
    }
}
