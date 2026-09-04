param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$base = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $base "src\HowToFishVR\HowToFishVR.csproj"
[xml]$projectXml = Get-Content -Raw -LiteralPath $project
$version = ([string]$projectXml.Project.PropertyGroup.Version).Trim()
if ([string]::IsNullOrWhiteSpace($version)) { throw "Could not read the project version." }

$bin = Join-Path $base "bin\$Configuration"
$lib = Join-Path $base "lib"
$runtimeDeps = Join-Path $base "RuntimeDeps"
$releaseRoot = Join-Path $base "release"
$packageName = "HowToFishXR-v$version"
$stage = Join-Path $releaseRoot $packageName
$archive = Join-Path $releaseRoot "$packageName.zip"

$basePrefix = [System.IO.Path]::GetFullPath($base).TrimEnd('\') + '\'
$stageFull = [System.IO.Path]::GetFullPath($stage)
$archiveFull = [System.IO.Path]::GetFullPath($archive)
if (-not $stageFull.StartsWith($basePrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
    -not $archiveFull.StartsWith($basePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to package outside the repository: $stageFull"
}

Write-Output "=== Building HowToFishXR ($Configuration) ==="
dotnet build (Join-Path $base "src\Preload\HowToFishVR.Preload.csproj") -c $Configuration -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Preloader build failed." }
dotnet build $project -c $Configuration -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Plugin build failed." }

if (Test-Path -LiteralPath $stageFull) { Remove-Item -LiteralPath $stageFull -Recurse -Force }
if (Test-Path -LiteralPath $archiveFull) { Remove-Item -LiteralPath $archiveFull -Force }

$pluginDir = Join-Path $stageFull "BepInEx\plugins\HowToFishXR"
$patcherDir = Join-Path $stageFull "BepInEx\patchers\HowToFishXR"
$patcherRuntimeDir = Join-Path $patcherDir "RuntimeDeps"
New-Item -ItemType Directory -Force -Path $pluginDir, $patcherRuntimeDir | Out-Null

Copy-Item -LiteralPath (Join-Path $bin "plugin\HowToFishVR.dll") -Destination $pluginDir
Copy-Item -LiteralPath (Join-Path $base "src\HowToFishVR\lang.json") -Destination $pluginDir
foreach ($name in @(
    "Unity.XR.OpenXR.dll",
    "Unity.XR.Management.dll",
    "Unity.XR.CoreUtils.dll",
    "UnityEngine.SpatialTracking.dll",
    "Unity.XR.Interaction.Toolkit.dll"
)) {
    Copy-Item -LiteralPath (Join-Path $lib $name) -Destination $pluginDir
}

Copy-Item -LiteralPath (Join-Path $bin "Preload\HowToFishVR.Preload.dll") -Destination $patcherDir
Copy-Item -LiteralPath (Join-Path $runtimeDeps "UnityOpenXR.dll") -Destination $patcherRuntimeDir
Copy-Item -LiteralPath (Join-Path $runtimeDeps "openxr_loader.dll") -Destination $patcherRuntimeDir

foreach ($name in @("README.md", "NOTICE")) {
    Copy-Item -LiteralPath (Join-Path $base $name) -Destination $stageFull
}

Compress-Archive -Path (Join-Path $stageFull "*") -DestinationPath $archiveFull -CompressionLevel Optimal

Write-Output "GitHub release archive created: $archiveFull"
Write-Output "BepInEx is intentionally not bundled; users must install BepInEx-BepInExPack-5.4.2305 first."
Get-ChildItem -LiteralPath $stageFull -Recurse -File |
    ForEach-Object { $_.FullName.Substring($stageFull.Length + 1) } |
    Sort-Object
