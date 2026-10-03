<#
.SYNOPSIS
    A22 closing matrix: runs the forced C/R/L scenarios under each required N fault config.

.DESCRIPTION
    Enumerates the same contract as NebulaModel.Authority.AuthorityMatrixPlan (forced scenarios
    C01/C02/C08/C10/R01/R03/R06/R12/L01/L02/L05 under N0/N3/N4/N5/N6/N7) against already-running
    isolated instances, and collects HostTick-aligned evidence per case into
    TestResults/authority/<run>/evidence/matrix/<scenario>-<fault>/.

    IMPORTANT: the host fault link is always wired (release: no command-line flag remains).
    Each case arms the host fault rules through the harness `fault` verb, drives the
    underlying A00 scenario where one exists, then clears the fault and snapshots both
    peers' replica state. A00 launch:

        pwsh tools/authority/run-authority-instance.ps1 -Role host -RunId <run> \
            -Combat -LifetimeSeconds 7200

    Each case then arms the host fault rules through the harness `fault` verb (same refuse-the-whole
    spec semantics as the launch flag), drives the underlying A00 scenario where one exists, then
    clears the fault and snapshots both peers' replica state. Cases that cannot run (instance
    missing, no target, link not wired) report that explicitly instead of a passing result.

.EXAMPLE
    pwsh tools/authority/invoke-authority-matrix.ps1 -RunId run-a22matrix
    pwsh tools/authority/invoke-authority-matrix.ps1 -RunId run-a22matrix -Only C01-N3,R01-N0
#>
param(
    [string]$RunId,
    [string]$Only = '',
    [int]$WaitSeconds = 90,
    [switch]$DryRun,
    # Actually drive each case's underlying A00 scenario (default) or only arm faults.
    [switch]$DriveScenarios = $true,
    [int]$Attacks = 4,
    [int]$DamagePerAttack = 400,
    [int]$RepairWaitSeconds = 60
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
$matrixRoot = Join-Path $runRoot 'evidence/matrix'
if (!(Test-Path -LiteralPath $control)) { throw "Run '$RunId' has no control directory." }
New-Item -ItemType Directory -Path $matrixRoot -Force | Out-Null

$forcedScenarios = @('C01','C02','C08','C10','R01','R03','R06','R12','L01','L02','L05')
$faultSpecs = @{
    'N0' = ''; 'N3' = 'copies=1;reorder=1'; 'N4' = 'droplifecycle;dropstate';
    'N5' = 'corruptchunk'; 'N6' = 'pause';
    # N7 (A23): held-then-released link so a backlog builds. The send/receive budget half of N7 is
    # unconditional in authority mode, so this is the transport half only.
    'N7' = 'delay=1;copies=1'
}
$scenarioDriver = @{
    'C01' = 'sustained-attack'; 'C02' = 'sustained-attack'; 'C08' = 'sustained-attack';
    'C10' = 'sustained-attack'; 'R01' = 'repair-dispatch'; 'R03' = 'repair-dispatch';
    'R06' = 'repair-dispatch'; 'R12' = 'repair-dispatch'; 'L01' = 'third-player';
    'L02' = 'third-player'; 'L05' = 'third-player'
}

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
    $token = 'cmd' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $full = "$token $Command"
    [System.IO.File]::WriteAllText($path, $full)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $status = Get-Content -LiteralPath (Join-Path $control ($Role + '.status')) -Raw -ErrorAction SilentlyContinue
        if ($status -match [regex]::Escape($full)) { return $status }
    }
    return (Get-Content -LiteralPath (Join-Path $control ($Role + '.status')) -Raw -ErrorAction SilentlyContinue)
}

function Invoke-MatrixCase {
    param([string]$Scenario, [string]$Fault)
    $caseId = "$Scenario-$Fault"
    $caseDir = Join-Path $matrixRoot $caseId
    New-Item -ItemType Directory -Path $caseDir -Force | Out-Null
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("case=$caseId")
    $lines.Add("runId=$RunId")
    $lines.Add("scenario=$Scenario driver=$($scenarioDriver[$Scenario]) fault=$Fault spec=$($faultSpecs[$Fault])")
    $lines.Add("host-fault-link-wired=$linkWired")
    $lines.Add("utc=" + (Get-Date).ToUniversalTime().ToString('o'))
    try {
        if ($DryRun) {
            $lines.Add('dryrun=1 (no instance touched)')
        } else {
            $spec = $faultSpecs[$Fault]
            if ($spec -ne '') {
                $armed = Send-Command -Role host -Command "fault $spec"
                $lines.Add('--- fault arm ---')
                $lines.Add($armed)
                if ($armed -notmatch 'ok:fault-armed') { throw "fault arm refused: $armed" }
            } else {
                $cleared = Send-Command -Role host -Command 'faultclear'
                $lines.Add('--- fault clear (clean case) ---')
                $lines.Add($cleared)
            }
            $driver = $scenarioDriver[$Scenario]
            $lines.Add("--- driver=$driver ---")
            if ($DriveScenarios) {
                $scenarioEvidence = Join-Path $runRoot ("evidence/" + $driver)
                if (Test-Path -LiteralPath $scenarioEvidence) {
                    Remove-Item -LiteralPath $scenarioEvidence -Recurse -Force
                }
                pwsh -NoProfile -File (Join-Path $PSScriptRoot 'invoke-authority-scenario.ps1') `
                    -Scenario $driver -RunId $RunId -Attacks $Attacks `
                    -DamagePerAttack $DamagePerAttack -WaitSeconds $WaitSeconds `
                    -RepairWaitSeconds $RepairWaitSeconds 2>&1 | ForEach-Object { $lines.Add('driver> ' + $_) }
                $caseDriver = Join-Path $caseDir 'driver'
                if (Test-Path -LiteralPath $scenarioEvidence) {
                    Copy-Item -LiteralPath $scenarioEvidence -Destination $caseDriver -Recurse -Force
                } else {
                    $lines.Add('BLOCKED: scenario produced no evidence directory')
                }
            } else {
                $lines.Add("(drive with invoke-authority-scenario.ps1 -Scenario $driver -RunId $RunId; summary copied here)")
            }
            $status = Send-Command -Role host -Command 'faultstatus'
            $lines.Add('--- fault status after ---')
            $lines.Add($status)
            # Snapshot the host while the fault is still armed: this is the only point that shows
            # whether the link's rules object actually received the runtime spec and what it did.
            $pidPath = Join-Path $control 'host.pid'
            if (Test-Path -LiteralPath $pidPath) {
                $null = Send-Command -Role host -Command 'replica'
                $armedPath = Join-Path $logs 'host-replica.txt'
                if (Test-Path -LiteralPath $armedPath) {
                    Copy-Item -LiteralPath $armedPath -Destination (Join-Path $caseDir 'host-replica-armed.txt') -Force
                }
            }
            $null = Send-Command -Role host -Command 'faultclear'
            # A22: capture the replica consistency both peers report for this case. A fault that
            # corrupts or drops the stream still has to end with zero digest mismatches and a
            # non-empty mirror on the client; drift here is the signal the case failed.
            foreach ($role in @('host', 'client1')) {
                $pidPath = Join-Path $control ($role + '.pid')
                if (!(Test-Path -LiteralPath $pidPath)) { continue }
                $null = Send-Command -Role $role -Command 'replica'
                $replicaPath = Join-Path $logs ($role + '-replica.txt')
                if (Test-Path -LiteralPath $replicaPath) {
                    Copy-Item -LiteralPath $replicaPath -Destination (Join-Path $caseDir ($role + '-replica.txt')) -Force
                    $lines.Add("--- $role replica ---")
                    $lines.AddRange([string[]](Get-Content -LiteralPath $replicaPath |
                        Where-Object { $_ -match '^(replica=|replicator=|faultLink=|isHostAuthority|framesDrained|scope )' }))
                }
            }
            $lines.Add('--- evidence checklist (VALIDATION section 2) ---')
            $lines.Add('host-tick-log: ' + (Join-Path $logs 'host.jsonl'))
            $lines.Add('canonical-snapshot: host/client replica.txt copied into this case directory')
            $lines.Add('scope-digest: host PublishDigests counters in replica.txt (digests / matched / mismatched)')
        }
    } catch {
        $lines.Add('BLOCKED: ' + $_.Exception.Message)
    }
    $out = Join-Path $caseDir 'case.txt'
    $lines.ToArray() | Set-Content -LiteralPath $out -Encoding UTF8
    Write-Output "  wrote $out"
}

$filter = @()
if ($Only -ne '') { $filter = $Only.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' } }

# Preflight: the fault link only exists if the host process was launched with a fault spec. Query
# the armed state before any case runs; if injection is off, every case would silently run clean,
# so say so loudly instead of reporting a false pass.
$linkWired = $false
if (!$DryRun) {
    try {
        $preflight = Send-Command -Role host -Command 'faultstatus'
        $linkWired = $preflight -match 'enabled=1'
        if (!$linkWired) {
            Write-Warning ('Host fault link is not wired (faultstatus says enabled=0). Launch the ' +
                'host with -FaultSpec <benign spec> (e.g. delay=0); otherwise every fault case runs clean.')
        }
    } catch {
        Write-Warning ('Fault preflight failed: ' + $_.Exception.Message)
    }
}

foreach ($scenario in $forcedScenarios) {
    foreach ($fault in @('N0','N3','N4','N5','N6','N7')) {
        $caseId = "$scenario-$fault"
        if ($filter.Count -gt 0 -and -not ($filter -contains $caseId)) { continue }
        Write-Output "[$caseId]"
        Invoke-MatrixCase -Scenario $scenario -Fault $fault
    }
}
Write-Output "Matrix directory: $matrixRoot"
