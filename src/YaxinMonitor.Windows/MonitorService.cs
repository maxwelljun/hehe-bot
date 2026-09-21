using System.Collections.Concurrent;
using System.Diagnostics;
using YaxinMonitor.Core;

namespace YaxinMonitor.Windows;

internal sealed record TableViewState(
    long TableId, string Name, string State, long ShoeSeq, long GameSeq,
    string LatestRun, int LatestRunCount, string Chase, int RemainingSeconds);

internal sealed record ServiceSnapshot(
    bool Connected, bool LoggedIn, string Bundle, decimal Balance,
    decimal DailyStake, decimal ReservedStake, bool OrdersPaused,
    IReadOnlyList<TableViewState> Tables);

internal sealed record UnknownOrderView(
    string OrderKey, long TableId, BetSide Side, decimal Amount, int Attempt, DateTimeOffset CreatedAt);

internal sealed class MonitorService : IAsyncDisposable
{
    private readonly StateStore _store;
    private readonly AcceptedNotifier _notifier = new();
    private readonly ConcurrentQueue<BetCandidate> _candidates = new();
    private readonly ConcurrentDictionary<long, string> _quarantinedTables = new();
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

    public event Action<string>? LogReceived;
    public event Action<string>? StatusChanged;
    public event Action<ServiceSnapshot>? SnapshotChanged;

    public MonitorService(StateStore store, YaxinSettings settings)
    {
        _store = store;
        _settings = settings;
        EngineState state = store.LoadState();
        _engine = new StrategyEngine(state, settings.Strategy, settings.MinimumRemainingMilliseconds);
        if (state.Orders.Values.All(order => order.Status != "Unknown") && settings.Mode != MonitorMode.Live)
            _ordersPaused = false;
    }

    public bool IsRunning => _worker is { IsCompleted: false };
    public bool OrdersPaused => _ordersPaused;

    public IReadOnlyList<UnknownOrderView> GetUnknownOrders() => _engine.State.Orders.Values
        .Where(order => order.Status == "Unknown")
        .OrderBy(order => order.CreatedAt)
        .Select(order => new UnknownOrderView(order.OrderKey, order.TableId, order.Side, order.Amount, order.Attempt, order.CreatedAt))
        .ToArray();

    public void ResolveUnknownOrder(string orderKey, ManualOrderResolution resolution)
    {
        if (IsRunning) throw new InvalidOperationException("请先停止监控再进行订单对账。");
        _engine.ResolveUnknownOrder(orderKey, resolution, DateTimeOffset.Now);
        OrderState order = _engine.State.Orders[orderKey];
        _quarantinedTables.TryRemove(order.TableId, out _);
        _store.SaveState(_engine.State);
        _store.AppendOrder(order);
        string result = resolution == ManualOrderResolution.ConfirmedNotPlaced ? "人工确认未下注" : "人工确认已结算";
        WriteLog($"订单对账完成：桌台 {order.TableId}，{SideText(order.Side)} {order.Amount:0.##}，{result}。订单键：{order.OrderKey}");
    }

    public void UpdateSettings(YaxinSettings settings)
    {
        if (IsRunning) throw new InvalidOperationException("请先停止监控再修改设置。");
        settings.Validate();
        _engine = new StrategyEngine(_engine.State, settings.Strategy, settings.MinimumRemainingMilliseconds);
        lock (_settingsLock) _settings = settings;
        _store.SaveState(_engine.State);
        if (settings.Mode == MonitorMode.Live) _ordersPaused = true;
        WriteLog("当前策略：" + settings.Strategy.Summary + "。");
    }

    public void ResetRuntimeState(YaxinSettings settings)
    {
        if (IsRunning) throw new InvalidOperationException("请先停止监控再强制启动。");
        settings.Validate();
        _engine = new StrategyEngine(new EngineState(), settings.Strategy, settings.MinimumRemainingMilliseconds);
        lock (_settingsLock) _settings = settings;
        _candidates.Clear();
        _quarantinedTables.Clear();
        _ordersPaused = settings.Mode == MonitorMode.Live;
        _store.SaveState(_engine.State);
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
            if (_engine.State.Orders.Values.Any(order => order.Status == "Unknown"))
                throw new InvalidOperationException("存在状态不明的订单，完成对账前不能恢复真实下注。");
            if (!_bettingCompatible)
                throw new InvalidOperationException("页面下注接口兼容检查尚未通过，不能恢复真实下注。");
        }
        _ordersPaused = false;
        WriteLog("已恢复新订单处理。");
    }

    public void PauseOrders(string reason = "用户暂停")
    {
        _ordersPaused = true;
        _candidates.Clear();
        WriteLog("新订单已暂停：" + reason);
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
        while (!cancellationToken.IsCancellationRequested)
        {
            BridgePoll poll = await adapter.PollAsync(cancellationToken).ConfigureAwait(false);
            HandleBridgeEvents(poll.Events, settings);

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
                WriteLog("页面运行时兼容检查已恢复正常；真实新订单仍保持暂停，请核对后手动恢复。");
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
                        HandleEngineEvents(_engine.Observe(table, DateTimeOffset.Now), settings);
                    }
                    catch (InvalidDataException exception)
                    {
                        QuarantineTable(table.TableId, exception.Message, settings);
                    }
                }
                await DispatchOneAsync(adapter, poll, settings, cancellationToken).ConfigureAwait(false);
                CheckAckTimeout(settings);
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

    private void HandleBridgeEvents(IEnumerable<BridgeEvent> events, YaxinSettings settings)
    {
        foreach (BridgeEvent item in events)
        {
            if (item.Type != "betAck" || string.IsNullOrEmpty(item.OrderKey)) continue;
            if (!_engine.State.Orders.TryGetValue(item.OrderKey, out OrderState? order)
                || order.Status is not ("Submitted" or "Unknown")) continue;
            bool lateConfirmation = order.Status == "Unknown";
            if (lateConfirmation && item.ErrorCode == 0 && _quarantinedTables.ContainsKey(order.TableId))
            {
                WriteLog($"迟到确认显示桌台 {order.TableId} 的订单已受理，但桌台数据已异常，仍需人工对账。");
                continue;
            }
            if (item.ErrorCode == 0)
            {
                _engine.MarkAccepted(item.OrderKey, DateTimeOffset.Now);
                _store.SaveState(_engine.State);
                _store.AppendOrder(order);
                WriteLog($"下注已受理：桌台 {order.TableId}，{SideText(order.Side)} {order.Amount:0.##}，第 {order.Attempt} 档。");
                if (lateConfirmation) WriteLog($"桌台 {order.TableId} 的迟到确认已恢复订单状态；新订单仍保持暂停，请核对后手动恢复。");
                if (settings.PlayAcceptedSound) _notifier.NotifyAccepted(order);
            }
            else
            {
                _engine.MarkRejected(item.OrderKey, item.ErrorCode, DateTimeOffset.Now);
                _quarantinedTables.TryRemove(order.TableId, out _);
                _store.SaveState(_engine.State);
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
                    if (_ordersPaused) WriteLog($"订单已暂停，本局不发送：{requested.Candidate.TableName}。");
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
        }
        if (changed) _store.SaveState(_engine.State);
    }

    private async Task DispatchOneAsync(SiteRuntimeAdapter adapter, BridgePoll poll, YaxinSettings settings, CancellationToken cancellationToken)
    {
        if (settings.Mode != MonitorMode.Live || _ordersPaused) return;
        if (_engine.State.Orders.Values.Any(order => order.Status == "Submitted")) return;
        if (Stopwatch.GetTimestamp() < _nextSubmitTimestamp) return;
        if (!_candidates.TryDequeue(out BetCandidate? candidate)) return;

        TableSnapshot? table = poll.Tables.FirstOrDefault(value => value.TableId == candidate.TableId);
        string? validationError = ValidateCandidate(candidate, table, poll.Balance, settings);
        _engine.MarkSubmitted(candidate, DateTimeOffset.Now);
        _store.SaveState(_engine.State);
        if (validationError is not null)
        {
            _engine.MarkRejected(candidate.OrderKey, -9001, DateTimeOffset.Now);
            _store.SaveState(_engine.State);
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
            _engine.MarkRejected(candidate.OrderKey, -9000, DateTimeOffset.Now);
            _store.SaveState(_engine.State);
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
        if (daily + candidate.Amount > settings.DailyStakeLimit) return "达到每日下注金额上限。";
        return null;
    }

    private void CheckAckTimeout(YaxinSettings settings)
    {
        OrderState? timedOut = _engine.State.Orders.Values.FirstOrDefault(order => order.Status == "Submitted"
            && DateTimeOffset.Now - (order.UpdatedAt ?? order.CreatedAt) > TimeSpan.FromSeconds(8));
        if (timedOut is null) return;
        _engine.MarkUnknown(timedOut.OrderKey, DateTimeOffset.Now);
        _store.SaveState(_engine.State);
        _store.AppendOrder(timedOut);
        if (settings.Mode == MonitorMode.Live) PauseOrders("订单确认超时，状态不明");
    }

    private void QuarantineTable(long tableId, string reason, YaxinSettings settings)
    {
        if (_engine.State.Tables.TryGetValue(tableId, out TableRuntimeState? table)
            && table.ActiveChase is { Status: ChaseStatus.AwaitingAcceptance or ChaseStatus.AwaitingSettlement, PendingOrderKey: not null } chase)
        {
            _engine.MarkUnknown(chase.PendingOrderKey, DateTimeOffset.Now);
            OrderState order = _engine.State.Orders[chase.PendingOrderKey];
            _store.SaveState(_engine.State);
            _store.AppendOrder(order);
        }

        if (!_quarantinedTables.TryAdd(tableId, reason)) return;
        if (settings.Mode == MonitorMode.Live) PauseOrders($"桌台 {tableId} 数据异常");
        WriteLog($"桌台 {tableId} 已隔离，其他桌台继续监控：{reason}");
    }

    private void DelaySubmissions(TimeSpan delay)
    {
        long target = Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * Stopwatch.Frequency);
        if (target > _nextSubmitTimestamp) _nextSubmitTimestamp = target;
    }

    private void PublishSnapshot(BridgePoll poll, YaxinSettings settings)
    {
        var rows = poll.Tables.Select(table =>
        {
            LatestRun run = LatestRun.From(table.History);
            _engine.State.Tables.TryGetValue(table.TableId, out TableRuntimeState? runtime);
            string chase = runtime?.ActiveChase is { } active
                ? $"第 {active.AttemptIndex + 1} 档 / {active.Status}" : "-";
            string latest = run.Count == 0 ? "-" : $"{OutcomeText(run.Side)} × {run.Count}";
            return new TableViewState(table.TableId, table.TableName, table.State, table.ShoeSeq, table.GameSeq,
                latest, run.Count, chase, table.RemainingMilliseconds / 1000);
        }).OrderByDescending(row => row.LatestRunCount).ThenBy(row => row.TableId).ToArray();
        SnapshotChanged?.Invoke(new ServiceSnapshot(true, poll.Ready, poll.Bundle, poll.Balance,
            _engine.State.DailyAcceptedStake, _engine.ReservedStake, _ordersPaused, rows));
    }

    private void WriteLog(string message)
    {
        _store.Log(message);
        LogReceived?.Invoke(message);
    }

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
