param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$base = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $base "src\HowToFishVR\HowToFishVR.csproj"
$metadataRoot = Join-Path $base "thunderstore"
$manifestPath = Join-Path $metadataRoot "manifest.json"
$iconPath = Join-Path $metadataRoot "icon.png"
$readmePath = Join-Path $metadataRoot "README.md"

$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$packageVersion = ([string]$manifest.version_number).Trim()

if ($manifest.name -notmatch '^[A-Za-z0-9_]{1,128}$') { throw "Invalid Thunderstore package name: $($manifest.name)" }
if ($packageVersion -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid Thunderstore version: $packageVersion" }
if ([string]$manifest.description.Length -gt 250) { throw "Thunderstore description exceeds 250 characters." }
if ($manifest.website_url -ne "https://github.com/Ghostx10742/HowToFishXR") { throw "Unexpected website_url in manifest." }
if (@($manifest.dependencies).Count -ne 1 -or $manifest.dependencies[0] -ne "BepInEx-BepInExPack-5.4.2305") {
    throw "The required BepInEx dependency string is missing or incorrect."
}
if (-not (Test-Path -LiteralPath $readmePath)) { throw "Thunderstore README.md is missing." }

Add-Type -AssemblyName System.Drawing
$icon = [System.Drawing.Image]::FromFile($iconPath)
try {
    if ($icon.Width -ne 256 -or $icon.Height -ne 256 -or $icon.RawFormat.Guid -ne [System.Drawing.Imaging.ImageFormat]::Png.Guid) {
        throw "Thunderstore icon.png must be a 256x256 PNG."
    }
}
finally {
    $icon.Dispose()
}

$bin = Join-Path $base "bin\$Configuration"
$lib = Join-Path $base "lib"
$runtimeDeps = Join-Path $base "RuntimeDeps"
$releaseRoot = Join-Path $base "release"
$packageName = "HowToFishXR-Thunderstore-v$packageVersion"
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
New-Item -ItemType Directory -Force -Path $stageFull | Out-Null

Copy-Item -LiteralPath $manifestPath, $iconPath, $readmePath -Destination $stageFull

$imagesDir = Join-Path $stageFull "images"
New-Item -ItemType Directory -Force -Path $imagesDir | Out-Null
Copy-Item -LiteralPath (Join-Path $base "docs\assets\fishing-showcase.gif") -Destination $imagesDir
Copy-Item -LiteralPath (Join-Path $base "docs\assets\gun-showcase.gif") -Destination $imagesDir

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

Compress-Archive -Path (Join-Path $stageFull "*") -DestinationPath $archiveFull -CompressionLevel Optimal

Write-Output "Thunderstore package created: $archiveFull"
Get-ChildItem -LiteralPath $stageFull -Recurse -File |
    ForEach-Object { $_.FullName.Substring($stageFull.Length + 1) } |
    Sort-Object
