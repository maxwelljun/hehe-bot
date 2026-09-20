namespace ScreenWatch.Core;

public readonly record struct MatchResult(double Score, double ColorScore, double ChangedPixelPercent);

/// <summary>Compares equally sized RGB samples of a fixed screen region.</summary>
public static class ImageMatcher
{
    public static MatchResult Compare(ReadOnlySpan<byte> reference, ReadOnlySpan<byte> current)
    {
        if (reference.Length == 0 || reference.Length != current.Length || reference.Length % 3 != 0)
            throw new ArgumentException("必须提供相同长度、非空的 RGB 图片样本。");

        double squaredError = 0;
        int changedPixels = 0;
        for (int i = 0; i < reference.Length; i += 3)
        {
            int maximumDifference = 0;
            for (int c = 0; c < 3; c++)
            {
                int delta = Math.Abs(reference[i + c] - current[i + c]);
                squaredError += delta * delta;
                maximumDifference = Math.Max(maximumDifference, delta);
            }
            if (maximumDifference > 35) changedPixels++;
        }

        double colorScore = 100 * (1 - Math.Sqrt(squaredError / reference.Length) / 255);
        double changedPercent = 100.0 * changedPixels / (reference.Length / 3);
        return new MatchResult(Math.Clamp(Math.Min(colorScore, 100 - changedPercent), 0, 100), colorScore, changedPercent);
    }
}
