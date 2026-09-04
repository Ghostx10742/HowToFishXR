using System;
using System.Linq;
using HarmonyLib;
using HowToFishVR.Input;
using HowToFishVR.Localization;
using HowToFishVR.VR;

namespace HowToFishVR;

/// <summary>
/// Orchestrates startup: create input actions, boot OpenXR, build the rig, apply Harmony patches,
/// register localization + settings. Called once from <see cref="Plugin.Awake"/>.
/// </summary>
public static class Entrypoint
{
    public static void Initialize()
    {
        VRActions.Initialize();

        if (Plugin.VRRequested)
        {
            Plugin.VREnabled = OpenXRBoot.Start();
        }

        // Localization tables are always registered so ModMenu labels translate even in flatscreen.
        try { Loc.Register(); }
        catch { }

        // Multiplayer VR pose sync: registered on EVERY modded client (VR or flatscreen). VR players
        // broadcast their full-body poses; every client with the mod (including flatscreen ones) applies
        // them to remote players so VR players' body/arms/feet are seen correctly. Vanilla hosts are
        // unaffected — the handshake ensures no pose spam reaches them. The receiving side is identical
        // in VR and flatscreen mode (MAVR-style): only the pose BROADCAST requires an active VR session.
        try
        {
            Net.VRNetSync.Create();
            Body.RemoteBodyDriver.Create();
            ApplyNetPatches();
        }
        catch (System.Exception ex) { Plugin.Logger.LogError($"VR networking failed to initialize: {ex}"); }

        if (Plugin.VREnabled)
        {
            VRRig.Create();
            VRCameraPoser.Create();
            VRDesktopMirror.Create(); // MAVR-style PC window mirror (the built-in XR mirror is black here)
            VRCalibration.Create();
            VRDamageOverlay.Create();
            UI.VRUIManager.Create();
            UI.VRKeyboard.Create();
            UI.VRLaser.Create();
            // No custom death overlay: the REAL game DeathUI canvas is shown as a head-locked overlay
            // (VRUIManager converts + head-locks it to the view camera), so the death screen renders like
            // flatscreen — real respawn prompt, give-up mask, darkening — not a custom mirror.
            UI.VRSettingsPanel.Create();
            VRHeldItem.Create();
            // Persistent per-frame body/IK self-heal monitor (FRIK/vhvr doctrine): survives every transition
            // (death, boat, new scene, joining a game) and rebuilds the restored body the moment the game
            // recreates the player, so the full-body IK can never be left broken by a missed event.
            Body.BodyLifecycle.Create();

            // Real UI blur, the SAFE way: read-only capture of the game's own blur texture into a persistent
            // RT, re-bound so the world-space panels sample a valid (not black) blur. Touches no pipeline
            // state, so unlike the old BlurFix (global injection-point change, now removed) it cannot black
            // the view. VRUIManager keeps the real blur material only while this reports a captured blur.
            VR.BlurCapture.Create();

            ApplyPatchesResiliently();
        }
    }

    /// <summary>
    /// Patch each Harmony class independently so one bad target (e.g. after a game update) logs a
    /// warning instead of disabling every VR patch. Classes marked <see cref="Patches.NetOnlyPatchAttribute"/>
    /// are applied separately (see <see cref="ApplyNetPatches"/>) so they run on flatscreen clients too.
    /// </summary>
    private static void ApplyPatchesResiliently()
    {
        var assembly = typeof(Plugin).Assembly;
        foreach (var type in assembly.GetTypes())
        {
            if (!type.GetCustomAttributes(typeof(HarmonyPatch), true).Any()) continue;
            if (type.GetCustomAttributes(typeof(Patches.NetOnlyPatchAttribute), true).Any()) continue;
            try
            {
                Plugin.HarmonyInstance.CreateClassProcessor(type).Patch();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Patch class '{type.Name}' failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Apply the networking patches on EVERY modded client regardless of <c>Plugin.VREnabled</c>, so a
    /// flatscreen player with the mod still sees VR players' full bodies. Each class is applied
    /// independently so one bad target only logs a warning.
    /// </summary>
    private static void ApplyNetPatches()
    {
        var assembly = typeof(Plugin).Assembly;
        foreach (var type in assembly.GetTypes())
        {
            if (!type.GetCustomAttributes(typeof(Patches.NetOnlyPatchAttribute), true).Any()) continue;
            try
            {
                Plugin.HarmonyInstance.CreateClassProcessor(type).Patch();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Net patch class '{type.Name}' failed: {ex.Message}");
            }
        }
    }
}
