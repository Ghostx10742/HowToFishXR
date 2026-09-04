using HowToFishVR.Input;
using HowToFishVR.Patches;
using UnityEngine;

namespace HowToFishVR.VR;

/// <summary>
/// Positions NON-TOOL held items (food, caught fish, dead bodies, etc.) on your hand(s). Guns/rods are
/// <c>Tool</c>s and are handled by <see cref="Patches.PlayerToolMovementPatches"/>; everything else the
/// game just parks at a flatscreen camera-relative point (so it floats while the hand reaches for it).
///
/// Ordinary non-tools use the game's velocity-based hold, redirected to the main/right hand. Fish and
/// ragdoll secondary bodies therefore retain physics. The left hand can latch to an authored left grip,
/// but that latch is visual only and never changes the item's position or rotation.
///
/// Physics: while held, the item's ROOT rigidbody AND every extra rigidbody (fish flop parts, ragdoll
/// body parts) are made kinematic and their velocities zeroed every frame — a kinematic root with live
/// extra-rig physics is exactly what made held fish/bodies spin and glitch. Everything is restored the
/// moment the item is released.
///
/// Dead bodies use the same main-hand physics hold. PlayerHandsPatches layers the revive slap animation
/// onto the visually-latched left hand without giving that hand control of the body.
/// </summary>
[DefaultExecutionOrder(-500)] // position the item BEFORE PlayerHands.LateUpdate so the hand bones can latch onto its grips
public class VRHeldItem : MonoBehaviour
{
    // The previously-held item whose physics we froze — restored the moment it's no longer held so the
    // fish/creature flops normally again after being dropped/released.
    private static Item _lastHeld;
    // EVERY rigidbody we made kinematic on the held item (root + fish flop segments + ragdoll parts). A
    // held fish is a HINGE-JOINT chain; making only the root kinematic and then TELEPORTING it to the hand
    // each frame makes the joint solver fling the still-dynamic segments to infinity ("fish stretches out
    // to oblivion"). Freezing the whole chain keeps it rigid. Only bodies that were dynamic are recorded,
    // so restore returns exactly them.
    private static readonly System.Collections.Generic.List<Rigidbody> _frozen = new System.Collections.Generic.List<Rigidbody>();

    // Support-hand latch state: the LEFT hand engages by GRIPPING near the item's authored left grip.
    // It changes only the rendered hand position; it never rotates, teleports, or otherwise steers the item.
    // The main/right hand remains the sole authority and the game's hold solver preserves item physics.
    private static bool _fishTwoHand;
    private static Item _supportLatchItem;
    // Fish are bigger targets than a gun foregrip — generous latch radius.
    private const float FishLatchRadius = 0.3f;

    // REVIVE FREEZE state: when a held dead body starts being resurrected, its offset from the MAIN
    // hand is captured ONCE and re-applied every frame — the body stays exactly where it was (no snap
    // to a one-hand pose, no back-and-forth with the slap animation) while the off hand rides the game's
    // own slap animation. Released the moment the revive ends / the item is dropped.
    private static bool _revivePinned;
    private static Vector3 _reviveOffset;
    private static Quaternion _reviveRotOffset;

    /// <summary>True while the left support hand is visually latched to a non-tool item's left grip.</summary>
    public static bool FishTwoHand => _fishTwoHand;

    public static void Create()
    {
        var go = new GameObject("HowToFishVR HeldItem");
        DontDestroyOnLoad(go);
        go.AddComponent<VRHeldItem>();
    }

    private void LateUpdate() => Pose();

    /// <summary>Restore the previous held item's physics if it changed (dropped, released, swapped).</summary>
    private static void RestoreLastHeld()
    {
        if (_lastHeld == null && _frozen.Count == 0) return;
        // Un-freeze exactly the bodies we froze (they were dynamic before), so the fish flops normally again.
        for (int i = 0; i < _frozen.Count; i++)
            try { if (_frozen[i] != null && _frozen[i].isKinematic) _frozen[i].isKinematic = false; } catch { }
        _frozen.Clear();
        _lastHeld = null;
    }

    private static void ClearSupportHandLatch()
    {
        _fishTwoHand = false;
        _supportLatchItem = null;
    }

    /// <summary>Make the WHOLE held item rigid: every dynamic rigidbody in its hierarchy goes kinematic and
    /// is recorded for restore. This is what stops a hinge-jointed fish from flinging apart when the root is
    /// teleported to the hand.</summary>
    private static void FreezeWholeItem(Item held)
    {
        _frozen.Clear();
        Rigidbody[] all = null;
        try { all = held.GetComponentsInChildren<Rigidbody>(true); } catch { }
        if (all != null)
        {
            foreach (var rb in all)
            {
                if (rb == null || rb.isKinematic) continue;
                // Zero velocity BEFORE going kinematic — setting velocity on an already-kinematic body is a
                // no-op that logs a warning every call (was thousands of "Setting velocity of a kinematic
                // body is not supported" spam → lag).
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic = true;
                _frozen.Add(rb);
            }
        }
        else if (held.Rig != null && !held.Rig.isKinematic)
        {
            held.Rig.isKinematic = true; _frozen.Add(held.Rig);
        }
    }

    private static void FreezeRig(Rigidbody rb)
    {
        if (rb == null) return;
        if (!rb.isKinematic) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; } // zero before kinematic (see FreezeWholeItem)
        rb.isKinematic = true;
    }

    /// <summary>The authored hand pose (grip) of a hand, in the item's CURRENT local space. Returns false
    /// if the hand has no authored pose (that hand isn't used to hold this item).</summary>
    private static bool TryGetHandPoseLocal(Item held, HandTransforms ht, out Vector3 localPos, out Quaternion localRot)
    {
        localPos = default;
        localRot = Quaternion.identity;
        if (held == null || ht == null || !ht.Exists) return false;
        try
        {
            Transform parent = ht.Parent != null ? ht.Parent : held.transform;
            Vector3 worldPos = parent.TransformPoint(ht.HandPos);
            Quaternion worldRot = parent.rotation * ht.HandRot;
            localPos = held.transform.InverseTransformPoint(worldPos);
            localRot = Quaternion.Inverse(held.transform.rotation) * worldRot;
            return true;
        }
        catch { return false; }
    }

    private void Pose()
    {
        try
        {
            if (!Plugin.HeadsetActive) { RestoreLastHeld(); ClearSupportHandLatch(); return; } // no LIVE headset -> leave items to the game's own hold
            var rig = VRRig.Instance;
            if (rig == null || rig.RightHand == null || rig.LeftHand == null) { RestoreLastHeld(); ClearSupportHandLatch(); return; }

            Player p = null;
            try { p = Player.LocalPlayer; } catch { }
            if (p == null) { RestoreLastHeld(); ClearSupportHandLatch(); return; }
            try { if (Boat.IsDrivingLocally) { RestoreLastHeld(); ClearSupportHandLatch(); return; } } catch { }

            Item held = null;
            try { held = p.Holding != null ? p.Holding.HeldItem : null; } catch { }
            if (held == null) { RestoreLastHeld(); ClearSupportHandLatch(); return; }
            if (held is Tool) { RestoreLastHeld(); ClearSupportHandLatch(); return; }   // tools are handled by the tool patch
            if (held.transform == null) { RestoreLastHeld(); ClearSupportHandLatch(); return; }

            // PHYSICS HOLD (fish / creatures / simple items): leave the item to the GAME'S OWN velocity-based
            // hold, whose target we redirect to the hand in HeldItemHoldPatches — so it keeps its flop physics
            // and can't stretch. Do NOT freeze or teleport it here (that killed the physics and ripped the
            // jointed fish apart). RestoreLastHeld unfreezes anything a previous grab froze. Dead bodies
            // use this same stable path. The left hand may latch to the authored left grip, but it never
            // changes the item's pose; TNT remains excluded and falls through to manual handling below.
            if (Patches.HeldItemHoldPatches.ShouldHandHold(p.Holding))
            {
                RestoreLastHeld();
                _lastHeld = null;
                if (_supportLatchItem != held)
                {
                    _fishTwoHand = false;
                    _supportLatchItem = held;
                }
                UpdateFishTwoHand(rig, held);
                return;
            }

            ClearSupportHandLatch();

            // The item changed (or this is the first frame): freeze the new item's physics — root AND
            // every extra rig (fish flop parts, ragdoll limbs). A kinematic root with live extra-rig
            // physics is what made held fish/bodies spin and glitch. Reset the two-hand latch state.
            if (held != _lastHeld)
            {
                RestoreLastHeld();
                _fishTwoHand = false;
                FreezeWholeItem(held); // freeze the ENTIRE rigidbody chain (fixes the fish stretch-to-oblivion)
                _lastHeld = held;
            }
            // NOTE: no per-frame velocity zeroing here. The bodies are already kinematic (frozen at grab),
            // and a kinematic body ignores velocity — setting it every frame did nothing except log a
            // "Setting velocity of a kinematic body is not supported" warning per body per frame (major log
            // spam / lag). Velocity is zeroed once, before freezing (FreezeWholeItem/FreezeRig).

            // Grip points: real grip models (HandModelRight/Left) if authored, else the authored hand
            // poses. Fish/creatures hold via HandTransforms — they have NO HandModel, so the old
            // HandModel-only check silently skipped them (two-hand fish fell onto one hand).
            bool modelR = held.HandModelRight != null;
            bool modelL = held.HandModelLeft != null;
            bool poseR = TryGetHandPoseLocal(held, held.HandTransformsRight, out var lpR, out var lrR);
            bool poseL = TryGetHandPoseLocal(held, held.HandTransformsLeft, out var lpL, out var lrL);

            // REVIVE FREEZE: while a held dead body is being resurrected the body must NOT move — it
            // stays pinned to the MAIN hand (one-hand grip) and the off hand rides the game's own slap
            // animation (PlayerHandsPatches). Skip the two-hand re-pose entirely during the revive.
            bool reviving = false;
            try { reviving = held is DeadPlayer rdp && rdp._isResurrecting; } catch { }
            var main = rig.MainHand;
            if (main == null) { RestoreLastHeld(); return; }

            if (reviving)
            {
                // Capture the body's offset from the main hand ONCE when the revive starts, then pin it
                // there every frame: the body stays locked to the hand exactly where it was (whether the
                // player was one- or two-hand gripping) instead of snapping to a fresh pose or getting
                // shaken by the slap animation. The rigidbodies are already frozen, so the ragdoll holds
                // its pose.
                if (!_revivePinned)
                {
                    _revivePinned = true;
                    _reviveOffset = held.transform.position - main.position;
                    _reviveRotOffset = Quaternion.Inverse(main.rotation) * held.transform.rotation;
                }
                held.transform.SetPositionAndRotation(main.position + _reviveOffset, main.rotation * _reviveRotOffset);
                PinExplosiveLighter(rig, held);
                return;
            }
            _revivePinned = false;

            // ONE-HAND. Mirror of the gun latch: pin the MAIN hand's grip to the main controller.
            try
            {
                var gripModel = held.HandModelRight;
                bool mainHasPose = poseR;
                var gripPose = lpR;
                var gripPoseRot = lrR;

                // 1) Real grip model (tools with hand meshes): pin the model to the controller.
                if (gripModel != null)
                {
                    Quaternion rot = main.rotation;
                    Vector3 pos = main.position - rot * held.transform.InverseTransformPoint(gripModel.position);
                    held.transform.SetPositionAndRotation(pos, rot);
                    PinExplosiveLighter(rig, held);
                    return;
                }

                // 2) Authored hand pose (fish/edibles): place the item so the authored pose sits EXACTLY
                // on the controller WITH the natural hand orientation (controller * grip offset) — the
                // same natural angle as the empty/gun hand. This is what makes the fish sit on the hand
                // instead of flying to a camera-relative spot or centering inside the fist, AND keeps the
                // hand looking right instead of twisted (the hand bones latch position-only and keep the
                // controller-driven rotation).
                if (mainHasPose)
                {
                    Quaternion gripOffset = PlayerHandsPatches.RightGripOffset;
                    Quaternion rot = (main.rotation * gripOffset) * Quaternion.Inverse(gripPoseRot);
                    Vector3 pos = main.position - rot * gripPose;
                    held.transform.SetPositionAndRotation(pos, rot);
                    PinExplosiveLighter(rig, held);
                    return;
                }

                // 3) Last resort: carry the item in front of the hand (never centered inside it).
                held.transform.SetPositionAndRotation(
                    main.position + main.rotation * new Vector3(0f, -0.06f, 0.2f),
                    main.rotation);
            }
            catch { }
            PinExplosiveLighter(rig, held);
        }
        catch { }
    }

    /// <summary>
    /// TNT / dynamite: the item's LIGHTER is the "left-hand item" — the base game parks it at a
    /// camera-relative spot (it floats next to the TNT in the right hand). Pin it to the OFF controller
    /// so it sits in the left hand and can be moved independently; while the fuse is being lit
    /// (<c>_isActivated</c>) the game's own lighter-to-fuse animation takes over.
    /// </summary>
    private static void PinExplosiveLighter(VRRig rig, Item held)
    {
        try
        {
            if (!(held is Explosive ex) || ex._lighter == null) return;
            if (!ex._lighter.gameObject.activeInHierarchy) return;
            if (ex._isActivated) return;                // the game animates the lighter to the fuse
            if (ex._lighterPickupPercent < 1f) return;  // let the equip (draw) animation finish first

            // Pin to the visible off-hand bone using the EXPLOSIVE'S OWN authored left-hand relationship.
            // The previous arbitrary +2cm / +45deg correction double-offset the already-correct hand bone
            // and put the lighter nowhere near its intended grip.
            var p = Player.LocalPlayer;
            var hands = p != null ? p.Hands : null;
            if (hands == null) return;
            const bool offIsLeft = true;
            Transform offBone = hands.HandBoneLeft;
            if (offBone == null) return;

            if (_lighterGripItem != ex)
            {
                _lighterGripItem = ex;
                _lighterFromHandValid = false;
            }

            if (!_lighterFromHandValid)
            {
                try
                {
                    var ht = offIsLeft ? ex.HandTransformsLeft : ex.HandTransformsRight;
                    if (ht != null && ht.Exists && ht.Parent != null && ex._lighter.parent != null)
                    {
                        Vector3 authoredHandPos = ht.Parent.TransformPoint(ht.HandPos);
                        Quaternion authoredHandRot = ht.Parent.rotation * ht.HandRot;
                        Vector3 authoredLighterPos = ex._lighter.parent.TransformPoint(ex._lighterOrigPos);
                        Quaternion authoredLighterRot = ex._lighter.parent.rotation * ex._lighterOrigRot;
                        _lighterFromHandPos = Quaternion.Inverse(authoredHandRot) * (authoredLighterPos - authoredHandPos);
                        _lighterFromHandRot = Quaternion.Inverse(authoredHandRot) * authoredLighterRot;
                        _lighterFromHandValid = true;
                    }
                }
                catch { }
            }

            if (_lighterFromHandValid)
            {
                ex._lighter.SetPositionAndRotation(
                    offBone.position + offBone.rotation * _lighterFromHandPos,
                    offBone.rotation * _lighterFromHandRot);
            }
            else
            {
                // Items without a valid authored left-hand pose still sit directly on the visible hand;
                // importantly there is no second guessed rotation/translation layered on top.
                ex._lighter.SetPositionAndRotation(offBone.position, offBone.rotation);
            }
        }
        catch { }
    }

    // Stable authored relationship between the TNT lighter and the game's left-hand pose. Reusing the
    // item's own authored offset is more accurate than an arbitrary palm nudge/45-degree rotation and
    // works regardless of the lighter mesh pivot.
    private static Explosive _lighterGripItem;
    private static Vector3 _lighterFromHandPos;
    private static Quaternion _lighterFromHandRot = Quaternion.identity;
    private static bool _lighterFromHandValid;

    /// <summary>
    /// Support-hand latch for every physics-held non-tool item. The LEFT hand is free until it grips
    /// near the item's authored left grip. Latching affects only PlayerHandsPatches' rendered hand bone;
    /// the item continues to be moved solely by the main/right-hand physics target.
    /// </summary>
    private static void UpdateFishTwoHand(VRRig rig, Item held)
    {
        try
        {
            // Fish stay strictly one-handed. Their secondary rigidbodies keep flopping through the game's
            // physics hold, but the support hand must never latch or influence them.
            if (held is Fish || held.Fish != null)
            {
                _fishTwoHand = false;
                return;
            }

            var support = rig.LeftHand;
            if (support == null || support == rig.MainHand)
            {
                _fishTwoHand = false;
                return;
            }

            float gripVal = VRActions.Instance.LeftGrip.ReadValue<float>();
            bool gripping = gripVal >= VRConfig.GripThreshold.Value;

            // The authored LEFT grip in world space. Items without one remain one-handed.
            Vector3 supportGripWorld;
            bool hasSupportGrip = false;
            if (held.HandModelLeft != null)
            {
                supportGripWorld = held.HandModelLeft.position;
                hasSupportGrip = true;
            }
            else if (held.HandTransformsLeft != null && held.HandTransformsLeft.Exists &&
                     held.HandTransformsLeft.Parent != null)
            {
                supportGripWorld = held.HandTransformsLeft.Parent.TransformPoint(held.HandTransformsLeft.HandPos);
                hasSupportGrip = true;
            }
            else supportGripWorld = support.position;

            if (!hasSupportGrip)
            {
                _fishTwoHand = false;
                return;
            }

            if (!_fishTwoHand)
            {
                if (gripping && Vector3.Distance(support.position, supportGripWorld) <= FishLatchRadius)
                {
                    _fishTwoHand = true;
                }
            }
            else if (!gripping)
            {
                _fishTwoHand = false;
            }
        }
        catch { }
    }

}
