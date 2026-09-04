# Contributing to HowToFishXR

Thanks for helping improve HowToFishXR.

## Before opening a pull request

1. Open an issue or clearly describe the problem the change solves.
2. Keep changes focused. Avoid unrelated formatting or generated-file churn.
3. Build both projects locally and test gameplay changes in a headset when possible.
4. Document the headset, OpenXR runtime, game version, and test path used.
5. Mark meaningful changes in your pull request description.

Run the public source validation before committing:

```powershell
.\tools\validate-public-source.ps1 -Configuration Release
.\tools\validate-public-source.ps1 -Configuration Debug
```

## Files that must never be committed

- Game assemblies or publicized game assemblies
- BepInEx, Unity, FishNet, or other third-party binary dependency bundles
- Decompiled game source
- Logs, crash dumps, diagnostic captures, or stack traces containing personal paths
- Backups, installed DLL history, release archives, or local game files
- Private research/reference workspaces or handoff notes
- Secrets, tokens, local config files, or personally identifying data

The repository `.gitignore` blocks these categories. Always review `git status` and the staged diff anyway.

## Style and scope

- Follow the existing C# style and preserve the project's defensive Unity lifecycle checks.
- Keep VR behavior local-player-only unless the change explicitly belongs to pose networking.
- Do not add permanent debug, dump, or per-frame logging. Errors that prevent initialization may use error logging.
- Do not add a dependency without explaining why it is required and how it is licensed.
- Do not add AI-generated code that you have not personally reviewed, understood, and tested.

## License and attribution

By submitting a contribution, you agree that it may be distributed under the project's Apache License 2.0. Retain the license and NOTICE attribution to **J_axon**, clearly identify substantial changes, and link reused work back to [HowToFishXR](https://github.com/Ghostx10742/HowToFishXR).
