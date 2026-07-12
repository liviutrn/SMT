using System.Numerics;

namespace SnowRunnerTelemetry;

internal sealed record TelemetryValue<T>(T Value, nint Address, string Source, double Score = 100);

internal sealed record PointerResolution(
    nint StaticAddress,
    nint Singleton,
    nint ActiveVehicle,
    nint RigidBody,
    string Source,
    double Score)
{
    public static PointerResolution Empty { get; } = new(0, 0, 0, 0, "", 0);
}

internal sealed record BasisReading(
    int Offset,
    nint Address,
    Vector3 Forward,
    Vector3 Up,
    Vector3 Right,
    double Score,
    double PitchDeg,
    double RollDeg,
    double YawDeg,
    string Layout);

internal sealed record QuaternionReading(
    int Offset,
    nint Address,
    Quaternion Value,
    double Score,
    double PitchDeg,
    double RollDeg,
    double YawDeg);

internal sealed record VectorReading(
    int Offset,
    nint Address,
    Vector3 Value,
    double Score,
    string Kind,
    string Source);

internal sealed record CandidateReading(
    string Kind,
    string Offset,
    string Address,
    string Value,
    string Score,
    string Source);

internal sealed record ScanProgress(
    string Phase,
    string Detail,
    long Regions,
    long Chunks,
    long BytesRead,
    long AddressesScanned,
    long Hits,
    long Candidates,
    long TypeDescriptors,
    long VTables,
    long Instances,
    long VehiclePointers,
    long RigidBodyPointers,
    bool Important);

internal sealed record RigidBodyTelemetry(
    BasisReading? Basis,
    QuaternionReading? Quaternion,
    VectorReading? Position,
    VectorReading? LinearVelocity,
    VectorReading? LocalAcceleration,
    float? LinearSpeedKmh,
    string Validation);

internal sealed record TelemetrySnapshot(
    DateTimeOffset Timestamp,
    bool Attached,
    string Status,
    nint ModuleBase,
    int ModuleSize,
    string ModuleVersion,
    TelemetryValue<float>? SpeedKmh,
    TelemetryValue<Vector3>? SnowFlyerPosition,
    PointerResolution Pointer,
    RigidBodyTelemetry? RigidBody,
    IReadOnlyList<CandidateReading> Candidates);
