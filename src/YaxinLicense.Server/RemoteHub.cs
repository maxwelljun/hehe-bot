using System.Collections.Concurrent;
using System.Text.Json;

namespace YaxinLicense.Server;

public sealed record MobileLoginRequest(string? Username, string? Password);
public sealed record MobileCommandRequest(string? MachineId, string? Action, string? OrderKey, string? Resolution);
public sealed record MobileMachineView(string MachineId, string? Name, string? Note, string? Version, bool Enabled,
    JsonElement LastSeen, JsonElement Telemetry, bool Connected);
public sealed record SyncResult(string? Id, bool Ok, string? Message);
public sealed record ClientSync(JsonElement? Snapshot, SyncResult[]? Results);

public sealed record RemoteCommand(string Id, string Action, string? OrderKey, string? Resolution);

public sealed class RemoteCommandView
{
    public required string Id { get; init; }
    public required string Action { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string Status { get; set; } = "pending";
    public string Message { get; set; } = "";
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>
/// 手机远程查看和控制的中转：客户端长轮询 /api/v1/sync 领取指令并回传结果；
/// 有手机在看时，客户端每隔约 2 秒上传一次实时快照。全部状态只在内存中，服务重启即清空。
/// </summary>
public sealed class RemoteHub
{
    public static readonly string[] Actions = ["start", "stop", "pause-orders", "resume-orders", "reconcile"];
    // 指令超过这个时间还没被客户端领取就作废，避免设备重新上线后执行过期的操作。
    private static readonly TimeSpan CommandTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ViewerWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ClientWindow = TimeSpan.FromSeconds(60);
    private const int MaxHistory = 20;

    private readonly ConcurrentDictionary<string, Channel> _channels = new(StringComparer.Ordinal);

    private sealed class Channel
    {
        public readonly object Lock = new();
        public readonly Queue<(RemoteCommand Command, DateTimeOffset At)> Pending = new();
        public readonly List<RemoteCommandView> History = [];
        public TaskCompletionSource Signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public JsonElement? Snapshot;
        public DateTimeOffset SnapshotAt;
        public DateTimeOffset ViewerAt;
        public DateTimeOffset ClientAt;
    }

    private Channel Get(string machineId) => _channels.GetOrAdd(machineId, _ => new Channel());

    public bool IsClientConnected(string machineId) =>
        _channels.TryGetValue(machineId, out Channel? channel) && DateTimeOffset.UtcNow - channel.ClientAt < ClientWindow;

    /// <summary>手机查看设备时调用；从无人查看变为有人查看时唤醒客户端立即上传快照。</summary>
    public (JsonElement? Snapshot, DateTimeOffset SnapshotAt, bool Connected, RemoteCommandView[] Commands) View(string machineId)
    {
        Channel channel = Get(machineId);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool wake;
        lock (channel.Lock)
        {
            wake = now - channel.ViewerAt >= ViewerWindow;
            channel.ViewerAt = now;
            ExpireLocked(channel, now);
        }
        if (wake) Wake(channel);
        lock (channel.Lock)
            return (channel.Snapshot, channel.SnapshotAt, now - channel.ClientAt < ClientWindow,
                channel.History.Select(Clone).Reverse().ToArray());
    }

    public RemoteCommandView Enqueue(string machineId, RemoteCommand command)
    {
        Channel channel = Get(machineId);
        var view = new RemoteCommandView { Id = command.Id, Action = command.Action, CreatedAt = DateTimeOffset.UtcNow };
        lock (channel.Lock)
        {
            channel.Pending.Enqueue((command, view.CreatedAt));
            channel.History.Add(view);
            if (channel.History.Count > MaxHistory) channel.History.RemoveRange(0, channel.History.Count - MaxHistory);
        }
        Wake(channel);
        return Clone(view);
    }

    /// <summary>
    /// 客户端同步：保存结果和快照，然后等待新指令。
    /// 有人查看时最多等 2 秒（让客户端尽快送来下一张快照），否则最多等 25 秒。
    /// </summary>
    public async Task<(RemoteCommand[] Commands, bool Live)> SyncAsync(string machineId, ClientSync sync, CancellationToken cancellationToken)
    {
        Channel channel = Get(machineId);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Task signal;
        lock (channel.Lock)
        {
            channel.ClientAt = now;
            if (sync.Snapshot is { ValueKind: JsonValueKind.Object } snapshot)
            {
                channel.Snapshot = snapshot.Clone();
                channel.SnapshotAt = now;
            }
            foreach (SyncResult result in sync.Results ?? [])
            {
                RemoteCommandView? view = channel.History.FirstOrDefault(item => item.Id == result.Id);
                if (view is null || view.Status != "running") continue;
                view.Status = result.Ok ? "ok" : "failed";
                view.Message = Truncate(result.Message ?? "", 300);
                view.FinishedAt = now;
            }
            if (TakeLocked(channel, now) is { Length: > 0 } ready) return (ready, IsLiveLocked(channel, now));
            if (channel.Signal.Task.IsCompleted)
                channel.Signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            signal = channel.Signal.Task;
        }

        TimeSpan wait = IsLive(channel) ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(25);
        try { await signal.WaitAsync(wait, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { }

        now = DateTimeOffset.UtcNow;
        lock (channel.Lock)
        {
            channel.ClientAt = now;
            return (TakeLocked(channel, now), IsLiveLocked(channel, now));
        }
    }

    private bool IsLive(Channel channel)
    {
        lock (channel.Lock) return IsLiveLocked(channel, DateTimeOffset.UtcNow);
    }

    private static bool IsLiveLocked(Channel channel, DateTimeOffset now) => now - channel.ViewerAt < ViewerWindow;

    private static RemoteCommand[] TakeLocked(Channel channel, DateTimeOffset now)
    {
        ExpireLocked(channel, now);
        var result = new List<RemoteCommand>();
        while (channel.Pending.TryDequeue(out var item))
        {
            result.Add(item.Command);
            if (channel.History.FirstOrDefault(view => view.Id == item.Command.Id) is { } view) view.Status = "running";
        }
        return result.ToArray();
    }

    private static void ExpireLocked(Channel channel, DateTimeOffset now)
    {
        while (channel.Pending.TryPeek(out var item) && now - item.At > CommandTtl)
        {
            channel.Pending.Dequeue();
            if (channel.History.FirstOrDefault(view => view.Id == item.Command.Id) is { } view)
            {
                view.Status = "failed";
                view.Message = "设备未在 60 秒内领取指令，已作废。";
                view.FinishedAt = now;
            }
        }
        // 已领取但两分钟没有回传结果（例如客户端退出），不再显示为执行中。
        foreach (RemoteCommandView view in channel.History.Where(view => view.Status == "running" && now - view.CreatedAt > TimeSpan.FromMinutes(2)))
        {
            view.Status = "failed";
            view.Message = "设备没有回传执行结果。";
            view.FinishedAt = now;
        }
    }

    private static void Wake(Channel channel)
    {
        TaskCompletionSource signal;
        lock (channel.Lock) signal = channel.Signal;
        signal.TrySetResult();
    }

    private static RemoteCommandView Clone(RemoteCommandView view) => new()
    {
        Id = view.Id, Action = view.Action, CreatedAt = view.CreatedAt, Status = view.Status, Message = view.Message, FinishedAt = view.FinishedAt
    };

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
