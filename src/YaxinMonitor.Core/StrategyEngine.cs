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
            if (table.ActiveChase is { Status: ChaseStatus.AwaitingSettlement })
                DeferSettlement(table, now, events, "切换牌靴前未观察到开奖结果");
            if (table.ActiveChase is { Status: ChaseStatus.AwaitingAcceptance or ChaseStatus.Unknown })
                throw new InvalidDataException($"桌台 {snapshot.TableId} 在订单尚未完成时切换牌靴。请核对订单和结算记录。");
            table.ShoeSeq = snapshot.ShoeSeq;
            table.LastHistoryCount = 0;
            table.LockedTaskKeys.Clear();
            table.ActiveChase = null;
        }
        else if (snapshot.History.Length < table.LastHistoryCount)
        {
            if (snapshot.State is "S" or "RP" && table.ActiveChase is { Status: ChaseStatus.AwaitingSettlement })
                DeferSettlement(table, now, events, "洗牌前未观察到开奖结果");
            if (snapshot.State is "S" or "RP" && table.ActiveChase is not { Status: ChaseStatus.AwaitingAcceptance or ChaseStatus.Unknown })
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

        IReadOnlyList<ResultRun> runs = PatternDetector.RecentRuns(snapshot.History);
        bool newestResultIsDecisive = snapshot.History.Length > 0
            && LatestRun.Normalize(snapshot.History[^1]) is BaccaratOutcome.Banker or BaccaratOutcome.Player;
        bool sawNewHistory = snapshot.History.Length > table.LastHistoryCount;

        if (newestResultIsDecisive && runs.Count > 0 && _strategy.Matches(runs[0].Side)
            && (sawNewHistory || table.LastHistoryCount == 0))
        {
            BaccaratOutcome lastSide = runs[0].Side;
            foreach (StrategyPattern pattern in _strategy.OrderedPatterns)
            {
                if (PatternDetector.Match(runs, pattern, _strategy.StreakLength) is not { } anchor) continue;
                string taskKey = BuildTaskKey(snapshot, pattern, anchor);
                // One chase per table: a pattern seen while the table is busy is consumed, never queued.
                if (!table.LockedTaskKeys.Add(taskKey) || table.ActiveChase is not null) continue;
                table.ActiveChase = new ChaseTaskState
                {
                    TaskKey = taskKey,
                    StrategyId = _strategy.Id,
                    Pattern = pattern,
                    StreakSide = lastSide,
                    BetSide = _strategy.SelectBetSide(lastSide)
                };
                events.Add(new SignalEvent(snapshot.TableId,
                    $"{snapshot.TableName} 最新形成{_strategy.PatternText(pattern)}（末口{SideText(lastSide)}），准备买{SideText(table.ActiveChase.BetSide)}。", taskKey));
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
            order.CountedInDailyStake = true;
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

    public void ResolveOrder(string orderKey, ManualOrderResolution resolution, DateTimeOffset now)
    {
        OrderState order = RequireOrder(orderKey);
        bool isUnknown = order.Status == "Unknown";
        bool isDeferredSettlement = order.Status == "SettlementPending";
        if (!isUnknown && !isDeferredSettlement)
            throw new InvalidOperationException("只能人工处理状态不明或待外部结算的订单。");
        if (isDeferredSettlement && resolution != ManualOrderResolution.ConfirmedSettled)
            throw new InvalidOperationException("已受理订单只能确认已结算。");

        order.Status = resolution switch
        {
            ManualOrderResolution.ConfirmedNotPlaced => "ManuallyConfirmedNotPlaced",
            ManualOrderResolution.ConfirmedSettled => "ManuallyConfirmedSettled",
            _ => throw new ArgumentOutOfRangeException(nameof(resolution))
        };
        order.UpdatedAt = now;

        if (State.Tables.TryGetValue(order.TableId, out TableRuntimeState? table)
            && table.ActiveChase is { Status: ChaseStatus.Unknown } chase
            && chase.PendingOrderKey == order.OrderKey)
        {
            table.ActiveChase = null;
            table.LastHistoryCount = 0;
        }
    }

    public decimal ReservedStake => State.Orders.Values
        .Where(order => order.Status is "Submitted" or "Accepted" or "Unknown" or "SettlementPending")
        .Sum(order => order.Amount);

    public decimal UncertainStake => State.Orders.Values
        .Where(order => order.Status == "Unknown" && !order.CountedInDailyStake)
        .Sum(order => order.Amount);

    private void SettlePending(TableRuntimeState table, TableSnapshot snapshot, DateTimeOffset now, List<EngineEvent> events)
    {
        ChaseTaskState? chase = table.ActiveChase;
        if (chase is not { Status: ChaseStatus.AwaitingSettlement, PendingOrderKey: not null }) return;
        if (snapshot.History.Length <= chase.BetHistoryCount) return;

        BaccaratOutcome outcome = LatestRun.Normalize(snapshot.History[chase.BetHistoryCount]);
        if (outcome == BaccaratOutcome.None) throw new InvalidDataException("结算结果不是有效的庄、闲或和。停止自动下注。");

        SettleChase(table, chase, RequireOrder(chase.PendingOrderKey), outcome, snapshot.TableName, now, events, "");
    }

    /// <summary>
    /// Settles the player's own order from the site's bet-result push. The push proves the bet was placed, so it
    /// also resolves orders whose ack never arrived, and it settles orders that were deferred because the shoe
    /// ended before the road showed the result.
    /// </summary>
    public IReadOnlyList<EngineEvent> ApplySiteResult(long tableId, string tableName, long shoeSeq, long gameSeq,
        BaccaratOutcome outcome, DateTimeOffset now)
    {
        var events = new List<EngineEvent>();
        if (outcome is not (BaccaratOutcome.Banker or BaccaratOutcome.Player or BaccaratOutcome.Tie)) return events;
        OrderState? order = State.Orders.Values.FirstOrDefault(value => !value.IsSimulation
            && value.TableId == tableId && value.ShoeSeq == shoeSeq && value.GameSeq == gameSeq
            && value.Status is "Submitted" or "Accepted" or "Unknown" or "SettlementPending");
        if (order is null) return events;

        State.Tables.TryGetValue(tableId, out TableRuntimeState? table);
        ChaseTaskState? chase = table?.ActiveChase is { } active && active.PendingOrderKey == order.OrderKey ? active : null;
        string note = "";
        if (order.Status is "Submitted" or "Unknown")
        {
            note = order.Status == "Unknown" ? "（未收到下注回执，已按网站结算推送确认下注成功）" : "";
            if (chase is not null) MarkAccepted(order.OrderKey, now);
            else CountAccepted(order, now);
        }
        else if (order.Status == "SettlementPending")
        {
            note = "（自动对账）";
        }

        if (chase is { Status: ChaseStatus.AwaitingSettlement } && table is not null)
        {
            SettleChase(table, chase, order, outcome, tableName, now, events, note);
            return events;
        }

        order.Status = "Settled";
        order.Outcome = outcome;
        order.UpdatedAt = now;
        events.Add(new SettlementEvent(tableId,
            $"{tableName} 第 {order.Attempt} 档结算为{SideText(outcome)}{note}。", order.OrderKey, outcome));
        return events;
    }

    private void SettleChase(TableRuntimeState table, ChaseTaskState chase, OrderState order, BaccaratOutcome outcome,
        string tableName, DateTimeOffset now, List<EngineEvent> events, string note)
    {
        order.Status = "Settled";
        order.Outcome = outcome;
        order.UpdatedAt = now;
        events.Add(new SettlementEvent(table.TableId,
            $"{tableName} 第 {order.Attempt} 档结算为{SideText(outcome)}{note}。", order.OrderKey, outcome));

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
            events.Add(new ChaseCompletedEvent(table.TableId, $"{tableName} 下注获胜，任务结束。", taskKey, true));
            return;
        }

        chase.AttemptIndex++;
        if (chase.AttemptIndex >= _strategy.Stakes.Length)
        {
            string taskKey = chase.TaskKey;
            table.ActiveChase = null;
            events.Add(new ChaseCompletedEvent(table.TableId, $"{tableName} {_strategy.Stakes.Length} 档均失败，任务结束。", taskKey, false));
        }
        else
        {
            ClearPending(chase);
        }
    }

    private void CountAccepted(OrderState order, DateTimeOffset now)
    {
        order.ResponseCode = 0;
        if (order.CountedInDailyStake) return;
        RollAccountingDate(now);
        State.DailyAcceptedStake += order.Amount;
        order.CountedInDailyStake = true;
    }

    private void DeferSettlement(TableRuntimeState table, DateTimeOffset now, List<EngineEvent> events, string reason)
    {
        ChaseTaskState chase = table.ActiveChase
            ?? throw new InvalidOperationException("找不到待结算追注任务。");
        if (chase.PendingOrderKey is null) throw new InvalidOperationException("待结算订单键为空。");
        OrderState order = RequireOrder(chase.PendingOrderKey);
        order.Status = order.IsSimulation ? "SimulationUnresolved" : "SettlementPending";
        order.UpdatedAt = now;
        table.ActiveChase = null;
        events.Add(new SettlementDeferredEvent(table.TableId,
            order.IsSimulation
                ? $"桌台 {table.TableId} 的模拟订单{reason}，已结束该追注任务。"
                : $"桌台 {table.TableId} 的已受理订单{reason}，已挂起等待外部结算；该桌继续处理新订单。",
            order.OrderKey));
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

    // Streak keys keep the original format so locks saved before alternation modes existed stay valid.
    private string BuildTaskKey(TableSnapshot snapshot, StrategyPattern pattern, ResultRun anchor)
    {
        string key = string.Join(':',
            _strategy.Id, snapshot.TableId.ToString(CultureInfo.InvariantCulture), snapshot.ShoeSeq.ToString(CultureInfo.InvariantCulture),
            ((int)anchor.Side).ToString(CultureInfo.InvariantCulture), anchor.StartIndex.ToString(CultureInfo.InvariantCulture));
        return pattern == StrategyPattern.Streak ? key : key + ":" + pattern;
    }

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
        TableRuntimeState? blockedTable = State.Tables.Values.FirstOrDefault(table => table.ActiveChase is { } chase
            && HasDifferentStrategy(chase)
            && chase.Status is ChaseStatus.AwaitingAcceptance or ChaseStatus.AwaitingSettlement or ChaseStatus.Unknown);
        if (blockedTable?.ActiveChase is { } blocked)
        {
            string orderKey = string.IsNullOrWhiteSpace(blocked.PendingOrderKey) ? "未知" : blocked.PendingOrderKey;
            string detail = blocked.Status switch
            {
                ChaseStatus.AwaitingAcceptance => "正在等待网站确认，请等待确认完成后再切换策略",
                ChaseStatus.AwaitingSettlement => "正在等待开奖结果，请等待结算完成后再切换策略",
                ChaseStatus.Unknown => "状态不明，请停止监控，在网站订单记录中核对后点击“订单对账”",
                _ => "尚未完成"
            };
            throw new InvalidDataException($"桌台 {blockedTable.TableId} 的订单{detail}。订单键：{orderKey}");
        }

        foreach (TableRuntimeState table in State.Tables.Values)
        {
            ChaseTaskState? chase = table.ActiveChase;
            if (chase is null) continue;
            if (!HasDifferentStrategy(chase))
            {
                if (chase.AttemptIndex < 0 || chase.AttemptIndex >= _strategy.Stakes.Length
                    || !_strategy.Patterns.Contains(chase.Pattern)
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
