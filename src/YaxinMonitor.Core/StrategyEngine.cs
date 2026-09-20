using System.Globalization;

namespace YaxinMonitor.Core;

public sealed class StrategyEngine
{
    private readonly StrategySettings _strategy;
    public EngineState State { get; }
    public int MinimumRemainingMilliseconds { get; }

    public StrategyEngine(EngineState state, int minimumRemainingMilliseconds = 8_000)
        : this(state, new StrategySettings(), minimumRemainingMilliseconds) { }

    public StrategyEngine(EngineState state, StrategySettings strategy, int minimumRemainingMilliseconds = 8_000)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        _strategy.Validate();
        MinimumRemainingMilliseconds = minimumRemainingMilliseconds is >= 0 and <= 120_000
            ? minimumRemainingMilliseconds : throw new ArgumentOutOfRangeException(nameof(minimumRemainingMilliseconds));
        ReconcileStrategyState();
    }

    public IReadOnlyList<EngineEvent> Observe(TableSnapshot snapshot, DateTimeOffset now)
    {
        ValidateSnapshot(snapshot);
        var events = new List<EngineEvent>();
        TableRuntimeState table = GetTable(snapshot);

        if (table.ShoeSeq != snapshot.ShoeSeq)
        {
            if (table.ActiveChase is { Status: ChaseStatus.AwaitingAcceptance or ChaseStatus.AwaitingSettlement or ChaseStatus.Unknown })
                throw new InvalidDataException($"桌台 {snapshot.TableId} 在订单尚未完成时切换牌靴。请核对订单和结算记录。");
            table.ShoeSeq = snapshot.ShoeSeq;
            table.LastHistoryCount = 0;
            table.LockedTaskKeys.Clear();
            table.ActiveChase = null;
        }
        else if (snapshot.History.Length < table.LastHistoryCount)
        {
            if (snapshot.State is "S" or "RP" && table.ActiveChase is not { Status: ChaseStatus.AwaitingAcceptance or ChaseStatus.AwaitingSettlement or ChaseStatus.Unknown })
            {
                table.LastHistoryCount = 0;
                table.LockedTaskKeys.Clear();
                table.ActiveChase = null;
            }
            else
            {
                throw new InvalidDataException($"桌台 {snapshot.TableId} 在同一牌靴中的历史记录倒退。请重新同步全桌快照。");
            }
        }

        SettlePending(table, snapshot, now, events);

        LatestRun run = LatestRun.From(snapshot.History);
        bool newestResultIsDecisive = snapshot.History.Length > 0
            && LatestRun.Normalize(snapshot.History[^1]) is BaccaratOutcome.Banker or BaccaratOutcome.Player;
        bool sawNewHistory = snapshot.History.Length > table.LastHistoryCount;

        if (table.ActiveChase is null && newestResultIsDecisive && run.Count == _strategy.StreakLength && _strategy.Matches(run.Side)
            && (sawNewHistory || table.LastHistoryCount == 0))
        {
            string taskKey = BuildTaskKey(snapshot, run);
            if (table.LockedTaskKeys.Add(taskKey))
            {
                table.ActiveChase = new ChaseTaskState
                {
                    TaskKey = taskKey,
                    StrategyId = _strategy.Id,
                    StreakSide = run.Side,
                    BetSide = _strategy.SelectBetSide(run.Side)
                };
                events.Add(new SignalEvent(snapshot.TableId,
                    $"{snapshot.TableName} 最新形成 {run.Count} 连{SideText(run.Side)}，准备买{SideText(table.ActiveChase.BetSide)}。", taskKey));
            }
        }

        if (table.ActiveChase is { Status: ChaseStatus.Ready } chase
            && string.Equals(snapshot.State, "A", StringComparison.Ordinal)
            && snapshot.RemainingMilliseconds >= MinimumRemainingMilliseconds
            && !chase.AttemptedGameSeqs.Contains(snapshot.GameSeq))
        {
            decimal amount = _strategy.Stakes[chase.AttemptIndex];
            string orderKey = BuildOrderKey(snapshot, chase);
            chase.AttemptedGameSeqs.Add(snapshot.GameSeq);
            var candidate = new BetCandidate(orderKey, chase.TaskKey, snapshot.TableId, snapshot.TableName,
                snapshot.ShoeSeq, snapshot.GameSeq, chase.BetSide, amount, chase.AttemptIndex + 1,
                snapshot.History.Length, now);
            events.Add(new BetRequestedEvent(snapshot.TableId,
                $"{snapshot.TableName} 第 {candidate.Attempt} 档买{SideText(candidate.Side)} {amount:0.##}。", candidate));
        }

        table.LastHistoryCount = snapshot.History.Length;
        return events;
    }

    public void MarkSubmitted(BetCandidate candidate, DateTimeOffset now)
    {
        ChaseTaskState chase = RequireCandidate(candidate);
        chase.Status = ChaseStatus.AwaitingAcceptance;
        chase.PendingGameSeq = candidate.GameSeq;
        chase.BetHistoryCount = candidate.HistoryCount;
        chase.PendingOrderKey = candidate.OrderKey;
        State.Orders[candidate.OrderKey] = new OrderState
        {
            OrderKey = candidate.OrderKey,
            TaskKey = candidate.TaskKey,
            TableId = candidate.TableId,
            ShoeSeq = candidate.ShoeSeq,
            GameSeq = candidate.GameSeq,
            Side = candidate.Side,
            Amount = candidate.Amount,
            Attempt = candidate.Attempt,
            Status = "Submitted",
            CreatedAt = candidate.CreatedAt,
            UpdatedAt = now
        };
    }

    public void MarkAccepted(string orderKey, DateTimeOffset now, bool simulation = false)
    {
        OrderState order = RequireOrder(orderKey);
        ChaseTaskState chase = RequirePending(order);
        order.Status = simulation ? "SimulatedAccepted" : "Accepted";
        order.IsSimulation = simulation;
        order.ResponseCode = 0;
        order.UpdatedAt = now;
        chase.Status = ChaseStatus.AwaitingSettlement;
        if (!simulation)
        {
            RollAccountingDate(now);
            State.DailyAcceptedStake += order.Amount;
        }
    }

    public void MarkRejected(string orderKey, int responseCode, DateTimeOffset now)
    {
        OrderState order = RequireOrder(orderKey);
        ChaseTaskState chase = RequirePending(order);
        order.Status = "Rejected";
        order.ResponseCode = responseCode;
        order.UpdatedAt = now;
        chase.Status = ChaseStatus.Ready;
        chase.PendingOrderKey = null;
        chase.PendingGameSeq = 0;
        chase.BetHistoryCount = 0;
    }

    public void MarkUnknown(string orderKey, DateTimeOffset now)
    {
        OrderState order = RequireOrder(orderKey);
        ChaseTaskState chase = RequirePending(order);
        order.Status = "Unknown";
        order.UpdatedAt = now;
        chase.Status = ChaseStatus.Unknown;
    }

    public decimal ReservedStake => State.Orders.Values
        .Where(order => order.Status is "Submitted" or "Accepted")
        .Sum(order => order.Amount);

    private void SettlePending(TableRuntimeState table, TableSnapshot snapshot, DateTimeOffset now, List<EngineEvent> events)
    {
        ChaseTaskState? chase = table.ActiveChase;
        if (chase is not { Status: ChaseStatus.AwaitingSettlement, PendingOrderKey: not null }) return;
        if (snapshot.History.Length <= chase.BetHistoryCount) return;

        BaccaratOutcome outcome = LatestRun.Normalize(snapshot.History[chase.BetHistoryCount]);
        if (outcome == BaccaratOutcome.None) throw new InvalidDataException("结算结果不是有效的庄、闲或和。停止自动下注。");

        OrderState order = RequireOrder(chase.PendingOrderKey);
        order.Status = "Settled";
        order.Outcome = outcome;
        order.UpdatedAt = now;
        events.Add(new SettlementEvent(snapshot.TableId,
            $"{snapshot.TableName} 第 {order.Attempt} 档结算为{SideText(outcome)}。", order.OrderKey, outcome));

        if (outcome == BaccaratOutcome.Tie)
        {
            ClearPending(chase);
            return;
        }

        bool won = outcome == FixedStrategy.ToOutcome(chase.BetSide);
        if (won)
        {
            string taskKey = chase.TaskKey;
            table.ActiveChase = null;
            events.Add(new ChaseCompletedEvent(snapshot.TableId, $"{snapshot.TableName} 下注获胜，任务结束。", taskKey, true));
            return;
        }

        chase.AttemptIndex++;
        if (chase.AttemptIndex >= _strategy.Stakes.Length)
        {
            string taskKey = chase.TaskKey;
            table.ActiveChase = null;
            events.Add(new ChaseCompletedEvent(snapshot.TableId, $"{snapshot.TableName} {_strategy.Stakes.Length} 档均失败，任务结束。", taskKey, false));
        }
        else
        {
            ClearPending(chase);
        }
    }

    private static void ClearPending(ChaseTaskState chase)
    {
        chase.Status = ChaseStatus.Ready;
        chase.PendingOrderKey = null;
        chase.PendingGameSeq = 0;
        chase.BetHistoryCount = 0;
    }

    private TableRuntimeState GetTable(TableSnapshot snapshot)
    {
        if (!State.Tables.TryGetValue(snapshot.TableId, out TableRuntimeState? table))
        {
            table = new TableRuntimeState { TableId = snapshot.TableId, ShoeSeq = snapshot.ShoeSeq };
            State.Tables.Add(snapshot.TableId, table);
        }
        return table;
    }

    private ChaseTaskState RequireCandidate(BetCandidate candidate)
    {
        if (!State.Tables.TryGetValue(candidate.TableId, out TableRuntimeState? table)
            || table.ActiveChase is not { } chase || chase.TaskKey != candidate.TaskKey
            || chase.Status != ChaseStatus.Ready)
            throw new InvalidOperationException("下注候选已失效。");
        return chase;
    }

    private OrderState RequireOrder(string orderKey) => State.Orders.TryGetValue(orderKey, out OrderState? order)
        ? order : throw new InvalidOperationException("找不到订单状态。");

    private ChaseTaskState RequirePending(OrderState order)
    {
        if (!State.Tables.TryGetValue(order.TableId, out TableRuntimeState? table)
            || table.ActiveChase is not { } chase || chase.PendingOrderKey != order.OrderKey)
            throw new InvalidOperationException("订单不属于当前追注任务。");
        return chase;
    }

    private void RollAccountingDate(DateTimeOffset now)
    {
        DateOnly date = DateOnly.FromDateTime(now.LocalDateTime);
        if (State.AccountingDate == date) return;
        State.AccountingDate = date;
        State.DailyAcceptedStake = 0;
    }

    private string BuildTaskKey(TableSnapshot snapshot, LatestRun run) => string.Join(':',
        _strategy.Id, snapshot.TableId.ToString(CultureInfo.InvariantCulture), snapshot.ShoeSeq.ToString(CultureInfo.InvariantCulture),
        ((int)run.Side).ToString(CultureInfo.InvariantCulture), run.StartIndex.ToString(CultureInfo.InvariantCulture));

    private static string BuildOrderKey(TableSnapshot snapshot, ChaseTaskState chase) => string.Join(':', chase.TaskKey,
        snapshot.GameSeq.ToString(CultureInfo.InvariantCulture), ((int)chase.BetSide).ToString(CultureInfo.InvariantCulture),
        (chase.AttemptIndex + 1).ToString(CultureInfo.InvariantCulture));

    private static string SideText(BaccaratOutcome outcome) => outcome switch
    {
        BaccaratOutcome.Banker => "庄",
        BaccaratOutcome.Player => "闲",
        BaccaratOutcome.Tie => "和",
        _ => "未知"
    };

    private static string SideText(BetSide side) => side == BetSide.Banker ? "庄" : "闲";

    private void ReconcileStrategyState()
    {
        bool HasDifferentStrategy(ChaseTaskState chase) => !string.Equals(chase.StrategyId, _strategy.Id, StringComparison.Ordinal);
        if (State.Tables.Values.Select(table => table.ActiveChase).OfType<ChaseTaskState>()
            .Any(chase => HasDifferentStrategy(chase)
                && chase.Status is ChaseStatus.AwaitingAcceptance or ChaseStatus.AwaitingSettlement or ChaseStatus.Unknown))
            throw new InvalidDataException("存在由其他策略创建且尚未完成的订单，不能切换策略。");

        foreach (TableRuntimeState table in State.Tables.Values)
        {
            ChaseTaskState? chase = table.ActiveChase;
            if (chase is null) continue;
            if (!HasDifferentStrategy(chase))
            {
                if (chase.AttemptIndex < 0 || chase.AttemptIndex >= _strategy.Stakes.Length
                    || chase.BetSide != _strategy.SelectBetSide(chase.StreakSide))
                    throw new InvalidDataException("保存的追注任务与当前策略不一致。");
                continue;
            }
            table.ActiveChase = null;
            table.LastHistoryCount = 0;
        }
    }

    private static void ValidateSnapshot(TableSnapshot snapshot)
    {
        if (snapshot.TableId <= 0 || snapshot.ShoeSeq < 0 || snapshot.GameSeq < 0)
            throw new ArgumentException("桌台、牌靴或局号无效。", nameof(snapshot));
        if (snapshot.History is null || snapshot.History.Length > 1000)
            throw new ArgumentException("桌台历史记录无效。", nameof(snapshot));
        if (snapshot.RemainingMilliseconds < 0)
            throw new ArgumentException("桌台剩余时间无效。", nameof(snapshot));
    }
}
