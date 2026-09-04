# Building HowToFishXR

HowToFishXR targets .NET Standard 2.1 and the Mono build of **How to Fish**. The repository contains the complete mod and preloader source, but it intentionally does not redistribute proprietary game assemblies, generated publicized references, BepInEx binaries, Unity runtime DLLs, logs, dumps, or backups.

## Requirements

- Windows and PowerShell 7 or Windows PowerShell 5.1
- .NET SDK 8 or newer
- An installed copy of **How to Fish**
- [BepInEx 5](https://thunderstore.io/c/how-to-fish/p/BepInEx/BepInExPack/) (`BepInEx-BepInExPack-5.4.2305`)
- Unity OpenXR managed/native runtime dependencies compatible with the game's Unity version

## Local reference layout

Create `lib/` at the repository root. Copy the compile-time dependencies from your legal local installations into it. At minimum, the project expects:

- BepInEx and Harmony assemblies
- `Mono.Cecil.dll`
- the UnityEngine modules used by the project
- Unity Input System, TextMeshPro, URP, FishNet, and Unity XR assemblies
- the game's `Assembly-CSharp.dll`

Put generated game/network references in `lib/publicized/`. These files are ignored by Git and must never be committed.

The local publicizer defaults to the standard Steam location. To generate the public game reference:

```powershell
.\tools\publicize.ps1
```

For a different game location:

```powershell
.\tools\publicize.ps1 -InputDir 'D:\SteamLibrary\steamapps\common\How to Fish\How to Fish\How to Fish_Data\Managed'
```

## Compile

From the repository root:

```powershell
dotnet build .\src\Preload\HowToFishVR.Preload.csproj -c Release
dotnet build .\src\HowToFishVR\HowToFishVR.csproj -c Release
```

For a debug build, replace `Release` with `Debug`.

## Assemble a local package

Extract the BepInExPack so its game-root overlay exists at `packages/BepInEx/BepInExPack/`, and place the native OpenXR loader files in `RuntimeDeps/`. Then run:

```powershell
.\tools\package.ps1
```

The local overlay is written to `dist/HowToFishXR/`. To install it directly into the default Steam location while the game is closed:

```powershell
.\tools\package.ps1 -Install
```

Or specify another game directory:

```powershell
.\tools\package.ps1 -Install -GameRoot 'D:\SteamLibrary\steamapps\common\How to Fish\How to Fish'
```

## Create a GitHub release ZIP

To create the upload-ready GitHub archive, run:

```powershell
.\tools\package-github-release.ps1
```

This writes `release/HowToFishXR-v1.0.0.zip`. The archive includes only the mod, its XR runtime components, installation/readme files and media, changelog, license, and NOTICE. It does not bundle BepInEx; users must install `BepInEx-BepInExPack-5.4.2305` first.

## Public CI checks

GitHub Actions validates the repository layout, project metadata, JSON, PowerShell syntax, accidental diagnostic logging, and the absence of private/proprietary files. A full binary compilation remains local because the game reference assemblies cannot be legally committed to the public repository.
