<#
.SYNOPSIS
    A00 baseline harness: launches isolated Dyson Sphere Program instances that run the
    AuthorityBaseline recorder, then drives reproducible scenarios over a control file.

.DESCRIPTION
    Nothing here touches the real game installation or a real player profile. Every instance
    runs from its own portable directory under TestResults/authority/, with its own save
    profile and its own player key, so host + two clients can run on one machine.

    Instances are driven by writing a command line into
        <run>/control/<role>.command
    and reading status from
        <run>/control/<role>.status

.EXAMPLE
    # 1. Build the harness and start an isolated host with a fresh 1-planet sandbox
    pwsh tools/authority/run-authority-instance.ps1 -Role host -Build

    # 2. Start two clients against it
    pwsh tools/authority/run-authority-instance.ps1 -Role client1
    pwsh tools/authority/run-authority-instance.ps1 -Role client2

    # 3. Drive scenario 1 (sustained attack on one enemy) and read the record
    pwsh tools/authority/invoke-authority-scenario.ps1 -Scenario sustained-attack
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('host', 'client1', 'client2')]
    [string]$Role,

    [string]$GameDirectory = 'C:\Program Files (x86)\Steam\steamapps\common\Dyson Sphere Program',
    [string]$RunId,
    [string]$LoadSave,
    [string]$NewGame = '975310,16,1',
    [int]$Port = 28469,
    [string]$HostAddress = '127.0.0.1',
    [switch]$Combat,
    # 1 forces the serial path (no worker threads); 0 or omitted keeps the auto worker count.
    # A01 must verify both, since the serial and parallel code paths differ.
    [int]$ParallelThreadCount = 0,
    [switch]$Build,
    [switch]$FreshProfile,
    [switch]$TraceAll,
    [switch]$NoLaunch,
    # Host-authority is the only multiplayer mode; there are no mode or fault command-line
    # flags. Faults are driven by the harness `fault` verb after launch.
    # Harness self-quit after this many seconds (default 1800). Matrix runs pass a larger
    # budget so one set of instances survives all cases.
    [int]$LifetimeSeconds = 0
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (!$RunId) { $RunId = 'run-' + (Get-Date -Format 'yyyyMMdd-HHmmss') }
$runRoot = Join-Path $repository ('TestResults/authority/' + $RunId)
$runtime = Join-Path $runRoot ('instances/' + $Role)
$control = Join-Path $runRoot 'control'
$logs = Join-Path $runRoot 'logs'

if ($Build) {
    dotnet build (Join-Path $PSScriptRoot 'AuthorityBaseline/AuthorityBaseline.csproj') -c Release -v quiet -consoleloggerparameters:ErrorsOnly
    if ($LASTEXITCODE -ne 0) { throw 'AuthorityBaseline build failed' }
}

$harness = Join-Path $repository 'TestResults/authority/harness'
if (!(Test-Path -LiteralPath (Join-Path $harness 'AuthorityBaseline.dll'))) {
    throw "Harness not built. Run with -Build, or build tools/authority/AuthorityBaseline/AuthorityBaseline.csproj."
}
if (!(Test-Path -LiteralPath (Join-Path $GameDirectory 'DSPGAME_Data/Managed/Assembly-CSharp.dll'))) {
    throw "Game installation not found at '$GameDirectory'."
}

New-Item -ItemType Directory -Path $runtime, $control, $logs -Force | Out-Null

$pidPath = Join-Path $control ($Role + '.pid')
if (Test-Path -LiteralPath $pidPath) {
    $previous = Get-Process -Id ([int](Get-Content -LiteralPath $pidPath)) -ErrorAction SilentlyContinue
    if ($previous -and $previous.Path -eq (Join-Path $runtime 'DSPGAME.exe')) {
        throw "The isolated $Role instance is still running (PID $($previous.Id)). Send 'quit' to its command file first."
    }
}

# Junction the read-only game payload instead of copying it. The instance directory is the
# only writable part, so no run can modify the real installation.
foreach ($directory in @('DSPGAME_Data', 'MonoBleedingEdge', 'Icarus', 'Locale', 'Updates', 'Verta', 'Wiki')) {
    $source = Join-Path $GameDirectory $directory
    $destination = Join-Path $runtime $directory
    if ((Test-Path -LiteralPath $source) -and !(Test-Path -LiteralPath $destination)) {
        New-Item -ItemType Junction -Path $destination -Value $source | Out-Null
    }
}
foreach ($file in @('DSPGAME.exe', 'UnityPlayer.dll', 'winhttp.dll', 'doorstop_config.ini', 'steam_appid.txt', '.doorstop_version')) {
    $source = Join-Path $GameDirectory $file
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $runtime $file) -Force }
}

New-Item -ItemType Directory -Path (Join-Path $runtime 'BepInEx/plugins'), (Join-Path $runtime 'BepInEx/config'), (Join-Path $runtime 'Configs') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $GameDirectory 'BepInEx/core') -Destination (Join-Path $runtime 'BepInEx') -Recurse -Force

# The mod under test comes from the local build output, not from a release download.
foreach ($mod in @('nebula-NebulaMultiplayerMod', 'nebula-NebulaMultiplayerModApi')) {
    $destination = Join-Path $runtime "BepInEx/plugins/$mod"
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $repository "dist/release/$mod") -File -ErrorAction SilentlyContinue | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $destination $_.Name) -Force
    }
}

# The recorder plugin sits next to the mod and loads through the same BepInEx chain.
$harnessDestination = Join-Path $runtime 'BepInEx/plugins/AuthorityBaseline'
New-Item -ItemType Directory -Path $harnessDestination -Force | Out-Null
Get-ChildItem -LiteralPath $harness -File -Filter 'AuthorityBaseline.*' | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $harnessDestination $_.Name) -Force
}

# dsp-steamless is a local BepInEx plugin already installed in the user's game directory. It
# stubs SteamAPI so a client instance can boot while the shared Steam library is in use by
# someone else (SteamAPI_Init failure makes UIRunner quit the process). Copied, like the
# harness, into the isolated instance only; the real installation is untouched. The dedicated
# host (-nebula-server) never reaches SteamAPI and does not need it, but uniformity keeps the
# plugin set identical across roles.
$steamlessSource = Join-Path $GameDirectory 'BepInEx/plugins/dsp-steamless'
if (Test-Path -LiteralPath $steamlessSource) {
    $steamlessDestination = Join-Path $runtime 'BepInEx/plugins/dsp-steamless'
    New-Item -ItemType Directory -Path $steamlessDestination -Force | Out-Null
    Get-ChildItem -LiteralPath $steamlessSource -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $steamlessDestination $_.Name) -Force
    }
}

# Each instance gets its own save profile. The path is validated to stay inside the run root
# so a typo can never point an instance at the real player profile.
$profileConfig = Join-Path $runtime 'Configs/path.txt'
$profilePath = Join-Path $runtime 'profile'
if (!$FreshProfile -and (Test-Path -LiteralPath $profileConfig)) {
    $candidate = [System.IO.Path]::GetFullPath([System.IO.File]::ReadAllText($profileConfig).Trim())
    $allowedRoot = [System.IO.Path]::GetFullPath($runtime).TrimEnd('\') + '\'
    if ($candidate.StartsWith($allowedRoot, [System.StringComparison]::OrdinalIgnoreCase)) { $profilePath = $candidate }
}
New-Item -ItemType Directory -Path $profilePath -Force | Out-Null
[System.IO.File]::WriteAllText($profileConfig, $profilePath.Replace('\', '/') + '/')

$settings = @"
[Nebula - Settings]
HostPort = $Port
EnableUPnpOrPmpSupport = false
EnableDiscordRPC = false
EnableAchievement = false
RemoteAccessEnabled = false
AutoPauseEnabled = false
"@
[System.IO.File]::WriteAllText((Join-Path $runtime 'BepInEx/config/nebula.cfg'), $settings)

# -newgame goes through GameDesc.SetForNewGame, which forces isPeaceMode = true and never reads
# this file. -newgame-cfg does apply it, so the sandbox can enable dark fog and still pin the
# seed for reproducibility.
$newGameParts = @('975310', '16', '1')
if ($NewGame) {
    $newGameParts = $NewGame.Split(',')
    if ($newGameParts.Count -ne 3) { throw "-NewGame must be 'seed,starCount,resourceMultiplier', got '$NewGame'." }
}
$gameDesc = @"
[Basic]
galaxySeed = $($newGameParts[0])
starCount = $($newGameParts[1])
resourceMultiplier = $($newGameParts[2])

[General]
isPeaceMode = $(if ($Combat) { 'false' } else { 'true' })
isSandboxMode = false

[Combat]
aggressiveness = 1
initialLevel = $(if ($Combat) { '1' } else { '0' })
initialGrowth = 1
initialColonize = 1
maxDensity = 1
growthSpeedFactor = 1
powerThreatFactor = 1
battleThreatFactor = 1
battleExpFactor = 1
"@
[System.IO.File]::WriteAllText((Join-Path $runtime 'BepInEx/config/nebulaGameDescSettings.cfg'), $gameDesc)

[System.IO.File]::WriteAllText((Join-Path $control ($Role + '.command')), '')
[System.IO.File]::WriteAllText((Join-Path $control ($Role + '.status')), "role=$Role`nready=0`noutcome=starting`n")

$launchArguments = @(
    '-batchmode', '-screen-width', '640', '-screen-height', '480',
    '-logFile', ('"' + (Join-Path $runtime 'player.log') + '"'),
    '-authority-role', $Role,
    '-authority-control', ('"' + $control + '"'),
    '-authority-log', ('"' + $logs + '"'),
    '-authority-host', $HostAddress,
    '-authority-port', $Port.ToString()
)
if ($TraceAll) { $launchArguments += '-authority-verbose' }
# No -nebula-authority / -nebula-authority-faults flags exist. Mode is unconditional;
# faults are driven by the harness `fault` verb (see tools/authority/README.md).
if ($LifetimeSeconds -gt 0) { $launchArguments += @('-authority-lifetime', $LifetimeSeconds.ToString()) }
if ($Role -eq 'host') {
    $launchArguments += @('-nographics', '-nebula-server')
    if ($LoadSave) { $launchArguments += @('-load', $LoadSave) }
    elseif ($NewGame) {
        # -newgame-cfg reads nebulaGameDescSettings.cfg written above, which is the only path
        # that can leave peace mode. -newgame would silently force isPeaceMode = true.
        $launchArguments += '-newgame-cfg'
    }
}

# The game reads its threading options from the per-profile options.xml. Writing the value here
# is what lets a run exercise the serial path, which A01 must cover alongside the parallel one.
$optionsPath = Join-Path $profilePath 'Dyson Sphere Program/options.xml'
if ($ParallelThreadCount -gt 0) {
    if (!(Test-Path -LiteralPath $optionsPath)) {
        throw "options.xml not found at $optionsPath. Start the instance once before forcing a thread count."
    }
    $options = [System.IO.File]::ReadAllText($optionsPath)
    $options = [regex]::Replace($options, '<ParallelThreadCount>\d+</ParallelThreadCount>',
        "<ParallelThreadCount>$ParallelThreadCount</ParallelThreadCount>")
    [System.IO.File]::WriteAllText($optionsPath, $options)
    Write-Output "Forced ParallelThreadCount=$ParallelThreadCount in $optionsPath"
}

$manifest = [ordered]@{
    runId          = $RunId
    role           = $Role
    startedUtc     = (Get-Date).ToUniversalTime().ToString('o')
    repositoryHead = (git -C $repository rev-parse HEAD)
    gameDirectory  = $GameDirectory
    gameVersion    = if (Test-Path -LiteralPath (Join-Path $GameDirectory 'Updates/Versions.txt')) {
        (Get-Content -LiteralPath (Join-Path $GameDirectory 'Updates/Versions.txt') -Tail 1).Split(',')[0]
    } else { 'unknown' }
    gameDllSha256  = (Get-FileHash -LiteralPath (Join-Path $GameDirectory 'DSPGAME_Data/Managed/Assembly-CSharp.dll') -Algorithm SHA256).Hash
    harnessSha256  = (Get-FileHash -LiteralPath (Join-Path $harness 'AuthorityBaseline.dll') -Algorithm SHA256).Hash
    port           = $Port
    parallelThreadCount = $ParallelThreadCount
    authorityMode  = $true
    faultSpec      = ''
    lifetimeSeconds = $LifetimeSeconds
    commandLine    = ($launchArguments -join ' ')
    runtime        = $runtime
    profile        = $profilePath
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runRoot ('manifest-' + $Role + '.json')) -Encoding UTF8

if ($NoLaunch) {
    Write-Output "Prepared isolated $Role (no launch). Command line: $($launchArguments -join ' ')"
    return
}

$process = Start-Process -FilePath (Join-Path $runtime 'DSPGAME.exe') -WorkingDirectory $runtime -ArgumentList $launchArguments -WindowStyle Hidden -PassThru
[System.IO.File]::WriteAllText($pidPath, $process.Id.ToString())
Write-Output "Started isolated $Role, PID $($process.Id)"
Write-Output "  run:     $runRoot"
Write-Output "  status:  $(Join-Path $control ($Role + '.status'))"
Write-Output "  command: $(Join-Path $control ($Role + '.command'))"
Write-Output "  log:     $(Join-Path $logs ($Role + '.jsonl'))"
