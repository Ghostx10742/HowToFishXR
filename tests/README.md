# Handedness smoke tests

Build and install the updated mod with its dependencies, launch it once to generate configuration, then close the game. Build references in `lib/` are required as described in `BUILDING.md`.

Run with a compatible OpenXR simulator manifest:

```powershell
.\tests\run-smoke.ps1 -RuntimeManifest 'C:\path\to\runtime.json'
```

The runner temporarily installs a test-only BepInEx plugin, launches left-handed VR, right-handed VR, and flat-screen mode, and restores the original configuration and removes the test DLL afterward. It supplies the Steam app ID so Steam does not relaunch the process without the testing environment. It never changes the machine-wide OpenXR runtime or loads a save. Results and Unity logs are written to the ignored `artifacts/handedness-smoke/` directory. Do not package the test plugin.

Checks cover the actual Unity action bindings for all default gameplay controls, role-selected sticks and triggers, unchanged anatomical pose bindings, reinjection without duplicates, preservation of existing non-XR bindings, main/support controller selection, calibrated melee origin/direction, restart-only setting behavior, and flat-screen startup without a VR rig or injected gameplay bindings. Independent synthetic Unity XR devices exercise trigger, grip, movement, and turning values through the Input System.

The left-hand run also checks the actual item prefabs in an inactive test hierarchy: full model reflection, original main-grip alignment, hand rotation, muzzle direction, sampled animation clips, idempotence, and restoration through the game's Drop hook. Articulated-body checks use a separate physics scene with gameplay/network scripts removed and collisions disabled; they compare mirrored joint motion against the original prefab. These tests never load or modify a save.

The synthetic input checks deliberately test the Unity boundary. The tested `phunkaeg/xr-sim` runtime stores only one control per shared OpenXR action, so Unity's left/right subaction paths cannot carry independent analog values. Its default 64-action capacity also overflows with this mod's controller profiles; the local test runtime used a 512-action capacity. Rendering, startup, and tracked-device availability were tested with that runtime, but end-to-end native analog input is not certified by these checks.

Still requires manual testing: physical casting/reeling, tool/knife/grip appearance, boat handling, menu dragging/keyboard typing on actual controllers, and multiplayer sessions with an unmodified host/client. No network message layout or serializer changes are part of handedness support.
