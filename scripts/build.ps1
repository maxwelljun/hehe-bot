param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    & dotnet build ScreenWatch.slnx -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

    & dotnet run --project tests/ScreenWatch.Core.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    & dotnet run --project tests/YaxinMonitor.Core.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Yaxin monitor tests failed.' }

    $publishDir = Join-Path $projectRoot "artifacts/publish/$Runtime"
    & dotnet publish src/ScreenWatch.Windows/ScreenWatch.Windows.csproj -c Release -r $Runtime `
        --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
        -o $publishDir --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

    $bundleName = "ScreenWatch-1.0.0-$Runtime"
    $stagingDir = Join-Path $projectRoot "artifacts/package/$bundleName"
    if (Test-Path $stagingDir) { Remove-Item -Recurse -Force $stagingDir }
    New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
    Copy-Item (Join-Path $publishDir 'ScreenWatch.exe') $stagingDir
    Copy-Item README.md (Join-Path $stagingDir 'README.txt')
    Copy-Item demo (Join-Path $stagingDir 'demo') -Recurse
    Copy-Item docs (Join-Path $stagingDir 'docs') -Recurse
    $archivePath = Join-Path $projectRoot "artifacts/$bundleName.zip"
    Compress-Archive -Path $stagingDir -DestinationPath $archivePath -Force
    $hash = (Get-FileHash -Algorithm SHA256 $archivePath).Hash.ToLowerInvariant()
    "$hash  $bundleName.zip" | Set-Content "$archivePath.sha256" -Encoding ascii
    Write-Host "Ready: $archivePath"

    $yaxinPublishDir = Join-Path $projectRoot "artifacts/publish/yaxin-$Runtime"
    & dotnet publish src/YaxinMonitor.Windows/YaxinMonitor.Windows.csproj -c Release -r $Runtime `
        --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
        -o $yaxinPublishDir --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Yaxin monitor publish failed.' }

    $yaxinBundleName = "YaxinMonitor-1.2.9-$Runtime"
    $yaxinStagingDir = Join-Path $projectRoot "artifacts/package/$yaxinBundleName"
    if (Test-Path $yaxinStagingDir) { Remove-Item -Recurse -Force $yaxinStagingDir }
    New-Item -ItemType Directory -Path $yaxinStagingDir -Force | Out-Null
    Copy-Item (Join-Path $yaxinPublishDir 'YaxinMonitor.exe') $yaxinStagingDir
    Copy-Item docs/yaxin-user-guide.md (Join-Path $yaxinStagingDir 'USER-GUIDE.md')
    Copy-Item docs/yaxin-integration-plan.md (Join-Path $yaxinStagingDir 'TECHNICAL-NOTES.md')
    $yaxinArchivePath = Join-Path $projectRoot "artifacts/$yaxinBundleName.zip"
    Compress-Archive -Path $yaxinStagingDir -DestinationPath $yaxinArchivePath -Force
    $yaxinHash = (Get-FileHash -Algorithm SHA256 $yaxinArchivePath).Hash.ToLowerInvariant()
    "$yaxinHash  $yaxinBundleName.zip" | Set-Content "$yaxinArchivePath.sha256" -Encoding ascii
    Write-Host "Ready: $yaxinArchivePath"
}
finally {
    Pop-Location
}
