using System;
using System.Collections.Generic;
using System.Text;

namespace BetterES.Services;

/// <summary>
/// Generates Engine Simulator .mr files natively — no Python required.
/// Direct port of engine_generator.py logic.
/// </summary>
public static class MrGeneratorService
{
    // ═══════════════════════════════════════════════════════════
    //  CONFIG — populated from WPF sliders
    // ═══════════════════════════════════════════════════════════
    public record EngineConfig
    {
        public string Name { get; init; } = "My Engine";
        public double BoreMm { get; init; } = 86;
        public double StrokeMm { get; init; } = 86;
        public int Cylinders { get; init; } = 4;
        public int RedlineRpm { get; init; } = 7500;
        public double CompressionRatio { get; init; } = 10.5;
        public double RodLengthMm { get; init; } = 140;
        public int RodMassG { get; init; } = 600;
        public double CompressionHeightMm { get; init; } = 32;
        public double ChamberVolumeCc { get; init; } = 45;
        public double FlywheelMassKg { get; init; } = 8;
        public double FlywheelRadiusCm { get; init; } = 15;
        public double LsaDeg { get; init; } = 110;
        public int IntakeDurationDeg { get; init; } = 240;
        public int ExhaustDurationDeg { get; init; } = 240;
        public double IntakeLiftMm { get; init; } = 10;
        public double ExhaustLiftMm { get; init; } = 10;
        public double IntakeRunnerLengthCm { get; init; } = 25;
        public int IntakeRunnerVolumeCc { get; init; } = 200;
        public double PlenumVolumeL { get; init; } = 3;
        public double ExhaustPrimaryLengthCm { get; init; } = 40;
        public int ExhaustRunnerVolumeCc { get; init; } = 300;
        public double SparkAdvanceDeg { get; init; } = 15;
        public double BankAngleDeg { get; init; } = 0;
        public int[] FiringOrder { get; init; } = { 1, 3, 4, 2 };
        // Vehicle
        public int VehicleMassKg { get; init; } = 1500;
        public double DragCoefficient { get; init; } = 0.25;
        public double FrontalWidthIn { get; init; } = 66;
        public double FrontalHeightIn { get; init; } = 56;
        public double DiffRatio { get; init; } = 3.5;
        public double TireRadiusIn { get; init; } = 10;
        public int RollingResistanceN { get; init; } = 200;
        // Transmission
        public int MaxClutchTorqueLbFt { get; init; } = 500;
        public double[] GearRatios { get; init; } = { 3.5, 2.2, 1.5, 1.1, 0.85, 0.7 };
        public double IdleThrottle { get; init; } = 0.997;
    }

    // ═══════════════════════════════════════════════════════════
    //  FIRING ORDERS
    // ═══════════════════════════════════════════════════════════
    private static readonly Dictionary<int, int[]> InlineFiringOrders = new()
    {
        [1] = new[] { 1 },
        [2] = new[] { 1, 2 },
        [3] = new[] { 1, 3, 2 },
        [4] = new[] { 1, 3, 4, 2 },
        [5] = new[] { 1, 2, 4, 5, 3 },
        [6] = new[] { 1, 5, 3, 6, 2, 4 },
        [8] = new[] { 1, 8, 4, 3, 6, 5, 7, 2 },
        [10] = new[] { 1, 6, 5, 10, 2, 7, 4, 9, 3, 8 },
        [12] = new[] { 1, 7, 5, 11, 3, 9, 6, 12, 2, 8, 4, 10 },
    };

    private static readonly Dictionary<int, double[]> InlineJournalAngles = new()
    {
        [1] = new[] { 0.0 },
        [2] = new[] { 0.0, 360.0 },
        [3] = new[] { 0.0, 240.0, 480.0 },
        [4] = new[] { 0.0, 180.0, 540.0, 360.0 },
        [5] = new[] { 0.0, 144.0, 288.0, 72.0, 216.0 },
        [6] = new[] { 0.0, 480.0, 240.0, 600.0, 120.0, 360.0 },
        [8] = new[] { 0.0, 90.0, 180.0, 270.0, 360.0, 450.0, 540.0, 630.0 },
        [10] = new[] { 0.0, 72.0, 144.0, 216.0, 288.0, 360.0, 432.0, 504.0, 576.0, 648.0 },
        [12] = new[] { 0.0, 60.0, 120.0, 180.0, 240.0, 300.0, 360.0, 420.0, 480.0, 540.0, 600.0, 660.0 },
    };

    private static readonly Dictionary<int, double[]> VJournalAngles = new()
    {
        [6] = new[] { 0.0, 120.0, 240.0, 360.0, 480.0, 600.0 },
        [8] = new[] { 0.0, 90.0, 180.0, 270.0, 360.0, 450.0, 540.0, 630.0 },
        [10] = new[] { 0.0, 72.0, 144.0, 216.0, 288.0, 360.0, 432.0, 504.0, 576.0, 648.0 },
        [12] = new[] { 0.0, 60.0, 120.0, 180.0, 240.0, 300.0, 360.0, 420.0, 480.0, 540.0, 600.0, 660.0 },
    };

    // ═══════════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════════
    private static double CalcChamberCc(double boreMm, double strokeMm, double cr)
    {
        double boreM = boreMm / 1000.0;
        double strokeM = strokeMm / 1000.0;
        double sweptM3 = Math.PI / 4.0 * boreM * boreM * strokeM;
        double sweptCc = sweptM3 * 1e6;
        return Math.Round(sweptCc / (cr - 1.0), 1);
    }

    private static string SafeName(string name) =>
        name.Replace(" ", "_").Replace(".", "_").Replace("-", "_").Replace("/", "_");

    private static (double[] lifts, double[] intakeFlows, double[] exhaustFlows) GenerateFlowCurves(
        double boreMm, double maxHp, double flowAttenuation)
    {
        double bs = Math.Pow(boreMm / 82.0, 2);
        double hs = maxHp / 382.0;
        double bIntake = 295 * bs * (0.7 + 0.3 * hs);
        double bExhaust = 250 * bs * (0.7 + 0.3 * hs);

        double[] lifts = { 0, 50, 100, 150, 200, 250, 300, 350, 400, 450 };
        double[] iRatios = { 0, 0.21, 0.38, 0.57, 0.77, 0.89, 0.95, 0.98, 1.0, 1.0 };
        double[] eRatios = { 0, 0.15, 0.30, 0.47, 0.66, 0.80, 0.90, 0.94, 0.98, 1.0 };

        double[] iFlows = new double[10];
        double[] eFlows = new double[10];
        for (int i = 0; i < 10; i++)
        {
            iFlows[i] = Math.Round(iRatios[i] * bIntake * flowAttenuation, 1);
            eFlows[i] = Math.Round(eRatios[i] * bExhaust * flowAttenuation, 1);
        }
        return (lifts, iFlows, eFlows);
    }

    // ═══════════════════════════════════════════════════════════
    //  MAIN GENERATE
    // ═══════════════════════════════════════════════════════════
    public static string Generate(EngineConfig c)
    {
        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);
        void I(int n, string s = "") => sb.Append(new string(' ', n * 4)).AppendLine(s);

        string sn = SafeName(c.Name);
        int n = c.Cylinders;
        double bore = c.BoreMm;
        double stroke = c.StrokeMm;
        double rodLen = c.RodLengthMm;
        double chamberCc = c.ChamberVolumeCc > 0 ? c.ChamberVolumeCc : CalcChamberCc(bore, stroke, c.CompressionRatio);

        // Derived values
        int pistonMass = (int)Math.Round(280 + (bore - 82) * 5);
        int rodMass = c.RodMassG > 0 ? c.RodMassG : (int)Math.Round(400 + (bore - 82) * 8);
        double crankMass = Math.Round(10 + n * 1.5 + (stroke - 85) * 0.15, 1);
        double fwMass = c.FlywheelMassKg;
        double fwRadInch = c.FlywheelRadiusCm / 2.54;
        double compHeight = c.CompressionHeightMm > 0 ? c.CompressionHeightMm : Math.Round(25 + (bore - 80) * 0.3, 1);
        double blowby = Math.Round(0.05 + (300.0 / 400) * 0.05, 2); // default hp estimate

        // Max HP estimation for flow curves
        double dispCc = Math.PI * (bore / 2) * (bore / 2) * stroke * n / 1000.0;
        double estHp = Math.Max(100, dispCc * c.RedlineRpm / 15000.0 * 0.8);

        // Flow curves
        var (lifts, intakeFlows, exhaustFlows) = GenerateFlowCurves(bore, estHp, 1.5);

        // Runner volumes
        double intakeRunnerCc = Math.Round(140 * Math.Pow(bore / 82, 2), 1);
        double exhaustRunnerCc = Math.Round(50 * Math.Pow(bore / 82, 2), 1);
        double intakeXsec = Math.Round(1.8 * bore / 82, 1);
        double exhaustXsec = Math.Round(1.2 * bore / 82, 1);

        bool isVEngine = c.BankAngleDeg > 0 && n > 4;
        double vAngle = c.BankAngleDeg;

        // ── HEADER ──
        W("import \"engine_sim.mr\"");
        W();
        W("units units()");
        W("constants constants()");
        W("impulse_response_library ir_lib()");
        W("label cycle(2 * 360 * units.deg)");
        W();

        // ── WIRES ──
        W("private node wires {");
        for (int i = 1; i <= n; i++) I(1, $"output wire{i}: ignition_wire();");
        W("}");
        W();

        // ── HEAD NODE ──
        W($"private node {sn}_head {{");
        I(1, "input intake_camshaft;");
        I(1, "input exhaust_camshaft;");
        I(1, $"input chamber_volume: {chamberCc} * units.cc;");
        I(1, $"input intake_runner_volume: {intakeRunnerCc} * units.cc;");
        I(1, $"input intake_runner_cross_section_area: {intakeXsec} * units.inch * {intakeXsec} * units.inch;");
        I(1, $"input exhaust_runner_volume: {exhaustRunnerCc} * units.cc;");
        I(1, $"input exhaust_runner_cross_section_area: {exhaustXsec} * units.inch * {exhaustXsec} * units.inch;");
        I(1, "input flow_attenuation: 1.0;");
        I(1, "input lift_scale: 1.0;");
        I(1, "input flip_display: false;");
        I(1, "alias output __out: head;");
        W();

        I(1, "function intake_flow(50 * units.thou)");
        I(1, "intake_flow");
        for (int i = 0; i < lifts.Length; i++)
            I(2, $".add_flow_sample({lifts[i]} * lift_scale, {intakeFlows[i]} * flow_attenuation)");
        W();

        I(1, "function exhaust_flow(50 * units.thou)");
        I(1, "exhaust_flow");
        for (int i = 0; i < lifts.Length; i++)
            I(2, $".add_flow_sample({lifts[i]} * lift_scale, {exhaustFlows[i]} * flow_attenuation)");
        W();

        I(1, "generic_cylinder_head head(");
        I(2, "chamber_volume: chamber_volume,");
        I(2, "intake_runner_volume: intake_runner_volume,");
        I(2, "intake_runner_cross_section_area: intake_runner_cross_section_area,");
        I(2, "exhaust_runner_volume: exhaust_runner_volume,");
        I(2, "exhaust_runner_cross_section_area: exhaust_runner_cross_section_area,");
        I(2, "intake_port_flow: intake_flow,");
        I(2, "exhaust_port_flow: exhaust_flow,");
        I(2, "valvetrain: standard_valvetrain(");
        I(3, "intake_camshaft: intake_camshaft,");
        I(3, "exhaust_camshaft: exhaust_camshaft");
        I(2, "),");
        I(2, "flip_display: flip_display");
        I(1, ")");
        W("}");
        W();

        if (isVEngine)
            GenerateVEngine(sb, sn, c, n, bore, stroke, rodLen, chamberCc, pistonMass, rodMass, crankMass,
                fwMass, fwRadInch, compHeight, blowby, vAngle);
        else
            GenerateInlineEngine(sb, sn, c, n, bore, stroke, rodLen, chamberCc, pistonMass, rodMass, crankMass,
                fwMass, fwRadInch, compHeight, blowby);

        // ── VEHICLE ──
        W($"private node {sn}_vehicle {{");
        I(1, "alias output __out:");
        I(2, $"vehicle(mass: {c.VehicleMassKg} * units.kg, drag_coefficient: {c.DragCoefficient:F2},");
        I(3, $"cross_sectional_area: ({c.FrontalWidthIn:F0} * units.inch) * ({c.FrontalHeightIn:F0} * units.inch),");
        I(3, $"diff_ratio: {c.DiffRatio:F2}, tire_radius: {c.TireRadiusIn:F1} * units.inch,");
        I(3, $"rolling_resistance: {c.RollingResistanceN} * units.N);");
        W("}");
        W();

        // ── TRANSMISSION ──
        W($"private node {sn}_transmission {{");
        I(1, "alias output __out:");
        I(2, $"transmission(max_clutch_torque: {c.MaxClutchTorqueLbFt} * units.lb_ft)");
        for (int i = 0; i < c.GearRatios.Length; i++)
        {
            string semi = (i == c.GearRatios.Length - 1) ? ";" : "";
            I(2, $".add_gear({c.GearRatios[i]:F2}){semi}");
        }
        W("}");
        W();

        // ── MAIN ──
        W("public node main {");
        I(1, $"set_engine({sn}())");
        I(1, $"set_vehicle({sn}_vehicle())");
        I(1, $"set_transmission({sn}_transmission())");
        W("}");
        W();
        W("main()");

        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════
    //  INLINE ENGINE GENERATION
    // ═══════════════════════════════════════════════════════════
    private static void GenerateInlineEngine(StringBuilder sb, string sn, EngineConfig c, int n,
        double bore, double stroke, double rodLen, double chamberCc, int pistonMass, int rodMass,
        double crankMass, double fwMass, double fwRadInch, double compHeight, double blowby)
    {
        void W(string s = "") => sb.AppendLine(s);
        void I(int lvl, string s = "") => sb.Append(new string(' ', lvl * 4)).AppendLine(s);

        int[] fo = c.FiringOrder;

        // Build firing-position map: firingPos[cyl] = 0-based position in firing sequence
        // e.g. for I4 1-3-4-2: firingPos[1]=0, firingPos[3]=1, firingPos[4]=2, firingPos[2]=3
        var firingPos = new int[n + 1];
        for (int i = 0; i < fo.Length; i++) firingPos[fo[i]] = i;

        // Compute rod journal angles from firing order:
        // Cylinder at physical position i+1 reaches TDC when crank is at
        // (its firing sequence index) * (720° / n)
        double[] ja = new double[n];
        for (int i = 0; i < n; i++)
            ja[i] = firingPos[i + 1] * (720.0 / n);

        W($"public node {sn}_camshaft_builder {{");
        I(1, "input lobe_profile: comp_cams_magnum_11_450_8_lobe_profile();");
        I(1, "input intake_lobe_profile: lobe_profile;");
        I(1, "input exhaust_lobe_profile: lobe_profile;");
        I(1, $"input lobe_separation: {c.LsaDeg} * units.deg;");
        I(1, $"input intake_lobe_center: {c.LsaDeg + 2} * units.deg;");
        I(1, $"input exhaust_lobe_center: {c.LsaDeg - 2} * units.deg;");
        I(1, "input advance: 0.0 * units.deg;");
        I(1, "input base_radius: 0.75 * units.inch;");
        W();
        I(1, "output intake_cam: _intake_cam;");
        I(1, "output exhaust_cam: _exhaust_cam;");
        W();
        I(1, "camshaft_parameters params(advance: advance, base_radius: base_radius)");
        W();
        I(1, "camshaft _intake_cam(params, lobe_profile: intake_lobe_profile)");
        I(1, "camshaft _exhaust_cam(params, lobe_profile: exhaust_lobe_profile)");
        W();
        I(1, $"label rot(2 * (360 / {n}.0) * units.deg)");
        I(1, "label rot360(360 * units.deg)");
        W();
        I(1, "_exhaust_cam");
        for (int i = 0; i < n; i++) I(2, $".add_lobe((rot360 - exhaust_lobe_center) + {firingPos[i + 1]} * rot)");
        W();
        I(1, "_intake_cam");
        for (int i = 0; i < n; i++) I(2, $".add_lobe(rot360 + intake_lobe_center + {firingPos[i + 1]} * rot)");
        W("}");
        W();

        // ── IGNITION NODE ──
        W($"public node {sn}_ignition {{");
        I(1, "input wires;");
        I(1, "input timing_curve;");
        I(1, $"input rev_limit: {c.RedlineRpm} * units.rpm;");
        I(1, "alias output __out:");
        I(2, "ignition_module(timing_curve: timing_curve, rev_limit: rev_limit, limiter_duration: 0.001)");
        for (int i = 0; i < n; i++)
        {
            string frac = ((double)i / n).ToString("F6");
            I(3, $".connect_wire(wires.wire{fo[i]}, ({frac}) * (2 * 360 * units.deg))");
        }
        I(1, ";");
        W("}");
        W();

        // ── ENGINE NODE ──
        W($"public node {sn} {{");
        I(1, "alias output __out: engine;");
        W();
        I(1, "wires wires()");
        W();
        I(1, "engine engine(");
        I(2, $"name: \"{c.Name}\",");
        I(2, $"starter_torque: 150 * units.lb_ft, starter_speed: 600 * units.rpm, redline: {c.RedlineRpm} * units.rpm,");
        I(2, "fuel: fuel(molecular_mass: 114.23 * units.g, energy_density: 44.3 * units.kJ / units.g,");
        I(3, "density: 0.703 * units.kg / units.L, molecular_afr: 14.7, max_burning_efficiency: 1.0,");
        I(3, "burning_efficiency_randomness: 0.0, low_efficiency_attenuation: 1.0, max_turbulence_effect: 0.0, max_dilution_effect: 0.0),");
        I(2, "hf_gain: 0.12, noise: 0.8, jitter: 0.3, simulation_frequency: 10000");
        I(1, ")");
        W();

        I(1, $"label stroke({stroke} * units.mm)");
        I(1, $"label bore({bore} * units.mm)");
        I(1, $"label rod_length({rodLen} * units.mm)");
        I(1, $"label rod_mass({rodMass} * units.g)");
        I(1, $"label compression_height({compHeight} * units.mm)");
        I(1, $"label crank_mass({crankMass} * units.kg)");
        I(1, $"label flywheel_mass({fwMass} * units.kg)");
        I(1, $"label flywheel_radius({fwRadInch:F1} * units.inch)");
        W();
        I(1, "label crank_moment(disk_moment_of_inertia(mass: crank_mass, radius: stroke / 2))");
        I(1, "label flywheel_moment(disk_moment_of_inertia(mass: flywheel_mass, radius: flywheel_radius))");
        I(1, "label other_moment(disk_moment_of_inertia(mass: 20 * units.kg, radius: 8.0 * units.cm))");
        W();

        I(1, "crankshaft c0(");
        I(2, "throw: stroke / 2,");
        I(2, "flywheel_mass: flywheel_mass,");
        I(2, "mass: crank_mass,");
        I(2, "friction_torque: 5.0 * units.lb_ft,");
        I(2, "moment_of_inertia: crank_moment + flywheel_moment + other_moment,");
        I(2, "position_x: 0.0,");
        I(2, "position_y: 0.0,");
        I(2, "tdc: constants.pi / 2");
        I(1, ")");
        W();

        for (int i = 0; i < ja.Length; i++)
            I(1, $"rod_journal rj{i}(angle: {ja[i]} * units.deg)");
        I(1, "c0");
        for (int i = 0; i < ja.Length; i++)
            I(2, $".add_rod_journal(rj{i})");
        W();

        I(1, "piston_parameters piston_params(");
        I(2, $"mass: {pistonMass} * units.g,");
        I(2, "compression_height: compression_height,");
        I(2, "wrist_pin_position: 0 * units.mm,");
        I(2, "displacement: 0.0");
        I(1, ")");
        W();
        I(1, $"connecting_rod_parameters cr_params(");
        I(2, $"mass: {rodMass} * units.g,");
        I(2, $"moment_of_inertia: rod_moment_of_inertia(mass: {rodMass} * units.g, length: rod_length),");
        I(2, "center_of_mass: 0.0,");
        I(2, "length: rod_length");
        I(1, ")");
        W();
        I(1, "cylinder_bank_parameters bank_params(");
        I(2, "bore: bore,");
        I(2, "deck_height: stroke / 2 + rod_length + compression_height");
        I(1, ")");
        W();

        double plenumL = c.PlenumVolumeL;
        int intakeKcarb = Math.Max(200, (int)(dispCcVal(bore, stroke, n) * 0.5));
        I(1, "intake intake(");
        I(2, $"plenum_volume: {plenumL} * units.L,");
        I(2, "plenum_cross_section_area: 14.0 * units.cm2,");
        I(2, $"intake_flow_rate: k_carb({intakeKcarb}),");
        I(2, $"runner_flow_rate: k_carb({intakeKcarb / 2}),");
        I(2, "runner_length: 20.0 * units.inch,");
        I(2, "idle_flow_rate: k_carb(0.0),");
        I(2, $"idle_throttle_plate_position: {c.IdleThrottle:F4}");
        I(1, ")");
        W();

        int outletKcarb = Math.Max(300, (int)(dispCcVal(bore, stroke, n) * 0.6));
        int primKcarb = Math.Max(100, (int)(dispCcVal(bore, stroke, n) * 0.2));
        double primLenInch = c.ExhaustPrimaryLengthCm / 2.54;
        I(1, "exhaust_system_parameters es_params(");
        I(2, $"outlet_flow_rate: k_carb({outletKcarb}),");
        I(2, $"primary_tube_length: {primLenInch:F1} * units.inch,");
        I(2, $"primary_flow_rate: k_carb({primKcarb}),");
        I(2, "velocity_decay: 1.0");
        I(1, ")");
        W();
        I(1, "exhaust_system exhaust0(");
        I(2, "es_params,");
        I(2, "audio_volume: 1.0,");
        I(2, "impulse_response: ir_lib.default_0");
        I(1, ")");
        W();

        I(1, "label spacing(1.2 * units.inch)");
        I(1, "cylinder_bank b0(bank_params, angle: 0)");
        I(1, "b0");
        for (int i = 0; i < n; i++)
        {
            double pm = n > 1 ? (n - 1 - i) * 0.8 : 0;
            double att = Math.Round(0.8 + 0.2 * (i % 3), 1);
            I(2, ".add_cylinder(");
            I(3, $"piston: piston(piston_params, blowby: k_28inH2O({FormatNum(blowby)})),");
            I(3, "connecting_rod: connecting_rod(cr_params),");
            I(3, $"rod_journal: rj{i},");
            I(3, "intake: intake,");
            I(3, "exhaust_system: exhaust0,");
            // Wire number = physical cylinder number (i+1). The ignition MODULE
            // handles TIMING via connect_wire(wire{fo[k]}, k/n * cycle).
            I(3, $"ignition_wire: wires.wire{i + 1},");
            I(3, $"primary_length: spacing * {FormatNum(pm)},");
            I(3, $"sound_attenuation: {FormatNum(att)}");
            I(2, ")");
        }
        W();
        I(1, "engine.add_cylinder_bank(b0)");
        W();
        I(1, "engine.add_crankshaft(c0)");
        W();

        I(1, $"harmonic_cam_lobe intake_lobe(");
        I(2, $"duration_at_50_thou: {c.IntakeDurationDeg} * units.deg,");
        I(2, "gamma: 1.1,");
        I(2, $"lift: {c.IntakeLiftMm} * units.mm,");
        I(2, "steps: 100");
        I(1, ")");
        W();
        I(1, $"harmonic_cam_lobe exhaust_lobe(");
        I(2, $"duration_at_50_thou: {c.ExhaustDurationDeg} * units.deg,");
        I(2, "gamma: 1.1,");
        I(2, $"lift: {c.ExhaustLiftMm} * units.mm,");
        I(2, "steps: 100");
        I(1, ")");
        W();
        I(1, $"{sn}_camshaft_builder camshaft(");
        I(2, "lobe_profile: \"N/A\",");
        I(2, "intake_lobe_profile: intake_lobe,");
        I(2, "exhaust_lobe_profile: exhaust_lobe,");
        I(2, $"intake_lobe_center: {c.LsaDeg + 2} * units.deg,");
        I(2, $"exhaust_lobe_center: {c.LsaDeg - 2} * units.deg,");
        I(2, "base_radius: (34.0 / 2) * units.mm");
        I(1, ")");
        W();
        I(1, $"b0.set_cylinder_head(");
        I(2, $"{sn}_head(");
        I(3, $"chamber_volume: {chamberCc} * units.cc,");
        I(3, "intake_camshaft: camshaft.intake_cam,");
        I(3, "exhaust_camshaft: camshaft.exhaust_cam,");
        I(3, "flow_attenuation: 1.5");
        I(2, ")");
        I(1, ")");
        W();

        // Timing curve
        I(1, $"function timing_curve({c.RedlineRpm / 2} * units.rpm)");
        I(1, "timing_curve");
        I(2, ".add_sample(0000 * units.rpm, 10 * units.deg)");
        I(2, ".add_sample(1000 * units.rpm, 15 * units.deg)");
        I(2, ".add_sample(2000 * units.rpm, 25 * units.deg)");
        I(2, ".add_sample(3000 * units.rpm, 32 * units.deg)");
        I(2, ".add_sample(4000 * units.rpm, 35 * units.deg)");
        if (c.RedlineRpm >= 6000) I(2, ".add_sample(5000 * units.rpm, 34 * units.deg)");
        if (c.RedlineRpm >= 7000) { I(2, ".add_sample(6000 * units.rpm, 33 * units.deg)"); I(2, ".add_sample(7000 * units.rpm, 31 * units.deg)"); }
        if (c.RedlineRpm >= 8000) I(2, ".add_sample(8000 * units.rpm, 30 * units.deg)");
        if (c.RedlineRpm >= 8500) I(2, ".add_sample(8500 * units.rpm, 28 * units.deg)");
        W();
        I(1, "engine.add_ignition_module(");
        I(2, $"{sn}_ignition(");
        I(3, "wires: wires,");
        I(3, "timing_curve: timing_curve,");
        I(3, $"rev_limit: {c.RedlineRpm} * units.rpm");
        I(2, ")");
        I(1, ")");
        W("}");
        W();
    }

    // ═══════════════════════════════════════════════════════════
    //  V-ENGINE GENERATION
    // ═══════════════════════════════════════════════════════════
    private static void GenerateVEngine(StringBuilder sb, string sn, EngineConfig c, int n,
        double bore, double stroke, double rodLen, double chamberCc, int pistonMass, int rodMass,
        double crankMass, double fwMass, double fwRadInch, double compHeight, double blowby, double vAngle)
    {
        void W(string s = "") => sb.AppendLine(s);
        void I(int lvl, string s = "") => sb.Append(new string(' ', lvl * 4)).AppendLine(s);

        int[] fo = c.FiringOrder;
        int half = n / 2;

        // Build firing-position map: firingPos[cyl] = 0-based sequence index
        var firingPos = new int[n + 1];
        for (int i = 0; i < fo.Length; i++) firingPos[fo[i]] = i;

        // Bank assignment by PHYSICAL cylinder number (standard V/flat convention):
        //   Bank0 = odd cylinders:  1, 3, 5, 7...
        //   Bank1 = even cylinders: 2, 4, 6, 8...
        int[] bank0 = new int[half], bank1 = new int[half];
        for (int k = 0; k < half; k++)
        {
            bank0[k] = 2 * k + 1;   // 1, 3, 5, 7...
            bank1[k] = 2 * k + 2;   // 2, 4, 6, 8...
        }
        // Each cylinder gets its own rod journal.
        // Journal angle = firingPos[cyl] * (720/n) mod 360°
        double[] ja = new double[n];
        for (int i = 0; i < n; i++)
            ja[i] = (firingPos[i + 1] * (720.0 / n)) % 360.0;

        // ── CAMSHAFT BUILDER (dual cam) ──
        W($"public node {sn}_camshaft_builder {{");
        I(1, "input lobe_profile: comp_cams_magnum_11_450_8_lobe_profile();");
        I(1, "input intake_lobe_profile: lobe_profile; input exhaust_lobe_profile: lobe_profile;");
        I(1, $"input lobe_separation: {c.LsaDeg} * units.deg;");
        I(1, $"input intake_lobe_center: {c.LsaDeg + 2} * units.deg;");
        I(1, $"input exhaust_lobe_center: {c.LsaDeg - 2} * units.deg;");
        I(1, "input advance: 0.0 * units.deg; input base_radius: 0.75 * units.inch;");
        W();
        I(1, "output intake_cam_0: _intake_cam_0; output exhaust_cam_0: _exhaust_cam_0;");
        I(1, "output intake_cam_1: _intake_cam_1; output exhaust_cam_1: _exhaust_cam_1;");
        W();
        I(1, "camshaft_parameters params(advance: advance, base_radius: base_radius)");
        W();
        I(1, "camshaft _intake_cam_0(params, lobe_profile: intake_lobe_profile)");
        I(1, "camshaft _exhaust_cam_0(params, lobe_profile: exhaust_lobe_profile)");
        I(1, "camshaft _intake_cam_1(params, lobe_profile: intake_lobe_profile)");
        I(1, "camshaft _exhaust_cam_1(params, lobe_profile: exhaust_lobe_profile)");
        W();
        I(1, $"label rot(2 * (360 / {n}.0) * units.deg)");
        I(1, "label rot360(360 * units.deg)");
        W();
        // Cam lobes ordered by PHYSICAL cylinder position in each bank,
        // but positioned at the cylinder's firing-sequence index × rot
        I(1, "_exhaust_cam_0");
        for (int i = 0; i < half; i++)
            I(2, $".add_lobe((rot360 - exhaust_lobe_center) + {firingPos[bank0[i]]} * rot)");
        W();
        I(1, "_intake_cam_0");
        for (int i = 0; i < half; i++)
            I(2, $".add_lobe(rot360 + intake_lobe_center + {firingPos[bank0[i]]} * rot)");
        W();
        I(1, "_exhaust_cam_1");
        for (int i = 0; i < half; i++)
            I(2, $".add_lobe((rot360 - exhaust_lobe_center) + {firingPos[bank1[i]]} * rot)");
        W();
        I(1, "_intake_cam_1");
        for (int i = 0; i < half; i++)
            I(2, $".add_lobe(rot360 + intake_lobe_center + {firingPos[bank1[i]]} * rot)");
        W("}");
        W();

        // ── IGNITION NODE ──
        W($"public node {sn}_ignition {{");
        I(1, "input wires; input timing_curve;");
        I(1, $"input rev_limit: {c.RedlineRpm} * units.rpm;");
        I(1, "alias output __out:");
        I(2, "ignition_module(timing_curve: timing_curve, rev_limit: rev_limit, limiter_duration: 0.001)");
        for (int i = 0; i < n; i++)
        {
            string frac = ((double)i / n).ToString("F6");
            I(3, $".connect_wire(wires.wire{fo[i]}, ({frac}) * (2 * 360 * units.deg))");
        }
        I(1, ";");
        W("}");
        W();

        // ── ENGINE NODE ──
        W($"public node {sn} {{");
        I(1, "alias output __out: engine;");
        W();
        I(1, "wires wires()");
        W();
        I(1, "engine engine(");
        I(2, $"name: \"{c.Name}\",");
        I(2, $"starter_torque: 150 * units.lb_ft, starter_speed: 600 * units.rpm, redline: {c.RedlineRpm} * units.rpm,");
        I(2, "fuel: fuel(molecular_mass: 114.23 * units.g, energy_density: 44.3 * units.kJ / units.g,");
        I(3, "density: 0.703 * units.kg / units.L, molecular_afr: 14.7, max_burning_efficiency: 1.0,");
        I(3, "burning_efficiency_randomness: 0.0, low_efficiency_attenuation: 1.0, max_turbulence_effect: 0.0, max_dilution_effect: 0.0),");
        I(2, "hf_gain: 0.12, noise: 0.8, jitter: 0.3, simulation_frequency: 10000");
        I(1, ")");
        W();

        I(1, $"label stroke({stroke} * units.mm)"); I(1, $"label bore({bore} * units.mm)");
        I(1, $"label rod_length({rodLen} * units.mm)"); I(1, $"label rod_mass({rodMass} * units.g)");
        I(1, $"label compression_height({compHeight} * units.mm)"); I(1, $"label crank_mass({crankMass} * units.kg)");
        I(1, $"label flywheel_mass({fwMass} * units.kg)"); I(1, $"label flywheel_radius({fwRadInch:F1} * units.inch)");
        W();
        I(1, "label crank_moment(disk_moment_of_inertia(mass: crank_mass, radius: stroke / 2))");
        I(1, "label flywheel_moment(disk_moment_of_inertia(mass: flywheel_mass, radius: flywheel_radius))");
        I(1, "label other_moment(disk_moment_of_inertia(mass: 20 * units.kg, radius: 8.0 * units.cm))");
        W();

        I(1, "crankshaft c0(");
        I(2, "throw: stroke / 2,");
        I(2, "flywheel_mass: flywheel_mass,");
        I(2, "mass: crank_mass,");
        I(2, "friction_torque: 5.0 * units.lb_ft,");
        I(2, "moment_of_inertia: crank_moment + flywheel_moment + other_moment,");
        I(2, "position_x: 0.0,");
        I(2, "position_y: 0.0,");
        I(2, $"tdc: 90 * units.deg - ({FormatNum(vAngle / 2)} * units.deg)");
        I(1, ")");
        W();

        // n individual rod journals — one per cylinder
        for (int i = 0; i < n; i++) I(1, $"rod_journal rj{i}(angle: {ja[i]} * units.deg)");
        I(1, "c0");
        for (int i = 0; i < n; i++) I(2, $".add_rod_journal(rj{i})");
        W();

        I(1, "piston_parameters piston_params(");
        I(2, $"mass: {pistonMass} * units.g,");
        I(2, "compression_height: compression_height,");
        I(2, "wrist_pin_position: 0 * units.mm,");
        I(2, "displacement: 0.0");
        I(1, ")");
        W();
        I(1, $"connecting_rod_parameters cr_params(");
        I(2, $"mass: {rodMass} * units.g,");
        I(2, $"moment_of_inertia: rod_moment_of_inertia(mass: {rodMass} * units.g, length: rod_length),");
        I(2, "center_of_mass: 0.0,");
        I(2, "length: rod_length");
        I(1, ")");
        W();
        I(1, "cylinder_bank_parameters bank_params(");
        I(2, "bore: bore,");
        I(2, "deck_height: stroke / 2 + rod_length + compression_height");
        I(1, ")");
        W();

        double plenumL = c.PlenumVolumeL;
        int intakeKcarb = Math.Max(200, (int)(dispCcVal(bore, stroke, n) * 0.5));
        I(1, "intake intake(");
        I(2, $"plenum_volume: {plenumL} * units.L,");
        I(2, "plenum_cross_section_area: 14.0 * units.cm2,");
        I(2, $"intake_flow_rate: k_carb({intakeKcarb}),");
        I(2, $"runner_flow_rate: k_carb({intakeKcarb / 2}),");
        I(2, "runner_length: 20.0 * units.inch,");
        I(2, "idle_flow_rate: k_carb(0.0),");
        I(2, $"idle_throttle_plate_position: {c.IdleThrottle:F4}");
        I(1, ")");
        W();

        int outletKcarb = Math.Max(300, (int)(dispCcVal(bore, stroke, n) * 0.6));
        int primKcarb = Math.Max(100, (int)(dispCcVal(bore, stroke, n) * 0.2));
        double primLenInch = c.ExhaustPrimaryLengthCm / 2.54;
        I(1, "exhaust_system_parameters es_params(");
        I(2, $"outlet_flow_rate: k_carb({outletKcarb}),");
        I(2, $"primary_tube_length: {primLenInch:F1} * units.inch,");
        I(2, $"primary_flow_rate: k_carb({primKcarb}),");
        I(2, "velocity_decay: 1.0");
        I(1, ")");
        W();
        I(1, "exhaust_system exhaust0(");
        I(2, "es_params,");
        I(2, "audio_volume: 1.0,");
        I(2, "impulse_response: ir_lib.default_0");
        I(1, ")");
        W();

        double halfAngle = vAngle / 2;
        I(1, $"cylinder_bank b0(bank_params, angle: -{halfAngle} * units.deg)");
        I(1, $"cylinder_bank b1(bank_params, angle: {halfAngle} * units.deg)");
        W();

        I(1, "b0");
        for (int i = 0; i < bank0.Length; i++)
        {
            int cyl    = bank0[i];                          // physical cylinder number
            int rjIdx  = cyl - 1;                           // each cylinder has its own rj
            double att = Math.Round(0.8 + 0.2 * (i % 3), 1);
            I(2, ".add_cylinder(");
            I(3, $"piston: piston(piston_params, blowby: k_28inH2O({FormatNum(blowby)})),");
            I(3, "connecting_rod: connecting_rod(cr_params),");
            I(3, $"rod_journal: rj{rjIdx},");
            I(3, "intake: intake,");
            I(3, "exhaust_system: exhaust0,");
            I(3, $"ignition_wire: wires.wire{cyl},");
            I(3, $"sound_attenuation: {FormatNum(att)}");
            I(2, ")");
        }
        W();
        I(1, "b1");
        for (int i = 0; i < bank1.Length; i++)
        {
            int cyl    = bank1[i];                          // physical cylinder number
            int rjIdx  = cyl - 1;                           // each cylinder has its own rj
            double att = Math.Round(0.8 + 0.2 * (i % 3), 1);
            I(2, ".add_cylinder(");
            I(3, $"piston: piston(piston_params, blowby: k_28inH2O({FormatNum(blowby)})),");
            I(3, "connecting_rod: connecting_rod(cr_params),");
            I(3, $"rod_journal: rj{rjIdx},");
            I(3, "intake: intake,");
            I(3, "exhaust_system: exhaust0,");
            I(3, $"ignition_wire: wires.wire{cyl},");
            I(3, $"sound_attenuation: {FormatNum(att)}");
            I(2, ")");
        }
        W();
        I(1, "engine.add_cylinder_bank(b0).add_cylinder_bank(b1)");
        W();
        I(1, "engine.add_crankshaft(c0)");
        W();

        I(1, $"harmonic_cam_lobe intake_lobe(");
        I(2, $"duration_at_50_thou: {c.IntakeDurationDeg} * units.deg,");
        I(2, "gamma: 1.1,");
        I(2, $"lift: {c.IntakeLiftMm} * units.mm,");
        I(2, "steps: 100");
        I(1, ")");
        W();
        I(1, $"harmonic_cam_lobe exhaust_lobe(");
        I(2, $"duration_at_50_thou: {c.ExhaustDurationDeg} * units.deg,");
        I(2, "gamma: 1.1,");
        I(2, $"lift: {c.ExhaustLiftMm} * units.mm,");
        I(2, "steps: 100");
        I(1, ")");
        W();
        I(1, $"{sn}_camshaft_builder camshaft(");
        I(2, "lobe_profile: \"N/A\",");
        I(2, "intake_lobe_profile: intake_lobe,");
        I(2, "exhaust_lobe_profile: exhaust_lobe,");
        I(2, $"intake_lobe_center: {c.LsaDeg + 2} * units.deg,");
        I(2, $"exhaust_lobe_center: {c.LsaDeg - 2} * units.deg,");
        I(2, "base_radius: (34.0 / 2) * units.mm");
        I(1, ")");
        W();
        I(1, "b0.set_cylinder_head(");
        I(2, $"{sn}_head(");
        I(3, $"chamber_volume: {chamberCc} * units.cc,");
        I(3, "intake_camshaft: camshaft.intake_cam_0,");
        I(3, "exhaust_camshaft: camshaft.exhaust_cam_0,");
        I(3, "flow_attenuation: 1.5,");
        I(3, "flip_display: false");
        I(2, ")");
        I(1, ")");
        I(1, "b1.set_cylinder_head(");
        I(2, $"{sn}_head(");
        I(3, $"chamber_volume: {chamberCc} * units.cc,");
        I(3, "intake_camshaft: camshaft.intake_cam_1,");
        I(3, "exhaust_camshaft: camshaft.exhaust_cam_1,");
        I(3, "flow_attenuation: 1.5,");
        I(3, "flip_display: true");
        I(2, ")");
        I(1, ")");
        W();

        // Timing curve
        I(1, $"function timing_curve({c.RedlineRpm / 2} * units.rpm)");
        I(1, "timing_curve");
        I(2, ".add_sample(0000 * units.rpm, 10 * units.deg)");
        I(2, ".add_sample(1000 * units.rpm, 15 * units.deg)");
        I(2, ".add_sample(2000 * units.rpm, 25 * units.deg)");
        I(2, ".add_sample(3000 * units.rpm, 32 * units.deg)");
        I(2, ".add_sample(4000 * units.rpm, 35 * units.deg)");
        if (c.RedlineRpm >= 6000) I(2, ".add_sample(5000 * units.rpm, 34 * units.deg)");
        if (c.RedlineRpm >= 7000) { I(2, ".add_sample(6000 * units.rpm, 33 * units.deg)"); I(2, ".add_sample(7000 * units.rpm, 31 * units.deg)"); }
        if (c.RedlineRpm >= 8000) I(2, ".add_sample(8000 * units.rpm, 30 * units.deg)");
        if (c.RedlineRpm >= 8500) I(2, ".add_sample(8500 * units.rpm, 28 * units.deg)");
        W();
        I(1, "engine.add_ignition_module(");
        I(2, $"{sn}_ignition(");
        I(3, "wires: wires,");
        I(3, "timing_curve: timing_curve,");
        I(3, $"rev_limit: {c.RedlineRpm} * units.rpm");
        I(2, ")");
        I(1, ")");
        W("}");
        W();
    }

    private static double dispCcVal(double bore, double stroke, int cyl) =>
        Math.PI * (bore / 2) * (bore / 2) * stroke * cyl / 1000.0;

    /// <summary>Format number without trailing .0 for whole numbers</summary>
    private static string FormatNum(double val)
    {
        if (Math.Abs(val - Math.Round(val)) < 0.001)
            return ((int)Math.Round(val)).ToString();
        return val.ToString("F1");
    }
}
