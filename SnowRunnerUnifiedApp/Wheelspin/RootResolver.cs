using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace SnowRunnerWheelspinFinder;

internal sealed class SnowRunnerRootResolver
{
    private const string SnowFlyerPattern = "0F B6 5C 24 70 84 DB 75 43";
    private readonly object _gate = new();
    private readonly ProcessMemoryReader _memory;
    private readonly byte[] _module;
    private readonly List<int> _staticRvas = new();
    private readonly List<nint> _truckControlInstances = new();
    private readonly List<nint> _directVehicles = new();
    private nint _snowFlyerStatic;

    public SnowRunnerRootResolver(ProcessMemoryReader memory, Action<string> log)
    {
        _memory = memory;
        _module = memory.ReadModule();
        ModuleFingerprint = memory.ModuleFingerprint(_module);
        ResolveSnowFlyerStatic(log);
        LoadCaches(log);
    }

    public string ModuleFingerprint { get; }
    public nint SnowFlyerStatic => _snowFlyerStatic;

    public bool TryResolveChassis(out nint chassis, out Vector3 position, out string path)
    {
        chassis = 0;
        position = default;
        path = "SnowFlyer static -> [base] +0x28 -> +0x18 -> chassis";
        if (_snowFlyerStatic == 0 ||
            !_memory.TryReadPointer(_snowFlyerStatic, out var root) ||
            !_memory.TryReadPointer(root + 0x28, out var next) ||
            !_memory.TryReadPointer(next + 0x18, out var candidate) ||
            !_memory.ValidateRange(candidate, 0x250))
        {
            return false;
        }

        if (!TryReadBasis(candidate, out var basis) || basis.Score < 72 ||
            !TryReadPosition(candidate, out position) ||
            !_memory.TryRead<Vector3>(candidate + 0x230, out var velocity) ||
            !FinderMath.IsFinite(velocity))
        {
            return false;
        }

        chassis = candidate;
        return true;
    }

    public bool TryResolveActiveVehicle(nint chassis, out nint truckControl, out nint vehicle, out string path)
    {
        truckControl = 0;
        vehicle = 0;
        path = "not resolved";

        List<nint> instances;
        List<nint> directVehicles;
        List<int> rvas;
        lock (_gate)
        {
            instances = _truckControlInstances.ToList();
            directVehicles = _directVehicles.ToList();
            rvas = _staticRvas.ToList();
        }

        foreach (var directVehicle in directVehicles)
        {
            if (TryValidateVehicle(directVehicle, chassis))
            {
                vehicle = directVehicle;
                path = $"Read-only chassis back-link: vehicle +0x5C8 -> chassis";
                return true;
            }
        }

        foreach (var rva in rvas)
        {
            if (rva < 0 || rva + 8 > _memory.ModuleSize ||
                !_memory.TryReadPointer(_memory.ModuleBase + rva, out var instance))
            {
                continue;
            }

            if (TryValidateTruckControl(instance, chassis, out vehicle, out var activeOffset))
            {
                AddInstance(instance);
                truckControl = instance;
                path = $"SnowRunner.exe+0x{rva:X} -> TRUCK_CONTROL +0x{activeOffset:X} -> vehicle +0x5C8 -> chassis";
                return true;
            }
        }

        foreach (var instance in instances)
        {
            if (TryValidateTruckControl(instance, chassis, out vehicle, out var activeOffset))
            {
                truckControl = instance;
                path = $"RTTI TRUCK_CONTROL {FinderMath.Address(instance)} +0x{activeOffset:X} -> vehicle +0x5C8 -> chassis";
                return true;
            }
        }

        return false;
    }

    public nint SearchTruckControl(
        nint chassis,
        IProgress<DiscoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var vtables = FindTruckControlVtables();
        progress?.Report(new DiscoveryProgress(
            "RTTI vehicle root",
            $"Found {vtables.Count} TRUCK_CONTROL vtable hypothesis(es).",
            0, 0, _module.Length, _module.Length, vtables.Count, 0, stopwatch.Elapsed.TotalSeconds));
        long regions = 0;
        long chunks = 0;
        long bytes = _module.Length;
        long addresses = 0;
        long hits = 0;
        const int chunkSize = 1024 * 1024;
        const long maximumBytes = 768L * 1024 * 1024;

        foreach (var region in _memory.EnumerateReadableRegions())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes >= maximumBytes)
            {
                break;
            }

            regions++;
            if (region.Type is not (NativeMethods.MemPrivate or NativeMethods.MemMapped) ||
                region.Size < 0x1000 || region.Size > 128L * 1024 * 1024)
            {
                continue;
            }

            for (long regionOffset = 0; regionOffset < region.Size && bytes < maximumBytes; regionOffset += chunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(chunkSize, region.Size - regionOffset);
                var block = _memory.TryReadBlock(region.BaseAddress + (nint)regionOffset, count);
                chunks++;
                addresses += count / 8;
                if (block is null)
                {
                    continue;
                }

                bytes += block.Length;
                for (var offset = 0; offset + 8 <= block.Length; offset += 8)
                {
                    var raw = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(offset, 8));
                    var address = region.BaseAddress + (nint)regionOffset + offset;
                    if (raw == unchecked((ulong)(long)chassis) &&
                        TryValidateVehicle(address - 0x5C8, chassis))
                    {
                        AddDirectVehicle(address - 0x5C8);
                        progress?.Report(new DiscoveryProgress(
                            "Direct vehicle root",
                            $"Recovered vehicle from its +0x5C8 chassis back-link at {FinderMath.Address(address)}.",
                            regions, chunks, bytes, addresses, hits, 1, stopwatch.Elapsed.TotalSeconds));
                        return address - 0x5C8;
                    }

                    if (!vtables.Contains(raw))
                    {
                        continue;
                    }

                    hits++;
                    var instance = address;
                    if (!TryValidateTruckControl(instance, chassis, out _, out _))
                    {
                        continue;
                    }

                    AddInstance(instance);
                    CacheStaticReferences(instance);
                    progress?.Report(new DiscoveryProgress(
                        "RTTI vehicle root",
                        $"Cross-linked TRUCK_CONTROL at {FinderMath.Address(instance)} to the SnowFlyer chassis.",
                        regions, chunks, bytes, addresses, hits, 1, stopwatch.Elapsed.TotalSeconds));
                    return instance;
                }

                if (chunks % 16 == 0)
                {
                    progress?.Report(new DiscoveryProgress(
                        "RTTI vehicle root",
                        $"Scanning {FinderMath.Address(region.BaseAddress + (nint)regionOffset)} read-only.",
                        regions, chunks, bytes, addresses, hits, 0, stopwatch.Elapsed.TotalSeconds));
                }
            }
        }

        progress?.Report(new DiscoveryProgress(
            "RTTI vehicle root",
            "No cross-linked TRUCK_CONTROL instance was found. Chassis-based wheel discovery remains active.",
            regions, chunks, bytes, addresses, hits, 0, stopwatch.Elapsed.TotalSeconds));
        return 0;
    }

    public bool TryReadBasis(nint body, out BasisState basis)
    {
        basis = new BasisState(Vector3.Zero, Vector3.Zero, Vector3.Zero, 0);
        if (!_memory.TryRead<Vector3>(body + 0x170, out var forward) ||
            !_memory.TryRead<Vector3>(body + 0x180, out var up) ||
            !_memory.TryRead<Vector3>(body + 0x190, out var right))
        {
            return false;
        }

        var score = FinderMath.ScoreBasis(forward, up, right);
        basis = new BasisState(
            FinderMath.NormalizeOrZero(forward),
            FinderMath.NormalizeOrZero(up),
            FinderMath.NormalizeOrZero(right),
            score);
        return score > 0;
    }

    public bool TryReadPosition(nint body, out Vector3 position)
    {
        foreach (var offset in new[] { 0x1A0, 0x1B0, 0x1C0 })
        {
            if (_memory.TryRead<Vector3>(body + offset, out position) &&
                FinderMath.IsFinite(position) &&
                Math.Abs(position.X) < 1_000_000 && Math.Abs(position.Y) < 1_000_000 && Math.Abs(position.Z) < 1_000_000)
            {
                return true;
            }
        }

        position = default;
        return false;
    }

    public bool TryReadQuaternion(nint body, out Quaternion quaternion, out int offset)
    {
        foreach (var candidateOffset in new[] { 0x1D0, 0x1E0 })
        {
            if (_memory.TryRead<Quaternion>(body + candidateOffset, out quaternion) && FinderMath.ScoreQuaternion(quaternion) >= 65)
            {
                offset = candidateOffset;
                return true;
            }
        }

        quaternion = Quaternion.Identity;
        offset = 0;
        return false;
    }

    private void ResolveSnowFlyerStatic(Action<string> log)
    {
        var matches = PatternScanner.FindAll(_module, BytePattern.Parse(SnowFlyerPattern), maximum: 8);
        foreach (var offset in matches)
        {
            var afterPattern = _memory.ModuleBase + offset + SnowFlyerPattern.Split(' ').Length;
            if (TryResolveRelativeTarget(afterPattern, 1, out var callTarget) &&
                TryResolveRelativeTarget(callTarget, 3, out var singletonAddress) &&
                _memory.ValidateRange(singletonAddress, 8))
            {
                _snowFlyerStatic = singletonAddress;
                log($"SnowFlyer chassis anchor: module+0x{offset:X} -> {FinderMath.Address(singletonAddress)}.");
                return;
            }
        }

        log($"SnowFlyer chassis signature produced {matches.Count} match(es), but none validated.");
    }

    private bool TryResolveRelativeTarget(nint instructionStart, int displacementOffset, out nint target)
    {
        if (_memory.TryRead<int>(instructionStart + displacementOffset, out var relative))
        {
            target = instructionStart + displacementOffset + 4 + relative;
            return _memory.ValidateRange(target, 1);
        }

        target = 0;
        return false;
    }

    private bool TryValidateTruckControl(nint instance, nint chassis, out nint vehicle, out int activeOffset)
    {
        var offsets = new[] { 0xE8, 0x08 }
            .Concat(Enumerable.Range(0, 0x201 / 8).Select(index => index * 8))
            .Distinct();
        foreach (var offset in offsets)
        {
            if (_memory.TryReadPointer(instance + offset, out vehicle) && TryValidateVehicle(vehicle, chassis))
            {
                activeOffset = offset;
                return true;
            }
        }

        vehicle = 0;
        activeOffset = 0;
        return false;
    }

    private bool TryValidateVehicle(nint vehicle, nint chassis) =>
        _memory.ValidateRange(vehicle, 0x5D0) &&
        _memory.TryReadPointer(vehicle + 0x5C8, out var vehicleChassis) &&
        IsSameChassis(vehicleChassis, chassis);

    private void AddDirectVehicle(nint vehicle)
    {
        lock (_gate)
        {
            if (!_directVehicles.Contains(vehicle))
            {
                _directVehicles.Add(vehicle);
            }
        }
    }

    private bool IsSameChassis(nint candidate, nint known)
    {
        if (candidate == known)
        {
            return true;
        }

        return TryReadPosition(candidate, out var candidatePosition) && TryReadPosition(known, out var knownPosition) &&
               Vector3.Distance(candidatePosition, knownPosition) < 0.05f &&
               TryReadBasis(candidate, out var candidateBasis) && TryReadBasis(known, out var knownBasis) &&
               candidateBasis.Score >= 75 && knownBasis.Score >= 75 &&
               Vector3.Dot(candidateBasis.Forward, knownBasis.Forward) > 0.995f;
    }

    private HashSet<ulong> FindTruckControlVtables()
    {
        var vtables = new HashSet<ulong>();
        foreach (var hit in FindAsciiOccurrences(_module, "TRUCK_CONTROL"))
        {
            var nameStart = hit;
            for (var back = 0; back < 128 && nameStart > 0 && _module[nameStart - 1] != 0; back++)
            {
                nameStart--;
            }

            var typeDescriptorRva = nameStart - 16;
            if (typeDescriptorRva <= 0)
            {
                continue;
            }

            foreach (var reference in FindUInt32Occurrences(_module, (uint)typeDescriptorRva))
            {
                var completeObjectLocatorRva = reference - 12;
                if (completeObjectLocatorRva <= 0)
                {
                    continue;
                }

                var locator = unchecked((ulong)(long)(_memory.ModuleBase + completeObjectLocatorRva));
                foreach (var locatorReference in FindUInt64Occurrences(_module, locator))
                {
                    var vtable = unchecked((ulong)(long)(_memory.ModuleBase + locatorReference + 8));
                    vtables.Add(vtable);
                }
            }
        }

        return vtables;
    }

    private void CacheStaticReferences(nint instance)
    {
        var raw = unchecked((ulong)(long)instance);
        var found = FindUInt64Occurrences(_module, raw).Where(rva => rva >= 0 && rva + 8 <= _memory.ModuleSize).Take(64).ToArray();
        if (found.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var rva in found)
            {
                if (!_staticRvas.Contains(rva))
                {
                    _staticRvas.Add(rva);
                }
            }

            _staticRvas.Sort();
            SaveOwnCache();
        }
    }

    private void AddInstance(nint instance)
    {
        lock (_gate)
        {
            if (!_truckControlInstances.Contains(instance))
            {
                _truckControlInstances.Add(instance);
            }
        }
    }

    private void LoadCaches(Action<string> log)
    {
        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnowRunnerWheelspinFinder", "root-offset-cache.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnowRunnerTelemetry", "live-offset-cache.json")
        };

        foreach (var path in paths)
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                if (!root.TryGetProperty("ModuleSize", out var moduleSize) || moduleSize.GetInt32() != _memory.ModuleSize ||
                    !root.TryGetProperty("ModuleVersion", out var version) ||
                    !string.Equals(version.GetString(), _memory.VersionText, StringComparison.Ordinal) ||
                    !root.TryGetProperty("TruckControlStaticRvas", out var rvas))
                {
                    continue;
                }

                lock (_gate)
                {
                    foreach (var item in rvas.EnumerateArray())
                    {
                        var value = item.GetInt32();
                        if (value >= 0 && value + 8 <= _memory.ModuleSize && !_staticRvas.Contains(value))
                        {
                            _staticRvas.Add(value);
                        }
                    }
                }

                log($"Loaded {_staticRvas.Count} compatible read-only vehicle-root cache reference(s).");
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                log($"Ignored offset cache {path}: {ex.Message}");
            }
        }
    }

    private void SaveOwnCache()
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnowRunnerWheelspinFinder");
            Directory.CreateDirectory(directory);
            var payload = new
            {
                ModuleVersion = _memory.VersionText,
                ModuleSize = _memory.ModuleSize,
                TruckControlStaticRvas = _staticRvas.Distinct().OrderBy(value => value).Take(64).ToArray()
            };
            File.WriteAllText(Path.Combine(directory, "root-offset-cache.json"), JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Cache persistence is optional. Discovery remains fully functional without it.
        }
    }

    private static IEnumerable<int> FindAsciiOccurrences(byte[] data, string text)
    {
        var needle = Encoding.ASCII.GetBytes(text);
        for (var offset = 0; offset <= data.Length - needle.Length; offset++)
        {
            if (data.AsSpan(offset, needle.Length).SequenceEqual(needle))
            {
                yield return offset;
            }
        }
    }

    private static IEnumerable<int> FindUInt32Occurrences(byte[] data, uint value)
    {
        for (var offset = 0; offset + 4 <= data.Length; offset++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)) == value)
            {
                yield return offset;
            }
        }
    }

    private static IEnumerable<int> FindUInt64Occurrences(byte[] data, ulong value)
    {
        for (var offset = 0; offset + 8 <= data.Length; offset++)
        {
            if (BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8)) == value)
            {
                yield return offset;
            }
        }
    }
}
