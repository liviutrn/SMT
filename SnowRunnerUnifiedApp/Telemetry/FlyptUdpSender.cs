using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Numerics;

namespace SnowRunnerTelemetry;

internal sealed class FlyptUdpSender : IDisposable
{
    public const int Port = 4123;
    public const int PacketSize = 24;

    private readonly UdpClient _udp = new();
    private readonly IPEndPoint _endpoint = new(IPAddress.Loopback, Port);
    private readonly byte[] _packet = new byte[PacketSize];

    public int SentPackets { get; private set; }

    public string EndpointText => $"127.0.0.1:{Port}";

    public bool TrySend(TelemetrySnapshot snapshot, out string status)
    {
        var basis = snapshot.RigidBody?.Basis;
        var quaternion = snapshot.RigidBody?.Quaternion;
        var velocity = snapshot.RigidBody?.LinearVelocity;
        if (basis is null ||
            quaternion is null ||
            velocity is null ||
            basis.Offset != SnowRunnerOffsets.RigidBodyForward ||
            quaternion.Offset is not SnowRunnerOffsets.RigidBodyQuaternion0 and not SnowRunnerOffsets.RigidBodyQuaternion1 ||
            velocity.Offset != SnowRunnerOffsets.RigidBodyLinearVelocity ||
            !snapshot.Pointer.Source.StartsWith("CONFIRMED ", StringComparison.OrdinalIgnoreCase))
        {
            status = "UDP waiting for confirmed chassis telemetry.";
            return false;
        }

        var acceleration = snapshot.RigidBody?.LocalAcceleration?.Value ?? Vector3.Zero;

        WriteFloat(0, (float)basis.PitchDeg);
        WriteFloat(4, (float)basis.RollDeg);
        WriteFloat(8, (float)basis.YawDeg);
        WriteFloat(12, acceleration.X);
        WriteFloat(16, acceleration.Y);
        WriteFloat(20, acceleration.Z);

        _udp.Send(_packet, _packet.Length, _endpoint);
        SentPackets++;
        status = $"UDP sending to {EndpointText}, {PacketSize} bytes, packet {SentPackets}.";
        return true;
    }

    private void WriteFloat(int offset, float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            value = 0;
        }

        BinaryPrimitives.WriteSingleLittleEndian(_packet.AsSpan(offset, sizeof(float)), value);
    }

    public void Dispose()
    {
        _udp.Dispose();
    }
}
