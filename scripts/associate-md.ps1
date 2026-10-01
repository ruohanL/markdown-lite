# 为当前用户注册 .md 关联（无需管理员权限）
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\associate-md.ps1
#   powershell ... -File scripts\associate-md.ps1 -ExePath "D:\app\MarkdownLite.exe"
#   powershell ... -File scripts\associate-md.ps1 -SetDefault     # 顺便把 .md 默认程序改成 MarkdownLite
#   powershell ... -File scripts\associate-md.ps1 -Unregister     # 取消注册（含还原默认程序）
#
# 关于图标：应用自身的图标（exe 里的 app.ico）与 **.md 文件在资源管理器里显示的图标**是两回事。
# 后者由 ProgID 的 DefaultIcon 决定，所以这里会把 src\MarkdownLite\md-file.ico（那张白色文档卡片）
# 复制到 %LOCALAPPDATA%\MdReader\icons\ 并注册成 DefaultIcon——放应用数据目录而不是 exe 旁边，
# 是为了不破坏「单文件 exe」的分发形态（图标与 exe 放在哪里无关）。
param(
    [string]$ExePath = "",
    # 指定 .md 文件类型图标（md-file.ico）的来源；不传则按仓库布局找 src\MarkdownLite\md-file.ico。
    # 安装程序安装后的目录没有仓库结构，由 installer.iss 显式传入 {app}\md-file.ico。
    [string]$IconSource = "",
    [switch]$Unregister,
    [switch]$SetDefault
)

$ErrorActionPreference = "Stop"
$progId = "MarkdownLite.Markdown"
$extensions = @(".md", ".markdown")
$iconDir = Join-Path $env:LOCALAPPDATA "MdReader\icons"
$iconPath = Join-Path $iconDir "md-file.ico"
$backupRoot = "HKCU:\Software\MdReader\AssociationBackup"

function Remove-AppKey([string]$path) {
    if (Test-Path $path) { Remove-Item -Path $path -Recurse -Force -ErrorAction SilentlyContinue }
}

if ($Unregister) {
    # 还原被 -SetDefault 改过的 .md / .markdown 默认程序
    foreach ($ext in $extensions) {
        $slot = Join-Path $backupRoot $ext
        if (Test-Path $slot) {
            $previous = (Get-ItemProperty -Path $slot -ErrorAction SilentlyContinue).'(default)'
            if (-not [string]::IsNullOrEmpty($previous)) {
                Set-ItemProperty -Path "HKCU:\Software\Classes\$ext" -Name "(default)" -Value $previous
                Write-Host "已把 $ext 的默认程序还原为 $previous"
            }
            Remove-AppKey $slot
        }
    }
    Remove-AppKey $backupRoot
    Remove-AppKey "HKCU:\Software\Classes\$progId"
    Remove-AppKey "HKCU:\Software\Classes\Applications\MdReader.exe\DefaultIcon"
    foreach ($ext in $extensions) {
        $owp = "HKCU:\Software\Classes\$ext\OpenWithProgids"
        if (Test-Path $owp) {
            Remove-ItemProperty -Path $owp -Name $progId -ErrorAction SilentlyContinue
        }
    }
    Write-Host "已取消注册。" -ForegroundColor Green
    return
}

# ---- 定位 exe 与图标源 ----
$root = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $candidate = Join-Path $root "publish\MarkdownLite.exe"
    if (-not (Test-Path $candidate)) {
        Write-Host "未找到 publish\MarkdownLite.exe，请先运行 scripts/publish.ps1，或用 -ExePath 指定路径。" -ForegroundColor Yellow
        return
    }
    $ExePath = $candidate
}
$ExePath = (Resolve-Path $ExePath).Path

$iconSource = if ([string]::IsNullOrWhiteSpace($IconSource)) { Join-Path $root "src\MarkdownLite\md-file.ico" } else { $IconSource }
if (-not (Test-Path $iconSource)) {
    Write-Host "未找到文件类型图标 $iconSource（可运行 python scripts\make-icon.py 生成）；将退回用 exe 自带图标。" -ForegroundColor Yellow
    $iconValue = "`"$ExePath`",0"
} else {
    New-Item -ItemType Directory -Path $iconDir -Force | Out-Null
    Copy-Item -Path $iconSource -Destination $iconPath -Force
    $iconValue = $iconPath
}

# ---- 1. ProgID：命令、图标 ----
$key = "HKCU:\Software\Classes\$progId"
New-Item -Path $key -Force | Out-Null
Set-ItemProperty -Path $key -Name "(default)" -Value "Markdown 文档 (MarkdownLite)"
New-Item -Path "$key\DefaultIcon" -Force | Out-Null
Set-ItemProperty -Path "$key\DefaultIcon" -Name "(default)" -Value $iconValue
New-Item -Path "$key\shell\open\command" -Force | Out-Null
Set-ItemProperty -Path "$key\shell\open\command" -Name "(default)" -Value "`"$ExePath`" `"%1`""
New-Item -Path "$key\shell\view" -Force | Out-Null
Set-ItemProperty -Path "$key\shell\view" -Name "(default)" -Value "用 MarkdownLite 阅读"
New-Item -Path "$key\shell\view\command" -Force | Out-Null
Set-ItemProperty -Path "$key\shell\view\command" -Name "(default)" -Value "`"$ExePath`" `"%1`""

# ---- 2. 挂到 .md / .markdown 的「打开方式」列表（不强行改动现有默认程序） ----
foreach ($ext in $extensions) {
    $owp = "HKCU:\Software\Classes\$ext\OpenWithProgids"
    New-Item -Path $owp -Force | Out-Null
    New-ItemProperty -Path $owp -Name $progId -PropertyType String -Value "" -Force | Out-Null
}

# ---- 3. 注册应用能力，便于在 设置 > 默认应用 中选择 ----
$caps = "HKCU:\Software\Classes\Applications\MarkdownLite.exe\Capabilities"
New-Item -Path "$caps\FileAssociations" -Force | Out-Null
foreach ($ext in $extensions) {
    Set-ItemProperty -Path "$caps\FileAssociations" -Name $ext -Value $progId
}
New-Item -Path "HKCU:\Software\RegisteredApplications" -Force | Out-Null
Set-ItemProperty -Path "HKCU:\Software\RegisteredApplications" -Name "MarkdownLite" -Value $caps

# ---- 4. 「打开方式 → 始终」这条路径下，图标取自 Applications\MarkdownLite.exe\DefaultIcon ----
#        不写这一项的话，即使注册了 ProgID，资源管理器里 .md 仍是 exe 图标的缩小版。
New-Item -Path "HKCU:\Software\Classes\Applications\MarkdownLite.exe\DefaultIcon" -Force | Out-Null
Set-ItemProperty -Path "HKCU:\Software\Classes\Applications\MarkdownLite.exe\DefaultIcon" -Name "(default)" -Value $iconValue

# ---- 5. 可选：把 .md 的默认程序真正切到 MarkdownLite ----
if ($SetDefault) {
    foreach ($ext in $extensions) {
        $slot = Join-Path $backupRoot $ext
        New-Item -Path $slot -Force | Out-Null
        $current = (Get-ItemProperty -Path "HKCU:\Software\Classes\$ext" -ErrorAction SilentlyContinue).'(default)'
        if ([string]::IsNullOrEmpty($current)) { $current = "" }
        Set-ItemProperty -Path $slot -Name "(default)" -Value $current
        New-Item -Path "HKCU:\Software\Classes\$ext" -Force | Out-Null
        Set-ItemProperty -Path "HKCU:\Software\Classes\$ext" -Name "(default)" -Value $progId
        Write-Host "已把 $ext 的默认程序从 `"$current`" 改为 $progId（原值已备份，-Unregister 可还原）"
    }
}

Write-Host ""
Write-Host "已注册（仅当前用户）。" -ForegroundColor Green
Write-Host "  程序：$ExePath"
Write-Host "  图标：$iconValue"
Write-Host "在任意 .md 文件右键 > 打开方式 > 选择 MarkdownLite（勾选「始终」），"
Write-Host "或到 设置 > 应用 > 默认应用 里把 .md 指定给 MarkdownLite。"
if (-not $SetDefault) {
    Write-Host "想让脚本直接改默认程序，加 -SetDefault 参数。" -ForegroundColor DarkGray
}
Write-Host "提示：资源管理器有图标缓存，换图标后若没立刻生效，重启（或注销）一次即可。" -ForegroundColor DarkGray
