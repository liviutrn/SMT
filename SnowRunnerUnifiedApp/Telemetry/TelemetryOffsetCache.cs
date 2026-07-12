using System.Text.Json;

namespace SnowRunnerTelemetry;

internal sealed record TelemetryOffsetCache(
    string ModuleVersion,
    int ModuleSize,
    int[] TruckControlStaticRvas);

internal static class TelemetryOffsetCacheStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static TelemetryOffsetCache? Load(string moduleVersion, int moduleSize)
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return null;
            }

            var cache = JsonSerializer.Deserialize<TelemetryOffsetCache>(File.ReadAllText(CachePath), JsonOptions);
            return cache is not null &&
                   cache.ModuleSize == moduleSize &&
                   string.Equals(cache.ModuleVersion, moduleVersion, StringComparison.Ordinal)
                ? cache
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string moduleVersion, int moduleSize, IEnumerable<int> truckControlStaticRvas)
    {
        try
        {
            var offsets = truckControlStaticRvas
                .Where(offset => offset >= 0 && offset + IntPtr.Size <= moduleSize)
                .Distinct()
                .OrderBy(offset => offset)
                .Take(64)
                .ToArray();
            if (offsets.Length == 0)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var cache = new TelemetryOffsetCache(moduleVersion, moduleSize, offsets);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(cache, JsonOptions));
        }
        catch
        {
            // Cache persistence is optional; telemetry remains fully functional without it.
        }
    }

    private static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SnowRunnerTelemetry",
        "live-offset-cache.json");
}
