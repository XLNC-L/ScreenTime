# publish.ps1 — 自包含发布 UI + Agent，并可选镜像到本地运行目录
#
# 用法:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\publish.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\publish.ps1 -Deploy
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\publish.ps1 -Deploy -SkipStop
#
# 和 build.cmd 的区别：build.cmd 是框架依赖（--self-contained false），
# 而 D:\ScreenTime 这份运行目录是自包含（自带 .NET 8 运行时）。
# 用 build.cmd 的产物覆盖运行目录会丢掉运行时，目标机可能打不开。

param(
    [string]$Target = "D:\ScreenTime",
    [switch]$Deploy,
    [switch]$SkipStop
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$staging = Join-Path $repo "ScreenTime-WinUI"

function Publish-Project {
    param([string]$Project)
    & dotnet publish (Join-Path $repo $Project) -c Release -r win-x64 --self-contained true -o $staging --nologo
    if ($LASTEXITCODE -ne 0) { throw "发布失败: $Project" }
}

# 发布后精简安装包体积与文件数（每次发布都要跑，dotnet publish 会把语言资源再复制回来）：
#   1. *.pdb 调试符号：发布版用不到，出问题时靠日志定位，不靠符号
#   2. Microsoft.DiaSymReader：只有加载 pdb 才需要，1 里删了 pdb 后它就没用了
#   3. 70 多个语言的 WinUI 框架资源（.mui）：界面只有中文，保留简中/繁中/英文即可
#
# 特意保留 Microsoft.UI.Xaml.Phone.dll —— 它是 WinUI 启动期的硬依赖，
# 删掉后界面直接起不来（已实测），不要当成"无关组件"裁掉。
function Remove-PublishExtras {
    param([string]$Dir)

    Get-ChildItem $Dir -Recurse -File -Filter *.pdb | Remove-Item -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $Dir "Microsoft.DiaSymReader.Native.amd64.dll") -Force -ErrorAction SilentlyContinue

    $keep = @("zh-CN", "zh-TW", "en-us", "en-US", "en-GB")
    # 语言目录名有两段式（zh-CN）也有三段式（sr-Latn-RS、ca-Es-VALENCIA），两段式正则会漏掉后者
    Get-ChildItem $Dir -Directory |
        Where-Object { $_.Name -cmatch '^[A-Za-z]{2,3}-[A-Za-z]{2,4}(-[A-Za-z]{2,})?$' -and $_.Name -cnotin $keep } |
        ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
}

if (-not $SkipStop) {
    foreach ($name in "ScreenTime", "ScreenTime.Agent") {
        Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force
    }
    Start-Sleep -Milliseconds 500
}

Publish-Project "ScreenTime.WinUI\ScreenTime.WinUI.csproj"
Publish-Project "ScreenTime.Agent\ScreenTime.Agent.csproj"

Remove-PublishExtras $staging

# WindowsPackageType=None 下 .xbf 要靠 csproj 的 CopyXbfToPublishDir 补齐，
# 缺了发布版启动即崩 —— 这里当硬性门槛校验。
$xbf = @(Get-ChildItem $staging -Recurse -Filter *.xbf)
if ($xbf.Count -ne 7) { throw "xbf 数量异常：$($xbf.Count)（应为 7）" }

$files = @(Get-ChildItem $staging -Recurse -File)
$size = [math]::Round((($files | Measure-Object Length -Sum).Sum) / 1MB, 1)
Write-Host ("发布目录: {0} 个文件 / {1} MB" -f $files.Count, $size) -ForegroundColor DarkGray

$version = (Get-Item (Join-Path $staging "ScreenTime.exe")).VersionInfo.ProductVersion
$head = (& git -C $repo rev-parse --short HEAD)

# `-r win-x64` 的还原会给 Core/Collect 的锁文件写入 RID 段，
# 而普通 restore 又不会保留，放着不管每次发布都会留下脏改动。
& git -C $repo restore -- ScreenTime.Core/packages.lock.json ScreenTime.Collect/packages.lock.json 2>$null

Write-Host ("发布完成: {0}（仓库 {1}）" -f $version, $head) -ForegroundColor Green

if (-not $Deploy) {
    Write-Host ("只发布到 {0}；确认无误后加 -Deploy 才镜像到 {1}" -f $staging, $Target) -ForegroundColor DarkGray
    exit 0
}

if (-not (Test-Path $Target)) { throw "运行目录不存在: $Target" }

# /MIR 让运行目录与发布目录完全一致（会删掉旧版残留，例如旧的 mscordaccore）。
# 排除 .workbuddy-ai：那是运行目录里的 AI 记忆，不属于发布产物。
& robocopy $staging $Target /MIR /XD ".workbuddy-ai" /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "镜像失败（robocopy 退出码 $LASTEXITCODE）" }

$xbf = @(Get-ChildItem $Target -Recurse -Filter *.xbf)
if ($xbf.Count -ne 7) { throw "运行目录 xbf 数量异常：$($xbf.Count)" }

Write-Host ("已同步到 {0}（{1} 个文件）" -f $Target, (Get-ChildItem $Target -File).Count) -ForegroundColor Green
Write-Host "从托盘或开始菜单启动 ScreenTime.Agent.exe 即可恢复采集。" -ForegroundColor DarkGray
