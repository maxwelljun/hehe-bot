using System.Text.Json;

namespace ScreenWatch.Core;

public sealed class ProfileStore(string directory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");

    public MonitorSettings Load()
    {
        if (!File.Exists(SettingsPath)) return new();
        using var stream = File.OpenRead(SettingsPath);
        if (stream.Length > 65_536) throw new InvalidDataException("配置文件过大。");
        var settings = JsonSerializer.Deserialize<MonitorSettings>(stream, JsonOptions)
            ?? throw new InvalidDataException("配置文件为空。");
        settings.Validate();
        return settings;
    }

    public void Save(MonitorSettings settings)
    {
        settings.Validate();
        Directory.CreateDirectory(DirectoryPath);
        string temporary = Path.Combine(DirectoryPath, $".settings-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public string GetReferencePath(string fileName)
    {
        if (!MonitorSettings.IsReferenceFileName(fileName)) throw new ArgumentException("参考图片文件名无效。");
        return Path.Combine(DirectoryPath, fileName);
    }
}
