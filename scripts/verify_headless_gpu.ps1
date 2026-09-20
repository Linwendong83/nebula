param(
    [string]$GameDirectory = 'C:\Program Files (x86)\Steam\steamapps\common\Dyson Sphere Program',
    [switch]$AllTests
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
Push-Location $repository
try {
    dotnet build Nebula.sln -c Release --no-restore -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    # NuGet GameLibs/CoreModule contain reference stubs. Transpiler/vector tests need actual IL.
    foreach ($assembly in @('Assembly-CSharp.dll', 'UnityEngine.CoreModule.dll')) {
        Copy-Item -LiteralPath (Join-Path $GameDirectory "DSPGAME_Data/Managed/$assembly") `
            -Destination (Join-Path $repository "NebulaTests/bin/Release/$assembly")
    }
    $testArguments = @('test', 'NebulaTests/NebulaTests.csproj', '-c', 'Release', '--no-build', '--no-restore',
        '--logger', 'trx;LogFileName=headless-gpu.trx', '--results-directory', 'TestResults')
    if (!$AllTests) { $testArguments += @('--filter', 'FullyQualifiedName~Headless|FullyQualifiedName~ShieldBackend') }
    & dotnet @testArguments
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
finally { Pop-Location }
