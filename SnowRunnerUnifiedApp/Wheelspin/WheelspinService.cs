using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;

namespace SnowRunnerWheelspinFinder;

internal sealed class WheelspinService : IDisposable
{
    private const int TargetRateHz = 60;
    private const int CapturePreparationSeconds = 5;
    private const int CaptureRecordingSeconds = 10;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentQueue<string> _pendingLogs = new();
    private readonly List<string> _fullLogs = new();
    private readonly Dictionary<string, CandidateTracker> _candidateTrackers = new(StringComparer.Ordinal);
    private readonly Dictionary<nint, WheelTracker> _wheelTrackers = new();
    private readonly Dictionary<nint, QuaternionHistory> _quaternionHistory = new();
    private readonly object _captureGate = new();
    private ProcessMemoryReader? _memory;
    private SnowRunnerRootResolver? _resolver;
    private Task? _loopTask;
    private Task<nint>? _rootSearchTask;
    private CancellationTokenSource? _rootSearchCancellation;
    private Task<ActionSearchResult?>? _actionSearchTask;
    private CancellationTokenSource? _actionSearchCancellation;
    private FinderSnapshot _latest = FinderSnapshot.Empty("Starting read-only wheel discovery.");
    private IslandDescriptor? _island;
    private WheelModelDescriptor? _wheelModels;
    private IReadOnlyList<CandidateSnapshot> _candidateSnapshotCache = Array.Empty<CandidateSnapshot>();
    private DateTimeOffset _lastCandidateSnapshot = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAttachAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRootSearchStart = DateTimeOffset.MinValue;
    private DateTimeOffset _rateWindowStart = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastPhysicsChange = DateTimeOffset.MinValue;
    private Vector3 _lastChassisVelocity;
    private long _rateSamples;
    private long _physicsChanges;
    private long _candidateOrdinal;
    private long _wheelOrdinal;
    private double _measuredRate;
    private double _physicsChangeRate;
    private nint _epochChassis;
    private nint _epochVehicle;
    private string _bodyArrayFingerprint = "";
    private DateTimeOffset _lastIslandChangeLog = DateTimeOffset.MinValue;
    private nint _lastActionHypothesis;
    private int _epoch;
    private volatile bool _paused;
    private volatile bool _restartRequested;
    private CapturePhase _capturePhase;
    private bool _captureRecordingStarted;
    private DateTimeOffset _captureStart;
    private DateTimeOffset _captureEnd;
    private string _status = "Starting.";
    private DiscoveryProgress _progress = DiscoveryProgress.Idle;

    public FinderSnapshot LatestSnapshot => Volatile.Read(ref _latest);
    public bool Paused => _paused;

    public void Start()
    {
        _loopTask ??= Task.Run(() => RunLoop(_cancellation.Token));
    }

    public void SetPaused(bool value)
    {
        _paused = value;
        Log(value
            ? "Presentation and proof capture paused; root monitoring remains active."
            : "Sampling and proof capture resumed.");
    }

    public void RestartDiscovery()
    {
        _restartRequested = true;
        Log("A fresh bounded wheel discovery was requested.");
    }

    public void StartCapture(CapturePhase phase)
    {
        if (phase == CapturePhase.None)
        {
            return;
        }

        lock (_captureGate)
        {
            var now = DateTimeOffset.UtcNow;
            _capturePhase = phase;
            _captureRecordingStarted = false;
            _captureStart = now.AddSeconds(CapturePreparationSeconds);
            _captureEnd = _captureStart.AddSeconds(CaptureRecordingSeconds);
        }

        Log($"Guided proof armed: {CaptureName(phase)} ({CapturePreparationSeconds}-second preparation, then {CaptureRecordingSeconds}-second recording; current vehicle epoch only).");
    }

    public void CancelCapture()
    {
        var wasActive = false;
        lock (_captureGate)
        {
            wasActive = _capturePhase != CapturePhase.None;
            _capturePhase = CapturePhase.None;
            _captureRecordingStarted = false;
            _captureStart = DateTimeOffset.MinValue;
            _captureEnd = DateTimeOffset.MinValue;
        }

        if (wasActive)
        {
            Log("Guided proof cancelled.");
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _rootSearchCancellation?.Cancel();
        _actionSearchCancellation?.Cancel();
        try
        {
            _loopTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException aggregate) when (aggregate.InnerExceptions.All(error => error is TaskCanceledException or OperationCanceledException))
        {
        }

        _rootSearchCancellation?.Dispose();
        _actionSearchCancellation?.Dispose();
        _memory?.Dispose();
        _cancellation.Dispose();
    }

    private void RunLoop(CancellationToken cancellationToken)
    {
        NativeMethods.timeBeginPeriod(1);
        try
        {
            var scheduler = Stopwatch.StartNew();
            var periodTicks = Stopwatch.Frequency / (double)TargetRateHz;
            double nextTick = scheduler.ElapsedTicks;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    Sample(DateTimeOffset.UtcNow, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log($"Acquisition error: {ex}");
                    _status = "A read failed safely; discovery will retry.";
                }

                nextTick += periodTicks;
                var nowTicks = scheduler.ElapsedTicks;
                if (nextTick < nowTicks - periodTicks)
                {
                    nextTick = nowTicks + periodTicks;
                }

                WaitForDeadline(scheduler, nextTick, cancellationToken);
            }
        }
        finally
        {
            NativeMethods.timeEndPeriod(1);
        }
    }

    private void Sample(DateTimeOffset now, CancellationToken cancellationToken)
    {
        FlushLogs();
        if (_memory is null || !_memory.IsAlive)
        {
            if (_memory is not null)
            {
                Log("SnowRunner exited. Cleared every process, vehicle, wheel, and candidate address.");
                ResetProcess();
            }

            if (now - _lastAttachAttempt >= TimeSpan.FromSeconds(2))
            {
                _lastAttachAttempt = now;
                Attach();
            }

            PublishDisconnected(now);
            return;
        }

        var memory = _memory;
        var resolver = _resolver;
        if (resolver is null || !resolver.TryResolveChassis(out var chassis, out var chassisPosition, out var chassisPath))
        {
            InvalidateEpoch("Waiting for the active SnowFlyer chassis.");
            _status = "Attached; waiting for an active truck.";
            PublishAttachedWithoutTruck(now, memory);
            return;
        }

        var hasVehicle = resolver.TryResolveActiveVehicle(chassis, out var truckControl, out var vehicle, out var vehiclePath);
        if (!hasVehicle)
        {
            truckControl = 0;
            vehicle = 0;
            vehiclePath = "TRUCK_CONTROL cross-link not resolved; chassis route remains active";
        }

        if (_restartRequested)
        {
            _restartRequested = false;
            BeginEpoch(chassis, vehicle, "manual rediscovery");
        }
        else if (chassis != _epochChassis || (vehicle != 0 && vehicle != _epochVehicle))
        {
            BeginEpoch(chassis, vehicle, chassis != _epochChassis ? "active chassis changed" : "active vehicle root changed");
        }

        if (!hasVehicle)
        {
            StartRootSearch(chassis, cancellationToken);
        }

        if (!resolver.TryReadBasis(chassis, out var chassisBasis) || chassisBasis.Score < 70 ||
            !memory.TryRead<Vector3>(chassis + 0x230, out var chassisVelocity) || !FinderMath.IsFinite(chassisVelocity) ||
            !memory.TryRead<Vector3>(chassis + 0x240, out var chassisOmega) || !FinderMath.IsFinite(chassisOmega))
        {
            InvalidateEpoch("The chassis pointer stopped validating.");
            PublishAttachedWithoutTruck(now, memory);
            return;
        }

        var chassisLongitudinal = Vector3.Dot(chassisVelocity, chassisBasis.Forward);
        TrackRates(now, chassisVelocity);

        if (_paused)
        {
            var pausedWheels = _latest.Root?.Epoch == _epoch ? _latest.Wheels : Array.Empty<WheelSnapshot>();
            PublishSnapshot(now, memory, new RootState(
                resolver.SnowFlyerStatic, truckControl, vehicle, chassis, chassisPath, vehiclePath, _epoch),
                chassisLongitudinal, chassisVelocity.Length(), pausedWheels);
            return;
        }

        _island ??= DiscoverIsland(chassis, chassisPosition, chassisBasis);
        if (_island is null || !TryReadBodyPointers(_island, out var bodyPointers) || !bodyPointers.Contains(chassis))
        {
            _island = DiscoverIsland(chassis, chassisPosition, chassisBasis);
            if (_island is null || !TryReadBodyPointers(_island, out bodyPointers))
            {
                _status = "Chassis found; searching its bounded physics graph for the simulation island.";
                PublishSnapshot(now, memory, new RootState(
                    resolver.SnowFlyerStatic, truckControl, vehicle, chassis, chassisPath, vehiclePath, _epoch),
                    chassisLongitudinal, chassisVelocity.Length(), Array.Empty<WheelSnapshot>());
                return;
            }
        }

        var bodyFingerprint = string.Join(";", bodyPointers.Select(pointer => ((ulong)(long)pointer).ToString("X16", CultureInfo.InvariantCulture)));
        if (string.IsNullOrEmpty(_bodyArrayFingerprint))
        {
            _bodyArrayFingerprint = bodyFingerprint;
        }
        else if (!string.Equals(_bodyArrayFingerprint, bodyFingerprint, StringComparison.Ordinal))
        {
            _bodyArrayFingerprint = bodyFingerprint;
            _island = DiscoverIsland(chassis, chassisPosition, chassisBasis) ?? _island;
            if (now - _lastIslandChangeLog >= TimeSpan.FromSeconds(2))
            {
                Log("Simulation-island membership changed; preserving the truck epoch and validating each body independently.");
                _lastIslandChangeLog = now;
            }
        }

        var bodyStates = ReadBodyStates(bodyPointers, chassis, chassisPosition, chassisBasis, chassisOmega);
        var wheelBodies = ClassifyWheelBodies(bodyStates, chassisBasis);
        var capturePhase = ActiveCapture(now);
        UpdateBodyCandidates(now, bodyStates, wheelBodies);
        UpdateWheelTrackers(now, wheelBodies, chassis, chassisBasis, chassisOmega, chassisLongitudinal, capturePhase);
        ApplyAutomaticContrast(chassisLongitudinal);
        ApplySteeringEvidence(capturePhase);

        if (vehicle != 0)
        {
            StartActionSearch(vehicle, cancellationToken);
            SampleDrivetrainRuntimeHypotheses(now, vehicle);
            _wheelModels ??= DiscoverWheelModels(vehicle, _wheelTrackers.Count);
            if (_wheelModels is not null)
            {
                var currentModels = ReadObjectPointers(_wheelModels.Begin, _wheelModels.Count);
                if (!currentModels.SequenceEqual(_wheelModels.Objects))
                {
                    BeginEpoch(chassis, vehicle, "wheel-model vector changed");
                    return;
                }

                SampleWheelModelScalars(now, _wheelModels, chassisLongitudinal);
            }
        }

        if (!resolver.TryResolveChassis(out var postChassis, out _, out _) || postChassis != chassis)
        {
            BeginEpoch(postChassis, 0, "root changed during sample; discarded batch");
            return;
        }

        var wheels = _wheelTrackers.Values
            .Where(tracker => tracker.IsPromotedPhysical && tracker.IsRecentlySeen(now))
            .OrderBy(tracker => tracker.Ordinal)
            .Select(tracker => tracker.Snapshot(now))
            .ToArray();
        _status = wheels.Length == 0
            ? "Physics bodies are live; collecting geometry and spin evidence."
            : $"Tracking {wheels.Length} wheel-shaped physical bod{(wheels.Length == 1 ? "y" : "ies")} at {_measuredRate:0.0} Hz.";
        PublishSnapshot(now, memory, new RootState(
            resolver.SnowFlyerStatic, truckControl, vehicle, chassis, chassisPath, vehiclePath, _epoch),
            chassisLongitudinal, chassisVelocity.Length(), wheels);
    }

    private void Attach()
    {
        if (!ProcessMemoryReader.TryAttach(out var memory, out var message) || memory is null)
        {
            _status = message;
            return;
        }

        _memory = memory;
        Log(message);
        _resolver = new SnowRunnerRootResolver(memory, Log);
        _status = "Attached read-only; resolving the active chassis.";
        _progress = new DiscoveryProgress("Attach", "Module and SnowFlyer signature read complete.", 0, 1, memory.ModuleSize, memory.ModuleSize, 0, 0, 0);
    }

    private void ResetProcess()
    {
        _rootSearchCancellation?.Cancel();
        _rootSearchCancellation?.Dispose();
        _rootSearchCancellation = null;
        _rootSearchTask = null;
        _actionSearchCancellation?.Cancel();
        _actionSearchCancellation?.Dispose();
        _actionSearchCancellation = null;
        _actionSearchTask = null;
        _memory?.Dispose();
        _memory = null;
        _resolver = null;
        _epochChassis = 0;
        _epochVehicle = 0;
        _epoch = 0;
        ClearEpochData();
    }

    private void BeginEpoch(nint chassis, nint vehicle, string reason)
    {
        _rootSearchCancellation?.Cancel();
        _actionSearchCancellation?.Cancel();
        _actionSearchTask = null;
        _lastRootSearchStart = DateTimeOffset.MinValue;
        _epoch++;
        _epochChassis = chassis;
        _epochVehicle = vehicle;
        ClearEpochData();
        CancelCapture();
        Log($"Vehicle epoch {_epoch}: {reason}; all prior wheel and candidate pointers were discarded.");
    }

    private void InvalidateEpoch(string reason)
    {
        if (_epochChassis != 0)
        {
            BeginEpoch(0, 0, reason);
        }
    }

    private void ClearEpochData()
    {
        _island = null;
        _wheelModels = null;
        _candidateTrackers.Clear();
        _wheelTrackers.Clear();
        _quaternionHistory.Clear();
        _candidateSnapshotCache = Array.Empty<CandidateSnapshot>();
        _candidateOrdinal = 0;
        _wheelOrdinal = 0;
        _bodyArrayFingerprint = "";
        _lastActionHypothesis = 0;
    }

    private void StartActionSearch(nint vehicle, CancellationToken outerToken)
    {
        if (_memory is null || _actionSearchTask is { IsCompleted: false })
        {
            return;
        }

        if (_actionSearchTask is { IsCompletedSuccessfully: true } completed &&
            completed.Result?.Vehicle == vehicle)
        {
            return;
        }

        _actionSearchCancellation?.Dispose();
        _actionSearchCancellation = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        var memory = _memory;
        _actionSearchTask = Task.Run(
            () => SearchActionReferences(memory, vehicle, _actionSearchCancellation.Token),
            _actionSearchCancellation.Token);
    }

    private void StartRootSearch(nint chassis, CancellationToken outerToken)
    {
        if (_rootSearchTask is { IsCompleted: false } || _resolver is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastRootSearchStart < TimeSpan.FromSeconds(30))
        {
            return;
        }

        if (_rootSearchTask is { IsFaulted: true })
        {
            Log($"Vehicle-root discovery ended safely: {_rootSearchTask.Exception?.GetBaseException().Message}");
        }

        _rootSearchCancellation?.Dispose();
        _rootSearchCancellation = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        _lastRootSearchStart = now;
        var resolver = _resolver;
        var progress = new Progress<DiscoveryProgress>(value => _progress = value);
        _rootSearchTask = Task.Run(
            () => resolver.SearchTruckControl(chassis, progress, _rootSearchCancellation.Token),
            _rootSearchCancellation.Token);
    }

    private IslandDescriptor? DiscoverIsland(nint chassis, Vector3 chassisPosition, BasisState chassisBasis)
    {
        var stopwatch = Stopwatch.StartNew();
        var hypotheses = new List<IslandDescriptor>();
        TryAdd(0x128, 0x60, "chassis +0x128 -> hkpSimulationIsland +0x60 -> body array", 50);

        for (var pointerOffset = 0x80; pointerOffset <= 0x220; pointerOffset += 8)
        {
            if (pointerOffset == 0x128)
            {
                continue;
            }

            for (var arrayOffset = 0x20; arrayOffset <= 0x100; arrayOffset += 8)
            {
                TryAdd(pointerOffset, arrayOffset, $"chassis +0x{pointerOffset:X} -> object +0x{arrayOffset:X} -> body array", 0);
            }
        }

        var best = hypotheses.OrderByDescending(value => value.Score).FirstOrDefault();
        if (best is not null)
        {
            Log($"Physics island validated: {best.Path}; {best.Count} bodies; score {best.Score:0}.");
            _progress = new DiscoveryProgress("Physics graph", best.Path, 0, hypotheses.Count, 0, hypotheses.Count, hypotheses.Count, best.Count, stopwatch.Elapsed.TotalSeconds);
        }

        return best;

        void TryAdd(int pointerOffset, int arrayOffset, string path, double prior)
        {
            if (_memory is null || !_memory.TryReadPointer(chassis + pointerOffset, out var island))
            {
                return;
            }

            if (!TryRecognizePointerArray(island + arrayOffset, maximumCount: 160, out var array, out var count, out var encoding))
            {
                return;
            }

            var descriptor = new IslandDescriptor(island, array, count, pointerOffset, arrayOffset, $"{path} ({encoding})", prior);
            if (!TryReadBodyPointers(descriptor, out var pointers) || !pointers.Contains(chassis))
            {
                return;
            }

            var shapeCount = 0;
            var pairedPositions = 0;
            var positions = new List<Vector3>();
            foreach (var pointer in pointers.Take(80))
            {
                if (_resolver?.TryReadBasis(pointer, out var basis) == true && basis.Score >= 65)
                {
                    shapeCount++;
                    if (TryReadBodyPosition(pointer, chassisPosition, out var position) && Vector3.Distance(position, chassisPosition) < 20)
                    {
                        var delta = position - chassisPosition;
                        positions.Add(new Vector3(
                            Vector3.Dot(delta, chassisBasis.Right),
                            Vector3.Dot(delta, chassisBasis.Up),
                            Vector3.Dot(delta, chassisBasis.Forward)));
                    }
                }
            }

            foreach (var position in positions)
            {
                if (positions.Any(other => position.X * other.X < 0 && Math.Abs(position.Z - other.Z) < 0.8 && Math.Abs(position.Y - other.Y) < 0.8))
                {
                    pairedPositions++;
                }
            }

            if (shapeCount < 2)
            {
                return;
            }

            descriptor = descriptor with { Score = prior + Math.Min(30, shapeCount * 2) + Math.Min(20, pairedPositions * 2) };
            hypotheses.Add(descriptor);
        }
    }

    private bool TryReadBodyPointers(IslandDescriptor descriptor, out IReadOnlyList<nint> pointers)
    {
        pointers = Array.Empty<nint>();
        if (_memory is null || descriptor.Count is < 1 or > 160 || !_memory.ValidateRange(descriptor.Array, descriptor.Count * 8))
        {
            return false;
        }

        var output = new List<nint>(descriptor.Count);
        for (var index = 0; index < descriptor.Count; index++)
        {
            if (_memory.TryReadPointer(descriptor.Array + index * 8, out var pointer) && _memory.ValidateRange(pointer, 0x250))
            {
                output.Add(pointer);
            }
        }

        pointers = output;
        return output.Count >= 1;
    }

    private bool TryRecognizePointerArray(nint descriptorAddress, int maximumCount, out nint begin, out int count, out string encoding)
    {
        begin = 0;
        count = 0;
        encoding = "";
        if (_memory is null || !_memory.TryReadPointer(descriptorAddress, out begin))
        {
            return false;
        }

        if (_memory.TryRead<int>(descriptorAddress + 8, out var integerCount) &&
            integerCount is >= 1 && integerCount <= maximumCount && _memory.ValidateRange(begin, integerCount * 8))
        {
            count = integerCount;
            encoding = "pointer + int count";
            return true;
        }

        if (_memory.TryReadPointer(descriptorAddress + 8, out var end))
        {
            var bytes = (long)end - (long)begin;
            if (bytes >= 8 && bytes % 8 == 0 && bytes / 8 <= maximumCount && _memory.ValidateRange(begin, (int)bytes))
            {
                count = (int)(bytes / 8);
                encoding = "begin/end vector";
                return true;
            }
        }

        return false;
    }

    private IReadOnlyList<BodyState> ReadBodyStates(
        IReadOnlyList<nint> pointers,
        nint chassis,
        Vector3 chassisPosition,
        BasisState chassisBasis,
        Vector3 chassisOmega)
    {
        var output = new List<BodyState>();
        for (var index = 0; index < pointers.Count; index++)
        {
            var pointer = pointers[index];
            if (_resolver?.TryReadBasis(pointer, out var basis) != true || basis.Score < 55 ||
                !TryReadBodyPosition(pointer, chassisPosition, out var position) ||
                _memory?.TryRead<Vector3>(pointer + 0x230, out var velocity) != true || !FinderMath.IsFinite(velocity) ||
                _memory.TryRead<Vector3>(pointer + 0x240, out var omega) != true || !FinderMath.IsFinite(omega))
            {
                continue;
            }

            var delta = position - chassisPosition;
            var local = new Vector3(
                Vector3.Dot(delta, chassisBasis.Right),
                Vector3.Dot(delta, chassisBasis.Up),
                Vector3.Dot(delta, chassisBasis.Forward));
            _resolver.TryReadQuaternion(pointer, out var quaternion, out var quaternionOffset);
            output.Add(new BodyState(index, pointer, pointer == chassis, basis, position, local, velocity, omega, chassisOmega, quaternion, quaternionOffset));
        }

        return output;
    }

    private IReadOnlyList<BodyState> ClassifyWheelBodies(IReadOnlyList<BodyState> bodies, BasisState chassisBasis)
    {
        var hypotheses = bodies.Where(body => !body.IsChassis && body.Basis.Score >= 60).ToArray();
        var geometry = hypotheses
            .Where(body => !body.IsChassis && body.Basis.Score >= 60 &&
                           Math.Abs(body.LocalPosition.X) is >= 0.35f and <= 6f &&
                           Math.Abs(body.LocalPosition.Y) <= 5f &&
                           Math.Abs(body.LocalPosition.Z) <= 12f)
            .ToArray();
        var paired = geometry.Where(body => geometry.Any(other =>
            other.Address != body.Address && body.LocalPosition.X * other.LocalPosition.X < 0 &&
            Math.Abs(body.LocalPosition.Z - other.LocalPosition.Z) < 0.85f &&
            Math.Abs(body.LocalPosition.Y - other.LocalPosition.Y) < 0.85f &&
            Math.Abs(Math.Abs(body.LocalPosition.X) - Math.Abs(other.LocalPosition.X)) < 1.2f)).ToArray();
        var lowestPairedHeight = paired.Length == 0 ? 0 : paired.Min(body => body.LocalPosition.Y);
        var layoutWheels = paired
            .Where(body => body.LocalPosition.Y <= lowestPairedHeight + 0.45f)
            .ToArray();

        foreach (var body in hypotheses)
        {
            if (!_wheelTrackers.TryGetValue(body.Address, out var tracker))
            {
                tracker = new WheelTracker(++_wheelOrdinal, body.IslandIndex, body.Address,
                    $"{_island?.Path} -> [{body.IslandIndex}] hkpRigidBody");
                _wheelTrackers.Add(body.Address, tracker);
            }

            tracker.IslandIndex = body.IslandIndex;
            tracker.PointerPath = $"{_island?.Path} -> [{body.IslandIndex}] hkpRigidBody";
            tracker.LocalPosition = body.LocalPosition;
            tracker.Side = "Unknown";
            tracker.Axle = "Unknown";
            tracker.StructuralScore = 12 + Math.Min(8, body.Basis.Score / 15);
        }

        var axleCenters = new List<double>();
        foreach (var body in layoutWheels.OrderByDescending(value => value.LocalPosition.Z))
        {
            var nearest = axleCenters
                .Select((value, index) => new { Value = value, Index = index, Distance = Math.Abs(value - body.LocalPosition.Z) })
                .OrderBy(value => value.Distance)
                .FirstOrDefault();
            int axleIndex;
            if (nearest is null || nearest.Distance > 0.75)
            {
                axleCenters.Add(body.LocalPosition.Z);
                axleIndex = axleCenters.Count - 1;
            }
            else
            {
                axleIndex = nearest.Index;
            }

            var tracker = _wheelTrackers[body.Address];
            tracker.Side = body.LocalPosition.X >= 0 ? "Right" : "Left";
            tracker.Axle = $"Axle {axleIndex + 1}";
            tracker.StructuralScore = 45 + Math.Min(10, body.Basis.Score / 10);
        }

        return hypotheses;
    }

    private void UpdateBodyCandidates(DateTimeOffset now, IReadOnlyList<BodyState> bodies, IReadOnlyList<BodyState> wheelBodies)
    {
        foreach (var body in bodies.Where(body => !body.IsChassis))
        {
            var axle = SelectAxleAxis(body.Basis, _resolver is not null && _epochChassis != 0 && _resolver.TryReadBasis(_epochChassis, out var chassisBasis)
                ? chassisBasis.Right
                : Vector3.UnitX);
            var projection = Vector3.Dot(body.AngularVelocity - body.ChassisAngularVelocity, axle);
            var path = $"{_island?.Path} -> [{body.IslandIndex}] +0x240 angular velocity";
            var isWheel = _wheelTrackers.TryGetValue(body.Address, out var wheelTracker) && wheelTracker.StructuralScore >= 25;
            var bodyKey = ((ulong)(long)body.Address).ToString("X16", CultureInfo.InvariantCulture);
            GetCandidate($"B{bodyKey}:P", "Havok axial projection", body.IslandIndex, body.Address + 0x240, path)
                .Update(now, projection, isWheel ? 45 : 18, isWheel ? "Wheel-shaped paired body" : "Island-body hypothesis",
                    $"world omega ({body.AngularVelocity.X:0.000}, {body.AngularVelocity.Y:0.000}, {body.AngularVelocity.Z:0.000}); chassis-subtracted projection");
            var components = new[] { body.AngularVelocity.X, body.AngularVelocity.Y, body.AngularVelocity.Z };
            for (var component = 0; component < 3; component++)
            {
                GetCandidate($"B{bodyKey}:C{component}", $"Havok omega component {"XYZ"[component]}", body.IslandIndex,
                        body.Address + 0x240 + component * 4, path + $" component {"XYZ"[component]}")
                    .Update(now, components[component], isWheel ? 30 : 12, isWheel ? "Wheel-body component" : "Island-body component", "Raw world-space float32");
            }
        }
    }

    private void UpdateWheelTrackers(
        DateTimeOffset now,
        IReadOnlyList<BodyState> wheelBodies,
        nint chassis,
        BasisState chassisBasis,
        Vector3 chassisOmega,
        double chassisLongitudinal,
        CapturePhase capturePhase)
    {
        var chassisQuaternionRate = Vector3.Zero;
        if (_resolver?.TryReadQuaternion(chassis, out var chassisQuaternion, out _) == true)
        {
            chassisQuaternionRate = UpdateQuaternionHistory(chassis, chassisQuaternion, now);
        }

        foreach (var body in wheelBodies)
        {
            if (!_wheelTrackers.TryGetValue(body.Address, out var tracker))
            {
                continue;
            }

            var axleAxis = SelectAxleAxis(body.Basis, chassisBasis.Right);
            var rawSigned = Vector3.Dot(body.AngularVelocity, axleAxis);
            var relative = Vector3.Dot(body.AngularVelocity - chassisOmega, axleAxis);
            var bodyLongitudinal = Vector3.Dot(body.LinearVelocity, chassisBasis.Forward);
            double? quaternionSpin = null;
            if (body.QuaternionOffset != 0)
            {
                var bodyRate = UpdateQuaternionHistory(body.Address, body.Quaternion, now);
                quaternionSpin = Vector3.Dot(bodyRate - chassisQuaternionRate, axleAxis);
            }

            tracker.Update(now, rawSigned, relative, chassisLongitudinal, bodyLongitudinal, quaternionSpin, capturePhase);
        }
    }

    private Vector3 UpdateQuaternionHistory(nint body, Quaternion quaternion, DateTimeOffset now)
    {
        var rate = Vector3.Zero;
        if (_quaternionHistory.TryGetValue(body, out var history))
        {
            rate = FinderMath.QuaternionAngularVelocity(history.Quaternion, quaternion, (now - history.Timestamp).TotalSeconds);
        }

        _quaternionHistory[body] = new QuaternionHistory(quaternion, now);
        return rate;
    }

    private void ApplySteeringEvidence(CapturePhase capturePhase)
    {
        if (capturePhase != CapturePhase.Steering)
        {
            return;
        }

        foreach (var axle in _wheelTrackers.Values.GroupBy(tracker => tracker.Axle))
        {
            var left = axle.Where(tracker => tracker.Side == "Left").ToArray();
            var right = axle.Where(tracker => tracker.Side == "Right").ToArray();
            if (left.Length == 0 || right.Length == 0)
            {
                continue;
            }

            var leftMean = left.Average(tracker => Math.Abs(tracker.CalibratedSpin));
            var rightMean = right.Average(tracker => Math.Abs(tracker.CalibratedSpin));
            if (Math.Max(leftMean, rightMean) > 1 && Math.Abs(leftMean - rightMean) / Math.Max(leftMean, rightMean) > 0.02)
            {
                foreach (var tracker in axle)
                {
                    tracker.MarkSteeringPassed();
                }
            }
        }
    }

    private void ApplyAutomaticContrast(double chassisLongitudinal)
    {
        var now = DateTimeOffset.UtcNow;
        var physical = _wheelTrackers.Values.Where(tracker => tracker.IsPromotedPhysical && tracker.IsRecentlySeen(now)).ToArray();
        if (physical.Length < 2 || Math.Abs(chassisLongitudinal) >= 0.2)
        {
            foreach (var tracker in physical)
            {
                tracker.MarkAutomaticContrast(spinning: false, blocked: false);
            }

            return;
        }

        var spinning = physical.Where(tracker =>
            Math.Abs(tracker.CalibratedSpin) >= 1.5 && Math.Abs(tracker.TreadSpeed ?? 0) >= 0.75).ToHashSet();
        var blocked = physical.Where(tracker => Math.Abs(tracker.CalibratedSpin) <= 0.25).ToHashSet();
        var hasContrast = spinning.Count > 0 && blocked.Count > 0;
        foreach (var tracker in physical)
        {
            tracker.MarkAutomaticContrast(
                spinning: hasContrast && spinning.Contains(tracker),
                blocked: hasContrast && blocked.Contains(tracker));
        }
    }

    private WheelModelDescriptor? DiscoverWheelModels(nint vehicle, int expectedWheelCount)
    {
        if (_memory is null)
        {
            return null;
        }

        var candidates = new List<WheelModelDescriptor>();
        for (var offset = 0; offset <= 0x1000 - 24; offset += 8)
        {
            if (!TryRecognizeMsvcVector(vehicle + offset, out var begin, out var count, out var capacity) || count is < 2 or > 64)
            {
                continue;
            }

            var pointers = ReadObjectPointers(begin, count);
            if (pointers.Count != count)
            {
                continue;
            }

            var vtables = pointers.Select(pointer => _memory.TryReadPointer(pointer, out var vtable) ? vtable : 0).Where(value => value != 0).ToArray();
            var homogeneous = vtables.Length == count && vtables.GroupBy(value => value).Max(group => group.Count()) >= Math.Max(2, count * 3 / 4);
            var score = (offset == 0x1F8 ? 45 : 0) + (homogeneous ? 30 : 0) +
                        (expectedWheelCount > 0 && count == expectedWheelCount ? 20 : 0) + (count % 2 == 0 ? 5 : 0);
            if (score >= 30)
            {
                candidates.Add(new WheelModelDescriptor(vehicle, offset, begin, count, capacity,
                    $"vehicle +0x{offset:X} begin/end/capacity -> TRUCK_WHEEL_MODEL hypotheses", score, pointers));
            }
        }

        var best = candidates.OrderByDescending(value => value.Score).FirstOrDefault();
        if (best is not null)
        {
            Log($"Wheel-model vector hypothesis: {best.Path}; {best.Count} objects; score {best.Score:0}.");
        }

        return best;
    }

    private void SampleWheelModelScalars(DateTimeOffset now, WheelModelDescriptor descriptor, double chassisLongitudinal)
    {
        if (_memory is null)
        {
            return;
        }

        for (var index = 0; index < descriptor.Objects.Count; index++)
        {
            var owner = descriptor.Objects[index];
            var block = _memory.TryReadBlock(owner, 0x400);
            if (block is null)
            {
                continue;
            }

            for (var offset = 0; offset + 4 <= block.Length; offset += 4)
            {
                var value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(offset, 4)));
                if (!FinderMath.IsFinite(value) || Math.Abs(value) > 10_000)
                {
                    continue;
                }

                var plausibleRolling = Math.Abs(chassisLongitudinal) > 0.5 && Math.Abs(value) > 0.5 &&
                                       Math.Abs(chassisLongitudinal / value) is >= 0.15 and <= 1.8;
                var score = 12 + (plausibleRolling ? 18 : 0);
                GetCandidate($"M{index}:F{offset:X}", "Wheel-model scalar family", index, owner + offset,
                        $"{descriptor.Path} -> [{index}] {FinderMath.Address(owner)} +0x{offset:X}")
                    .Update(now, value, score, plausibleRolling ? "Plausible rolling-scale scalar" : "Unvalidated wheel-model scalar",
                        "float32; retained for temporal reverse/wheelspin ranking; not used as RPM without proof");
            }
        }
    }

    private void SampleDrivetrainRuntimeHypotheses(DateTimeOffset now, nint vehicle)
    {
        if (_memory is null)
        {
            return;
        }

        nint action;
        string actionPath;
        if (!TryResolveTruckAction(vehicle, out action, out actionPath))
        {
            if (_actionSearchTask is not { IsCompletedSuccessfully: true } completed ||
                completed.Result is not { } result || result.Vehicle != vehicle ||
                !_memory.ValidateRange(result.Action, 0xE8))
            {
                return;
            }

            action = result.Action;
            actionPath = $"reverse vehicle reference: action +0x{result.BackOffset:X} -> vehicle; score {result.Score}";
        }

        if (_lastActionHypothesis != action)
        {
            _lastActionHypothesis = action;
            Log($"Truck-action hypothesis resolved: {actionPath} -> {FinderMath.Address(action)}.");
        }

        SampleActionByte(now, action, actionPath, 0x48, "Handbrake", "Expected 0/1 handbrake state");
        SampleActionByte(now, action, actionPath, 0x49, "AWD", "Expected 0/1 AWD state");
        SampleActionByte(now, action, actionPath, 0x4A, "DiffLock", "Expected 0/1 differential-lock state");
        SampleActionInt(now, action, actionPath, 0x70, "GearA", "Current-gear integer hypothesis A");
        SampleActionInt(now, action, actionPath, 0x74, "GearB", "Current-gear integer hypothesis B");
        SampleActionFloat(now, action, actionPath, 0x38, "PowerCoef", "Power coefficient hypothesis");
        SampleActionFloat(now, action, actionPath, 0x44, "Accelerator", "Accelerator input hypothesis");
    }

    private static ActionSearchResult? SearchActionReferences(
        ProcessMemoryReader memory,
        nint vehicle,
        CancellationToken cancellationToken)
    {
        const int chunkSize = 1024 * 1024;
        const long maximumBytes = 768L * 1024 * 1024;
        long bytesRead = 0;
        ActionSearchResult? best = null;
        var target = unchecked((ulong)(long)vehicle);
        var moduleStart = (long)memory.ModuleBase;
        var moduleEnd = moduleStart + memory.ModuleSize;

        foreach (var region in memory.EnumerateReadableRegions())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytesRead >= maximumBytes)
            {
                break;
            }

            if (region.Type is not (NativeMethods.MemPrivate or NativeMethods.MemMapped) ||
                region.Size < 0x1000 || region.Size > 128L * 1024 * 1024)
            {
                continue;
            }

            for (long regionOffset = 0; regionOffset < region.Size && bytesRead < maximumBytes; regionOffset += chunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(chunkSize, region.Size - regionOffset);
                var block = memory.TryReadBlock(region.BaseAddress + (nint)regionOffset, count);
                if (block is null)
                {
                    continue;
                }

                bytesRead += block.Length;
                for (var offset = 0; offset + 8 <= block.Length; offset += 8)
                {
                    if (BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(offset, 8)) != target)
                    {
                        continue;
                    }

                    var reference = region.BaseAddress + (nint)regionOffset + offset;
                    for (var backOffset = 0; backOffset <= 0x100; backOffset += 8)
                    {
                        var candidate = reference - backOffset;
                        if (!memory.ValidateRange(candidate, 0xE8))
                        {
                            continue;
                        }

                        var score = backOffset == 0x30 ? 28 : 0;
                        if (memory.TryReadPointer(candidate, out var vtable) &&
                            (long)vtable >= moduleStart && (long)vtable < moduleEnd)
                        {
                            score += 35;
                        }

                        if (memory.TryRead<byte>(candidate + 0x48, out var handbrake) && handbrake <= 1) score += 5;
                        if (memory.TryRead<byte>(candidate + 0x49, out var awd) && awd <= 1) score += 8;
                        if (memory.TryRead<byte>(candidate + 0x4A, out var diff) && diff <= 1) score += 5;
                        if (memory.TryRead<int>(candidate + 0x70, out var gearA) && gearA is >= -20 and <= 20) score += 8;
                        if (memory.TryRead<int>(candidate + 0x74, out var gearB) && gearB is >= -20 and <= 20) score += 8;
                        if (memory.TryRead<float>(candidate + 0x44, out var accelerator) &&
                            FinderMath.IsFinite(accelerator) && Math.Abs(accelerator) <= 10) score += 5;

                        if (best is null || score > best.Score)
                        {
                            best = new ActionSearchResult(vehicle, candidate, backOffset, score);
                        }
                    }
                }
            }
        }

        return best is { Score: >= 55 } ? best : null;
    }

    private bool TryResolveTruckAction(nint vehicle, out nint action, out string path)
    {
        action = 0;
        path = "not resolved";
        if (_memory is null)
        {
            return false;
        }

        var candidates = new List<(nint Action, int VehicleOffset, int BackOffset, int Score)>();
        for (var vehicleOffset = 0; vehicleOffset <= 0x300; vehicleOffset += 8)
        {
            if (!_memory.TryReadPointer(vehicle + vehicleOffset, out var candidate) ||
                !_memory.ValidateRange(candidate, 0xE8))
            {
                continue;
            }

            for (var backOffset = 0; backOffset <= 0x80; backOffset += 8)
            {
                if (!_memory.TryReadPointer(candidate + backOffset, out var backPointer) || backPointer != vehicle)
                {
                    continue;
                }

                var score = (vehicleOffset == 0x80 ? 20 : 0) + (backOffset == 0x30 ? 25 : 0);
                if (_memory.TryRead<byte>(candidate + 0x48, out var handbrake) && handbrake <= 1) score += 8;
                if (_memory.TryRead<byte>(candidate + 0x49, out var awd) && awd <= 1) score += 12;
                if (_memory.TryRead<byte>(candidate + 0x4A, out var diff) && diff <= 1) score += 8;
                if (_memory.TryRead<int>(candidate + 0x70, out var gearA) && gearA is >= -20 and <= 20) score += 10;
                if (_memory.TryRead<int>(candidate + 0x74, out var gearB) && gearB is >= -20 and <= 20) score += 10;
                candidates.Add((candidate, vehicleOffset, backOffset, score));
            }
        }

        var best = candidates.OrderByDescending(value => value.Score).FirstOrDefault();
        if (best.Action == 0 || best.Score < 30)
        {
            return false;
        }

        action = best.Action;
        path = $"vehicle +0x{best.VehicleOffset:X} -> action; action +0x{best.BackOffset:X} -> same vehicle; score {best.Score}";
        return true;
    }

    private void SampleActionByte(DateTimeOffset now, nint action, string actionPath, int offset, string name, string detail)
    {
        if (_memory?.TryRead<byte>(action + offset, out var value) != true || value > 1)
        {
            return;
        }

        GetCandidate($"DRIVE:{name}", "Truck-action runtime byte hypothesis", 0, action + offset,
                $"{actionPath}; action +0x{offset:X} ({name})")
            .Update(now, value, 45, "Bounded 0/1 runtime state", $"{detail}; read-only mapping hypothesis");
    }

    private void SampleActionInt(DateTimeOffset now, nint action, string actionPath, int offset, string name, string detail)
    {
        if (_memory?.TryRead<int>(action + offset, out var value) != true || value is < -20 or > 20)
        {
            return;
        }

        GetCandidate($"DRIVE:{name}", "Truck-action gear integer hypothesis", 0, action + offset,
                $"{actionPath}; action +0x{offset:X} ({name})")
            .Update(now, value, 40, "Bounded gear-like integer", $"{detail}; validate by selecting Reverse/1/2");
    }

    private void SampleActionFloat(DateTimeOffset now, nint action, string actionPath, int offset, string name, string detail)
    {
        if (_memory?.TryRead<float>(action + offset, out var value) != true ||
            !FinderMath.IsFinite(value) || Math.Abs(value) > 10)
        {
            return;
        }

        GetCandidate($"DRIVE:{name}", "Truck-action float hypothesis", 0, action + offset,
                $"{actionPath}; action +0x{offset:X} ({name})")
            .Update(now, value, 32, "Bounded control-like float", $"{detail}; validate by throttle changes");
    }

    private bool TryRecognizeMsvcVector(nint address, out nint begin, out int count, out int capacity)
    {
        begin = 0;
        count = 0;
        capacity = 0;
        if (_memory is null || !_memory.TryReadPointer(address, out begin) ||
            !_memory.TryReadPointer(address + 8, out var end) || !_memory.TryReadPointer(address + 16, out var cap))
        {
            return false;
        }

        var usedBytes = (long)end - (long)begin;
        var capacityBytes = (long)cap - (long)begin;
        if (usedBytes < 16 || capacityBytes < usedBytes || usedBytes % 8 != 0 || capacityBytes % 8 != 0 || capacityBytes / 8 > 128)
        {
            return false;
        }

        count = (int)(usedBytes / 8);
        capacity = (int)(capacityBytes / 8);
        return _memory.ValidateRange(begin, count * 8);
    }

    private IReadOnlyList<nint> ReadObjectPointers(nint begin, int count)
    {
        var output = new List<nint>(count);
        if (_memory is null)
        {
            return output;
        }

        for (var index = 0; index < count; index++)
        {
            if (!_memory.TryReadPointer(begin + index * 8, out var pointer) || !_memory.ValidateRange(pointer, 0x400))
            {
                return Array.Empty<nint>();
            }

            output.Add(pointer);
        }

        return output;
    }

    private CandidateTracker GetCandidate(string id, string kind, int owner, nint address, string path)
    {
        if (!_candidateTrackers.TryGetValue(id, out var tracker))
        {
            tracker = new CandidateTracker(id, ++_candidateOrdinal, kind, owner, address, path);
            _candidateTrackers.Add(id, tracker);
        }

        return tracker;
    }

    private CapturePhase ActiveCapture(DateTimeOffset now)
    {
        lock (_captureGate)
        {
            if (_capturePhase != CapturePhase.None && now >= _captureEnd)
            {
                var finished = _capturePhase;
                _capturePhase = CapturePhase.None;
                _captureRecordingStarted = false;
                _captureStart = DateTimeOffset.MinValue;
                _captureEnd = DateTimeOffset.MinValue;
                Log($"Guided proof finished: {CaptureName(finished)} ({CaptureRecordingSeconds} recorded seconds). Results are retained for vehicle epoch {_epoch}.");
                return CapturePhase.None;
            }

            if (_capturePhase == CapturePhase.None || now < _captureStart)
            {
                return CapturePhase.None;
            }

            if (!_captureRecordingStarted)
            {
                foreach (var tracker in _wheelTrackers.Values)
                {
                    tracker.ResetPhase(_capturePhase);
                }

                _captureRecordingStarted = true;
                Log($"Guided proof recording started: {CaptureName(_capturePhase)}; prior evidence for this phase was replaced.");
            }

            return _capturePhase;
        }
    }

    private CaptureStatus CaptureSnapshot(DateTimeOffset now)
    {
        lock (_captureGate)
        {
            var active = _capturePhase != CapturePhase.None && now < _captureEnd;
            var recording = active && now >= _captureStart;
            var remaining = recording
                ? (_captureEnd - now).TotalSeconds
                : (_captureStart - now).TotalSeconds;
            return new CaptureStatus(
                _capturePhase,
                active,
                recording,
                active ? Math.Max(0, remaining) : 0,
                active
                    ? recording
                        ? $"RECORDING {CaptureName(_capturePhase)}"
                        : $"GET READY for {CaptureName(_capturePhase)}; recording starts in"
                    : "Choose a guided proof when the truck is ready.");
        }
    }

    private void PublishSnapshot(
        DateTimeOffset now,
        ProcessMemoryReader memory,
        RootState root,
        double chassisLongitudinal,
        double chassisSpeed,
        IReadOnlyList<WheelSnapshot> wheels)
    {
        if (now - _lastCandidateSnapshot >= TimeSpan.FromMilliseconds(400))
        {
            _candidateSnapshotCache = _candidateTrackers.Values.OrderBy(value => value.Ordinal).Select(value => value.Snapshot(now)).ToArray();
            _lastCandidateSnapshot = now;
        }

        var (drivelineRad, drivelineRpm, assumption) = CalculateDriveline(wheels);
        var captureEvidenceBodies = _wheelTrackers.Values
            .Where(tracker => !tracker.IsPromotedPhysical && tracker.HasCaptureEvidence && tracker.IsRecentlySeen(now))
            .OrderBy(tracker => tracker.Ordinal)
            .Select(tracker => tracker.Snapshot(now))
            .ToArray();
        FlushLogs();
        var snapshot = new FinderSnapshot(
            now.ToLocalTime(),
            Attached: true,
            _paused,
            _status,
            "READ-ONLY: OpenProcess query/read rights + ReadProcessMemory only. No writes, hooks, injection, UDP, or game changes.",
            memory.Process.Id,
            memory.ProcessStartTimeUtc,
            memory.ModuleBase,
            memory.ModuleSize,
            memory.ModulePath,
            memory.VersionText,
            _resolver?.ModuleFingerprint ?? "",
            root,
            _island?.Island ?? 0,
            _island?.Array ?? 0,
            _island?.Count ?? 0,
            _island?.Path ?? "not resolved",
            _wheelModels?.Begin ?? 0,
            _wheelModels?.Count ?? 0,
            _wheelModels?.Path ?? "not resolved",
            chassisLongitudinal,
            chassisSpeed,
            _measuredRate,
            _physicsChangeRate,
            drivelineRad,
            drivelineRpm,
            assumption,
            CaptureSnapshot(now),
            _progress,
            wheels,
            captureEvidenceBodies,
            _candidateSnapshotCache,
            _fullLogs.ToArray());
        Volatile.Write(ref _latest, snapshot);
    }

    private void PublishDisconnected(DateTimeOffset now)
    {
        var snapshot = FinderSnapshot.Empty(_status) with
        {
            Timestamp = now.ToLocalTime(),
            Paused = _paused,
            Logs = _fullLogs.ToArray(),
            Progress = _progress
        };
        Volatile.Write(ref _latest, snapshot);
    }

    private void PublishAttachedWithoutTruck(DateTimeOffset now, ProcessMemoryReader memory)
    {
        FlushLogs();
        var snapshot = FinderSnapshot.Empty(_status) with
        {
            Timestamp = now.ToLocalTime(),
            Attached = true,
            Paused = _paused,
            ProcessId = memory.Process.Id,
            ProcessStartUtc = memory.ProcessStartTimeUtc,
            ModuleBase = memory.ModuleBase,
            ModuleSize = memory.ModuleSize,
            ModulePath = memory.ModulePath,
            GameVersion = memory.VersionText,
            ModuleFingerprint = _resolver?.ModuleFingerprint ?? "",
            Progress = _progress,
            Logs = _fullLogs.ToArray()
        };
        Volatile.Write(ref _latest, snapshot);
    }

    private void TrackRates(DateTimeOffset now, Vector3 chassisVelocity)
    {
        _rateSamples++;
        if (Vector3.DistanceSquared(chassisVelocity, _lastChassisVelocity) > 1e-8f)
        {
            _physicsChanges++;
            _lastPhysicsChange = now;
            _lastChassisVelocity = chassisVelocity;
        }

        var elapsed = (now - _rateWindowStart).TotalSeconds;
        if (elapsed >= 2)
        {
            _measuredRate = _rateSamples / elapsed;
            _physicsChangeRate = _physicsChanges / elapsed;
            _rateSamples = 0;
            _physicsChanges = 0;
            _rateWindowStart = now;
        }

        if (_lastPhysicsChange != default && now - _lastPhysicsChange > TimeSpan.FromSeconds(1) && _physicsChangeRate > 0)
        {
            _physicsChangeRate = 0;
        }
    }

    private static (double? Rad, double? Rpm, string Assumption) CalculateDriveline(IReadOnlyList<WheelSnapshot> wheels)
    {
        if (wheels.Count == 0)
        {
            return (null, null, "No wheel bodies are classified yet.");
        }

        var axleSpeeds = new List<(double Speed, bool Driven)>();
        foreach (var axle in wheels.GroupBy(wheel => wheel.Axle))
        {
            var left = axle.Where(wheel => wheel.Side == "Left").Select(wheel => wheel.CalibratedRadPerSecond).ToArray();
            var right = axle.Where(wheel => wheel.Side == "Right").Select(wheel => wheel.CalibratedRadPerSecond).ToArray();
            if (left.Length == 0 || right.Length == 0)
            {
                continue;
            }

            var carrier = (left.Average() + right.Average()) / 2.0;
            axleSpeeds.Add((carrier, axle.Any(wheel => wheel.DrivenState == "Likely active")));
        }

        if (axleSpeeds.Count == 0)
        {
            return (null, null, "No complete left/right axle pair is available.");
        }

        var active = axleSpeeds.Where(value => value.Driven).ToArray();
        var selected = active.Length > 0 ? active : axleSpeeds.ToArray();
        var rad = selected.Average(value => value.Speed);
        var assumption = active.Length > 0
            ? "Open-differential signed left/right mean; combined behaviorally active axles. Torque-state metadata is not yet proven."
            : "All-axle hypothesis: signed left/right mean per axle, then mean across axles. Powered state is unknown.";
        return (rad, rad * 60.0 / FinderMath.TwoPi, assumption);
    }

    private static Vector3 SelectAxleAxis(BasisState wheelBasis, Vector3 chassisRight)
    {
        var axes = new[] { wheelBasis.Forward, wheelBasis.Up, wheelBasis.Right };
        var best = axes.OrderByDescending(axis => Math.Abs(Vector3.Dot(axis, chassisRight))).First();
        return Vector3.Dot(best, chassisRight) < 0 ? -best : best;
    }

    private bool TryReadBodyPosition(nint body, Vector3 chassisPosition, out Vector3 position)
    {
        position = default;
        if (_memory is null)
        {
            return false;
        }

        return _memory.TryRead<Vector3>(body + 0x1A0, out position) &&
               FinderMath.IsFinite(position) && Vector3.Distance(position, chassisPosition) < 25;
    }

    private void Log(string message)
    {
        _pendingLogs.Enqueue($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {message}");
    }

    private void FlushLogs()
    {
        while (_pendingLogs.TryDequeue(out var message))
        {
            _fullLogs.Add(message);
        }
    }

    private static string CaptureName(CapturePhase phase) => phase switch
    {
        CapturePhase.Parked => "parked / near-zero",
        CapturePhase.Forward => "straight forward rolling",
        CapturePhase.Reverse => "reverse sign",
        CapturePhase.Steering => "steering left/right difference",
        CapturePhase.StationaryWheelspin => "stationary wheelspin",
        CapturePhase.PhysicallyBlocked => "physically blocked wheel",
        CapturePhase.StationaryGear1 => "stationary wheelspin — manual gear 1",
        CapturePhase.StationaryGear2 => "stationary wheelspin — manual gear 2",
        CapturePhase.StationaryGear3 => "stationary wheelspin — manual gear 3",
        CapturePhase.StationaryGear4 => "stationary wheelspin — manual gear 4",
        CapturePhase.StationaryGear5 => "stationary wheelspin — manual gear 5",
        CapturePhase.StationaryReverse => "stationary wheelspin — manual reverse",
        CapturePhase.Stationary2wdGear1 => "stationary wheelspin — 2WD manual gear 1",
        CapturePhase.Stationary2wdGear2 => "stationary wheelspin — 2WD manual gear 2",
        CapturePhase.Stationary4wdGear1 => "stationary wheelspin — 4WD manual gear 1",
        CapturePhase.Stationary4wdGear2 => "stationary wheelspin — 4WD manual gear 2",
        _ => "none"
    };

    private static void WaitForDeadline(Stopwatch scheduler, double targetTicks, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var remainingTicks = targetTicks - scheduler.ElapsedTicks;
            if (remainingTicks <= 0)
            {
                return;
            }

            var remainingMilliseconds = remainingTicks * 1000.0 / Stopwatch.Frequency;
            if (remainingMilliseconds > 1.5)
            {
                cancellationToken.WaitHandle.WaitOne(Math.Max(1, (int)(remainingMilliseconds - 0.75)));
            }
            else
            {
                Thread.SpinWait(80);
            }
        }
    }

    private sealed record IslandDescriptor(
        nint Island,
        nint Array,
        int Count,
        int ChassisPointerOffset,
        int ArrayOffset,
        string Path,
        double Score);

    private sealed record WheelModelDescriptor(
        nint Vehicle,
        int VectorOffset,
        nint Begin,
        int Count,
        int Capacity,
        string Path,
        double Score,
        IReadOnlyList<nint> Objects);

    private sealed record ActionSearchResult(
        nint Vehicle,
        nint Action,
        int BackOffset,
        int Score);

    private sealed record BodyState(
        int IslandIndex,
        nint Address,
        bool IsChassis,
        BasisState Basis,
        Vector3 Position,
        Vector3 LocalPosition,
        Vector3 LinearVelocity,
        Vector3 AngularVelocity,
        Vector3 ChassisAngularVelocity,
        Quaternion Quaternion,
        int QuaternionOffset);

    private sealed record QuaternionHistory(Quaternion Quaternion, DateTimeOffset Timestamp);
}
