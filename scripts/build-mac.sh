#!/bin/zsh
set -euo pipefail

runtime="${1:-osx-arm64}"
case "$runtime" in
  osx-arm64|osx-x64) ;;
  *) echo "Usage: $0 [osx-arm64|osx-x64]" >&2; exit 2 ;;
esac

project_root="${0:A:h:h}"
version="1.2.9"
publish_dir="$project_root/artifacts/publish/yaxin-$runtime"
bundle_name="YaxinMonitor-$version-$runtime"
stage_dir="$project_root/artifacts/package/$bundle_name"
app_dir="$stage_dir/YaxinMonitor.app"
package_dir="$project_root/artifacts/package"

dotnet publish "$project_root/src/YaxinMonitor.Mac/YaxinMonitor.Mac.csproj" \
  -c Release -r "$runtime" --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=None -p:DebugSymbols=false \
  -o "$publish_dir" --nologo

rm -rf "$stage_dir"
mkdir -p "$app_dir/Contents/MacOS" "$app_dir/Contents/Resources"
cp "$publish_dir/YaxinMonitor" "$app_dir/Contents/MacOS/YaxinMonitor"
cp "$project_root/packaging/macos/Info.plist" "$app_dir/Contents/Info.plist"
cp "$project_root/docs/yaxin-mac-user-guide.md" "$stage_dir/README.md"
chmod +x "$app_dir/Contents/MacOS/YaxinMonitor"
codesign --force --deep --sign - "$app_dir"

archive="$project_root/artifacts/$bundle_name.zip"
rm -f "$archive"
(cd "$package_dir" && zip -r "$archive" "$bundle_name" -x '*/._*')
shasum -a 256 "$archive"
