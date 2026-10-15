using HarmonyLib;
using HowToFishVR.VR;
using System.Reflection;
using UnityEngine;

namespace HowToFishVR.Patches;

/// <summary>
/// Drives the hand bones from the motion controllers. The game positions <c>_handBoneRight/Left</c>
/// in <c>PlayerHands.LateUpdate</c>; we postfix it to snap the bones to the controller poses. Arm IK
/// (<c>PlayerArms → IK</c>) then solves toward the bones, so the visible arms follow the controllers.
/// </summary>
[HarmonyPatch(typeof(PlayerHands))]
internal static class PlayerHandsPatches
{
    // Controller-pose -> hand-bone orientation offset (tuned so the RIGHT hand is correct).
    // G = Rx(-25) * Rx(180)*Ry(90) * Rz(180).
    internal static readonly Quaternion RightGripOffset = Quaternion.Euler(-25f, 0f, 0f) * Quaternion.Euler(180f, 90f, 0f) * Quaternion.Euler(0f, 180f, 0f);
    // The controller pose mirrors across world X, but the game's authored left-hand mesh mirrors the
    // right-hand finger/thumb geometry across the HAND BONE'S local Z (confirmed by the live skeleton
    // dump: every left/right finger root has equal X/Y and opposite Z). After the sagittal controller
    // reflection, bridge those two mirror planes with a 180-degree roll around local Y -- the hand's
    // wrist-to-fingertips axis. This turns the left palm inward without reversing its pointing axis.
    internal static readonly Quaternion LeftGripOffset =
        MirrorAcrossSagittalPlane(RightGripOffset) * Quaternion.Euler(0f, 180f, 0f);

    // Permanent controller-local position calibration, captured from the user's finalized in-headset
    // settings. X is outward and is mirrored for the left hand; Y/Z remain identical.
    private static readonly Vector3 RightHandPositionOffset = new(0.02260734f, 0.02081691f, 0.001267433f);
    private static readonly Vector3 LeftHandPositionOffset = new(-0.02260734f, 0.02081691f, 0.001267433f);

    /// <summary>Apply the user's permanent controller-local hand-position calibration. Gun placement
    /// uses this same point so equipping a weapon cannot silently fall back to the raw controller.</summary>
    internal static Vector3 CalibratedHandPosition(Transform controller, bool isLeft)
    {
        if (controller == null) return Vector3.zero;
        Vector3 offset = isLeft ? LeftHandPositionOffset : RightHandPositionOffset;
        return controller.position + controller.rotation * offset;
    }

    /// <summary>
    /// The wrist basis for the hand currently designated as the gameplay main hand. Item prefabs store
    /// their primary grip as the authored right-hand pose, but that pose must be aligned to the physical
    /// left wrist in left-handed mode. Using the right-hand basis on a left controller rotates its local
    /// grip offset sideways, which leaves items hovering beside the palm.
    /// </summary>
    internal static Quaternion MainHandGripRotation(Transform controller)
    {
        if (controller == null) return Quaternion.identity;
        return controller.rotation * (VRConfig.IsLeftHanded ? LeftGripOffset : RightGripOffset);
    }

    internal static Quaternion MirrorAcrossLocalZ(Quaternion q)
    {
        return Quaternion.Normalize(new Quaternion(-q.x, -q.y, q.z, q.w));
    }

    private static readonly FieldInfo RightFingersField = AccessTools.Field(typeof(PlayerHands), "_fingerTransformsRight");
    private static readonly FieldInfo LeftFingersField = AccessTools.Field(typeof(PlayerHands), "_fingerTransformsLeft");
    private static readonly FieldInfo RightOpenPoseField = AccessTools.Field(typeof(PlayerHands), "_handTransformsOrigRight");
    private static readonly FieldInfo LeftOpenPoseField = AccessTools.Field(typeof(PlayerHands), "_handTransformsOrigLeft");

    private static void SetFingers(Transform[] bones, Quaternion[] pose)
    {
        if (bones == null || pose == null || pose.Length < bones.Length) return;
        for (int i = 0; i < bones.Length; i++)
            if (bones[i] != null) bones[i].localRotation = pose[i];
    }

    private static void MirrorFingers(Transform[] targetBones, Quaternion[] sourcePose)
    {
        if (targetBones == null || sourcePose == null || sourcePose.Length < targetBones.Length) return;
        for (int i = 0; i < targetBones.Length; i++)
        {
            if (targetBones[i] == null) continue;
            targetBones[i].localRotation = MirrorAcrossLocalZ(sourcePose[i]);
        }
    }

    private static bool MirrorAnimatedFingers(Transform marker, Transform[] targetBones)
    {
        if (marker == null || targetBones == null || marker.childCount * 3 != targetBones.Length) return false;
        for (int finger = 0; finger < marker.childCount; finger++)
        {
            Transform bone = marker.GetChild(finger);
            for (int joint = 0; joint < 3; joint++)
            {
                if (bone == null || targetBones[finger * 3 + joint] == null) return false;
                targetBones[finger * 3 + joint].localRotation = MirrorAcrossLocalZ(bone.localRotation);
                bone = bone.childCount > 0 ? bone.GetChild(0) : null;
            }
        }
        return true;
    }

    private static void PoseLeftHandedFingers(PlayerHands hands, Item held)
    {
        if (!VRConfig.IsLeftHanded || held == null) return;
        try
        {
            var rightBones = (Transform[])RightFingersField.GetValue(hands);
            var leftBones = (Transform[])LeftFingersField.GetValue(hands);
            var rightOpen = ((HandTransforms)RightOpenPoseField.GetValue(hands))?.FingerRots;
            var leftOpen = ((HandTransforms)LeftOpenPoseField.GetValue(hands))?.FingerRots;
            var rightGrip = held != null ? held.HandTransformsRight : null;
            var leftGrip = held != null ? held.HandTransformsLeft : null;

            // Item prefabs animate the anatomical right fingers even when the left controller is
            // the main hand. Transfer the authored curl relative to each hand's own open pose.
            bool liveMain = held != null && held.Tool != null &&
                MirrorAnimatedFingers(held.HandModelRight, leftBones);
            if (!liveMain && rightGrip != null && rightGrip.Exists)
                MirrorFingers(leftBones, rightGrip.FingerRots);
            else if (!liveMain) SetFingers(leftBones, leftOpen);

            // The physical right hand is free unless it has actually latched to a support grip.
            bool supporting = PlayerToolMovementPatches.OffHandGripTarget != null ||
                VRHeldItem.FishTwoHand || held is Explosive || (held is Melee melee && melee._useBothHands);
            bool liveOff = supporting && held != null && held.Tool != null &&
                MirrorAnimatedFingers(held.HandModelLeft, rightBones);
            if (!liveOff && supporting && leftGrip != null && leftGrip.Exists)
                MirrorFingers(rightBones, leftGrip.FingerRots);
            else if (!liveOff) SetFingers(rightBones, rightOpen);
        }
        catch { }
    }

    private static Quaternion MirrorAcrossSagittalPlane(Quaternion q)
    {
        var mirrored = new Quaternion(q.x, -q.y, -q.z, q.w);
        return Quaternion.Normalize(mirrored);
    }

    // Revive-slap latch state: the slap hand's position captured when the revive starts, so its MOTION
    // can be layered onto the body's grip (see PlaceHands).
    private static bool _slapRestInit;
    private static Vector3 _slapRestPos;


    [HarmonyPostfix]
    [HarmonyPatch("LateUpdate")]
    private static void PlaceHands(PlayerHands __instance) => ApplyTrackedHands(__instance);

    /// <summary>Place the local visible hands from the latest controller poses. Besides the normal
    /// <c>PlayerHands.LateUpdate</c> postfix, the camera poser calls this once more immediately before
    /// rendering: OpenXR supplies a newer predicted pose there, so the hand targets and arm IK must be
    /// advanced with the camera instead of remaining one normal-frame sample behind it.</summary>
    internal static void ApplyTrackedHands(PlayerHands __instance)
    {
        if (!Plugin.HeadsetActive) return; // no LIVE headset right now (VR-chosen session with the HMD off/on a desk) -> leave the body alone
        // LOCAL PLAYER ONLY. This patch runs on EVERY player's PlayerHands — remote players' hand bones
        // must keep following their own animator, and snapping them to OUR controllers is exactly how
        // other people's hands ended up glued to our rig in multiplayer.
        try { if (__instance._player == null || __instance._player.Owner == null || !__instance._player.Owner.IsLocalClient) return; } catch { return; }
        // While driving the boat, let the game place the hands on the controls (resumes automatically when
        // you get off).
        try { if (Boat.IsDrivingLocally) return; } catch { }
        var rig = VRRig.Instance;
        if (rig == null || rig.LeftHand == null) return;

        // If nothing is held, make sure no STALE grip target from a previous frame keeps the hand frozen
        // mid-air: GlueToCamera stops being called once the tool is dropped, so the tool patch can't clear
        // its own targets anymore — clear them here instead.
        bool holdsTool = false;
        try { holdsTool = __instance._player != null && __instance._player.Holding != null && __instance._player.Holding.HeldItem != null; } catch { }
        if (!holdsTool) PlayerToolMovementPatches.ClearGripTargets();

        var rb = __instance.HandBoneRight;
        var lb = __instance.HandBoneLeft;

        // ---- Permanent palm-to-controller alignment ----
        // The OpenXR grip pose is the controller/palm origin while the game's hand bone is the wrist.
        // Apply the finalized in-headset calibration in controller-local space. Position uses the same
        // forward/up offset on both hands and mirrors only the outward axis; orientation is handled by
        // the exact sagittal reflection above.
        if (rb != null)
        {
            rb.position = CalibratedHandPosition(rig.RightHand, isLeft: false);
            rb.rotation = rig.RightHand.rotation * RightGripOffset;
        }
        if (lb != null)
        {
            lb.position = CalibratedHandPosition(rig.LeftHand, isLeft: true);
            lb.rotation = rig.LeftHand.rotation * LeftGripOffset;
        }

        // Two-handing a gun: the OFF hand stops tracking the controller and snaps to the gun's actual grip
        // point, so the support hand sits on the gun correctly (its default grab pose).
        var gripTarget = PlayerToolMovementPatches.OffHandGripTarget;
        if (gripTarget != null)
        {
            var offBone = VRConfig.IsLeftHanded ? rb : lb;
            if (offBone != null) offBone.SetPositionAndRotation(gripTarget.position, HeldItemMirror.HandRotation(gripTarget));
        }

        // The MAIN hand snaps to the gun's main grip (one-hand AND two-hand). While two-handing, the off
        // hand steers the gun's rotation, so the gun no longer follows the main controller's rotation — the
        // main hand must conform to the gun's grip (position AND orientation) or it floats off the weapon;
        // in one-hand the same latch keeps the hand sitting ON the grip.
        var mainGripTarget = PlayerToolMovementPatches.MainHandGripTarget;
        if (mainGripTarget != null)
        {
            // Only latch while the grip is actually live — a dropped/holstered tool's grip model is
            // inactive, and snapping to a dead grip is what froze the hand after letting go of the gun.
            if (!mainGripTarget.gameObject.activeInHierarchy)
            {
                PlayerToolMovementPatches.ClearGripTargets();
            }
            else
            {
                var mainBone = VRConfig.IsLeftHanded ? lb : rb;
                // In left-handed mode FollowHand already rotated the authored grip onto the left
                // wrist. Applying another handedness correction here would twist the visible hand.
                if (mainBone != null)
                {
                    mainBone.SetPositionAndRotation(mainGripTarget.position, HeldItemMirror.HandRotation(mainGripTarget));
                }
            }
        }

        // Physics-held non-tool items: latch the hand bones' POSITION to the item's OWN
        // authored hand poses (the game's own hold math: Parent.TransformPoint(HandPos)) — the ROTATION
        // stays controller-driven (set above), so the hand keeps its natural orientation while sitting on
        // the item. The game physics solver moves the item from the main/right hand target. A two-hand
        // latch never moves or rotates the item: it only pins the rendered left hand to the authored left
        // grip. The main hand remains visually seated at its authored grip as well.
        try
        {
            var held = __instance._player != null && __instance._player.Holding != null
                ? __instance._player.Holding.HeldItem : null;
            if (held != null && held.Tool == null)
            {
                // REVIVE/SLAP (two-hand): while two-hand-gripping a dead body AND reviving it, the OFF
                // hand stays LATCHED to the body's own authored grip (so it doesn't fly off the body),
                // and the game's slap-hand animation (_slapHandAnim — the animated hand doing the
                // slapping on the body) is layered on top as a MOTION OFFSET: the hand stays glued to
                // the body's left grip while visibly slapping at that same spot. The body itself stays
                // latched to the main hand (kinematic, VRHeldItem) — it does NOT move. Single-grip
                // revive is untouched: the game just plays the sounds + revive.
                if (VRHeldItem.FishTwoHand && held is DeadPlayer dp && dp._slapHandAnim != null &&
                    dp._isResurrecting && dp._slapHandAnim.transform != null)
                {
                    const bool offIsLeft = true;
                    var offBone = VRConfig.IsLeftHanded ? rb : lb;
                    var slapT = dp._slapHandAnim.transform;
                    if (offBone != null && slapT != null)
                    {
                        // Capture the slap hand's position once when the revive starts — its MOTION from
                        // there is layered onto the latched grip, so the hand doesn't jump to wherever
                        // the animation happened to be.
                        if (!_slapRestInit) { _slapRestPos = slapT.position; _slapRestInit = true; }
                        Vector3 bodyGrip = Vector3.zero;
                        bool haveGrip = false;
                        try
                        {
                            var ht = offIsLeft ? held.HandTransformsLeft : held.HandTransformsRight;
                            if (ht != null && ht.Parent != null)
                            {
                                bodyGrip = ht.Parent.TransformPoint(ht.HandPos);
                                haveGrip = true;
                            }
                        }
                        catch { }
                        if (haveGrip)
                        {
                            // Latched to the body's grip + the slap animation's motion (clamped so a huge
                            // animation jump can't drag the hand off the body).
                            Vector3 motion = slapT.position - _slapRestPos;
                            if (motion.magnitude > 0.35f) motion = motion.normalized * 0.35f;
                            offBone.SetPositionAndRotation(bodyGrip + motion, HeldItemMirror.HandRotation(slapT));
                        }
                        else
                        {
                            // No authored left grip on the body: fall back to riding the slap hand.
                            offBone.SetPositionAndRotation(slapT.position, HeldItemMirror.HandRotation(slapT));
                        }
                    }
                }
                else if (VRHeldItem.FishTwoHand)
                {
                    _slapRestInit = false; // revive ended / not reviving -> re-arm the slap-motion capture
                    var mainBone = VRConfig.IsLeftHanded ? lb : rb;
                    var offBone = VRConfig.IsLeftHanded ? rb : lb;
                    var mainPose = held.HandTransformsRight;
                    var offPose = held.HandTransformsLeft;
                    if (mainBone != null && mainPose != null && mainPose.Exists && mainPose.Parent != null)
                        mainBone.position = mainPose.Parent.TransformPoint(mainPose.HandPos);
                    if (offBone != null && offPose != null && offPose.Exists && offPose.Parent != null)
                        offBone.position = offPose.Parent.TransformPoint(offPose.HandPos);
                }
                else
                {
                    var mainBone = VRConfig.IsLeftHanded ? lb : rb;
                    var ht = held.HandTransformsRight;
                    if (ht != null && ht.Exists && ht.Parent != null && mainBone != null)
                        mainBone.position = ht.Parent.TransformPoint(ht.HandPos);
                }
            }
        }
        catch { }

        try
        {
            var held = __instance._player != null && __instance._player.Holding != null
                ? __instance._player.Holding.HeldItem : null;
            PoseLeftHandedFingers(__instance, held);
        }
        catch { }

    }
}
