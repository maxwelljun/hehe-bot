using System.Reflection;
using System.Text.Json;
using YaxinMonitor.Core;

namespace YaxinMonitor.Windows;

internal sealed record BridgeEvent
{
    public string Type { get; init; } = "";
    public long Sequence { get; init; }
    public string SessionId { get; init; } = "";
    public string OrderKey { get; init; } = "";
    public long TableId { get; init; }
    public long GameSeq { get; init; }
    public int ErrorCode { get; init; }
    public string ErrorMessage { get; init; } = "";
}

internal sealed record BridgePoll
{
    public int BridgeVersion { get; init; }
    public bool ObservationCompatible { get; init; }
    public bool BettingCompatible { get; init; }
    public string CompatibilityError { get; init; } = "";
    public bool Ready { get; init; }
    public bool LoggedIn { get; init; }
    public bool SocketConnected { get; init; }
    public string SessionId { get; init; } = "";
    public string Bundle { get; init; } = "";
    public decimal Balance { get; init; }
    public TableSnapshot[] Tables { get; init; } = [];
    public BridgeEvent[] Events { get; init; } = [];
}

internal sealed record BridgeSubmitResult
{
    public bool Submitted { get; init; }
    public string Error { get; init; } = "";
}

internal sealed class SiteRuntimeAdapter : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private CdpClient? _cdp;

    public async Task ConnectAsync(int port, CancellationToken cancellationToken)
    {
        string bridgeScript = LoadBridgeScript();
        string error = "没有找到已加载游戏模块的页面。";
        DateTime bridgeDeadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < bridgeDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<Uri> targets = await ChromeLauncher.FindPageTargetsAsync(port, cancellationToken).ConfigureAwait(false);
            foreach (Uri target in targets)
            {
                var candidate = new CdpClient();
                bool selected = false;
                try
                {
                    await candidate.ConnectAsync(target, cancellationToken).ConfigureAwait(false);
                    await candidate.SendAsync("Runtime.enable", new { }, cancellationToken).ConfigureAwait(false);
                    JsonElement result = await EvaluateRawAsync(candidate, bridgeScript, cancellationToken).ConfigureAwait(false);
                    if (result.TryGetProperty("ok", out JsonElement ok) && ok.GetBoolean())
                    {
                        JsonElement pollValue = await EvaluateRawAsync(candidate, "window.__YAXIN_MONITOR__.poll()", cancellationToken).ConfigureAwait(false);
                        BridgePoll? poll = pollValue.Deserialize<BridgePoll>(JsonOptions);
                        if (poll is not null && poll.Tables.Length > 0)
                        {
                            _cdp = candidate;
                            selected = true;
                            return;
                        }
                        error = poll is null ? "页面没有返回桌台状态。"
                            : poll.LoggedIn ? "已登录，但全桌数据尚未加载。" : "页面游戏模块已加载，但登录信息尚未就绪。";
                    }
                    if (result.TryGetProperty("error", out JsonElement value))
                        error = value.GetString() ?? error;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    error = exception.Message;
                }
                finally
                {
                    if (!selected) await candidate.DisposeAsync().ConfigureAwait(false);
                }
            }
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException("页面桥接安装失败：" + error);
    }

    public Task<BridgePoll> PollAsync(CancellationToken cancellationToken) =>
        EvaluateAsync<BridgePoll>("window.__YAXIN_MONITOR__.poll()", cancellationToken);

    public Task<BridgeSubmitResult> SubmitAsync(BetCandidate candidate, int minimumRemainingMilliseconds, CancellationToken cancellationToken)
    {
        var request = new
        {
            orderKey = candidate.OrderKey,
            tableId = candidate.TableId,
            shoeSeq = candidate.ShoeSeq,
            gameSeq = candidate.GameSeq,
            side = candidate.Side == BetSide.Player ? "PLAYER" : "BANKER",
            amount = candidate.Amount,
            minimumRemainingMilliseconds
        };
        string json = JsonSerializer.Serialize(request);
        return EvaluateAsync<BridgeSubmitResult>($"window.__YAXIN_MONITOR__.submitBet({json})", cancellationToken);
    }

    private async Task<T> EvaluateAsync<T>(string expression, CancellationToken cancellationToken)
    {
        JsonElement value = await EvaluateRawAsync(expression, cancellationToken).ConfigureAwait(false);
        return value.Deserialize<T>(JsonOptions) ?? throw new InvalidDataException("页面返回空数据。");
    }

    private async Task<JsonElement> EvaluateRawAsync(string expression, CancellationToken cancellationToken)
    {
        CdpClient cdp = _cdp ?? throw new InvalidOperationException("页面桥接尚未连接。");
        return await EvaluateRawAsync(cdp, expression, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonElement> EvaluateRawAsync(CdpClient cdp, string expression, CancellationToken cancellationToken)
    {
        object parameters = new { expression, awaitPromise = true, returnByValue = true };
        JsonElement response = await cdp.SendAsync("Runtime.evaluate", parameters, cancellationToken).ConfigureAwait(false);
        if (response.TryGetProperty("exceptionDetails", out JsonElement exception))
            throw new InvalidOperationException("页面脚本执行失败：" + exception.GetRawText());
        JsonElement remote = response.GetProperty("result");
        if (remote.TryGetProperty("value", out JsonElement value)) return value.Clone();
        string description = remote.TryGetProperty("description", out JsonElement text) ? text.GetString() ?? "无返回值" : "无返回值";
        throw new InvalidOperationException("页面脚本没有返回结构化数据：" + description);
    }

    private static string LoadBridgeScript()
    {
        Assembly assembly = typeof(SiteRuntimeAdapter).Assembly;
        string name = assembly.GetManifestResourceNames().Single(value => value.EndsWith("SiteBridge.js", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("缺少页面桥接资源。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public ValueTask DisposeAsync() => _cdp?.DisposeAsync() ?? ValueTask.CompletedTask;
}
