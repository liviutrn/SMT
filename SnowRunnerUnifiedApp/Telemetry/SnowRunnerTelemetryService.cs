using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace SnowRunnerTelemetry;

internal sealed class SnowRunnerTelemetryService : IDisposable
{
    private readonly object _gate = new();
    private ProcessMemoryReader? _memory;
    private nint _snowFlyerPositionBase;
    private nint _snowFlyerRigidBody;
    private PointerResolution _pointer = PointerResolution.Empty;
    private int? _preferredBasisOffset;
    private int[] _cachedTruckControlStaticRvas = Array.Empty<int>();
    private IReadOnlyList<CandidateReading> _lastCandidates = Array.Empty<CandidateReading>();
    private readonly List<LiveRigidBodyProbe> _liveProbes = new();
    private nint _accelerationRigidBody;
    private int _accelerationVelocityOffset;
    private Vector3 _previousVelocityWorld;
    private Vector3 _smoothedLocalAcceleration;
    private DateTimeOffset _previousVelocityTime;
    private bool _hasAccelerationSample;
    private string _lastStatus = "Not attached.";
    private string _lastDiagnostics = "";

    public string LastDiagnostics
    {
        get
        {
            lock (_gate)
            {
                return _lastDiagnostics;
            }
        }
    }

    public bool IsAttached
    {
        get
        {
            lock (_gate)
            {
                try
                {
                    return _memory is not null && !_memory.Process.HasExited;
                }
                catch
                {
                    return false;
                }
            }
        }
    }

    public int AttachedProcessId
    {
        get
        {
            lock (_gate)
            {
                try
                {
                    return _memory is not null && !_memory.Process.HasExited ? _memory.Process.Id : 0;
                }
                catch
                {
                    return 0;
                }
            }
        }
    }

    public bool HasLiveCandidateTracking
    {
        get
        {
            lock (_gate)
            {
                return _liveProbes.Count > 0 && _pointer.RigidBody == 0;
            }
        }
    }

    public string Attach()
    {
        lock (_gate)
        {
            DisposeReader();

            if (!ProcessMemoryReader.TryAttach(out _memory, out var message))
            {
                _lastStatus = message;
                return message;
            }

            var memory = _memory!;
            _cachedTruckControlStaticRvas = TelemetryOffsetCacheStore
                .Load(memory.VersionText, memory.ModuleSize)?
                .TruckControlStaticRvas ?? Array.Empty<int>();
            _lastStatus = message;
            TryResolveInitialTelemetry();
            return _lastStatus;
        }
    }

    public string Scan(IProgress<ScanProgress>? progress = null)
    {
        lock (_gate)
        {
            var stats = new ScanStats(progress);
            stats.Report("Direct", "Resolving confirmed SnowRunner offsets only.", force: true);

            if (!EnsureAttached())
            {
                stats.Report("Attach", _lastStatus, force: true, important: true);
                return _lastStatus;
            }

            _lastDiagnostics = "";
            _liveProbes.Clear();
            ResetAccelerationTracker();
            ResolveSnowFlyerPositionSignature(force: true);
            var position = TryReadSnowFlyerPosition();
            var speed = TryReadSpeed();
            stats.Report("Known telemetry", $"speed {(speed is null ? "n/a" : speed.Value.ToString("0.000"))}, position {(position is null ? "n/a" : TelemetryFormatting.Vector(position.Value))}", force: true, important: true);
            ResolveDirectActiveVehicle(stats, includeLiveInstanceResolver: false);
            if (_pointer.RigidBody == 0)
            {
                ResolveActiveVehicleByRtti(position?.Value, speed?.Value, stats);
            }

            if (_pointer.RigidBody != 0)
            {
                var resolvedCandidates = _lastCandidates.ToArray();
                var rigidBodyCandidates = BuildRigidBodyTelemetry(_pointer.RigidBody, position?.Value, speed?.Value, _preferredBasisOffset, IsLockedOffsetPointer(_pointer)).Candidates;
                _lastCandidates = resolvedCandidates
                    .Concat(rigidBodyCandidates)
                    .Take(120)
                    .ToArray();
            }

            _lastStatus = _pointer.RigidBody == 0
                ? "Waiting for live truck object. Keep the truck loaded in-game."
                : $"Direct telemetry locked. Active rigid body {_pointer.RigidBody:X}.";
            stats.Report("Finished", _lastStatus, force: true, important: true);
            return _lastStatus;
        }
    }

    public TelemetrySnapshot Poll()
    {
        lock (_gate)
        {
            if (_memory is null)
            {
                return EmptySnapshot(_lastStatus);
            }

            if (_memory.Process.HasExited)
            {
                _lastStatus = "SnowRunner.exe exited.";
                DisposeReader();
                return EmptySnapshot(_lastStatus);
            }

            var memory = _memory!;
            if (_pointer.RigidBody != 0 && !memory.IsReadable(_pointer.RigidBody, 0x20))
            {
                _pointer = PointerResolution.Empty;
                ResetAccelerationTracker();
            }

            var speed = TryReadSpeed();
            var position = TryReadSnowFlyerPosition();
            var stableChassisLocked = TryResolveSnowFlyerRigidBody(position);
            if (!stableChassisLocked &&
                _pointer.Source.Contains("SnowFlyer position chain", StringComparison.OrdinalIgnoreCase))
            {
                _pointer = PointerResolution.Empty;
                ResetAccelerationTracker();
            }

            if (_pointer.RigidBody == 0 && _liveProbes.Count > 0)
            {
                UpdateLiveProbes(speed?.Value);
            }

            if (_pointer.RigidBody == 0)
            {
                ResolveDirectActiveVehicle(includeLiveInstanceResolver: false);
            }

            RigidBodyTelemetry? rigidBody = null;
            var candidates = new List<CandidateReading>();
            candidates.AddRange(BuildLiveProbeRows());
            if (_pointer.RigidBody != 0)
            {
                var result = BuildRigidBodyTelemetry(_pointer.RigidBody, position?.Value, speed?.Value, _preferredBasisOffset, IsLockedOffsetPointer(_pointer));
                rigidBody = IsTrialPointer(_pointer)
                    ? result.Telemetry with { Validation = "LIVE PROBE candidate. Check whether pitch/roll follow the truck. " + result.Telemetry.Validation }
                    : result.Telemetry;
                candidates.AddRange(result.Candidates);
            }

            candidates.AddRange(_lastCandidates.Where(c => candidates.All(existing =>
                existing.Kind != c.Kind || existing.Offset != c.Offset || existing.Address != c.Address)));

            _lastStatus = _pointer.RigidBody == 0
                ? _liveProbes.Count > 0
                    ? "Tracking chassis movement; drive normally."
                    : "Waiting for live truck object."
                : "Reading locked SnowRunner telemetry.";
            return new TelemetrySnapshot(
                DateTimeOffset.Now,
                Attached: true,
                _lastStatus,
                memory.ModuleBase,
                memory.ModuleSize,
                memory.VersionText,
                speed,
                position,
                _pointer,
                rigidBody,
                candidates.Take(120).ToArray());
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            DisposeReader();
        }
    }

    public string LockCandidate(nint rigidBody, int? basisOffset, string source)
    {
        lock (_gate)
        {
            if (!EnsureAttached())
            {
                return _lastStatus;
            }

            var memory = _memory;
            if (memory is null || rigidBody == 0 || !memory.IsReadable(rigidBody, 0x20))
            {
                _lastStatus = "Selected candidate is not readable anymore.";
                return _lastStatus;
            }

            _pointer = new PointerResolution(
                0,
                0,
                0,
                rigidBody,
                "MANUAL LOCK " + source,
                100);
            _preferredBasisOffset = basisOffset;
            ResetAccelerationTracker();
            _lastStatus = basisOffset.HasValue
                ? $"Locked selected candidate {rigidBody:X} basis +0x{basisOffset.Value:X}."
                : $"Locked selected candidate {rigidBody:X}.";
            return _lastStatus;
        }
    }

    private bool EnsureAttached()
    {
        try
        {
            if (_memory is not null && !_memory.Process.HasExited)
            {
                return true;
            }
        }
        catch
        {
        }

        DisposeReader();
        return ProcessMemoryReader.TryAttach(out _memory, out _lastStatus);
    }

    private static bool IsTrialPointer(PointerResolution pointer)
    {
        return pointer.Source.StartsWith("TRIAL ", StringComparison.OrdinalIgnoreCase) ||
               pointer.Source.StartsWith("LIVE PROBE ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLockedOffsetPointer(PointerResolution pointer)
    {
        return pointer.Source.StartsWith("CONFIRMED ", StringComparison.OrdinalIgnoreCase);
    }

    private void ArmLiveProbes(IEnumerable<NearRigidBodyCandidate> nearCandidates)
    {
        _liveProbes.Clear();
        var seen = new HashSet<nint>();
        foreach (var candidate in nearCandidates
                     .Where(candidate =>
                         candidate.Evaluation.BasisOffset == SnowRunnerOffsets.RigidBodyForward &&
                         candidate.Evaluation.QuaternionOffset is SnowRunnerOffsets.RigidBodyQuaternion0 or SnowRunnerOffsets.RigidBodyQuaternion1 &&
                         candidate.Evaluation.VelocityScore > 0)
                     .OrderByDescending(candidate => candidate.Score)
                     .Take(80))
        {
            if (!seen.Add(candidate.Pointer.RigidBody))
            {
                continue;
            }

            _liveProbes.Add(new LiveRigidBodyProbe(candidate, _liveProbes.Count + 1));
            if (_liveProbes.Count >= 40)
            {
                break;
            }
        }
    }

    private void UpdateLiveProbes(float? speedKmh)
    {
        var memory = _memory;
        if (memory is null || _liveProbes.Count == 0)
        {
            return;
        }

        foreach (var probe in _liveProbes)
        {
            if (!memory.IsReadable(probe.Pointer.RigidBody, 0x20) ||
                !TryReadBasis(probe.Pointer.RigidBody, probe.BasisOffset, out var basis))
            {
                continue;
            }

            probe.Samples++;
            probe.CurrentPitchDeg = basis.PitchDeg;
            probe.CurrentRollDeg = basis.RollDeg;
            probe.CurrentYawDeg = basis.YawDeg;
            probe.LastPitchRollDeltaDeg =
                Math.Abs(AngleDeltaDegrees(basis.PitchDeg, probe.StartPitchDeg)) +
                Math.Abs(AngleDeltaDegrees(basis.RollDeg, probe.StartRollDeg));
            probe.MaxPitchRollDeltaDeg = Math.Max(probe.MaxPitchRollDeltaDeg, probe.LastPitchRollDeltaDeg);
            probe.MaxYawDeltaDeg = Math.Max(probe.MaxYawDeltaDeg, Math.Abs(AngleDeltaDegrees(basis.YawDeg, probe.StartYawDeg)));

            if (TryReadVector3(probe.Pointer.RigidBody + SnowRunnerOffsets.RigidBodyLinearVelocity, out var velocity))
            {
                probe.LastLinearSpeedKmh = velocity.Length() * 3.6;
                probe.MaxLinearSpeedKmh = Math.Max(probe.MaxLinearSpeedKmh, probe.LastLinearSpeedKmh);
                probe.VelocitySamples++;
                if (speedKmh.HasValue && Math.Abs(speedKmh.Value) >= 0.25)
                {
                    probe.SpeedComparisonSamples++;
                    probe.SpeedErrorTotalKmh += Math.Abs(probe.LastLinearSpeedKmh - Math.Abs(speedKmh.Value));
                }
            }
        }

        if (_pointer.RigidBody != 0 && !IsTrialPointer(_pointer))
        {
            return;
        }

        var bestMovingProbe = _liveProbes
            .Where(probe => probe.Samples >= 8 &&
                            probe.VelocitySamples >= 4 &&
                            probe.MaxAttitudeDeltaDeg >= 1.2 &&
                            (probe.ConfirmedShapeScore >= 170 || probe.SpeedComparisonSamples >= 3) &&
                            (probe.SpeedComparisonSamples == 0 ||
                             probe.AverageSpeedErrorKmh <= Math.Max(4.0, Math.Abs(speedKmh ?? 0) * 0.75)))
            .OrderBy(probe => probe.AverageSpeedErrorKmh)
            .ThenByDescending(probe => probe.ConfirmedShapeScore)
            .ThenByDescending(probe => probe.MaxAttitudeDeltaDeg)
            .ThenByDescending(probe => probe.SeedScore)
            .FirstOrDefault();
        if (bestMovingProbe is not null)
        {
            var previousRigidBody = _pointer.RigidBody;
            _pointer = bestMovingProbe.Pointer with { Source = "CONFIRMED " + bestMovingProbe.Pointer.Source, Score = 100 };
            _preferredBasisOffset = SnowRunnerOffsets.RigidBodyForward;
            if (previousRigidBody != _pointer.RigidBody)
            {
                ResetAccelerationTracker();
                var module = memory.ReadRangeLossy(memory.ModuleBase, memory.ModuleSize);
                CacheTruckControlStaticReferences(module, _pointer.Singleton);
            }

            _lastDiagnostics += $" Live probe lock: {TelemetryFormatting.Address(_pointer.RigidBody)} movement {bestMovingProbe.MaxAttitudeDeltaDeg:0.0} deg, shape {bestMovingProbe.ConfirmedShapeScore:0}.";
        }
    }

    private IReadOnlyList<CandidateReading> BuildLiveProbeRows()
    {
        if (_liveProbes.Count == 0)
        {
            return Array.Empty<CandidateReading>();
        }

        return _liveProbes
            .Where(probe => probe.Samples < 20 || probe.HasMeaningfulMovement)
            .OrderBy(probe => probe.DisplayRank)
            .Take(40)
            .Select(probe => new CandidateReading(
                "live probe",
                $"{probe.ActiveOffsetPair} / basis {TelemetryFormatting.Offset(probe.BasisOffset)}",
                TelemetryFormatting.Address(probe.Pointer.RigidBody),
                $"move {probe.MaxAttitudeDeltaDeg:0.00} deg (att {probe.MaxPitchRollDeltaDeg:0.00}, yaw {probe.MaxYawDeltaDeg:0.00}), now p/r/y {probe.CurrentPitchDeg:0.0}/{probe.CurrentRollDeg:0.0}/{probe.CurrentYawDeg:0.0}, velocity {probe.LastLinearSpeedKmh:0.00} km/h, speed error {probe.AverageSpeedErrorKmh:0.00}, samples {probe.Samples}, seed {probe.SeedSummary}",
                TelemetryFormatting.Score(probe.MovementScore),
                "Live change tracker after scan"))
            .ToArray();
    }

    private static double AngleDeltaDegrees(double value, double baseline)
    {
        var delta = value - baseline;
        while (delta > 180)
        {
            delta -= 360;
        }

        while (delta < -180)
        {
            delta += 360;
        }

        return delta;
    }

    private void TryResolveInitialTelemetry()
    {
        try
        {
            ResolveSnowFlyerPositionSignature(force: true);
            ResolveDirectActiveVehicle(includeLiveInstanceResolver: false);
        }
        catch (Exception ex)
        {
            _lastStatus = $"Attached, but initial telemetry resolve failed: {ex.Message}";
        }
    }

    private void DisposeReader()
    {
        _memory?.Dispose();
        _memory = null;
        _snowFlyerPositionBase = 0;
        _snowFlyerRigidBody = 0;
        _pointer = PointerResolution.Empty;
        _preferredBasisOffset = null;
        _cachedTruckControlStaticRvas = Array.Empty<int>();
        _lastCandidates = Array.Empty<CandidateReading>();
        _liveProbes.Clear();
        ResetAccelerationTracker();
    }

    private void ResetAccelerationTracker()
    {
        _accelerationRigidBody = 0;
        _accelerationVelocityOffset = 0;
        _previousVelocityWorld = Vector3.Zero;
        _smoothedLocalAcceleration = Vector3.Zero;
        _previousVelocityTime = DateTimeOffset.MinValue;
        _hasAccelerationSample = false;
    }

    private TelemetrySnapshot EmptySnapshot(string status)
    {
        return new TelemetrySnapshot(
            DateTimeOffset.Now,
            Attached: false,
            status,
            0,
            0,
            "",
            null,
            null,
            PointerResolution.Empty,
            null,
            Array.Empty<CandidateReading>());
    }

    private TelemetryValue<float>? TryReadSpeed()
    {
        var memory = _memory;
        if (memory is null)
        {
            return null;
        }

        var address = memory.ModuleBase + SnowRunnerOffsets.SpeedKmh;
        if (!memory.TryRead<float>(address, out var value) || !TelemetryMath.IsFinite(value) || Math.Abs(value) > 1000)
        {
            return null;
        }

        return new TelemetryValue<float>(value, address, "SpeedHud static speed offset");
    }

    private void ResolveSnowFlyerPositionSignature(bool force)
    {
        var memory = _memory;
        if (memory is null || (_snowFlyerPositionBase != 0 && !force))
        {
            return;
        }

        _snowFlyerPositionBase = 0;
        var module = memory.ReadRangeLossy(memory.ModuleBase, memory.ModuleSize);
        var pattern = BytePattern.Parse(SnowRunnerOffsets.SnowFlyerPositionPattern);
        var offset = PatternScanner.FindFirst(module, pattern);
        if (offset < 0)
        {
            return;
        }

        var instructionAfterPattern = memory.ModuleBase + offset + pattern.Length;
        if (!TryResolveRelativeTarget(instructionAfterPattern, 1, out var callTarget))
        {
            return;
        }

        if (TryResolveRelativeTarget(callTarget, 3, out var singletonAddress))
        {
            _snowFlyerPositionBase = singletonAddress;
        }
    }

    private void ResolveDirectActiveVehicle(ScanStats? stats = null, bool includeLiveInstanceResolver = false)
    {
        var memory = _memory;
        if (memory is null)
        {
            return;
        }

        var positionReading = TryReadSnowFlyerPosition();
        if (TryResolveSnowFlyerRigidBody(positionReading, stats))
        {
            return;
        }

        var knownPosition = positionReading?.Value;
        var speedKmh = TryReadSpeed()?.Value;
        var candidates = new List<(PointerResolution Pointer, CandidateReading Row)>();

        var cachedOffsets = _cachedTruckControlStaticRvas;
        foreach (var offset in cachedOffsets)
        {
            TryAddDirectCandidate(
                offset,
                SnowRunnerOffsets.ObservedTruckControlActiveVehicle,
                "CACHED TRUCK_CONTROL");
        }

        var best = candidates
            .Where(c => IsConfirmedDirectRigidBody(c.Pointer))
            .OrderByDescending(c => c.Pointer.Source.Contains("TRUCK_CONTROL", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(c => c.Pointer.Score)
            .FirstOrDefault();

        if ((best.Pointer is null || best.Pointer.RigidBody == 0) && includeLiveInstanceResolver)
        {
            var live = TryResolveConfirmedTruckControlInstance(knownPosition, speedKmh, stats);
            if (live.Pointer is not null && live.Pointer.RigidBody != 0)
            {
                candidates.Add(live);
                best = live;
            }
        }

        if (best.Pointer is not null && best.Pointer.RigidBody != 0)
        {
            if (_pointer.RigidBody != best.Pointer.RigidBody)
            {
                ResetAccelerationTracker();
            }

            var confirmedSource = best.Pointer.Source.StartsWith("CONFIRMED ", StringComparison.OrdinalIgnoreCase)
                ? best.Pointer.Source
                : "CONFIRMED " + best.Pointer.Source;
            _pointer = best.Pointer with
            {
                Source = confirmedSource,
                Score = 100
            };
            _preferredBasisOffset = SnowRunnerOffsets.RigidBodyForward;
        }
        else
        {
            _pointer = PointerResolution.Empty;
        }

        _lastCandidates = candidates
            .OrderByDescending(c => c.Pointer.Score)
            .Select(c => c.Row)
            .Take(30)
            .ToArray();

        _lastDiagnostics = _pointer.RigidBody == 0
            ? includeLiveInstanceResolver
                ? "Live truck lookup completed; no confirmed chassis is loaded yet."
                : $"Cached resolver: tested {cachedOffsets.Length} learned module reference(s); live object lookup pending."
            : $"Direct resolver: {_pointer.Source}, vehicle {TelemetryFormatting.Address(_pointer.ActiveVehicle)}, rigid body {TelemetryFormatting.Address(_pointer.RigidBody)}.";
        stats?.SetCandidates(candidates.Count);
        stats?.Report(includeLiveInstanceResolver ? "Live offsets" : "Direct offsets", _lastDiagnostics, force: true, important: true);

        void TryAddDirectCandidate(int staticOffset, int activeVehicleOffset, string source)
        {
            if (staticOffset < 0 || staticOffset + IntPtr.Size > memory.ModuleSize)
            {
                return;
            }

            var staticAddress = memory.ModuleBase + staticOffset;
            var pointer = ValidatePointerCandidate(
                staticAddress,
                activeVehicleOffset,
                knownPosition,
                $"{source} static {TelemetryFormatting.Offset(staticOffset)} active {TelemetryFormatting.Offset(activeVehicleOffset)} vehicle {TelemetryFormatting.Offset(SnowRunnerOffsets.VehicleRigidBody)}");

            if (pointer.RigidBody == 0)
            {
                return;
            }

            candidates.Add((pointer, new CandidateReading(
                "direct",
                TelemetryFormatting.Offset(staticOffset),
                TelemetryFormatting.Address(staticAddress),
                $"vehicle {TelemetryFormatting.Address(pointer.ActiveVehicle)} rb {TelemetryFormatting.Address(pointer.RigidBody)}",
                TelemetryFormatting.Score(pointer.Score),
                pointer.Source)));
        }
    }

    private (PointerResolution Pointer, CandidateReading Row) TryResolveConfirmedTruckControlInstance(Vector3? knownPosition, float? speedKmh, ScanStats? stats)
    {
        var memory = _memory;
        if (memory is null)
        {
            return default;
        }

        stats?.Report("Live object", "Locating the current TRUCK_CONTROL object for this game launch.", force: true, important: true);
        var module = memory.ReadRangeLossy(memory.ModuleBase, memory.ModuleSize);
        stats?.AddChunk(memory.ModuleSize, Math.Max(1, memory.ModuleSize / 8));

        var typeDescriptors = FindRttiTypeDescriptors(module, "TRUCK_CONTROL").ToArray();
        var vtables = FindRttiVtables(module, typeDescriptors)
            .Where(v => v.ClassName.Equals("TRUCK_CONTROL", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        stats?.SetRttiCounts(typeDescriptors.Length, vtables.Length);
        stats?.Report("Live object", $"{typeDescriptors.Length} TRUCK_CONTROL RTTI descriptor(s), {vtables.Length} vtable(s).", force: true, important: true);

        if (vtables.Length == 0)
        {
            return default;
        }

        var vtableMap = vtables
            .GroupBy(v => unchecked((ulong)(long)v.VTable))
            .ToDictionary(g => g.Key, g => g.First());

        var inspectedInstances = 0;
        var inspectedVehicles = 0;
        var inspectedRigidBodies = 0;
        var bestScore = 0.0;
        (PointerResolution Pointer, CandidateReading Row) bestSeen = default;

        foreach (var instance in FindInstancesByVTable(vtableMap, stats, liveOnly: true))
        {
            inspectedInstances++;
            stats?.SetPointerCounts(inspectedInstances, inspectedVehicles, inspectedRigidBodies);

            if (!TryValidateKnownTruckControlInstance(instance.Address, knownPosition, speedKmh, out var pointer, out var row))
            {
                continue;
            }

            inspectedVehicles++;
            inspectedRigidBodies++;
            stats?.SetPointerCounts(inspectedInstances, inspectedVehicles, inspectedRigidBodies);
            stats?.SetCandidates(inspectedRigidBodies);

            if (pointer.Score > bestScore)
            {
                bestScore = pointer.Score;
                bestSeen = (pointer, row);
            }

            if (IsConfirmedDirectRigidBody(pointer) && pointer.Score >= 70)
            {
                CacheTruckControlStaticReferences(module, instance.Address);
                stats?.Report("Live object", $"Confirmed {TelemetryFormatting.Address(pointer.RigidBody)} from TRUCK_CONTROL {TelemetryFormatting.Address(instance.Address)}.", force: true, important: true);
                return (pointer with { Source = "CONFIRMED LIVE TRUCK_CONTROL instance +0xE8 vehicle +0x5C8", Score = 100 }, row with
                {
                    Source = "CONFIRMED LIVE TRUCK_CONTROL instance +0xE8 vehicle +0x5C8",
                    Score = "100"
                });
            }
        }

        if (bestSeen.Pointer is not null)
        {
            stats?.Report("Live object", $"Best path only scored {bestScore:0}; not using it.", force: true, important: true);
        }
        else
        {
            stats?.Report("Live object", $"Checked {inspectedInstances} TRUCK_CONTROL object(s); none followed the confirmed +0xE8 / +0x5C8 chain.", force: true, important: true);
        }

        return default;
    }

    private void CacheTruckControlStaticReferences(byte[] module, nint truckControl)
    {
        var memory = _memory;
        if (memory is null || truckControl == 0)
        {
            return;
        }

        var rawPointer = unchecked((ulong)(long)truckControl);
        var offsets = FindUInt64Occurrences(module, rawPointer)
            .Where(offset => offset >= 0 && offset + IntPtr.Size <= memory.ModuleSize)
            .Where(offset => memory.TryReadPointer(memory.ModuleBase + offset, out var value) && value == truckControl)
            .ToArray();
        _cachedTruckControlStaticRvas = offsets;
        TelemetryOffsetCacheStore.Save(memory.VersionText, memory.ModuleSize, offsets);
    }

    private bool TryValidateKnownTruckControlInstance(
        nint truckControl,
        Vector3? knownPosition,
        float? speedKmh,
        out PointerResolution pointer,
        out CandidateReading row)
    {
        pointer = PointerResolution.Empty;
        row = default!;
        var memory = _memory;
        if (memory is null ||
            !memory.TryReadPointer(truckControl + SnowRunnerOffsets.ObservedTruckControlActiveVehicle, out var activeVehicle) ||
            !IsLikelyObjectPointer(activeVehicle) ||
            !memory.IsReadable(activeVehicle, 0x600) ||
            !memory.TryReadPointer(activeVehicle + SnowRunnerOffsets.VehicleRigidBody, out var rigidBody) ||
            !IsLikelyObjectPointer(rigidBody) ||
            !memory.IsReadable(rigidBody, 0x260))
        {
            return false;
        }

        var basisScore = TryReadBasis(rigidBody, SnowRunnerOffsets.RigidBodyForward, out var basis) ? basis.Score : 0;
        var quat0Score = TryReadQuaternion(rigidBody, SnowRunnerOffsets.RigidBodyQuaternion0, out var quat0) ? quat0.Score : 0;
        var quat1Score = TryReadQuaternion(rigidBody, SnowRunnerOffsets.RigidBodyQuaternion1, out var quat1) ? quat1.Score : 0;
        var quatScore = Math.Max(quat0Score, quat1Score);
        var velocityScore = TryReadVector3(rigidBody + SnowRunnerOffsets.RigidBodyLinearVelocity, out var velocity)
            ? TelemetryMath.ScoreVelocity(velocity, speedKmh)
            : 0;
        var positionScore = TryReadVector3(rigidBody + SnowRunnerOffsets.RigidBodyPosition, out var rbPos)
            ? TelemetryMath.ScorePosition(rbPos, knownPosition)
            : 0;
        var visualScore = TryReadVector3(activeVehicle + SnowRunnerOffsets.VehicleVisualPosition, out var visualPos)
            ? TelemetryMath.ScorePosition(visualPos, knownPosition)
            : 0;
        var addonBonus = memory.TryReadPointer(activeVehicle + SnowRunnerOffsets.VehicleAddonManager, out var addon) && memory.IsReadable(addon, 0x20) ? 5 : 0;

        var score = TelemetryMath.ClampScore(
            basisScore * 0.50 +
            quatScore * 0.20 +
            velocityScore * 0.12 +
            Math.Max(positionScore, visualScore) * 0.13 +
            addonBonus);

        if (basisScore < 75 || quatScore < 70 || velocityScore <= 0)
        {
            return false;
        }

        pointer = new PointerResolution(
            0,
            truckControl,
            activeVehicle,
            rigidBody,
            "CONFIRMED LIVE TRUCK_CONTROL instance +0xE8 vehicle +0x5C8",
            score);

        row = new CandidateReading(
            "live direct",
            "+0xE8 / +0x5C8",
            TelemetryFormatting.Address(rigidBody),
            $"TRUCK_CONTROL {TelemetryFormatting.Address(truckControl)} vehicle {TelemetryFormatting.Address(activeVehicle)} basis +0x170 {basisScore:0} quat {quatScore:0} vel {velocityScore:0}",
            TelemetryFormatting.Score(score),
            pointer.Source);
        return true;
    }

    private bool IsConfirmedDirectRigidBody(PointerResolution pointer)
    {
        if (pointer.RigidBody == 0)
        {
            return false;
        }

        var basisOk = TryReadBasis(pointer.RigidBody, SnowRunnerOffsets.RigidBodyForward, out var basis) &&
                      basis.Score >= 75;
        var quatOk = (TryReadQuaternion(pointer.RigidBody, SnowRunnerOffsets.RigidBodyQuaternion0, out var quat0) && quat0.Score >= 70) ||
                     (TryReadQuaternion(pointer.RigidBody, SnowRunnerOffsets.RigidBodyQuaternion1, out var quat1) && quat1.Score >= 70);
        var velocityReadable = TryReadVector3(pointer.RigidBody + SnowRunnerOffsets.RigidBodyLinearVelocity, out _);

        return basisOk && quatOk && velocityReadable;
    }

    private TelemetryValue<Vector3>? TryReadSnowFlyerPosition()
    {
        var memory = _memory;
        if (memory is null || _snowFlyerPositionBase == 0)
        {
            return null;
        }

        if (!memory.TryReadPointer(_snowFlyerPositionBase, out var ptr0) ||
            !memory.TryReadPointer(ptr0 + 0x28, out var ptr1) ||
            !memory.TryReadPointer(ptr1 + 0x18, out var ptr2))
        {
            return null;
        }

        var valueAddress = ptr2 + SnowRunnerOffsets.SnowFlyerRigidBodyPosition;
        if (!TryReadVector3(valueAddress, out var position) || !TelemetryMath.IsFinite(position))
        {
            return null;
        }

        _snowFlyerRigidBody = ptr2;
        return new TelemetryValue<Vector3>(position, valueAddress, "SnowFlyer2 player/world coordinate chain");
    }

    private bool TryResolveSnowFlyerRigidBody(TelemetryValue<Vector3>? position, ScanStats? stats = null)
    {
        var memory = _memory;
        var rigidBody = _snowFlyerRigidBody;
        if (memory is null || position is null || rigidBody == 0 ||
            position.Address != rigidBody + SnowRunnerOffsets.SnowFlyerRigidBodyPosition ||
            !memory.IsReadable(rigidBody, 0x250) ||
            !TryReadSnowFlyerBasis(rigidBody, out var basis) || basis.Score < 90 ||
            !TryReadSnowFlyerQuaternion(rigidBody, SnowRunnerOffsets.RigidBodyQuaternion0, out var quaternion) || quaternion.Score < 90 ||
            !TryReadVector3(rigidBody + SnowRunnerOffsets.RigidBodyLinearVelocity, out var velocity))
        {
            return false;
        }

        if (_pointer.RigidBody != rigidBody)
        {
            ResetAccelerationTracker();
        }

        _pointer = new PointerResolution(
            0,
            0,
            0,
            rigidBody,
            "CONFIRMED SnowFlyer position chain -> chassis +0x1A8",
            100);
        _preferredBasisOffset = SnowRunnerOffsets.RigidBodyForward;
        _liveProbes.Clear();
        _lastCandidates = new[]
        {
            new CandidateReading(
                "direct chassis",
                $"position +0x{SnowRunnerOffsets.SnowFlyerRigidBodyPosition:X}",
                TelemetryFormatting.Address(rigidBody),
                $"basis +0x170 {basis.Score:0}, quaternion +0x1D0 {quaternion.Score:0}, velocity +0x230 {TelemetryFormatting.Vector(velocity)}",
                "100",
                "stable SnowFlyer position-chain object")
        };
        _lastDiagnostics = $"Stable SnowFlyer chassis locked: {TelemetryFormatting.Address(rigidBody)}; position +0x{SnowRunnerOffsets.SnowFlyerRigidBodyPosition:X}, basis +0x170, quaternion +0x1D0/+0x1E0, velocity +0x230.";
        stats?.SetCandidates(1);
        stats?.SetPointerCounts(1, 0, 1);
        stats?.Report("Stable chassis", _lastDiagnostics, force: true, important: true);
        return true;
    }

    private bool TryResolveRelativeTarget(nint instructionStart, int displacementOffset, out nint target)
    {
        target = 0;
        var memory = _memory;
        if (memory is null || !memory.TryRead<int>(instructionStart + displacementOffset, out var rel32))
        {
            return false;
        }

        target = instructionStart + displacementOffset + sizeof(int) + rel32;
        return target != 0;
    }

    private void ResolveActiveVehicle(bool scanNearby, ScanStats? stats = null)
    {
        var memory = _memory;
        if (memory is null)
        {
            return;
        }

        var knownPosition = TryReadSnowFlyerPosition()?.Value;
        var candidates = new List<(PointerResolution Pointer, CandidateReading Row)>();
        stats?.Report("Old anchors", "Testing exact Noclip static offsets.", force: true);

        TryAddPointerCandidate(
            candidates,
            memory.ModuleBase + SnowRunnerOffsets.OldTruckControlStatic,
            SnowRunnerOffsets.TruckControlActiveVehicle,
            "Noclip old TRUCK_CONTROL exact");

        TryAddPointerCandidate(
            candidates,
            memory.ModuleBase + SnowRunnerOffsets.OldDriveLogicStatic,
            SnowRunnerOffsets.DriveLogicActiveVehicle,
            "Noclip old DRIVE_LOGIC exact");

        if (scanNearby)
        {
            ScanPointerRegion(candidates, SnowRunnerOffsets.OldTruckControlStatic, SnowRunnerOffsets.TruckControlActiveVehicle, knownPosition, "TRUCK_CONTROL nearby static", stats);
            ScanPointerRegion(candidates, SnowRunnerOffsets.OldDriveLogicStatic, SnowRunnerOffsets.DriveLogicActiveVehicle, knownPosition, "DRIVE_LOGIC nearby static", stats);
        }

        var best = candidates
            .OrderByDescending(c => c.Pointer.Score)
            .ThenBy(c => Math.Abs((long)c.Pointer.StaticAddress - ((long)memory.ModuleBase + SnowRunnerOffsets.OldTruckControlStatic)))
            .FirstOrDefault();

        const double minimumTrustedAnchorScore = 68;
        if (best.Pointer is not null && best.Pointer.RigidBody != 0 && best.Pointer.Score >= minimumTrustedAnchorScore)
        {
            _pointer = best.Pointer;
        }
        else
        {
            _pointer = PointerResolution.Empty;
        }

        _lastCandidates = candidates
            .OrderByDescending(c => c.Pointer.Score)
            .Take(30)
            .Select(c => c.Row)
            .ToArray();

        var bestAnchorScore = best.Pointer is null ? 0 : best.Pointer.Score;
        _lastDiagnostics = $"Anchor scan: {candidates.Count} candidate(s) from old Noclip static offsets, best score {bestAnchorScore:0}, trusted threshold {minimumTrustedAnchorScore:0}.";
        stats?.SetCandidates(candidates.Count);
        stats?.Report("Old anchors", $"{candidates.Count} candidate(s) from old Noclip static offsets, best score {bestAnchorScore:0}.", force: true, important: true);

        void TryAddPointerCandidate(List<(PointerResolution Pointer, CandidateReading Row)> target, nint staticAddress, int activeVehicleOffset, string source)
        {
            var pointer = ValidatePointerCandidate(staticAddress, activeVehicleOffset, knownPosition, source);
            if (pointer.RigidBody == 0)
            {
                return;
            }

            target.Add((pointer, new CandidateReading(
                "anchor",
                $"+0x{(long)staticAddress - (long)memory.ModuleBase:X}",
                TelemetryFormatting.Address(staticAddress),
                $"vehicle {TelemetryFormatting.Address(pointer.ActiveVehicle)} rb {TelemetryFormatting.Address(pointer.RigidBody)}",
                TelemetryFormatting.Score(pointer.Score),
                source)));
        }
    }

    private void ScanPointerRegion(
        List<(PointerResolution Pointer, CandidateReading Row)> candidates,
        int centerOffset,
        int activeVehicleOffset,
        Vector3? knownPosition,
        string source,
        ScanStats? stats = null)
    {
        var memory = _memory;
        if (memory is null)
        {
            return;
        }

        const int radius = 0x400000;
        var startOffset = Math.Max(0, centerOffset - radius);
        var endOffset = Math.Min(memory.ModuleSize - 8, centerOffset + radius);
        var length = Math.Max(0, endOffset - startOffset);
        if (length <= 0)
        {
            return;
        }

        stats?.Report("Old anchor nearby scan", $"{source}: reading 0x{length:X} bytes around module offset 0x{centerOffset:X}.", force: true);
        var bytes = memory.ReadRangeLossy(memory.ModuleBase + startOffset, length);
        stats?.AddChunk(length, length / 8);
        var seenSingletons = new HashSet<nint>();

        for (var i = 0; i + 8 <= bytes.Length; i += 8)
        {
            var raw = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(i, 8));
            if (!LooksLikeUserPointer(raw))
            {
                continue;
            }
            stats?.AddHit();

            var singleton = unchecked((nint)(long)raw);
            if (!seenSingletons.Add(singleton))
            {
                continue;
            }

            var staticAddress = memory.ModuleBase + startOffset + i;
            var pointer = ValidatePointerCandidate(staticAddress, activeVehicleOffset, knownPosition, source);
            if (pointer.RigidBody == 0 || pointer.Score < 35)
            {
                continue;
            }

            candidates.Add((pointer, new CandidateReading(
                "anchor",
                $"+0x{startOffset + i:X}",
                TelemetryFormatting.Address(staticAddress),
                $"vehicle {TelemetryFormatting.Address(pointer.ActiveVehicle)} rb {TelemetryFormatting.Address(pointer.RigidBody)}",
                TelemetryFormatting.Score(pointer.Score),
                source)));
            stats?.SetCandidates(candidates.Count);
        }
    }

    private PointerResolution ValidatePointerCandidate(nint staticAddress, int activeVehicleOffset, Vector3? knownPosition, string source)
    {
        var memory = _memory;
        if (memory is null ||
            !memory.TryReadPointer(staticAddress, out var singleton) ||
            !memory.TryReadPointer(singleton + activeVehicleOffset, out var activeVehicle) ||
            !IsLikelyObjectPointer(activeVehicle) ||
            !memory.TryReadPointer(activeVehicle + SnowRunnerOffsets.VehicleRigidBody, out var rigidBody) ||
            !IsLikelyObjectPointer(rigidBody))
        {
            return PointerResolution.Empty;
        }

        var basisScore = TryReadBasis(rigidBody, SnowRunnerOffsets.RigidBodyForward, out var basis) ? basis.Score : 0;
        var quatScore = TryReadQuaternion(rigidBody, SnowRunnerOffsets.RigidBodyQuaternion0, out var quaternion) ? quaternion.Score : 0;
        var positionScore = TryReadVector3(rigidBody + SnowRunnerOffsets.RigidBodyPosition, out var rbPos)
            ? TelemetryMath.ScorePosition(rbPos, knownPosition)
            : 0;
        var visualScore = TryReadVector3(activeVehicle + SnowRunnerOffsets.VehicleVisualPosition, out var visualPos)
            ? TelemetryMath.ScorePosition(visualPos, knownPosition)
            : 0;

        var addonBonus = memory.TryReadPointer(activeVehicle + SnowRunnerOffsets.VehicleAddonManager, out var addon) && memory.IsReadable(addon, 0x20) ? 5 : 0;
        var score = TelemetryMath.ClampScore(basisScore * 0.45 + quatScore * 0.20 + Math.Max(positionScore, visualScore) * 0.30 + addonBonus);

        return new PointerResolution(staticAddress, singleton, activeVehicle, rigidBody, source, score);
    }

    private void ResolveActiveVehicleByRtti(Vector3? knownPosition, float? speedKmh, ScanStats? stats = null)
    {
        var memory = _memory;
        if (memory is null)
        {
            return;
        }

        stats?.Report("RTTI", $"Reading SnowRunner module: 0x{memory.ModuleSize:X} bytes.", force: true, important: true);
        var module = memory.ReadRangeLossy(memory.ModuleBase, memory.ModuleSize);
        stats?.AddChunk(memory.ModuleSize, memory.ModuleSize / 4);
        var typeDescriptors = FindRttiTypeDescriptors(module, "TRUCK_CONTROL", "DRIVE_LOGIC").ToArray();
        var vtables = FindRttiVtables(module, typeDescriptors).ToArray();
        stats?.SetRttiCounts(typeDescriptors.Length, vtables.Length);
        stats?.Report("RTTI", $"{typeDescriptors.Length} type descriptor(s), {vtables.Length} vtable(s).", force: true, important: true);
        var vtableMap = vtables
            .GroupBy(v => unchecked((ulong)(long)v.VTable))
            .ToDictionary(g => g.Key, g => g.First());
        var candidates = new List<(PointerResolution Pointer, CandidateReading Row)>();
        var nearMisses = new List<NearRigidBodyCandidate>();
        var trackingCandidates = new List<NearRigidBodyCandidate>();
        var seenRigidBodies = new HashSet<nint>();
        var inspectedInstances = 0;
        var inspectedVehicles = 0;
        var inspectedRigidBodies = 0;

        if (vtableMap.Count == 0)
        {
            _lastDiagnostics += " RTTI scan: no TRUCK_CONTROL/DRIVE_LOGIC vtables found.";
            stats?.Report("RTTI", "No TRUCK_CONTROL/DRIVE_LOGIC vtables found.", force: true, important: true);
            return;
        }

        foreach (var instance in FindInstancesByVTable(vtableMap, stats))
        {
            inspectedInstances++;
            stats?.SetPointerCounts(inspectedInstances, inspectedVehicles, inspectedRigidBodies);
            foreach (var activeOffset in CandidateActiveVehicleOffsets())
            {
                if (!memory.TryReadPointer(instance.Address + activeOffset, out var activeVehicle) ||
                    !IsLikelyObjectPointer(activeVehicle) ||
                    !memory.IsReadable(activeVehicle, 0x100))
                {
                    continue;
                }

                inspectedVehicles++;
                stats?.SetPointerCounts(inspectedInstances, inspectedVehicles, inspectedRigidBodies);
                foreach (var rbOffset in CandidateRigidBodyPointerOffsets())
                {
                    if (!memory.TryReadPointer(activeVehicle + rbOffset, out var rigidBody) ||
                        !IsLikelyObjectPointer(rigidBody) ||
                        !memory.IsReadable(rigidBody, 0x260))
                    {
                        continue;
                    }

                    if (!seenRigidBodies.Add(rigidBody))
                    {
                        continue;
                    }

                    inspectedRigidBodies++;
                    stats?.SetPointerCounts(inspectedInstances, inspectedVehicles, inspectedRigidBodies);
                    var evaluation = EvaluateRigidBody(rigidBody, knownPosition, speedKmh);
                    if (!evaluation.IsInteresting)
                    {
                        continue;
                    }

                    var activeBonus = activeOffset == SnowRunnerOffsets.TruckControlActiveVehicle ? 8 : 0;
                    var rbBonus = rbOffset == SnowRunnerOffsets.VehicleRigidBody ? 8 : 0;
                    var score = TelemetryMath.ClampScore(evaluation.Score + activeBonus + rbBonus);

                    if (!evaluation.IsValid)
                    {
                        var nearPointer = new PointerResolution(
                            0,
                            instance.Address,
                            activeVehicle,
                            rigidBody,
                            $"RTTI {instance.ClassName} instance {TelemetryFormatting.Offset(activeOffset)} vehicle {TelemetryFormatting.Offset(rbOffset)}",
                            score);

                        var nearCandidate = new NearRigidBodyCandidate(score, nearPointer, new CandidateReading(
                            "near rb",
                            $"{TelemetryFormatting.Offset(activeOffset)} / {TelemetryFormatting.Offset(rbOffset)}",
                            TelemetryFormatting.Address(rigidBody),
                            $"{instance.ClassName} {TelemetryFormatting.Address(instance.Address)} {evaluation.Summary}",
                            TelemetryFormatting.Score(score),
                            "Rejected RTTI rigid-body candidate"),
                            evaluation,
                            activeOffset,
                            rbOffset);
                        nearMisses.Add(nearCandidate);
                        trackingCandidates.Add(nearCandidate);
                        continue;
                    }

                    var pointer = new PointerResolution(
                        0,
                        instance.Address,
                        activeVehicle,
                        rigidBody,
                        $"RTTI {instance.ClassName} instance {TelemetryFormatting.Offset(activeOffset)} vehicle {TelemetryFormatting.Offset(rbOffset)}",
                        score);

                    var candidateRow = new CandidateReading(
                        "rtti vehicle",
                        $"{TelemetryFormatting.Offset(activeOffset)} / {TelemetryFormatting.Offset(rbOffset)}",
                        TelemetryFormatting.Address(rigidBody),
                        $"{instance.ClassName} {TelemetryFormatting.Address(instance.Address)} {evaluation.Summary}",
                        TelemetryFormatting.Score(score),
                        "MSVC RTTI vtable instance scan");
                    candidates.Add((pointer, candidateRow));
                    trackingCandidates.Add(new NearRigidBodyCandidate(
                        score,
                        pointer,
                        candidateRow,
                        evaluation,
                        activeOffset,
                        rbOffset));
                    stats?.SetCandidates(candidates.Count);
                    stats?.Report("RTTI", $"Validated candidate {candidates.Count}: {TelemetryFormatting.Address(rigidBody)} score {score:0}.", force: true, important: true);

                    if (candidates.Count >= 80)
                    {
                        break;
                    }
                }

                if (candidates.Count >= 80)
                {
                    break;
                }
            }

            if (candidates.Count >= 80 || inspectedInstances >= 600)
            {
                break;
            }
        }

        var confirmedChassis = trackingCandidates
            .Where(IsConfirmedChassisShape)
            .OrderByDescending(n => n.Score)
            .FirstOrDefault();
        if (confirmedChassis is not null && confirmedChassis.Pointer.RigidBody != 0)
        {
            _pointer = confirmedChassis.Pointer with
            {
                Source = "CONFIRMED " + confirmedChassis.Pointer.Source,
                Score = 100
            };
            _preferredBasisOffset = SnowRunnerOffsets.RigidBodyForward;
            ResetAccelerationTracker();
            CacheTruckControlStaticReferences(module, _pointer.Singleton);
            _lastDiagnostics += $" Confirmed chassis path selected: {TelemetryFormatting.Address(_pointer.RigidBody)} active +0x{confirmedChassis.ActiveVehicleOffset:X} vehicle +0x{confirmedChassis.RigidBodyOffset:X}.";
            stats?.Report("RTTI confirmed chassis", $"Selected {TelemetryFormatting.Address(_pointer.RigidBody)} via active +0x{confirmedChassis.ActiveVehicleOffset:X} vehicle +0x{confirmedChassis.RigidBodyOffset:X}.", force: true, important: true);
        }
        else
        {
            ArmLiveProbes(trackingCandidates);
            var bestTracked = trackingCandidates
                .Where(candidate => candidate.Evaluation.BasisOffset == SnowRunnerOffsets.RigidBodyForward &&
                                    candidate.Evaluation.QuaternionOffset is SnowRunnerOffsets.RigidBodyQuaternion0 or SnowRunnerOffsets.RigidBodyQuaternion1)
                .OrderByDescending(candidate => candidate.Score)
                .FirstOrDefault();
            if (bestTracked is not null && _liveProbes.Count > 0)
            {
                _lastDiagnostics += $" Exact-layout live tracking armed: {_liveProbes.Count} candidate(s), best {TelemetryFormatting.Address(bestTracked.Pointer.RigidBody)} score {bestTracked.Score:0}.";
                stats?.Report("RTTI live tracking", $"Watching {_liveProbes.Count} exact-layout candidate(s) while the truck moves.", force: true, important: true);
            }
            else
            {
                _lastDiagnostics += " No exact +0x170 / +0x1D0-or-1E0 / +0x230 telemetry candidates were found.";
                stats?.Report("RTTI", "No exact-layout chassis candidates found; telemetry remains unlocked.", force: true, important: true);
            }
        }

        _lastCandidates = _lastCandidates
            .Concat(candidates.OrderByDescending(c => c.Pointer.Score).Select(c => c.Row))
            .Concat(nearMisses.OrderByDescending(n => n.Score).Take(30).Select(n => n.Row))
            .Take(120)
            .ToArray();

        _lastDiagnostics += $" RTTI scan: {typeDescriptors.Length} type descriptor(s), {vtables.Length} vtable(s), {inspectedInstances} instance ref(s), {inspectedVehicles} vehicle ptr(s), {inspectedRigidBodies} unique rigid-body ptr(s), {candidates.Count} validated candidate(s), {nearMisses.Count} near miss(es).";
        stats?.Report("RTTI", $"{inspectedInstances} instance ref(s), {inspectedVehicles} vehicle ptr(s), {inspectedRigidBodies} unique rigid-body ptr(s), {candidates.Count} candidate(s), {nearMisses.Count} near miss(es).", force: true, important: true);
    }

    private IEnumerable<RttiTypeDescriptor> FindRttiTypeDescriptors(byte[] module, params string[] classNames)
    {
        var seen = new HashSet<int>();
        foreach (var className in classNames)
        {
            foreach (var hit in FindAsciiOccurrences(module, className))
            {
                var nameStart = FindRttiNameStart(module, hit);
                var typeDescriptorOffset = nameStart - 0x10;
                if (typeDescriptorOffset < 0 || typeDescriptorOffset + 0x18 >= module.Length || !seen.Add(typeDescriptorOffset))
                {
                    continue;
                }

                var decoratedName = ReadAsciiZ(module, nameStart, 180);
                if (!decoratedName.Contains(className, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return new RttiTypeDescriptor(className, typeDescriptorOffset, _memory!.ModuleBase + typeDescriptorOffset, decoratedName);
            }
        }
    }

    private IEnumerable<RttiVTable> FindRttiVtables(byte[] module, IReadOnlyCollection<RttiTypeDescriptor> typeDescriptors)
    {
        var memory = _memory;
        if (memory is null || typeDescriptors.Count == 0)
        {
            yield break;
        }

        var seen = new HashSet<nint>();
        foreach (var typeDescriptor in typeDescriptors)
        {
            foreach (var tdRef in FindUInt32Occurrences(module, unchecked((uint)typeDescriptor.Rva)))
            {
                var colOffset = tdRef - 0x0C;
                if (colOffset < 0 || colOffset + 0x18 > module.Length)
                {
                    continue;
                }

                var signature = BinaryPrimitives.ReadInt32LittleEndian(module.AsSpan(colOffset, 4));
                var objectOffset = BinaryPrimitives.ReadInt32LittleEndian(module.AsSpan(colOffset + 4, 4));
                var classDescriptorRva = BinaryPrimitives.ReadInt32LittleEndian(module.AsSpan(colOffset + 0x10, 4));
                var selfRva = BinaryPrimitives.ReadInt32LittleEndian(module.AsSpan(colOffset + 0x14, 4));

                if (signature is not 0 and not 1 ||
                    objectOffset < 0 ||
                    objectOffset > 0x4000 ||
                    classDescriptorRva <= 0 ||
                    classDescriptorRva >= module.Length)
                {
                    continue;
                }

                if (signature == 1 && selfRva != colOffset)
                {
                    continue;
                }

                var colAddress = memory.ModuleBase + colOffset;
                var colRaw = unchecked((ulong)(long)colAddress);
                foreach (var colRef in FindUInt64Occurrences(module, colRaw))
                {
                    var vtableAddress = memory.ModuleBase + colRef + IntPtr.Size;
                    if (!seen.Add(vtableAddress))
                    {
                        continue;
                    }

                    yield return new RttiVTable(typeDescriptor.ClassName, vtableAddress, colAddress, typeDescriptor.DecoratedName);
                }
            }
        }
    }

    private IEnumerable<RttiInstance> FindInstancesByVTable(IReadOnlyDictionary<ulong, RttiVTable> vtableMap, ScanStats? stats = null, bool liveOnly = false)
    {
        var memory = _memory;
        if (memory is null || vtableMap.Count == 0)
        {
            yield break;
        }

        const int chunkSize = 4 * 1024 * 1024;
        const int maxInstances = 1200;
        var seen = new HashSet<nint>();
        var found = 0;

        var regions = memory.EnumerateReadableRegions()
            .Where(ShouldScanRegion)
            .OrderByDescending(region => unchecked((ulong)(long)region.BaseAddress))
            .ToArray();

        foreach (var region in regions)
        {
            stats?.AddRegion();
            stats?.Report(liveOnly ? "Live object" : "RTTI heap scan", $"Region {TelemetryFormatting.Address(region.BaseAddress)} size {FormatBytes(region.Size)}.", important: false);
            for (var regionOffset = 0L; regionOffset < region.Size; regionOffset += chunkSize)
            {
                var bytesToRead = (int)Math.Min(chunkSize, region.Size - regionOffset);
                if (bytesToRead < 8)
                {
                    continue;
                }

                var block = new byte[bytesToRead];
                var blockAddress = region.BaseAddress + (nint)regionOffset;
                if (!memory.TryReadBytes(blockAddress, block))
                {
                    continue;
                }
                stats?.AddChunk(bytesToRead, bytesToRead / 8);

                for (var i = 0; i + 8 <= block.Length; i += 8)
                {
                    var raw = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(i, 8));
                    if (!vtableMap.TryGetValue(raw, out var vtable))
                    {
                        continue;
                    }
                    stats?.AddHit();

                    var instanceAddress = blockAddress + i;
                    if (!seen.Add(instanceAddress))
                    {
                        continue;
                    }

                    found++;
                    yield return new RttiInstance(vtable.ClassName, instanceAddress, vtable.VTable);
                    if (found >= maxInstances)
                    {
                        yield break;
                    }
                }
            }
        }
    }

    private bool TryScoreRigidBody(nint rigidBody, Vector3? knownPosition, float? speedKmh, out double score, out string summary)
    {
        var evaluation = EvaluateRigidBody(rigidBody, knownPosition, speedKmh);
        score = evaluation.Score;
        summary = evaluation.Summary;
        return evaluation.IsValid;
    }

    private RigidBodyEvaluation EvaluateRigidBody(nint rigidBody, Vector3? knownPosition, float? speedKmh)
    {
        var bestBasis = ScanBasisCandidates(rigidBody)
            .OrderByDescending(c => c.Score + (c.Offset == SnowRunnerOffsets.RigidBodyForward ? 5 : 0))
            .FirstOrDefault();

        var bestQuaternion = ScanQuaternionCandidates(rigidBody)
            .OrderByDescending(c => c.Score + (c.Offset is SnowRunnerOffsets.RigidBodyQuaternion0 or SnowRunnerOffsets.RigidBodyQuaternion1 ? 5 : 0))
            .FirstOrDefault();

        var positionScore = TryFindBestNearbyPosition(rigidBody, knownPosition, out var positionOffset, out var positionDistance);
        var velocityScore = TryReadVector3(rigidBody + SnowRunnerOffsets.RigidBodyLinearVelocity, out var velocity)
            ? TelemetryMath.ScoreVelocity(velocity, speedKmh)
            : 0;

        var basisScore = bestBasis?.Score ?? 0;
        var quatScore = bestQuaternion?.Score ?? 0;
        var alignmentScore = bestBasis is not null && bestQuaternion is not null
            ? ScoreQuaternionBasisAlignment(bestQuaternion, bestBasis)
            : 0;

        var score = TelemetryMath.ClampScore(
            basisScore * 0.48 +
            quatScore * 0.12 +
            alignmentScore * 0.08 +
            positionScore * 0.24 +
            velocityScore * 0.08);

        var basisText = bestBasis is null
            ? "basis n/a"
            : $"basis {TelemetryFormatting.Offset(bestBasis.Offset)} {bestBasis.Layout} {basisScore:0} p/r/y {bestBasis.PitchDeg:0.0}/{bestBasis.RollDeg:0.0}/{bestBasis.YawDeg:0.0}";
        var quatText = bestQuaternion is null
            ? "quat n/a"
            : $"quat {TelemetryFormatting.Offset(bestQuaternion.Offset)} {quatScore:0} p/r/y {bestQuaternion.PitchDeg:0.0}/{bestQuaternion.RollDeg:0.0}/{bestQuaternion.YawDeg:0.0}";
        var positionText = double.IsPositiveInfinity(positionDistance)
            ? $"pos {TelemetryFormatting.Offset(positionOffset)} none"
            : $"pos {TelemetryFormatting.Offset(positionOffset)} {positionScore:0} d {positionDistance:0.0}";
        var summary = $"{basisText}, {quatText}, align {alignmentScore:0}, {positionText}, vel {velocityScore:0}";
        var nearKnownPosition = !knownPosition.HasValue ||
                                (!double.IsPositiveInfinity(positionDistance) && positionDistance <= 80);
        var isValid = nearKnownPosition &&
                      basisScore >= 75 &&
                      score >= 62 &&
                      (positionScore >= 35 || alignmentScore >= 86);
        var isInteresting = score >= 18 || basisScore >= 45 || quatScore >= 70 || positionScore >= 35;
        return new RigidBodyEvaluation(
            score,
            basisScore,
            quatScore,
            positionScore,
            velocityScore,
            bestBasis?.Offset ?? 0,
            bestQuaternion?.Offset ?? 0,
            bestBasis?.PitchDeg ?? 0,
            bestBasis?.RollDeg ?? 0,
            bestBasis?.YawDeg ?? 0,
            positionOffset,
            positionDistance,
            summary,
            isValid,
            isInteresting);
    }

    private double TryFindBestNearbyPosition(nint rigidBody, Vector3? knownPosition, out int bestOffset, out double bestDistance)
    {
        bestOffset = 0;
        bestDistance = double.PositiveInfinity;
        var bestScore = knownPosition.HasValue ? 0.0 : 40.0;

        for (var offset = 0x40; offset <= 0x900; offset += 0x10)
        {
            if (!TryReadVector3(rigidBody + offset, out var value) ||
                !TelemetryMath.IsFinite(value) ||
                Math.Abs(value.X) > 1_000_000 ||
                Math.Abs(value.Y) > 1_000_000 ||
                Math.Abs(value.Z) > 1_000_000)
            {
                continue;
            }

            if (!knownPosition.HasValue)
            {
                bestOffset = offset;
                bestDistance = 0;
                bestScore = Math.Max(bestScore, 40);
                continue;
            }

            var distance = Vector3.Distance(value, knownPosition.Value);
            var score = TelemetryMath.ClampScore(100 - distance * 1.2);
            if (score > bestScore)
            {
                bestScore = score;
                bestOffset = offset;
                bestDistance = distance;
            }
        }

        return bestScore;
    }

    private static IEnumerable<int> CandidateActiveVehicleOffsets()
    {
        yield return SnowRunnerOffsets.TruckControlActiveVehicle;
        yield return SnowRunnerOffsets.ObservedTruckControlActiveVehicle;
        for (var offset = 0; offset <= 0x400; offset += 8)
        {
            if (offset != SnowRunnerOffsets.TruckControlActiveVehicle &&
                offset != SnowRunnerOffsets.ObservedTruckControlActiveVehicle)
            {
                yield return offset;
            }
        }
    }

    private static IEnumerable<int> CandidateRigidBodyPointerOffsets()
    {
        yield return SnowRunnerOffsets.VehicleRigidBody;
        for (var offset = 0x0; offset <= 0x1200; offset += 8)
        {
            if (offset != SnowRunnerOffsets.VehicleRigidBody)
            {
                yield return offset;
            }
        }
    }

    private static bool IsConfirmedChassisShape(NearRigidBodyCandidate candidate)
    {
        return candidate.ActiveVehicleOffset == SnowRunnerOffsets.ObservedTruckControlActiveVehicle &&
               candidate.RigidBodyOffset == SnowRunnerOffsets.VehicleRigidBody &&
               candidate.Evaluation.BasisOffset == SnowRunnerOffsets.RigidBodyForward &&
               candidate.Evaluation.BasisScore >= 80;
    }

    private static IEnumerable<int> FindAsciiOccurrences(byte[] haystack, string needle)
    {
        var bytes = Encoding.ASCII.GetBytes(needle);
        for (var i = 0; i + bytes.Length <= haystack.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < bytes.Length; j++)
            {
                var a = haystack[i + j];
                var b = bytes[j];
                if (a != b && char.ToUpperInvariant((char)a) != char.ToUpperInvariant((char)b))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                yield return i;
            }
        }
    }

    private static IEnumerable<int> FindUInt32Occurrences(byte[] haystack, uint value)
    {
        var needle = BitConverter.GetBytes(value);
        for (var i = 0; i + 4 <= haystack.Length; i += 4)
        {
            if (haystack[i] == needle[0] &&
                haystack[i + 1] == needle[1] &&
                haystack[i + 2] == needle[2] &&
                haystack[i + 3] == needle[3])
            {
                yield return i;
            }
        }
    }

    private static IEnumerable<int> FindUInt64Occurrences(byte[] haystack, ulong value)
    {
        var needle = BitConverter.GetBytes(value);
        for (var i = 0; i + 8 <= haystack.Length; i += 8)
        {
            if (haystack[i] == needle[0] &&
                haystack[i + 1] == needle[1] &&
                haystack[i + 2] == needle[2] &&
                haystack[i + 3] == needle[3] &&
                haystack[i + 4] == needle[4] &&
                haystack[i + 5] == needle[5] &&
                haystack[i + 6] == needle[6] &&
                haystack[i + 7] == needle[7])
            {
                yield return i;
            }
        }
    }

    private static int FindRttiNameStart(byte[] module, int substringHit)
    {
        for (var i = substringHit; i >= Math.Max(0, substringHit - 20); i--)
        {
            if (module[i] == (byte)'.')
            {
                return i;
            }
        }

        return substringHit;
    }

    private static string ReadAsciiZ(byte[] bytes, int offset, int maxLength)
    {
        var end = offset;
        var maxEnd = Math.Min(bytes.Length, offset + maxLength);
        while (end < maxEnd && bytes[end] != 0)
        {
            end++;
        }

        return Encoding.ASCII.GetString(bytes, offset, end - offset);
    }

    private sealed record RttiTypeDescriptor(string ClassName, int Rva, nint Address, string DecoratedName);

    private sealed record RttiVTable(string ClassName, nint VTable, nint CompleteObjectLocator, string DecoratedName);

    private sealed record RttiInstance(string ClassName, nint Address, nint VTable);

    private sealed record RigidBodyEvaluation(
        double Score,
        double BasisScore,
        double QuaternionScore,
        double PositionScore,
        double VelocityScore,
        int BasisOffset,
        int QuaternionOffset,
        double PitchDeg,
        double RollDeg,
        double YawDeg,
        int PositionOffset,
        double PositionDistance,
        string Summary,
        bool IsValid,
        bool IsInteresting);

    private sealed record NearRigidBodyCandidate(
        double Score,
        PointerResolution Pointer,
        CandidateReading Row,
        RigidBodyEvaluation Evaluation,
        int ActiveVehicleOffset,
        int RigidBodyOffset);

    private sealed class LiveRigidBodyProbe
    {
        public LiveRigidBodyProbe(NearRigidBodyCandidate candidate, int displayRank)
        {
            Pointer = candidate.Pointer with
            {
                Source = "LIVE PROBE " + candidate.Pointer.Source
            };
            DisplayRank = displayRank;
            SeedScore = candidate.Score;
            ActiveVehicleOffset = candidate.ActiveVehicleOffset;
            RigidBodyOffset = candidate.RigidBodyOffset;
            BasisOffset = candidate.Evaluation.BasisOffset;
            QuaternionOffset = candidate.Evaluation.QuaternionOffset;
            StartPitchDeg = candidate.Evaluation.PitchDeg;
            StartRollDeg = candidate.Evaluation.RollDeg;
            StartYawDeg = candidate.Evaluation.YawDeg;
            CurrentPitchDeg = StartPitchDeg;
            CurrentRollDeg = StartRollDeg;
            CurrentYawDeg = StartYawDeg;
            SeedSummary = candidate.Evaluation.Summary;
            ActiveOffsetPair = candidate.Row.Offset;
        }

        public PointerResolution Pointer { get; }
        public int DisplayRank { get; }
        public double SeedScore { get; }
        public int ActiveVehicleOffset { get; }
        public int RigidBodyOffset { get; }
        public int BasisOffset { get; }
        public int QuaternionOffset { get; }
        public double StartPitchDeg { get; }
        public double StartRollDeg { get; }
        public double StartYawDeg { get; }
        public double CurrentPitchDeg { get; set; }
        public double CurrentRollDeg { get; set; }
        public double CurrentYawDeg { get; set; }
        public double MaxPitchRollDeltaDeg { get; set; }
        public double LastPitchRollDeltaDeg { get; set; }
        public double MaxYawDeltaDeg { get; set; }
        public double LastLinearSpeedKmh { get; set; }
        public double MaxLinearSpeedKmh { get; set; }
        public int VelocitySamples { get; set; }
        public int SpeedComparisonSamples { get; set; }
        public double SpeedErrorTotalKmh { get; set; }
        public int Samples { get; set; }
        public string SeedSummary { get; }
        public string ActiveOffsetPair { get; }

        public double MaxAttitudeDeltaDeg => Math.Max(MaxPitchRollDeltaDeg, MaxYawDeltaDeg);

        public bool HasMeaningfulMovement => MaxAttitudeDeltaDeg >= 0.25;

        public double MovementScore => TelemetryMath.ClampScore(MaxAttitudeDeltaDeg * 12.0 + Math.Min(12, Samples));

        public double AverageSpeedErrorKmh => SpeedComparisonSamples == 0
            ? 0
            : SpeedErrorTotalKmh / SpeedComparisonSamples;

        public double ConfirmedShapeScore
        {
            get
            {
                var score = 0.0;
                if (BasisOffset == SnowRunnerOffsets.RigidBodyForward)
                {
                    score += 100;
                }

                if (RigidBodyOffset == SnowRunnerOffsets.VehicleRigidBody)
                {
                    score += 70;
                }

                if (QuaternionOffset is SnowRunnerOffsets.RigidBodyQuaternion0 or SnowRunnerOffsets.RigidBodyQuaternion1)
                {
                    score += 20;
                }

                if (ActiveVehicleOffset == SnowRunnerOffsets.ObservedTruckControlActiveVehicle)
                {
                    score += 15;
                }

                return score;
            }
        }
    }

    private sealed class ScanStats
    {
        private readonly IProgress<ScanProgress>? _progress;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private TimeSpan _lastReport = TimeSpan.Zero;

        public ScanStats(IProgress<ScanProgress>? progress)
        {
            _progress = progress;
        }

        public long Regions { get; private set; }
        public long Chunks { get; private set; }
        public long BytesRead { get; private set; }
        public long AddressesScanned { get; private set; }
        public long Hits { get; private set; }
        public long Candidates { get; private set; }
        public long TypeDescriptors { get; private set; }
        public long VTables { get; private set; }
        public long Instances { get; private set; }
        public long VehiclePointers { get; private set; }
        public long RigidBodyPointers { get; private set; }

        public void AddRegion()
        {
            Regions++;
        }

        public void AddChunk(long bytesRead, long addressesScanned)
        {
            Chunks++;
            BytesRead += Math.Max(0, bytesRead);
            AddressesScanned += Math.Max(0, addressesScanned);
            Report("Reading memory", "", important: false);
        }

        public void AddHit()
        {
            Hits++;
        }

        public void SetCandidates(long candidates)
        {
            Candidates = Math.Max(Candidates, candidates);
        }

        public void SetRttiCounts(long typeDescriptors, long vtables)
        {
            TypeDescriptors = typeDescriptors;
            VTables = vtables;
        }

        public void SetPointerCounts(long instances, long vehiclePointers, long rigidBodyPointers)
        {
            Instances = instances;
            VehiclePointers = vehiclePointers;
            RigidBodyPointers = rigidBodyPointers;
        }

        public void Report(string phase, string detail, bool force = false, bool important = false)
        {
            if (_progress is null)
            {
                return;
            }

            var elapsed = _stopwatch.Elapsed;
            if (!force && elapsed - _lastReport < TimeSpan.FromMilliseconds(350))
            {
                return;
            }

            _lastReport = elapsed;
            _progress.Report(new ScanProgress(
                phase,
                detail,
                Regions,
                Chunks,
                BytesRead,
                AddressesScanned,
                Hits,
                Candidates,
                TypeDescriptors,
                VTables,
                Instances,
                VehiclePointers,
                RigidBodyPointers,
                important));
        }
    }

    private void ResolveRigidBodyByPositionScan(Vector3 knownPosition, float? speedKmh, ScanStats? stats = null)
    {
        var memory = _memory;
        if (memory is null)
        {
            return;
        }

        var candidates = new List<(PointerResolution Pointer, CandidateReading Row)>();
        var seenRigidBodies = new HashSet<nint>();
        const float positionTolerance = 12.0f;
        const int chunkSize = 4 * 1024 * 1024;
        const int overlap = 16;
        var positionOffsets = new[]
        {
            SnowRunnerOffsets.RigidBodyPosition,
            SnowRunnerOffsets.RigidBodySweptPosition0,
            SnowRunnerOffsets.RigidBodySweptPosition1,
            0x150, 0x160, 0x170, 0x180, 0x190, 0x1F0, 0x200, 0x210, 0x220, 0x230, 0x240, 0x250, 0x260
        };

        stats?.Report("Direct position scan", $"Searching for position fingerprint {TelemetryFormatting.Vector(knownPosition)}.", force: true, important: true);
        foreach (var region in memory.EnumerateReadableRegions())
        {
            if (!ShouldScanRegion(region))
            {
                continue;
            }

            stats?.AddRegion();
            stats?.Report("Direct position scan", $"Region {TelemetryFormatting.Address(region.BaseAddress)} size {FormatBytes(region.Size)}.", important: false);
            for (var regionOffset = 0L; regionOffset < region.Size; regionOffset += chunkSize - overlap)
            {
                var bytesToRead = (int)Math.Min(chunkSize, region.Size - regionOffset);
                if (bytesToRead < 16)
                {
                    continue;
                }

                var block = new byte[bytesToRead];
                var blockAddress = region.BaseAddress + (nint)regionOffset;
                if (!memory.TryReadBytes(blockAddress, block))
                {
                    continue;
                }
                stats?.AddChunk(bytesToRead, bytesToRead / 4);

                var livePosition = TryReadSnowFlyerPosition()?.Value ?? knownPosition;
                ScanPositionBlock(block, blockAddress, livePosition, positionTolerance, positionOffsets, speedKmh, candidates, seenRigidBodies, stats);
                if (candidates.Count >= 80)
                {
                    break;
                }
            }

            if (candidates.Count >= 80)
            {
                break;
            }
        }

        var best = candidates
            .OrderByDescending(c => c.Pointer.Score)
            .FirstOrDefault();

        if (best.Pointer is not null && best.Pointer.RigidBody != 0 && best.Pointer.Score >= 55)
        {
            _pointer = best.Pointer;
        }

        _lastCandidates = _lastCandidates
            .Concat(candidates.OrderByDescending(c => c.Pointer.Score).Select(c => c.Row))
            .Take(120)
            .ToArray();
        stats?.SetCandidates(Math.Max(stats.Candidates, candidates.Count));
        stats?.Report("Direct position scan", $"{candidates.Count} candidate(s) from direct position scan.", force: true, important: true);
    }

    private void ScanPositionBlock(
        byte[] block,
        nint blockAddress,
        Vector3 knownPosition,
        float tolerance,
        int[] positionOffsets,
        float? speedKmh,
        List<(PointerResolution Pointer, CandidateReading Row)> candidates,
        HashSet<nint> seenRigidBodies,
        ScanStats? stats = null)
    {
        var span = block.AsSpan();
        var toleranceSquared = tolerance * tolerance;

        for (var i = 0; i + 12 <= span.Length; i += 4)
        {
            var x = MemoryMarshal.Read<float>(span.Slice(i, 4));
            if (!TelemetryMath.IsFinite(x) || Math.Abs(x - knownPosition.X) > tolerance)
            {
                continue;
            }

            var y = MemoryMarshal.Read<float>(span.Slice(i + 4, 4));
            var z = MemoryMarshal.Read<float>(span.Slice(i + 8, 4));
            var position = new Vector3(x, y, z);
            if (!TelemetryMath.IsFinite(position) || Vector3.DistanceSquared(position, knownPosition) > toleranceSquared)
            {
                continue;
            }
            stats?.AddHit();

            var positionAddress = blockAddress + i;
            foreach (var positionOffset in positionOffsets)
            {
                var rigidBody = positionAddress - positionOffset;
                if (!IsLikelyObjectPointer(rigidBody) || !seenRigidBodies.Add(rigidBody))
                {
                    continue;
                }

                if (!TryValidateDirectRigidBody(rigidBody, knownPosition, position, positionOffset, speedKmh, out var pointer, out var row))
                {
                    continue;
                }

                candidates.Add((pointer, row));
                stats?.SetCandidates(candidates.Count);
                stats?.Report("Direct position scan", $"Validated candidate {candidates.Count}: {TelemetryFormatting.Address(rigidBody)} score {pointer.Score:0}.", force: true, important: true);
                if (candidates.Count >= 80)
                {
                    return;
                }
            }
        }
    }

    private bool TryValidateDirectRigidBody(
        nint rigidBody,
        Vector3 knownPosition,
        Vector3 matchedPosition,
        int matchedPositionOffset,
        float? speedKmh,
        out PointerResolution pointer,
        out CandidateReading row)
    {
        pointer = PointerResolution.Empty;
        row = default!;

        var bestBasis = ScanBasisCandidates(rigidBody)
            .OrderByDescending(c => c.Score + (c.Offset == SnowRunnerOffsets.RigidBodyForward ? 5 : 0))
            .FirstOrDefault();
        if (bestBasis is null)
        {
            return false;
        }

        var basisScore = bestBasis.Score;
        var bestQuaternion = ScanQuaternionCandidates(rigidBody)
            .OrderByDescending(c => c.Score + (c.Offset is SnowRunnerOffsets.RigidBodyQuaternion0 or SnowRunnerOffsets.RigidBodyQuaternion1 ? 5 : 0))
            .FirstOrDefault();
        var quatScore = bestQuaternion?.Score ?? 0;
        var positionScore = TelemetryMath.ScorePosition(matchedPosition, knownPosition);
        var velocityScore = TryReadVector3(rigidBody + SnowRunnerOffsets.RigidBodyLinearVelocity, out var velocity)
            ? TelemetryMath.ScoreVelocity(velocity, speedKmh)
            : 0;

        var score = TelemetryMath.ClampScore(
            basisScore * 0.50 +
            quatScore * 0.20 +
            positionScore * 0.25 +
            velocityScore * 0.05);

        if (basisScore < 70 || positionScore < 55 || score < 55)
        {
            return false;
        }

        pointer = new PointerResolution(0, 0, 0, rigidBody, $"direct memory position scan, matched {TelemetryFormatting.Offset(matchedPositionOffset)}", score);
        row = new CandidateReading(
            "direct rb",
            TelemetryFormatting.Offset(matchedPositionOffset),
            TelemetryFormatting.Address(rigidBody),
            $"pos {TelemetryFormatting.Vector(matchedPosition)} basis {TelemetryFormatting.Offset(bestBasis.Offset)} {bestBasis.Layout}",
            TelemetryFormatting.Score(score),
            "position fingerprint + Havok basis validation");
        return true;
    }

    private static bool ShouldScanRegion(MemoryRegion region)
    {
        const uint memPrivate = 0x20000;
        const uint memMapped = 0x40000;
        const long maxRegionSize = 512L * 1024L * 1024L;

        if (region.Size <= 0 || region.Size > maxRegionSize)
        {
            return false;
        }

        return region.Type is memPrivate or memMapped;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L)
        {
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.00} GB";
        }

        if (bytes >= 1024L * 1024L)
        {
            return $"{bytes / (1024.0 * 1024.0):0.0} MB";
        }

        if (bytes >= 1024L)
        {
            return $"{bytes / 1024.0:0.0} KB";
        }

        return $"{bytes} B";
    }

    private (RigidBodyTelemetry Telemetry, IReadOnlyList<CandidateReading> Candidates) BuildRigidBodyTelemetry(
        nint rigidBody,
        Vector3? knownPosition,
        float? speedKmh,
        int? preferredBasisOffset = null,
        bool lockedOffsets = false)
    {
        if (_pointer.RigidBody == rigidBody &&
            _pointer.Source.Contains("SnowFlyer position chain", StringComparison.OrdinalIgnoreCase) &&
            TryBuildSnowFlyerRigidBodyTelemetry(rigidBody, knownPosition, speedKmh, out var snowFlyerResult))
        {
            return snowFlyerResult;
        }

        if (lockedOffsets &&
            TryBuildLockedRigidBodyTelemetry(rigidBody, knownPosition, speedKmh, out var lockedResult))
        {
            return lockedResult;
        }

        var candidates = new List<CandidateReading>();

        var basisCandidateList = ScanBasisCandidates(rigidBody).ToList();
        if (preferredBasisOffset.HasValue &&
            basisCandidateList.All(c => c.Offset != preferredBasisOffset.Value) &&
            TryReadBasis(rigidBody, preferredBasisOffset.Value, out var preferredBasis) &&
            preferredBasis.Score > 0)
        {
            basisCandidateList.Add(preferredBasis);
        }

        var basisCandidates = basisCandidateList.ToArray();
        var bestBasis = basisCandidates
            .OrderByDescending(c => ScoreBasisCandidate(c, preferredBasisOffset))
            .FirstOrDefault();

        foreach (var basis in basisCandidates.Take(12))
        {
            candidates.Add(new CandidateReading(
                "basis",
                TelemetryFormatting.Offset(basis.Offset),
                TelemetryFormatting.Address(basis.Address),
                $"{basis.Layout} F {TelemetryFormatting.Vector(basis.Forward)} U {TelemetryFormatting.Vector(basis.Up)} R {TelemetryFormatting.Vector(basis.Right)}",
                TelemetryFormatting.Score(basis.Score),
                basis.Offset == SnowRunnerOffsets.RigidBodyForward ? "Noclip exact matrix offset" : "nearby rigid-body matrix probe"));
        }

        var quatCandidates = ScanQuaternionCandidates(rigidBody).ToArray();
        var bestQuat = quatCandidates
            .OrderByDescending(c => ScoreQuaternionCandidate(c, bestBasis))
            .FirstOrDefault();

        foreach (var quat in quatCandidates.Take(10))
        {
            candidates.Add(new CandidateReading(
                "quaternion",
                TelemetryFormatting.Offset(quat.Offset),
                TelemetryFormatting.Address(quat.Address),
                TelemetryFormatting.Quaternion(quat.Value),
                TelemetryFormatting.Score(quat.Score),
                quat.Offset is SnowRunnerOffsets.RigidBodyQuaternion0 or SnowRunnerOffsets.RigidBodyQuaternion1 ? "Noclip exact quaternion offset" : "nearby quaternion probe"));
        }

        var positionCandidates = ScanVectorCandidates(
            rigidBody,
            0x160,
            0x220,
            "position",
            offset => TelemetryMath.ScorePosition(ReadVectorOrZero(rigidBody + offset), knownPosition)).ToArray();
        var bestPosition = positionCandidates
            .OrderByDescending(c => c.Score + (c.Offset == SnowRunnerOffsets.RigidBodyPosition ? 5 : 0))
            .FirstOrDefault();

        foreach (var position in positionCandidates.Take(10))
        {
            candidates.Add(new CandidateReading(
                "position",
                TelemetryFormatting.Offset(position.Offset),
                TelemetryFormatting.Address(position.Address),
                TelemetryFormatting.Vector(position.Value),
                TelemetryFormatting.Score(position.Score),
                position.Offset == SnowRunnerOffsets.RigidBodyPosition ? "Noclip exact rigid-body position" : "nearby position probe"));
        }

        var linearVelocityCandidates = ScanVectorCandidates(
            rigidBody,
            0x200,
            0x290,
            "linear velocity",
            offset => TelemetryMath.ScoreVelocity(ReadVectorOrZero(rigidBody + offset), speedKmh))
            .Where(c => !OverlapsTransformCandidate(c.Offset, bestBasis, bestQuat))
            .ToArray();
        var bestLinearVelocity = linearVelocityCandidates
            .OrderByDescending(c => c.Score + (c.Offset == SnowRunnerOffsets.RigidBodyLinearVelocity ? 35 : 0))
            .FirstOrDefault();

        foreach (var velocity in linearVelocityCandidates.Take(10))
        {
            candidates.Add(new CandidateReading(
                "linear velocity",
                TelemetryFormatting.Offset(velocity.Offset),
                TelemetryFormatting.Address(velocity.Address),
                TelemetryFormatting.Vector(velocity.Value),
                TelemetryFormatting.Score(velocity.Score),
                velocity.Offset == SnowRunnerOffsets.RigidBodyLinearVelocity ? "Noclip exact linear velocity" : "nearby velocity probe"));
        }

        var localAcceleration = TryBuildLocalAcceleration(rigidBody, bestBasis, bestLinearVelocity);
        if (localAcceleration is not null)
        {
            candidates.Add(new CandidateReading(
                "local acceleration",
                TelemetryFormatting.Offset(localAcceleration.Offset),
                TelemetryFormatting.Address(localAcceleration.Address),
                TelemetryFormatting.LocalAcceleration(localAcceleration.Value),
                TelemetryFormatting.Score(localAcceleration.Score),
                "derived from linear velocity delta in chassis basis"));
        }

        float? linearSpeedKmh = bestLinearVelocity is null ? null : bestLinearVelocity.Value.Length() * 3.6f;
        var validation = BuildValidation(bestBasis, bestQuat, bestPosition, bestLinearVelocity, localAcceleration, speedKmh, knownPosition);
        var telemetry = new RigidBodyTelemetry(bestBasis, bestQuat, bestPosition, bestLinearVelocity, localAcceleration, linearSpeedKmh, validation);

        return (telemetry, candidates.OrderByDescending(c => int.TryParse(c.Score, out var s) ? s : 0).ToArray());
    }

    private bool TryBuildSnowFlyerRigidBodyTelemetry(
        nint rigidBody,
        Vector3? knownPosition,
        float? speedKmh,
        out (RigidBodyTelemetry Telemetry, IReadOnlyList<CandidateReading> Candidates) result)
    {
        result = default;
        if (!TryReadSnowFlyerBasis(rigidBody, out var basis))
        {
            return false;
        }

        QuaternionReading? quaternion = null;
        if (TryReadSnowFlyerQuaternion(rigidBody, SnowRunnerOffsets.RigidBodyQuaternion0, out var quaternion0))
        {
            quaternion = quaternion0;
        }
        else if (TryReadSnowFlyerQuaternion(rigidBody, SnowRunnerOffsets.RigidBodyQuaternion1, out var quaternion1))
        {
            quaternion = quaternion1;
        }

        if (quaternion is null ||
            !TryReadVector3(rigidBody + SnowRunnerOffsets.RigidBodyLinearVelocity, out var velocityValue))
        {
            return false;
        }

        var position = knownPosition.HasValue
            ? new VectorReading(
                SnowRunnerOffsets.SnowFlyerRigidBodyPosition,
                rigidBody + SnowRunnerOffsets.SnowFlyerRigidBodyPosition,
                knownPosition.Value,
                100,
                "position",
                "SnowFlyer direct chassis position")
            : null;
        var linearVelocity = new VectorReading(
            SnowRunnerOffsets.RigidBodyLinearVelocity,
            rigidBody + SnowRunnerOffsets.RigidBodyLinearVelocity,
            velocityValue,
            TelemetryMath.ScoreVelocity(velocityValue, speedKmh),
            "linear velocity",
            "SnowFlyer direct chassis velocity");
        var localAcceleration = TryBuildLocalAcceleration(rigidBody, basis, linearVelocity);

        var candidates = new List<CandidateReading>
        {
            new(
                "basis",
                "+0x170",
                TelemetryFormatting.Address(basis.Address),
                $"{basis.Layout} F {TelemetryFormatting.Vector(basis.Forward)} U {TelemetryFormatting.Vector(basis.Up)} R {TelemetryFormatting.Vector(basis.Right)}",
                TelemetryFormatting.Score(basis.Score),
                "SnowFlyer direct chassis matrix"),
            new(
                "quaternion",
                TelemetryFormatting.Offset(quaternion.Offset),
                TelemetryFormatting.Address(quaternion.Address),
                TelemetryFormatting.Quaternion(quaternion.Value),
                TelemetryFormatting.Score(quaternion.Score),
                "SnowFlyer direct chassis quaternion"),
            new(
                "linear velocity",
                "+0x230",
                TelemetryFormatting.Address(linearVelocity.Address),
                TelemetryFormatting.Vector(linearVelocity.Value),
                TelemetryFormatting.Score(linearVelocity.Score),
                "SnowFlyer direct chassis velocity")
        };
        if (localAcceleration is not null)
        {
            candidates.Add(new CandidateReading(
                "local acceleration",
                "+0x230",
                TelemetryFormatting.Address(localAcceleration.Address),
                TelemetryFormatting.LocalAcceleration(localAcceleration.Value),
                "100",
                "velocity delta projected into direct chassis basis"));
        }

        var validation = $"stable position-chain chassis; basis +0x170 score {basis.Score:0}; quaternion +0x{quaternion.Offset:X} score {quaternion.Score:0}; velocity +0x230";
        var telemetry = new RigidBodyTelemetry(
            basis,
            quaternion,
            position,
            linearVelocity,
            localAcceleration,
            velocityValue.Length() * 3.6f,
            validation);
        result = (telemetry, candidates);
        return true;
    }

    private bool TryBuildLockedRigidBodyTelemetry(
        nint rigidBody,
        Vector3? knownPosition,
        float? speedKmh,
        out (RigidBodyTelemetry Telemetry, IReadOnlyList<CandidateReading> Candidates) result)
    {
        result = default;
        if (!TryReadBasis(rigidBody, SnowRunnerOffsets.RigidBodyForward, out var basis))
        {
            return false;
        }

        var candidates = new List<CandidateReading>
        {
            new(
                "basis",
                TelemetryFormatting.Offset(basis.Offset),
                TelemetryFormatting.Address(basis.Address),
                $"{basis.Layout} F {TelemetryFormatting.Vector(basis.Forward)} U {TelemetryFormatting.Vector(basis.Up)} R {TelemetryFormatting.Vector(basis.Right)}",
                TelemetryFormatting.Score(basis.Score),
                "locked chassis matrix offset")
        };

        var quaternions = new List<QuaternionReading>();
        if (TryReadQuaternion(rigidBody, SnowRunnerOffsets.RigidBodyQuaternion0, out var quaternion0))
        {
            quaternions.Add(quaternion0);
        }

        if (TryReadQuaternion(rigidBody, SnowRunnerOffsets.RigidBodyQuaternion1, out var quaternion1))
        {
            quaternions.Add(quaternion1);
        }

        var bestQuat = quaternions
            .OrderByDescending(q => ScoreQuaternionCandidate(q, basis))
            .FirstOrDefault();
        foreach (var quaternion in quaternions)
        {
            candidates.Add(new CandidateReading(
                "quaternion",
                TelemetryFormatting.Offset(quaternion.Offset),
                TelemetryFormatting.Address(quaternion.Address),
                TelemetryFormatting.Quaternion(quaternion.Value),
                TelemetryFormatting.Score(quaternion.Score),
                "locked chassis quaternion offset"));
        }

        VectorReading? linearVelocity = null;
        var velocityAddress = rigidBody + SnowRunnerOffsets.RigidBodyLinearVelocity;
        if (TryReadVector3(velocityAddress, out var velocity))
        {
            linearVelocity = new VectorReading(
                SnowRunnerOffsets.RigidBodyLinearVelocity,
                velocityAddress,
                velocity,
                TelemetryMath.ScoreVelocity(velocity, speedKmh),
                "linear velocity",
                "locked chassis linear velocity offset");
            candidates.Add(new CandidateReading(
                "linear velocity",
                TelemetryFormatting.Offset(linearVelocity.Offset),
                TelemetryFormatting.Address(linearVelocity.Address),
                TelemetryFormatting.Vector(linearVelocity.Value),
                TelemetryFormatting.Score(linearVelocity.Score),
                "locked chassis linear velocity offset"));
        }

        var localAcceleration = TryBuildLocalAcceleration(rigidBody, basis, linearVelocity);
        if (localAcceleration is not null)
        {
            candidates.Add(new CandidateReading(
                "local acceleration",
                TelemetryFormatting.Offset(localAcceleration.Offset),
                TelemetryFormatting.Address(localAcceleration.Address),
                TelemetryFormatting.LocalAcceleration(localAcceleration.Value),
                TelemetryFormatting.Score(localAcceleration.Score),
                "derived from locked velocity delta in chassis basis"));
        }

        float? linearSpeedKmh = linearVelocity is null ? null : linearVelocity.Value.Length() * 3.6f;
        var validation = BuildValidation(basis, bestQuat, null, linearVelocity, localAcceleration, speedKmh, knownPosition);
        var telemetry = new RigidBodyTelemetry(basis, bestQuat, null, linearVelocity, localAcceleration, linearSpeedKmh, validation);
        result = (telemetry, candidates.ToArray());
        return true;
    }

    private VectorReading? TryBuildLocalAcceleration(nint rigidBody, BasisReading? basis, VectorReading? linearVelocity)
    {
        if (basis is null || linearVelocity is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (_accelerationRigidBody != rigidBody ||
            _accelerationVelocityOffset != linearVelocity.Offset ||
            !_hasAccelerationSample)
        {
            SeedAccelerationTracker(rigidBody, linearVelocity, now);
            return null;
        }

        var dt = (now - _previousVelocityTime).TotalSeconds;
        if (dt < 0.015)
        {
            return null;
        }

        if (dt > 1.0)
        {
            SeedAccelerationTracker(rigidBody, linearVelocity, now);
            return null;
        }

        var worldAcceleration = (linearVelocity.Value - _previousVelocityWorld) / (float)dt;
        _previousVelocityWorld = linearVelocity.Value;
        _previousVelocityTime = now;

        if (!TelemetryMath.IsFinite(worldAcceleration) || worldAcceleration.Length() > 250)
        {
            _smoothedLocalAcceleration = Vector3.Zero;
            return null;
        }

        var forward = TelemetryMath.NormalizeOrZero(basis.Forward);
        var right = TelemetryMath.NormalizeOrZero(basis.Right);
        var up = TelemetryMath.NormalizeOrZero(basis.Up);
        if (forward == Vector3.Zero || right == Vector3.Zero || up == Vector3.Zero)
        {
            return null;
        }

        var localAcceleration = new Vector3(
            Vector3.Dot(worldAcceleration, forward),
            Vector3.Dot(worldAcceleration, right),
            Vector3.Dot(worldAcceleration, up));

        const float smoothingAlpha = 0.35f;
        _smoothedLocalAcceleration = _smoothedLocalAcceleration * (1 - smoothingAlpha) + localAcceleration * smoothingAlpha;
        return new VectorReading(
            linearVelocity.Offset,
            linearVelocity.Address,
            _smoothedLocalAcceleration,
            100,
            "local acceleration",
            "derived from linear velocity delta in chassis basis");
    }

    private void SeedAccelerationTracker(nint rigidBody, VectorReading linearVelocity, DateTimeOffset timestamp)
    {
        _accelerationRigidBody = rigidBody;
        _accelerationVelocityOffset = linearVelocity.Offset;
        _previousVelocityWorld = linearVelocity.Value;
        _previousVelocityTime = timestamp;
        _smoothedLocalAcceleration = Vector3.Zero;
        _hasAccelerationSample = true;
    }

    private IEnumerable<BasisReading> ScanBasisCandidates(nint rigidBody)
    {
        for (var offset = 0x40; offset <= 0x580; offset += 0x10)
        {
            if (TryReadBasis(rigidBody, offset, out var basis) && basis.Score >= 45)
            {
                yield return basis;
            }
        }
    }

    private IEnumerable<QuaternionReading> ScanQuaternionCandidates(nint rigidBody)
    {
        for (var offset = 0x40; offset <= 0x680; offset += 0x10)
        {
            if (TryReadQuaternion(rigidBody, offset, out var quat) && quat.Score >= 60)
            {
                yield return quat;
            }
        }
    }

    private IEnumerable<VectorReading> ScanVectorCandidates(nint rigidBody, int startOffset, int endOffset, string kind, Func<int, double> scorer)
    {
        for (var offset = startOffset; offset <= endOffset; offset += 0x10)
        {
            var address = rigidBody + offset;
            if (!TryReadVector3(address, out var value))
            {
                continue;
            }

            var score = scorer(offset);
            if (score >= 35)
            {
                yield return new VectorReading(offset, address, value, score, kind, "rigid-body nearby probe");
            }
        }
    }

    private bool TryReadBasis(nint rigidBody, int offset, out BasisReading basis)
    {
        basis = default!;
        if (!TryReadVector3(rigidBody + offset, out var a) ||
            !TryReadVector3(rigidBody + offset + 0x10, out var b) ||
            !TryReadVector3(rigidBody + offset + 0x20, out var c))
        {
            return false;
        }

        return TryChooseBasisLayout(offset, rigidBody + offset, a, b, c, out basis);
    }

    private bool TryReadSnowFlyerBasis(nint rigidBody, out BasisReading basis)
    {
        basis = default!;
        var address = rigidBody + SnowRunnerOffsets.RigidBodyForward;
        if (!TryReadVector3(address, out var forward) ||
            !TryReadVector3(address + 0x10, out var up) ||
            !TryReadVector3(address + 0x20, out var right))
        {
            return false;
        }

        var score = ScoreChassisBasis(forward, up, right);
        if (score <= 0)
        {
            return false;
        }

        var euler = TelemetryMath.EulerFromBasis(forward, up, right);
        basis = new BasisReading(
            SnowRunnerOffsets.RigidBodyForward,
            address,
            forward,
            up,
            right,
            score,
            TelemetryMath.NormalizeDegrees(euler.PitchDeg),
            TelemetryMath.NormalizeDegrees(euler.RollDeg),
            TelemetryMath.NormalizeDegrees(euler.YawDeg),
            "F=A+ U=B+ R=C+");
        return true;
    }

    private bool TryReadSnowFlyerQuaternion(nint rigidBody, int offset, out QuaternionReading reading)
    {
        reading = default!;
        var address = rigidBody + offset;
        if (!TryReadQuaternion(address, out var quaternion))
        {
            return false;
        }

        var score = TelemetryMath.ScoreQuaternion(quaternion);
        if (score <= 0)
        {
            return false;
        }

        var basis = TelemetryMath.BasisFromQuaternion(quaternion);
        var euler = TelemetryMath.EulerFromBasis(basis.Forward, basis.Up, basis.Right);
        reading = new QuaternionReading(
            offset,
            address,
            quaternion,
            score,
            TelemetryMath.NormalizeDegrees(euler.PitchDeg),
            TelemetryMath.NormalizeDegrees(euler.RollDeg),
            TelemetryMath.NormalizeDegrees(euler.YawDeg));
        return true;
    }

    private static bool TryChooseBasisLayout(int offset, nint address, Vector3 a, Vector3 b, Vector3 c, out BasisReading basis)
    {
        basis = default!;
        var axes = new[]
        {
            ("A", a),
            ("B", b),
            ("C", c)
        };

        BasisReading? best = null;
        for (var upIndex = 0; upIndex < axes.Length; upIndex++)
        {
            var upSign = axes[upIndex].Item2.Y >= 0 ? 1 : -1;
            var up = axes[upIndex].Item2 * upSign;
            var remaining = Enumerable.Range(0, axes.Length).Where(i => i != upIndex).ToArray();
            foreach (var forwardIndex in remaining)
            {
                var rightIndex = remaining.First(i => i != forwardIndex);
                foreach (var forwardSign in new[] { 1, -1 })
                {
                    foreach (var rightSign in new[] { 1, -1 })
                    {
                        var forward = axes[forwardIndex].Item2 * forwardSign;
                        var right = axes[rightIndex].Item2 * rightSign;
                        var score = ScoreChassisBasis(forward, up, right);
                        if (score <= 0)
                        {
                            continue;
                        }

                        var euler = ApplyFlatReferenceOffset(TelemetryMath.EulerFromBasis(forward, up, right));
                        var layout =
                            $"F={axes[forwardIndex].Item1}{SignText(forwardSign)} U={axes[upIndex].Item1}{SignText(upSign)} R={axes[rightIndex].Item1}{SignText(rightSign)}";
                        var candidate = new BasisReading(offset, address, forward, up, right, score, euler.PitchDeg, euler.RollDeg, euler.YawDeg, layout);
                        if (best is null || candidate.Score > best.Score)
                        {
                            best = candidate;
                        }
                    }
                }
            }
        }

        if (best is null)
        {
            return false;
        }

        basis = best;
        return basis.Score > 0;
    }

    private static double ScoreChassisBasis(Vector3 forward, Vector3 up, Vector3 right)
    {
        if (!TelemetryMath.IsFinite(forward) || !TelemetryMath.IsFinite(up) || !TelemetryMath.IsFinite(right))
        {
            return 0;
        }

        var forwardLength = forward.Length();
        var upLength = up.Length();
        var rightLength = right.Length();
        if (forwardLength < 0.2f || upLength < 0.2f || rightLength < 0.2f ||
            forwardLength > 2f || upLength > 2f || rightLength > 2f)
        {
            return 0;
        }

        var f = Vector3.Normalize(forward);
        var u = Vector3.Normalize(up);
        var r = Vector3.Normalize(right);
        var euler = TelemetryMath.EulerFromBasis(forward, up, right);

        var lengthPenalty = (Math.Abs(forwardLength - 1) + Math.Abs(upLength - 1) + Math.Abs(rightLength - 1)) * 28.0;
        var dotPenalty = (Math.Abs(Vector3.Dot(f, u)) + Math.Abs(Vector3.Dot(f, r)) + Math.Abs(Vector3.Dot(u, r))) * 42.0;
        var handedness = Vector3.Dot(Vector3.Cross(f, r), u);
        var handedPenalty = Math.Abs(Math.Abs(handedness) - 1.0) * 18.0;
        var uprightPenalty = Math.Max(0, 0.55 - u.Y) * 60.0;
        var extremePenalty = Math.Max(0, Math.Abs(euler.PitchDeg) - 65) * 1.3 +
                             Math.Max(0, Math.Abs(euler.RollDeg) - 65) * 1.3;
        var uprightBonus = Math.Max(0, u.Y) * 6.0;

        return TelemetryMath.ClampScore(100.0 - lengthPenalty - dotPenalty - handedPenalty - uprightPenalty - extremePenalty + uprightBonus);
    }

    private static string SignText(int sign)
    {
        return sign >= 0 ? "+" : "-";
    }

    private static (double PitchDeg, double RollDeg, double YawDeg) ApplyFlatReferenceOffset((double PitchDeg, double RollDeg, double YawDeg) euler)
    {
        return (
            TelemetryMath.NormalizeDegrees(euler.PitchDeg - SnowRunnerOffsets.FlatPitchOffsetDeg),
            TelemetryMath.NormalizeDegrees(euler.RollDeg - SnowRunnerOffsets.FlatRollOffsetDeg),
            euler.YawDeg);
    }

    private bool TryReadQuaternion(nint rigidBody, int offset, out QuaternionReading reading)
    {
        reading = default!;
        var address = rigidBody + offset;
        if (!TryReadQuaternion(address, out var quaternion))
        {
            return false;
        }

        var score = TelemetryMath.ScoreQuaternion(quaternion);
        var basis = TelemetryMath.BasisFromQuaternion(quaternion);
        var euler = ApplyFlatReferenceOffset(TelemetryMath.EulerFromBasis(basis.Forward, basis.Up, basis.Right));
        reading = new QuaternionReading(offset, address, quaternion, score, euler.PitchDeg, euler.RollDeg, euler.YawDeg);
        return score > 0;
    }

    private bool TryReadVector3(nint address, out Vector3 value)
    {
        value = default;
        var memory = _memory;
        if (memory is null ||
            !memory.TryRead<float>(address, out var x) ||
            !memory.TryRead<float>(address + 0x4, out var y) ||
            !memory.TryRead<float>(address + 0x8, out var z))
        {
            return false;
        }

        value = new Vector3(x, y, z);
        return TelemetryMath.IsFinite(value);
    }

    private bool TryReadQuaternion(nint address, out Quaternion value)
    {
        value = default;
        var memory = _memory;
        if (memory is null ||
            !memory.TryRead<float>(address, out var x) ||
            !memory.TryRead<float>(address + 0x4, out var y) ||
            !memory.TryRead<float>(address + 0x8, out var z) ||
            !memory.TryRead<float>(address + 0xC, out var w))
        {
            return false;
        }

        value = new Quaternion(x, y, z, w);
        return TelemetryMath.IsFinite(value);
    }

    private Vector3 ReadVectorOrZero(nint address)
    {
        return TryReadVector3(address, out var value) ? value : Vector3.Zero;
    }

    private static double ScoreBasisCandidate(BasisReading basis, int? preferredBasisOffset)
    {
        var exactBonus = basis.Offset == SnowRunnerOffsets.RigidBodyForward ? 5 : 0;
        var preferredBonus = preferredBasisOffset.HasValue && basis.Offset == preferredBasisOffset.Value ? 100 : 0;
        return basis.Score + exactBonus + preferredBonus;
    }

    private static double ScoreQuaternionCandidate(QuaternionReading quaternion, BasisReading? basis)
    {
        var exactBonus = quaternion.Offset is SnowRunnerOffsets.RigidBodyQuaternion0 or SnowRunnerOffsets.RigidBodyQuaternion1 ? 5 : 0;
        if (basis is null)
        {
            return quaternion.Score + exactBonus;
        }

        return quaternion.Score * 0.70 + ScoreQuaternionBasisAlignment(quaternion, basis) * 0.30 + exactBonus;
    }

    private static double ScoreQuaternionBasisAlignment(QuaternionReading quaternion, BasisReading basis)
    {
        var qb = TelemetryMath.BasisFromQuaternion(quaternion.Value);
        var qForward = TelemetryMath.NormalizeOrZero(qb.Forward);
        var qUp = TelemetryMath.NormalizeOrZero(qb.Up);
        var qRight = TelemetryMath.NormalizeOrZero(qb.Right);
        var bForward = TelemetryMath.NormalizeOrZero(basis.Forward);
        var bUp = TelemetryMath.NormalizeOrZero(basis.Up);
        var bRight = TelemetryMath.NormalizeOrZero(basis.Right);

        if (qForward == Vector3.Zero || qUp == Vector3.Zero || qRight == Vector3.Zero ||
            bForward == Vector3.Zero || bUp == Vector3.Zero || bRight == Vector3.Zero)
        {
            return 0;
        }

        var forward = (Vector3.Dot(qForward, bForward) + 1) * 50;
        var up = (Vector3.Dot(qUp, bUp) + 1) * 50;
        var right = (Vector3.Dot(qRight, bRight) + 1) * 50;
        return TelemetryMath.ClampScore((forward + up + right) / 3.0);
    }

    private static bool OverlapsTransformCandidate(int offset, BasisReading? basis, QuaternionReading? quaternion)
    {
        if (basis is not null && offset >= basis.Offset && offset <= basis.Offset + 0x20)
        {
            return true;
        }

        if (quaternion is not null && offset >= quaternion.Offset && offset <= quaternion.Offset + 0x0C)
        {
            return true;
        }

        return false;
    }

    private static string BuildValidation(
        BasisReading? basis,
        QuaternionReading? quaternion,
        VectorReading? position,
        VectorReading? linearVelocity,
        VectorReading? localAcceleration,
        float? speedKmh,
        Vector3? knownPosition)
    {
        var parts = new List<string>();
        parts.Add(basis is null ? "basis missing" : $"basis {basis.Offset:X} score {basis.Score:0}");
        parts.Add(quaternion is null ? "quat missing" : $"quat {quaternion.Offset:X} score {quaternion.Score:0}");

        if (position is not null && knownPosition.HasValue)
        {
            parts.Add($"position delta {Vector3.Distance(position.Value, knownPosition.Value):0.000}");
        }
        else if (position is not null)
        {
            parts.Add($"position {position.Offset:X} finite");
        }

        if (linearVelocity is not null && speedKmh.HasValue)
        {
            parts.Add($"velocity mag {linearVelocity.Value.Length() * 3.6f:0.0} km/h vs speed {speedKmh.Value:0.0}");
        }

        if (localAcceleration is not null)
        {
            parts.Add($"local accel F/L/V {localAcceleration.Value.X:0.00}/{localAcceleration.Value.Y:0.00}/{localAcceleration.Value.Z:0.00} m/s^2");
        }
        else if (linearVelocity is not null)
        {
            parts.Add("local accel waiting for second velocity sample");
        }

        return string.Join("; ", parts);
    }

    private static bool LooksLikeUserPointer(ulong value)
    {
        return value is > 0x10000 and < 0x0000800000000000;
    }

    private static bool IsLikelyObjectPointer(nint value)
    {
        var raw = unchecked((ulong)(long)value);
        return LooksLikeUserPointer(raw) && (raw & 0x7UL) == 0;
    }
}
