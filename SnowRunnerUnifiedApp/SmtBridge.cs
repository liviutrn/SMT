using System.IO.MemoryMappedFiles;

namespace SnowRunnerUnified;

internal sealed record SmtLiveState(bool Connected, int Gear, double Clutch, double Throttle, bool EngineRunning, string Status);

internal sealed class SmtBridge : IDisposable
{
    public const string MapName = "Local\\SnowRunnerUnified.v1";
    private const int Capacity = 2048;
    private const uint Magic = 0x31555253; // SRU1
    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private int _sequence;
    private static readonly string[] BindingOrder = ["Reverse", "Neutral", "Gear 1", "Gear 2", "Gear 3", "Gear 4", "Gear 5", "Gear 6", "Range Low", "Range Auto", "Range High"];

    public SmtBridge()
    {
        _map = MemoryMappedFile.CreateOrOpen(MapName, Capacity, MemoryMappedFileAccess.ReadWrite);
        _view = _map.CreateViewAccessor(0, Capacity, MemoryMappedFileAccess.ReadWrite);
        _view.Write(0, Magic);
        _view.Write(4, 1);
    }

    public void Publish(AppSettings settings, bool stallRequest)
    {
        var flags = (settings.Smt.ManualGearbox ? 1 : 0) |
                    (settings.Smt.AnalogClutch ? 2 : 0) |
                    (settings.Smt.RequireClutchForShift ? 4 : 0) |
                    (settings.Smt.StartInNeutral ? 8 : 0) |
                    (settings.Stall.Enabled ? 16 : 0) |
                    (stallRequest ? 32 : 0);
        _view.Write(12, flags);
        _view.Write(16, (float)settings.Clutch.BiteStart);
        _view.Write(20, (float)settings.Clutch.BiteEnd);
        _view.Write(24, (float)settings.Clutch.Curve);
        _view.Write(28, (float)settings.Clutch.IdleThrottle);
        _view.Write(32, (float)settings.Stall.MaxThrottle);
        _view.Write(36, (float)settings.Stall.MinClutchEngagement);
        _view.Write(40, settings.Stall.DelayMilliseconds);
        _view.Write(44, settings.Stall.RestartCooldownMilliseconds);
        _view.Write(48, DateTime.UtcNow.Ticks);
        _view.Write(56, settings.Controls.ClutchAxis.Minimum);
        _view.Write(60, settings.Controls.ClutchAxis.Maximum);
        _view.Write(64, (byte)(settings.Controls.ClutchAxis.Invert ? 1 : 0));
        _view.Write(68, settings.Controls.ThrottleAxis.Minimum);
        _view.Write(72, settings.Controls.ThrottleAxis.Maximum);
        _view.Write(76, (byte)(settings.Controls.ThrottleAxis.Invert ? 1 : 0));
        WriteFixedString(192, 64, settings.Controls.ClutchAxis.Axis);
        WriteFixedString(256, 64, settings.Controls.ThrottleAxis.Axis);
        for (var i = 0; i < BindingOrder.Length; i++)
            WriteFixedString(320 + i * 64, 64, settings.Controls.Bindings.GetValueOrDefault(BindingOrder[i], "NONE"));
        _view.Write(8, ++_sequence); // Publish last so the plug-in never reads a partial update.
    }

    private void WriteFixedString(int offset, int length, string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value ?? "");
        var count = Math.Min(bytes.Length, length - 1);
        var empty = new byte[length];
        Array.Copy(bytes, empty, count);
        _view.WriteArray(offset, empty, 0, empty.Length);
    }

    public SmtLiveState Read()
    {
        var heartbeat = _view.ReadInt64(136);
        var connected = heartbeat > 0 && DateTime.UtcNow.Ticks - heartbeat < TimeSpan.FromSeconds(2).Ticks;
        return new SmtLiveState(
            connected,
            _view.ReadInt32(144),
            Math.Clamp(_view.ReadSingle(148), 0, 1),
            Math.Clamp(_view.ReadSingle(152), 0, 1),
            _view.ReadByte(156) != 0,
            connected ? "SMT v10 bridge connected." : "Waiting for SMT v10 bridge in game.");
    }

    public void Dispose() { _view.Dispose(); _map.Dispose(); }
}
