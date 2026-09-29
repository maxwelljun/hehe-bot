using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace YaxinLicense.Server;

public sealed record UpdatePackage(string Runtime, string Version, string FileName, long Size, string Sha256, DateTimeOffset UploadedAt, string Notes)
{
    public Version ParsedVersion => System.Version.Parse(Version);
}

/// <summary>
/// 客户端更新包仓库：目录下的 <c>YaxinMonitor-版本-平台.zip</c> 即为可下载的包，
/// 每个平台取最高版本作为最新版。说明文字保存在 <c>YaxinMonitor-版本.notes.txt</c>。
/// </summary>
public sealed partial class UpdateStore(string directory)
{
    public static readonly string[] Runtimes = ["win-x64", "osx-arm64", "osx-x64"];
    public const long MaxPackageBytes = 400L * 1024 * 1024;

    private readonly ConcurrentDictionary<string, (long Size, DateTime Modified, string Hash)> _hashes = new();
    private readonly object _writeLock = new();

    [GeneratedRegex(@"^YaxinMonitor-(\d{1,4}\.\d{1,4}\.\d{1,4})-(win-x64|osx-arm64|osx-x64)\.zip$")]
    private static partial Regex PackageName();

    public string Directory => directory;

    public IReadOnlyList<UpdatePackage> List()
    {
        if (!System.IO.Directory.Exists(directory)) return [];
        var packages = new List<UpdatePackage>();
        foreach (string path in System.IO.Directory.EnumerateFiles(directory, "YaxinMonitor-*.zip"))
        {
            Match match = PackageName().Match(Path.GetFileName(path));
            if (!match.Success) continue;
            var info = new FileInfo(path);
            string version = match.Groups[1].Value;
            packages.Add(new UpdatePackage(match.Groups[2].Value, version, info.Name, info.Length, HashOf(info),
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), ReadNotes(version)));
        }
        return packages.OrderBy(package => package.Runtime, StringComparer.Ordinal)
            .ThenByDescending(package => package.ParsedVersion).ToArray();
    }

    public UpdatePackage? Latest(string runtime) =>
        List().Where(package => package.Runtime == runtime).MaxBy(package => package.ParsedVersion);

    public UpdatePackage? Find(string runtime, string version) =>
        List().FirstOrDefault(package => package.Runtime == runtime && package.Version == version);

    public string PathOf(UpdatePackage package) => Path.Combine(directory, package.FileName);

    /// <summary>保存上传的包：先写临时文件，校验文件名与压缩包内容后再原子替换。</summary>
    public async Task<UpdatePackage> SaveAsync(string fileName, Stream content, string? notes, CancellationToken cancellationToken)
    {
        Match match = PackageName().Match(fileName ?? "");
        if (!match.Success) throw new ArgumentException("文件名必须是 YaxinMonitor-版本-平台.zip，例如 YaxinMonitor-1.5.2-win-x64.zip。");
        string runtime = match.Groups[2].Value;
        System.IO.Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, $".upload-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream output = File.Create(temp))
            {
                await content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
            if (new FileInfo(temp).Length == 0) throw new ArgumentException("上传的文件为空。");
            ValidateArchive(temp, runtime);
            lock (_writeLock)
            {
                File.Move(temp, Path.Combine(directory, fileName!), overwrite: true);
                if (notes is not null) WriteNotes(match.Groups[1].Value, notes);
            }
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return Find(runtime, match.Groups[1].Value) ?? throw new InvalidOperationException("保存更新包失败。");
    }

    public void Delete(string fileName)
    {
        if (!PackageName().IsMatch(fileName ?? "")) throw new ArgumentException("文件名无效。");
        string path = Path.Combine(directory, fileName!);
        lock (_writeLock)
        {
            if (File.Exists(path)) File.Delete(path);
            _hashes.TryRemove(path, out _);
        }
    }

    public void SetNotes(string version, string notes)
    {
        if (!System.Version.TryParse(version, out _)) throw new ArgumentException("版本号无效。");
        lock (_writeLock) WriteNotes(version, notes);
    }

    private static void ValidateArchive(string path, string runtime)
    {
        string expected = runtime.StartsWith("win-", StringComparison.Ordinal)
            ? "YaxinMonitor.exe"
            : "YaxinMonitor.app/Contents/MacOS/YaxinMonitor";
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(path);
            if (!archive.Entries.Any(entry => entry.FullName.Replace('\\', '/').EndsWith(expected, StringComparison.Ordinal)))
                throw new ArgumentException($"压缩包中没有找到 {expected}，请确认上传的是 {runtime} 安装包。");
        }
        catch (InvalidDataException)
        {
            throw new ArgumentException("上传的文件不是有效的 zip 压缩包。");
        }
    }

    private string HashOf(FileInfo info)
    {
        if (_hashes.TryGetValue(info.FullName, out var cached) && cached.Size == info.Length && cached.Modified == info.LastWriteTimeUtc)
            return cached.Hash;
        using FileStream stream = info.OpenRead();
        string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        _hashes[info.FullName] = (info.Length, info.LastWriteTimeUtc, hash);
        return hash;
    }

    private string NotesPath(string version) => Path.Combine(directory, $"YaxinMonitor-{version}.notes.txt");

    private string ReadNotes(string version)
    {
        try { return File.Exists(NotesPath(version)) ? File.ReadAllText(NotesPath(version)).Trim() : ""; }
        catch { return ""; }
    }

    private void WriteNotes(string version, string notes)
    {
        notes = notes.Trim();
        if (notes.Length > 4000) throw new ArgumentException("更新说明不能超过 4000 字。");
        if (notes.Length == 0)
        {
            if (File.Exists(NotesPath(version))) File.Delete(NotesPath(version));
            return;
        }
        File.WriteAllText(NotesPath(version), notes);
    }
}
