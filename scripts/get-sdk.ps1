# 把便携版 .NET SDK 取到仓库内的 .dotnet\ 目录。
#
# 背景：本仓库不要求系统级 .NET SDK，构建统一走 .dotnet\dotnet.exe（已 gitignore，删除无残留）。
# 新克隆的仓库没有这个目录，本脚本用官方 dotnet-install 脚本把它补齐。
#
# 用法：
#   pwsh scripts/get-sdk.ps1                        # 取 8.0 通道最新版
#   pwsh scripts/get-sdk.ps1 -Channel 8.0.425       # 指定具体版本
#   pwsh scripts/get-sdk.ps1 -Proxy http://127.0.0.1:7897   # 网络受限时走本地代理
#
# 需要能访问 https://dot.net（离线环境请手动放置 .dotnet\ 目录）。

param(
    [string]$Channel = "8.0",
    [string]$InstallDir = ".dotnet",
    [string]$Proxy = ""
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root $InstallDir
$dotnetExe = Join-Path $target "dotnet.exe"

if (Test-Path $dotnetExe) {
    Write-Host "已存在便携版 SDK：$dotnetExe" -ForegroundColor Yellow
    & $dotnetExe --version
    Write-Host "如需重新下载，请先删除 $target 后重试。" -ForegroundColor Yellow
    exit 0
}

if (-not [string]::IsNullOrWhiteSpace($Proxy)) {
    Write-Host "使用代理：$Proxy" -ForegroundColor Cyan
    $env:HTTP_PROXY = $Proxy
    $env:HTTPS_PROXY = $Proxy
}

$installer = Join-Path $env:TEMP "dotnet-install.ps1"
$uri = "https://dot.net/v1/dotnet-install.ps1"

Write-Host "下载官方安装脚本：$uri"
Invoke-WebRequest -Uri $uri -OutFile $installer -UseBasicParsing

Write-Host "安装 .NET SDK（通道 $Channel）到 $target"
$installArgs = @{
    Channel    = $Channel
    InstallDir = $target
    NoPath     = $true
}
if (-not [string]::IsNullOrWhiteSpace($Proxy)) {
    $installArgs.ProxyAddress = $Proxy
}

& $installer @installArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet-install 执行失败（退出码 $LASTEXITCODE）" }

Write-Host "`n完成：$dotnetExe（$(& $dotnetExe --version)）" -ForegroundColor Green
Write-Host "接下来可以直接：.\.dotnet\dotnet.exe build MarkdownLite.sln -c Release"
