#!/bin/zsh
# 把 artifacts/ 下指定版本的安装包上传到授权服务器，客户端点击“检查更新”即可获取。
# 用法：scripts/publish-update.sh <版本> ["更新说明"]
# 需要能 SSH 到 VPS（会提示输入密码，或设置 SSHPASS 并安装 sshpass）。
set -euo pipefail

version="${1:-}"
notes="${2:-}"
if [[ ! "$version" =~ '^[0-9]+\.[0-9]+\.[0-9]+$' ]]; then
  echo "Usage: $0 <version> [notes]" >&2
  exit 2
fi

project_root="${0:A:h:h}"
host="root@45.152.67.26"
port=38311
remote_dir="/root/wp/hehebot/data/updates"
ssh_cmd=(ssh -p $port)
scp_cmd=(scp -P $port)
if [[ -n "${SSHPASS:-}" ]] && command -v sshpass >/dev/null; then
  ssh_cmd=(sshpass -e "${ssh_cmd[@]}")
  scp_cmd=(sshpass -e "${scp_cmd[@]}")
fi

packages=("$project_root"/artifacts/YaxinMonitor-$version-{win-x64,osx-arm64,osx-x64}.zip(N))
if (( ${#packages} == 0 )); then
  echo "artifacts/ 下没有 YaxinMonitor-$version-*.zip，请先运行打包脚本。" >&2
  exit 1
fi

"${ssh_cmd[@]}" "$host" "mkdir -p $remote_dir"
for package in "${packages[@]}"; do
  name="${package:t}"
  echo "上传 $name ..."
  # 先传临时文件再改名，避免客户端下载到不完整的包。
  "${scp_cmd[@]}" "$package" "$host:$remote_dir/.$name.part"
  "${ssh_cmd[@]}" "$host" "mv -f $remote_dir/.$name.part $remote_dir/$name"
done
if [[ -n "$notes" ]]; then
  printf '%s\n' "$notes" | "${ssh_cmd[@]}" "$host" "cat > $remote_dir/YaxinMonitor-$version.notes.txt"
fi
"${ssh_cmd[@]}" "$host" "ls -la $remote_dir"
