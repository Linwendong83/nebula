<#
.SYNOPSIS
    host-authority 任务卡常驻循环：每张卡起一个全新 opencode 会话，直到全部实现。

.DESCRIPTION
    背景：docs/host-authority/TASKS.md 要求一次只完成一张卡。
    同一会话连续做多卡会导致上下文污染，所以这里每卡调用一次
    `opencode run`（默认新建会话，不带 --continue/--session），跑完即弃，
    下一卡重新起全新会话。

    已完成判定：docs/host-authority/PROGRESS.md 出现 "## A18" 这类二级标题。
    当前 HEAD 为 e5e74004 时 A00-A17 已在 PROGRESS 落盘，未提交是预期状态，
    本脚本默认不做 git commit，只推进工作区 + PROGRESS + TestResults。

.EXAMPLE
    # 干跑检查队列
    pwsh tools/authority/invoke-authority-task-loop.ps1 -DryRun

    # 真跑：从 A18 起，跑完 A18-A25 + W00-W07 直到全部完成（无超时，失败即停）
    pwsh tools/authority/invoke-authority-task-loop.ps1 -StartTask A18

    # 只跑一卡验证链路
    pwsh tools/authority/invoke-authority-task-loop.ps1 -StartTask A18 -MaxTasks 1
#>
param(
    [string]$Repo = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$StartTask = '',
    [int]$MaxTasks = 0,
    [string]$Agent = 'build',
    [string]$Model = 'opencode/muse-spark-1.3-contributor-free',
    [string]$ModelVariant = 'xhigh',
    [string]$CliPath = '',
    [switch]$DryRun,
    [int]$SleepSecBetweenTasks = 10
)

$ErrorActionPreference = 'Stop'

$TaskOrder = @(
    'A00','A01','A02','A03','A04','A05','A06','A07','A08','A09',
    'A10','A11','A12','A13','A14','A15','A16','A17','A18','A19',
    'A20','A21','A22','A23','A24','A25',
    'W00','W01','W02','W03','W04','W05','W06','W07'
)

function Resolve-CliPath {
    param([string]$Hint)
    if ($Hint -and (Test-Path -LiteralPath $Hint)) { return $Hint }
    $desktopCli = Join-Path $env:APPDATA 'ai.opencode.desktop\cli\2.0.21\opencode-cli.exe'
    if (Test-Path -LiteralPath $desktopCli) { return $desktopCli }
    $cmd = Get-Command 'opencode-cli' -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $cmd = Get-Command 'opencode' -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw "找不到 opencode CLI。请用 -CliPath 指定 opencode-cli.exe 全路径。"
}

function Get-CompletedTasks {
    param([string]$ProgressFile)
    $done = @{}
    if (!(Test-Path -LiteralPath $ProgressFile)) { return $done }
    foreach ($line in (Get-Content -LiteralPath $ProgressFile)) {
        if ($line -match '^##\s+(A\d{2}|W\d{2})\b') { $done[$Matches[1]] = $true }
    }
    return $done
}

function Build-TaskPrompt {
    return "根据 C:\Users\54555\Desktop\nebula\docs\host-authority 开始实现"
}

function Rename-RunSession {
    param([string]$Task, [string]$Suffix, [long]$SinceMs)
    try {
        $raw = & $cli api get /api/session 2>$null | ConvertFrom-Json
        $repoNorm = $Repo.TrimEnd('\')
        $cand = $raw.data | Where-Object {
            $_.time.created -ge $SinceMs -and $_.location.directory.TrimEnd('\') -ieq $repoNorm
        } | Sort-Object { $_.time.created } -Descending | Select-Object -First 1
        if ($cand) {
            $body = @{ title = "host-authority $Task $Suffix" } | ConvertTo-Json -Compress
            & $cli api patch "/api/session/$($cand.id)" --data $body | Out-Null
            Write-LoopLog "$Task 会话已改名便于在 desktop 里找: $($cand.id.Substring(0,12))... -> host-authority $Task $Suffix"
        } else { Write-LoopLog "$Task 未找到本次 run 会话（改名跳过）" }
    } catch { Write-LoopLog "改名失败（不影响任务判定）: $_" }
}

$cli = Resolve-CliPath -Hint $CliPath
$progressFile = Join-Path $Repo 'docs/host-authority/PROGRESS.md'
$tasksDoc = Join-Path $Repo 'docs/host-authority/TASKS.md'
if (!(Test-Path -LiteralPath $progressFile)) { throw "缺少 $progressFile" }
if (!(Test-Path -LiteralPath $tasksDoc)) { throw "缺少 $tasksDoc" }

$completed = Get-CompletedTasks -ProgressFile $progressFile
$queue = @()
foreach ($t in $TaskOrder) {
    if ($StartTask -and ($TaskOrder.IndexOf($t) -lt $TaskOrder.IndexOf($StartTask))) { continue }
    if ($completed.ContainsKey($t)) { continue }
    $queue += $t
}
if ($MaxTasks -gt 0 -and $queue.Count -gt $MaxTasks) { $queue = $queue[0..($MaxTasks - 1)] }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$loopLog = Join-Path $Repo ("TestResults/authority/loop-$stamp.log")
New-Item -ItemType Directory -Path (Split-Path -Parent $loopLog) -Force | Out-Null

function Write-LoopLog {
    param([string]$Message)
    $line = "[$(Get-Date -Format 'o')] $Message"
    $line | Tee-Object -FilePath $loopLog -Append | Write-Output
}

Write-LoopLog "CLI: $cli"
Write-LoopLog "Repo: $Repo"
Write-LoopLog "已完成: $($completed.Keys -join ',')"
Write-LoopLog "待跑队列: $($queue -join ',')"
if ($queue.Count -eq 0) { Write-LoopLog "全部任务卡已在 PROGRESS 落盘，无需运行。"; return }

$modelFlag = $Model
if ($ModelVariant) { $modelFlag = "$Model#$ModelVariant" }

$ran = 0
foreach ($task in $queue) {
    $prompt = Build-TaskPrompt
    $taskLog = Join-Path $Repo ("TestResults/authority/loop-$task.log")
    Write-LoopLog "开始 $task，日志: $taskLog"
    if ($DryRun) {
        Write-LoopLog "[DryRun] 将执行: & `$cli run --agent $Agent --model $modelFlag --title 'host-authority/$task' '$prompt'"
        break
    }
    try { & $cli service status | Out-String | Tee-Object -FilePath $taskLog -Append | Write-Output }
    catch { Write-LoopLog "service status 失败（继续尝试 run）: $_" }

    # Git 自带的 head/tail 等供 run 子会话里的 agent 使用（只改本进程环境，不污染用户 shell）。
    $gitUsrBin = Join-Path (Split-Path -Parent ((Get-Command git -ErrorAction SilentlyContinue).Source)) 'usr\bin'
    if (!$gitUsrBin -or !(Test-Path -LiteralPath $gitUsrBin)) { $gitUsrBin = 'C:\Program Files\Git\usr\bin' }
    if ((Test-Path -LiteralPath $gitUsrBin) -and ($env:PATH -notlike "*$gitUsrBin*")) { $env:PATH = "$gitUsrBin;$env:PATH" }

    Write-LoopLog "已启动 $task，等待完成（Ctrl+C 即按失败停循环）…"
    $runStartMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $proc = Start-Process -FilePath $cli -WorkingDirectory $Repo `
        -ArgumentList @('run', '--agent', $Agent, '--model', $modelFlag, '--title', "host-authority/$task", $prompt) `
        -NoNewWindow -Wait -PassThru -RedirectStandardOutput "$taskLog.out" -RedirectStandardError "$taskLog.err"
    Get-Content -LiteralPath "$taskLog.out" -ErrorAction SilentlyContinue | Add-Content -Path $taskLog
    Get-Content -LiteralPath "$taskLog.err" -ErrorAction SilentlyContinue | Add-Content -Path $taskLog
    Remove-Item -LiteralPath "$taskLog.out", "$taskLog.err" -ErrorAction SilentlyContinue
    $exitCode = $proc.ExitCode
    Write-LoopLog "$task 退出码: $exitCode"
    if ($exitCode -eq 0) { Rename-RunSession -Task $task -Suffix '(done)' -SinceMs $runStartMs }
    else { Rename-RunSession -Task $task -Suffix "(FAILED exit $exitCode)" -SinceMs $runStartMs }

    $after = Get-CompletedTasks -ProgressFile $progressFile
    if (-not ($after.ContainsKey($task) -and $exitCode -eq 0)) {
        Write-LoopLog "$task 失败（退出码=$exitCode，PROGRESS含=$($after.ContainsKey($task)))，停止循环。见 $taskLog"
        throw "$task 失败，已停止。"
    }
    Write-LoopLog "$task 完成（PROGRESS 已含 ## $task，退出码 0）。"
    $ran++
    if ($MaxTasks -gt 0 -and $ran -ge $MaxTasks) { break }
    Start-Sleep -Seconds $SleepSecBetweenTasks
}

$final = Get-CompletedTasks -ProgressFile $progressFile
$missing = @($TaskOrder | Where-Object { -not $final.ContainsKey($_) })
Write-LoopLog "本轮跑完 $ran 卡。仍缺: $($missing -join ',')"
if ($missing.Count -eq 0) { Write-LoopLog "A00-A25 + W00-W07 全部在 PROGRESS 落盘，可以进入发布闸门复核。" }
