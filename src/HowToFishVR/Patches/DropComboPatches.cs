using HarmonyLib;
using HowToFishVR.Input;
using HowToFishVR.VR;
using UnityEngine;
using UnityEngine.InputSystem;

namespace HowToFishVR.Patches;

/// <summary>
/// Drop/throw as a COMBO: hold the LEFT grip, then press B. B alone is switch-bait (tap); while the
/// off-hand grip is held, B becomes drop/throw (the game's own charge-throw — press starts charging,
/// release throws with the accumulated force). Both actions live on the same button, so each is gated
/// here so they can never fire together:
///  - PlayerHolding.DropInput only runs while the off-hand grip is held.
///  - PlayerInventory.ChangeBaitInput only runs while the off-hand grip is NOT held.
/// </summary>
[HarmonyPatch]
internal static class DropComboPatches
{
    private static bool OffHandGripHeld()
    {
        try
        {
            var a = VRActions.Instance;
            if (a == null) return false;
            return a.OffHandGrip >= VRConfig.GripThreshold.Value;
        }
        catch { return false; }
    }

    // Drop only while the off-hand grip is held (so a plain B tap never drops anything), and never
    // while two-handing a gun (the left grip is already held on the foregrip — an accidental B press
    // must not drop it).
    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlayerHolding), "DropInput")]
    private static bool GateDropInput()
    {
        if (!Plugin.VREnabled) return true;
        if (PlayerToolMovementPatches.GunTwoHand) return false;
        return OffHandGripHeld();
    }

    // Switch-bait only when the off-hand grip is NOT held (so the drop combo never also switches bait).
    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlayerInventory), "ChangeBaitInput")]
    private static bool GateChangeBait(InputAction.CallbackContext context)
    {
        if (!Plugin.VREnabled) return true;
        return !OffHandGripHeld();
    }

    // Throw AIM in VR: use the controller's corrected grip/pointer direction plus the XR runtime's release
    // velocity (including wrist-flick angular velocity at the item's centre). Pure controller.forward is
    // the GRIP axis, not the natural throwing/pointer axis, which is why hand aiming felt offset. A real
    // throwing motion increasingly steers the trajectory; a still hand remains predictable. The game's
    // charged throw magnitude is preserved. Tools keep their existing velocity-based throw behavior.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerHolding), "FinalDropForce")]
    private static void AimThrowWithHand(PlayerHolding __instance, ref Vector3 __result)
    {
        if (!Plugin.VREnabled) return;
        try
        {
            var held = __instance._heldItem;
            if (held != null && held.Tool != null) return; // tools already throw sensibly
            var rig = VRRig.Instance;
            if (rig == null || rig.MainHand == null) return;
            float mag = __result.magnitude;
            if (mag < 1e-3f) return;
            // Same 45-degree grip correction used by held tools: converts the OpenXR grip pose into the
            // natural forward axis of the object in the palm.
            Vector3 poseDirection = rig.MainHand.rotation * Quaternion.Euler(45f, 0f, 0f) * Vector3.forward;
            Vector3 releaseVelocity = Vector3.zero;
            bool haveVelocity = false;

            var node = VRConfig.Handedness.Value == DominantHand.Right
                ? UnityEngine.XR.XRNode.RightHand : UnityEngine.XR.XRNode.LeftHand;
            var device = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(node);
            if (device.isValid && device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.deviceVelocity, out var localVelocity))
            {
                releaseVelocity = rig.Root != null ? rig.Root.TransformDirection(localVelocity) : localVelocity;
                haveVelocity = true;
            }

            // A wrist flick contributes tangential velocity at the held item's centre of mass.
            if (device.isValid && device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.deviceAngularVelocity, out var localAngularVelocity))
            {
                Vector3 angularVelocity = rig.Root != null
                    ? rig.Root.TransformDirection(localAngularVelocity) : localAngularVelocity;
                Vector3 itemOffset = Vector3.zero;
                try
                {
                    if (held != null && held.Rig != null)
                        itemOffset = held.Rig.worldCenterOfMass - rig.MainHand.position;
                }
                catch { }
                releaseVelocity += Vector3.Cross(angularVelocity, itemOffset);
                haveVelocity = true;
            }

            Vector3 dir = poseDirection.normalized;
            float releaseSpeed = releaseVelocity.magnitude;
            if (haveVelocity && releaseSpeed > 0.20f)
            {
                Vector3 motionDirection = releaseVelocity / releaseSpeed;
                // Ignore a backwards pull at button release; otherwise blend up to 80% real motion.
                if (Vector3.Dot(motionDirection, dir) > -0.15f)
                {
                    float motionWeight = Mathf.InverseLerp(0.20f, 1.75f, releaseSpeed) * 0.80f;
                    dir = Vector3.Slerp(dir, motionDirection, motionWeight).normalized;
                }
            }

            if (dir.sqrMagnitude < 1e-6f) return;
            __result = dir * mag;
        }
        catch { }
    }
}
