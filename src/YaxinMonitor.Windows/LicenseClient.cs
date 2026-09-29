using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YaxinMonitor.Core;

namespace YaxinMonitor.Windows;

internal sealed record LicenseLoginResult(bool Success, string Message);

/// <summary>
/// 授权服务客户端：启动时登录换取许可；运行中定时上报机器状态与日志。
/// 运行中网络异常不影响使用，只有后台明确禁用账号或机器时才触发 <see cref="Revoked"/>。
/// </summary>
internal sealed class LicenseClient : IAsyncDisposable
{
    private const string ServerUrl = "https://45.152.67.26:38443/";
    private const string PinnedCertificateSha256 = "47CA08652DF44EC3280E82FAA43F7334133168245441E4F8D807608DE287A86D";
    private const int MaxQueuedLogs = 3_000;
    private const int MaxLogsPerReport = 1_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly StateStore _store;
    private readonly string _appVersion;
    private readonly HttpClient _http;
    private readonly object _lock = new();
    private readonly Queue<string> _logs = new();
    private readonly CancellationTokenSource _stop = new();
    private MonitorService? _service;
    private Func<YaxinSettings>? _settings;
    private ServiceSnapshot? _snapshot;
    private string _status = "未连接";
    private string? _username;
    private string? _password;
    private string? _token;
    private TimeSpan _interval = TimeSpan.FromSeconds(60);
    private Task? _reporter;
    private int _revoked;

    public LicenseClient(StateStore store, string appVersion)
    {
        _store = store;
        _appVersion = appVersion;
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            SslOptions = new SslClientAuthenticationOptions
            {
                // 服务器使用自签证书，固定其指纹，拒绝其它任何证书。
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null && string.Equals(
                        Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())),
                        PinnedCertificateSha256, StringComparison.OrdinalIgnoreCase)
            }
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri(ServerUrl), Timeout = TimeSpan.FromSeconds(15) };
        MachineId = MachineIdentity.GetMachineId(store.DirectoryPath);
    }

    public event Action<string>? Revoked;

    public string MachineId { get; }
    public string? Username => _username;

    private string UsernamePath => Path.Combine(_store.DirectoryPath, "license-user.txt");

    public string LoadSavedUsername()
    {
        try { return File.Exists(UsernamePath) ? File.ReadAllText(UsernamePath).Trim() : ""; }
        catch { return ""; }
    }

    public async Task<LicenseLoginResult> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        username = username.Trim();
        if (username.Length == 0 || password.Length == 0) return new(false, "请输入用户名和密码。");
        try
        {
            var request = new
            {
                username,
                password,
                machineId = MachineId,
                machineName = Environment.MachineName,
                os = RuntimeInformation.OSDescription,
                appVersion = _appVersion,
                machineInfo = MachineIdentity.Describe()
            };
            using HttpResponseMessage response = await _http.PostAsJsonAsync("api/v1/login", request, JsonOptions, cancellationToken).ConfigureAwait(false);
            LoginResponse? body = await ReadAsync<LoginResponse>(response, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || body is not { Ok: true, Token.Length: > 0 })
                return new(false, body?.Message is { Length: > 0 } message ? message : $"授权服务器拒绝登录（{(int)response.StatusCode}）。");

            lock (_lock)
            {
                _username = username;
                _password = password;
                _token = body.Token;
                if (body.ReportIntervalSeconds is >= 15 and <= 3600) _interval = TimeSpan.FromSeconds(body.ReportIntervalSeconds);
            }
            try
            {
                Directory.CreateDirectory(_store.DirectoryPath);
                File.WriteAllText(UsernamePath, username);
            }
            catch { }
            return new(true, body.Message ?? "授权成功");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "连接授权服务器超时，请检查网络后重试。");
        }
        catch (HttpRequestException exception)
        {
            return new(false, "无法连接授权服务器：" + (exception.InnerException?.Message ?? exception.Message));
        }
    }

    /// <summary>登录成功后挂接监控服务并开始定时上报。</summary>
    public void Attach(MonitorService service, Func<YaxinSettings> settings)
    {
        if (_reporter is not null) throw new InvalidOperationException("授权上报已启动。");
        _service = service;
        _settings = settings;
        service.LogReceived += OnLog;
        service.StatusChanged += OnStatus;
        service.SnapshotChanged += OnSnapshot;
        _reporter = Task.Run(() => ReportLoopAsync(_stop.Token));
    }

    private void OnLog(string message)
    {
        lock (_lock)
        {
            _logs.Enqueue($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}");
            while (_logs.Count > MaxQueuedLogs) _logs.Dequeue();
        }
    }

    private void OnStatus(string status)
    {
        lock (_lock) _status = status;
    }

    private void OnSnapshot(ServiceSnapshot snapshot)
    {
        lock (_lock) _snapshot = snapshot;
    }

    private async Task ReportLoopAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ReportOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // 只校验启动：运行中的网络或服务器故障不影响使用，下次再报。
            }
            if (Volatile.Read(ref _revoked) != 0) return;
            await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReportOnceAsync(CancellationToken cancellationToken)
    {
        string[] logs;
        string? token;
        lock (_lock)
        {
            logs = _logs.Take(MaxLogsPerReport).ToArray();
            token = _token;
        }
        var report = new { telemetry = BuildTelemetry(), logs };

        using HttpResponseMessage response = await SendReportAsync(token, report, cancellationToken).ConfigureAwait(false);
        HttpResponseMessage final = response;
        HttpResponseMessage? retried = null;
        try
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // 会话失效（例如管理员重置了密码或服务器清理了会话）：用本次启动的凭据重新登录。
                LicenseLoginResult relogin = await LoginAsync(_username ?? "", _password ?? "", cancellationToken).ConfigureAwait(false);
                if (!relogin.Success)
                {
                    if (IsExplicitDenial(relogin.Message)) Revoke(relogin.Message);
                    return;
                }
                lock (_lock) token = _token;
                retried = await SendReportAsync(token, report, cancellationToken).ConfigureAwait(false);
                final = retried;
            }
            if (!final.IsSuccessStatusCode) return;
            ReportResponse? body = await ReadAsync<ReportResponse>(final, cancellationToken).ConfigureAwait(false);
            if (body is null) return;
            lock (_lock)
            {
                for (int index = 0; index < logs.Length && _logs.Count > 0; index++) _logs.Dequeue();
                if (body.ReportIntervalSeconds is >= 15 and <= 3600) _interval = TimeSpan.FromSeconds(body.ReportIntervalSeconds);
            }
            if (!body.Allowed) Revoke(string.IsNullOrWhiteSpace(body.Message) ? "授权已被管理员撤销。" : body.Message);
        }
        finally
        {
            retried?.Dispose();
        }
    }

    private Task<HttpResponseMessage> SendReportAsync(string? token, object report, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/report") { Content = JsonContent.Create(report, options: JsonOptions) };
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
        return _http.SendAsync(request, cancellationToken);
    }

    /// <summary>带会话令牌的 GET 请求；会话失效时用本次启动的凭据重新登录后重试一次。</summary>
    public async Task<HttpResponseMessage> GetAuthorizedAsync(string path, HttpCompletionOption option, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            string? token;
            lock (_lock) token = _token;
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (token is not null) request.Headers.Authorization = new("Bearer", token);
            HttpResponseMessage response = await _http.SendAsync(request, option, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0) return response;
            response.Dispose();
            LicenseLoginResult relogin = await LoginAsync(_username ?? "", _password ?? "", cancellationToken).ConfigureAwait(false);
            if (!relogin.Success)
            {
                if (IsExplicitDenial(relogin.Message)) Revoke(relogin.Message);
                throw new InvalidOperationException(relogin.Message);
            }
        }
    }

    private static bool IsExplicitDenial(string message) =>
        message.Contains("禁用", StringComparison.Ordinal) || message.Contains("禁止", StringComparison.Ordinal)
        || message.Contains("用户名或密码错误", StringComparison.Ordinal);

    private void Revoke(string message)
    {
        if (Interlocked.Exchange(ref _revoked, 1) != 0) return;
        Revoked?.Invoke(message);
    }

    private object BuildTelemetry()
    {
        ServiceSnapshot? snapshot;
        string status;
        lock (_lock)
        {
            snapshot = _snapshot;
            status = _status;
        }
        MonitorService? service = _service;
        YaxinSettings? settings = _settings?.Invoke();
        bool running = service?.IsRunning == true;
        TableViewState[] chasing = snapshot?.Tables.Where(table => table.Chase != "-").ToArray() ?? [];
        return new
        {
            version = _appVersion,
            running,
            ordersPaused = service?.OrdersPaused ?? true,
            status,
            connected = running && snapshot?.Connected == true,
            siteLoggedIn = running && snapshot?.LoggedIn == true,
            bundle = snapshot?.Bundle,
            balance = snapshot?.Balance,
            dailyStake = snapshot?.DailyStake,
            reservedStake = snapshot?.ReservedStake,
            tables = snapshot?.Tables.Count ?? 0,
            chasing = chasing.Length,
            chases = chasing.Take(30).Select(table => new { table.TableId, table.Name, table.Chase, table.LatestRun }).ToArray(),
            mode = settings?.Mode.ToString(),
            strategy = settings?.Strategy.Summary,
            dailyStakeLimit = settings?.DailyStakeLimit,
            maxReservedStake = settings?.MaxReservedStake
        };
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken) where T : class
    {
        try { return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false); }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    /// <summary>退出前尽量把最后的日志和状态报上去。</summary>
    public async Task FlushAsync()
    {
        if (_reporter is null || Volatile.Read(ref _revoked) != 0) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await ReportOnceAsync(timeout.Token).ConfigureAwait(false);
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_service is { } service)
        {
            service.LogReceived -= OnLog;
            service.StatusChanged -= OnStatus;
            service.SnapshotChanged -= OnSnapshot;
        }
        if (_reporter is not null)
        {
            try { await _reporter.ConfigureAwait(false); } catch { }
        }
        _http.Dispose();
        _stop.Dispose();
    }

    private sealed record LoginResponse(bool Ok, string? Token, string? Message, int ReportIntervalSeconds);
    private sealed record ReportResponse(bool Allowed, string? Message, int ReportIntervalSeconds);
}

internal static class MachineIdentity
{
    public static string GetMachineId(string dataDirectory)
    {
        string? raw = null;
        try
        {
            if (OperatingSystem.IsWindows()) raw = ReadWindowsMachineGuid();
            else if (OperatingSystem.IsMacOS()) raw = ReadMacPlatformUuid();
        }
        catch { }
        if (string.IsNullOrWhiteSpace(raw)) raw = LoadOrCreateFallbackId(dataDirectory);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("yaxin-monitor:" + raw.Trim().ToUpperInvariant()));
        return Convert.ToHexString(hash)[..40].ToLowerInvariant();
    }

    public static Dictionary<string, object?> Describe()
    {
        var info = new Dictionary<string, object?>
        {
            ["主机名"] = Environment.MachineName,
            ["系统用户"] = Environment.UserName,
            ["系统"] = RuntimeInformation.OSDescription,
            ["架构"] = RuntimeInformation.OSArchitecture.ToString(),
            ["CPU 核数"] = Environment.ProcessorCount,
            ["内存 GB"] = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024 / 1024, 1),
            ["时区"] = TimeZoneInfo.Local.Id,
            ["运行时"] = RuntimeInformation.FrameworkDescription
        };
        try
        {
            string? cpu = OperatingSystem.IsWindows() ? ReadWindowsCpuName()
                : OperatingSystem.IsMacOS() ? RunTool("/usr/sbin/sysctl", "-n", "machdep.cpu.brand_string") : null;
            if (!string.IsNullOrWhiteSpace(cpu)) info["CPU"] = cpu.Trim();
            if (OperatingSystem.IsMacOS() && RunTool("/usr/sbin/sysctl", "-n", "hw.model") is { Length: > 0 } model) info["型号"] = model.Trim();
        }
        catch { }
        return info;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? ReadWindowsMachineGuid()
    {
        using var root = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
        using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        return key?.GetValue("MachineGuid") as string;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? ReadWindowsCpuName()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return key?.GetValue("ProcessorNameString") as string;
    }

    private static string? ReadMacPlatformUuid()
    {
        string? output = RunTool("/usr/sbin/ioreg", "-rd1", "-c", "IOPlatformExpertDevice");
        if (output is null) return null;
        foreach (string line in output.Split('\n'))
        {
            if (!line.Contains("\"IOPlatformUUID\"", StringComparison.Ordinal)) continue;
            string[] parts = line.Split('"');
            if (parts.Length >= 4) return parts[3];
        }
        return null;
    }

    private static string LoadOrCreateFallbackId(string dataDirectory)
    {
        string path = Path.Combine(dataDirectory, "machine-id");
        try
        {
            if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: >= 16 } existing) return existing;
            Directory.CreateDirectory(dataDirectory);
            string created = Guid.NewGuid().ToString("N");
            File.WriteAllText(path, created);
            return created;
        }
        catch
        {
            return Environment.MachineName + "|" + Environment.UserName;
        }
    }

    private static string? RunTool(string executable, params string[] arguments)
    {
        if (!File.Exists(executable)) return null;
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        using Process? process = Process.Start(startInfo);
        if (process is null) return null;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(3_000);
        return output;
    }
}
