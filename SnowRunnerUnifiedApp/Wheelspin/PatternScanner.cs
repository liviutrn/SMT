namespace SnowRunnerWheelspinFinder;

internal sealed class BytePattern
{
    private BytePattern(byte?[] bytes) => Bytes = bytes;

    public byte?[] Bytes { get; }
    public int Length => Bytes.Length;

    public static BytePattern Parse(string text)
    {
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new BytePattern(tokens.Select(token => token is "?" or "??" ? (byte?)null : Convert.ToByte(token, 16)).ToArray());
    }
}

internal static class PatternScanner
{
    public static IReadOnlyList<int> FindAll(ReadOnlySpan<byte> data, BytePattern pattern, int maximum = 64)
    {
        var matches = new List<int>();
        if (pattern.Length == 0 || data.Length < pattern.Length || maximum <= 0)
        {
            return matches;
        }

        for (var start = 0; start <= data.Length - pattern.Length && matches.Count < maximum; start++)
        {
            var matched = true;
            for (var index = 0; index < pattern.Length; index++)
            {
                var expected = pattern.Bytes[index];
                if (expected.HasValue && data[start + index] != expected.Value)
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                matches.Add(start);
            }
        }

        return matches;
    }
}
