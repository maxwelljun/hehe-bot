using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YaxinMonitor.Core;

public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string DirectoryPath { get; }
    public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    public string StatePath => Path.Combine(DirectoryPath, "state.json");
    public string LogsDirectory => Path.Combine(DirectoryPath, "logs");
    public string OrdersDirectory => Path.Combine(DirectoryPath, "orders");

    public StateStore(string directory)
    {
        DirectoryPath = Path.GetFullPath(directory);
    }

    public YaxinSettings LoadSettings()
    {
        if (!File.Exists(SettingsPath)) return new();
        using FileStream stream = OpenBounded(SettingsPath);
        var settings = JsonSerializer.Deserialize<YaxinSettings>(stream, JsonOptions)
            ?? throw new InvalidDataException("配置文件为空。");
        settings.Validate();
        return settings;
    }

    public EngineState LoadState()
    {
        if (!File.Exists(StatePath)) return new();
        using FileStream stream = OpenBounded(StatePath);
        var state = JsonSerializer.Deserialize<EngineState>(stream, JsonOptions)
            ?? throw new InvalidDataException("状态文件为空。");
        if (state.Version != 1) throw new InvalidDataException("状态文件版本不受支持。");
        if (state.Orders.Values.Any(order => order.Status is "Preparing" or "Submitted" or "Accepted"))
        {
            foreach (OrderState order in state.Orders.Values.Where(order => order.Status is "Preparing" or "Submitted" or "Accepted"))
                order.Status = "Unknown";
            foreach (TableRuntimeState table in state.Tables.Values)
                if (table.ActiveChase is { Status: ChaseStatus.AwaitingAcceptance or ChaseStatus.AwaitingSettlement } chase)
                    chase.Status = ChaseStatus.Unknown;
        }
        return state;
    }

    public void SaveSettings(YaxinSettings settings)
    {
        settings.Validate();
        SaveAtomic(SettingsPath, settings);
    }

    public void SaveState(EngineState state) => SaveAtomic(StatePath, state);

    public void AppendOrder(OrderState order)
    {
        Directory.CreateDirectory(OrdersDirectory);
        string path = Path.Combine(OrdersDirectory, $"{DateTime.Now:yyyy-MM-dd}.ndjson");
        string json = JsonSerializer.Serialize(order, JsonOptions);
        File.AppendAllText(path, json.ReplaceLineEndings("") + Environment.NewLine, new UTF8Encoding(false));
    }

    public void Log(string message)
    {
        Directory.CreateDirectory(LogsDirectory);
        string path = Path.Combine(LogsDirectory, $"{DateTime.Now:yyyy-MM-dd}.log");
        if (File.Exists(path) && new FileInfo(path).Length >= 4 * 1024 * 1024)
            File.Move(path, path + ".previous", overwrite: true);
        File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}", new UTF8Encoding(false));
    }

    private void SaveAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(DirectoryPath);
        string temporary = Path.Combine(DirectoryPath, $".{Path.GetFileName(path)}-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static FileStream OpenBounded(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > 8 * 1024 * 1024) throw new InvalidDataException($"文件过大：{info.Name}");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
}
