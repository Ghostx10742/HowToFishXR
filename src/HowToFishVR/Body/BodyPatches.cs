using System;
using System.Collections.Generic;
using HarmonyLib;
using HowToFishVR.VR;
using UnityEngine;

namespace HowToFishVR.Body;

/// <summary>
/// Harmony patches that keep the restored local body alive and consistent (SeaLegs-style, reworked):
///  - Save the body objects before the game's local-only destroy pass, then rebuild them after.
///  - Hide the body while dead, show it again on resurrect, forget it on disconnect.
///  - Suppress the doubled footstep/splash audio the restored body would produce.
/// </summary>
[HarmonyPatch]
internal static class BodyPatches
{
    private static List<GameObject> _stashed;

    /// <summary>
    /// Before the destroy pass: steal the list of objects the game is about to destroy. This runs on
    /// EVERY local spawn regardless of Body Mode — in Hands Only the body objects stay alive (inactive,
    /// the base-game look) instead of being destroyed, so toggling Full Body back on can always rebuild
    /// them. The old mode-gate here is exactly why the body "stayed in hands only": a respawn/scene
    /// change while Hands Only was active destroyed the body and left the mod holding dead references.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), "InitializePlayer")]
    private static void SaveBodyPrefix(Player __instance)
    {
        _stashed = null;
        if (!Plugin.VREnabled) return;
        // Only the owner's own player is stripped.
        if (__instance.Owner.IsLocalClient && __instance._otherObjects != null)
        {
            _stashed = __instance._otherObjects;
            __instance._otherObjects = new List<GameObject>();
        }
    }

    /// <summary>After init: put the list back, remember the objects for any later toggle, and build the
    /// local body when Full Body mode is active.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), "InitializePlayer")]
    private static void SaveBodyPostfix(Player __instance)
    {
        if (_stashed == null) return;
        var stashed = _stashed;
        _stashed = null;
        __instance._otherObjects = stashed;
        // Always remember (Hands Only spawns keep the objects for a later Full Body toggle).
        HowToFishBody.Store(__instance, stashed);
        if (Plugin.VREnabled && FullBodyActive())
            HowToFishBody.Build(__instance, stashed);

        // FRESH PLAYER INIT (joined a new game / a new scene loaded): the roomscale walk offset
        // accumulated in the PREVIOUS scene is meaningless at the new origin, so the view/body would spawn
        // displaced and never heal. Reset the roomscale anchor (calibration/height persist) exactly like a
        // respawn, and re-assert the arm IK so it binds to the freshly-built body.
        if (Plugin.VREnabled && __instance.Owner.IsLocalClient)
        {
            try { VRRig.Instance?.OnRespawn(); } catch { }
            if (FullBodyActive()) { try { ArmRig.Setup(__instance); } catch { } }
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerDying), "DeathEffects")]
    private static void HideOnDeath(PlayerDying __instance)
    {
        if (Plugin.VREnabled && FullBodyActive() && __instance._player == Player.LocalPlayer)
            HowToFishBody.SetVisible(false);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerDying), "ResurrectEffect")]
    private static void ShowOnResurrect(PlayerDying __instance)
    {
        if (!Plugin.VREnabled || __instance._player != Player.LocalPlayer) return;
        if (FullBodyActive()) HowToFishBody.SetVisible(true);
        // The game teleports the body to the respawn point — reset the accumulated roomscale walk
        // offset so the view/body align instantly (calibration itself is kept; see VRRig.OnRespawn).
        var rig = VRRig.Instance;
        if (rig != null) rig.OnRespawn();
        // REPOSITIONING FIX: after dying/respawning (or being revived) the game re-teleports the body,
        // and the full-body IK / arm solvers can be left pointing at stale targets — the body drifts,
        // collisions feel off, arms bend wrong. Re-assert everything now: re-point the arm IK at the
        // (possibly new) player's hand bones, re-sync the body offset/yaw to the view, and re-apply the
        // body mode so the visible body, height and collider all line back up 1:1 with before the death.
        try { ArmRig.Setup(__instance._player); } catch { }
        try { var drv = __instance._player.GetComponent<LocalBodyDriver>(); if (drv != null) drv.Sync(); } catch { }
        try { HowToFishBody.ApplyBodyMode(); } catch { }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), "OnStopClient")]
    private static void ForgetOnDisconnect(Player __instance)
    {
        // Always clear on disconnect — stale refs to a destroyed player are what left the body
        // unrecoverable when toggling Full Body back on after a scene change in Hands Only.
        if (Plugin.VREnabled && __instance == Player.LocalPlayer)
            HowToFishBody.Clear();
    }

    // NOTE: NO audio is dropped in Full Body — the restored body's footsteps, splashes, take-damage,
    // fish-bite, rod-cast, melee-swing and sell sounds all play, matching Hands Only exactly ("all the
    // audio I'm supposed to hear"). Old gates here dropped PlayRandomPlayerClip / WaterCheck for the
    // local player in Full Body and silenced those sounds — removed.

    /// <summary>Body systems only run when the user picked Full Body IK mode.</summary>
    private static bool FullBodyActive()
    {
        return VRConfig.BodyMode.Value == BodyMode.FullBodyIK;
    }
}

/// <summary>
/// Snap-turn companion to the real body turn (VRRig rotates the player root, LCVR/Pratfall style): the
/// game rotates the visible torso/head with a SPRING (PlayerBody._swayRot springs toward CurPlayerRot,
/// _headRot toward the camera yaw), so a 45° snap would leave the torso swinging behind the view. On
/// the exact frame a snap fires, jump both springs straight to the post-snap view yaw so the whole body
/// rotates with the view, then let the springs hold it there.
/// </summary>
[HarmonyPatch(typeof(PlayerBody))]
internal static class PlayerBodyPatches
{
    [HarmonyPostfix]
    [HarmonyPatch("LateUpdate")]
    private static void SnapBodyWithTurn(PlayerBody __instance)
    {
        if (!Plugin.VREnabled) return;
        var rig = VRRig.Instance;
        if (rig == null || !rig.SnapTurned) return;
        try
        {
            if (__instance._player == null || __instance._player != Player.LocalPlayer) return;
            float yaw = rig.SnapTurnYaw;
            // Pitch from the rig head (the true current pose) — the game camera isn't re-posed until
            // onBeforeRender, so reading it here would use the previous frame's rotation.
            float pitch = rig.Head != null ? rig.Head.localRotation.eulerAngles.x : 0f;
            __instance._swayRot = Quaternion.Euler(0f, yaw, 0f) * __instance._bendRot;
            __instance._headRot = Quaternion.Euler(pitch, yaw, 0f);
            __instance._swayAngVel = Vector3.zero; // kill the spring's catch-up so it doesn't oscillate
            __instance._headAngVel = Vector3.zero;
            if (__instance._lowerBody != null) __instance._lowerBody.rotation = __instance._swayRot;
            if (__instance._head != null) __instance._head.rotation = __instance._headRot;
        }
        catch { }
    }
}
