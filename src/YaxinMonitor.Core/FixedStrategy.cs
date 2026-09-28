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

public enum StrategyPattern
{
    Streak,
    SingleAlternation,
    DoubleAlternation,
    TripleAlternation
}

public sealed record StrategySettings
{
    public int StreakLength { get; init; } = FixedStrategy.StreakLength;
    public StrategyTriggerSide TriggerSide { get; init; } = StrategyTriggerSide.Both;
    public StrategyDirection Direction { get; init; } = StrategyDirection.Opposite;
    public decimal[] Stakes { get; init; } = [.. FixedStrategy.Stakes];
    public StrategyPattern[] Patterns { get; init; } = [StrategyPattern.Streak];

    [JsonIgnore]
    public IEnumerable<StrategyPattern> OrderedPatterns => Patterns.Order();

    [JsonIgnore]
    public string Id
    {
        get
        {
            Validate();
            // Streak-only settings keep their original ids so saved chases survive the upgrade.
            string patterns = Patterns is [StrategyPattern.Streak] ? ""
                : "-p" + string.Join('_', OrderedPatterns.Select(pattern => (int)pattern));
            if (patterns.Length == 0 && StreakLength == FixedStrategy.StreakLength && TriggerSide == StrategyTriggerSide.Both
                && Direction == StrategyDirection.Opposite && Stakes.SequenceEqual(FixedStrategy.Stakes))
                return FixedStrategy.Id;
            string amounts = string.Join('_', Stakes.Select(value => value.ToString("0", System.Globalization.CultureInfo.InvariantCulture)));
            return $"custom-v1-{StreakLength}-{(int)TriggerSide}-{(int)Direction}-{amounts}{patterns}";
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
        if (Patterns is null || Patterns.Length is < 1 or > 4 || Patterns.Any(pattern => !Enum.IsDefined(pattern))
            || Patterns.Distinct().Count() != Patterns.Length)
            throw new ArgumentException("请至少选择一种有效的识别模式，且不要重复选择。");
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
    public string Summary => $"识别 {string.Join(" + ", OrderedPatterns.Select(PatternText))}，末口{TriggerText(TriggerSide)}时触发，{DirectionText(Direction)}，金额 {string.Join(" → ", Stakes.Select(value => value.ToString("0")))}";

    public string PatternText(StrategyPattern pattern) => pattern switch
    {
        StrategyPattern.Streak => $"连续同色 {StreakLength} 口",
        StrategyPattern.SingleAlternation => "单口交替 6 口",
        StrategyPattern.DoubleAlternation => "两口交替 6 口",
        StrategyPattern.TripleAlternation => "三口交替 9 口",
        _ => pattern.ToString()
    };

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

public readonly record struct ResultRun(BaccaratOutcome Side, int Count, int StartIndex);

public static class PatternDetector
{
    // Decisive runs ordered newest first. Ties are skipped; an invalid value ends the scan.
    public static IReadOnlyList<ResultRun> RecentRuns(IReadOnlyList<int> history)
    {
        var runs = new List<ResultRun>();
        for (int i = history.Count - 1; i >= 0; i--)
        {
            BaccaratOutcome value = LatestRun.Normalize(history[i]);
            if (value == BaccaratOutcome.Tie) continue;
            if (value == BaccaratOutcome.None) break;
            if (runs.Count > 0 && runs[^1].Side == value)
                runs[^1] = runs[^1] with { Count = runs[^1].Count + 1, StartIndex = i };
            else
                runs.Add(new(value, 1, i));
        }
        return runs;
    }

    // Returns the run that anchors a match, or null. Alternation needs every run in the chain to have
    // exactly the block size, so a longer run is never trimmed to fit. The anchor is the first run of the
    // whole equal-sized chain, which stays stable while the pattern keeps extending.
    public static ResultRun? Match(IReadOnlyList<ResultRun> runs, StrategyPattern pattern, int streakLength)
    {
        if (runs.Count == 0) return null;
        if (pattern == StrategyPattern.Streak) return runs[0].Count == streakLength ? runs[0] : null;
        (int block, int required) = pattern switch
        {
            StrategyPattern.SingleAlternation => (1, 6),
            StrategyPattern.DoubleAlternation => (2, 3),
            StrategyPattern.TripleAlternation => (3, 3),
            _ => throw new ArgumentOutOfRangeException(nameof(pattern))
        };
        int chain = 0;
        while (chain < runs.Count && runs[chain].Count == block) chain++;
        return chain >= required ? runs[chain - 1] : null;
    }
}
