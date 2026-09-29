# 授权服务器

`src/YaxinLicense.Server` 是亚信全桌监控的授权与监控后台，部署在 VPS `/root/wp/hehebot`。

- 公开下载页：`https://45.152.67.26:38443/download`（根地址也会跳转到这里），列出 1.5.1 及以后各版本的安装包和 SHA-256，无需登录，用于新用户首次下载。固定的最新版链接：`/download/latest/win-x64`、`/download/latest/osx-arm64`、`/download/latest/osx-x64`。
- 后台地址：`https://45.152.67.26:38443/admin`。证书是自签的，浏览器首次打开需要选择“继续访问”。
- 客户端固定了证书 SHA-256 指纹（见 `src/YaxinMonitor.Windows/LicenseClient.cs`）。**更换证书后必须同步修改指纹并重新打包客户端**，否则所有客户端都无法登录。
- 数据：`/root/wp/hehebot/data/license.db`（SQLite）。证书：`/root/wp/hehebot/cert/server.pfx`。
- 服务：`systemctl status|restart yaxin-license`，日志 `journalctl -u yaxin-license`。

## 更新部署

```bash
dotnet publish src/YaxinLicense.Server -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/publish/yaxin-license-linux-x64
scp -P 38311 artifacts/publish/yaxin-license-linux-x64/yaxin-license root@45.152.67.26:/root/wp/hehebot/yaxin-license.new
ssh -p 38311 root@45.152.67.26 'cd /root/wp/hehebot && mv yaxin-license.new yaxin-license && systemctl restart yaxin-license'
```

## 维护命令（在 VPS 上）

```bash
cd /root/wp/hehebot
systemctl stop yaxin-license
DOTNET_BUNDLE_EXTRACT_BASE_DIR=.net YAXIN_LICENSE_DATA=data ./yaxin-license set-admin-password '<新密码>'
DOTNET_BUNDLE_EXTRACT_BASE_DIR=.net YAXIN_LICENSE_DATA=data ./yaxin-license add-user <用户名> '<密码>'
systemctl start yaxin-license
```

## 接口

- `POST /api/v1/login`：用户名、密码、机器编号和机器信息 → 会话令牌。新机器自动登记并允许；账号或机器被禁用时拒绝。
- `POST /api/v1/report`（Bearer 令牌）：每 60 秒上报状态和日志；账号或机器被禁用时返回 `allowed=false`，客户端停止监控。
- 客户端只在启动时强制联网校验；运行中网络故障不影响使用。
- `GET /api/v1/update?runtime=&version=`（Bearer 令牌）：查询该平台是否有比 `version` 更高的版本，返回版本、大小、SHA-256 和更新说明。
- `GET /api/v1/update/download?runtime=&version=`（Bearer 令牌）：下载安装包，支持断点续传。
- 保留期：每台机器最近 20000 行日志，状态记录 30 天，登录记录 90 天。

## 客户端更新包

更新包放在 `/root/wp/hehebot/data/updates/`，文件名必须是 `YaxinMonitor-<版本>-<平台>.zip`（平台为 `win-x64`、`osx-arm64`、`osx-x64`），即打包脚本在 `artifacts/` 下生成的 zip。每个平台取最高版本作为最新版，客户端点击“检查更新”时下载。更新说明保存在 `YaxinMonitor-<版本>.notes.txt`。

发布新版本：

1. 修改版本号：两个客户端 csproj、`packaging/macos/Info.plist`、`scripts/build.ps1`、`scripts/build-mac.sh`。
2. 打包：`scripts/build.ps1`（Windows）和 `scripts/build-mac.sh osx-arm64`、`scripts/build-mac.sh osx-x64`。
3. 上传：`scripts/publish-update.sh <版本> "更新说明"`，或在后台“客户端更新”页上传 zip。

后台“客户端更新”页可以查看各平台最新版本和已升级机器数，上传、删除更新包和编辑说明。删除最高版本即可撤回一次发布，客户端会回到上一个版本为最新版。
