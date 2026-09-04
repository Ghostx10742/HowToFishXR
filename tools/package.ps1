param(
    [switch]$Install,
    [string]$GameRoot = "C:\Program Files (x86)\Steam\steamapps\common\How to Fish\How to Fish"
)

$ErrorActionPreference = "Stop"
$base = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$lib  = "$base\lib"
$pkg  = "$base\packages"
$bin  = "$base\bin\Release"
$dist = "$base\dist\HowToFishXR"

Write-Output "=== Building projects (Release) ==="
dotnet build "$base\src\Preload\HowToFishVR.Preload.csproj" -c Release -nologo -v q | Out-Null
dotnet build "$base\src\HowToFishVR\HowToFishVR.csproj"     -c Release -nologo -v q | Out-Null

Write-Output "=== Assembling dist overlay ==="
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

# 1. BepInEx core + doorstop (game-root overlay)
$bepPack = "$pkg\BepInEx\BepInExPack"
Copy-Item "$bepPack\*" $dist -Recurse -Force

# 2. Main plugin + lang.json + managed XR deps
$pluginDir = "$dist\BepInEx\plugins\HowToFishXR"
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
Copy-Item "$bin\plugin\HowToFishVR.dll" $pluginDir -Force
Copy-Item "$base\src\HowToFishVR\lang.json" $pluginDir -Force
foreach ($n in @("Unity.XR.OpenXR.dll","Unity.XR.Management.dll","Unity.XR.CoreUtils.dll","UnityEngine.SpatialTracking.dll","Unity.XR.Interaction.Toolkit.dll")) {
    Copy-Item "$lib\$n" $pluginDir -Force
}

# 3. Preloader patcher + native runtime deps
$patcherDir = "$dist\BepInEx\patchers\HowToFishXR"
New-Item -ItemType Directory -Force -Path "$patcherDir\RuntimeDeps" | Out-Null
Copy-Item "$bin\Preload\HowToFishVR.Preload.dll" $patcherDir -Force
Copy-Item "$base\RuntimeDeps\UnityOpenXR.dll"  "$patcherDir\RuntimeDeps\" -Force
Copy-Item "$base\RuntimeDeps\openxr_loader.dll" "$patcherDir\RuntimeDeps\" -Force

# 4. (Removed) ModMenu/ModAPI dependency — the mod now ships its own in-game VR Settings panel
# (UI/VRSettingsPanel.cs), so no ModMenu is bundled and no ModMenu is required.

Write-Output "dist assembled at: $dist"
Get-ChildItem $dist -Recurse -File | ForEach-Object { $_.FullName.Replace($dist,'') } | Sort-Object

if ($Install) {
    Write-Output "=== Installing into game folder ==="
    if (-not (Test-Path $GameRoot)) { throw "Game root not found: $GameRoot" }
    Copy-Item "$dist\*" $GameRoot -Recurse -Force
    Write-Output "Installed to: $GameRoot"
}
