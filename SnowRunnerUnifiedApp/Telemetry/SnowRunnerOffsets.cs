namespace SnowRunnerTelemetry;

internal static class SnowRunnerOffsets
{
    public const int SpeedKmh = 0x2A856CC;

    public const int OldTruckControlStatic = 0x2E5DA18;
    public const int OldDriveLogicStatic = 0x2E5DA08;
    public const int TruckControlActiveVehicle = 0x08;
    public const int DriveLogicActiveVehicle = 0x20;
    public const int ObservedTruckControlActiveVehicle = 0xE8;

    public const int VehicleAddonManager = 0x58;
    public const int VehicleRigidBody = 0x5C8;
    public const int VehicleVisualPosition = 0x8EC;

    public const int RigidBodySimulationIsland = 0x128;
    public const int RigidBodyForward = 0x170;
    public const int RigidBodyUp = 0x180;
    public const int RigidBodyRight = 0x190;
    public const int RigidBodyPosition = 0x1A0;
    public const int SnowFlyerRigidBodyPosition = 0x1A8;
    public const int RigidBodySweptPosition0 = 0x1B0;
    public const int RigidBodySweptPosition1 = 0x1C0;
    public const int RigidBodyQuaternion0 = 0x1D0;
    public const int RigidBodyQuaternion1 = 0x1E0;
    public const int RigidBodyLinearVelocity = 0x230;
    public const int RigidBodyAngularVelocity = 0x240;

    public const double FlatPitchOffsetDeg = 14.97;
    public const double FlatRollOffsetDeg = 0.16;

    public const string SnowFlyerPositionPattern = "0F B6 5C 24 70 84 DB 75 43";
}
