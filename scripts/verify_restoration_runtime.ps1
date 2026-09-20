param(
    [string]$GameDirectory = 'C:\Program Files (x86)\Steam\steamapps\common\Dyson Sphere Program',
    [ValidateSet('server','client1','client2')][string]$Role = 'server',
    [switch]$NoBuild,
    [switch]$FreshProfile,
    [string]$LoadSave
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$runtimeRoot = Join-Path $repository 'TestResults/restoration-runtime'
$runtime = Join-Path $runtimeRoot $Role
$control = Join-Path $runtimeRoot 'control'
if (!$NoBuild) {
    dotnet build (Join-Path $PSScriptRoot 'RestorationSmoke/RestorationSmoke.csproj') -c Release -v quiet -consoleloggerparameters:ErrorsOnly
    if ($LASTEXITCODE -ne 0) { throw 'Smoke driver build failed' }
}
New-Item -ItemType Directory -Path $runtime,$control -Force | Out-Null
$pidPath = Join-Path $control ($Role + '.pid')
if (Test-Path -LiteralPath $pidPath) {
    $previous = Get-Process -Id ([int](Get-Content -LiteralPath $pidPath)) -ErrorAction SilentlyContinue
    if ($previous -and $previous.Path -eq (Join-Path $runtime 'DSPGAME.exe')) { throw "The isolated $Role is still running" }
}
[System.IO.File]::WriteAllText((Join-Path $control ($Role + '.command')), '')
[System.IO.File]::WriteAllText((Join-Path $control ($Role + '.status')), "ready=0`nresult=starting`n")
foreach ($directory in @('DSPGAME_Data','MonoBleedingEdge','Icarus','Locale','Updates','Verta','Wiki')) {
    $source = Join-Path $GameDirectory $directory
    $destination = Join-Path $runtime $directory
    if ((Test-Path -LiteralPath $source) -and !(Test-Path -LiteralPath $destination)) {
        New-Item -ItemType Junction -Path $destination -Value $source | Out-Null
    }
}
foreach ($file in @('DSPGAME.exe','UnityPlayer.dll','winhttp.dll','doorstop_config.ini','steam_appid.txt','.doorstop_version')) {
    $source = Join-Path $GameDirectory $file
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $runtime $file) -Force }
}
New-Item -ItemType Directory -Path (Join-Path $runtime 'BepInEx/plugins'),(Join-Path $runtime 'BepInEx/config'),(Join-Path $runtime 'Configs') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $GameDirectory 'BepInEx/core') -Destination (Join-Path $runtime 'BepInEx') -Recurse -Force
foreach ($mod in @('nebula-NebulaMultiplayerMod','nebula-NebulaMultiplayerModApi')) {
    $destination = Join-Path $runtime "BepInEx/plugins/$mod"
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $repository "dist/release/$mod") -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $destination $_.Name) -Force
    }
}
Copy-Item -LiteralPath (Join-Path $repository 'TestResults/smoke-harness/RestorationSmoke.dll') -Destination (Join-Path $runtime 'BepInEx/plugins/RestorationSmoke.dll') -Force
$profileConfig = Join-Path $runtime 'Configs/path.txt'
$profilePath = Join-Path $runtime ('p-' + [guid]::NewGuid().ToString('N').Substring(0,8))
if (!$FreshProfile -and (Test-Path -LiteralPath $profileConfig)) {
    $profilePath = [System.IO.Path]::GetFullPath([System.IO.File]::ReadAllText($profileConfig).Trim())
    $allowedRoot = [System.IO.Path]::GetFullPath($runtime).TrimEnd('\') + '\'
    if (!$profilePath.StartsWith($allowedRoot, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Smoke profile must remain inside the isolated runtime' }
}
[System.IO.File]::WriteAllText($profileConfig, $profilePath.Replace('\','/') + '/')
$settings = @'
[Nebula - Settings]
HostPort = 28469
EnableUPnpOrPmpSupport = false
EnableDiscordRPC = false
EnableAchievement = false
RemoteAccessEnabled = false
AutoPauseEnabled = false
'@
[System.IO.File]::WriteAllText((Join-Path $runtime 'BepInEx/config/nebula.cfg'), $settings)
$launchArguments = @('-batchmode','-screen-width','640','-screen-height','480','-logFile',('"' + (Join-Path $runtime 'player.log') + '"'),'-restoration-role',$Role,'-restoration-control',('"' + $control + '"'))
if ($Role -eq 'server') {
    $launchArguments += @('-nographics','-nebula-server')
    if ($LoadSave) { $launchArguments += @('-load',$LoadSave) }
    else { $launchArguments += @('-newgame','975310','16','1') }
}
$process = Start-Process -FilePath (Join-Path $runtime 'DSPGAME.exe') -WorkingDirectory $runtime -ArgumentList $launchArguments -WindowStyle Hidden -PassThru
[System.IO.File]::WriteAllText((Join-Path $control ($Role + '.pid')), $process.Id.ToString())
Write-Output "Started isolated $Role, PID $($process.Id), runtime $runtime"
