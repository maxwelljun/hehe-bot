using System.Net;
using System.Text;

namespace YaxinLicense.Server;

/// <summary>
/// 公开下载页：列出 1.5.1 及以后各版本的安装包，供新用户首次下载安装。
/// 程序本身仍需管理员分配的账号登录，下载不需要登录。
/// </summary>
public static class DownloadPage
{
    public static readonly Version MinimumVersion = new(1, 5, 1);

    private static readonly (string Runtime, string Name, string Hint)[] Platforms =
    [
        ("win-x64", "Windows", "Windows 10/11 64 位"),
        ("osx-arm64", "macOS · Apple 芯片", "M1、M2、M3、M4 等 M 系列"),
        ("osx-x64", "macOS · Intel", "Intel 处理器的 Mac")
    ];

    public static IEnumerable<UpdatePackage> Packages(UpdateStore store) =>
        store.List().Where(package => package.ParsedVersion >= MinimumVersion);

    public static string Render(UpdateStore store)
    {
        UpdatePackage[] packages = Packages(store).ToArray();
        var html = new StringBuilder();
        html.Append("""
            <!doctype html>
            <html lang="zh-CN">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>亚信全桌监控 · 下载</title>
            <style>
              body { margin: 0; font: 14px/1.6 -apple-system, "PingFang SC", "Microsoft YaHei", sans-serif; background: #f4f6f8; color: #1f2933; }
              main { max-width: 920px; margin: 0 auto; padding: 32px 20px 48px; }
              h1 { font-size: 24px; margin: 0 0 4px; }
              h2 { font-size: 17px; margin: 32px 0 12px; }
              .muted { color: #6b7785; }
              .cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(240px, 1fr)); gap: 12px; margin-top: 20px; }
              .card { background: #fff; border: 1px solid #dde3ea; border-radius: 10px; padding: 16px; }
              .card h3 { margin: 0; font-size: 16px; }
              .card .ver { margin: 6px 0 12px; }
              a.button { display: inline-block; background: #1f6feb; color: #fff; text-decoration: none; padding: 7px 16px; border-radius: 6px; }
              a.button:hover { background: #1558c0; }
              table { width: 100%; border-collapse: collapse; background: #fff; border: 1px solid #dde3ea; border-radius: 10px; overflow: hidden; }
              th, td { text-align: left; padding: 8px 12px; border-bottom: 1px solid #edf0f3; vertical-align: top; }
              th { background: #f8fafc; font-weight: 600; }
              td.nowrap { white-space: nowrap; }
              code { font: 12px ui-monospace, Menlo, Consolas, monospace; word-break: break-all; color: #52606d; }
              .notes { white-space: pre-wrap; margin: 4px 0 0; color: #3e4c59; }
              ol { padding-left: 20px; }
            </style>
            </head>
            <body>
            <main>
              <h1>亚信全桌监控</h1>
              <div class="muted">请按电脑系统下载对应安装包。程序需要管理员分配的账号登录后才能使用；安装后可在程序内一键更新。</div>
              <div class="cards">
            """);
        foreach (var (runtime, name, hint) in Platforms)
        {
            UpdatePackage? latest = packages.Where(package => package.Runtime == runtime).MaxBy(package => package.ParsedVersion);
            html.Append("<div class=\"card\"><h3>").Append(name).Append("</h3><div class=\"muted\">").Append(hint).Append("</div>");
            if (latest is null)
            {
                html.Append("<div class=\"ver muted\">暂无安装包</div></div>");
                continue;
            }
            html.Append("<div class=\"ver\">最新版 v").Append(latest.Version).Append(" · ").Append(FormatSize(latest.Size)).Append("</div>")
                .Append("<a class=\"button\" href=\"/download/").Append(Encode(latest.FileName)).Append("\">下载</a></div>");
        }
        html.Append("""
              </div>
              <h2>安装步骤</h2>
              <ol>
                <li>Windows：解压后把整个文件夹放到桌面或 D 盘等可写目录（不要放在 C:\Program Files），运行 <code>YaxinMonitor.exe</code>。</li>
                <li>macOS：解压后把 <code>YaxinMonitor.app</code> 拖入“应用程序”再打开；首次打开如被拦截，请在“系统设置 → 隐私与安全性”中点击“仍要打开”。</li>
                <li>需要已安装 Google Chrome。使用说明见压缩包内的 README / USER-GUIDE。</li>
                <li>本站使用自签名证书，浏览器提示“不安全”时选择继续访问即可；可用下方 SHA-256 核对下载的文件。</li>
              </ol>
              <h2>全部版本</h2>
            """);
        if (packages.Length == 0)
        {
            html.Append("<div class=\"muted\">暂无安装包。</div>");
        }
        else
        {
            html.Append("<table><thead><tr><th>版本</th><th>平台</th><th>大小</th><th>SHA-256</th><th></th></tr></thead><tbody>");
            foreach (IGrouping<string, UpdatePackage> group in packages.GroupBy(package => package.Version)
                         .OrderByDescending(group => group.First().ParsedVersion))
            {
                bool first = true;
                UpdatePackage[] items = group.OrderBy(package => Array.FindIndex(Platforms, platform => platform.Runtime == package.Runtime)).ToArray();
                foreach (UpdatePackage package in items)
                {
                    html.Append("<tr>");
                    if (first)
                    {
                        html.Append("<td rowspan=\"").Append(items.Length).Append("\"><b>v").Append(package.Version).Append("</b><div class=\"muted\">")
                            .Append(package.UploadedAt.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd")).Append("</div>");
                        if (package.Notes.Length > 0) html.Append("<div class=\"notes\">").Append(Encode(package.Notes)).Append("</div>");
                        html.Append("</td>");
                        first = false;
                    }
                    string platform = Platforms.FirstOrDefault(item => item.Runtime == package.Runtime).Name ?? package.Runtime;
                    html.Append("<td class=\"nowrap\">").Append(platform).Append("</td><td class=\"nowrap\">").Append(FormatSize(package.Size)).Append("</td><td><code>")
                        .Append(package.Sha256).Append("</code></td><td class=\"nowrap\"><a href=\"/download/").Append(Encode(package.FileName)).Append("\">下载</a></td></tr>");
                }
            }
            html.Append("</tbody></table>");
        }
        html.Append("</main></body></html>");
        return html.ToString();
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private static string FormatSize(long bytes) => $"{bytes / 1024d / 1024d:0.0} MB";
}
