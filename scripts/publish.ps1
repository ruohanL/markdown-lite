# 将 MarkdownLite 发布为免安装的单文件 exe
# 用法：pwsh scripts/publish.ps1 [-Runtime win-x64] [-OutputDir publish]
param(
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "publish",
    # 追求极致启动速度（跳过启动时解压）可加 -NoCompress，代价是产物体积显著增大
    [switch]$NoCompress
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

Push-Location $root
try {
    # 优先使用系统 dotnet；否则回退到仓库内便携版 .dotnet\dotnet.exe
    $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    if (-not $dotnet) {
        $local = Join-Path $root ".dotnet\dotnet.exe"
        if (Test-Path $local) { $dotnet = $local }
        else { throw "未找到 dotnet CLI，请安装 .NET 8 SDK 或将便携版 SDK 放到 $local" }
    }

    # PublishReadyToRun=true：发布时预编译为本机代码，免去每次冷启动的全量 JIT，
    #   冷启动更快；代价是产物体积增大。
    # EnableCompressionInSingleFile —— R2R 可提升冷启动，压缩对启动影响小，
    #   默认开启压缩以显著减小产物体积；追求极致启动速度时加 -NoCompress 关掉。
    $compressFlag = if ($NoCompress) { "false" } else { "true" }

    & $dotnet publish (Join-Path $root "src\MarkdownLite\MarkdownLite.csproj") `
        -c Release `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeAllContentForSelfExtract=true `
        -p:EnableCompressionInSingleFile=$compressFlag `
        -p:PublishReadyToRun=true `
        -o (Join-Path $root $OutputDir)

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }

    $exe = Join-Path $root (Join-Path $OutputDir "MarkdownLite.exe")
    $sizeMB = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "`n发布完成：$exe （$sizeMB MB）" -ForegroundColor Green
    Write-Host "提示：自包含单文件包含 .NET 运行时与 wwwroot 离线资源，双击即可运行，全程无需联网。"
    if ($NoCompress) {
        Write-Host "提示：未压缩（-NoCompress），体积大但省去启动时的解压。"
    }
    else {
        Write-Host "提示：ReadyToRun 预编译 + 单文件压缩。"
    }
}
finally {
    Pop-Location
}
