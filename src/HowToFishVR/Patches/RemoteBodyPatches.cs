using System;
using System.Collections.Generic;
using FishNet;
using HarmonyLib;
using HowToFishVR.Net;
using UnityEngine;

namespace HowToFishVR.Patches;

/// <summary>Marks Harmony patch classes that must run on EVERY modded client (VR or flatscreen), so they
/// are applied outside the VR-enabled gate (<see cref="Entrypoint.ApplyNetPatches"/>). The main VR-only
/// patch scan skips classes marked with this.</summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class NetOnlyPatchAttribute : Attribute { }

/// <summary>
/// Full-body receive side of VR pose syncing. The base game only replicates the player ROOT position
/// plus the camera's raw local yaw/pitch, then re-animates the remote body procedurally (PlayerBody /
/// PlayerLegs) — so snap/smooth-turned VR players face the wrong way and their feet never match. This
/// patch pins the remote body to the broadcast full-body pose:
///   - <c>OtherPlayer.ApplyReceivedPosRot</c> prefix: for VR players, replaces the game's rotation
///     handling with the broadcast body yaw (the view yaw incl. snap/smooth turns) and adds the
///     broadcast body offset to the position lerp. Head pitch stays game-synced; the remote head yaw
///     follows the body yaw automatically (PlayerBody animates the head from the body rotation).
/// No-ops for vanilla/flatscreen players (no pose) and while dead or on a boat.
/// </summary>
[HarmonyPatch(typeof(OtherPlayer))]
[NetOnlyPatch]
internal static class RemoteOtherPlayerPatches
{
    [HarmonyPrefix]
    [HarmonyPatch("ApplyReceivedPosRot")]
    private static bool ApplyReceivedPosRotPrefix(OtherPlayer __instance)
    {
        if (!VRConfig.MultiplayerVRSync.Value) return true;
        try
        {
            var p = __instance._player;
            if (p == null) return true;
            if (p.Owner == null || p.Owner.IsLocalClient) return true;
            if (p == Player.LocalPlayer) return true;
            if (p.Dying != null && p.Dying.IsDead) return true;
            // The game tracks a REMOTE player's boat state in OtherPlayer.OnBoat (set by the
            // SetReceivedPosRot onBoat flag) — PlayerMovement.OnBoat is only ever set for the LOCAL
            // player (BoatTrigger). Gating on Movement.OnBoat alone never fired for remote players, so
            // the mod kept pose-pinning remote boat passengers/drivers: it added a WORLD body offset to
            // the boat-LOCAL received position, throwing the body to a garbage spot (the "invisible on
            // the boat" bug). Skip boat players on the remote flag and let the game's own boat-relative
            // replication render them.
            if ((p.Other != null && p.Other.OnBoat) || (p.Movement != null && p.Movement.OnBoat)) return true;
            if (p.NetworkObject == null) return true;
            if (!VRNetSync.TryGetInterpolatedPose(p.NetworkObject.ObjectId, out var pose)) return true;

            // SNAPSHOT-INTERPOLATED body root: BodyPos is now the absolute body-root world position,
            // interpolated between buffered snapshots (VRNetSync), so we set it DIRECTLY — the interpolation
            // IS the smoothing. This removes the exponential trailing lag that made the sync "feel slow"
            // (RemoteBodyDriver also drives it every frame so it stays smooth between network callbacks).
            // Head pitch is the game's own replicated value (not in our buffer), so it keeps a light lerp.
            __instance._transform.position = pose.BodyPos;
            __instance._transform.rotation = Quaternion.Euler(0f, pose.BodyYaw, 0f);
            float pk = 1f - Mathf.Exp(-25f * Time.deltaTime);
            __instance._camProxy.localRotation = Quaternion.Slerp(__instance._camProxy.localRotation, Quaternion.Euler(__instance._receivedRot.x, 0f, 0f), pk);
            return false; // skip the game's version — we handled position + rotation + head pitch
        }
        catch { return true; }
    }

}

/// <summary>
/// Drives REMOTE VR players' hand bones at the exact moment the game would (inside
/// <c>PlayerHands.LateUpdate</c>), so the arm IK (<c>PlayerArms.SetIKTarget</c> -> IK.LateUpdate) solves
/// toward OUR poses in the same frame.
///
/// Why this exists: the previous driver ran in a late LateUpdate (order 500) — AFTER the remote
/// player's own arm IK had already solved toward the game-animated hand positions. The hands were then
/// teleported to the VR poses after the fact, so while the HANDS sat in the right spot the ARMS/elbows
/// stayed bent toward the old positions — which read as "not synced when alive" (and only looked right
/// in the specific states where the game's own hand animation wasn't fighting). Running inside the
/// game's own LateUpdate (prefix skips the game's hand placement for driven remotes; postfix writes the
/// VR poses) puts the poses in place BEFORE SetIKTarget/IK.LateUpdate, so the whole arm bends to reach
/// the VR hands.
/// </summary>
[HarmonyPatch(typeof(PlayerHands))]
[NetOnlyPatch]
internal static class RemoteHandsPatches
{
    [HarmonyPrefix]
    [HarmonyPatch("LateUpdate")]
    private static bool SkipGameHands(PlayerHands __instance)
    {
        // Remote VR player with a live pose: skip the game's own hand animation so its values never
        // overwrite our pose-driven placement (the postfix writes ours immediately after).
        return !IsDrivenRemote(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch("LateUpdate")]
    private static void DriveHands(PlayerHands __instance)
    {
        if (!IsDrivenRemote(__instance)) return;
        try
        {
            var p = __instance._player;
            if (p == null || p.NetworkObject == null) return;
            if (__instance.HandBoneRight == null || __instance.HandBoneLeft == null) return;
            if (!VRNetSync.TryGetInterpolatedPose(p.NetworkObject.ObjectId, out var pose)) return;

            // Compose against the INTERPOLATED absolute body root (pose.BodyPos) — the same frame the sender
            // composed against — so hands land exactly right and move smoothly with the interpolated body.
            var frameRot = Quaternion.Euler(0f, pose.BodyYaw, 0f);
            var framePos = pose.BodyPos;

            __instance.HandBoneRight.SetPositionAndRotation(
                framePos + frameRot * pose.RightPos, frameRot * pose.RightRot);
            __instance.HandBoneLeft.SetPositionAndRotation(
                framePos + frameRot * pose.LeftPos, frameRot * pose.LeftRot);

            // Re-point the arm IK at the hand bones NOW (the same call the game makes at the end of its
            // own LateUpdate), so the IK solve this frame bends the arms to reach our hands.
            if (p.Arms != null) p.Arms.SetIKTarget(__instance.HandBoneRight, __instance.HandBoneLeft);
        }
        catch { }
    }

    private static bool IsDrivenRemote(PlayerHands hands)
    {
        if (!VRConfig.MultiplayerVRSync.Value) return false;
        try
        {
            var p = hands._player;
            if (p == null || p.Owner == null || p.Owner.IsLocalClient) return false;
            if (p == Player.LocalPlayer) return false;
            if (p.Dying != null && p.Dying.IsDead) return false;
            // Remote boat state lives on OtherPlayer.OnBoat (see ApplyReceivedPosRotPrefix) — check it
            // so boat passengers/drivers are left to the game's own hand animation instead of being
            // pose-driven to a wrong spot.
            if ((p.Other != null && p.Other.OnBoat) || (p.Movement != null && p.Movement.OnBoat)) return false;
            if (p.NetworkObject == null) return false;
            return VRNetSync.TryGetPose(p.NetworkObject.ObjectId, out _);
        }
        catch { return false; }
    }
}

/// <summary>
/// Pins the remote VR player's FEET to the broadcast poses. The game's PlayerLegs recomputes its own
/// procedural footsteps from the synced root velocity every frame; for VR players we override its foot
/// IK targets, IK poles and foot model rotations with the broadcast values (smoothed at the pose rate so
/// they glide instead of stepping). Runs as a postfix on <c>PlayerLegs.Update</c> so the leg IK solvers
/// (IK.cs, LateUpdate) solve toward OUR targets in the same frame — zero lag.
/// </summary>
[HarmonyPatch(typeof(PlayerLegs))]
[NetOnlyPatch]
internal static class RemoteLegsPatches
{
    [HarmonyPostfix]
    [HarmonyPatch("Update")]
    private static void LegsPostfix(PlayerLegs __instance)
    {
        if (!VRConfig.MultiplayerVRSync.Value) return;
        try
        {
            var p = __instance._player;
            if (p == null || p.Owner == null || p.Owner.IsLocalClient) return;
            if (p == Player.LocalPlayer) return;
            if (p.Dying != null && p.Dying.IsDead) return;
            // Remote boat state lives on OtherPlayer.OnBoat (see ApplyReceivedPosRotPrefix) — skip
            // boat players so the game's own leg animation (boat position space) is used.
            if ((p.Other != null && p.Other.OnBoat) || (p.Movement != null && p.Movement.OnBoat)) return;
            if (p.NetworkObject == null) return;
            if (!VRNetSync.TryGetInterpolatedPose(p.NetworkObject.ObjectId, out var pose)) return;
            if (!pose.HasBody) return;

            var legs = __instance;
            if (legs._legTargets == null || legs._legTargets.Length < 2 ||
                legs._ikPoles == null || legs._ikPoles.Length < 2 ||
                legs._footModels == null || legs._footModels.Length < 2) return;
            if (legs._legTargets[0] == null || legs._legTargets[1] == null ||
                legs._ikPoles[0] == null || legs._ikPoles[1] == null ||
                legs._footModels[0] == null || legs._footModels[1] == null) return;

            // Compose against the INTERPOLATED absolute body root (pose.BodyPos). The interpolation is the
            // smoothing, so feet are applied DIRECTLY — no separate per-leg exponential filter (that was
            // the old trailing lag).
            var frameRot = Quaternion.Euler(0f, pose.BodyYaw, 0f);
            var framePos = pose.BodyPos;
            for (int i = 0; i < 2; i++)
            {
                Vector3 pos = i == 0 ? pose.FootLPos : pose.FootRPos;
                Quaternion rot = i == 0 ? pose.FootLRot : pose.FootRRot;
                Vector3 pole = i == 0 ? pose.FootLPole : pose.FootRPole;
                legs._legTargets[i].position = framePos + frameRot * pos;
                legs._ikPoles[i].position = framePos + frameRot * pole;
                legs._footModels[i].rotation = frameRot * rot;
            }
        }
        catch { }
    }
}

/// <summary>
/// When a dead player's ragdoll is CARRIED by another player, the vanilla death cam (OrbitCamera)
/// follows the replicated ragdoll position every frame. The carried body arrives in network ticks, so
/// the camera snaps after it and reads as a violent shake ("picking up a dead player shakes the
/// camera"). This postfix eases the orbit ANCHOR toward the body while it is held, so the view glides
/// with the carrier instead of shaking, and restores the raw follow the moment the body is released.
/// Runs on every modded client (the dead player may be flatscreen), and only fires for the local dead
/// player — VR clients skip OrbitCamera entirely (PlayerDeathCamPatches.SkipOrbit), so VR death views
/// are unaffected.
/// </summary>
[HarmonyPatch(typeof(PlayerDeathCam))]
[NetOnlyPatch]
internal static class DeathCamCarryPatches
{
    private static Vector3 _carryAnchor;
    private static bool _carryAnchorInit;

    [HarmonyPostfix]
    [HarmonyPatch("OrbitCamera")]
    private static void SmoothCarriedAnchor(PlayerDeathCam __instance)
    {
        try
        {
            var p = __instance._player;
            if (p == null || !p.Owner.IsLocalClient) return;
            var dying = p.Dying;
            if (dying == null || !dying.IsDead || dying.DeadPlayer == null) { _carryAnchorInit = false; return; }
            var dp = dying.DeadPlayer;
            // Only while the body is actually held by another player (SyncedHolder is replicated).
            if (dp.SyncedHolder == null || dp.SyncedHolder == p) { _carryAnchorInit = false; return; }
            var cam = __instance._deathCam;
            if (cam == null) return;

            Vector3 bodyPos = dp.transform.position;
            if (!_carryAnchorInit) { _carryAnchor = bodyPos; _carryAnchorInit = true; }
            float k = 1f - Mathf.Exp(-10f * Time.deltaTime);
            _carryAnchor = Vector3.Lerp(_carryAnchor, bodyPos, k);
            // Keep the orbit offset the game just computed (look input stays fully responsive); only
            // the anchor translation is smoothed.
            Vector3 delta = cam.transform.position - bodyPos;
            cam.transform.position = _carryAnchor + delta;
        }
        catch { }
    }
}
