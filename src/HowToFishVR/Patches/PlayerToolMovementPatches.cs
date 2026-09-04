using HarmonyLib;
using HowToFishVR.Input;
using HowToFishVR.VR;
using UnityEngine;

namespace HowToFishVR.Patches;

/// <summary>
/// Held tools/weapons follow the dominant-hand controller (the game glues them to the camera in
/// <c>PlayerToolMovement.GlueToCamera()</c>; we postfix to place them on the hand). The muzzle
/// (<c>Attachments.FirePoint</c>) is a child of the model, so the gun aims where the hand points.
///
/// Two-hand grip (both guns and fishing rods): the off-hand is free until it grips near the tool's
/// fore-grip. Guns use both controller positions to aim the actual muzzle, with the main hand as the
/// rear anchor and the support hand controlling direction. For
/// fishing rods it latches the left hand onto the rod's own grip (visual only — the rod stays 100%
/// main-hand driven) and enables physical reeling (<see cref="FishingPhysicalReelPatches"/>).
/// </summary>
[HarmonyPatch(typeof(PlayerToolMovement))]
internal static class PlayerToolMovementPatches
{
    private static readonly Quaternion ToolRotOffset = Quaternion.Euler(45f, 0f, 0f);

    private const float ForegripDistance = 0.24f;
    private const float ForegripLatchRadius = 0.20f;
    private const float RodLatchRadius = 0.5f; // generous — grabbing anywhere near the reel/crank latches the off hand
    private const float MinTwoHandAimSpan = 0.15f; // below this the hand-to-hand direction is unstable
    private const float TwoHandAimFollow = 35f;    // fast damping removes controller micro-jitter without float
    private const float SupportSteeringGain = 1.06f; // tester requested only a very small increase

    private static bool _gunTwoHand;
    private static bool _rodTwoHand;

    // Per-side authored knuckle-to-hand relationships. Brass knuckles have distinct left and right
    // calibration in the prefab; copying/mirroring the dominant pose loses that left-hand correction.
    private static readonly Vector3[] _fistFromHandPos = new Vector3[2];
    private static readonly Quaternion[] _fistFromHandRot = { Quaternion.identity, Quaternion.identity };
    private static readonly bool[] _fistFromHandValid = new bool[2];
    private static Melee _fistPoseTool;

    // Stable two-hand gun aim state. The actual muzzle axis is aligned to the vector between controllers;
    // the last valid rotation is retained inside the close-hands deadzone so the gun cannot flip or jitter.
    private static Item _twoHandTool;
    private static Quaternion _twoHandAimRotation = Quaternion.identity;
    private static bool _twoHandAimValid;
    // Preserve the exact one-hand gun pose at the instant the support hand grips. Without this grab
    // offset, aligning the muzzle directly to the hand-to-hand line makes the whole gun visibly jump
    // when two-hand mode engages even though the rear grip remains mathematically anchored.
    private static Quaternion _twoHandGrabOffset = Quaternion.identity;
    private static bool _twoHandGrabOffsetValid;

    // Our own smoothed tool pose. We can't ease from tool.transform because the game resets it to the
    // camera every frame before this postfix — so we keep the eased pose ourselves.
    private static Vector3 _toolPos;
    private static Quaternion _toolRot;
    private static bool _toolInit;

    // Recoil rig rest pose (captured once per TOOL) so we can read the game's recoil as an offset from
    // it. Re-captured whenever the held tool changes — a stale rest pose from a previous weapon (or a
    // fresh spawn) is what made the gun jump out of place for a split second.
    private static Vector3 _recoilRestPos;
    private static Quaternion _recoilRestRot;
    private static bool _recoilInit;
    private static PlayerToolMovement _recoilLastOwner;
    private static Item _recoilLastTool;

    /// <summary>True while a fishing rod is gripped by both hands (enables the off-hand latch + physical reeling).</summary>
    public static bool RodTwoHand => _rodTwoHand;

    /// <summary>True while a gun is gripped by both hands (drop combo is disabled then, so an accidental
    /// B press can't drop a two-handed gun).</summary>
    public static bool GunTwoHand => _gunTwoHand;

    /// <summary>When two-handing (gun or rod), the tool's off-hand grip transform the off hand should snap to.</summary>
    public static Transform OffHandGripTarget { get; private set; }

    /// <summary>When holding a tool, the tool's MAIN-hand grip transform the main hand should snap to
    /// (one-hand AND two-hand — same latch).</summary>
    public static Transform MainHandGripTarget { get; private set; }

    /// <summary>Drop both hand-snap targets (called when nothing is held, so a stale target can't freeze
    /// the hand after the tool is dropped).</summary>
    public static void ClearGripTargets()
    {
        OffHandGripTarget = null;
        MainHandGripTarget = null;
    }

    [HarmonyPostfix]
    [HarmonyPatch("GlueToCamera")]
    private static void FollowHand(PlayerToolMovement __instance)
    {
        if (!Plugin.HeadsetActive) return; // no LIVE headset -> don't pin tools to a desk HMD/controller (corrupts the body for other players)
        // LOCAL PLAYER ONLY. This patch runs on every player's PlayerToolMovement — remote players' held
        // tools must stay on THEIR hands; without this gate their guns got glued to our controllers in
        // multiplayer.
        try { if (__instance._player == null || __instance._player.Owner == null || !__instance._player.Owner.IsLocalClient) return; } catch { return; }
        try { if (Boat.IsDrivingLocally) { _gunTwoHand = false; _rodTwoHand = false; _toolInit = false; _recoilInit = false; ClearGripTargets(); return; } } catch { }
        var rig = VRRig.Instance;
        if (rig == null) return;

        var tool = __instance.CurrentTool;
        if (tool == null || tool.transform == null)
        {
            _gunTwoHand = false; _rodTwoHand = false; _toolInit = false;
            _twoHandTool = null; _twoHandAimValid = false; _twoHandGrabOffsetValid = false;
            ClearGripTargets(); // never leave a stale snap target on a dropped tool
            return;
        }

        if (_twoHandTool != tool)
        {
            _twoHandTool = tool;
            _twoHandAimValid = false;
            _twoHandGrabOffsetValid = false;
            _gunTwoHand = false;
        }

        var main = rig.MainHand;
        var off = rig.OffHand;
        if (main == null || off == null) return;

        var weapon = tool.Weapon;
        bool isRod = tool is FishingRod;

        // Use EACH ITEM'S OWN grip transform (HandModelRight/Left) so every item is held natively by its
        // own grip point AND orientation — no per-item eyeballing. gripLocal* is that grip pose expressed
        // in the tool's local frame (constant per item, so it works for any item automatically).
        Transform gripModel = null;
        try { gripModel = tool.HandModelRight; } catch { }
        bool haveGrip = gripModel != null;
        Vector3 gripLocalPos = haveGrip ? tool.transform.InverseTransformPoint(gripModel.position) : Vector3.zero;

        // One-hand rotation follows the hand. Guns AND fishing rods use the exact same permanent
        // controller-local position calibration as the visible hand bone. Keep the legacy palm nudge
        // only for other tools whose already-approved placement must remain unchanged.
        Quaternion oneHandRot = main.rotation * ToolRotOffset;
        Vector3 palmNudge = main.rotation * new Vector3(0f, -0.035f, -0.03f);
        bool mainIsLeft = main == rig.LeftHand;
        bool offIsLeft = off == rig.LeftHand;
        bool useCalibratedHandAnchors = weapon != null || isRod;
        Vector3 mainAnchor = useCalibratedHandAnchors
            ? PlayerHandsPatches.CalibratedHandPosition(main, mainIsLeft)
            : main.position + palmNudge;
        // The support-hand proximity test uses that calibrated point as well, so the rendered hand and
        // the point used to grab a gun/rod cannot disagree by the saved offset.
        Vector3 offAnchor = useCalibratedHandAnchors
            ? PlayerHandsPatches.CalibratedHandPosition(off, offIsLeft)
            : off.position;
        Vector3 oneHandBasePos = haveGrip ? mainAnchor - oneHandRot * gripLocalPos : mainAnchor;

        // Off-hand grip latches at the ACTUAL second-hand grip point (its HandModel for the off hand),
        // so you grab where the hand really goes — and when latched the off hand snaps there (see
        // OffHandGripTarget, applied in PlayerHandsPatches). For a fishing rod the left hand belongs on
        // the REEL HANDLE (the crank), not the rod base, so the rod's off-hand grip point is its crank
        // handle holder when present — that's also what makes physical cranking put your hand on the reel.
        Transform offGripModel = null;
        try { offGripModel = tool.HandModelLeft; } catch { }
        if (isRod)
        {
            // The rod's off hand belongs on the REEL / CRANK. Resolve to it EVEN IF the rod has no authored
            // HandModelLeft — the old `offGripModel != null` gate left the grip point null on those rods, so
            // the latch fell back to a computed spot that isn't the reel and grabbing "wouldn't work". Prefer
            // the crank hand-holder, then the crank handle, then the crank itself.
            try
            {
                var rod = (FishingRod)tool;
                if (rod._crankHandleHandHolder != null) offGripModel = rod._crankHandleHandHolder;
                else if (rod._crankHandle != null) offGripModel = rod._crankHandle;
                else if (rod._crank != null) offGripModel = rod._crank;
            }
            catch { }
        }
        bool haveOffGrip = offGripModel != null;
        Vector3 offGripLocalPos = haveOffGrip
            ? tool.transform.InverseTransformPoint(offGripModel.position)
            : gripLocalPos + Vector3.forward * ForegripDistance;
        float offGrip = off == rig.LeftHand
            ? VRActions.Instance.LeftGrip.ReadValue<float>()
            : VRActions.Instance.RightGrip.ReadValue<float>();
        // Grip HYSTERESIS: engage at the configured threshold, release only well below it, so a slightly
        // wavering trigger can't drop the two-hand grip mid-reel (the "grip keeps letting go" flicker).
        float engageGrip = VRConfig.GripThreshold.Value;
        float releaseGrip = Mathf.Max(0.05f, engageGrip - 0.2f);
        // The reel/crank section is a bigger target than a gun's foregrip — give the rod a generous
        // latch radius so grabbing near the reel is easy.
        float latchRadius = isRod ? RodLatchRadius : ForegripLatchRadius;
        // Test against where the foregrip will be in the hand-driven pose, not the camera-relative pose
        // that the base game briefly writes before this postfix.
        Vector3 foregrip = oneHandBasePos + oneHandRot * offGripLocalPos;
        bool nearGrip = Vector3.Distance(offAnchor, foregrip) <= latchRadius;

        // Gun two-hand is always available: engage on grip-at-foregrip, release on ungrip (hysteresis).
        bool gunWasTwoHand = _gunTwoHand;
        if (weapon != null)
        {
            if (!_gunTwoHand) { if (offGrip >= engageGrip && nearGrip) _gunTwoHand = true; }
            else if (offGrip < releaseGrip) { _gunTwoHand = false; _twoHandAimValid = false; _twoHandGrabOffsetValid = false; }
            if (!gunWasTwoHand && _gunTwoHand) _twoHandGrabOffsetValid = false;
            weapon._holdingAdsInput = _gunTwoHand;
        }
        else { _gunTwoHand = false; _twoHandAimValid = false; _twoHandGrabOffsetValid = false; }

        // Rod two-hand detection (BEFORE the latch below so the off hand snaps the same frame it
        // engages). Engages when the off hand GRIPS near the reel/crank; releases only below the release
        // threshold (hysteresis) so it doesn't flicker off while you're cranking.
        if (isRod)
        {
            if (!_rodTwoHand) { if (offGrip >= engageGrip && nearGrip) _rodTwoHand = true; }
            else if (offGrip < releaseGrip) _rodTwoHand = false;
        }
        else _rodTwoHand = false;

        // Grip latching: the OFF hand snaps to the fore-grip while two-handing and its real controller
        // position steers the gun. The MAIN hand remains the rear/trigger anchor. Both rendered hands
        // conform to the authored grips while the physical controllers define the rigid weapon pose.
        // Guns AND fishing rods latch the off hand to their authored off-hand grip while two-handing.
        // For the rod the latch is VISUAL ONLY — the left hand sits on the reel/crank section and never
        // influences the rod's pose (100% main-hand driven); it also enables the physical reel
        // (FishingPhysicalReelPatches).
        OffHandGripTarget = (_gunTwoHand || _rodTwoHand) ? offGripModel : null;
        // Both brass-knuckle meshes are independently placed from the raw XR controllers below. Do not
        // also pull the dominant player-hand bone onto the item's animated grip: that changed the hand
        // rotation, and retaining only its position made the right knuckle appear displaced from the
        // now controller-driven hand.
        MainHandGripTarget = haveGrip && !(tool is Melee) ? gripModel : null;

        // TWO-HAND: rear/main hand is the positional anchor. The vector from main to support hand defines
        // the aim direction, and we rotate the ACTUAL FirePoint axis onto it (model-local forward is not
        // reliable across guns). A minimal FromToRotation preserves the main hand's roll naturally.
        Quaternion rot = oneHandRot;
        if (_gunTwoHand)
        {
            Vector3 handAxis = offAnchor - mainAnchor;
            if (handAxis.magnitude >= MinTwoHandAimSpan)
            {
                Vector3 localAimAxis = Vector3.forward;
                try
                {
                    if (weapon.Attachments != null && weapon.Attachments.FirePoint != null)
                        localAimAxis = tool.transform.InverseTransformDirection(weapon.Attachments.FirePoint.forward);
                    else if (haveOffGrip)
                        localAimAxis = offGripLocalPos - gripLocalPos;
                }
                catch { }
                if (localAimAxis.sqrMagnitude < 1e-6f) localAimAxis = Vector3.forward;

                Vector3 oneHandAim = oneHandRot * localAimAxis.normalized;
                Quaternion geometricRot = Quaternion.FromToRotation(oneHandAim, handAxis.normalized) * oneHandRot;

                // Capture the orientation difference between the geometric hand-line solution and the
                // gun's current one-hand pose. Applying that same grab offset thereafter means engaging
                // the support hand starts at exactly the existing pose (zero snap), while subsequent
                // support-hand movement still steers through the proven two-hand geometry.
                if (!_twoHandGrabOffsetValid)
                {
                    _twoHandGrabOffset = Quaternion.Inverse(geometricRot) * oneHandRot;
                    _twoHandGrabOffsetValid = true;
                    _twoHandAimRotation = oneHandRot;
                    _twoHandAimValid = true;
                }

                Quaternion targetRot = geometricRot * _twoHandGrabOffset;

                // A tiny 6% angular gain gives the support hand the requested extra steering authority
                // without changing the rear-hand anchor or the otherwise-approved handling.
                Quaternion steeringDelta = targetRot * Quaternion.Inverse(oneHandRot);
                steeringDelta.ToAngleAxis(out float steerAngle, out Vector3 steerAxis);
                if (steerAngle > 180f) steerAngle -= 360f;
                if (steerAxis.sqrMagnitude > 1e-6f && Mathf.Abs(steerAngle) > 0.001f)
                    targetRot = Quaternion.AngleAxis(steerAngle * SupportSteeringGain, steerAxis.normalized) * oneHandRot;

                if (_twoHandAimValid)
                {
                    float follow = 1f - Mathf.Exp(-TwoHandAimFollow * Time.unscaledDeltaTime);
                    _twoHandAimRotation = Quaternion.Slerp(_twoHandAimRotation, targetRot, follow);
                }
                else
                {
                    _twoHandAimRotation = targetRot;
                    _twoHandAimValid = true;
                }
            }
            if (_twoHandAimValid) rot = _twoHandAimRotation;
        }
        else { _twoHandAimValid = false; _twoHandGrabOffsetValid = false; }

        // Keep the main authored grip fixed to the rear hand after the two-hand rotation is solved.
        Vector3 basePos = haveGrip ? mainAnchor - rot * gripLocalPos : mainAnchor;

        // Item stays on the grip at your hand, with the game's RECOIL kick applied on top (it worked well).
        Vector3 finalPos = basePos;
        Quaternion finalRot = rot;
        var recoilRig = __instance._toolRecoilRig;
        if (recoilRig != null)
        {
            var rt = recoilRig.transform;
            // Re-capture the rest pose when the tool (or the owner's rig) changes — a stale rest pose
            // from a previous weapon made the gun sit out of place for a moment on pickup/respawn.
            if (!_recoilInit || _recoilLastOwner != __instance || _recoilLastTool != tool)
            {
                _recoilRestPos = rt.localPosition; _recoilRestRot = rt.localRotation; _recoilInit = true;
                _recoilLastOwner = __instance; _recoilLastTool = tool;
            }
            Quaternion recoilRot = Quaternion.Inverse(_recoilRestRot) * rt.localRotation;
            Vector3 recoilPos = rt.localPosition - _recoilRestPos;
            // The rest pose can be captured mid-kick (picked up a new tool while the previous one was
            // still kicking) — that makes the gun sit out of place for the whole hold. When the rig has
            // SETTLED (offset ~0), refresh the rest pose so a stale capture self-corrects instantly.
            if (recoilPos.magnitude < 0.002f && recoilRot.eulerAngles.magnitude < 1f)
            {
                _recoilRestPos = rt.localPosition;
                _recoilRestRot = rt.localRotation;
                recoilRot = Quaternion.identity;
                recoilPos = Vector3.zero;
            }
            finalRot = rot * recoilRot;
            finalPos = basePos + finalRot * recoilPos;
        }
        tool.transform.SetPositionAndRotation(finalPos, finalRot);

        if (tool is Melee melee) PinTwoHandMeleeFists(melee);
    }

    /// <summary>Place BOTH meshes of a two-fist melee item from their raw XR controllers using each
    /// side's own authored pose. Never use rendered hand bones as sources: PlayerHands can itself latch
    /// those bones to item grips, forming the feedback loop that previously pinned the arms.</summary>
    internal static void PinTwoHandMeleeFists(Melee melee)
    {
        try
        {
            if (melee == null || !melee._useBothHands || melee._hands == null || melee._hands.Length < 2) return;
            try
            {
                if (melee.Holder == null || melee.Holder.Owner == null || !melee.Holder.Owner.IsLocalClient) return;
            }
            catch { return; }
            var rig = VRRig.Instance;
            if (rig == null || rig.LeftHand == null || rig.RightHand == null) return;

            if (_fistPoseTool != melee)
            {
                _fistPoseTool = melee;
                _fistFromHandValid[0] = _fistFromHandValid[1] = false;
            }

            for (int side = 0; side < 2; side++)
            {
                bool isLeft = side == 1; // game arrays are authored right, then left
                Transform controller = isLeft ? rig.LeftHand : rig.RightHand;
                Transform fist = melee._hands[side];
                var ht = isLeft ? melee.HandTransformsLeft : melee.HandTransformsRight;
                if (fist == null || controller == null || ht == null || !ht.Exists || ht.Parent == null || fist.parent == null)
                    continue;

                if (!_fistFromHandValid[side])
                {
                    Vector3 authoredHandPos = ht.Parent.TransformPoint(ht.HandPos);
                    Quaternion authoredHandRot = ht.Parent.rotation * ht.HandRot;
                    Vector3 authoredFistPos = fist.parent.TransformPoint(melee._origPos[side]);
                    Quaternion authoredFistRot = fist.parent.rotation * melee._origRot[side];
                    _fistFromHandPos[side] = Quaternion.Inverse(authoredHandRot) * (authoredFistPos - authoredHandPos);
                    _fistFromHandRot[side] = Quaternion.Inverse(authoredHandRot) * authoredFistRot;
                    _fistFromHandValid[side] = true;
                }

                if (_fistFromHandValid[side])
                {
                    Quaternion gripOffset = isLeft
                        ? PlayerHandsPatches.LeftGripOffset
                        : PlayerHandsPatches.RightGripOffset;
                    Quaternion desiredHandRot = controller.rotation * gripOffset;
                    fist.SetPositionAndRotation(
                        controller.position + desiredHandRot * _fistFromHandPos[side],
                        desiredHandRot * _fistFromHandRot[side]);
                }
            }

        }
        catch { }
    }
}

/// <summary>Let Melee advance all attack timers, target checks, sounds and damage, but restore the two
/// fist transforms afterward so the flatscreen lunge is visual-only suppressed. Capturing/restoring
/// avoids modifying animation state and—critically—does not drive either player arm from an item bone.</summary>
[HarmonyPatch(typeof(Melee), "LateUpdate")]
internal static class VRMeleeVisualPatches
{
    private static Melee _captured;
    private static readonly Vector3[] _positions = new Vector3[2];
    private static readonly Quaternion[] _rotations = { Quaternion.identity, Quaternion.identity };

    [HarmonyPrefix]
    private static void CaptureKnucklePose(Melee __instance)
    {
        _captured = null;
        if (!Plugin.HeadsetActive || !IsLocalTwoFist(__instance)) return;
        PlayerToolMovementPatches.PinTwoHandMeleeFists(__instance);
        for (int i = 0; i < 2; i++)
        {
            var fist = __instance._hands[i];
            if (fist == null) return;
            _positions[i] = fist.position;
            _rotations[i] = fist.rotation;
        }
        _captured = __instance;
    }

    [HarmonyPostfix]
    private static void RestoreKnucklePose(Melee __instance)
    {
        if (_captured != __instance || __instance._hands == null || __instance._hands.Length < 2) return;
        for (int i = 0; i < 2; i++)
            if (__instance._hands[i] != null)
                __instance._hands[i].SetPositionAndRotation(_positions[i], _rotations[i]);
        _captured = null;
    }

    private static bool IsLocalTwoFist(Melee melee)
    {
        try
        {
            return melee != null && melee._useBothHands && melee._hands != null && melee._hands.Length >= 2 &&
                   melee.Holder != null && melee.Holder.Owner != null && melee.Holder.Owner.IsLocalClient;
        }
        catch { return false; }
    }
}

/// <summary>Weapons write a sprint tuck/indent through these setters. Controller-driven VR items should
/// remain exactly in the physical hand while sprinting, so discard only that sprint pose layer.</summary>
[HarmonyPatch]
internal static class VRSprintItemPosePatches
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlayerToolMovement), nameof(PlayerToolMovement.SetSprintPos))]
    private static void NoSprintPosition(ref Vector3 sprintPos)
    {
        if (Plugin.HeadsetActive) sprintPos = Vector3.zero;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlayerToolMovement), nameof(PlayerToolMovement.SetSprintRot))]
    private static void NoSprintRotation(ref Vector3 sprintRot)
    {
        if (Plugin.HeadsetActive) sprintRot = Vector3.zero;
    }
}

/// <summary>
/// Disable the rod's cast pull-back ANIMATION — it rotates/moves the rod back when throwing the line,
/// which fights the controller-driven rod. Forcing the pull-back amount to 0 keeps the rod on your hand
/// (the cast mechanic itself is unaffected).
/// </summary>
[HarmonyPatch]
internal static class FishingRodAnimPatches
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(FishingRod), "SetPullBackAnimation")]
    private static void NoPullBack(FishingRod __instance, ref float rodRot)
    {
        if (!Plugin.VREnabled) return;
        // LOCAL PLAYER ONLY — killing a REMOTE (flatscreen) player's rod pull-back animation makes their
        // cast look broken on our VR client. Only the local VR player's own rod skips the pull-back.
        try { if (__instance.Holder == null || __instance.Holder.Owner == null || !__instance.Holder.Owner.IsLocalClient) return; } catch { return; }
        rodRot = 0f;
    }
}

/// <summary>Fishing reel remains available through the game's normal rod input.</summary>
[HarmonyPatch]
internal static class FishingReelPatches
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(FishingRod), "PrimaryInput")]
    private static bool GateReel()
    {
        return true; // reeling always allowed for now (was gated to two-hand)
    }
}

/// <summary>
/// Physical reeling (two-hand rod grip). When the rod is held two-handed and the user PHYSICALLY cranks
/// the off hand around the reel axis, this drives the game's OWN reel: cranking = the game's regular
/// hold-reel speed (<c>_isReelingIn</c> + <c>_holdReelSpeed</c>), and FAST cranking pumps the game's own
/// fast/spam reel mode (<c>_curReelSpeedMulti</c>). We never invent reel speeds — we only detect that the
/// hand is cranking and switch the game's modes. Clockwise and counter-clockwise both work
/// (direction-agnostic, we use the absolute angular speed).
///
/// AUTO REEL MODE: as soon as the two-hand grip engages (or you start cranking) with the line still
/// reeling out (just cast), the rod is switched from cast/reel-out to reeling-in (the same transition the
/// reel button's press performs) — so you never have to press the reel button first.
///
/// The crank is measured from the CONTROLLER (not the latched hand bone): the hand bone snaps to the
/// reel handle for display, but the controller is free, so the real physical orbit around the reel axis
/// is what we read.
/// </summary>
[HarmonyPatch(typeof(FishingRod))]
internal static class FishingPhysicalReelPatches
{
    // deg/s of off-hand rotation around the reel axis before it counts as cranking. 40°/s ≈ one full
    // crank in ~9s — a normal physical crank comfortably exceeds it, while resting the hand on the reel
    // (tiny jitter) does not. Fast/spam mode kicks in above ~380°/s (a deliberately fast crank).
    private const float CrankStartAngVel = 25f; // gentler cranks now register (was 40 — too stiff to trigger)
    // deg/s above which cranking counts as FAST = the game's fast/spam reel mode
    private const float CrankFastAngVel = 380f;
    // cap for the game's reel-speed multiplier while fast-cranking
    private const float MaxReelMulti = 4f;

    private static float _lastAngle;
    private static bool _angleValid;
    private static float _angVel;
    private static bool _wasCranking;
    private static bool _wasTwoHand;

    [HarmonyPrefix]
    [HarmonyPatch("FixedUpdate")]
    private static void PhysicalReel(FishingRod __instance)
    {
        if (!Plugin.VREnabled) return;
        // Physical reeling ONLY while two-handing the rod (holding the reel with the off hand). The reason
        // it "mostly didn't work / wouldn't even grip" is fixed at the grip-detection SOURCE (ComputeGripTargets
        // now resolves the rod off-grip to the CRANK even when the rod has no HandModelLeft, + a generous
        // latch radius + grip hysteresis), NOT by dropping this gate.
        bool twoHand = PlayerToolMovementPatches.RodTwoHand;
        if (!twoHand)
        {
            _angleValid = false;
            _wasCranking = false;
            _wasTwoHand = false;
            return;
        }
        try
        {
            var holder = __instance.Holder;
            if (holder == null || holder.Owner == null || !holder.Owner.IsLocalClient)
            {
                _angleValid = false;
                _wasCranking = false;
                _wasTwoHand = false;
                return;
            }
        }
        catch { _angleValid = false; _wasCranking = false; _wasTwoHand = false; return; }

        var rig = VRRig.Instance;
        if (rig == null || rig.OffHand == null || rig.MainHand == null) { _angleValid = false; return; }
        // Crank spin transform: prefer _crank, fall back to the crank handle / its hand holder (some rods
        // leave _crank unassigned, which silently killed physical reeling — "physical reeling didn't work").
        var crank = __instance._crank;
        if (crank == null) crank = __instance._crankHandle;
        if (crank == null) crank = __instance._crankHandleHandHolder;
        if (crank == null) { _angleValid = false; _wasCranking = false; _wasTwoHand = false; return; }

        // AUTO REEL MODE: the instant the two-hand grip engages while the line is still reeling out (just
        // cast), switch to reeling-in so cranking works immediately — no reel-button press first.
        if (!_wasTwoHand && twoHand && __instance._isReelingOut && __instance is FishingRodCast cast0)
        {
            cast0.StartReel();
        }
        _wasTwoHand = twoHand;

        // The off hand orbits the reel's spin axis as you crank. The crank's spin axis is its local Z
        // (the game animates _crank.localEulerAngles.z with the line length).
        Vector3 center = crank.position;
        Vector3 axis = crank.TransformDirection(Vector3.forward);
        float positionalAngVel = 0f;
        Vector3 p = rig.OffHand.position - center;
        Vector3 v = p - axis * Vector3.Dot(p, axis);
        if (v.sqrMagnitude >= 1e-6f)
        {
            v.Normalize();

            Vector3 u = Vector3.Cross(axis, Vector3.up);
            if (u.sqrMagnitude < 1e-4f) u = Vector3.Cross(axis, Vector3.right);
            u.Normalize();
            Vector3 w = Vector3.Cross(axis, u);
            float ang = Mathf.Atan2(Vector3.Dot(v, w), Vector3.Dot(v, u)) * Mathf.Rad2Deg;

            float delta = 0f;
            if (_angleValid)
            {
                delta = ang - _lastAngle;
                if (delta > 180f) delta -= 360f;
                else if (delta < -180f) delta += 360f;
            }
            _lastAngle = ang;
            _angleValid = true;
            positionalAngVel = Mathf.Abs(delta) / Mathf.Max(0.0001f, Time.fixedDeltaTime);
        }
        else _angleValid = false;

        // Also accept a natural wrist twist around the reel axis. Position-only orbit detection could
        // read zero when the hand was held close to the handle after button reeling, making physical
        // reeling appear dead until the grip was reset. OpenXR angular velocity is radians/sec.
        float wristAngVel = 0f;
        try
        {
            var node = rig.OffHand == rig.LeftHand
                ? UnityEngine.XR.XRNode.LeftHand : UnityEngine.XR.XRNode.RightHand;
            var device = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(node);
            if (device.isValid && device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.deviceAngularVelocity, out var localAngular))
            {
                Vector3 worldAngular = rig.Root != null ? rig.Root.TransformDirection(localAngular) : localAngular;
                wristAngVel = Mathf.Abs(Vector3.Dot(worldAngular, axis.normalized)) * Mathf.Rad2Deg;
            }
        }
        catch { }

        // Direction-agnostic angular speed (deg/s), smoothed so a jittery hand doesn't stutter the reel.
        float inst = Mathf.Max(positionalAngVel, wristAngVel);
        _angVel = Mathf.Lerp(_angVel, inst, 0.2f);

        bool cranking = _angVel > CrankStartAngVel;
        if (cranking)
        {
            // If still reeling out for any reason, transition now (also covers cranking right after a cast).
            if (__instance._isReelingOut && __instance is FishingRodCast cast1)
            {
                cast1.StartReel();
            }

            // Drive the game's OWN reel: regular cranking = regular hold-reel rate (_holdReelSpeed);
            // fast cranking pumps the game's own _curReelSpeedMulti so steps come faster (spam mode).
            // The game's own FixedUpdate decay (_reelMultiDecreaseSpeed) pulls it back when you slow down.
            __instance._isReelingIn = true;
            if (_angVel > CrankFastAngVel)
                __instance._curReelSpeedMulti = Mathf.Min(
                    __instance._curReelSpeedMulti + __instance._reelMultiIncreaseSpeed * Time.fixedDeltaTime * (_angVel / CrankFastAngVel),
                    MaxReelMulti);
        }
        else if (_wasCranking)
        {
            // Stopped cranking: clear the reel state unless the reel button (right trigger) is held.
            bool buttonHeld = false;
            try { buttonHeld = VRActions.Instance.RightTrigger.ReadValue<float>() >= 0.5f; } catch { }
            if (!buttonHeld) __instance._isReelingIn = false;
        }
        _wasCranking = cranking;
    }
}
