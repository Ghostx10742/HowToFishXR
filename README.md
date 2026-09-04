<h1 align="center">HowToFishXR</h1>

<p align="center"><strong>by J_axon</strong></p>

<p align="center">
  <img src="docs/assets/howtofishxr-cover.png" width="520" alt="HowToFishXR cover art">
</p>

<p align="center">
  <a href="https://github.com/Ghostx10742/HowToFishXR/releases/latest"><img alt="GitHub Version" src="https://img.shields.io/github/v/release/Ghostx10742/HowToFishXR?style=for-the-badge&logo=github"></a>
  <a href="https://github.com/Ghostx10742/HowToFishXR/releases/latest"><img alt="GitHub Downloads" src="https://img.shields.io/github/downloads/Ghostx10742/HowToFishXR/total?style=for-the-badge&logo=github"></a>
  <br>
  <a href="https://github.com/Ghostx10742/HowToFishXR/actions/workflows/build-release.yml"><img alt="Release Check" src="https://img.shields.io/github/actions/workflow/status/Ghostx10742/HowToFishXR/build-release.yml?branch=main&style=for-the-badge&label=RELEASE"></a>
  <a href="https://github.com/Ghostx10742/HowToFishXR/actions/workflows/build-debug.yml"><img alt="Debug Check" src="https://img.shields.io/github/actions/workflow/status/Ghostx10742/HowToFishXR/build-debug.yml?branch=main&style=for-the-badge&label=DEBUG"></a>
</p>

HowToFishXR is a free, open-source [BepInEx](https://thunderstore.io/c/how-to-fish/p/BepInEx/BepInExPack/) mod that brings full 6DoF PCVR to **How to Fish**. It adds tracked hands, motion-controlled tools, physical fishing and reeling, one- and two-handed gun handling, roomscale movement, full-body IK, VR-native UI, multiplayer pose syncing, and the visual feedback needed to play the complete game inside a headset.

> **Credit required.** You are free to study, modify, and redistribute this project under the Apache License 2.0. If you reuse its code, you must preserve the license and NOTICE attribution, clearly credit **J_axon** as the creator, and link to [HowToFishXR](https://github.com/Ghostx10742/HowToFishXR). See [LICENSE](LICENSE) and [NOTICE](NOTICE).

## Support the project

HowToFishXR is completely free, and donating is entirely optional. Nothing is locked behind payment and you never need to donate to download, use, or modify the mod. If you enjoy it and would like to support future work, you can help on Ko-fi:

### [ko-fi.com/j_axon](https://ko-fi.com/j_axon)

## Controls

Button names use the familiar Meta/PICO layout. Valve Index, Vive, WMR, and other OpenXR controllers use the equivalent primary and secondary buttons for each hand.

| Input | Action |
|---|---|
| Left stick | Move; while driving, steer left/right and control throttle with up/down |
| Left stick click | Sprint |
| Right stick left/right | Snap turn or smooth turn |
| Right stick up/down | Previous/next inventory slot |
| Right stick click | Crouch |
| Right trigger | Fire, use, punch, or hold to reel in; click/drag menus with the right-hand laser |
| Left trigger | Secondary use; hold and release to cast or reel out |
| Right grip | Pick up, interact, and enter/exit the boat |
| Support-hand grip near a gun | Grab the foregrip for two-handed aiming |
| Support-hand grip near a fishing reel | Latch onto the reel for physical reeling |
| Support-hand grip near a non-tool item/body | Visually latch the second hand; the main hand keeps control |
| A / right primary | Jump |
| B / right secondary | Switch bait |
| Hold support-hand grip + B | Drop; hold B to charge and release to throw |
| X / left primary | Reload |
| Y / left secondary | Pause/unpause |
| Left grip tap | Inspect the held item |
| F1 or both stick clicks | Open/close VR Settings while the main or pause menu is open |
| F2 | Recenter forward direction |
| F3 | Start standing-height and body calibration |

Only the right-hand laser controls menus. Gun aiming is physical—there is no separate ADS button—and push-to-talk is intentionally not bound by the mod.

## Requirements and headset support

- **How to Fish** on Windows/Steam.
- [BepInEx 5](https://thunderstore.io/c/how-to-fish/p/BepInEx/BepInExPack/) (`BepInEx-BepInExPack-5.4.2305`).
- A PCVR headset and an active OpenXR runtime such as SteamVR, Meta/Oculus, PICO Connect, or Virtual Desktop.

HowToFishXR uses OpenXR instead of locking itself to one brand. Its included interaction profiles cover Meta Quest 1, 2, 3 and 3S over Link/Air Link/SteamVR/Virtual Desktop, Rift and Rift S, Valve Index, HTC Vive, Windows Mixed Reality, HP Reverb G2, Meta Touch Pro, and Khronos-compatible controllers. PICO PCVR controllers use Unity's common OpenXR controller mappings. This is a Windows PCVR mod, not a standalone Android/Quest APK.

## Fishing in VR

### Casting

Hold the **left trigger** to prepare the cast. Swing the rod from the side like a baseball bat, but do not swing extremely fast. Release the trigger a little earlier than you might expect as the rod comes forward. A controlled side swing and an early release produce a much more reliable cast than a hard wrist flick.

<p align="center">
  <img src="docs/assets/fishing-showcase.gif" width="800" alt="Casting and physically reeling a fishing rod in HowToFishXR">
</p>

### Physical reeling

Move your support hand to the reel and hold its grip button. Your visible hand latches onto the crank while the rod remains driven by your main hand. Rotate your support hand around the reel to crank it physically; either direction is accepted. Grabbing the reel after a cast automatically changes the rod from reeling out to reeling in, and faster cranking uses the game's faster reel behavior. You can switch freely between physical cranking and holding the right trigger to reel.

The rod, bait, bend, line, audio, caught-item behavior, and game-authored reel speeds remain part of the original fishing system—the mod changes how you physically control them.

## Gun handling

Guns sit on the calibrated main-hand grip and fire from their real muzzle. Bring your support hand near the weapon's authored foregrip and hold grip to enter two-handed handling. Both controller positions influence the weapon's direction, the main hand remains the rear pivot, and the support hand steadies and steers the barrel. Release support grip to return to one-handed handling.

<p align="center">
  <img src="docs/assets/gun-showcase.gif" width="800" alt="Shooting and two-handed gun handling in HowToFishXR">
</p>

Two-handed handling activates the game's aiming accuracy without forcing a flat-screen FOV zoom. Scopes and sights remain physical objects on the gun; the flat 2D sniper overlay is disabled, and scoped shots follow the real muzzle. Native recoil still appears without moving the grip away from your hands, and the flat-screen sprint tuck animation is removed from held VR items.

### A note about the support-hand finger

At some angles the finger on the left/support hand can look twisted. That shape comes from the game's original mirrored hand/finger geometry. It is present in the flat-screen model too, but VR lets you inspect it much more closely; it is not controller tracking drift or a broken two-hand grip.

<p align="center">
  <img src="docs/assets/left-hand-finger-note.jpg" width="800" alt="The original mirrored left-hand finger geometry visible in VR">
</p>

## Complete feature list

### VR rendering and tracking

- Full stereoscopic 6DoF head tracking through Unity OpenXR, with the newest predicted head and hand poses applied immediately before rendering.
- Tracked left and right hands with permanent, headset-tested wrist/palm calibration.
- Full roomscale leaning, walking, and physical crouching with collision-aware body movement.
- Snap turning with configurable angle or smooth turning with configurable speed.
- Recenter support and guided T-pose height/body calibration with controller haptics.
- The game's native eye height is used permanently; there is no artificial height-offset slider.
- A one-eye desktop mirror shows the live VR view instead of a black or split stereo window.
- The main-menu camera rides the moving boat and remains stable through focus changes and menu interactions.
- Character customization is repositioned for VR so the avatar, arrows, and menu remain usable.
- VR can be disabled without uninstalling the mod for a normal flat-screen launch.

### Body and multiplayer

- Optional full visible player body restored from the game's own character model.
- Controller-driven two-bone arm IK with natural elbow poles, shoulder reach correction, and render-timed solving.
- Hands Only mode for players who do not want the full body rendered.
- The body stays aligned through roomscale motion, snap turns, boats, death, respawning, scene changes, and rejoining.
- The body remains visible while riding a boat and hides only while actively driving it.
- VR head, hand, arm, foot, body, held-tool, and fishing-line poses sync through the game's FishNet transport when the host and receiving players have the mod.
- Modded flat-screen players can receive and display VR players' poses. Vanilla hosts remain unaffected; without the mod on the host, extra VR pose broadcasting simply does not start.

### Hands, items, and combat

- Tools and weapons follow the dominant controller using each item's authored grip pose.
- One- and two-handed gun handling with support-hand steering, physical sights, muzzle-correct shots, and game-authored accuracy/recoil.
- Fishing rods use the same calibrated hand anchor as guns and remain main-hand driven while the reel hand latches visually.
- Fish, creatures, food, dead bodies, and other non-tool items are held with the game's physics-based movement rather than being frozen to the camera.
- Fish remain one-handed so their secondary physics can move naturally.
- Two-handing a normal non-tool item or dead body latches only the support hand; it does not take control away from the main hand or stretch the object.
- Dead-body carrying keeps ragdoll physics and the revive slap presentation.
- TNT stays in the main hand while its lighter is correctly placed in the support hand.
- Brass knuckles are positioned independently on both tracked hands; punching uses head aim and removes the canned flat-screen knuckle/punch pose.
- Empty-hand punching also uses head aim and cannot target or damage the local player's own restored body.
- Held items do not play the flat-screen sprint indent/tuck animation.
- Charged throws preserve the game's force while blending the calibrated hand direction, controller release velocity, and wrist motion for better aiming.

### UI, menus, and visual feedback

- The real game HUD, main menu, pause menu, settings screens, dialogue, prompts, and overlays are converted into readable VR panels rather than replaced with a separate flat interface.
- A right-hand laser supports accurate clicking and dragging, including sliders and moving main-menu panels.
- A native-styled **VR Settings** entry is added to both the main and pause menus.
- HUD distance and scale are configurable; head-following keeps the HUD level instead of pitching it into the player's face.
- World item dots remain attached to their items, keep the game's distance scaling and height behavior, and render through walls as the base game intends.
- Caught-fish notices, item information and prices, hit markers, NPC dialogue, inventory, bait text, and other camera-projected UI are corrected for VR space.
- The actual death UI is presented head-locked at full-view scale, with a recreated translucent dark backdrop and first- or third-person death viewing.
- The game's underwater color/tint state follows the rendered VR camera, including while dead, and clears immediately when the view leaves the water.
- Blood, poison, fire, damage, dying, and low-health vignette feedback are reproduced in the headset.
- The game's frosted-glass UI blur is preserved through a safe persistent capture; a flat dark fallback is available if a system renders the blur incorrectly.
- The flat-screen sniper overlay and zoom are removed in favor of physical scope use.

## VR Settings menu

Open the native-styled **VR Settings** page from the main menu or pause menu. You can also press **F1** or click both thumbsticks while either menu is visible.

| Setting | What it changes |
|---|---|
| Body | Full Body IK or Hands Only |
| Turning | Snap or Smooth turning |
| Snap Angle | Degrees rotated per snap turn |
| Smooth Speed | Smooth-turn degrees per second |
| Dominant Hand | Which hand owns tools, aiming, and primary item control |
| Roomscale Movement | Physical walking and crouching move the game body |
| VR Post Processing | Uses the game's color grading and underwater post effects in the headset |
| Sprint Mode | Hold or Toggle |
| Crouch Mode | Hold or Toggle |
| HUD Scale | Size of the VR HUD |
| HUD Distance | Distance of the HUD from the headset |
| Death View | First-person ragdoll view or head-tracked third-person view |
| Calibrate | Starts the guided standing T-pose calibration |

Hand position/rotation sliders, the height-offset slider, and motion-vignette options are intentionally not present. The tested hand calibration is built into the mod, player height uses the game's native height, and there is no forced motion vignette.

### Advanced BepInEx settings

The generated `BepInEx/config/com.jaxon.howtofishvr.cfg` also exposes advanced values:

- **Disable VR** for persistent flat-screen startup; `--disable-vr` is the one-launch Steam option.
- **World Scale**.
- **Recenter** and **Calibrate Height** keyboard bindings, plus saved calibration values.
- **Turn Deadzone** and **Movement Deadzone**.
- **Head Relative Movement**; when disabled, movement follows the support-hand direction.
- **Boat Steer Sensitivity**.
- **HUD Follows Head** and **HUD Smoothing**.
- **Grip Threshold**.
- **Real UI Blur** toggle for the native frosted effect/fallback.
- **Body Offset** for advanced body-position correction.
- **VR Pose Sync** and **VR Pose Sync Rate** from 5–60 Hz.

## Launching in VR or flat screen

VR is the default. Start your chosen OpenXR runtime before launching **How to Fish**, then start the game normally through Steam.

For a one-time flat-screen launch, add this Steam launch option:

```text
--disable-vr
```

For persistent flat-screen mode, set `Disable VR = true` in `BepInEx/config/com.jaxon.howtofishvr.cfg`. The mod can remain installed, and its multiplayer receive side can still display synced VR players on a modded flat-screen client.

## Installation

The first public package will be attached to the [GitHub Releases](https://github.com/Ghostx10742/HowToFishXR/releases) page. Thunderstore packaging is intentionally not included yet.

1. Install [BepInEx 5](https://thunderstore.io/c/how-to-fish/p/BepInEx/BepInExPack/).
2. Download the latest HowToFishXR release archive.
3. Extract the archive into the folder containing `How to Fish.exe` and allow folders to merge.
4. Start an OpenXR runtime, then launch the game through Steam.

Developers who want to compile the source should follow [BUILDING.md](BUILDING.md). Game assemblies, logs, dumps, backups, and decompiled game files are deliberately not included in this repository.

## Compatibility

HowToFishXR changes the camera, input, player-body presentation, item handling, and game UI. Mods that patch the same systems may conflict. Multiplayer gameplay remains compatible with vanilla lobbies, but full remote VR body/hand posing requires a modded host and modded receiving clients.

If a game update changes the relevant methods or assets, the mod may require an update. Please include the game version, headset/runtime, and clear reproduction steps in bug reports—never upload personal or unrelated logs without reviewing them first.

## Building and contributing

See [BUILDING.md](BUILDING.md) for local compilation and packaging. See [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request. Proprietary game assemblies and locally generated reference files must never be committed.

## Open source and attribution

HowToFishXR is open source under the **Apache License 2.0**. You may inspect, modify, and redistribute the code under that license. When you reuse or distribute this work, keep the Apache license and the project's NOTICE attribution with it, clearly credit **J_axon** as the original creator, mark your changes, and link back to [HowToFishXR](https://github.com/Ghostx10742/HowToFishXR). The game and all original game assets remain the property of their respective owners.

## Credits

- Created and directed by **J_axon**.
- Testing by **mrbub**, **VernalWitch**, **Feesh**, **Wake**, **Aeolian**, and **supersaiyanslyr**.

## AI disclosure

AI was used during the development of this project, mainly for revisions, inquiries, and things I just did not know. This does not mean the mod was fully AI-made, but rather that AI was used as part of the development process. I wanted to disclose this for people who may have a problem with AI being involved and may not want anything to do with it. Even though I disagree with your view on AI, I still respect your opinion on the subject.
