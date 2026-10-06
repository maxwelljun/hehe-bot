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
    TripleAlternation,
    // 一拖二：庄闲闲庄闲闲（6 口），一拖三：庄闲闲闲庄闲闲闲（8 口）；庄闲互换同样有效。
    OneTwoAlternation,
    OneThreeAlternation
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
        if (Patterns is null || Patterns.Length is < 1 or > 6 || Patterns.Any(pattern => !Enum.IsDefined(pattern))
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
        StrategyPattern.SingleAlternation => "单跳 6 口",
        StrategyPattern.DoubleAlternation => "二排 6 口",
        StrategyPattern.TripleAlternation => "三排 9 口",
        StrategyPattern.OneTwoAlternation => "一拖二 6 口",
        StrategyPattern.OneThreeAlternation => "一拖三 8 口",
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

    // Decisive hands of the pattern currently forming at the end of the history. For alternation the newest
    // run may still be shorter than the block (in progress); every older run in the chain must be exact.
    public static int Progress(IReadOnlyList<ResultRun> runs, StrategyPattern pattern)
    {
        if (runs.Count == 0) return 0;
        if (LongSide(pattern) is int length)
            return Math.Max(CycleProgress(runs, length, true), CycleProgress(runs, length, false));
        int block = BlockSize(pattern);
        if (block == 0) return runs[0].Count;
        if (runs[0].Count > block) return 0;
        int hands = runs[0].Count;
        for (int i = 1; i < runs.Count && runs[i].Count == block; i++) hands += block;
        return hands;
    }

    public static int RequiredHands(StrategyPattern pattern, int streakLength) => pattern switch
    {
        StrategyPattern.Streak => streakLength,
        StrategyPattern.TripleAlternation => 9,
        StrategyPattern.OneThreeAlternation => 8,
        _ => 6
    };

    // 一拖 N 图案中较长那一段的口数；其他模式返回 null。
    private static int? LongSide(StrategyPattern pattern) => pattern switch
    {
        StrategyPattern.OneTwoAlternation => 2,
        StrategyPattern.OneThreeAlternation => 3,
        _ => null
    };

    // 从最新一段往前，按“长段、单口、长段、单口……”交替计数，返回连续符合的段数。
    // newestIsLong=false 时最新一段对应单口。最新一段允许尚未走完（只要不超过应有口数）。
    private static int CycleChain(IReadOnlyList<ResultRun> runs, int length, bool newestIsLong, bool allowPartialNewest)
    {
        int chain = 0;
        while (chain < runs.Count)
        {
            bool isLong = (chain % 2 == 0) == newestIsLong;
            int expected = isLong ? length : 1;
            int count = runs[chain].Count;
            if (count != expected && !(chain == 0 && allowPartialNewest && count < expected)) break;
            chain++;
        }
        // 图案从单口开始，最老一段如果是长段就不算在内（前面缺了那一口单口）。
        if (chain > 0 && ((chain - 1) % 2 == 0) == newestIsLong) chain--;
        return chain;
    }

    private static int CycleProgress(IReadOnlyList<ResultRun> runs, int length, bool newestIsLong)
    {
        int chain = CycleChain(runs, length, newestIsLong, allowPartialNewest: true);
        int hands = 0;
        for (int i = 0; i < chain; i++) hands += runs[i].Count;
        return hands;
    }

    private static int BlockSize(StrategyPattern pattern) => pattern switch
    {
        StrategyPattern.Streak => 0,
        StrategyPattern.SingleAlternation => 1,
        StrategyPattern.DoubleAlternation => 2,
        StrategyPattern.TripleAlternation => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(pattern))
    };

    // Returns the run that anchors a match, or null. Alternation needs every run in the chain to have
    // exactly the block size, so a longer run is never trimmed to fit. The anchor is the first run of the
    // whole equal-sized chain, which stays stable while the pattern keeps extending.
    public static ResultRun? Match(IReadOnlyList<ResultRun> runs, StrategyPattern pattern, int streakLength)
    {
        if (runs.Count == 0) return null;
        if (pattern == StrategyPattern.Streak) return runs[0].Count == streakLength ? runs[0] : null;
        if (LongSide(pattern) is int length)
        {
            // 最新一段必须正好走完长段；锚定整条交替链最老的单口，图案继续延长时不重复触发。
            int cycle = CycleChain(runs, length, newestIsLong: true, allowPartialNewest: false);
            return cycle >= 4 ? runs[cycle - 1] : null;
        }
        int block = BlockSize(pattern);
        int required = RequiredHands(pattern, streakLength) / block;
        int chain = 0;
        while (chain < runs.Count && runs[chain].Count == block) chain++;
        return chain >= required ? runs[chain - 1] : null;
    }
}
