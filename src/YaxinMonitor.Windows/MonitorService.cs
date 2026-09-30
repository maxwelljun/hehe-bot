using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using YaxinMonitor.Core;

namespace YaxinMonitor.Windows;

internal sealed record TableViewState(
    long TableId, string Name, string State, long ShoeSeq, long GameSeq,
    string LatestRun, int LatestRunCount, string Chase, int RemainingSeconds, double Closeness = 0);

internal sealed record ServiceSnapshot(
    bool Connected, bool LoggedIn, string Bundle, decimal Balance,
    decimal DailyStake, decimal ReservedStake, bool OrdersPaused,
    IReadOnlyList<TableViewState> Tables);

internal sealed record ReconciliationOrderView(
    string OrderKey, long TableId, BetSide Side, decimal Amount, int Attempt, DateTimeOffset CreatedAt, string Status);

internal sealed class MonitorService : IAsyncDisposable
{
    private readonly StateStore _store;
    private readonly AcceptedNotifier _notifier = new();
    private readonly ConcurrentQueue<BetCandidate> _candidates = new();
    private readonly ConcurrentDictionary<long, string> _quarantinedTables = new();
    // Orders sent over the current page bridge, so a missing bet-result push can be trusted as "not placed".
    private readonly Dictionary<string, long> _orderBridgeConnection = [];
    private readonly Dictionary<string, DateTimeOffset> _unknownRoundPassedAt = [];
    private long _bridgeConnection;
    // 已结束订单在状态文件中只保留这么久，完整记录在每日订单日志里。
    private static readonly TimeSpan FinishedOrderRetention = TimeSpan.FromHours(24);
    private readonly object _engineLock = new();
    private volatile int _pendingReconciliation;
    private readonly object _settingsLock = new();
    private YaxinSettings _settings;
    private StrategyEngine _engine;
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private volatile bool _ordersPaused = true;
    private volatile bool _bettingCompatible;
    private long _nextSubmitTimestamp;
    private string? _reportedBundle;
    private string? _reportedCompatibilityIssue;
    private static readonly TimeSpan TodayTurnoverInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan WeekTurnoverInterval = TimeSpan.FromMinutes(5);
    private volatile BridgeTurnover? _siteTurnover;

    public event Action<string>? LogReceived;
    public event Action<string>? StatusChanged;
    public event Action<ServiceSnapshot>? SnapshotChanged;

    public MonitorService(StateStore store, YaxinSettings settings)
    {
        _store = store;
        _settings = settings;
        EngineState state = store.LoadState();
        _engine = new StrategyEngine(state, settings.Strategy, settings.MinimumRemainingMilliseconds);
        if (_engine.PruneOrders(DateTimeOffset.Now, FinishedOrderRetention)) store.SaveState(state);
        _pendingReconciliation = CountReconciliationOrders();
        if (state.Orders.Values.All(order => order.Status != "Unknown") && settings.Mode != MonitorMode.Live)
            _ordersPaused = false;
    }

    public bool IsRunning => _worker is { IsCompleted: false };

    /// <summary>网站投注记录接口给出的今日 / 本周汇总，不受本地状态重置影响。</summary>
    public BridgeTurnover? SiteTurnover => _siteTurnover;
    public bool OrdersPaused => _ordersPaused;
    /// <summary>需要人工对账的订单数，界面据此启用“订单对账”按钮；读取不加锁。</summary>
    public int PendingReconciliationCount => _pendingReconciliation;

    // 调用方须持有 _engineLock。每次状态变化都经这里写盘，同时刷新待对账数量。
    private void Persist()
    {
        _pendingReconciliation = CountReconciliationOrders();
        _store.SaveState(_engine.State);
    }

    private int CountReconciliationOrders() =>
        _engine.State.Orders.Values.Count(order => order.Status is "Unknown" or "SettlementPending");

    public IReadOnlyList<ReconciliationOrderView> GetReconciliationOrders()
    {
        lock (_engineLock)
            return _engine.State.Orders.Values
                .Where(order => order.Status is "Unknown" or "SettlementPending")
                .OrderBy(order => order.CreatedAt)
                .Select(order => new ReconciliationOrderView(order.OrderKey, order.TableId, order.Side,
                    order.Amount, order.Attempt, order.CreatedAt, order.Status))
                .ToArray();
    }

    public void ResolveOrder(string orderKey, ManualOrderResolution resolution)
    {
        OrderState order;
        bool tableCanResume;
        lock (_engineLock)
        {
            _engine.ResolveOrder(orderKey, resolution, DateTimeOffset.Now);
            order = _engine.State.Orders[orderKey];
            tableCanResume = !_engine.State.Orders.Values.Any(candidate =>
                candidate.TableId == order.TableId && candidate.Status == "Unknown");
            Persist();
            _store.AppendOrder(order);
        }
        bool tableResumed = tableCanResume && _quarantinedTables.TryRemove(order.TableId, out _);
        string result = resolution == ManualOrderResolution.ConfirmedNotPlaced ? "人工确认未下注" : "人工确认已结算";
        WriteLog($"订单对账完成：桌台 {order.TableId}，{SideText(order.Side)} {order.Amount:0.##}，{result}。订单键：{order.OrderKey}");
        if (tableResumed) WriteLog($"桌台 {order.TableId} 已解除隔离，继续监控和处理新订单。");
    }

    public void UpdateSettings(YaxinSettings settings)
    {
        if (IsRunning) throw new InvalidOperationException("请先停止监控再修改设置。");
        settings.Validate();
        lock (_engineLock)
        {
            _engine = new StrategyEngine(_engine.State, settings.Strategy, settings.MinimumRemainingMilliseconds);
            Persist();
        }
        lock (_settingsLock) _settings = settings;
        if (settings.Mode == MonitorMode.Live) _ordersPaused = true;
        WriteLog("当前策略：" + settings.Strategy.Summary + "。");
    }

    public void ResetRuntimeState(YaxinSettings settings)
    {
        if (IsRunning) throw new InvalidOperationException("请先停止监控再重置运行状态。");
        settings.Validate();
        lock (_engineLock)
        {
            _engine = new StrategyEngine(new EngineState(), settings.Strategy, settings.MinimumRemainingMilliseconds);
            Persist();
        }
        lock (_settingsLock) _settings = settings;
        _candidates.Clear();
        _quarantinedTables.Clear();
        _ordersPaused = settings.Mode == MonitorMode.Live;
        WriteLog("已清空本地运行状态，将按网页最新全桌数据重新开始。");
        WriteLog("当前策略：" + settings.Strategy.Summary + "。");
    }

    public void ResumeOrders()
    {
        YaxinSettings settings;
        lock (_settingsLock) settings = _settings;
        if (settings.Mode == MonitorMode.Live)
        {
            settings.Validate();
            QuarantineUnknownTables();
            if (!_bettingCompatible)
                throw new InvalidOperationException("页面下注接口兼容检查尚未通过，不能恢复真实下注。");
        }
        _ordersPaused = false;
        WriteLog("已恢复自动下单。");
    }

    public void PauseOrders(string reason = "用户暂停")
    {
        _ordersPaused = true;
        _candidates.Clear();
        WriteLog("自动下单已暂停：" + reason);
    }

    public void Start(string chromeProfileDirectory)
    {
        if (IsRunning) return;
        YaxinSettings settings;
        lock (_settingsLock) settings = _settings;
        if (settings.Mode == MonitorMode.Live) _ordersPaused = true;
        _candidates.Clear();
        _quarantinedTables.Clear();
        _bettingCompatible = false;
        _reportedCompatibilityIssue = null;
        _cancellation = new CancellationTokenSource();
        _worker = RunAsync(chromeProfileDirectory, _cancellation.Token);
    }

    public async Task StopAsync()
    {
        if (_cancellation is null || _worker is null) return;
        _cancellation.Cancel();
        try { await _worker.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _cancellation.Dispose();
        _cancellation = null;
        _worker = null;
        StatusChanged?.Invoke("已停止");
    }

    private async Task RunAsync(string chromeProfileDirectory, CancellationToken cancellationToken)
    {
        YaxinSettings settings;
        lock (_settingsLock) settings = _settings;
        await ChromeLauncher.LaunchAsync(settings.ChromeDebugPort, chromeProfileDirectory, cancellationToken).ConfigureAwait(false);
        WriteLog("Chrome 已就绪；首次使用请在浏览器中手动登录。");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var adapter = new SiteRuntimeAdapter();
                _candidates.Clear();
                StatusChanged?.Invoke("正在连接页面...");
                await adapter.ConnectAsync(settings.ChromeDebugPort, cancellationToken).ConfigureAwait(false);
                lock (_engineLock) _bridgeConnection++;
                WriteLog("页面桥接已连接。");
                await PollLoopAsync(adapter, settings, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                if (settings.Mode == MonitorMode.Live) PauseOrders("页面连接异常");
                WriteLog("连接中断：" + exception.Message);
                StatusChanged?.Invoke("连接中断，2 秒后重试");
                await Task.Delay(2_000, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task PollLoopAsync(SiteRuntimeAdapter adapter, YaxinSettings settings, CancellationToken cancellationToken)
    {
        var turnoverClock = new TurnoverClock();
        while (!cancellationToken.IsCancellationRequested)
        {
            BridgePoll poll = await adapter.PollAsync(cancellationToken).ConfigureAwait(false);
            if (poll.Turnover is not null) _siteTurnover = poll.Turnover;
            if (poll.LoggedIn) await RequestTurnoverAsync(adapter, turnoverClock, cancellationToken).ConfigureAwait(false);
            HandleBridgeEvents(poll.Events, poll.Tables, settings);
            ResolveUnplacedUnknownOrders(poll);
            QuarantineUnknownTables();

            string? observationIssue = poll.BridgeVersion != 3
                ? $"页面桥接协议版本为 {poll.BridgeVersion}，程序要求版本 3"
                : !poll.ObservationCompatible ? "全桌读取接口不兼容：" + poll.CompatibilityError
                : null;
            string? bettingIssue = observationIssue is null && !poll.BettingCompatible
                ? "下注接口不兼容：" + poll.CompatibilityError : null;
            string? compatibilityIssue = observationIssue ?? bettingIssue;
            _bettingCompatible = poll.BridgeVersion == 3 && poll.BettingCompatible;
            if (compatibilityIssue is not null)
            {
                if (!string.Equals(_reportedCompatibilityIssue, compatibilityIssue, StringComparison.Ordinal))
                {
                    _reportedCompatibilityIssue = compatibilityIssue;
                    if (settings.Mode == MonitorMode.Live) PauseOrders("页面接口兼容检查未通过");
                    WriteLog(compatibilityIssue + "。自动下注已禁用，请升级程序。");
                }
                StatusChanged?.Invoke("页面接口不兼容 · 自动下注已禁用");
                if (observationIssue is not null)
                {
                    PublishSnapshot(poll, settings);
                    await Task.Delay(1_000, cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }
            else if (_reportedCompatibilityIssue is not null)
            {
                _reportedCompatibilityIssue = null;
                WriteLog("页面运行时兼容检查已恢复正常；自动下单仍保持暂停，请核对后手动恢复。");
            }
            if (!string.Equals(_reportedBundle, poll.Bundle, StringComparison.Ordinal))
            {
                _reportedBundle = poll.Bundle;
                WriteLog($"网站前端 {poll.Bundle} 运行时兼容检查通过。");
            }

            if (poll.Ready)
            {
                foreach (TableSnapshot table in poll.Tables.OrderBy(table => table.TableId))
                {
                    if (_quarantinedTables.ContainsKey(table.TableId)) continue;
                    try
                    {
                        lock (_engineLock)
                            HandleEngineEvents(_engine.Observe(table, DateTimeOffset.Now), settings);
                    }
                    catch (InvalidDataException exception)
                    {
                        QuarantineTable(table.TableId, exception.Message);
                    }
                }
                await DispatchOneAsync(adapter, poll, settings, cancellationToken).ConfigureAwait(false);
                CheckAckTimeout();
                string isolated = _quarantinedTables.IsEmpty ? "" : $" · 已隔离 {_quarantinedTables.Count} 桌";
                string compatibility = _bettingCompatible ? "" : " · 下注接口不兼容";
                StatusChanged?.Invoke($"已连接 · {poll.Tables.Length} 桌{isolated}{compatibility}");
            }
            else
            {
                string waiting = !poll.LoggedIn ? "等待网站登录信息..."
                    : poll.Tables.Length == 0 ? "已登录，等待全桌数据..."
                    : !poll.SocketConnected ? "已登录，等待游戏连接..."
                    : "等待网站数据就绪...";
                StatusChanged?.Invoke(waiting);
            }
            PublishSnapshot(poll, settings);
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class TurnoverClock
    {
        public DateTimeOffset Today = DateTimeOffset.MinValue;
        public DateTimeOffset Week = DateTimeOffset.MinValue;
    }

    private static async Task RequestTurnoverAsync(SiteRuntimeAdapter adapter, TurnoverClock clock, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        if (now - clock.Today < TodayTurnoverInterval) return;
        DateOnly day = SiteCalendar.BusinessDate(now);
        string end = SiteCalendar.Format(day.AddDays(1));
        var ranges = new List<object> { new { key = "today", start = SiteCalendar.Format(day), end } };
        bool week = now - clock.Week >= WeekTurnoverInterval;
        if (week) ranges.Add(new { key = "week", start = SiteCalendar.Format(SiteCalendar.WeekStart(day)), end });
        try
        {
            JsonElement result = await adapter.RequestTurnoverAsync(ranges, cancellationToken).ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("started", out JsonElement started) || !started.GetBoolean())
                return;
            clock.Today = now;
            if (week) clock.Week = now;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (InvalidOperationException) { clock.Today = now; }
    }

    private void HandleBridgeEvents(IEnumerable<BridgeEvent> events, IReadOnlyList<TableSnapshot> tables, YaxinSettings settings)
    {
        lock (_engineLock)
        {
            foreach (BridgeEvent item in events)
            {
                if (item.Type == "betResult")
                {
                    HandleBetResult(item, tables, settings);
                    continue;
                }
                if (item.Type != "betAck" || string.IsNullOrEmpty(item.OrderKey)) continue;
                if (!_engine.State.Orders.TryGetValue(item.OrderKey, out OrderState? order)
                    || order.Status is not ("Submitted" or "Unknown")) continue;
                bool lateConfirmation = order.Status == "Unknown";
                if (item.ErrorCode == 0)
                {
                    _engine.MarkAccepted(item.OrderKey, DateTimeOffset.Now);
                    _quarantinedTables.TryRemove(order.TableId, out _);
                    Persist();
                    _store.AppendOrder(order);
                    WriteLog($"下注已受理：桌台 {order.TableId}，{SideText(order.Side)} {order.Amount:0.##}，第 {order.Attempt} 档。");
                    if (lateConfirmation) WriteLog($"桌台 {order.TableId} 的迟到确认已恢复为等待结算状态。");
                    if (settings.PlayAcceptedSound) _notifier.NotifyAccepted(order);
                }
                else
                {
                    _engine.MarkRejected(item.OrderKey, item.ErrorCode, DateTimeOffset.Now);
                    _quarantinedTables.TryRemove(order.TableId, out _);
                    Persist();
                    _store.AppendOrder(order);
                    string detail = string.IsNullOrWhiteSpace(item.ErrorMessage) ? "" : $"：{item.ErrorMessage}";
                    WriteLog($"下注被拒绝：桌台 {order.TableId}，错误码 {item.ErrorCode}{detail}。");
                    if (item.ErrorCode == -140) PauseOrders("网站要求验证码");
                    if (item.ErrorCode == -96)
                    {
                        DelaySubmissions(TimeSpan.FromSeconds(10));
                        WriteLog("网站限制投注频率，新的订单延迟 10 秒发送。");
                    }
                }
            }
        }
    }

    private void HandleBetResult(BridgeEvent item, IReadOnlyList<TableSnapshot> tables, YaxinSettings settings)
    {
        BaccaratOutcome outcome = item.Result switch
        {
            "BANKER" => BaccaratOutcome.Banker,
            "PLAYER" => BaccaratOutcome.Player,
            "TIE" => BaccaratOutcome.Tie,
            _ => BaccaratOutcome.None
        };
        if (outcome == BaccaratOutcome.None) return;
        string name = tables.FirstOrDefault(table => table.TableId == item.TableId)?.TableName ?? $"桌台 {item.TableId}";
        HandleEngineEvents(_engine.ApplySiteResult(item.TableId, name, item.ShoeSeq, item.GameSeq, outcome, DateTimeOffset.Now), settings);
        if (!_engine.State.Orders.Values.Any(order => order.TableId == item.TableId && order.Status == "Unknown")
            && _quarantinedTables.TryRemove(item.TableId, out _))
            WriteLog($"桌台 {item.TableId} 已按网站结算推送解除隔离，继续监控和处理新订单。");
    }

    // An order whose ack timed out is treated as not placed once its round has been drawn and the site pushed no
    // bet result for it. Only orders sent over the current bridge qualify: a reconnect may have dropped the push.
    private void ResolveUnplacedUnknownOrders(BridgePoll poll)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        var resolvedTables = new List<long>();
        lock (_engineLock)
        {
            foreach (OrderState order in _engine.State.Orders.Values.Where(order => order.Status == "Unknown" && !order.IsSimulation).ToArray())
            {
                if (!_orderBridgeConnection.TryGetValue(order.OrderKey, out long connection) || connection != _bridgeConnection) continue;
                TableSnapshot? table = poll.Tables.FirstOrDefault(value => value.TableId == order.TableId);
                if (table is null || (table.ShoeSeq == order.ShoeSeq && table.GameSeq <= order.GameSeq)) continue;
                if (!_unknownRoundPassedAt.TryGetValue(order.OrderKey, out DateTimeOffset passedAt))
                {
                    _unknownRoundPassedAt[order.OrderKey] = now;
                    continue;
                }
                if (now - passedAt < TimeSpan.FromSeconds(10)) continue;
                try
                {
                    _engine.MarkRejected(order.OrderKey, -9002, now);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }
                _unknownRoundPassedAt.Remove(order.OrderKey);
                Persist();
                _store.AppendOrder(order);
                WriteLog($"订单自动对账：桌台 {order.TableId}，{SideText(order.Side)} {order.Amount:0.##}，该局已开奖但网站没有推送本单结算，判定未下注，追注按原档继续。");
                if (!_engine.State.Orders.Values.Any(value => value.TableId == order.TableId && value.Status == "Unknown"))
                    resolvedTables.Add(order.TableId);
            }
        }
        foreach (long tableId in resolvedTables)
            if (_quarantinedTables.TryRemove(tableId, out _))
                WriteLog($"桌台 {tableId} 已解除隔离，继续监控和处理新订单。");
    }

    private void HandleEngineEvents(IReadOnlyList<EngineEvent> events, YaxinSettings settings)
    {
        bool changed = false;
        foreach (EngineEvent item in events)
        {
            WriteLog(item.Message);
            changed = true;
            if (item is BetRequestedEvent requested)
            {
                if (settings.Mode == MonitorMode.Live)
                {
                    if (_ordersPaused) WriteLog($"自动下单已暂停，本局不发送：{requested.Candidate.TableName}。");
                    else _candidates.Enqueue(requested.Candidate);
                }
                else
                {
                    _engine.MarkSubmitted(requested.Candidate, DateTimeOffset.Now);
                    _engine.MarkAccepted(requested.Candidate.OrderKey, DateTimeOffset.Now, simulation: true);
                    string prefix = settings.Mode == MonitorMode.ReadOnly ? "只读推演" : "模拟受理";
                    WriteLog($"{prefix}：{requested.Candidate.TableName} {SideText(requested.Candidate.Side)} {requested.Candidate.Amount:0.##}。");
                }
            }
            if (item is SettlementEvent settled && _engine.State.Orders.TryGetValue(settled.OrderKey, out OrderState? order))
            {
                _store.AppendOrder(order);
                if (settings.PlayAcceptedSound && !order.IsSimulation && settled.Outcome != BaccaratOutcome.Tie)
                    _notifier.NotifySettlement(order, settled.Outcome == FixedStrategy.ToOutcome(order.Side));
            }
            if (item is SettlementDeferredEvent deferred
                && _engine.State.Orders.TryGetValue(deferred.OrderKey, out OrderState? deferredOrder))
                _store.AppendOrder(deferredOrder);
        }
        if (!changed) return;
        _engine.PruneOrders(DateTimeOffset.Now, FinishedOrderRetention);
        Persist();
    }

    private async Task DispatchOneAsync(SiteRuntimeAdapter adapter, BridgePoll poll, YaxinSettings settings, CancellationToken cancellationToken)
    {
        if (settings.Mode != MonitorMode.Live || _ordersPaused) return;
        if (Stopwatch.GetTimestamp() < _nextSubmitTimestamp) return;
        BetCandidate candidate;
        string? validationError;
        lock (_engineLock)
        {
            if (_engine.State.Orders.Values.Any(order => order.Status == "Submitted")) return;
            if (!_candidates.TryDequeue(out candidate!)) return;

            TableSnapshot? table = poll.Tables.FirstOrDefault(value => value.TableId == candidate.TableId);
            validationError = ValidateCandidate(candidate, table, poll.Balance, settings);
            _engine.MarkSubmitted(candidate, DateTimeOffset.Now);
            _orderBridgeConnection[candidate.OrderKey] = _bridgeConnection;
            if (_orderBridgeConnection.Count > 200)
                foreach (string key in _orderBridgeConnection.Keys.Where(key => !_engine.State.Orders.TryGetValue(key, out OrderState? order)
                    || order.Status is not ("Submitted" or "Unknown")).ToArray())
                {
                    _orderBridgeConnection.Remove(key);
                    _unknownRoundPassedAt.Remove(key);
                }
            if (validationError is not null)
                _engine.MarkRejected(candidate.OrderKey, -9001, DateTimeOffset.Now);
            Persist();
        }
        if (validationError is not null)
        {
            WriteLog("跳过下注：" + validationError);
            return;
        }

        BridgeSubmitResult result;
        try
        {
            result = await adapter.SubmitAsync(candidate, settings.MinimumRemainingMilliseconds, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DelaySubmissions(TimeSpan.FromSeconds(3));
        }
        if (!result.Submitted)
        {
            lock (_engineLock)
            {
                _engine.MarkRejected(candidate.OrderKey, -9000, DateTimeOffset.Now);
                Persist();
            }
            WriteLog("桥接未发送：" + result.Error);
        }
        else
        {
            WriteLog($"订单已发送，等待网站确认：{candidate.TableName} {SideText(candidate.Side)} {candidate.Amount:0.##}。");
        }
    }

    private string? ValidateCandidate(BetCandidate candidate, TableSnapshot? table, decimal balance, YaxinSettings settings)
    {
        if (table is null) return "桌台已从全桌快照消失。";
        if (table.GameSeq != candidate.GameSeq || table.ShoeSeq != candidate.ShoeSeq) return "目标局号或牌靴已变化。";
        if (table.State != "A" || table.RemainingMilliseconds < settings.MinimumRemainingMilliseconds) return "下注窗口已关闭或时间不足。";
        decimal? minimum = candidate.Side == BetSide.Player ? table.PlayerMin : table.BankerMin;
        decimal? maximum = candidate.Side == BetSide.Player ? table.PlayerMax : table.BankerMax;
        if (minimum is null || maximum is null || candidate.Amount < minimum || candidate.Amount > maximum) return "金额不符合桌台限额。";
        decimal reserved = _engine.ReservedStake;
        if (balance - reserved < candidate.Amount) return "可用余额不足。";
        if (reserved + candidate.Amount > settings.MaxReservedStake) return "达到同时在途金额上限。";
        decimal daily = _engine.State.AccountingDate == DateOnly.FromDateTime(DateTime.Now) ? _engine.State.DailyAcceptedStake : 0;
        if (daily + _engine.UncertainStake + candidate.Amount > settings.DailyStakeLimit) return "达到每日下注金额上限。";
        return null;
    }

    private void CheckAckTimeout()
    {
        OrderState? timedOut;
        lock (_engineLock)
        {
            timedOut = _engine.State.Orders.Values.FirstOrDefault(order => order.Status == "Submitted"
                && DateTimeOffset.Now - (order.UpdatedAt ?? order.CreatedAt) > TimeSpan.FromSeconds(8));
            if (timedOut is null) return;
            _engine.MarkUnknown(timedOut.OrderKey, DateTimeOffset.Now);
            Persist();
            _store.AppendOrder(timedOut);
        }
        if (_quarantinedTables.TryAdd(timedOut.TableId, "订单确认超时，状态不明"))
            WriteLog($"桌台 {timedOut.TableId} 已隔离：订单确认超时，其他桌台继续处理新订单。");
    }

    private void QuarantineTable(long tableId, string reason)
    {
        lock (_engineLock)
        {
            if (_engine.State.Tables.TryGetValue(tableId, out TableRuntimeState? table)
                && table.ActiveChase is { Status: ChaseStatus.AwaitingAcceptance or ChaseStatus.AwaitingSettlement, PendingOrderKey: not null } chase)
            {
                _engine.MarkUnknown(chase.PendingOrderKey, DateTimeOffset.Now);
                OrderState order = _engine.State.Orders[chase.PendingOrderKey];
                Persist();
                _store.AppendOrder(order);
            }
        }

        if (!_quarantinedTables.TryAdd(tableId, reason)) return;
        WriteLog($"桌台 {tableId} 已隔离，其他桌台继续监控和处理新订单：{reason}");
    }

    private void QuarantineUnknownTables()
    {
        lock (_engineLock)
        {
            foreach (OrderState order in _engine.State.Orders.Values.Where(order => order.Status == "Unknown"))
                _quarantinedTables.TryAdd(order.TableId, "存在状态不明的订单");
        }
    }

    private void DelaySubmissions(TimeSpan delay)
    {
        long target = Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * Stopwatch.Frequency);
        if (target > _nextSubmitTimestamp) _nextSubmitTimestamp = target;
    }

    private void PublishSnapshot(BridgePoll poll, YaxinSettings settings)
    {
        TableViewState[] rows;
        decimal dailyStake;
        decimal reservedStake;
        lock (_engineLock)
        {
            // 有在途订单（已提交、未结算或状态不明）的桌台排在最前，其余仍按接近触发程度排序。
            var inFlight = _engine.State.Orders.Values
                .Where(order => order.Status is "Submitted" or "Accepted" or "Unknown" or "SettlementPending")
                .Select(order => order.TableId).ToHashSet();
            rows = poll.Tables.Select(table =>
            {
                IReadOnlyList<ResultRun> runs = PatternDetector.RecentRuns(table.History);
                _engine.State.Tables.TryGetValue(table.TableId, out TableRuntimeState? runtime);
                string chase = runtime?.ActiveChase is { } active
                    ? $"{settings.Strategy.PatternText(active.Pattern)} · 第 {active.AttemptIndex + 1} 档 / {active.Status}" : "-";
                (string latest, double closeness) = DescribeTrend(runs, settings.Strategy);
                return new TableViewState(table.TableId, table.TableName, table.State, table.ShoeSeq, table.GameSeq,
                    latest, runs.Count == 0 ? 0 : runs[0].Count, chase, table.RemainingMilliseconds / 1000, closeness);
            }).OrderByDescending(row => inFlight.Contains(row.TableId)).ThenByDescending(row => row.Closeness).ThenByDescending(row => row.LatestRunCount)
              .ThenBy(row => row.TableId).ToArray();
            dailyStake = _engine.State.DailyAcceptedStake;
            reservedStake = _engine.ReservedStake;
        }
        SnapshotChanged?.Invoke(new ServiceSnapshot(true, poll.Ready, poll.Bundle, poll.Balance,
            dailyStake, reservedStake, _ordersPaused, rows));
    }

    private void WriteLog(string message)
    {
        _store.Log(message);
        LogReceived?.Invoke(message);
    }

    // Current streak plus progress of every enabled alternation mode that has at least two runs forming.
    // Closeness is the best progress ratio over enabled modes and keeps near-trigger tables on top.
    private static (string Text, double Closeness) DescribeTrend(IReadOnlyList<ResultRun> runs, StrategySettings strategy)
    {
        if (runs.Count == 0) return ("-", 0);
        var parts = new List<string> { $"{OutcomeText(runs[0].Side)} × {runs[0].Count}" };
        double closeness = 0;
        foreach (StrategyPattern pattern in strategy.OrderedPatterns)
        {
            int hands = PatternDetector.Progress(runs, pattern);
            int required = PatternDetector.RequiredHands(pattern, strategy.StreakLength);
            closeness = Math.Max(closeness, Math.Min(hands, required) / (double)required);
            if (pattern == StrategyPattern.Streak || hands <= runs[0].Count) continue;
            parts.Add($"{AlternationName(pattern)} {hands}/{required}");
        }
        return (string.Join(" · ", parts), closeness);
    }

    private static string AlternationName(StrategyPattern pattern) => pattern switch
    {
        StrategyPattern.SingleAlternation => "单跳",
        StrategyPattern.DoubleAlternation => "二排",
        StrategyPattern.TripleAlternation => "三排",
        _ => pattern.ToString()
    };

    private static string SideText(BetSide side) => side == BetSide.Banker ? "庄" : "闲";
    private static string OutcomeText(BaccaratOutcome outcome) => outcome switch
    {
        BaccaratOutcome.Banker => "庄",
        BaccaratOutcome.Player => "闲",
        BaccaratOutcome.Tie => "和",
        _ => "-"
    };

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
