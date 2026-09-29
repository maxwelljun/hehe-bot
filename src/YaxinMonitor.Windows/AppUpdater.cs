using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YaxinMonitor.Windows;

internal sealed record UpdateInfo(string Version, long Size, string Sha256, string Notes);

internal sealed record BackupVersion(string Version, string Path);

/// <summary>
/// 在线更新：从授权服务器下载本平台最新安装包，校验 SHA-256 后替换当前程序并重启。
/// 被替换的旧程序保存在备份目录，可一键回滚。Windows 替换 exe 文件，macOS 替换整个 .app。
/// </summary>
internal sealed class AppUpdater
{
    private const int KeepBackups = 3;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly LicenseClient _license;
    private readonly string _dataDirectory;

    public AppUpdater(LicenseClient license, string dataDirectory, string currentVersion)
    {
        _license = license;
        _dataDirectory = dataDirectory;
        CurrentVersion = currentVersion;
    }

    public string CurrentVersion { get; }

    public static string RuntimeId
    {
        get
        {
            string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            // Windows 包只有 x64 版本，ARM 设备通过仿真运行。
            return os == "win" ? "win-x64" : $"{os}-{arch}";
        }
    }

    private static bool IsBundle => OperatingSystem.IsMacOS();
    private string UpdatesDirectory => Path.Combine(_dataDirectory, "updates");
    private string NoticePath => Path.Combine(_dataDirectory, "update-notice.txt");

    private string BackupDirectory => IsBundle
        ? Path.Combine(_dataDirectory, "backup")
        : Path.Combine(Path.GetDirectoryName(InstallPath())!, "backup");

    private static string BackupExtension => IsBundle ? ".app" : ".exe";

    /// <summary>返回上次更新或回滚写下的提示，并删除提示文件。</summary>
    public static string? ConsumeNotice(string dataDirectory)
    {
        string path = Path.Combine(dataDirectory, "update-notice.txt");
        try
        {
            if (!File.Exists(path)) return null;
            string text = File.ReadAllText(path).Trim();
            File.Delete(path);
            return text.Length == 0 ? null : text;
        }
        catch
        {
            return null;
        }
    }

    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        string path = $"api/v1/update?runtime={Uri.EscapeDataString(RuntimeId)}&version={Uri.EscapeDataString(CurrentVersion)}";
        using HttpResponseMessage response = await _license.GetAuthorizedAsync(path, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        CheckResponse? body = null;
        try { body = await response.Content.ReadFromJsonAsync<CheckResponse>(JsonOptions, cancellationToken).ConfigureAwait(false); }
        catch (JsonException) { }
        catch (NotSupportedException) { }
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(body?.Message is { Length: > 0 } message ? message : $"检查更新失败（{(int)response.StatusCode}）。");
        if (body is not { Available: true, Version.Length: > 0, Sha256.Length: 64 } || body.Size <= 0) return null;
        if (!Version.TryParse(body.Version, out Version? latest) || latest <= Version.Parse(CurrentVersion)) return null;
        return new UpdateInfo(body.Version, body.Size, body.Sha256, body.Notes ?? "");
    }

    /// <summary>下载安装包到数据目录并校验大小与 SHA-256，返回压缩包路径。</summary>
    public async Task<string> DownloadAsync(UpdateInfo update, IProgress<int>? progress, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(UpdatesDirectory);
        string archive = Path.Combine(UpdatesDirectory, $"YaxinMonitor-{update.Version}-{RuntimeId}.zip");
        if (File.Exists(archive) && await HashFileAsync(archive, cancellationToken).ConfigureAwait(false) == update.Sha256)
        {
            progress?.Report(100);
            return archive;
        }
        string partial = archive + ".part";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(20));
        string path = $"api/v1/update/download?runtime={Uri.EscapeDataString(RuntimeId)}&version={Uri.EscapeDataString(update.Version)}";
        using (HttpResponseMessage response = await _license.GetAuthorizedAsync(path, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"下载更新失败（{(int)response.StatusCode}）。");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using Stream source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (FileStream output = File.Create(partial))
            {
                byte[] buffer = new byte[128 * 1024];
                long received = 0;
                int lastPercent = -1;
                int read;
                while ((read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    received += read;
                    if (received > update.Size) throw new InvalidDataException("下载的文件大小与服务器登记不符。");
                    int percent = (int)(received * 100 / update.Size);
                    if (percent != lastPercent) progress?.Report(lastPercent = percent);
                }
                if (received != update.Size) throw new InvalidDataException("下载不完整，请重试。");
            }
            string actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (actual != update.Sha256.ToLowerInvariant())
            {
                File.Delete(partial);
                throw new InvalidDataException("安装包校验失败（SHA-256 不一致），已丢弃，请重试。");
            }
        }
        File.Move(partial, archive, overwrite: true);
        return archive;
    }

    /// <summary>检查当前程序能否被替换；不能替换时抛出说明原因的异常。</summary>
    public void EnsureInstallable()
    {
        string install = InstallPath();
        string parent = Path.GetDirectoryName(install)!;
        if (IsBundle && install.Contains("/AppTranslocation/", StringComparison.Ordinal))
            throw new InvalidOperationException("macOS 正在隔离运行本程序（从下载目录直接打开）。请先把 YaxinMonitor.app 拖到“应用程序”文件夹，从那里打开后再更新。");
        try
        {
            string probe = Path.Combine(parent, $".yaxin-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException($"程序所在目录没有写入权限：{parent}。请把程序放到有写入权限的位置（例如桌面或“应用程序”文件夹）后再更新。");
        }
    }

    /// <summary>
    /// 解压新版本替换当前程序，旧程序移入备份目录，然后安排在本进程退出后启动新程序。
    /// 返回后调用方必须尽快退出进程。
    /// </summary>
    public void InstallAndRestart(UpdateInfo update, string archive)
    {
        EnsureInstallable();
        string install = InstallPath();
        string staging = Path.Combine(UpdatesDirectory, "staging-" + update.Version);
        DeletePath(staging);
        Directory.CreateDirectory(staging);
        try
        {
            Extract(archive, staging);
            string extracted = FindExtractedProgram(staging);
            // 先把新程序放到安装目录旁边，跨磁盘复制在这一步完成，失败时当前程序不受影响。
            string incoming = Path.Combine(Path.GetDirectoryName(install)!, $".YaxinMonitor-{update.Version}{BackupExtension}.new");
            DeletePath(incoming);
            MovePath(extracted, incoming);
            PrepareProgram(incoming);

            string backup = BackupCurrent(install);
            try
            {
                MovePath(incoming, install);
            }
            catch
            {
                MovePath(backup, install);
                throw;
            }
            if (!IsBundle) CopySideFiles(Path.GetDirectoryName(extracted)!, Path.GetDirectoryName(install)!);
            PruneBackups();
            WriteNotice($"已从 v{CurrentVersion} 更新到 v{update.Version}。旧版本已备份，可点击“回滚版本”恢复。");
        }
        finally
        {
            DeletePath(staging);
        }
        try { File.Delete(archive); } catch { }
        ScheduleRestart(install);
    }

    /// <summary>可回滚的版本：优先取低于当前版本的最高备份，否则取最近一次备份。</summary>
    public BackupVersion? RollbackTarget()
    {
        BackupVersion[] backups;
        try { backups = ListBackups(); }
        catch { return null; }
        Version current = Version.Parse(CurrentVersion);
        return backups.Where(backup => Version.Parse(backup.Version) < current).MaxBy(backup => Version.Parse(backup.Version))
            ?? backups.Where(backup => backup.Version != CurrentVersion).MaxBy(backup => GetWriteTime(backup.Path));
    }

    public void RollbackAndRestart(BackupVersion target)
    {
        EnsureInstallable();
        string install = InstallPath();
        string incoming = Path.Combine(Path.GetDirectoryName(install)!, $".YaxinMonitor-{target.Version}{BackupExtension}.rollback");
        DeletePath(incoming);
        MovePath(target.Path, incoming);
        string backup;
        try
        {
            backup = BackupCurrent(install);
        }
        catch
        {
            MovePath(incoming, target.Path);
            throw;
        }
        try
        {
            MovePath(incoming, install);
        }
        catch
        {
            MovePath(backup, install);
            MovePath(incoming, target.Path);
            throw;
        }
        PruneBackups();
        WriteNotice($"已从 v{CurrentVersion} 回滚到 v{target.Version}。");
        ScheduleRestart(install);
    }

    private static string InstallPath()
    {
        string process = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序位置。");
        if (!IsBundle) return process;
        // .../YaxinMonitor.app/Contents/MacOS/YaxinMonitor
        DirectoryInfo? bundle = Directory.GetParent(process)?.Parent?.Parent;
        if (bundle is null || !bundle.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前不是以 YaxinMonitor.app 方式运行，无法在线更新。");
        return bundle.FullName;
    }

    private string BackupCurrent(string install)
    {
        Directory.CreateDirectory(BackupDirectory);
        string backup = Path.Combine(BackupDirectory, $"YaxinMonitor-{CurrentVersion}{BackupExtension}");
        DeletePath(backup);
        MovePath(install, backup);
        try
        {
            if (IsBundle) Directory.SetLastWriteTimeUtc(backup, DateTime.UtcNow);
            else File.SetLastWriteTimeUtc(backup, DateTime.UtcNow);
        }
        catch { }
        return backup;
    }

    private BackupVersion[] ListBackups()
    {
        if (!Directory.Exists(BackupDirectory)) return [];
        IEnumerable<string> entries = IsBundle
            ? Directory.EnumerateDirectories(BackupDirectory, "YaxinMonitor-*.app")
            : Directory.EnumerateFiles(BackupDirectory, "YaxinMonitor-*.exe");
        var backups = new List<BackupVersion>();
        foreach (string entry in entries)
        {
            string name = Path.GetFileNameWithoutExtension(entry)["YaxinMonitor-".Length..];
            if (Version.TryParse(name, out Version? version) && version.Build >= 0) backups.Add(new BackupVersion(name, entry));
        }
        return backups.ToArray();
    }

    private void PruneBackups()
    {
        try
        {
            foreach (BackupVersion stale in ListBackups().OrderByDescending(backup => GetWriteTime(backup.Path)).Skip(KeepBackups))
                DeletePath(stale.Path);
        }
        catch { }
    }

    private static DateTime GetWriteTime(string path) =>
        Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path);

    private static void Extract(string archive, string destination)
    {
        if (IsBundle)
        {
            // ditto 能完整保留可执行权限和代码签名文件。
            Run("/usr/bin/ditto", "-x", "-k", archive, destination);
            return;
        }
        ZipFile.ExtractToDirectory(archive, destination, overwriteFiles: true);
    }

    private static string FindExtractedProgram(string staging)
    {
        string? found = IsBundle
            ? Directory.EnumerateDirectories(staging, "YaxinMonitor.app", SearchOption.AllDirectories).FirstOrDefault()
            : Directory.EnumerateFiles(staging, "YaxinMonitor.exe", SearchOption.AllDirectories).FirstOrDefault();
        if (found is null) throw new InvalidDataException("安装包中没有找到程序文件。");
        if (IsBundle && !File.Exists(Path.Combine(found, "Contents", "MacOS", "YaxinMonitor")))
            throw new InvalidDataException("安装包中的 YaxinMonitor.app 不完整。");
        return found;
    }

    private static void PrepareProgram(string path)
    {
        if (!OperatingSystem.IsMacOS()) return;
        string executable = Path.Combine(path, "Contents", "MacOS", "YaxinMonitor");
        File.SetUnixFileMode(executable, File.GetUnixFileMode(executable)
            | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        try { Run("/usr/bin/xattr", "-dr", "com.apple.quarantine", path); } catch { }
    }

    private static void CopySideFiles(string sourceDirectory, string targetDirectory)
    {
        foreach (string file in Directory.EnumerateFiles(sourceDirectory, "*.md"))
        {
            try { File.Copy(file, Path.Combine(targetDirectory, Path.GetFileName(file)), overwrite: true); }
            catch { }
        }
    }

    private void WriteNotice(string message)
    {
        try { File.WriteAllText(NoticePath, message); } catch { }
    }

    /// <summary>启动一个等待本进程退出后再打开新程序的辅助进程。</summary>
    private static void ScheduleRestart(string install)
    {
        int pid = Environment.ProcessId;
        ProcessStartInfo startInfo;
        if (IsBundle)
        {
            startInfo = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            startInfo.ArgumentList.Add("-c");
            // open 启动的新进程不继承当前环境，自定义的 YAXIN_MONITOR_* 变量（数据目录、控制端口等）需要显式透传。
            var environment = new StringBuilder();
            var passthrough = new List<string>();
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is not string key || !key.StartsWith("YAXIN_MONITOR_", StringComparison.Ordinal)) continue;
                passthrough.Add($"{key}={entry.Value}");
                environment.Append($" --env \"${passthrough.Count}\"");
            }
            startInfo.ArgumentList.Add($"while kill -0 {pid} 2>/dev/null; do sleep 0.3; done; sleep 0.5; exec /usr/bin/open -n{environment} \"$0\" --args --after-update");
            startInfo.ArgumentList.Add(install);
            foreach (string item in passthrough) startInfo.ArgumentList.Add(item);
        }
        else
        {
            string script = $"Wait-Process -Id {pid} -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500; "
                + $"Start-Process -FilePath '{install.Replace("'", "''")}' -WorkingDirectory '{Path.GetDirectoryName(install)!.Replace("'", "''")}'";
            startInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-EncodedCommand",
                         Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
                startInfo.ArgumentList.Add(argument);
        }
        using Process? helper = Process.Start(startInfo);
        if (helper is null) throw new InvalidOperationException("程序已替换，但无法自动重启，请手动打开程序。");
    }

    private static void MovePath(string source, string destination)
    {
        if (File.Exists(source))
        {
            File.Move(source, destination, overwrite: false);
            return;
        }
        try
        {
            Directory.Move(source, destination);
        }
        catch (IOException) when (IsBundle && Directory.Exists(source) && !Directory.Exists(destination))
        {
            // 跨磁盘时 Directory.Move 不可用，改为完整复制后删除源目录。
            Run("/usr/bin/ditto", source, destination);
            Directory.Delete(source, recursive: true);
        }
    }

    private static void DeletePath(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path)) File.Delete(path);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static void Run(string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"无法运行 {executable}。");
        string error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException($"{Path.GetFileName(executable)} 失败：{error.Trim()}");
    }

    private sealed record CheckResponse(bool Available, string? Version, long Size, string? Sha256, string? Notes, string? Message);
}
