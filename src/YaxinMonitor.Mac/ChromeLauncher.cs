using System.Diagnostics;
using System.Text.Json;

namespace YaxinMonitor.Windows;

internal static class ChromeLauncher
{
    private const string GameUrl = "https://www.yaxin868.com/";

    public static async Task<bool> IsDebugEndpointReadyAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using HttpResponseMessage response = await client.GetAsync(
                $"http://127.0.0.1:{port}/json/version", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
    }

    public static async Task LaunchAsync(int port, string profileDirectory, CancellationToken cancellationToken)
    {
        if (await IsDebugEndpointReadyAsync(port, cancellationToken).ConfigureAwait(false))
        {
            await EnsureGameTargetAsync(port, cancellationToken).ConfigureAwait(false);
            return;
        }

        string chrome = FindChrome() ?? throw new FileNotFoundException(
            "未找到 Google Chrome。请先安装 Chrome。", "Google Chrome.app");
        Directory.CreateDirectory(profileDirectory);
        var startInfo = new ProcessStartInfo(chrome) { UseShellExecute = false };
        startInfo.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
        startInfo.ArgumentList.Add($"--remote-debugging-port={port}");
        startInfo.ArgumentList.Add($"--user-data-dir={profileDirectory}");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--disable-background-timer-throttling");
        startInfo.ArgumentList.Add("--disable-renderer-backgrounding");
        startInfo.ArgumentList.Add(GameUrl);
        _ = Process.Start(startInfo) ?? throw new InvalidOperationException("Chrome 启动失败。");

        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsDebugEndpointReadyAsync(port, cancellationToken).ConfigureAwait(false))
            {
                await EnsureGameTargetAsync(port, cancellationToken).ConfigureAwait(false);
                return;
            }
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("Chrome 已启动，但调试端口在 20 秒内没有就绪。");
    }

    public static async Task<IReadOnlyList<Uri>> FindPageTargetsAsync(int port, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using Stream stream = await client.GetStreamAsync(
            $"http://127.0.0.1:{port}/json/list", cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return document.RootElement.EnumerateArray()
            .Select(element => new
            {
                Url = element.TryGetProperty("url", out JsonElement url) ? url.GetString() ?? "" : "",
                Type = element.TryGetProperty("type", out JsonElement type) ? type.GetString() ?? "" : "",
                WebSocket = element.TryGetProperty("webSocketDebuggerUrl", out JsonElement ws) ? ws.GetString() : null
            })
            .Where(item => item.Type == "page"
                && Uri.TryCreate(item.Url, UriKind.Absolute, out Uri? uri)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                && item.WebSocket is not null)
            .Select(item => new Uri(item.WebSocket!))
            .ToArray();
    }

    private static async Task EnsureGameTargetAsync(int port, CancellationToken cancellationToken)
    {
        if ((await FindPageTargetsAsync(port, cancellationToken).ConfigureAwait(false)).Count > 0) return;

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        string requestUrl = $"http://127.0.0.1:{port}/json/new?{Uri.EscapeDataString(GameUrl)}";
        using var request = new HttpRequestMessage(HttpMethod.Put, requestUrl);
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"无法在专用 Chrome 中打开亚信页面（HTTP {(int)response.StatusCode}）。");
    }

    private static string? FindChrome()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidates =
        [
            "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
            Path.Combine(home, "Applications", "Google Chrome.app", "Contents", "MacOS", "Google Chrome"),
            "/Applications/Google Chrome for Testing.app/Contents/MacOS/Google Chrome for Testing"
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}
