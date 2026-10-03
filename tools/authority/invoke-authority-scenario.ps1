<#
.SYNOPSIS
    Drives the A00 baseline scenarios against already-running isolated instances and collects
    the evidence into TestResults/authority/<run>/evidence/.

.DESCRIPTION
    Three scenarios from TASKS.md A00:
      sustained-attack  Same enemy under continuous attack; host hp before/after per hit.
      repair-dispatch   Damaged building next to mecha/base; who dispatches repair drones.
      third-player      Third player joins/leaves a planet; observe host hp across the join.

    Each scenario writes a transcript, the filtered JSONL record, and a summary. Scenarios that
    cannot run (missing instance, no target in the sandbox) report that explicitly instead of
    producing a passing result.

.EXAMPLE
    pwsh tools/authority/invoke-authority-scenario.ps1 -Scenario sustained-attack
    pwsh tools/authority/invoke-authority-scenario.ps1 -Scenario repair-dispatch -Attacks 6
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('sustained-attack', 'repair-dispatch', 'third-player', 'all')]
    [string]$Scenario,

    [string]$RunId,
    [int]$Attacks = 8,
    [int]$DamagePerAttack = 400,
    [int]$IntervalTicks = 12,
    [int]$WaitSeconds = 90,
    [int]$RepairWaitSeconds = 150,
    [switch]$KeepObserved
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
$evidence = Join-Path $runRoot ('evidence/' + $Scenario)
if (!(Test-Path -LiteralPath $control)) { throw "Run '$RunId' has no control directory." }
New-Item -ItemType Directory -Path $evidence -Force | Out-Null

function Send-Command {
    param([string]$Role, [string]$Command, [int]$TimeoutSeconds = 30)
    $path = Join-Path $control ($Role + '.command')
    if (!(Test-Path -LiteralPath (Join-Path $control ($Role + '.pid')))) {
        throw "Instance '$Role' was never started in run $RunId."
    }
    $processId = [int](Get-Content -LiteralPath (Join-Path $control ($Role + '.pid')))
    if (!(Get-Process -Id $processId -ErrorAction SilentlyContinue)) {
        throw "Instance '$Role' (PID $processId) is not running."
    }
    # The driver only reacts to changed file content, so each dispatch carries a unique token
    # in the first word (the driver ignores words[0]).
    $token = 'cmd' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $full = "$token $Command"
    [System.IO.File]::WriteAllText($path, $full)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $status = Get-Status -Role $Role
        # The driver echoes the exact command it consumed; matching on it removes the race
        # where the previous command's outcome is still in the status file.
        if ((Get-StatusField $status 'command') -eq $full) { return $status }
    }
    return (Get-Status -Role $Role)
}

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

function Wait-Ready {
    param([string]$Role, [int]$TimeoutSeconds = 600, [switch]$RequireProgress)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $previousTick = -1
    while ((Get-Date) -lt $deadline) {
        $status = Get-Status -Role $Role
        if ((Get-StatusField $status 'ready') -eq '1') {
            if (!$RequireProgress) { return $true }
            # A dedicated host reports IsGameLoaded while its logic frame is still paused, and
            # rejects clients with HostStillLoading until the tick actually advances. Require one
            # observed increase before calling it ready.
            $tick = [long]0
            [void][long]::TryParse((Get-StatusField $status 'tick'), [ref]$tick)
            if ($previousTick -ge 0 -and $tick -gt $previousTick) { return $true }
            $previousTick = $tick
        }
        Start-Sleep -Seconds 2
    }
    return $false
}

function Save-Transcript {
    param([string]$Name, [string[]]$Lines)
    $path = Join-Path $evidence $Name
    $Lines | Set-Content -LiteralPath $path -Encoding UTF8
    Write-Output "  wrote $path"
}

function Get-Records {
    param([string]$Role, [string[]]$Events)
    $path = Join-Path $logs ($Role + '.jsonl')
    if (!(Test-Path -LiteralPath $path)) { return @() }
    $filter = if ($Events) { $Events -join '|' } else { $null }
    Get-Content -LiteralPath $path | Where-Object {
        $_.Trim().Length -gt 0 -and (!$filter -or $_ -match ('"event":"(' + $filter + ')"'))
    }
}

# ---------------------------------------------------------------- scenario 1: sustained attack
function Invoke-SustainedAttack {
    Write-Output '[sustained-attack] observing one enemy under continuous attack'
    if (!(Wait-Ready -Role host -RequireProgress)) { throw 'Host never became ready.' }
    $observe = Send-Command -Role host -Command 'observe enemy'
    $enemyLine = [regex]::Match($observe, '(?m)^target=enemy#(\d+)').Groups[0].Value
    Save-Transcript -Name 'observe.txt' -Lines @($observe, "# parsed: $enemyLine")
    if ($observe -notmatch 'outcome=ok:observing-enemy') {
        throw "Host could not observe an enemy: $observe"
    }
    $attack = Send-Command -Role host -Command "attack $Attacks $DamagePerAttack $IntervalTicks 1"
    Save-Transcript -Name 'attack.txt' -Lines @($attack)
    Write-Output '  waiting for the injected attack plan to finish...'
    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    while ((Get-Date) -lt $deadline) {
        $status = Get-Status -Role host
        if ((Get-StatusField $status 'outcome') -like 'ok:plan-complete*') { break }
        Start-Sleep -Seconds 2
    }
    Start-Sleep -Seconds 3
    $null = Send-Command -Role host -Command 'sample'
    $null = Send-Command -Role host -Command 'sweep'
    $null = Send-Command -Role host -Command 'probe'
    $summary = New-Object System.Collections.Generic.List[string]
    $summary.Add("scenario=sustained-attack")
    $summary.Add("runId=$RunId")
    $summary.Add("attacks=$Attacks damagePerAttack=$DamagePerAttack intervalTicks=$IntervalTicks")
    $summary.Add("observedTarget=$enemyLine")
    $summary.Add('')
    $summary.Add('--- host hp transitions (hp.tick / damage.entry / sample.change) ---')
    $summary.AddRange([string[]](Get-Records -Role host -Events @('hp.tick', 'damage.entry', 'damage.ground.local', 'sample.change', 'hp.zerohp.begin', 'hp.zerohp.end')))
    $summary.Add('')
    $summary.Add('--- enemy sweep ---')
    $sweepPath = Join-Path $logs 'host-sweep.txt'
    if (Test-Path -LiteralPath $sweepPath) { $summary.AddRange([string[]](Get-Content -LiteralPath $sweepPath)) }
    else { $summary.Add('no sweep file') }
    Save-Transcript -Name 'summary.txt' -Lines $summary.ToArray()
    $increases = ($summary | Where-Object { $_ -match '"event":"hp.tick"' -and $_ -match '"hpAfter":\s*(\d+)' }).Count
    Write-Output "  hp.tick records: $increases"
}

# ------------------------------------------------------- scenario 2: repair drone dispatch
function Invoke-RepairDispatch {
    Write-Output '[repair-dispatch] damaging a building and observing repair dispatch'
    if (!(Wait-Ready -Role host -RequireProgress)) { throw 'Host never became ready.' }
    $observe = Send-Command -Role host -Command 'observe building'
    if ($observe -notmatch 'outcome=ok:observing-building') { throw "Host could not observe a building: $observe" }
    $attack = Send-Command -Role host -Command "damagebuilding $Attacks $DamagePerAttack $IntervalTicks 1"
    Save-Transcript -Name 'damage.txt' -Lines @($observe, $attack)
    Write-Output '  waiting for the injected damage plan to finish...'
    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    while ((Get-Date) -lt $deadline) {
        $status = Get-Status -Role host
        if ((Get-StatusField $status 'outcome') -like 'ok:plan-complete*') { break }
        Start-Sleep -Seconds 2
    }
    Write-Output "  waiting up to $RepairWaitSeconds s for repair dispatch..."
    Start-Sleep -Seconds $RepairWaitSeconds
    $null = Send-Command -Role host -Command 'sample'
    $null = Send-Command -Role host -Command 'probe'
    $summary = New-Object System.Collections.Generic.List[string]
    $summary.Add('scenario=repair-dispatch')
    $summary.Add("runId=$RunId")
    $summary.Add("attacks=$Attacks damagePerAttack=$DamagePerAttack")
    $summary.Add('')
    $summary.Add('--- construct stat lifecycle ---')
    $summary.AddRange([string[]](Get-Records -Role host -Events @('construct.add', 'construct.remove', 'damage.ground.local', 'damage.entry')))
    $summary.Add('')
    $summary.Add('--- repair dispatch / repairerCount ---')
    $summary.AddRange([string[]](Get-Content -LiteralPath (Join-Path $logs 'host.jsonl') -ErrorAction SilentlyContinue |
        Where-Object { $_ -match '"event":"(repair\.launch|repair\.count|repair\.tick|drone\.remote\.eject|sample\.change|sample\.heartbeat)"' }))
    Save-Transcript -Name 'summary.txt' -Lines $summary.ToArray()
    $dispatch = ($summary | Where-Object { $_ -match '"event":"repair\.launch"' }).Count
    $repairTicks = ($summary | Where-Object { $_ -match '"event":"repair\.tick"' }).Count
    Write-Output "  repair.launch records: $dispatch ; repair.tick records: $repairTicks"
    if ($dispatch -eq 0) {
        Write-Output '  NOTE: no drone was dispatched. That is itself a baseline observation; record the reason in PROGRESS.md.'
    }
}

# --------------------------------------------- scenario 3: third player joins/leaves a planet
function Invoke-ThirdPlayer {
    Write-Output '[third-player] third client joins while host hp is recorded'
    if (!(Wait-Ready -Role host -RequireProgress)) { throw 'Host never became ready.' }
    if (!(Wait-Ready -Role client1)) { throw 'client1 never became ready.' }
    if (!(Wait-Ready -Role client2)) { throw 'client2 never became ready. Start it with run-authority-instance.ps1 -Role client2.' }
    $observe = Send-Command -Role host -Command 'observe building'
    $before = Get-Status -Role host
    Save-Transcript -Name 'before.txt' -Lines @($observe, $before)
    $summary = New-Object System.Collections.Generic.List[string]
    $summary.Add('scenario=third-player')
    $summary.Add("runId=$RunId")
    $summary.Add('')
    $summary.Add('--- host target status before the third player joined ---')
    $summary.AddRange([string[]]($before -split "`r?`n" | Where-Object { $_ -match '^target=' }))
    $summary.Add('')
    $summary.Add('--- session events (join/leave/roster) and host hp around them ---')
    $summary.AddRange([string[]](Get-Records -Role host -Events @('session\.join', 'session\.leave', 'session\.roster', 'hp\.fullhp\.begin', 'hp\.fullhp\.end', 'sample\.change')))
    $summary.Add('')
    $summary.Add('--- host target status after ---')
    $after = Get-Status -Role host
    $summary.AddRange([string[]]($after -split "`r?`n" | Where-Object { $_ -match '^target=' }))
    Save-Transcript -Name 'summary.txt' -Lines $summary.ToArray()
    $joins = ($summary | Where-Object { $_ -match '"event":"session\.join"' }).Count
    $fullHp = ($summary | Where-Object { $_ -match '"event":"hp\.fullhp\.begin"' }).Count
    Write-Output "  session.join records: $joins ; host hp.fullhp events during join window: $fullHp"
}

switch ($Scenario) {
    'sustained-attack' { Invoke-SustainedAttack }
    'repair-dispatch' { Invoke-RepairDispatch }
    'third-player' { Invoke-ThirdPlayer }
    'all' {
        Invoke-SustainedAttack
        Invoke-RepairDispatch
        Invoke-ThirdPlayer
    }
}
Write-Output "Evidence directory: $evidence"
