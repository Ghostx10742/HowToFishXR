# HowToFishXR

HowToFishXR brings full 6DoF PCVR support to **How to Fish**, including tracked hands, physical fishing, two-handed weapons, roomscale movement, full-body IK, VR-ready UI, and multiplayer pose syncing.

This is a Windows PCVR mod. It is not a standalone Quest or PICO APK.

## Requirements

- **How to Fish** on Windows/Steam.
- A PCVR headset and active OpenXR runtime, such as SteamVR, Meta/Oculus, PICO Connect, or Virtual Desktop.
- [BepInEx 5.4.2305](https://thunderstore.io/c/how-to-fish/p/BepInEx/BepInExPack/). The mod manager installs this dependency automatically.

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
| A / right primary | Jump |
| B / right secondary | Switch bait |
| Hold left grip + B | Drop; hold B to charge and release to throw |
| X / left primary | Reload |
| Y / left secondary | Pause/unpause |
| Left grip tap | Inspect held item |
| F1 or both stick clicks | Open VR Settings while a menu is visible |
| F2 | Recenter view |
| F3 | Calibrate height and body |

Only the right-hand laser controls menus. The right hand is always the primary tool and weapon hand.

## Features

- Physical rod casting and reeling while preserving the game's fishing behavior.
- One- and two-handed gun aiming with physical sights, muzzle-correct firing, recoil, and support-hand steering.
- Physics-aware fish, food, item, creature, and dead-body handling with improved throwing.
- Full-body IK or Hands Only mode, roomscale movement, physical crouching, and snap or smooth turning.
- VR-converted HUD, menus, dialogue, character customization, item markers, death screen, water, damage effects, and UI blur.
- Correct VR handling for TNT, brass knuckles, punching, scopes, boats, and held-item animations.
- Multiplayer VR body, hand, tool, and fishing-pose synchronization between modded players.
- VR or flat-screen startup without uninstalling the mod.

## Multiplayer

HowToFishXR provides full multiplayer pose syncing between VR and flat-screen players who have the mod installed. Modded players can see VR head, hand, body, tool, and fishing poses. Some VR-player movements or interactions may still look inaccurate or bugged to other players; those remaining sync issues are actively being patched.

## Fishing

Hold the **left trigger**, swing the rod from the side like a baseball bat without swinging too fast, and release slightly early as the rod comes forward.

To reel physically, hold the **left grip** near the reel and rotate your hand around the crank. You can switch freely between physical reeling and holding the right trigger.

## VR Settings

The in-game VR Settings menu includes body mode, turning mode, snap angle, smooth-turn speed, roomscale movement, sprint mode, crouch mode, HUD scale, death view, and calibration.

Hand placement, right-hand primary controls, HUD distance, native player height, and VR visual behavior use permanent tested values.

## Installation

### Mod manager

Install HowToFishXR through the Thunderstore Mod Manager or r2modman. BepInEx will be installed automatically as a declared dependency.

### Manual

1. Install [BepInEx 5.4.2305](https://thunderstore.io/c/how-to-fish/p/BepInEx/BepInExPack/) using the instructions on its package page.
2. Download and unzip the HowToFishXR package.
3. Copy its `BepInEx` folder into the folder containing `How to Fish.exe` and allow the folders to merge.
4. Start your OpenXR runtime, then launch the game.

## Headset support

HowToFishXR supports major PCVR OpenXR headsets and controllers, including Meta Quest 1/2/3/3S over PCVR, Rift/Rift S, Valve Index, HTC Vive, Windows Mixed Reality, HP Reverb G2, Meta Touch Pro, and PICO PCVR.

## Open source and support

HowToFishXR is free and open source under Apache 2.0. Source code, issue reporting, and build instructions are available on [GitHub](https://github.com/Ghostx10742/HowToFishXR).

Donations are entirely optional. The mod is free and nothing is locked behind payment. If you would like to support development, visit [Ko-fi](https://ko-fi.com/j_axon).

## Credits

- Created and directed by **J_axon**.
- Testing by **mrbub**, **VernalWitch**, **Feesh**, **Wake**, **Aeolian**, and **supersaiyanslyr**.
