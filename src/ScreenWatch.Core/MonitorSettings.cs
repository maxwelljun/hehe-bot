namespace ScreenWatch.Core;

public sealed record CaptureRegion(int X, int Y, int Width, int Height)
{
    public bool IsValid => Width is >= 12 and <= 8192 && Height is >= 12 and <= 8192
        && (long)Width * Height <= 33_554_432;

    public bool FitsInside(int x, int y, int width, int height) => IsValid
        && X >= x && Y >= y
        && (long)X + Width <= (long)x + width
        && (long)Y + Height <= (long)y + height;
}

public sealed record MonitorSettings
{
    public int Version { get; init; } = 1;
    public CaptureRegion? Region { get; init; }
    public string? ReferenceFile { get; init; }
    public int IntervalMs { get; init; } = 1000;
    public double Threshold { get; init; } = 95;
    public int ConfirmFrames { get; init; } = 2;
    public int RearmFrames { get; init; } = 2;
    public int CooldownSeconds { get; init; } = 30;
    public bool PlaySound { get; init; } = true;
    public bool SaveScreenshots { get; init; } = true;

    public void Validate()
    {
        if (Version != 1) throw new ArgumentException("配置版本不受支持。请备份配置后重新设置。");
        if (Region is not null && !Region.IsValid) throw new ArgumentException("区域尺寸必须在 12–8192 像素之间，且不超过 3200 万像素。");
        if (IntervalMs is < 250 or > 60_000) throw new ArgumentException("截图间隔必须在 250–60000 毫秒之间。");
        if (!double.IsFinite(Threshold) || Threshold is < 50 or > 100) throw new ArgumentException("匹配阈值必须在 50–100 之间。");
        if (ConfirmFrames is < 1 or > 20 || RearmFrames is < 1 or > 20) throw new ArgumentException("确认次数必须在 1–20 之间。");
        if (CooldownSeconds is < 0 or > 3600) throw new ArgumentException("提醒冷却时间必须在 0–3600 秒之间。");
        if (ReferenceFile is not null && !IsReferenceFileName(ReferenceFile)) throw new ArgumentException("参考图片文件名无效。");
    }

    public static bool IsReferenceFileName(string name) => name.StartsWith("reference-", StringComparison.Ordinal)
        && name.EndsWith(".png", StringComparison.Ordinal)
        && name.Length == 46
        && name.AsSpan(10, 32).ToArray().All(Uri.IsHexDigit);
}
