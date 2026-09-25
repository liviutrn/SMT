namespace SnowRunnerTelemetry;

internal sealed class BytePattern
{
    private BytePattern(byte?[] bytes)
    {
        Bytes = bytes;
    }

    public byte?[] Bytes { get; }
    public int Length => Bytes.Length;

    public static BytePattern Parse(string pattern)
    {
        var tokens = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var bytes = new byte?[tokens.Length];

        for (var i = 0; i < tokens.Length; i++)
        {
            bytes[i] = tokens[i] is "?" or "??" ? null : Convert.ToByte(tokens[i], 16);
        }

        return new BytePattern(bytes);
    }
}

internal static class PatternScanner
{
    public static int FindFirst(ReadOnlySpan<byte> data, BytePattern pattern)
    {
        if (pattern.Length == 0 || data.Length < pattern.Length)
        {
            return -1;
        }

        var bytes = pattern.Bytes;
        var lastStart = data.Length - bytes.Length;

        for (var i = 0; i <= lastStart; i++)
        {
            var matched = true;
            for (var j = 0; j < bytes.Length; j++)
            {
                var expected = bytes[j];
                if (expected.HasValue && data[i + j] != expected.Value)
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return i;
            }
        }

        return -1;
    }
}
