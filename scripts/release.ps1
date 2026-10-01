# 产出 GitHub Release 的两种发行资产：
#   1) 便携版   MarkdownLite-<version>-portable.exe   —— 自包含单文件，双击即用
#   2) 安装程序 MarkdownLite-Setup-<version>.exe      —— Inno Setup per-user 安装器
#      安装器装的是「目录形态」发布（文件直接铺到安装目录），启动时无自解压环节，
#      首次启动明显快于单文件便携版（便携版首次要解压 68MB 并被安全软件首扫）。
#   外加 SHA256SUMS.txt 校验和。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release.ps1
#   powershell ... -File scripts\release.ps1 -SkipInstaller     # 只出便携版
#   powershell ... -File scripts\release.ps1 -OutputDir release
#
# 依赖：dotnet（解析逻辑与 publish.ps1 一致）；构建安装器需要 Inno Setup 6 的 ISCC.exe
#（choco install innosetup，或官方安装包）。找不到 ISCC 时跳过安装程序并提示。
# 版本号唯一真源：src\MarkdownLite\MarkdownLite.csproj 的 <Version>。

param(
    [string]$OutputDir = "release",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

# ---- 版本号：从 csproj 读取，不在此处手写 ----
$csproj = Join-Path $root "src\MarkdownLite\MarkdownLite.csproj"
if (-not (Test-Path $csproj)) { throw "找不到 $csproj" }
$m = [regex]::Match((Get-Content $csproj -Raw), '<Version>([^<]+)</Version>')
if (-not $m.Success) { throw "MarkdownLite.csproj 中没有 <Version> 节点" }
$version = $m.Groups[1].Value.Trim()
Write-Host "版本号（来自 csproj）：$version" -ForegroundColor Cyan

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) {
    $local = Join-Path $root ".dotnet\dotnet.exe"
    if (Test-Path $local) { $dotnet = $local }
}
if (-not $dotnet) { throw "未找到 dotnet CLI，请安装 .NET 8 SDK 或将便携版 SDK 放到 .dotnet\" }

$out = Join-Path $root $OutputDir
New-Item -ItemType Directory -Path $out -Force | Out-Null

# ---- 1. 便携版：复用 publish.ps1（自包含单文件 + R2R + 压缩）----
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "scripts\publish.ps1") -OutputDir (Join-Path $OutputDir "publish")
if ($LASTEXITCODE -ne 0) { throw "publish.ps1 失败" }
$portable = Join-Path $out "MarkdownLite-$version-portable.exe"
Move-Item -Path (Join-Path $out "publish\MarkdownLite.exe") -Destination $portable -Force
Remove-Item -Path (Join-Path $out "publish") -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "便携版：$portable" -ForegroundColor Green

# ---- 2. 安装器内容源：目录形态发布（无单文件自解压，R2R 预编译保留）----
# --locked-mode：与仓库 lock 文件严格校验，防止发布过程悄悄更新依赖
if (-not $SkipInstaller) {
    $installerSrc = Join-Path $out "installer-src"
    & $dotnet publish (Join-Path $root "src\MarkdownLite\MarkdownLite.csproj") `
        -c Release -r win-x64 --self-contained true `
        -p:PublishReadyToRun=true --no-restore `
        -o $installerSrc
    if ($LASTEXITCODE -ne 0) { throw "目录形态 publish 失败" }
    Remove-Item -Path (Join-Path $installerSrc "*.pdb") -Force -ErrorAction SilentlyContinue
    Write-Host "安装器内容源：$installerSrc" -ForegroundColor DarkGray

    # ---- 3. 安装程序：Inno Setup（需要 ISCC.exe）----
    $isccCandidates = @(
        (Get-Command iscc -ErrorAction SilentlyContinue).Source,
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) }
    if ($isccCandidates) {
        $iscc = $isccCandidates[0]
        Write-Host "使用 ISCC：$iscc" -ForegroundColor Cyan
        & $iscc "/DAppVersion=$version" (Join-Path $root "scripts\installer.iss")
        if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败" }
        Write-Host "安装程序：$(Join-Path $out "MarkdownLite-Setup-$version.exe")" -ForegroundColor Green
    }
    else {
        Write-Warning "未找到 ISCC.exe（Inno Setup 6）。已跳过安装程序；可运行 'choco install innosetup' 后重试，或仅发布便携版。"
    }

    # 内容源只服务于 ISCC 编译，打完包清掉，release 目录只留最终资产
    if (Test-Path $installerSrc) { Remove-Item -Path $installerSrc -Recurse -Force }
}

# ---- 4. 校验和（SHA256SUMS，两列制表符分隔：哈希 + 文件名）----
$assets = Get-ChildItem -Path $out -File | Where-Object { $_.Extension -eq ".exe" }
if ($assets) {
    $sums = Join-Path $out "SHA256SUMS.txt"
    $lines = $assets | ForEach-Object {
        "{0}`t{1}" -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
    }
    Set-Content -LiteralPath $sums -Value $lines -Encoding utf8
    Write-Host "校验和：$sums" -ForegroundColor Green
}

Write-Host "`nRelease 资产已就绪（$out）："
Get-ChildItem -Path $out -File | ForEach-Object { Write-Host ("  {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB)) }
Write-Host "`n发布方式：在 markdown-lite 公开仓库推送 v$version 标签，CI 的 release job 会自动产出同样资产并创建 Release；"
Write-Host "或手动在 GitHub Release 页上传以上文件。"
