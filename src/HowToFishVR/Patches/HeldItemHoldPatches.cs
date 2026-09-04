using HarmonyLib;
using HowToFishVR.Input;
using HowToFishVR.VR;
using UnityEngine;

namespace HowToFishVR.Patches;

/// <summary>
/// PHYSICS-BASED hold for fish/creatures (and other simple non-tool items), so they keep the game's own
/// flop physics — "we want physics because that's how the game works" — instead of being frozen kinematic
/// and teleported to the hand (which killed physics AND stretched jointed fish to oblivion).
///
/// The game already holds non-tool items with PHYSICS: <c>PlayerHolding.MoveItemToHoldPosRot</c> drives the
/// item's rigidbody VELOCITY toward <c>CalculateHoldTargetPos()</c> / <c>CalculateHoldTargetRot()</c> every
/// FixedUpdate (only while the item is non-kinematic). We simply REDIRECT those two targets to the VR hand,
/// so the game's own velocity solver carries the fish to your hand — smoothly, with its physics intact, and
/// with no teleport to rip the joints apart. VRHeldItem stops freezing/posing these items (see its Pose),
/// leaving them non-kinematic so this path runs. Dead bodies use this same stable one-hand-driven path;
/// the left hand may latch visually without taking control of the body. Tools and TNT keep their own handling.
/// </summary>
[HarmonyPatch(typeof(PlayerHolding))]
internal static class HeldItemHoldPatches
{
    /// <summary>True for the LOCAL player holding a fish/creature/simple item that should be physics-held at
    /// the hand. Tools and TNT (which has the independently-positioned off-hand lighter) are excluded.</summary>
    internal static bool ShouldHandHold(PlayerHolding h)
    {
        if (!Plugin.VREnabled || !Plugin.HeadsetActive) return false;
        try
        {
            if (h._player == null || h._player.Owner == null || !h._player.Owner.IsLocalClient) return false;
            if (Boat.IsDrivingLocally) return false;
            var held = h._heldItem;
            if (held == null || held.Tool != null) return false;
            if (held is Explosive) return false;
            return true;
        }
        catch { return false; }
    }

    [HarmonyPostfix]
    [HarmonyPatch("CalculateHoldTargetPos")]
    private static void HandHoldPos(PlayerHolding __instance, ref Vector3 __result)
    {
        if (!ShouldHandHold(__instance)) return;
        if (TryGetHandHoldTarget(__instance, out var position, out _)) __result = position;
    }

    [HarmonyPostfix]
    [HarmonyPatch("CalculateHoldTargetRot")]
    private static void HandHoldRot(PlayerHolding __instance, ref Quaternion __result)
    {
        if (!ShouldHandHold(__instance)) return;
        if (TryGetHandHoldTarget(__instance, out _, out var rotation)) __result = rotation;
    }

    /// <summary>Build a rigidbody target that puts the item's authored main-hand grip exactly on the
    /// controller. The game approaches this pose with velocity, preserving fish/ragdoll secondary physics.</summary>
    private static bool TryGetHandHoldTarget(PlayerHolding h, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = Quaternion.identity;
        try
        {
            var rig = VRRig.Instance;
            var held = h != null ? h._heldItem : null;
            if (rig == null || rig.MainHand == null || held == null || held.transform == null) return false;

            Quaternion gripOffset = PlayerHandsPatches.RightGripOffset;
            Quaternion desiredHandRotation = rig.MainHand.rotation * gripOffset;
            var handPose = held.HandTransformsRight;

            if (handPose != null && handPose.Exists)
            {
                Transform parent = handPose.Parent != null ? handPose.Parent : held.transform;
                Vector3 gripWorldPosition = parent.TransformPoint(handPose.HandPos);
                Quaternion gripWorldRotation = parent.rotation * handPose.HandRot;
                Vector3 gripLocalPosition = held.transform.InverseTransformPoint(gripWorldPosition);
                Quaternion gripLocalRotation = Quaternion.Inverse(held.transform.rotation) * gripWorldRotation;

                rotation = desiredHandRotation * Quaternion.Inverse(gripLocalRotation);
                position = rig.MainHand.position - rotation * gripLocalPosition;
                return true;
            }

            rotation = desiredHandRotation;
            position = rig.MainHand.position;
            return true;
        }
        catch { return false; }
    }
}
