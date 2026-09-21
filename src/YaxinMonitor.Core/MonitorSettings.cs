using System.Text.Json.Serialization;

namespace YaxinMonitor.Core;

public sealed record YaxinSettings
{
    public int Version { get; init; } = 1;
    [JsonConverter(typeof(JsonStringEnumConverter<MonitorMode>))]
    public MonitorMode Mode { get; init; } = MonitorMode.ReadOnly;
    public int ChromeDebugPort { get; init; } = 9222;
    public int MinimumRemainingMilliseconds { get; init; } = 8_000;
    public decimal DailyStakeLimit { get; init; }
    public decimal MaxReservedStake { get; init; }
    public bool PlayAcceptedSound { get; init; } = true;
    public StrategySettings Strategy { get; init; } = new();
    public string AllowedBundle { get; init; } = FrontendCompatibility.CurrentBundle;

    public void Validate()
    {
        if (Version != 1) throw new ArgumentException("配置版本不受支持。");
        if (ChromeDebugPort is < 1024 or > 65535) throw new ArgumentException("Chrome 调试端口必须在 1024–65535 之间。");
        if (MinimumRemainingMilliseconds is < 1_000 or > 60_000) throw new ArgumentException("下注安全余量必须在 1–60 秒之间。");
        if (DailyStakeLimit < 0 || MaxReservedStake < 0) throw new ArgumentException("资金上限不能为负数。");
        if (Mode == MonitorMode.Live && (DailyStakeLimit <= 0 || MaxReservedStake <= 0))
            throw new ArgumentException("真实模式必须设置每日下注上限和同时在途金额上限。");
        if (Strategy is null) throw new ArgumentException("策略配置不能为空。");
        Strategy.Validate();
        if (string.IsNullOrWhiteSpace(AllowedBundle) || AllowedBundle.Length > 100 || AllowedBundle.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_')))
            throw new ArgumentException("前端版本标识无效。");
    }
}

public static class FrontendCompatibility
{
    public const string CurrentBundle = "index.fd7e6.js";
    public const string PreviousBundle = "index.0e81c.js";

    public static bool IsSupported(string bundle, string configuredBundle) =>
        string.Equals(bundle, CurrentBundle, StringComparison.Ordinal)
        || string.Equals(bundle, PreviousBundle, StringComparison.Ordinal)
        || string.Equals(bundle, configuredBundle, StringComparison.Ordinal);
}
