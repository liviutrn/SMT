using System.Diagnostics;
using SnowRunnerTelemetry;
using SnowRunnerWheelspinFinder;

namespace SnowRunnerUnified;

internal sealed record UnifiedSnapshot(TelemetrySnapshot Telemetry, FinderSnapshot Wheelspin, SmtLiveState Smt, string MotionStatus, long MotionPackets, bool StallRequested);

internal sealed class UnifiedService : IDisposable
{
    private readonly SnowRunnerTelemetryService _telemetry = new();
    private readonly WheelspinService _wheelspin = new();
    private readonly MotionUdpSender _motion = new();
    private readonly SmtBridge _bridge = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly object _settingsGate = new();
    private AppSettings _settings;
    private Task? _loop;
    private DateTimeOffset? _stallConditionSince;
    private UnifiedSnapshot? _latest;

    public UnifiedService(AppSettings settings) => _settings = SettingsStore.Clone(settings);
    public UnifiedSnapshot? Latest => Volatile.Read(ref _latest);

    public void Start()
    {
        _wheelspin.Start();
        _loop ??= Task.Run(() => RunAsync(_stop.Token));
    }

    public void ApplySettings(AppSettings settings)
    {
        lock (_settingsGate) _settings = SettingsStore.Clone(settings);
    }

    public Task ReconnectAsync() => Task.Run(() =>
    {
        _telemetry.Attach();
        _telemetry.Scan();
        _wheelspin.RestartDiscovery();
    });

    private async Task RunAsync(CancellationToken cancellation)
    {
        _telemetry.Attach();
        _ = Task.Run(() => _telemetry.Scan(), cancellation);
        var stopwatch = Stopwatch.StartNew();
        var next = stopwatch.Elapsed;
        while (!cancellation.IsCancellationRequested)
        {
            AppSettings settings;
            lock (_settingsGate) settings = _settings;
            var telemetry = _telemetry.Poll();
            var wheels = _wheelspin.LatestSnapshot;
            var smt = _bridge.Read();
            var stall = EvaluateStall(settings, wheels, smt);
            _bridge.Publish(settings, stall);
            _motion.Send(telemetry, settings.Motion);
            Volatile.Write(ref _latest, new UnifiedSnapshot(telemetry, wheels, smt, _motion.Status, _motion.SentPackets, stall));

            var rate = Math.Clamp(settings.Motion.RateHz, 20, 120);
            next += TimeSpan.FromSeconds(1d / rate);
            var delay = next - stopwatch.Elapsed;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellation).ConfigureAwait(false);
            else next = stopwatch.Elapsed;
        }
    }

    private bool EvaluateStall(AppSettings settings, FinderSnapshot wheels, SmtLiveState smt)
    {
        if (!settings.Stall.Enabled || !smt.Connected || smt.Gear == 0 || !smt.EngineRunning)
        { _stallConditionSince = null; return false; }

        // A single moving powered wheel cancels a stall request.
        var provenPowered = wheels.Wheels.Where(w => string.Equals(w.DrivenState, "Likely active", StringComparison.OrdinalIgnoreCase)).ToArray();
        var wheelsRelevantForSafety = provenPowered.Length > 0 ? provenPowered : wheels.Wheels;
        var poweredWheelMoving = wheelsRelevantForSafety.Any(w =>
            Math.Abs(w.CalibratedRadPerSecond) >= settings.Stall.WheelMovingRadPerSecond);
        var chassisMoving = Math.Abs(wheels.ChassisSpeed) >= settings.Stall.ChassisMovingMetresPerSecond;
        var shouldCount = !poweredWheelMoving && !chassisMoving &&
                          smt.Throttle <= settings.Stall.MaxThrottle &&
                          smt.Clutch >= settings.Stall.MinClutchEngagement;
        if (!shouldCount) { _stallConditionSince = null; return false; }
        _stallConditionSince ??= DateTimeOffset.UtcNow;
        return DateTimeOffset.UtcNow - _stallConditionSince >= TimeSpan.FromMilliseconds(settings.Stall.DelayMilliseconds);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _telemetry.Dispose(); _wheelspin.Dispose(); _motion.Dispose(); _bridge.Dispose(); _stop.Dispose();
    }
}
