namespace XRaySystem.Domain.WindowLevel;

/// Pure Gray16 -> Gray8 presentation mapping. The source array is never changed.
public static class WindowLevelMapper
{
    public const int Gray16Maximum = ushort.MaxValue;

    public static byte[] BuildLut(double center, double width)
    {
        center = Math.Clamp(center, 0, Gray16Maximum);
        width = Math.Clamp(width, 1, Gray16Maximum);
        var low = center - width / 2.0;
        var high = center + width / 2.0;
        var range = high - low;
        var lut = new byte[Gray16Maximum + 1];
        for (var value = 0; value <= Gray16Maximum; value++)
        {
            var normalized = range <= 0 ? (value >= high ? 255 : 0) : (value - low) * 255.0 / range;
            lut[value] = (byte)Math.Clamp(Math.Round(normalized, MidpointRounding.AwayFromZero), 0, 255);
        }
        return lut;
    }

    public static byte[] Map(ReadOnlySpan<ushort> pixels, double center, double width, CancellationToken cancellationToken = default)
    {
        var lut = BuildLut(center, width);
        var mapped = new byte[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            if ((i & 0x3FFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            mapped[i] = lut[pixels[i]];
        }
        return mapped;
    }

    /// Exact histogram percentile: nearest-rank, selecting the first value whose cumulative
    /// count is at least ceil(percentile * sampleCount). Empty input returns 0.
    public static ushort HistogramPercentile(ReadOnlySpan<ushort> pixels, double percentile)
    {
        if (pixels.Length == 0) return 0;
        percentile = Math.Clamp(percentile, 0, 1);
        var histogram = new int[Gray16Maximum + 1];
        foreach (var pixel in pixels) histogram[pixel]++;
        var rank = Math.Max(1, (long)Math.Ceiling(percentile * pixels.Length));
        long cumulative = 0;
        for (var value = 0; value < histogram.Length; value++)
        {
            cumulative += histogram[value];
            if (cumulative >= rank) return (ushort)value;
        }
        return Gray16Maximum;
    }

    public static (double Center, double Width) Auto(ReadOnlySpan<ushort> pixels)
    {
        if (pixels.Length == 0) return (32767.5, 65535);
        var low = HistogramPercentile(pixels, 0.01);
        var high = HistogramPercentile(pixels, 0.99);
        return ((low + high) / 2.0, Math.Max(1, high - low));
    }
}
