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

    private readonly string _url;
    private readonly StateStore _store;
    private readonly MonitorService _service;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly object _viewLock = new();
    private readonly List<string> _logs = [];
    private readonly string _controlToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private readonly string _indexHtml;
    private YaxinSettings _settings;
    private ServiceSnapshot? _snapshot;
    private string _status = "未连接";

    public ControlServer(string url, StateStore store, YaxinSettings settings)
    {
        _url = url;
        _store = store;
        _settings = settings;
        _service = new MonitorService(store, settings);
        _service.LogReceived += OnLog;
        _service.StatusChanged += value => { lock (_viewLock) _status = value; };
        _service.SnapshotChanged += value => { lock (_viewLock) _snapshot = value; };
        _listener.Prefixes.Add(url);
        _indexHtml = LoadIndexHtml().Replace("__CONTROL_TOKEN__", _controlToken, StringComparison.Ordinal);
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

    public async Task RunAsync()
    {
        _listener.Start();
        Task acceptLoop = AcceptLoopAsync(_shutdown.Token);
        if (!string.Equals(Environment.GetEnvironmentVariable("YAXIN_MONITOR_NO_BROWSER"), "1", StringComparison.Ordinal))
            MacShell.Open(_url);
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
        switch (path)
        {
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
                _service.ResolveUnknownOrder(request.OrderKey, resolution);
                break;
            }
            case "/api/open-data":
                Directory.CreateDirectory(_store.DirectoryPath);
                MacShell.Open(_store.DirectoryPath);
                break;
            case "/api/quit":
                await _service.StopAsync().ConfigureAwait(false);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(200).ConfigureAwait(false);
                    _shutdown.Cancel();
                });
                break;
            default:
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                await WriteJsonAsync(context.Response, new { error = "接口不存在。" }).ConfigureAwait(false);
                return;
        }
        await WriteJsonAsync(context.Response, new { ok = true }).ConfigureAwait(false);
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
        return new
        {
            version = "1.2.9",
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
                triggerSide = _settings.Strategy.TriggerSide.ToString(),
                streakLength = _settings.Strategy.StreakLength,
                direction = _settings.Strategy.Direction.ToString(),
                stakes = string.Join(",", _settings.Strategy.Stakes.Select(value => value.ToString("0.##")))
            },
            snapshot,
            unknownOrders = _service.GetUnknownOrders(),
            logs
        };
    }

    private bool HasValidToken(HttpListenerRequest request) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(request.Headers["X-Control-Token"] ?? ""),
            Encoding.UTF8.GetBytes(_controlToken));

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
        public string TriggerSide { get; init; } = "Both";
        public int StreakLength { get; init; } = 6;
        public string Direction { get; init; } = "Opposite";
        public string Stakes { get; init; } = "10,20,40";

        public YaxinSettings ToSettings(YaxinSettings current)
        {
            if (!Enum.TryParse(Mode, out MonitorMode mode)) throw new ArgumentException("运行模式无效。");
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

    private sealed record ReconcileRequest
    {
        public string OrderKey { get; init; } = "";
        public string Resolution { get; init; } = "";
    }
}
