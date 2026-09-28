param(
    [Parameter(Mandatory = $true)][string]$RuntimeManifest,
    [string]$GameRoot = 'C:\Program Files (x86)\Steam\steamapps\common\How to Fish\How to Fish',
    [ValidateSet('left','right','flat')][string[]]$Modes = @('left','right','flat')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$RuntimeManifest = (Resolve-Path -LiteralPath $RuntimeManifest).Path
$exe = Join-Path $GameRoot 'How to Fish.exe'
if (Get-Process | Where-Object { $_.Path -eq $exe }) { throw 'Close the game before running smoke tests.' }
$configPath = Join-Path $GameRoot 'BepInEx\config\com.jaxon.howtofishvr.cfg'
$bepConfigPath = Join-Path $GameRoot 'BepInEx\config\BepInEx.cfg'
$testDll = Join-Path $GameRoot 'BepInEx\plugins\HandednessSmoke.dll'
if (Test-Path -LiteralPath $testDll) { throw "A test DLL already exists: $testDll" }
$config = [IO.File]::ReadAllText($configPath)
$bepConfig = [IO.File]::ReadAllText($bepConfigPath)
if ($config -notmatch 'Left Handed = (true|false)') { throw 'Install and launch the updated mod once before testing.' }
dotnet build "$PSScriptRoot\HandednessSmoke" -c Release -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Test plugin build failed.' }
$outDir = Join-Path $repo 'artifacts\handedness-smoke'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$environment = @{}
foreach ($name in @('SteamAppId','SteamGameId','XR_RUNTIME_JSON','XRSIM_DIR','HTF_TEST_OUTPUT','XR_DISABLE_API_LAYERS')) {
    $environment[$name] = [Environment]::GetEnvironmentVariable($name)
}
try {
    Copy-Item -LiteralPath "$PSScriptRoot\HandednessSmoke\bin\Release\netstandard2.1\HandednessSmoke.dll" -Destination $testDll
    [IO.File]::WriteAllText($bepConfigPath, [regex]::Replace($bepConfig, '(?s)(\[Logging.Console\].*?Enabled = )true', '$1false'))
    $env:SteamAppId = '4001890'
    $env:SteamGameId = '4001890'
    $env:XR_RUNTIME_JSON = $RuntimeManifest
    $env:XRSIM_DIR = Join-Path $outDir 'runtime'
    $env:XR_DISABLE_API_LAYERS = '~implicit~'
    foreach ($mode in $Modes) {
        $left = if ($mode -eq 'right') { 'false' } else { 'true' }
        [IO.File]::WriteAllText($configPath, ($config -replace 'Left Handed = (true|false)', "Left Handed = $left"))
        $env:HTF_TEST_OUTPUT = Join-Path $outDir "$mode.txt"
        [IO.File]::WriteAllText($env:HTF_TEST_OUTPUT, '')
        $arguments = '-batchmode -force-d3d11 -screen-width 1280 -screen-height 720 -screen-fullscreen 0 -logFile "' + (Join-Path $outDir "$mode-player.log") + '"'
        if ($mode -eq 'flat') { $arguments += ' --disable-vr' }
        $process = Start-Process -FilePath $exe -WorkingDirectory $GameRoot -ArgumentList $arguments -WindowStyle Hidden -PassThru
        if (!$process.WaitForExit(60000)) {
            Stop-Process -InputObject $process
            $process.WaitForExit()
            throw "Timed out: $mode"
        }
        $result = [IO.File]::ReadAllText($env:HTF_TEST_OUTPUT)
        Write-Output "$mode exit=$($process.ExitCode)"
        Write-Output $result
        if ($process.ExitCode -ne 0 -or $result -notmatch 'RESULT failures=0' -or $result -match '(?m)^FAIL ') {
            throw "Smoke tests failed: $mode. See $outDir"
        }
    }
}
finally {
    [IO.File]::WriteAllText($configPath, $config)
    [IO.File]::WriteAllText($bepConfigPath, $bepConfig)
    if (Test-Path -LiteralPath $testDll) { Remove-Item -LiteralPath $testDll }
    foreach ($name in $environment.Keys) { [Environment]::SetEnvironmentVariable($name, $environment[$name]) }
}
