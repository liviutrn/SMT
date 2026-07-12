using System.Text.Json.Serialization;

namespace SnowRunnerUnified;

internal sealed class AppSettings
{
    public string ActiveProfile { get; set; } = "Default";
    public MotionSettings Motion { get; set; } = new();
    public ControlSettings Controls { get; set; } = new();
    public ClutchSettings Clutch { get; set; } = new();
    public StallSettings Stall { get; set; } = new();
    public SmtSettings Smt { get; set; } = new();
}

internal sealed class MotionSettings
{
    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 4123;
    public int RateHz { get; set; } = 60;
    public Dictionary<string, AxisTuning> Axes { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Pitch"] = new(), ["Roll"] = new(), ["Yaw"] = new(),
        ["Surge"] = new(), ["Sway"] = new(), ["Heave"] = new()
    };
}

internal sealed class AxisTuning
{
    public bool Enabled { get; set; } = true;
    public double Gain { get; set; } = 1;
    public double Offset { get; set; }
    public double Limit { get; set; } = 100;
    public bool Invert { get; set; }
    public double Smoothing { get; set; }
}

internal sealed class ControlSettings
{
    public string DeviceName { get; set; } = "Auto";
    public AxisCalibration ClutchAxis { get; set; } = new() { Axis = "Axis 2" };
    public AxisCalibration ThrottleAxis { get; set; } = new() { Axis = "Axis 1" };
    public Dictionary<string, string> Bindings { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Reverse"] = "R", ["Neutral"] = "N", ["Gear 1"] = "1", ["Gear 2"] = "2",
        ["Gear 3"] = "3", ["Gear 4"] = "4", ["Gear 5"] = "5", ["Gear 6"] = "6",
        ["Range Low"] = "L", ["Range Auto"] = "A", ["Range High"] = "H"
    };
}

internal sealed class AxisCalibration
{
    public string Axis { get; set; } = "";
    public int Minimum { get; set; }
    public int Maximum { get; set; } = 65535;
    public int Center { get; set; } = 32767;
    public double DeadzonePercent { get; set; } = 1;
    public bool Invert { get; set; }
}

internal sealed class ClutchSettings
{
    public double BiteStart { get; set; } = 0.10;
    public double BiteEnd { get; set; } = 0.95;
    public double Curve { get; set; } = 6.0;
    public double IdleThrottle { get; set; } = 0.25;
}

internal sealed class StallSettings
{
    public bool Enabled { get; set; } = true;
    public double WheelMovingRadPerSecond { get; set; } = 0.35;
    public double ChassisMovingMetresPerSecond { get; set; } = 0.15;
    public double MaxThrottle { get; set; } = 0.22;
    public double MinClutchEngagement { get; set; } = 0.75;
    public int DelayMilliseconds { get; set; } = 650;
    public int RestartCooldownMilliseconds { get; set; } = 900;
}

internal sealed class SmtSettings
{
    public bool ManualGearbox { get; set; } = true;
    public bool AnalogClutch { get; set; } = true;
    public bool RequireClutchForShift { get; set; } = true;
    public bool StartInNeutral { get; set; } = true;
}
