using System.Text.Json.Serialization;

namespace YaxinMonitor.Core;

public enum StrategyTriggerSide
{
    Both,
    BankerOnly,
    PlayerOnly
}

public enum StrategyDirection
{
    Opposite,
    Follow
}

public sealed record StrategySettings
{
    public int StreakLength { get; init; } = FixedStrategy.StreakLength;
    public StrategyTriggerSide TriggerSide { get; init; } = StrategyTriggerSide.Both;
    public StrategyDirection Direction { get; init; } = StrategyDirection.Opposite;
    public decimal[] Stakes { get; init; } = [.. FixedStrategy.Stakes];

    [JsonIgnore]
    public string Id
    {
        get
        {
            Validate();
            if (StreakLength == FixedStrategy.StreakLength && TriggerSide == StrategyTriggerSide.Both
                && Direction == StrategyDirection.Opposite && Stakes.SequenceEqual(FixedStrategy.Stakes))
                return FixedStrategy.Id;
            string amounts = string.Join('_', Stakes.Select(value => value.ToString("0", System.Globalization.CultureInfo.InvariantCulture)));
            return $"custom-v1-{StreakLength}-{(int)TriggerSide}-{(int)Direction}-{amounts}";
        }
    }

    public void Validate()
    {
        if (StreakLength is < 2 or > 20) throw new ArgumentException("连续次数必须在 2–20 之间。");
        if (!Enum.IsDefined(TriggerSide)) throw new ArgumentException("触发走势无效。");
        if (!Enum.IsDefined(Direction)) throw new ArgumentException("下注方向无效。");
        if (Stakes is null || Stakes.Length is < 1 or > 10) throw new ArgumentException("金额序列必须包含 1–10 档。");
        if (Stakes.Any(value => value <= 0 || value > 1_000_000 || decimal.Truncate(value) != value))
            throw new ArgumentException("每档金额必须是 1–1000000 的整数。");
    }

    public bool Matches(BaccaratOutcome outcome) => TriggerSide switch
    {
        StrategyTriggerSide.Both => outcome is BaccaratOutcome.Banker or BaccaratOutcome.Player,
        StrategyTriggerSide.BankerOnly => outcome == BaccaratOutcome.Banker,
        StrategyTriggerSide.PlayerOnly => outcome == BaccaratOutcome.Player,
        _ => false
    };

    public BetSide SelectBetSide(BaccaratOutcome streakSide)
    {
        BetSide same = streakSide switch
        {
            BaccaratOutcome.Banker => BetSide.Banker,
            BaccaratOutcome.Player => BetSide.Player,
            _ => throw new ArgumentOutOfRangeException(nameof(streakSide))
        };
        return Direction == StrategyDirection.Follow ? same
            : same == BetSide.Banker ? BetSide.Player : BetSide.Banker;
    }

    [JsonIgnore]
    public string Summary => $"{TriggerText(TriggerSide)}连续 {StreakLength} 次，{DirectionText(Direction)}，金额 {string.Join(" → ", Stakes.Select(value => value.ToString("0")))}";

    private static string TriggerText(StrategyTriggerSide side) => side switch
    {
        StrategyTriggerSide.BankerOnly => "庄",
        StrategyTriggerSide.PlayerOnly => "闲",
        _ => "庄或闲"
    };

    private static string DirectionText(StrategyDirection direction) => direction == StrategyDirection.Follow ? "顺向下注" : "反向下注";
}

public static class FixedStrategy
{
    public const string Id = "six-opposite-10-20-40-v1";
    public const int StreakLength = 6;
    public static readonly decimal[] Stakes = [10m, 20m, 40m];

    public static BetSide Opposite(BaccaratOutcome outcome) => outcome switch
    {
        BaccaratOutcome.Banker => BetSide.Player,
        BaccaratOutcome.Player => BetSide.Banker,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };

    public static BaccaratOutcome ToOutcome(BetSide side) => side == BetSide.Banker
        ? BaccaratOutcome.Banker : BaccaratOutcome.Player;
}

public readonly record struct LatestRun(BaccaratOutcome Side, int Count, int StartIndex, int LastDecisiveIndex)
{
    public static LatestRun From(IReadOnlyList<int> history)
    {
        int last = history.Count - 1;
        while (last >= 0 && Normalize(history[last]) == BaccaratOutcome.Tie) last--;
        if (last < 0) return new(BaccaratOutcome.None, 0, -1, -1);

        BaccaratOutcome side = Normalize(history[last]);
        if (side is not (BaccaratOutcome.Banker or BaccaratOutcome.Player))
            return new(BaccaratOutcome.None, 0, -1, -1);

        int count = 0;
        int start = last;
        for (int i = last; i >= 0; i--)
        {
            BaccaratOutcome value = Normalize(history[i]);
            if (value == BaccaratOutcome.Tie) continue;
            if (value != side) break;
            count++;
            start = i;
        }
        return new(side, count, start, last);
    }

    public static BaccaratOutcome Normalize(int value) => (value & 0x03) switch
    {
        1 => BaccaratOutcome.Banker,
        2 => BaccaratOutcome.Player,
        3 => BaccaratOutcome.Tie,
        _ => BaccaratOutcome.None
    };
}
