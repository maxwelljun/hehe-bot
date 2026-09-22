namespace YaxinMonitor.Core;

public enum BaccaratOutcome
{
    None = 0,
    Banker = 1,
    Player = 2,
    Tie = 3
}

public enum BetSide
{
    Banker,
    Player
}

public enum MonitorMode
{
    ReadOnly,
    Simulation,
    Live
}

public enum ChaseStatus
{
    Ready,
    AwaitingAcceptance,
    AwaitingSettlement,
    Unknown
}

public enum ManualOrderResolution
{
    ConfirmedNotPlaced,
    ConfirmedSettled
}

public sealed record TableSnapshot
{
    public long TableId { get; init; }
    public string TableName { get; init; } = "";
    public long ShoeSeq { get; init; }
    public long GameSeq { get; init; }
    public string State { get; init; } = "";
    public int RemainingMilliseconds { get; init; }
    public int[] History { get; init; } = [];
    public decimal? PlayerMin { get; init; }
    public decimal? PlayerMax { get; init; }
    public decimal? BankerMin { get; init; }
    public decimal? BankerMax { get; init; }
}

public sealed record BetCandidate(
    string OrderKey,
    string TaskKey,
    long TableId,
    string TableName,
    long ShoeSeq,
    long GameSeq,
    BetSide Side,
    decimal Amount,
    int Attempt,
    int HistoryCount,
    DateTimeOffset CreatedAt);

public abstract record EngineEvent(long TableId, string Message);
public sealed record SignalEvent(long Id, string Text, string TaskKey) : EngineEvent(Id, Text);
public sealed record BetRequestedEvent(long Id, string Text, BetCandidate Candidate) : EngineEvent(Id, Text);
public sealed record SettlementEvent(long Id, string Text, string OrderKey, BaccaratOutcome Outcome) : EngineEvent(Id, Text);
public sealed record SettlementDeferredEvent(long Id, string Text, string OrderKey) : EngineEvent(Id, Text);
public sealed record ChaseCompletedEvent(long Id, string Text, string TaskKey, bool Won) : EngineEvent(Id, Text);

public sealed class ChaseTaskState
{
    public string TaskKey { get; set; } = "";
    public string StrategyId { get; set; } = FixedStrategy.Id;
    public BaccaratOutcome StreakSide { get; set; }
    public BetSide BetSide { get; set; }
    public int AttemptIndex { get; set; }
    public ChaseStatus Status { get; set; } = ChaseStatus.Ready;
    public long PendingGameSeq { get; set; }
    public int BetHistoryCount { get; set; }
    public string? PendingOrderKey { get; set; }
    public HashSet<long> AttemptedGameSeqs { get; set; } = [];
}

public sealed class TableRuntimeState
{
    public long TableId { get; set; }
    public long ShoeSeq { get; set; }
    public int LastHistoryCount { get; set; }
    public HashSet<string> LockedTaskKeys { get; set; } = [];
    public ChaseTaskState? ActiveChase { get; set; }
}

public sealed class OrderState
{
    public string OrderKey { get; set; } = "";
    public string TaskKey { get; set; } = "";
    public long TableId { get; set; }
    public long ShoeSeq { get; set; }
    public long GameSeq { get; set; }
    public BetSide Side { get; set; }
    public decimal Amount { get; set; }
    public int Attempt { get; set; }
    public string Status { get; set; } = "Preparing";
    public bool IsSimulation { get; set; }
    public bool CountedInDailyStake { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public int? ResponseCode { get; set; }
    public BaccaratOutcome? Outcome { get; set; }
}

public sealed class EngineState
{
    public int Version { get; set; } = 1;
    public Dictionary<long, TableRuntimeState> Tables { get; set; } = [];
    public Dictionary<string, OrderState> Orders { get; set; } = [];
    public DateOnly AccountingDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public decimal DailyAcceptedStake { get; set; }
}
