param(
    [string]$Cecil,
    [string]$InputDir = "C:\Program Files (x86)\Steam\steamapps\common\How to Fish\How to Fish\How to Fish_Data\Managed",
    [string]$OutDir,
    [string[]]$Assemblies = @("Assembly-CSharp.dll", "FishNet.Runtime.dll")
)

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($Cecil)) { $Cecil = Join-Path $repoRoot "lib\Mono.Cecil.dll" }
if ([string]::IsNullOrWhiteSpace($OutDir)) { $OutDir = Join-Path $repoRoot "lib\publicized" }

Add-Type -Path $Cecil
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# Resolver so Cecil can find dependent assemblies
$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
$resolver.AddSearchDirectory($InputDir)

foreach ($asm in $Assemblies) {
    $inPath = Join-Path $InputDir $asm
    if (-not (Test-Path $inPath)) { Write-Output "MISSING: $inPath"; continue }

    $rp = New-Object Mono.Cecil.ReaderParameters
    $rp.AssemblyResolver = $resolver
    $module = [Mono.Cecil.ModuleDefinition]::ReadModule($inPath, $rp)

    $typeCount = 0; $fieldCount = 0; $methodCount = 0

    function Publicize-Type($type) {
        if ($type.IsNested) {
            $type.IsNestedPublic = $true
        } else {
            $type.IsPublic = $true
        }
        $script:typeCount++
        foreach ($f in $type.Fields) {
            # keep compiler-generated backing fields as-is is fine; make everything public
            $f.IsPublic = $true
            $script:fieldCount++
        }
        foreach ($m in $type.Methods) {
            $m.IsPublic = $true
            $script:methodCount++
        }
        foreach ($nt in $type.NestedTypes) { Publicize-Type $nt }
    }

    foreach ($t in $module.Types) {
        if ($t.Name -eq "<Module>") { continue }
        Publicize-Type $t
    }

    $outPath = Join-Path $OutDir $asm
    $module.Write($outPath)
    Write-Output ("Publicized {0}: types={1} fields={2} methods={3} -> {4}" -f $asm, $typeCount, $fieldCount, $methodCount, $outPath)
}
