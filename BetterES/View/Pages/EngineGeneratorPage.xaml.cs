using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterES.Services;
using Wpf.Ui.Controls;

namespace BetterES.View.Pages;

public enum EngineLayout { Inline, V, Flat }

public partial class EngineGeneratorPage : Page
{
    private static readonly Random Rng = new();
    private EngineLayout _layout = EngineLayout.Inline;
    private bool _isLoaded = false;

    public EngineGeneratorPage()
    {
        InitializeComponent();
        WireSliderEvents();
        _isLoaded = true;
        UpdateLiveStats();
    }

    // ── Layout Selection ─────────────────────────────────────────────

    private void Layout_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;
        if (sender is not RadioButton rb) return;

        _layout = rb.Name switch
        {
            "LayoutInline" => EngineLayout.Inline,
            "LayoutV"      => EngineLayout.V,
            "LayoutFlat"   => EngineLayout.Flat,
            _ => EngineLayout.Inline
        };

        // Show bank angle only for V/Flat
        if (BankAngleRow != null)
        {
            BankAngleRow.Visibility = _layout == EngineLayout.Inline
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        // Auto-set bank angle
        if (_layout == EngineLayout.V)
        {
            if (CylinderSlider != null && BankAngleSlider != null)
            {
                int cyl = (int)CylinderSlider.Value;
                BankAngleSlider.Value = cyl switch { 6 => 60, 8 => 90, 10 => 72, 12 => 60, _ => 90 };
            }
        }
        else if (_layout == EngineLayout.Flat)
        {
            if (BankAngleSlider != null) BankAngleSlider.Value = 180;
        }

        UpdateLiveStats();
    }

    // ── Slider Wiring ────────────────────────────────────────────────

    private void WireSliderEvents()
    {
        // Basics
        BoreSlider.ValueChanged += (_, _) => { if (!_isLoaded || BoreLabel == null) return; BoreLabel.Text = $"{BoreSlider.Value:F1}"; UpdateLiveStats(); };
        StrokeSlider.ValueChanged += (_, _) => { if (!_isLoaded || StrokeLabel == null) return; StrokeLabel.Text = $"{StrokeSlider.Value:F1}"; UpdateLiveStats(); };
        CylinderSlider.ValueChanged += (_, _) =>
        {
            if (!_isLoaded || CylinderLabel == null) return;
            int cyl = (int)CylinderSlider.Value;
            CylinderLabel.Text = cyl.ToString();
            AutoSetFiringOrder(cyl);
            UpdateLiveStats();
            // Re-apply bank angle for V layout
            if (_layout == EngineLayout.V && BankAngleSlider != null)
                BankAngleSlider.Value = cyl switch { 6 => 60, 8 => 90, 10 => 72, 12 => 60, _ => 90 };
        };
        RedlineSlider.ValueChanged += (_, _) => { if (!_isLoaded || RedlineLabel == null) return; RedlineLabel.Text = $"{(int)RedlineSlider.Value}"; };
        CompressionSlider.ValueChanged += (_, _) => { if (!_isLoaded || CompressionLabel == null) return; CompressionLabel.Text = $"{CompressionSlider.Value:F1}"; };

        // Geometry
        RodLengthSlider.ValueChanged += (_, _) => { if (!_isLoaded || RodLengthLabel == null) return; RodLengthLabel.Text = $"{(int)RodLengthSlider.Value}"; };
        RodMassSlider.ValueChanged += (_, _) => { if (!_isLoaded || RodMassLabel == null) return; RodMassLabel.Text = $"{(int)RodMassSlider.Value}"; };
        CompHeightSlider.ValueChanged += (_, _) => { if (!_isLoaded || CompHeightLabel == null) return; CompHeightLabel.Text = $"{CompHeightSlider.Value:F1}"; };
        ChamberVolSlider.ValueChanged += (_, _) => { if (!_isLoaded || ChamberVolLabel == null) return; ChamberVolLabel.Text = $"{ChamberVolSlider.Value:F1}"; };
        FlywheelMassSlider.ValueChanged += (_, _) => { if (!_isLoaded || FlywheelMassLabel == null) return; FlywheelMassLabel.Text = $"{FlywheelMassSlider.Value:F1}"; };
        FlywheelRadiusSlider.ValueChanged += (_, _) => { if (!_isLoaded || FlywheelRadiusLabel == null) return; FlywheelRadiusLabel.Text = $"{FlywheelRadiusSlider.Value:F1}"; };

        // Camshaft
        LsaSlider.ValueChanged += (_, _) => { if (!_isLoaded || LsaLabel == null) return; LsaLabel.Text = $"{LsaSlider.Value:F1}"; };
        IntakeDurationSlider.ValueChanged += (_, _) => { if (!_isLoaded || IntakeDurationLabel == null) return; IntakeDurationLabel.Text = $"{(int)IntakeDurationSlider.Value}"; };
        ExhaustDurationSlider.ValueChanged += (_, _) => { if (!_isLoaded || ExhaustDurationLabel == null) return; ExhaustDurationLabel.Text = $"{(int)ExhaustDurationSlider.Value}"; };
        IntakeLiftSlider.ValueChanged += (_, _) => { if (!_isLoaded || IntakeLiftLabel == null) return; IntakeLiftLabel.Text = $"{IntakeLiftSlider.Value:F1}"; };
        ExhaustLiftSlider.ValueChanged += (_, _) => { if (!_isLoaded || ExhaustLiftLabel == null) return; ExhaustLiftLabel.Text = $"{ExhaustLiftSlider.Value:F1}"; };

        // Intake/Exhaust
        IntakeRunnerLenSlider.ValueChanged += (_, _) => { if (!_isLoaded || IntakeRunnerLenLabel == null) return; IntakeRunnerLenLabel.Text = $"{IntakeRunnerLenSlider.Value:F1}"; };
        IntakeRunnerVolSlider.ValueChanged += (_, _) => { if (!_isLoaded || IntakeRunnerVolLabel == null) return; IntakeRunnerVolLabel.Text = $"{(int)IntakeRunnerVolSlider.Value}"; };
        PlenumVolSlider.ValueChanged += (_, _) => { if (!_isLoaded || PlenumVolLabel == null) return; PlenumVolLabel.Text = $"{PlenumVolSlider.Value:F1}"; };
        ExhaustPrimLenSlider.ValueChanged += (_, _) => { if (!_isLoaded || ExhaustPrimLenLabel == null) return; ExhaustPrimLenLabel.Text = $"{ExhaustPrimLenSlider.Value:F1}"; };
        ExhaustRunnerVolSlider.ValueChanged += (_, _) => { if (!_isLoaded || ExhaustRunnerVolLabel == null) return; ExhaustRunnerVolLabel.Text = $"{(int)ExhaustRunnerVolSlider.Value}"; };
        IdleThrottleSlider.ValueChanged += (_, _) => { if (!_isLoaded || IdleThrottleLabel == null) return; IdleThrottleLabel.Text = $"{IdleThrottleSlider.Value:F3}"; };

        // Ignition
        SparkAdvanceSlider.ValueChanged += (_, _) => { if (!_isLoaded || SparkAdvanceLabel == null) return; SparkAdvanceLabel.Text = $"{SparkAdvanceSlider.Value:F1}"; };
        BankAngleSlider.ValueChanged += (_, _) => { if (!_isLoaded || BankAngleLabel == null) return; BankAngleLabel.Text = $"{(int)BankAngleSlider.Value}"; };

        // Vehicle
        VehicleMassSlider.ValueChanged += (_, _) => { if (!_isLoaded || VehicleMassLabel == null) return; VehicleMassLabel.Text = $"{(int)VehicleMassSlider.Value}"; };
        DragCoeffSlider.ValueChanged += (_, _) => { if (!_isLoaded || DragCoeffLabel == null) return; DragCoeffLabel.Text = $"{DragCoeffSlider.Value:F2}"; };
        FrontalWSlider.ValueChanged += (_, _) => { if (!_isLoaded || FrontalWLabel == null) return; FrontalWLabel.Text = $"{(int)FrontalWSlider.Value}"; };
        FrontalHSlider.ValueChanged += (_, _) => { if (!_isLoaded || FrontalHLabel == null) return; FrontalHLabel.Text = $"{(int)FrontalHSlider.Value}"; };
        DiffRatioSlider.ValueChanged += (_, _) => { if (!_isLoaded || DiffRatioLabel == null) return; DiffRatioLabel.Text = $"{DiffRatioSlider.Value:F2}"; };
        TireRadiusSlider.ValueChanged += (_, _) => { if (!_isLoaded || TireRadiusLabel == null) return; TireRadiusLabel.Text = $"{TireRadiusSlider.Value:F1}"; };
        RollingResistSlider.ValueChanged += (_, _) => { if (!_isLoaded || RollingResistLabel == null) return; RollingResistLabel.Text = $"{(int)RollingResistSlider.Value}"; };

        // Transmission
        ClutchTorqueSlider.ValueChanged += (_, _) => { if (!_isLoaded || ClutchTorqueLabel == null) return; ClutchTorqueLabel.Text = $"{(int)ClutchTorqueSlider.Value}"; };
        NumGearsSlider.ValueChanged += (_, _) =>
        {
            if (!_isLoaded || NumGearsLabel == null) return;
            int g = (int)NumGearsSlider.Value;
            NumGearsLabel.Text = g.ToString();
            AutoSetGearRatios(g);
        };
    }

    // ── Live Stats ───────────────────────────────────────────────────

    private void UpdateLiveStats()
    {
        if (!_isLoaded || BoreSlider == null || StrokeSlider == null || CylinderSlider == null) return;

        double bore = BoreSlider.Value;
        double stroke = StrokeSlider.Value;
        int cyl = (int)CylinderSlider.Value;

        double dispCc = Math.PI * (bore / 2) * (bore / 2) * stroke * cyl / 1000.0;

        if (LiveDisplacement != null) LiveDisplacement.Text = $"{dispCc:F0} cc ({dispCc / 1000.0:F1} L)";
        if (LiveBoreStroke != null) LiveBoreStroke.Text = $"{bore:F0} × {stroke:F0} mm";

        string layoutStr = _layout switch
        {
            EngineLayout.Inline => $"I{cyl}",
            EngineLayout.V      => $"V{cyl} {(BankAngleSlider != null ? (int)BankAngleSlider.Value : 0)}°",
            EngineLayout.Flat   => $"F{cyl} {(BankAngleSlider != null ? (int)BankAngleSlider.Value : 0)}°",
            _ => $"I{cyl}"
        };
        if (LiveLayout != null) LiveLayout.Text = layoutStr;
    }

    // ── Auto-firing Order & Gear Ratios ──────────────────────────────

    private void AutoSetFiringOrder(int cylinders)
    {
        if (!_isLoaded || FiringOrderBox == null) return;
        FiringOrderBox.Text = cylinders switch
        {
            1 => "1",
            2 => "1,2",
            3 => "1,3,2",
            4 => "1,3,4,2",
            5 => "1,2,4,5,3",
            6 => "1,5,3,6,2,4",
            8 => "1,8,4,3,6,5,7,2",
            10 => "1,6,5,10,2,7,4,9,3,8",
            12 => "1,7,5,11,3,9,6,12,2,8,4,10",
            _ => string.Join(",", Enumerable.Range(1, cylinders))
        };
    }

    private void AutoSetGearRatios(int gears)
    {
        if (!_isLoaded || GearRatiosBox == null) return;
        var ratios = gears switch
        {
            1 => new[] { 1.0 },
            2 => new[] { 3.0, 1.0 },
            3 => new[] { 3.5, 2.0, 1.0 },
            4 => new[] { 3.5, 2.2, 1.5, 1.0 },
            5 => new[] { 3.5, 2.2, 1.5, 1.1, 0.85 },
            6 => new[] { 3.5, 2.2, 1.5, 1.1, 0.85, 0.7 },
            7 => new[] { 4.0, 2.8, 1.8, 1.3, 1.0, 0.8, 0.65 },
            8 => new[] { 4.5, 3.0, 2.0, 1.5, 1.2, 0.95, 0.8, 0.65 },
            _ => new[] { 1.0 }
        };
        GearRatiosBox.Text = string.Join(", ", ratios.Select(r => r.ToString("F2")));
    }

    // ── Browse ───────────────────────────────────────────────────────

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Engine Simulator files (*.mr)|*.mr|All files (*.*)|*.*",
            Title = "Save Engine Definition",
            FileName = "engine.mr"
        };
        if (dlg.ShowDialog() == true)
            FilePathBox.Text = dlg.FileName;
    }

    // ── Generate (native C# — no Python) ─────────────────────────────

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var config = BuildConfig();
            string code = MrGeneratorService.Generate(config);
            CodeOutput.Text = code;
            CopyButton.IsEnabled = true;

            // Save to file if path specified
            string path = FilePathBox.Text.Trim();
            if (!string.IsNullOrEmpty(path))
            {
                File.WriteAllText(path, code);
                SetStatus("Saved!", "#40FF80", "#80FF80");
            }
            else
                SetStatus("Generated", "#40FF80", "#80FF80");
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}", "#40FF4040", "#FF8080");
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(CodeOutput.Text))
        {
            Clipboard.SetText(CodeOutput.Text);
            SetStatus("Copied!", "#408080FF", "#80BFFF");
        }
    }

    private void Random_Click(object sender, RoutedEventArgs e)
    {
        // Random engine type
        _layout = new[] { EngineLayout.Inline, EngineLayout.V, EngineLayout.Flat }[Rng.Next(3)];
        switch (_layout)
        {
            case EngineLayout.Inline: LayoutInline.IsChecked = true; break;
            case EngineLayout.V: LayoutV.IsChecked = true; break;
            case EngineLayout.Flat: LayoutFlat.IsChecked = true; break;
        }

        BoreSlider.Value = Rng.Next(65, 110);
        StrokeSlider.Value = Rng.Next(55, 105);

        int cyl = _layout == EngineLayout.Inline
            ? new[] { 3, 4, 5, 6 }[Rng.Next(4)]
            : new[] { 6, 8, 10, 12 }[Rng.Next(4)];
        CylinderSlider.Value = cyl;

        RedlineSlider.Value = Rng.Next(50, 110) * 100;
        CompressionSlider.Value = Rng.Next(80, 140) / 10.0;
        RodLengthSlider.Value = Rng.Next(100, 220);
        RodMassSlider.Value = Rng.Next(200, 1500) / 10 * 10;
        CompHeightSlider.Value = Rng.Next(250, 550) / 10.0;
        ChamberVolSlider.Value = Rng.Next(200, 800) / 10.0;
        FlywheelMassSlider.Value = Rng.Next(30, 250) / 10.0;
        FlywheelRadiusSlider.Value = Rng.Next(80, 350) / 10.0;
        LsaSlider.Value = Rng.Next(1000, 1200) / 10.0;
        IntakeDurationSlider.Value = Rng.Next(200, 300);
        ExhaustDurationSlider.Value = Rng.Next(200, 300);
        IntakeLiftSlider.Value = Rng.Next(60, 150) / 10.0;
        ExhaustLiftSlider.Value = Rng.Next(60, 150) / 10.0;
        IntakeRunnerLenSlider.Value = Rng.Next(100, 600) / 10.0;
        IntakeRunnerVolSlider.Value = Rng.Next(100, 800) / 10 * 10;
        PlenumVolSlider.Value = Rng.Next(5, 150) / 10.0;
        ExhaustPrimLenSlider.Value = Rng.Next(200, 800) / 10.0;
        ExhaustRunnerVolSlider.Value = Rng.Next(100, 800) / 10 * 10;
        IdleThrottleSlider.Value = Rng.Next(9950, 10001) / 10000.0;
        SparkAdvanceSlider.Value = Rng.Next(50, 350) / 10.0;

        // Bank angle
        if (_layout != EngineLayout.Inline)
        {
            BankAngleSlider.Value = _layout switch
            {
                EngineLayout.V => cyl switch { 6 => 60, 8 => 90, 10 => 72, 12 => 60, _ => 90 },
                EngineLayout.Flat => 180,
                _ => 0
            };
        }

        // Random name
        string[] names = _layout switch
        {
            EngineLayout.V => new[] { "Custom V8", "Flat Six", "V12 Engine", "Big Block V8", "Small Block V8", "Turbo V6" },
            EngineLayout.Flat => new[] { "Flat Six", "Boxer Four", "Flat Twelve" },
            _ => new[] { "Inline Fury", "Turbo I4", "Twin Turbo I6", "NA Monster", "High Rev I4", "Bulletproof I6" }
        };
        EngineName.Text = names[Rng.Next(names.Length)];

        // Random vehicle
        VehicleMassSlider.Value = Rng.Next(800, 3500);
        DragCoeffSlider.Value = Rng.Next(15, 50) / 100.0;
        FrontalWSlider.Value = Rng.Next(50, 80);
        FrontalHSlider.Value = Rng.Next(40, 70);
        DiffRatioSlider.Value = Rng.Next(250, 550) / 100.0;
        TireRadiusSlider.Value = Rng.Next(80, 150) / 10.0;
        RollingResistSlider.Value = Rng.Next(100, 2000);

        // Random transmission
        int gears = new[] { 4, 5, 6 }[Rng.Next(3)];
        NumGearsSlider.Value = gears;
        ClutchTorqueSlider.Value = Rng.Next(100, 1500);

        SetStatus("Randomized!", "#40FFFF80", "#FFFF80");
    }

    // ── Build Config ─────────────────────────────────────────────────

    private MrGeneratorService.EngineConfig BuildConfig()
    {
        // Parse firing order
        int[] firingOrder = FiringOrderBox.Text.Trim()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out int v) ? v : 0)
            .Where(v => v > 0)
            .ToArray();

        // Parse gear ratios
        double[] gearRatios = GearRatiosBox.Text.Trim()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0)
            .Where(v => v > 0)
            .ToArray();

        return new MrGeneratorService.EngineConfig
        {
            Name = EngineName.Text.Trim(),
            BoreMm = BoreSlider.Value,
            StrokeMm = StrokeSlider.Value,
            Cylinders = (int)CylinderSlider.Value,
            RedlineRpm = (int)RedlineSlider.Value,
            CompressionRatio = CompressionSlider.Value,
            RodLengthMm = RodLengthSlider.Value,
            RodMassG = (int)RodMassSlider.Value,
            CompressionHeightMm = CompHeightSlider.Value,
            ChamberVolumeCc = ChamberVolSlider.Value,
            FlywheelMassKg = FlywheelMassSlider.Value,
            FlywheelRadiusCm = FlywheelRadiusSlider.Value,
            LsaDeg = LsaSlider.Value,
            IntakeDurationDeg = (int)IntakeDurationSlider.Value,
            ExhaustDurationDeg = (int)ExhaustDurationSlider.Value,
            IntakeLiftMm = IntakeLiftSlider.Value,
            ExhaustLiftMm = ExhaustLiftSlider.Value,
            IntakeRunnerLengthCm = IntakeRunnerLenSlider.Value,
            IntakeRunnerVolumeCc = (int)IntakeRunnerVolSlider.Value,
            PlenumVolumeL = PlenumVolSlider.Value,
            ExhaustPrimaryLengthCm = ExhaustPrimLenSlider.Value,
            ExhaustRunnerVolumeCc = (int)ExhaustRunnerVolSlider.Value,
            IdleThrottle = IdleThrottleSlider.Value,
            SparkAdvanceDeg = SparkAdvanceSlider.Value,
            BankAngleDeg = _layout == EngineLayout.Inline ? 0 : BankAngleSlider.Value,
            FiringOrder = firingOrder,
            VehicleMassKg = (int)VehicleMassSlider.Value,
            DragCoefficient = DragCoeffSlider.Value,
            FrontalWidthIn = FrontalWSlider.Value,
            FrontalHeightIn = FrontalHSlider.Value,
            DiffRatio = DiffRatioSlider.Value,
            TireRadiusIn = TireRadiusSlider.Value,
            RollingResistanceN = (int)RollingResistSlider.Value,
            MaxClutchTorqueLbFt = (int)ClutchTorqueSlider.Value,
            GearRatios = gearRatios,
        };
    }

    private void SetStatus(string text, string bg, string fg)
    {
        if (StatusText != null) StatusText.Text = text;
        if (StatusBadge != null) StatusBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg));
        if (StatusText != null) StatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fg));
    }
}
