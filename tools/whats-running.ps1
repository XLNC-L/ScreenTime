# whats-running.ps1 — 一条命令看清「我在跑的程序」和「仓库代码」对不对得上
#
# 用法:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\whats-running.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\whats-running.ps1 -BuildDir "D:\别的构建目录"
#
# 原理: 构建产物 ScreenTime.dll 的 ProductVersion 形如 1.2.1+a15cc236...，
#       加号后面就是编译时的 git 提交号，用它跟仓库 HEAD 对比。

param(
    [string]$Repo = "",
    [string]$BuildDir = "D:\ScreenTime-v1.2.0-win-x64"
)

if ([string]::IsNullOrWhiteSpace($Repo)) {
    $Repo = Split-Path -Parent $PSScriptRoot
}
if (-not (Test-Path (Join-Path $Repo ".git"))) {
    Write-Host "找不到 git 仓库: $Repo" -ForegroundColor Red
    exit 1
}

# git 输出是 UTF-8，需要临时按 UTF-8 解码，否则中文提交信息会乱码
function Invoke-Git {
    param([string[]]$GitArgs)
    $prev = [Console]::OutputEncoding
    try {
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        $result = & git -C $Repo @GitArgs 2>$null
    } finally {
        [Console]::OutputEncoding = $prev
    }
    return $result
}

Write-Host "  "
Write-Host "===== ScreenTime 运行状态 =====" -ForegroundColor Cyan

# ---------- 运行中的进程 ----------
$procs = @(Get-Process -Name "ScreenTime", "ScreenTime.Agent" -ErrorAction SilentlyContinue)
if ($procs.Count -gt 0) {
    foreach ($p in $procs) {
        $path = ""
        try { $path = $p.Path } catch { $path = "(无权限读取路径)" }
        Write-Host ("进程: {0} (PID {1})" -f $p.Name, $p.Id)
        Write-Host ("      {0}" -f $path) -ForegroundColor DarkGray
    }
} else {
    Write-Host "进程: 未运行"
}

# ---------- 构建产物 ----------
$dll = Join-Path $BuildDir "ScreenTime.dll"
if (-not (Test-Path $dll)) {
    Write-Host "构建目录无效，找不到 $dll" -ForegroundColor Red
    exit 1
}
$vi = (Get-Item -LiteralPath $dll).VersionInfo
$prodVer = $vi.ProductVersion
$buildSha = ""
if ($prodVer -match "\+([0-9a-f]{7,40})") { $buildSha = $Matches[1] }
$buildTime = (Get-Item -LiteralPath $dll).LastWriteTime.ToString("yyyy-MM-dd HH:mm")

Write-Host ("构建: {0}" -f $BuildDir)
Write-Host ("      版本 {0}  编译于 {1}" -f $vi.FileVersion, $buildTime)
if ($buildSha) {
    Write-Host ("      产物提交 {0}" -f $buildSha.Substring(0, [Math]::Min(10, $buildSha.Length)))
} else {
    Write-Host "      产物未嵌入提交号" -ForegroundColor Yellow
}

# ---------- 仓库 ----------
$branch = Invoke-Git @("rev-parse", "--abbrev-ref", "HEAD")
$headSha = Invoke-Git @("rev-parse", "HEAD")
$headShort = $headSha.Substring(0, [Math]::Min(7, $headSha.Length))
$subject = Invoke-Git @("log", "-1", "--pretty=%s")
$dirty = @(Invoke-Git @("status", "--porcelain"))

Write-Host "  "
Write-Host ("仓库: 分支 {0} @ {1}" -f $branch, $headShort)
Write-Host ("      {0}" -f $subject) -ForegroundColor DarkGray
if ($dirty.Count -gt 0) {
    Write-Host ("      工作区有 {0} 处未提交改动" -f $dirty.Count) -ForegroundColor Yellow
} else {
    Write-Host "      工作区干净" -ForegroundColor Green
}

# ---------- 对比 ----------
Write-Host "  "
if (-not $buildSha) {
    Write-Host "无法从产物读取提交号，只能按版本号判断" -ForegroundColor Yellow
    exit 0
}
$exists = Invoke-Git @("cat-file", "-t", $buildSha)
if ($exists -ne "commit") {
    Write-Host "产物提交不在当前仓库历史里（可能是别的克隆编出来的）" -ForegroundColor Red
    exit 2
}
if ($headSha.StartsWith($buildSha) -or $buildSha.StartsWith($headSha)) {
    Write-Host "结论: 运行的就是当前代码" -ForegroundColor Green
    if ($dirty.Count -gt 0) {
        Write-Host "      未提交改动没被编译进去，要重新构建才生效" -ForegroundColor Yellow
    }
    exit 0
}

$behind = Invoke-Git @("rev-list", "--count", "$buildSha..HEAD")
$files = @(Invoke-Git @("diff", "--name-only", "$buildSha..HEAD"))
Write-Host ("结论: 仓库比运行版新 {0} 个提交、{1} 个文件" -f $behind, $files.Count) -ForegroundColor Yellow
Write-Host "  "
Write-Host "运行版之后的提交:" -ForegroundColor White
Invoke-Git @("log", "--oneline", "$buildSha..HEAD") | ForEach-Object { Write-Host ("   {0}" -f $_) }
Write-Host "  "
Write-Host "受影响的文件:" -ForegroundColor White
$show = $files
if ($show.Count -gt 20) { $show = $files[0..19] }
$show | ForEach-Object { Write-Host ("   {0}" -f $_) }
if ($files.Count -gt 20) { Write-Host ("   ... 另有 {0} 个" -f ($files.Count - 20)) -ForegroundColor DarkGray }
Write-Host "  "
Write-Host "回到运行版代码: git -C `"$Repo`" checkout $($buildSha.Substring(0,[Math]::Min(10,$buildSha.Length)))" -ForegroundColor Cyan
Write-Host "发布当前代码:   运行 build.cmd，产物提交会变成 $headShort" -ForegroundColor Cyan
Write-Host "  "
