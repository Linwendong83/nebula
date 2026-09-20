param(
    [string]$ShaderBytecode,
    [string]$GameDirectory,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repository 'dist/release/nebula-NebulaMultiplayerMod' }
$nativeOutput = Join-Path $repository 'NebulaPatcher/obj/native'
New-Item -ItemType Directory -Path $nativeOutput, $OutputDirectory -Force | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$installation) { throw 'Install the Visual Studio C++ x64 build tools and Windows SDK.' }
$vcvars = Join-Path $installation 'VC/Auxiliary/Build/vcvars64.bat'
# Import the compiler environment. No filesystem operations are delegated across shells.
$compilerSetup = [System.Diagnostics.ProcessStartInfo]::new($env:ComSpec)
$compilerSetup.Arguments = '/d /s /c ""' + $vcvars + '" >nul && set"'
$compilerSetup.UseShellExecute = $false
$compilerSetup.CreateNoWindow = $true
$compilerSetup.RedirectStandardOutput = $true
$setupProcess = [System.Diagnostics.Process]::Start($compilerSetup)
$environmentLines = $setupProcess.StandardOutput.ReadToEnd() -split "`r?`n"
$setupProcess.WaitForExit()
if ($setupProcess.ExitCode -ne 0) { throw 'Could not initialize the C++ compiler environment.' }
foreach ($line in $environmentLines) {
    if ($line -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($matches[1], $matches[2], 'Process') }
}
$source = Join-Path $repository 'NebulaPatcher/Native/ShieldNative.cpp'
$dll = Join-Path $OutputDirectory 'NebulaShieldNative.dll'
& cl.exe /nologo /LD /O2 /EHsc /std:c++17 /MT /W4 $source "/Fo$nativeOutput/ShieldNative.obj" `
    /link d3d11.lib dxgi.lib "/OUT:$dll" "/IMPLIB:$nativeOutput/NebulaShieldNative.lib"
if ($LASTEXITCODE -ne 0) { throw 'Native shield module compilation failed.' }
if ($ShaderBytecode) {
    $hash = (Get-FileHash -LiteralPath $ShaderBytecode -Algorithm SHA256).Hash
    if ($hash -ne 'A35923D287B3D6C5B02558F6FCADBA04E157601C9CF84C24BDB03F3E467CFF29') {
        throw 'Shader is not the verified DSP 0.10.34.28529 shield kernel. Do not guess its constant layout.'
    }
    $destinationShader = Join-Path $OutputDirectory 'planet-shield.dxbc'
    if ([IO.Path]::GetFullPath($ShaderBytecode) -ne [IO.Path]::GetFullPath($destinationShader)) {
        Copy-Item -LiteralPath $ShaderBytecode -Destination $destinationShader
    }
}
else {
    if (!$GameDirectory) {
        [xml]$development = Get-Content -LiteralPath (Join-Path $repository 'DevEnv.targets')
        $GameDirectory = [string]$development.Project.PropertyGroup.DSPGameDir
    }
    $GameDirectory = $GameDirectory.TrimEnd('\', '/')
    & python (Join-Path $PSScriptRoot 'extract_shield_shader.py') --game-dir $GameDirectory --output (Join-Path $OutputDirectory 'planet-shield.dxbc')
    if ($LASTEXITCODE -ne 0) { throw 'Could not extract verified shield shader from the game installation.' }
}
Get-FileHash -LiteralPath $dll, (Join-Path $OutputDirectory 'planet-shield.dxbc')
