using System.Numerics;

namespace SnowRunnerWheelspinFinder;

internal sealed class CandidateTracker
{
    private readonly DateTimeOffset _created;
    private DateTimeOffset _lastChange;
    private double _last;
    private bool _hasLast;

    public CandidateTracker(string id, long ordinal, string kind, int ownerIndex, nint address, string pointerPath)
    {
        Id = id;
        Ordinal = ordinal;
        Kind = kind;
        OwnerIndex = ownerIndex;
        Address = address;
        PointerPath = pointerPath;
        _created = DateTimeOffset.UtcNow;
        Minimum = double.PositiveInfinity;
        Maximum = double.NegativeInfinity;
    }

    public string Id { get; }
    public long Ordinal { get; }
    public string Kind { get; }
    public int OwnerIndex { get; }
    public nint Address { get; }
    public string PointerPath { get; }
    public double Current { get; private set; }
    public double Minimum { get; private set; }
    public double Maximum { get; private set; }
    public long Samples { get; private set; }
    public long Changes { get; private set; }
    public double Score { get; private set; }
    public string State { get; private set; } = "Collecting";
    public string Details { get; private set; } = "";
    public DateTimeOffset LastSample { get; private set; }

    public void Update(DateTimeOffset now, double value, double score, string state, string details)
    {
        if (!double.IsFinite(value))
        {
            return;
        }

        Current = value;
        Minimum = Math.Min(Minimum, value);
        Maximum = Math.Max(Maximum, value);
        Samples++;
        LastSample = now;
        Score = FinderMath.ClampScore(score);
        State = state;
        Details = details;
        var epsilon = Math.Max(1e-5, Math.Abs(value) * 1e-4);
        if (_hasLast && Math.Abs(value - _last) > epsilon)
        {
            Changes++;
            _lastChange = now;
        }

        _last = value;
        _hasLast = true;
    }

    public CandidateSnapshot Snapshot(DateTimeOffset now)
    {
        var elapsed = Math.Max(0.001, (now - _created).TotalSeconds);
        var sampleHz = Samples / elapsed;
        var changeHz = Changes / elapsed;
        var state = State;
        if (Samples > 30 && _lastChange != default && now - _lastChange > TimeSpan.FromSeconds(1) && Math.Abs(Current) > 0.5)
        {
            state += "; not changing recently";
        }

        return new CandidateSnapshot(
            Id,
            Ordinal,
            Kind,
            OwnerIndex,
            Address,
            PointerPath,
            Current,
            double.IsPositiveInfinity(Minimum) ? 0 : Minimum,
            double.IsNegativeInfinity(Maximum) ? 0 : Maximum,
            Samples,
            Changes,
            sampleHz,
            changeHz,
            Score,
            state,
            Details);
    }
}

internal sealed class PhaseAccumulator
{
    public long Samples { get; private set; }
    public long Changes { get; private set; }
    public long NearStationarySamples { get; private set; }
    public long StationarySpinSamples { get; private set; }
    public double SumSpin { get; private set; }
    public double SumAbsSpin { get; private set; }
    public double SumChassis { get; private set; }
    public double MaxAbsSpin { get; private set; }
    public double MaxAbsChassis { get; private set; }
    public double SumSpinNearStationary { get; private set; }
    public double SumAbsSpinNearStationary { get; private set; }
    public double MinimumSpin { get; private set; } = double.PositiveInfinity;
    public double MaximumSpin { get; private set; } = double.NegativeInfinity;
    private double _lastSpin;
    private bool _hasLast;

    public double MeanSpin => Samples == 0 ? 0 : SumSpin / Samples;
    public double MeanAbsSpin => Samples == 0 ? 0 : SumAbsSpin / Samples;
    public double MeanChassis => Samples == 0 ? 0 : SumChassis / Samples;
    public double MeanSpinNearStationary => NearStationarySamples == 0 ? 0 : SumSpinNearStationary / NearStationarySamples;
    public double MeanAbsSpinNearStationary => NearStationarySamples == 0 ? 0 : SumAbsSpinNearStationary / NearStationarySamples;

    public void Add(double rawSpin, double calibratedSpin, double chassisSpeed, double? treadSpeed)
    {
        Samples++;
        SumSpin += calibratedSpin;
        SumAbsSpin += Math.Abs(calibratedSpin);
        SumChassis += chassisSpeed;
        MaxAbsSpin = Math.Max(MaxAbsSpin, Math.Abs(calibratedSpin));
        MaxAbsChassis = Math.Max(MaxAbsChassis, Math.Abs(chassisSpeed));
        MinimumSpin = Math.Min(MinimumSpin, calibratedSpin);
        MaximumSpin = Math.Max(MaximumSpin, calibratedSpin);
        if (Math.Abs(chassisSpeed) < 0.2)
        {
            NearStationarySamples++;
            SumSpinNearStationary += calibratedSpin;
            SumAbsSpinNearStationary += Math.Abs(calibratedSpin);
            if (Math.Abs(treadSpeed ?? calibratedSpin) > 1.0)
            {
                StationarySpinSamples++;
            }
        }

        if (_hasLast && Math.Abs(rawSpin - _lastSpin) > Math.Max(1e-4, Math.Abs(rawSpin) * 1e-4))
        {
            Changes++;
        }

        _lastSpin = rawSpin;
        _hasLast = true;
    }
}

internal sealed class WheelTracker
{
    private readonly Dictionary<CapturePhase, PhaseAccumulator> _phases = new();
    private readonly Queue<double> _radiusSamples = new();
    private readonly DateTimeOffset _created = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastChange;
    private double _lastRelativeSpin;
    private bool _hasLastSpin;
    private double _orientationCorrelation;
    private long _samples;
    private long _changes;
    private bool _steeringPassed;
    private bool _resumedPassed;
    private bool _automaticWheelspinPassed;
    private bool _automaticBlockedPassed;
    private int _automaticWheelspinRun;
    private int _automaticBlockedRun;
    private double _historicalMaxAbsSpin;

    public WheelTracker(long ordinal, int islandIndex, nint bodyAddress, string pointerPath)
    {
        Ordinal = ordinal;
        IslandIndex = islandIndex;
        BodyAddress = bodyAddress;
        PointerPath = pointerPath;
        Id = $"W{ordinal:000}";
    }

    public string Id { get; }
    public long Ordinal { get; }
    public int IslandIndex { get; set; }
    public nint BodyAddress { get; }
    public string PointerPath { get; set; }
    public string Side { get; set; } = "Unknown";
    public string Axle { get; set; } = "Unknown";
    public Vector3 LocalPosition { get; set; }
    public double RawSignedAngularVelocity { get; private set; }
    public double RelativeSpin { get; private set; }
    public double CalibratedSpin { get; private set; }
    public double Rpm => CalibratedSpin * 60.0 / FinderMath.TwoPi;
    public double? Radius { get; private set; }
    public double? TreadSpeed => Radius.HasValue ? CalibratedSpin * Radius.Value : null;
    public double? SlipSpeed { get; private set; }
    public double? SlipRatio { get; private set; }
    public double QuaternionAgreement { get; private set; }
    public double StructuralScore { get; set; }
    public DateTimeOffset LastSeen { get; private set; }
    public double HistoricalMaxAbsSpin => _historicalMaxAbsSpin;
    public bool IsPromotedPhysical =>
        _historicalMaxAbsSpin >= 1.5 && QuaternionAgreement >= 0.40 &&
        (Radius.HasValue || StructuralScore >= 45);
    public bool HasCaptureEvidence => _phases.Count > 0;
    public bool AutomaticWheelspinPassed => _automaticWheelspinPassed;
    public bool AutomaticBlockedPassed => _automaticBlockedPassed;

    public void Update(
        DateTimeOffset now,
        double rawSigned,
        double relativeSpin,
        double chassisLongitudinalSpeed,
        double bodyLongitudinalSpeed,
        double? quaternionSpin,
        CapturePhase capturePhase)
    {
        RawSignedAngularVelocity = rawSigned;
        RelativeSpin = relativeSpin;
        LastSeen = now;
        _historicalMaxAbsSpin = Math.Max(_historicalMaxAbsSpin, Math.Abs(relativeSpin));
        _samples++;
        if (_hasLastSpin && Math.Abs(relativeSpin - _lastRelativeSpin) > Math.Max(1e-4, Math.Abs(relativeSpin) * 1e-4))
        {
            _changes++;
            _lastChange = now;
        }

        _hasLastSpin = true;
        _lastRelativeSpin = relativeSpin;

        if (Math.Abs(chassisLongitudinalSpeed) > 0.5 && Math.Abs(relativeSpin) > 0.5)
        {
            _orientationCorrelation += chassisLongitudinalSpeed * relativeSpin;
        }

        var polarity = _orientationCorrelation < 0 ? -1.0 : 1.0;
        CalibratedSpin = relativeSpin * polarity;

        if (Math.Abs(bodyLongitudinalSpeed) > 1.0 && Math.Abs(CalibratedSpin) > 1.0)
        {
            var radius = bodyLongitudinalSpeed / CalibratedSpin;
            if (radius is >= 0.15 and <= 1.8)
            {
                _radiusSamples.Enqueue(radius);
                while (_radiusSamples.Count > 600)
                {
                    _radiusSamples.Dequeue();
                }

                Radius = FinderMath.Median(_radiusSamples);
            }
        }

        if (quaternionSpin.HasValue && Math.Abs(CalibratedSpin) > 0.5 && Math.Abs(quaternionSpin.Value) > 0.5)
        {
            var error = Math.Abs(Math.Abs(quaternionSpin.Value) - Math.Abs(relativeSpin));
            QuaternionAgreement = Math.Clamp(1.0 - error / Math.Max(1.0, Math.Abs(relativeSpin)), 0, 1);
        }

        if (TreadSpeed.HasValue)
        {
            SlipSpeed = TreadSpeed.Value - bodyLongitudinalSpeed;
            var denominator = Math.Max(Math.Abs(TreadSpeed.Value), Math.Abs(bodyLongitudinalSpeed));
            SlipRatio = denominator >= 1.0 ? SlipSpeed.Value / denominator : null;
        }
        else
        {
            SlipSpeed = null;
            SlipRatio = null;
        }

        if (capturePhase != CapturePhase.None)
        {
            if (!_phases.TryGetValue(capturePhase, out var accumulator))
            {
                accumulator = new PhaseAccumulator();
                _phases.Add(capturePhase, accumulator);
            }

            accumulator.Add(relativeSpin, CalibratedSpin, chassisLongitudinalSpeed, TreadSpeed);
        }

        if (BlockedPassed && Math.Abs(CalibratedSpin) > 1.0 && Math.Abs(chassisLongitudinalSpeed) > 0.5)
        {
            _resumedPassed = true;
        }
    }

    public void MarkSteeringPassed() => _steeringPassed = true;

    public void ResetPhase(CapturePhase phase) => _phases.Remove(phase);

    public bool IsRecentlySeen(DateTimeOffset now) => LastSeen != default && now - LastSeen <= TimeSpan.FromSeconds(1);

    public void MarkAutomaticContrast(bool spinning, bool blocked)
    {
        _automaticWheelspinRun = spinning ? _automaticWheelspinRun + 1 : 0;
        _automaticBlockedRun = blocked ? _automaticBlockedRun + 1 : 0;
        if (_automaticWheelspinRun >= 15)
        {
            _automaticWheelspinPassed = true;
        }

        if (_automaticBlockedRun >= 15)
        {
            _automaticBlockedPassed = true;
        }
    }

    public bool ParkedPassed => Phase(CapturePhase.Parked) is { Samples: >= 90, MaxAbsSpin: < 0.4 };

    public bool ForwardPassed =>
        (Phase(CapturePhase.Forward) is { Samples: >= 90 } forward &&
         forward.MeanChassis > 0.5 && forward.MeanAbsSpin > 0.8 && forward.Changes >= 20) ||
        IsPromotedPhysical;

    public bool ReversePassed
    {
        get
        {
            var forward = Phase(CapturePhase.Forward);
            var reverse = Phase(CapturePhase.Reverse);
            return forward is { Samples: >= 90 } && reverse is { Samples: >= 90 } &&
                   forward.MeanChassis > 0.5 && reverse.MeanChassis < -0.5 &&
                   Math.Abs(forward.MeanSpin) > 0.5 && Math.Abs(reverse.MeanSpin) > 0.5 &&
                   forward.MeanSpin * reverse.MeanSpin < 0;
        }
    }

    public bool SteeringPassed => _steeringPassed;
    public bool WheelspinPassed => _automaticWheelspinPassed ||
                                   IsGuidedWheelspinPassed(CapturePhase.StationaryWheelspin) ||
                                   IsGuidedWheelspinPassed(CapturePhase.StationaryGear1) ||
                                   IsGuidedWheelspinPassed(CapturePhase.StationaryGear2) ||
                                   IsGuidedWheelspinPassed(CapturePhase.StationaryGear3) ||
                                   IsGuidedWheelspinPassed(CapturePhase.StationaryGear4) ||
                                   IsGuidedWheelspinPassed(CapturePhase.StationaryGear5) ||
                                   IsGuidedWheelspinPassed(CapturePhase.StationaryReverse) ||
                                   IsGuidedWheelspinPassed(CapturePhase.Stationary2wdGear1) ||
                                   IsGuidedWheelspinPassed(CapturePhase.Stationary2wdGear2) ||
                                   IsGuidedWheelspinPassed(CapturePhase.Stationary4wdGear1) ||
                                   IsGuidedWheelspinPassed(CapturePhase.Stationary4wdGear2);
    public bool BlockedPassed => _automaticBlockedPassed ||
                                 Phase(CapturePhase.PhysicallyBlocked) is { Samples: >= 90, MaxAbsChassis: < 0.15, MaxAbsSpin: < 0.5 };
    public bool ResumedPassed => _resumedPassed;

    public WheelSnapshot Snapshot(DateTimeOffset now)
    {
        var elapsed = Math.Max(0.001, (now - _created).TotalSeconds);
        var updateHz = _changes / elapsed;
        var confidence = StructuralScore;
        if (ParkedPassed) confidence += 7;
        if (ForwardPassed) confidence += 10;
        if (ReversePassed) confidence += 13;
        if (Radius.HasValue) confidence += 12;
        if (SteeringPassed) confidence += 8;
        if (WheelspinPassed) confidence += 18;
        if (BlockedPassed) confidence += 8;
        if (ResumedPassed) confidence += 4;
        confidence += QuaternionAgreement * 10;
        confidence = FinderMath.ClampScore(confidence);
        if (!IsPromotedPhysical) confidence = Math.Min(confidence, 34);
        else if (!ReversePassed) confidence = Math.Min(confidence, 84);
        else if (!WheelspinPassed) confidence = Math.Min(confidence, 84);
        else if (!BlockedPassed || !ResumedPassed) confidence = Math.Min(confidence, 94);

        var validation = WheelspinPassed && BlockedPassed && ResumedPassed
            ? "Independently rotating physical wheel — proven"
            : WheelspinPassed
                ? "Stationary wheelspin divergence captured; blocked/resume proof still needed"
                : BlockedPassed
                    ? "Physical rolling wheel stayed stationary while sibling wheelspin was live"
                    : IsPromotedPhysical
                        ? "Physical wheel proven by rolling radius + quaternion rotation"
                        : "Candidate; complete normal rolling and guided proofs";
        if (_samples > 60 && _lastChange != default && now - _lastChange > TimeSpan.FromSeconds(0.75) &&
            Math.Abs(CalibratedSpin) > 0.5)
        {
            validation += "; possible freeze";
        }

        var position = $"F {LocalPosition.Z:0.00}  R {LocalPosition.X:0.00}  U {LocalPosition.Y:0.00} m";
        var proofSource = _automaticWheelspinPassed || _automaticBlockedPassed
            ? "Automatic simultaneous wheelspin/blocked contrast"
            : "Guided captures and physical-structure validation";
        return new WheelSnapshot(
            Id,
            Ordinal,
            IslandIndex,
            BodyAddress,
            PointerPath,
            Side,
            Axle,
            position,
            RawSignedAngularVelocity,
            CalibratedSpin,
            Rpm,
            Radius,
            TreadSpeed,
            WheelspinPassed ? "Likely active" : "Unknown",
            WheelspinPassed ? "Medium; behavioral wheelspin evidence" : "Low; Torque state not decoded",
            SlipSpeed,
            SlipRatio,
            updateHz,
            confidence,
            validation,
            proofSource,
            _historicalMaxAbsSpin,
            ParkedPassed,
            ForwardPassed,
            ReversePassed,
            SteeringPassed,
            WheelspinPassed,
            BlockedPassed,
            ResumedPassed,
            QuaternionAgreement,
            _phases
                .OrderBy(value => value.Key)
                .Select(value => new CaptureEvidenceSnapshot(
                    value.Key,
                    value.Value.Samples,
                    value.Value.Changes,
                    value.Value.NearStationarySamples,
                    value.Value.StationarySpinSamples,
                    value.Value.MeanSpin,
                    value.Value.MeanAbsSpin,
                    double.IsPositiveInfinity(value.Value.MinimumSpin) ? 0 : value.Value.MinimumSpin,
                    double.IsNegativeInfinity(value.Value.MaximumSpin) ? 0 : value.Value.MaximumSpin,
                    value.Value.MaxAbsSpin,
                    value.Value.MeanSpinNearStationary,
                    value.Value.MeanAbsSpinNearStationary,
                    value.Value.MeanChassis,
                    value.Value.MaxAbsChassis))
                .ToArray());
    }

    private PhaseAccumulator? Phase(CapturePhase phase) => _phases.TryGetValue(phase, out var value) ? value : null;

    private bool IsGuidedWheelspinPassed(CapturePhase phase) =>
        Phase(phase) is { Samples: >= 60, StationarySpinSamples: >= 30, Changes: >= 10 };
}
