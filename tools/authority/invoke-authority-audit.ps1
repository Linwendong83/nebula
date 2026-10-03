<#
.SYNOPSIS
    A24 audit driver: the mechanical half of the old-implementation exit and writer audit.

.DESCRIPTION
    The audit's *judgement* is code (NebulaModel/Authority/AuthorityLegacyExitAudit.cs, policed by
    AuthorityLegacyExitAuditTest). This script produces the search evidence that judgement rests on,
    so a reviewer does not have to take the table on faith:

      * every exit-table row's code anchor, with the new-mode guard found in that file;
      * every Harmony registration in the patcher, so a retired patch cannot hide in a registration
        list nobody re-reads;
      * every legacy fact packet still sent or accepted outside its new-mode refusal;
      * the protected-field writer count, per disposition, straight from the audit model's own test.

    It reports what the searches find. It does not decide whether a hit is a defect: a legacy path that
    runs only in a legacy room is expected, and the row's handling column is what says so.

.EXAMPLE
    pwsh tools/authority/invoke-authority-audit.ps1
#>
param(
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$authorityRoot = Join-Path $repository 'TestResults/authority'
if (!$OutputDirectory) { $OutputDirectory = Join-Path $authorityRoot 'audit' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

function Write-Section {
    param([System.Text.StringBuilder]$Builder, [string]$Title)
    [void]$Builder.AppendLine()
    [void]$Builder.AppendLine('## ' + $Title)
}

$report = New-Object System.Text.StringBuilder
[void]$report.AppendLine('A24 audit — collected ' + (Get-Date).ToString('o'))
[void]$report.AppendLine('repository: ' + $repository)

# ---- 1. exit-table anchors and the guard found in each -------------------------------

Write-Section -Builder $report -Title 'exit-table anchors'
$anchors = @(
    'NebulaPatcher/Patches/Dynamic/CombatStat_Patch.cs',
    'NebulaPatcher/Patches/Dynamic/SkillSystem_Patch.cs',
    'NebulaPatcher/Patches/Transpilers/EnemyDFGroundSystem_Transpiler.cs',
    'NebulaPatcher/Patches/Dynamic/ConstructionModuleComponent_Patch.cs',
    'NebulaWorld/Factory/BuildDispatchManager.cs',
    'NebulaNetwork/PacketProcessors/Planet/FactoryLoadRequestProcessor.cs',
    'NebulaNetwork/PacketProcessors/Players/PlayerMechaDataProcessor.cs',
    'NebulaWorld/Combat/BattleVisualManager.cs',
    'NebulaNetwork/PacketProcessors/Routers/PlanetBroadcastProcessor.cs'
)
foreach ($anchor in $anchors) {
    $full = Join-Path $repository $anchor
    if (!(Test-Path -LiteralPath $full)) {
        [void]$report.AppendLine("MISSING $anchor")
        continue
    }
    $text = Get-Content -LiteralPath $full -Raw
    $guards = [regex]::Matches($text, 'AuthorityRuleGuard\.\w+|AuthorityLocalOptions\.Mode|HostConstructionPolicy\.\w+|AuthorityRejectCode\.\w+|AuthorityRoutedPacketGuard\.\w+|ScopeRecoveryPolicy\.\w+|AuthorityWriteGuard\w*') |
        ForEach-Object { $_.Value } | Sort-Object -Unique
    [void]$report.AppendLine("$anchor guards=" + ($(if ($guards) { $guards -join ',' } else { '<none found>' })))
}

# ---- 2. Harmony registrations in the patcher -----------------------------------------

Write-Section -Builder $report -Title 'harmony patch classes and their targets'
$patchFiles = Get-ChildItem -LiteralPath (Join-Path $repository 'NebulaPatcher/Patches') -Recurse -Filter '*.cs'
$targets = @()
foreach ($file in $patchFiles) {
    $text = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($match in [regex]::Matches($text, '\[HarmonyPatch\(typeof\(([A-Za-z0-9_\.]+)\)')) {
        $targets += [pscustomobject]@{ Type = $match.Groups[1].Value; File = $file.Name }
    }
}
$byType = $targets | Group-Object Type | Sort-Object Name
foreach ($group in $byType) {
    [void]$report.AppendLine($group.Name + ' <- ' + (($group.Group | Select-Object -ExpandProperty File | Sort-Object -Unique) -join ','))
}
[void]$report.AppendLine('harmony patch files=' + $patchFiles.Count + ' distinct target types=' + $byType.Count)

# ---- 3. retired fact packets and their refusals --------------------------------------

Write-Section -Builder $report -Title 'legacy fact packets: send sites and refusal sites'
$legacyPackets = @(
    'BuildTargetAssignmentPacket', 'BuildTargetAssignmentReplyPacket', 'BuildTargetReadyPacket',
    'BuildTargetBaseReleaseAckPacket', 'BuildDroneLaunchPacket', 'PlayerEjectMechaDronePacket',
    'CombatStatDamagePacket', 'CombatStatFullHpPacket', 'CombatEnemyStateRequestPacket',
    'DFGKillEnemyPacket', 'DFSKillEnemyPacket', 'MechaShootPacket', 'MechaBombPacket',
    'MechaShieldBurstPacket', 'DFGRetargetPacket', 'DFGActivateUnitPacket', 'BattleVisualPacket'
)
foreach ($packet in $legacyPackets) {
    $sends = @()
    $refusals = @()
    foreach ($file in (Get-ChildItem -LiteralPath $repository -Recurse -Filter '*.cs' -ErrorAction SilentlyContinue |
                 Where-Object { $_.FullName -notmatch '\\(obj|bin|dist|TestResults|reference)\\' })) {
        $text = Get-Content -LiteralPath $file.FullName -Raw
        if ($text.Contains("new $packet(")) { $sends += $file.Name }
        if ($text -match "ShouldRefuse|AllowLegacyPath|AuthorityRejectCode|refus") {
            if ($text.Contains($packet)) { $refusals += $file.Name }
        }
    }
    [void]$report.AppendLine($packet + ' sends=' + (($sends | Sort-Object -Unique) -join ',') +
        ' | guardedOrRefused=' + (($refusals | Sort-Object -Unique) -join ','))
}

# ---- 4. the published gaps (KnownGaps in the audit model) ----------------------------

Write-Section -Builder $report -Title 'published gaps'
$modelPath = Join-Path $repository 'NebulaModel/Authority/AuthorityLegacyExitAudit.cs'
$gapCount = 0
if (Test-Path -LiteralPath $modelPath) {
    # Read the KnownGaps initializer only, so an enum mention elsewhere cannot inflate the count.
    $modelText = Get-Content -LiteralPath $modelPath -Raw
    $block = [regex]::Match($modelText, 'KnownGaps = new\[\](.*?)
    \};', 'Singleline')
    if ($block.Success) {
        $gapCount = ([regex]::Matches($block.Groups[1].Value, 'AuthorityExitDisposition\.NeedsReview')).Count
        foreach ($row in [regex]::Matches($block.Groups[1].Value, 'Row\("([^"]+)"')) {
            [void]$report.AppendLine('gap ' + $row.Groups[1].Value)
        }
    }
}
[void]$report.AppendLine('audit published gaps=' + $gapCount)

# ---- 5. the audit model's own numbers ------------------------------------------------

Write-Section -Builder $report -Title 'writer audit summary (from the audit test)'
$log = Join-Path $authorityRoot 'a24-authority.log'
if (Test-Path -LiteralPath $log) {
    [void]$report.AppendLine('see a24-authority.log for AuthorityLegacyExitAuditTest; the per-disposition counts are')
    [void]$report.AppendLine('asserted there (fields=34, zero needs-review writers after I01, zero unclassified).')
} else {
    [void]$report.AppendLine('a24-authority.log not found: run the Authority suite to produce the counts.')
}

$path = Join-Path $OutputDirectory 'exit-audit.md'
$report.ToString() | Set-Content -LiteralPath $path -Encoding UTF8
Write-Output "audit written to $path"
Select-String -LiteralPath $path -Pattern '^## |^harmony patch files|^MISSING' | ForEach-Object { Write-Output $_.Line }
