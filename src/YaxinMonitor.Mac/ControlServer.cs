using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using YaxinMonitor.Core;

namespace YaxinMonitor.Windows;

internal sealed class ControlServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly string AppVersion = typeof(ControlServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly string _url;
    private readonly StateStore _store;
    private readonly MonitorService _service;
    private readonly LicenseClient _license;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly object _viewLock = new();
    private readonly List<string> _logs = [];
    private readonly string _controlToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private readonly string _indexHtml;
    private readonly string _instanceId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
    private readonly AppUpdater _updater;
    private UpdateInfo? _availableUpdate;
    private string _updateStage = "";
    private int _updatePercent;
    private string? _updateError;
    private volatile bool _updating;
    private long _lastPageSeen;
    private YaxinSettings _settings;
    private ServiceSnapshot? _snapshot;
    private string _status = "未连接";
    private volatile bool _licensed;
    private volatile string? _revokedMessage;

    public ControlServer(string url, StateStore store, YaxinSettings settings)
    {
        _url = url;
        _store = store;
        _settings = settings;
        _service = new MonitorService(store, settings);
        _service.LogReceived += OnLog;
        _service.StatusChanged += value => { lock (_viewLock) _status = value; };
        _service.SnapshotChanged += value => { lock (_viewLock) _snapshot = value; };
        _license = new LicenseClient(store, AppVersion);
        _license.Revoked += OnRevoked;
        _updater = new AppUpdater(_license, store.DirectoryPath, AppVersion);
        _listener.Prefixes.Add(url);
        _indexHtml = LoadIndexHtml().Replace("__CONTROL_TOKEN__", _controlToken, StringComparison.Ordinal);
        if (AppUpdater.ConsumeNotice(store.DirectoryPath) is { } notice) OnLog(notice);
        OnLog("当前策略：" + settings.Strategy.Summary + "。首次使用请启动监控并在 Chrome 中手动登录。");
    }

    public static async Task<bool> IsAlreadyRunningAsync(string url)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(700) };
            using HttpResponseMessage response = await client.GetAsync(url + "api/ping").ConfigureAwait(false);
            return response.IsSuccessStatusCode
                && string.Equals(await response.Content.ReadAsStringAsync().ConfigureAwait(false), "YaxinMonitor", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public async Task RunAsync(bool afterUpdate = false)
    {
        _listener.Start();
        Task acceptLoop = AcceptLoopAsync(_shutdown.Token);
        if (!string.Equals(Environment.GetEnvironmentVariable("YAXIN_MONITOR_NO_BROWSER"), "1", StringComparison.Ordinal))
            _ = OpenBrowserAsync(afterUpdate);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        _listener.Stop();
        try { await acceptLoop.ConfigureAwait(false); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (HttpListenerException) when (_shutdown.IsCancellationRequested) { }
    }

    public void RequestShutdown() => _shutdown.Cancel();

    private async Task OpenBrowserAsync(bool afterUpdate)
    {
        if (afterUpdate)
        {
            // 更新重启后，原来的控制页会自动刷新重连；几秒内没有页面连上时才新开一个。
            try { await Task.Delay(TimeSpan.FromSeconds(5), _shutdown.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (Environment.TickCount64 - Interlocked.Read(ref _lastPageSeen) < 5_000) return;
        }
        MacShell.Open(_url);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context = await _listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            _ = Task.Run(() => HandleAsync(context), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            string path = context.Request.Url?.AbsolutePath ?? "/";
            if (context.Request.HttpMethod == "GET" && path == "/")
            {
                await WriteTextAsync(context.Response, _indexHtml, "text/html; charset=utf-8").ConfigureAwait(false);
                return;
            }
            if (context.Request.HttpMethod == "GET" && path == "/api/ping")
            {
                await WriteTextAsync(context.Response, "YaxinMonitor", "text/plain; charset=utf-8").ConfigureAwait(false);
                return;
            }
            if (context.Request.HttpMethod == "GET" && path == "/api/state")
            {
                Interlocked.Exchange(ref _lastPageSeen, Environment.TickCount64);
                await WriteJsonAsync(context.Response, BuildView()).ConfigureAwait(false);
                return;
            }
            if (context.Request.HttpMethod != "POST" || !HasValidToken(context.Request))
            {
                context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                await WriteJsonAsync(context.Response, new { error = "请求无效。" }).ConfigureAwait(false);
                return;
            }

            await _commandGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await HandleCommandAsync(context, path).ConfigureAwait(false);
            }
            finally
            {
                _commandGate.Release();
            }
        }
        catch (Exception exception)
        {
            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteJsonAsync(context.Response, new { error = exception.Message }).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Close();
        }
    }

    private async Task HandleCommandAsync(HttpListenerContext context, string path)
    {
        if (path is not ("/api/license/login" or "/api/quit"))
        {
            if (_revokedMessage is { } revoked) throw new InvalidOperationException(revoked);
            if (!_licensed) throw new InvalidOperationException("请先登录授权。");
        }
        switch (path)
        {
            case "/api/license/login":
            {
                if (_licensed) break;
                LoginRequest request = await ReadJsonAsync<LoginRequest>(context.Request).ConfigureAwait(false);
                LicenseLoginResult result = await _license.LoginAsync(request.Username, request.Password).ConfigureAwait(false);
                if (!result.Success) throw new InvalidOperationException(result.Message);
                _licensed = true;
                _license.Attach(_service, () => _settings);
                OnLog($"授权成功：{_license.Username}（本机 {_license.MachineId[..12]}）。");
                _ = CheckUpdateQuietlyAsync();
                break;
            }
            case "/api/update/check":
            {
                if (_updating) throw new InvalidOperationException("正在更新，请稍候。");
                _updateError = null;
                UpdateInfo? update = await _updater.CheckAsync().ConfigureAwait(false);
                _availableUpdate = update;
                await WriteJsonAsync(context.Response, new { ok = true, current = AppVersion, update }).ConfigureAwait(false);
                return;
            }
            case "/api/update/install":
            {
                if (_updating) throw new InvalidOperationException("正在更新，请稍候。");
                if (_service.IsRunning) throw new InvalidOperationException("请先停止监控，再更新程序。");
                UpdateInfo update = _availableUpdate ?? throw new InvalidOperationException("没有可安装的新版本，请先检查更新。");
                _updater.EnsureInstallable();
                _updating = true;
                _updateError = null;
                _ = Task.Run(() => InstallUpdateAsync(update));
                break;
            }
            case "/api/update/rollback":
            {
                if (_updating) throw new InvalidOperationException("正在更新，请稍候。");
                if (_service.IsRunning) throw new InvalidOperationException("请先停止监控，再回滚版本。");
                BackupVersion target = _updater.RollbackTarget() ?? throw new InvalidOperationException("没有可回滚的备份版本。");
                _updating = true;
                try
                {
                    _updater.RollbackAndRestart(target);
                }
                catch
                {
                    _updating = false;
                    throw;
                }
                _updateStage = "restarting";
                OnLog($"已回滚到 v{target.Version}，程序即将重启。");
                ScheduleShutdown();
                break;
            }
            case "/api/save":
            {
                YaxinSettings settings = (await ReadJsonAsync<SettingsRequest>(context.Request).ConfigureAwait(false)).ToSettings(_settings);
                _service.UpdateSettings(settings);
                _store.SaveSettings(settings);
                _settings = settings;
                OnLog("设置已保存。");
                break;
            }
            case "/api/start":
            case "/api/reset-start":
            {
                if (_updating) throw new InvalidOperationException("正在更新程序，暂不能启动监控。");
                YaxinSettings settings = (await ReadJsonAsync<SettingsRequest>(context.Request).ConfigureAwait(false)).ToSettings(_settings);
                if (path == "/api/reset-start") _service.ResetRuntimeState(settings);
                else _service.UpdateSettings(settings);
                _store.SaveSettings(settings);
                _settings = settings;
                _service.Start(Path.Combine(_store.DirectoryPath, "chrome-profile"));
                OnLog(path == "/api/reset-start"
                    ? "正在重置运行状态并启动专用 Chrome 和监控服务..."
                    : "正在启动专用 Chrome 和监控服务...");
                break;
            }
            case "/api/stop":
                await _service.StopAsync().ConfigureAwait(false);
                break;
            case "/api/toggle-orders":
                if (_service.OrdersPaused) _service.ResumeOrders();
                else _service.PauseOrders();
                break;
            case "/api/reconcile":
            {
                ReconcileRequest request = await ReadJsonAsync<ReconcileRequest>(context.Request).ConfigureAwait(false);
                if (!Enum.TryParse(request.Resolution, out ManualOrderResolution resolution))
                    throw new ArgumentException("对账结果无效。");
                _service.ResolveOrder(request.OrderKey, resolution);
                break;
            }
            case "/api/open-data":
                Directory.CreateDirectory(_store.DirectoryPath);
                MacShell.Open(_store.DirectoryPath);
                break;
            case "/api/quit":
                await _service.StopAsync().ConfigureAwait(false);
                await _license.FlushAsync().ConfigureAwait(false);
                ScheduleShutdown();
                break;
            default:
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                await WriteJsonAsync(context.Response, new { error = "接口不存在。" }).ConfigureAwait(false);
                return;
        }
        await WriteJsonAsync(context.Response, new { ok = true }).ConfigureAwait(false);
    }

    private void ScheduleShutdown() => _ = Task.Run(async () =>
    {
        await Task.Delay(300).ConfigureAwait(false);
        _shutdown.Cancel();
    });

    private async Task CheckUpdateQuietlyAsync()
    {
        try
        {
            UpdateInfo? update = await _updater.CheckAsync().ConfigureAwait(false);
            if (update is null || _updating) return;
            _availableUpdate = update;
            OnLog($"发现新版本 v{update.Version}，停止监控后点击“更新到 v{update.Version}”即可安装。");
        }
        catch { }
    }

    private async Task InstallUpdateAsync(UpdateInfo update)
    {
        try
        {
            _updateStage = "downloading";
            _updatePercent = 0;
            OnLog($"开始下载 v{update.Version}...");
            string archive = await _updater.DownloadAsync(update, new Progress<int>(percent => _updatePercent = percent)).ConfigureAwait(false);
            await _commandGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_service.IsRunning) throw new InvalidOperationException("监控已启动，更新已取消。");
                _updateStage = "installing";
                _updater.InstallAndRestart(update, archive);
                _updateStage = "restarting";
                OnLog($"v{update.Version} 安装完成，程序即将重启。");
                await _license.FlushAsync().ConfigureAwait(false);
                ScheduleShutdown();
            }
            finally
            {
                _commandGate.Release();
            }
        }
        catch (Exception exception)
        {
            _updateStage = "";
            _updateError = exception.Message;
            _updating = false;
            OnLog("更新失败：" + exception.Message);
        }
    }

    private object BuildView()
    {
        string status;
        string[] logs;
        ServiceSnapshot? snapshot;
        lock (_viewLock)
        {
            status = _status;
            logs = _logs.ToArray();
            snapshot = _snapshot;
        }
        var license = new
        {
            loggedIn = _licensed,
            username = _license.Username,
            savedUsername = _licensed ? null : _license.LoadSavedUsername(),
            machineId = _license.MachineId[..12],
            revoked = _revokedMessage
        };
        if (!_licensed || _revokedMessage is not null)
            return new { version = AppVersion, instance = _instanceId, license, running = _service.IsRunning, status };
        var update = new
        {
            available = _availableUpdate,
            updating = _updating,
            stage = _updateStage,
            percent = _updatePercent,
            error = _updateError,
            rollback = _updating ? null : _updater.RollbackTarget()?.Version
        };
        return new
        {
            version = AppVersion,
            instance = _instanceId,
            update,
            license,
            running = _service.IsRunning,
            ordersPaused = _service.OrdersPaused,
            status,
            settings = new
            {
                mode = _settings.Mode.ToString(),
                dailyStakeLimit = _settings.DailyStakeLimit,
                maxReservedStake = _settings.MaxReservedStake,
                minimumSeconds = _settings.MinimumRemainingMilliseconds / 1000,
                playAcceptedSound = _settings.PlayAcceptedSound,
                patterns = _settings.Strategy.Patterns.Select(pattern => pattern.ToString()).ToArray(),
                triggerSide = _settings.Strategy.TriggerSide.ToString(),
                streakLength = _settings.Strategy.StreakLength,
                direction = _settings.Strategy.Direction.ToString(),
                stakes = string.Join(",", _settings.Strategy.Stakes.Select(value => value.ToString("0.##")))
            },
            snapshot,
            reconciliationOrders = _service.GetReconciliationOrders(),
            logs
        };
    }

    private bool HasValidToken(HttpListenerRequest request) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(request.Headers["X-Control-Token"] ?? ""),
            Encoding.UTF8.GetBytes(_controlToken));

    private void OnRevoked(string message)
    {
        _revokedMessage = message;
        OnLog("授权已被撤销：" + message + " 监控已停止。");
        _ = Task.Run(async () =>
        {
            await _commandGate.WaitAsync().ConfigureAwait(false);
            try { await _service.StopAsync().ConfigureAwait(false); }
            catch { }
            finally { _commandGate.Release(); }
        });
    }

    private void OnLog(string message)
    {
        lock (_viewLock)
        {
            _logs.Add($"{DateTime.Now:HH:mm:ss} {message}");
            if (_logs.Count > 500) _logs.RemoveRange(0, _logs.Count - 400);
        }
    }

    private static async Task<T> ReadJsonAsync<T>(HttpListenerRequest request)
    {
        if (request.ContentLength64 > 64 * 1024) throw new InvalidDataException("请求内容过大。");
        return await JsonSerializer.DeserializeAsync<T>(request.InputStream, JsonOptions).ConfigureAwait(false)
            ?? throw new InvalidDataException("请求内容为空。");
    }

    private static Task WriteJsonAsync(HttpListenerResponse response, object value) =>
        WriteBytesAsync(response, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), "application/json; charset=utf-8");

    private static Task WriteTextAsync(HttpListenerResponse response, string value, string contentType) =>
        WriteBytesAsync(response, Encoding.UTF8.GetBytes(value), contentType);

    private static async Task WriteBytesAsync(HttpListenerResponse response, byte[] bytes, string contentType)
    {
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        response.Headers["Cache-Control"] = "no-store";
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static string LoadIndexHtml()
    {
        Assembly assembly = typeof(ControlServer).Assembly;
        string name = assembly.GetManifestResourceNames().Single(value => value.EndsWith("Web.index.html", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("缺少控制页资源。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Close();
        await _service.DisposeAsync().ConfigureAwait(false);
        await _license.DisposeAsync().ConfigureAwait(false);
        _service.LogReceived -= OnLog;
        _commandGate.Dispose();
        _shutdown.Dispose();
    }

    private sealed record SettingsRequest
    {
        public string Mode { get; init; } = "ReadOnly";
        public decimal DailyStakeLimit { get; init; }
        public decimal MaxReservedStake { get; init; }
        public int MinimumSeconds { get; init; } = 8;
        public bool PlayAcceptedSound { get; init; } = true;
        public string[] Patterns { get; init; } = ["Streak"];
        public string TriggerSide { get; init; } = "Both";
        public int StreakLength { get; init; } = 6;
        public string Direction { get; init; } = "Opposite";
        public string Stakes { get; init; } = "10,20,40";

        public YaxinSettings ToSettings(YaxinSettings current)
        {
            if (!Enum.TryParse(Mode, out MonitorMode mode)) throw new ArgumentException("运行模式无效。");
            StrategyPattern[] patterns = (Patterns ?? [])
                .Select(value => Enum.TryParse(value, out StrategyPattern pattern) && Enum.IsDefined(pattern)
                    ? pattern : throw new ArgumentException("识别模式无效。"))
                .ToArray();
            if (!Enum.TryParse(TriggerSide, out StrategyTriggerSide triggerSide)) throw new ArgumentException("触发走势无效。");
            if (!Enum.TryParse(Direction, out StrategyDirection direction)) throw new ArgumentException("下注方向无效。");
            decimal[] stakes = Stakes.Split([',', '，', ';', '；', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => decimal.TryParse(value, out decimal amount) ? amount : throw new ArgumentException($"无法识别金额：{value}"))
                .ToArray();
            var settings = current with
            {
                Mode = mode,
                DailyStakeLimit = DailyStakeLimit,
                MaxReservedStake = MaxReservedStake,
                MinimumRemainingMilliseconds = checked(MinimumSeconds * 1000),
                PlayAcceptedSound = PlayAcceptedSound,
                Strategy = new StrategySettings
                {
                    Patterns = patterns,
                    TriggerSide = triggerSide,
                    StreakLength = StreakLength,
                    Direction = direction,
                    Stakes = stakes
                }
            };
            settings.Validate();
            return settings;
        }
    }

    private sealed record LoginRequest
    {
        public string Username { get; init; } = "";
        public string Password { get; init; } = "";
    }

    private sealed record ReconcileRequest
    {
        public string OrderKey { get; init; } = "";
        public string Resolution { get; init; } = "";
    }
}
