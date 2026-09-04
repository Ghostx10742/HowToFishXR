param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Push-Location $repoRoot

try {
    $required = @(
        "README.md",
        "LICENSE",
        "NOTICE",
        "BUILDING.md",
        "CONTRIBUTING.md",
        "src/HowToFishVR/HowToFishVR.csproj",
        "src/Preload/HowToFishVR.Preload.csproj",
        "docs/assets/howtofishxr-cover.png",
        "docs/assets/fishing-showcase.gif",
        "docs/assets/gun-showcase.gif",
        "docs/assets/left-hand-finger-note.jpg"
    )
    foreach ($path in $required) {
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Required public file is missing: $path"
        }
    }

    $tracked = @(git ls-files)
    if ($LASTEXITCODE -ne 0) { throw "git ls-files failed." }

    $forbidden = '(^|/)(bin|obj|dist|release|artifacts|lib|packages|RuntimeDeps|decompiled|decompiled-new|ref|refs|notes|_installed_before_fix|_removed_from_game|BepInEx)(/|$)|(^|/)HANDOFF_CODEX\.md$|\.(dll|pdb|mdb|log|dmp|dump|stackdump|trace|zip|7z|rar)$'
    $badFiles = @($tracked | Where-Object { $_ -match $forbidden })
    if ($badFiles.Count -gt 0) {
        throw "Private, generated, binary, or diagnostic files are tracked:`n$($badFiles -join "`n")"
    }

    Get-ChildItem -LiteralPath "src" -Recurse -Filter "*.json" | ForEach-Object {
        Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json | Out-Null
    }
    Get-ChildItem -LiteralPath "src" -Recurse -Filter "*.csproj" | ForEach-Object {
        [xml](Get-Content -LiteralPath $_.FullName -Raw) | Out-Null
    }

    $parseErrors = @()
    Get-ChildItem -LiteralPath "tools" -Filter "*.ps1" | Where-Object { $_.Name -ne "backup.ps1" } | ForEach-Object {
        $tokens = $null
        $errors = $null
        [System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$errors) | Out-Null
        if ($errors) { $parseErrors += $errors }
    }
    if ($parseErrors.Count -gt 0) {
        throw "PowerShell parse errors:`n$($parseErrors -join "`n")"
    }

    $loggingPatterns = 'LogInfo\s*\(|LogWarning\s*\(|LogDebug\s*\(|Debug\.Log(?:Warning|Error)?\s*\(|Console\.Write(?:Line)?\s*\('
    $loggingHits = @(Get-ChildItem -LiteralPath "src" -Recurse -Filter "*.cs" |
        Select-String -Pattern $loggingPatterns)
    if ($loggingHits.Count -gt 0) {
        throw "Non-production logging was found:`n$($loggingHits -join "`n")"
    }

    $mainProject = [xml](Get-Content -LiteralPath "src/HowToFishVR/HowToFishVR.csproj" -Raw)
    $projectVersion = [string](($mainProject.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version)
    $pluginSource = Get-Content -LiteralPath "src/HowToFishVR/Plugin.cs" -Raw
    $versionMatch = [regex]::Match($pluginSource, 'public const string Version = "([^"]+)";')
    if (-not $versionMatch.Success -or $versionMatch.Groups[1].Value -ne $projectVersion) {
        throw "Plugin.cs and HowToFishVR.csproj versions do not match."
    }

    $canCompile = (Test-Path -LiteralPath "lib/publicized/Assembly-CSharp.dll") -and
                  (Test-Path -LiteralPath "lib/publicized/FishNet.Runtime.dll") -and
                  (Test-Path -LiteralPath "lib/BepInEx.dll")
    if ($canCompile) {
        dotnet build "src/Preload/HowToFishVR.Preload.csproj" -c $Configuration -nologo
        if ($LASTEXITCODE -ne 0) { throw "Preloader build failed." }
        dotnet build "src/HowToFishVR/HowToFishVR.csproj" -c $Configuration -nologo
        if ($LASTEXITCODE -ne 0) { throw "Plugin build failed." }
    }
    else {
        Write-Output "Compile skipped: proprietary/local reference assemblies are intentionally absent from the public checkout."
    }

    Write-Output "HowToFishXR $Configuration public-source validation passed."
}
finally {
    Pop-Location
}
