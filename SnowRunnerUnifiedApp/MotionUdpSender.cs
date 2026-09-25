using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using SnowRunnerTelemetry;

namespace SnowRunnerUnified;

internal sealed class MotionUdpSender : IDisposable
{
    private readonly UdpClient _udp = new();
    private readonly byte[] _packet = new byte[24];
    private readonly double[] _filtered = new double[6];
    private string _endpointKey = "";
    private IPEndPoint? _endpoint;

    public long SentPackets { get; private set; }
    public string Status { get; private set; } = "Motion output waiting.";

    public bool Send(TelemetrySnapshot snapshot, MotionSettings settings)
    {
        if (!settings.Enabled) { Status = "Motion output disabled."; return false; }
        var body = snapshot.RigidBody;
        if (body?.Basis is null || body.LocalAcceleration is null || !snapshot.Attached)
        { Status = "Waiting for chassis motion telemetry."; return false; }

        try
        {
            var key = $"{settings.Host}:{settings.Port}";
            if (_endpoint is null || !string.Equals(key, _endpointKey, StringComparison.OrdinalIgnoreCase))
            {
                var addresses = Dns.GetHostAddresses(settings.Host);
                _endpoint = new IPEndPoint(addresses.First(a => a.AddressFamily == AddressFamily.InterNetwork), settings.Port);
                _endpointKey = key;
            }

            Vector3 accel = body.LocalAcceleration.Value;
            double[] raw = [body.Basis.PitchDeg, body.Basis.RollDeg, body.Basis.YawDeg, accel.X, accel.Y, accel.Z];
            string[] names = ["Pitch", "Roll", "Yaw", "Surge", "Sway", "Heave"];
            for (var i = 0; i < raw.Length; i++)
            {
                var tuning = settings.Axes.TryGetValue(names[i], out var configured) ? configured : new AxisTuning();
                var value = tuning.Enabled ? (raw[i] * tuning.Gain + tuning.Offset) * (tuning.Invert ? -1 : 1) : 0;
                value = Math.Clamp(value, -Math.Abs(tuning.Limit), Math.Abs(tuning.Limit));
                var alpha = 1 - Math.Clamp(tuning.Smoothing, 0, 0.99);
                _filtered[i] += (value - _filtered[i]) * alpha;
                BinaryPrimitives.WriteSingleLittleEndian(_packet.AsSpan(i * 4, 4), (float)_filtered[i]);
            }
            _udp.Send(_packet, _packet.Length, _endpoint);
            SentPackets++;
            Status = $"Sending 60 Hz to {_endpointKey} ({SentPackets:N0} packets).";
            return true;
        }
        catch (Exception error)
        {
            Status = "Motion UDP error: " + error.Message;
            return false;
        }
    }

    public void Dispose() => _udp.Dispose();
}
