using System.Text;

namespace ScreenWatch.Windows;

internal sealed class ActivityLog
{
    public string RootDirectory { get; }
    public string ScreenshotDirectory => Path.Combine(RootDirectory, "captures");
    public string LogDirectory => Path.Combine(RootDirectory, "logs");

    public ActivityLog(string rootDirectory)
    {
        RootDirectory = rootDirectory;
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(ScreenshotDirectory);
    }

    public void Write(string message)
    {
        string path = Path.Combine(LogDirectory, $"{DateTime.Now:yyyy-MM-dd}.log");
        // A bounded daily file avoids unlimited growth if the interval is very short.
        if (File.Exists(path) && new FileInfo(path).Length >= 2 * 1024 * 1024)
            File.Move(path, path + ".previous", overwrite: true);
        File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}", new UTF8Encoding(false));
    }

    public string SaveCapture(Bitmap bitmap)
    {
        string path = Path.Combine(ScreenshotDirectory, $"match-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.png");
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Prune();
        return path;
    }

    public void Prune()
    {
        var captures = new DirectoryInfo(ScreenshotDirectory).EnumerateFiles("match-*.png")
            .OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
        long keptBytes = 0;
        for (int i = 0; i < captures.Length; i++)
        {
            keptBytes += captures[i].Length;
            if (i >= 200 || keptBytes > 256L * 1024 * 1024 || captures[i].LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7))
                TryDelete(captures[i]);
        }
        foreach (var file in new DirectoryInfo(LogDirectory).EnumerateFiles("*.log*"))
            if (file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-30)) TryDelete(file);
    }

    private static void TryDelete(FileInfo file)
    {
        try { file.Delete(); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
