using System.Globalization;
using System.Numerics;

namespace SnowRunnerWheelspinFinder;

internal enum CapturePhase
{
    None,
    Parked,
    Forward,
    Reverse,
    Steering,
    StationaryWheelspin,
    PhysicallyBlocked,
    StationaryGear1,
    StationaryGear2,
    StationaryGear3,
    StationaryGear4,
    StationaryGear5,
    StationaryReverse,
    Stationary2wdGear1,
    Stationary2wdGear2,
    Stationary4wdGear1,
    Stationary4wdGear2
}

internal sealed record BasisState(Vector3 Forward, Vector3 Up, Vector3 Right, double Score);

internal sealed record RootState(
    nint SnowFlyerStatic,
    nint TruckControl,
    nint ActiveVehicle,
    nint Chassis,
    string ChassisPath,
    string VehiclePath,
    int Epoch);

internal sealed record DiscoveryProgress(
    string Phase,
    string Detail,
    long Regions,
    long Chunks,
    long BytesRead,
    long Addresses,
    long Hits,
    long Candidates,
    double ElapsedSeconds)
{
    public static DiscoveryProgress Idle { get; } = new("Waiting", "No discovery is running.", 0, 0, 0, 0, 0, 0, 0);
}

internal sealed record CandidateSnapshot(
    string Id,
    long Ordinal,
    string Kind,
    int OwnerIndex,
    nint Address,
    string PointerPath,
    double Current,
    double Minimum,
    double Maximum,
    long Samples,
    long Changes,
    double SampleHz,
    double ChangeHz,
    double Score,
    string State,
    string Details);

internal sealed record WheelSnapshot(
    string Id,
    long Ordinal,
    int IslandIndex,
    nint BodyAddress,
    string PointerPath,
    string Side,
    string Axle,
    string Position,
    double RawSignedAngularVelocity,
    double CalibratedRadPerSecond,
    double Rpm,
    double? EstimatedRadiusMetres,
    double? TangentialTreadSpeed,
    string DrivenState,
    string DrivenConfidence,
    double? SlipSpeed,
    double? SlipRatio,
    double UpdateHz,
    double Confidence,
    string Validation,
    string ProofSource,
    double HistoricalMaxAbsRadPerSecond,
    bool ParkedPassed,
    bool ForwardPassed,
    bool ReversePassed,
    bool SteeringPassed,
    bool WheelspinPassed,
    bool BlockedPassed,
    bool ResumedPassed,
    double QuaternionAgreement,
    IReadOnlyList<CaptureEvidenceSnapshot> CaptureEvidence);

internal sealed record CaptureEvidenceSnapshot(
    CapturePhase Phase,
    long Samples,
    long Changes,
    long NearStationarySamples,
    long StationarySpinSamples,
    double MeanSignedRadPerSecond,
    double MeanAbsRadPerSecond,
    double MinimumRadPerSecond,
    double MaximumRadPerSecond,
    double MaxAbsRadPerSecond,
    double MeanSignedRadPerSecondNearStationary,
    double MeanAbsRadPerSecondNearStationary,
    double MeanChassisMetresPerSecond,
    double MaxAbsChassisMetresPerSecond);

internal sealed record CaptureStatus(
    CapturePhase Phase,
    bool Active,
    bool Recording,
    double RemainingSeconds,
    string Message);

internal sealed record FinderSnapshot(
    DateTimeOffset Timestamp,
    bool Attached,
    bool Paused,
    string Status,
    string Safety,
    int ProcessId,
    DateTime ProcessStartUtc,
    nint ModuleBase,
    int ModuleSize,
    string ModulePath,
    string GameVersion,
    string ModuleFingerprint,
    RootState? Root,
    nint Island,
    nint IslandArray,
    int IslandCount,
    string IslandPath,
    nint WheelModelArray,
    int WheelModelCount,
    string WheelModelPath,
    double ChassisLongitudinalSpeed,
    double ChassisSpeed,
    double AcquisitionHz,
    double PhysicsChangeHz,
    double? DrivelineRadPerSecond,
    double? DrivelineRpm,
    string DrivelineAssumption,
    CaptureStatus Capture,
    DiscoveryProgress Progress,
    IReadOnlyList<WheelSnapshot> Wheels,
    IReadOnlyList<WheelSnapshot> CaptureEvidenceBodies,
    IReadOnlyList<CandidateSnapshot> Candidates,
    IReadOnlyList<string> Logs)
{
    public static FinderSnapshot Empty(string status) => new(
        DateTimeOffset.Now,
        Attached: false,
        Paused: false,
        status,
        "READ-ONLY: process query + memory reads only; no control or game modification.",
        0,
        DateTime.MinValue,
        0,
        0,
        "",
        "",
        "",
        null,
        0,
        0,
        0,
        "",
        0,
        0,
        "",
        0,
        0,
        0,
        0,
        null,
        null,
        "No powered axle is proven yet.",
        new CaptureStatus(CapturePhase.None, false, false, 0, "Choose a guided proof when the truck is ready."),
        DiscoveryProgress.Idle,
        Array.Empty<WheelSnapshot>(),
        Array.Empty<WheelSnapshot>(),
        Array.Empty<CandidateSnapshot>(),
        Array.Empty<string>());
}

internal static class FinderMath
{
    public const double TwoPi = Math.PI * 2.0;

    public static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    public static bool IsFinite(Vector3 value) => IsFinite(value.X) && IsFinite(value.Y) && IsFinite(value.Z);

    public static bool IsFinite(Quaternion value) =>
        IsFinite(value.X) && IsFinite(value.Y) && IsFinite(value.Z) && IsFinite(value.W);

    public static Vector3 NormalizeOrZero(Vector3 value)
    {
        return IsFinite(value) && value.LengthSquared() > 1e-8f ? Vector3.Normalize(value) : Vector3.Zero;
    }

    public static double ScoreBasis(Vector3 forward, Vector3 up, Vector3 right)
    {
        if (!IsFinite(forward) || !IsFinite(up) || !IsFinite(right))
        {
            return 0;
        }

        var lengths = Math.Abs(forward.Length() - 1) + Math.Abs(up.Length() - 1) + Math.Abs(right.Length() - 1);
        if (forward.Length() is < 0.25f or > 2f || up.Length() is < 0.25f or > 2f || right.Length() is < 0.25f or > 2f)
        {
            return 0;
        }

        var f = Vector3.Normalize(forward);
        var u = Vector3.Normalize(up);
        var r = Vector3.Normalize(right);
        var orthogonal = Math.Abs(Vector3.Dot(f, u)) + Math.Abs(Vector3.Dot(f, r)) + Math.Abs(Vector3.Dot(u, r));
        return ClampScore(100 - lengths * 30 - orthogonal * 45);
    }

    public static double ScoreQuaternion(Quaternion value)
    {
        if (!IsFinite(value))
        {
            return 0;
        }

        var length = value.Length();
        return length is < 0.3f or > 1.7f ? 0 : ClampScore(100 - Math.Abs(length - 1) * 130);
    }

    public static Vector3 QuaternionAngularVelocity(Quaternion previous, Quaternion current, double seconds)
    {
        if (seconds is <= 0 or > 0.25 || ScoreQuaternion(previous) < 50 || ScoreQuaternion(current) < 50)
        {
            return Vector3.Zero;
        }

        var delta = Quaternion.Normalize(current) * Quaternion.Inverse(Quaternion.Normalize(previous));
        if (delta.W < 0)
        {
            delta = new Quaternion(-delta.X, -delta.Y, -delta.Z, -delta.W);
        }

        var vector = new Vector3(delta.X, delta.Y, delta.Z);
        var length = vector.Length();
        if (length < 1e-7f)
        {
            return Vector3.Zero;
        }

        var angle = 2.0 * Math.Atan2(length, Math.Clamp(delta.W, -1f, 1f));
        return Vector3.Normalize(vector) * (float)(angle / seconds);
    }

    public static double Median(IEnumerable<double> values)
    {
        var ordered = values.Where(double.IsFinite).OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
        {
            return 0;
        }

        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) / 2.0 : ordered[middle];
    }

    public static double ClampScore(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;

    public static string Address(nint value) => value == 0 ? "n/a" : $"0x{(ulong)(long)value:X16}";

    public static string Number(double value, string format = "0.000") => value.ToString(format, CultureInfo.InvariantCulture);
}
