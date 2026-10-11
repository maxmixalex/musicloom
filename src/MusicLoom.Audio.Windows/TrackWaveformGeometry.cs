namespace MusicLoom.Audio.Windows;

/// <summary>
/// An immutable, finite point in the playback row's pixel-independent
/// coordinate system. Preparing points here prevents invalid WinUI shape
/// coordinates from crossing the WinRT boundary.
/// </summary>
public readonly record struct WaveformCoordinate(double X, double Y);

public static class TrackWaveformGeometry
{
    private const double MaximumDimension = 16_384;

    /// <summary>
    /// Project a real RMS envelope into safely bounded visual coordinates.
    /// An invalid viewport returns no points rather than attempting to
    /// create an invalid native XAML Geometry. Corrupt samples are silent.
    /// </summary>
    public static WaveformCoordinate[] Project(
        ReadOnlySpan<float> samples,
        double width,
        double height)
    {
        if (samples.Length < 2
            || samples.Length > 16_384
            || !double.IsFinite(width)
            || !double.IsFinite(height)
            || width < 2
            || height < 2
            || width > MaximumDimension
            || height > MaximumDimension)
        {
            return [];
        }

        double baseline = Math.Clamp(height - 3, 0, height);
        double usableHeight = Math.Max(0, height - 9);
        WaveformCoordinate[] points = new WaveformCoordinate[samples.Length];

        for (int i = 0; i < samples.Length; i++)
        {
            double center = SafeLevel(samples[i]);
            double before = SafeLevel(samples[Math.Max(0, i - 1)]);
            double after = SafeLevel(samples[Math.Min(samples.Length - 1, i + 1)]);

            // Smoothing only averages neighboring measured RMS values;
            // it never fabricates an oscillating animation.
            double level = center * 0.6 + before * 0.2 + after * 0.2;
            double x = width * i / (samples.Length - 1);
            double y = Math.Clamp(
                baseline - level * usableHeight,
                0,
                height);
            points[i] = new WaveformCoordinate(x, y);
        }

        return points;
    }

    private static double SafeLevel(float value) =>
        float.IsFinite(value)
            ? Math.Clamp((double)value, 0, 1)
            : 0;
}
