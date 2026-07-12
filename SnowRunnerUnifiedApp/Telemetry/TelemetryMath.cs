using System.Numerics;

namespace SnowRunnerTelemetry;

internal static class TelemetryMath
{
    private const double RadToDeg = 180.0 / Math.PI;

    public static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    public static bool IsFinite(Vector3 value)
    {
        return IsFinite(value.X) && IsFinite(value.Y) && IsFinite(value.Z);
    }

    public static bool IsFinite(Quaternion value)
    {
        return IsFinite(value.X) && IsFinite(value.Y) && IsFinite(value.Z) && IsFinite(value.W);
    }

    public static Vector3 NormalizeOrZero(Vector3 value)
    {
        return value.LengthSquared() < 1e-8f ? Vector3.Zero : Vector3.Normalize(value);
    }

    public static double ScoreBasis(Vector3 forward, Vector3 up, Vector3 right)
    {
        if (!IsFinite(forward) || !IsFinite(up) || !IsFinite(right))
        {
            return 0;
        }

        var lf = forward.Length();
        var lu = up.Length();
        var lr = right.Length();
        if (lf < 0.2f || lu < 0.2f || lr < 0.2f || lf > 2f || lu > 2f || lr > 2f)
        {
            return 0;
        }

        var f = Vector3.Normalize(forward);
        var u = Vector3.Normalize(up);
        var r = Vector3.Normalize(right);

        var lengthPenalty = (Math.Abs(lf - 1) + Math.Abs(lu - 1) + Math.Abs(lr - 1)) * 28.0;
        var dotPenalty = (Math.Abs(Vector3.Dot(f, u)) + Math.Abs(Vector3.Dot(f, r)) + Math.Abs(Vector3.Dot(u, r))) * 42.0;
        var handedness = Vector3.Dot(Vector3.Cross(f, r), u);
        var handedPenalty = Math.Abs(1.0 - handedness) * 18.0;

        return ClampScore(100.0 - lengthPenalty - dotPenalty - handedPenalty);
    }

    public static double ScoreQuaternion(Quaternion quaternion)
    {
        if (!IsFinite(quaternion))
        {
            return 0;
        }

        var len = Math.Sqrt(
            quaternion.X * quaternion.X +
            quaternion.Y * quaternion.Y +
            quaternion.Z * quaternion.Z +
            quaternion.W * quaternion.W);

        if (len < 0.25 || len > 1.75)
        {
            return 0;
        }

        return ClampScore(100.0 - Math.Abs(len - 1.0) * 120.0);
    }

    public static double ScorePosition(Vector3 value, Vector3? knownPosition)
    {
        if (!IsFinite(value) || Math.Abs(value.X) > 1_000_000 || Math.Abs(value.Y) > 1_000_000 || Math.Abs(value.Z) > 1_000_000)
        {
            return 0;
        }

        if (!knownPosition.HasValue || !IsFinite(knownPosition.Value))
        {
            return 55;
        }

        var distance = Vector3.Distance(value, knownPosition.Value);
        return ClampScore(100.0 - Math.Min(100.0, distance * 6.0));
    }

    public static double ScoreVelocity(Vector3 value, float? speedKmh)
    {
        if (!IsFinite(value) || Math.Abs(value.X) > 1000 || Math.Abs(value.Y) > 1000 || Math.Abs(value.Z) > 1000)
        {
            return 0;
        }

        if (!speedKmh.HasValue || !IsFinite(speedKmh.Value))
        {
            return 60;
        }

        var velocityKmh = value.Length() * 3.6;
        var error = Math.Abs(velocityKmh - Math.Abs(speedKmh.Value));
        return ClampScore(100.0 - Math.Min(100.0, error * 3.0));
    }

    public static (double PitchDeg, double RollDeg, double YawDeg) EulerFromBasis(Vector3 forward, Vector3 up, Vector3 right)
    {
        var f = NormalizeOrZero(forward);
        var u = NormalizeOrZero(up);
        var r = NormalizeOrZero(right);

        var pitch = Math.Atan2(f.Y, Math.Sqrt(f.X * f.X + f.Z * f.Z)) * RadToDeg;
        var roll = Math.Atan2(r.Y, u.Y) * RadToDeg;
        var yaw = Math.Atan2(f.X, f.Z) * RadToDeg;

        return (pitch, roll, yaw);
    }

    public static double NormalizeDegrees(double value)
    {
        while (value > 180)
        {
            value -= 360;
        }

        while (value <= -180)
        {
            value += 360;
        }

        return value;
    }

    public static (Vector3 Forward, Vector3 Up, Vector3 Right) BasisFromQuaternion(Quaternion quaternion)
    {
        if (quaternion.LengthSquared() < 1e-8f)
        {
            return (Vector3.Zero, Vector3.Zero, Vector3.Zero);
        }

        var q = Quaternion.Normalize(quaternion);
        return (
            Vector3.Transform(Vector3.UnitZ, q),
            Vector3.Transform(Vector3.UnitY, q),
            Vector3.Transform(Vector3.UnitX, q));
    }

    public static double ClampScore(double score)
    {
        if (double.IsNaN(score) || double.IsInfinity(score))
        {
            return 0;
        }

        return Math.Max(0, Math.Min(100, score));
    }
}
