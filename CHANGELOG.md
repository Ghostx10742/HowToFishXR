# Changelog

All notable changes to HowToFishXR will be documented here.

## 1.3.0

- Changed empty-hand, brass-knuckle, and knife attacks from head aim to calibrated right-hand aim.
- Increased VR melee targeting reach from the game's 1.5 meters to a 3-meter minimum and added direct input-time targeting for ground-creature bodies whose small or buried colliders are rejected by the delayed melee pipeline.
- Added a temporary segmented melee guide for empty fists, brass knuckles, and the knife: red during/recent trigger input, then light blue before hiding five seconds after the last input. It starts enabled and has an on/off toggle in VR Settings.
- Added a None turning mode for players who want physical turning only.
- Restored the crab rod's native physical-reel path and throttled only its rate while physically cranking, retaining immediate feedback without the original excessive speed.
- Put the knife through the same authored right-hand root-grip solve used by guns, while suppressing only its local flat-screen sway and attack-lunge animation in VR.
- Added physical reeling support for the crab fishing rod's differently authored crank axis.
- Fixed the sailing main-menu camera and tracking basis when returning from gameplay, including reuse of the clean initial boat-camera offset.
- Fixed snap-turn body drift by synchronizing the IK body's tracking parent immediately after each turn.
- Centered the full-body IK on the headset's floor-projected body pivot, matching Unity XRI/LCVR and removing the remaining player-root offset that caused a small orbit during snap and smooth turns.
- Moved the knife toward headset/view-left rather than raw controller-local X, restored its previously correct authored-grip rotation solve, and kept the hand fully controller-driven.
- Made the melee guide display the exact input-time assisted target without restoring per-frame creature scans.
- Increased the crab rod's physical-crank speed to 60% of its native rate and enabled the regular rod's fast-reeling multiplier/text when cranked quickly.
- Removed HowToFishXR-owned log output and eliminated per-frame creature enumeration from the melee aiming guide; normal BepInEx logging remains unchanged.
- Prevented remote-player death UI and stale death canvases from appearing in the local headset during multiplayer.
- Made low-health, blood/hit, underwater, and death effects independent of HUD Scale so full-view overlays retain fixed coverage.
- Added full fists, brass-knuckles, knife, and segmented melee-guide documentation with a new showcase GIF.

## 1.2.0

- Added a VR keyboard that opens for text-entry fields, follows the active menu, uses a dedicated keyboard pointer, and includes a reliable Close button.
- Improved right-hand menu clicking and dragging while keeping the normal menu laser hidden during keyboard input.
- Improved SteamVR cold-start handling so launching before SteamVR is ready does not produce an inverted VR view.
- Applied the finalized hand calibration consistently to guns, fishing rods, and both brass-knuckle meshes.
- Improved render-timed hand, arm IK, and knuckle posing for smoother, correctly aligned motion.
- Restored normal BepInEx logging behavior while keeping unnecessary mod diagnostics and dump systems removed.

## 1.0.0

- Initial public release.
- Full 6DoF OpenXR head and controller tracking.
- Roomscale movement, physical crouching, snap/smooth turning, recentering, and guided calibration.
- Full-body IK and Hands Only presentation modes.
- Motion-controlled tools, physics-held items, dead bodies, TNT, brass knuckles, and hand-directed throwing.
- Physical rod casting presentation, two-hand reel grip, and physical reeling.
- One- and two-handed gun aiming, physical sights/scopes, muzzle-correct firing, and recoil handling.
- VR conversion for HUD, menus, dialogue, item markers, overlays, character customization, and pointer dragging.
- Underwater, death, damage, low-health, and UI-blur presentation adapted for VR.
- Multiplayer VR body, hand, foot, held-tool, and fishing-line pose synchronization.
- Optional VR/flat-screen startup while the mod remains installed.
- Streamlined VR settings with permanent right-hand primary controls and tested HUD/visual defaults.
- Separate GitHub release packaging with installation documentation; BepInEx remains an external requirement.
- Thunderstore-specific manifest, README, icon, dependency declaration, and package builder.
