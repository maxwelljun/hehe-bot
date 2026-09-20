using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace YaxinMonitor.Windows;

internal sealed class CdpClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _receiveTask;
    private long _nextId;

    public event Action<string, JsonElement>? EventReceived;

    public async Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        await _socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        _receiveTask = ReceiveLoopAsync(_lifetime.Token);
    }

    public async Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        long id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion)) throw new InvalidOperationException("CDP 请求编号冲突。");
        try
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
            await _socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                if (message.Length > 16 * 1024 * 1024) throw new InvalidDataException("CDP 消息超过 16 MiB 限制。");
                using JsonDocument document = JsonDocument.Parse(message.GetBuffer().AsMemory(0, checked((int)message.Length)));
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("id", out JsonElement idElement))
                {
                    long id = idElement.GetInt64();
                    if (_pending.TryGetValue(id, out TaskCompletionSource<JsonElement>? completion))
                    {
                        if (root.TryGetProperty("error", out JsonElement error))
                            completion.TrySetException(new InvalidOperationException("CDP 错误：" + error.GetRawText()));
                        else completion.TrySetResult(root.GetProperty("result").Clone());
                    }
                }
                else if (root.TryGetProperty("method", out JsonElement method))
                {
                    JsonElement parameters = root.TryGetProperty("params", out JsonElement value) ? value.Clone() : default;
                    EventReceived?.Invoke(method.GetString() ?? "", parameters);
                }
                message.SetLength(0);
            }
            throw new IOException("Chrome 调试连接已关闭。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            foreach (TaskCompletionSource<JsonElement> completion in _pending.Values)
                completion.TrySetException(exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_socket.State == WebSocketState.Open)
        {
            try { await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "dispose", CancellationToken.None).ConfigureAwait(false); }
            catch (WebSocketException) { }
        }
        if (_receiveTask is not null)
        {
            try { await _receiveTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
        _socket.Dispose();
        _lifetime.Dispose();
    }
}
