using System.Globalization;
using System.Numerics;
using System.Text;

namespace SnowRunnerTelemetry;

internal static class TelemetryFormatting
{
    public static string Address(nint address)
    {
        return address == 0 ? "n/a" : $"0x{(ulong)address:X16}";
    }

    public static string Offset(int offset)
    {
        return $"+0x{offset:X}";
    }

    public static string Float(float value)
    {
        return value.ToString("0.000", CultureInfo.InvariantCulture);
    }

    public static string Degrees(double value)
    {
        return value.ToString("0.00", CultureInfo.InvariantCulture) + " deg";
    }

    public static string Score(double value)
    {
        return value.ToString("0", CultureInfo.InvariantCulture);
    }

    public static string Vector(Vector3 value)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{value.X,8:0.000} {value.Y,8:0.000} {value.Z,8:0.000}");
    }

    public static string LocalAcceleration(Vector3 value)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"F {value.X,7:0.00}  L {value.Y,7:0.00}  V {value.Z,7:0.00} m/s^2  ({value.Length() / 9.80665f:0.00} g)");
    }

    public static string Quaternion(Quaternion value)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{value.X,8:0.000} {value.Y,8:0.000} {value.Z,8:0.000} {value.W,8:0.000}");
    }

    public static string SnapshotText(TelemetrySnapshot snapshot, int maxCandidates = 80)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"SnowRunner telemetry snapshot: {snapshot.Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}");
        builder.AppendLine($"Status: {snapshot.Status}");
        builder.AppendLine($"Module: {Address(snapshot.ModuleBase)} size 0x{snapshot.ModuleSize:X} version {snapshot.ModuleVersion}");
        builder.AppendLine($"Speed km/h: {(snapshot.SpeedKmh is null ? "n/a" : Float(snapshot.SpeedKmh.Value))}");
        builder.AppendLine($"SnowFlyer position: {(snapshot.SnowFlyerPosition is null ? "n/a" : Vector(snapshot.SnowFlyerPosition.Value))}");
        builder.AppendLine($"Static: {Address(snapshot.Pointer.StaticAddress)} singleton {Address(snapshot.Pointer.Singleton)} vehicle {Address(snapshot.Pointer.ActiveVehicle)} rb {Address(snapshot.Pointer.RigidBody)} score {Score(snapshot.Pointer.Score)} source {snapshot.Pointer.Source}");

        if (snapshot.RigidBody is not null)
        {
            var rb = snapshot.RigidBody;
            builder.AppendLine($"Pitch/Roll/Yaw: {FormatNullable(rb.Basis?.PitchDeg)} / {FormatNullable(rb.Basis?.RollDeg)} / {FormatNullable(rb.Basis?.YawDeg)}");
            builder.AppendLine($"Basis: {(rb.Basis is null ? "n/a" : $"{Offset(rb.Basis.Offset)} {rb.Basis.Layout} score {Score(rb.Basis.Score)} F {Vector(rb.Basis.Forward)} U {Vector(rb.Basis.Up)} R {Vector(rb.Basis.Right)}")}");
            builder.AppendLine($"Quaternion: {(rb.Quaternion is null ? "n/a" : $"{Offset(rb.Quaternion.Offset)} score {Score(rb.Quaternion.Score)} {Quaternion(rb.Quaternion.Value)}")}");
            builder.AppendLine($"RB position: {(rb.Position is null ? "n/a" : $"{Offset(rb.Position.Offset)} score {Score(rb.Position.Score)} {Vector(rb.Position.Value)}")}");
            builder.AppendLine($"Linear velocity: {(rb.LinearVelocity is null ? "n/a" : $"{Offset(rb.LinearVelocity.Offset)} score {Score(rb.LinearVelocity.Score)} {Vector(rb.LinearVelocity.Value)} mag {(rb.LinearSpeedKmh ?? 0):0.00} km/h")}");
            builder.AppendLine($"Local acceleration: {(rb.LocalAcceleration is null ? "n/a" : $"{Offset(rb.LocalAcceleration.Offset)} {LocalAcceleration(rb.LocalAcceleration.Value)}")}");
            builder.AppendLine($"Validation: {rb.Validation}");
        }

        builder.AppendLine($"Candidates: {snapshot.Candidates.Count}");
        foreach (var candidate in snapshot.Candidates.Take(maxCandidates))
        {
            builder.AppendLine($"{candidate.Kind} {candidate.Offset} {candidate.Address} score {candidate.Score} {candidate.Value} [{candidate.Source}]");
        }
        if (snapshot.Candidates.Count > maxCandidates)
        {
            builder.AppendLine($"... {snapshot.Candidates.Count - maxCandidates} more candidate(s) omitted.");
        }

        return builder.ToString();
    }

    private static string FormatNullable(double? value)
    {
        return value.HasValue ? Degrees(value.Value) : "n/a";
    }
}
