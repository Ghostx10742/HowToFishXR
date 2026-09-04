# Installing HowToFishXR

HowToFishXR requires **How to Fish** on Windows, a PCVR headset with an active OpenXR runtime, and [BepInEx 5](https://thunderstore.io/c/how-to-fish/p/BepInEx/BepInExPack/) (`BepInEx-BepInExPack-5.4.2305`). BepInEx is not bundled in the GitHub release archive.

1. Install BepInEx 5.4.2305 into the folder containing `How to Fish.exe` and run the game once.
2. Start your OpenXR runtime, such as SteamVR, Meta/Oculus, PICO Connect, or Virtual Desktop.
3. Unzip the HowToFishXR release, then copy its `BepInEx` folder into the folder containing `How to Fish.exe` and allow the folders to merge.
4. Launch the game normally through Steam.

The archive contains only `README.md`, `NOTICE`, and the mod's `BepInEx/plugins` and `BepInEx/patchers` folders. It does not contain BepInEx itself, game files, logs, dumps, or backups.

For a one-time flat-screen launch without uninstalling the mod, use the Steam launch option `--disable-vr`.
