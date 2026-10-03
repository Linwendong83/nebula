<#
.SYNOPSIS
    A23 driver: measures the authority replication cost on already-running isolated instances.

.DESCRIPTION
    Read-only with respect to the game: it waits for the run's host and client1, drives a combat
    workload through the A00 scenario driver (the same `attack` verb whose injections are logged as
    driver.inject), lets the replication settle, then asks each instance for its `perf` report.

    The report is what VALIDATION section 9's budget is judged against, so the archive keeps the
    budget in force, the capture/apply latency distributions, the per-family byte rates, the queue
    depths and the deferral high-water marks next to the raw counters. A run that could not be
    measured reports BLOCKED instead of an empty pass.

.EXAMPLE
    pwsh tools/authority/invoke-authority-perf.ps1 -RunId run-a23perf
    pwsh tools/authority/invoke-authority-perf.ps1 -RunId run-a23perf -SkipAttack
#>
param(
    [string]$RunId,
    [int]$Attacks = 8,
    [int]$DamagePerAttack = 400,
    [int]$IntervalTicks = 12,
    [int]$SettleSeconds = 25,
    [switch]$SkipAttack,
    [string]$EvidenceName = 'perf'
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (!$RunId) {
    $latest = Get-ChildItem -LiteralPath (Join-Path $repository 'TestResults/authority') -Directory -Filter 'run-*' -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | Select-Object -First 1
    if (!$latest) { throw 'No run found. Start instances first with run-authority-instance.ps1.' }
    $RunId = $latest.Name
}
$runRoot = Join-Path $repository ('TestResults/authority/' + $RunId)
$control = Join-Path $runRoot 'control'
$logs = Join-Path $runRoot 'logs'
$evidence = Join-Path $runRoot ('evidence/' + $EvidenceName)
if (!(Test-Path -LiteralPath $control)) { throw "Run '$RunId' has no control directory." }
New-Item -ItemType Directory -Path $evidence -Force | Out-Null

function Get-Status {
    param([string]$Role)
    $path = Join-Path $control ($Role + '.status')
    if (!(Test-Path -LiteralPath $path)) { return '' }
    return (Get-Content -LiteralPath $path -Raw)
}

function Get-StatusField {
    param([string]$Status, [string]$Name)
    $match = [regex]::Match($Status, "(?m)^$([regex]::Escape($Name))=(.*)$")
    if ($match.Success) { return $match.Groups[1].Value.Trim() }
    return ''
}

function Send-Command {
    param([string]$Role, [string]$Command, [int]$TimeoutSeconds = 60)
    $path = Join-Path $control ($Role + '.command')
    if (!(Test-Path -LiteralPath (Join-Path $control ($Role + '.pid')))) {
        throw "Instance '$Role' was never started in run $RunId."
    }
    $processId = [int](Get-Content -LiteralPath (Join-Path $control ($Role + '.pid')))
    if (!(Get-Process -Id $processId -ErrorAction SilentlyContinue)) {
        throw "Instance '$Role' (PID $processId) is not running."
    }
    $token = 'cmd' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $full = "$token $Command"
    [System.IO.File]::WriteAllText($path, $full)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $status = Get-Status -Role $Role
        if ((Get-StatusField $status 'command') -eq $full) { return $status }
    }
    return (Get-Status -Role $Role)
}

function Wait-Ready {
    param([string]$Role, [int]$TimeoutSeconds = 900, [switch]$RequireProgress)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $previousTick = -1
    while ((Get-Date) -lt $deadline) {
        $status = Get-Status -Role $Role
        if ((Get-StatusField $status 'ready') -eq '1') {
            if (!$RequireProgress) { return $true }
            $tick = [long]0
            [void][long]::TryParse((Get-StatusField $status 'tick'), [ref]$tick)
            if ($previousTick -ge 0 -and $tick -gt $previousTick) { return $true }
            $previousTick = $tick
        }
        Start-Sleep -Seconds 2
    }
    return $false
}

function Wait-AuthorityWelcome {
    param([string]$Role, [int]$TimeoutSeconds = 300)
    # The host's Connected count is the host's own roster bookkeeping, not proof that the client
    # joined the authority session; the client's log line is. A measurement taken before the welcome
    # would describe a client that is still a legacy peer.
    $log = Join-Path $runRoot ('instances/' + $Role + '/BepInEx/LogOutput.log')
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $log) {
            $text = Get-Content -LiteralPath $log -Raw
            if ($text -match '\[authority\] welcome accepted') { return $true }
        }
        Start-Sleep -Seconds 3
    }
    return $false
}

Write-Output "[perf] run=$RunId evidence=$evidence"

# 1. Both sides have to be up before anything is measured; a report produced while the client is
#    still loading would describe an empty replication stream and be meaningless as a budget test.
if (!(Wait-Ready -Role host -RequireProgress)) { throw 'Host never became ready.' }
Write-Output '[perf] host ready'
if (!(Wait-Ready -Role client1)) { throw 'client1 never became ready.' }
if (!(Wait-AuthorityWelcome -Role client1)) { throw 'client1 never joined the authority session.' }
$players = [int](Get-StatusField (Get-Status -Role host) 'players')
Write-Output "[perf] client1 joined the authority session (host players=$players)"

# 2. A combat workload so the replication stream actually carries lifecycle and bulk state.
if (!$SkipAttack) {
    $status = Send-Command -Role host -Command 'observe enemy'
    $outcome = Get-StatusField $status 'outcome'
    Write-Output "[perf] observe enemy -> $outcome"
    $status = Send-Command -Role host -Command "attack $Attacks $DamagePerAttack $IntervalTicks"
    Write-Output "[perf] attack -> $($status -split "`n" | Select-String '^outcome=')"
}

# 3. Let the stream settle: capture, digests and apply runs all have to be represented in the
#    counters, not just the frame that produced the damage.
Write-Output "[perf] settling for ${SettleSeconds}s"
Start-Sleep -Seconds $SettleSeconds

# 4. Collect the reports. `perf` writes <role>-perf.txt into the instance's log directory.
foreach ($role in @('host', 'client1')) {
    if (!(Test-Path -LiteralPath (Join-Path $control ($role + '.pid')))) { continue }
    $status = Send-Command -Role $role -Command 'perf'
    Write-Output "[perf] $role perf -> $(Get-StatusField $status 'outcome')"
    [void](Send-Command -Role $role -Command 'replica')
    foreach ($suffix in @('-perf.txt', '-replica.txt')) {
        $source = Join-Path $logs ($role + $suffix)
        if (Test-Path -LiteralPath $source) {
            Copy-Item -LiteralPath $source -Destination (Join-Path $evidence ($role + $suffix)) -Force
            Write-Output "  archived $role$suffix"
        }
    }
    $status | Set-Content -LiteralPath (Join-Path $evidence ($role + '-status.txt')) -Encoding UTF8
}

# 5. A one-screen summary so the budget judgement does not require reading every counter.
$summary = Join-Path $evidence 'perf-summary.txt'
$lines = @("run=$RunId", "attacks=$Attacks damage=$DamagePerAttack intervalTicks=$IntervalTicks",
    "settleSeconds=$SettleSeconds", "players=$players", '')
foreach ($role in @('host', 'client1')) {
    $file = Join-Path $evidence ($role + '-perf.txt')
    if (!(Test-Path -LiteralPath $file)) { $lines += "${role}: MISSING perf report"; continue }
    $lines += "--- $role ---"
    $lines += (Get-Content -LiteralPath $file | Where-Object {
        $_ -match '^perf |^capture |^apply |^queues |^backpressure |^traffic |^perf budget='
    })
}
$lines | Set-Content -LiteralPath $summary -Encoding UTF8
Write-Output "[perf] wrote $summary"
Get-Content -LiteralPath $summary | ForEach-Object { Write-Output $_ }
