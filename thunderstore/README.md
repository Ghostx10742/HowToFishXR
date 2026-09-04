# HowToFishXR

**by J_axon**

<p align="center">
  <img src="https://raw.githubusercontent.com/Ghostx10742/HowToFishXR/main/docs/assets/howtofishxr-cover.png" width="420" alt="HowToFishXR cover art">
</p>

HowToFishXR is a free, open-source mod that brings full 6DoF PCVR to **How to Fish**. It adds tracked hands, physical fishing and reeling, one- and two-handed gun handling, roomscale movement, full-body IK, VR-native UI and typing, multiplayer pose syncing, and the visual feedback needed to play the complete game inside a headset.

This is a Windows PCVR mod. It is not a standalone Quest or PICO APK.

## Requirements

- **How to Fish** on Windows/Steam.
- A PCVR headset and active OpenXR runtime, such as SteamVR, Meta/Oculus, PICO Connect, or Virtual Desktop.
- [BepInEx 5.4.2305](https://thunderstore.io/c/how-to-fish/p/BepInEx/BepInExPack/). A mod manager installs this declared dependency automatically.

## Controls

| Input | Action |
|---|---|
| Left stick | Move; steer and control throttle while driving |
| Left stick click | Sprint |
| Right stick left/right | Snap or smooth turn |
| Right stick up/down | Previous/next inventory slot |
| Right stick click | Crouch |
| Right trigger | Fire, use, punch, reel in, or click/drag menus |
| Left trigger | Secondary use; hold and release to cast or reel out |
| Right grip | Pick up, interact, or enter/exit the boat |
| Left grip near a gun | Two-handed aiming |
| Left grip near a fishing reel | Physical reeling |
| Left grip near a non-tool item/body | Visually latch the second hand |
| A / right primary | Jump |
| B / right secondary | Switch bait |
| Hold left grip + B | Drop; hold B to charge and release to throw |
| X / left primary | Reload |
| Y / left secondary | Pause/unpause |
| Left grip tap | Inspect held item |
| F1 or both stick clicks | Open VR Settings while a menu is visible |
| F2 | Recenter view |
| F3 | Calibrate height and body |

Only the right-hand laser controls menus. The right hand is always the primary tool hand. Throwing is aimed from the right hand; empty-hand and brass-knuckle punches use head aiming. Gun aiming is physical, so there is no separate ADS button.

## Multiplayer

HowToFishXR provides full multiplayer pose syncing between VR and flat-screen players who have the mod installed. Modded players can see VR head, hand, body, tool, and fishing poses. Some VR-player movements or interactions may still look inaccurate or bugged to other players; those remaining sync issues are actively being patched. Vanilla lobbies remain compatible.

## Fishing in VR

Hold the **left trigger** to prepare a cast. Swing the rod from the side like a baseball bat without swinging too fast, then release slightly early as the rod comes forward. A controlled side swing works better than a hard wrist flick.

<p align="center">
  <img src="https://raw.githubusercontent.com/Ghostx10742/HowToFishXR/main/docs/assets/fishing-showcase.gif" width="800" alt="Physical casting and reeling">
</p>

For physical reeling, bring your left hand to the reel and hold **left grip**. The hand latches onto the crank while the rod remains controlled by the right hand. Rotate your left hand around the reel to crank it. You can freely switch between physical reeling and holding the right trigger.

## Gun handling

Guns sit on the calibrated right-hand grip and fire from their real muzzle. Hold **left grip** near the weapon's foregrip for two-handed handling; both hands then influence its direction and stability. Release left grip to return to one-handed handling. Physical sights, scopes, native recoil, and muzzle-correct firing remain supported without flat-screen ADS zoom.

<p align="center">
  <img src="https://raw.githubusercontent.com/Ghostx10742/HowToFishXR/main/docs/assets/gun-showcase.gif" width="800" alt="One- and two-handed gun handling">
</p>

At some angles, a finger on the left hand may look twisted. This comes from the game's original mirrored hand geometry and is visible in the flat-screen model too; it is not controller drift or a broken grip.

<p align="center">
  <img src="https://raw.githubusercontent.com/Ghostx10742/HowToFishXR/main/docs/assets/left-hand-finger-note.jpg" width="800" alt="Original mirrored left-hand finger geometry">
</p>

## Features

- Full stereoscopic OpenXR rendering, tracked hands, roomscale movement, physical crouching, recentering, height calibration, and snap or smooth turning.
- Full-body IK or Hands Only mode, with body behavior adapted for boats, death, respawning, and scene changes.
- Physical rod casting and reeling, one- and two-handed guns, physics-aware item/body handling, throwing, TNT, brass knuckles, and VR punching.
- VR-converted HUD, menus, dialogue, character customization, item markers, death UI, underwater visuals, damage effects, physical scopes, and UI blur.
- Accurate right-hand laser clicking and dragging plus a VR keyboard for names, text boxes, and other typing events.
- VR or flat-screen startup without uninstalling the mod.

## VR keyboard and menus

Selecting a writable text field automatically opens the in-world keyboard in the correct menu space. While it is open, the normal menu laser is hidden and the right controller becomes a keyboard-only pointer. The keyboard follows the moving main-menu boat, types directly into the selected field, and has a dedicated **Close** button that safely returns control to the menu.

The main menu, pause menu, settings, character creator, inventory, HUD, dialogue, prompts, and world markers are presented for VR. Only the right controller operates the menu pointer.

## VR Settings

Open the native-styled VR Settings page from the main or pause menu, or press **F1**/both stick clicks while a menu is visible.

- Full Body IK or Hands Only
- Snap or Smooth turning, snap angle, and smooth-turn speed
- Roomscale movement
- Hold or Toggle sprint and crouch
- HUD scale
- First- or third-person death view
- Guided standing/body calibration

Hand placement, right-hand primary controls, HUD distance, native player height, and VR visual behavior use permanent tested values.

## Headset support

HowToFishXR uses OpenXR and supports major PCVR headsets and controllers: Meta Quest 1/2/3/3S over Link, Air Link, SteamVR, or Virtual Desktop; Rift and Rift S; Valve Index; HTC Vive; Windows Mixed Reality and HP Reverb G2; Meta Touch Pro; and PICO PCVR controllers.

## Launching in VR or flat screen

Start your OpenXR runtime, then launch **How to Fish** normally. For one flat-screen launch, use the Steam launch option `--disable-vr`. For persistent flat-screen mode, set `Disable VR = true` in `BepInEx/config/com.jaxon.howtofishvr.cfg`.

## Installation

### Mod manager

Install HowToFishXR through the Thunderstore Mod Manager or r2modman. Its BepInEx dependency will be installed automatically.

### Manual

1. Install [BepInEx 5.4.2305](https://thunderstore.io/c/how-to-fish/p/BepInEx/BepInExPack/) using the instructions on its package page.
2. Download and unzip the HowToFishXR package.
3. Copy its `BepInEx` folder into the folder containing `How to Fish.exe` and allow the folders to merge.
4. Start your OpenXR runtime, then launch the game.

## Compatibility

Mods that patch the camera, input, player body, item handling, or UI may conflict with HowToFishXR. A game update that changes those systems may require a mod update.

## Open source and support

HowToFishXR is free and open source under Apache 2.0. Source code, issue reporting, and full documentation are available on [GitHub](https://github.com/Ghostx10742/HowToFishXR). If you reuse its code, preserve the license and attribution, clearly credit **J_axon** as the creator, and link to the project.

Donations are entirely optional. The mod is free and nothing is locked behind payment. If you would like to support development, visit [Ko-fi](https://ko-fi.com/j_axon).

## Credits

- Created and directed by **J_axon**.
- Testers: **Aeolian**, **Feesh**, **mrbub**, **supersaiyanslyr**, **VernalWitch**, and **Wake**.

## AI disclosure

AI was used during development for revisions, inquiries, and areas where assistance was needed. The mod was not fully AI-made; AI was one part of the development process. This disclosure is included for anyone who prefers to know when AI was involved.
