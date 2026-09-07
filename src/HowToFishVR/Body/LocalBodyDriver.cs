using System;
using HowToFishVR.VR;
using UnityEngine;

namespace HowToFishVR.Body;

/// <summary>
/// Per-frame driver for the restored local body. Parents the other-player transform under the player
/// root and follows the camera's yaw (so the body faces your gaze), places the body at the configured
/// offset, and keeps the boat flag in sync (fixes the body shaking on boats issue).
///
/// In VR (<see cref="ArmRig"/>) it additionally re-asserts the FABRIK arm targets every frame toward
/// the controller-driven hand bones and keeps the elbow poles placed, so the arms bend naturally and
/// NEVER stretch to reach — the wrist stays exactly on the controller/gun grip and the elbow/upper arm
/// absorb the remaining reach (BO2-VR's "wrist welded, elbow absorbs" model, implemented through the
/// game's own two-bone IK).
/// </summary>
internal sealed class LocalBodyDriver : MonoBehaviour
{
    private Player _player;
    private OtherPlayer _other;
    private bool _wasOnBoat;
    private bool _bodyHidden; // true while driving the boat (body hidden, shown again on exit)

    internal void Init(Player player)
    {
        _player = player;
        _other = player.Other;
        _wasOnBoat = player.Movement.OnBoat;
        _other._transform.SetParent(player.Transform, false);
        Sync();
    }

    private void Update()
    {
        if (_player == null || _other == null || !HowToFishBody.Active) return;
        Sync();
    }

    internal void Sync()
    {
        try
        {
            if (_player.CamObject == null || _player.Transform == null || _other._transform == null) return;

            // PER-FRAME REPARENT INVARIANT (LCVR VRPlayer.cs:530 "if xrOrigin.parent != transform.parent
            // fix it"): the body must always hang off the player transform. If the game re-parented it out
            // from under us (a transition, a seat), our local-space placement below would be wrong — so
            // re-assert the parent every frame instead of relying on the one-time Init parenting.
            if (_other._transform.parent != _player.Transform)
                _other._transform.SetParent(_player.Transform, false);

            bool onBoat = _player.Movement.OnBoat;
            if (onBoat != _wasOnBoat)
            {
                _wasOnBoat = onBoat;
                _other._velSamples.Clear();
            }
            _other.OnBoat = onBoat;

            // Riding/standing on the boat keeps the full body visible. Only the local DRIVER uses the
            // steering-seat presentation that requires hiding the restored body and arm IK.
            bool driving = false;
            try { driving = Boat.IsDrivingLocally; } catch { }
            if (driving != _bodyHidden)
            {
                _bodyHidden = driving;
                HowToFishBody.SetVisible(!driving);
                // EXITING the boat: the game drops you off at a NEW world position, but the view anchor
                // still holds the roomscale walk offset and the arm IK was suspended while the body was
                // hidden — so the body/IK land misaligned and never heal. Re-anchor exactly like a respawn:
                // reset the roomscale origin, rebuild the arm solvers/poles, and re-sync the body pose.
                if (!driving)
                {
                    try { VRRig.Instance?.OnRespawn(); } catch { }
                    try { ArmRig.Setup(_player); } catch { }
                }
            }

            // Body yaw = the VIEW yaw (rig yaw + head yaw) — exactly what the render camera shows. The
            // game camera transform's local yaw does NOT include the snap/smooth turn offset, so using it
            // left the body facing the old direction whenever you turned artificially ("snap turn fucks
            // up the whole thing"). The rig pose IS the rendered view, so the body now rotates with it
            // 1:1 — IRL turns AND snap/smooth turns alike.
            float viewYaw;
            float viewPitch;
            var rig = VRRig.Instance;
            if (rig != null && rig.Root != null && rig.Head != null)
            {
                viewYaw = (rig.Root.rotation * rig.Head.localRotation).eulerAngles.y;
                viewPitch = rig.Head.localRotation.eulerAngles.x;
            }
            else
            {
                var camEuler = _player.CamObject != null ? _player.CamObject.eulerAngles : Vector3.zero;
                viewYaw = camEuler.y;
                viewPitch = camEuler.x;
            }
            Quaternion yawOnly = Quaternion.Euler(0f, viewYaw, 0f);
            // Keep the turn pivot at the headset's floor projection, but restore the avatar's authored
            // body-space offset behind that pivot. Removing this spacing put the chest in front of the
            // headset, so the player viewed the body from behind. The offset is rotated by the already-
            // solved view yaw; this changes only where the body sits, not the snap/smooth-turn system.
            Vector3 bodyLocal = Vector3.zero;
            if (rig != null && rig.Head != null)
            {
                Vector3 headLocal = _player.Transform.InverseTransformPoint(rig.Head.position);
                bodyLocal.x = headLocal.x;
                bodyLocal.z = headLocal.z;
                bodyLocal += _player.Transform.InverseTransformDirection(yawOnly * VRConfig.BodyOffset.Value);
                bodyLocal.y = 0f;
            }
            _other._transform.localPosition = bodyLocal;
            _other._transform.localRotation = Quaternion.Inverse(_player.Transform.rotation) * yawOnly;
            if (_other._camProxy != null)
                _other._camProxy.localRotation = Quaternion.Euler(viewPitch, 0f, 0f);
        }
        catch { }
    }
}

/// <summary>
/// VR arm IK wiring (BO2-style). The restored body's arms are solved by the game's FABRIK solvers
/// (IK.cs, chain = upper arm + forearm). We point each solver at the controller-driven hand bone, place
/// elbow pole transforms below/behind the shoulders so elbows bend down naturally, and disable stretch so
/// arms never elongate (no "arm reach").
/// </summary>
internal static class ArmRig
{
    private static IK _ikRight;
    private static IK _ikLeft;
    private static Transform _poleRight;
    private static Transform _poleLeft;
    private static Player _player;

    // Per-arm pole smoothing state (reset on Setup so a rebuild — respawn/revive/boat-exit/rejoin —
    // starts clean and can't inherit a stale pole from the previous body).
    private static Vector3 _prevPoleR, _prevPoleL;
    private static bool _havePrevR, _havePrevL;
    private const float ElbowPoleFollow = 50f;

    // Dynamic elbow-pole tunables (VRArmIK-derived, cross-verified against FRIK/VRIK/LCVR — see
    // VRRef\VR-FullBody-IK\RESEARCH). All in the torso frame; the pole is built as a DIRECTION so it can
    // never point the elbow inward or up by accident.
    private const float ElbowZDistanceStart = 0.6f;   // hand-forward distance below which the fore/aft push kicks in
    private const float ElbowXDistanceStart = 0.1f;

    private static IK _lastIkR, _lastIkL;

    // Shoulder reach correction is applied in CLAVICLE-LOCAL space. Tracking the exact last local pose
    // lets us distinguish "our previous correction is still present" from "the animator already wrote a
    // fresh base pose". The previous world-space blind undo became invalid when snap turning rotated the
    // body, which is why repeated snaps could progressively twist/misplace the arm chain.
    private static readonly Quaternion[] _shoulderLocalDelta = { Quaternion.identity, Quaternion.identity };
    private static readonly Quaternion[] _shoulderLastAppliedLocal = { Quaternion.identity, Quaternion.identity };
    private static readonly bool[] _shoulderApplied = new bool[2];

    /// <summary>Called on body build / re-show. Resets pole smoothing and delegates to the per-frame,
    /// fully self-healing <see cref="Refresh"/> — a single code path keeps the IK correct after ANY event
    /// (death/respawn, boat enter/exit, recenter, rejoin), so it can never stay broken.</summary>
    internal static void Setup(Player player)
    {
        RestoreShoulderBase(_ikLeft, 0);
        RestoreShoulderBase(_ikRight, 1);
        _player = player;
        _havePrevR = _havePrevL = false;
        _shoulderLocalDelta[0] = _shoulderLocalDelta[1] = Quaternion.identity;
        _shoulderApplied[0] = _shoulderApplied[1] = false;
        Refresh(player);
    }

    /// <summary>Called from the final pre-render pass after every Animator and LateUpdate writer. Solving
    /// here removes Unity's undefined component ordering from the equation, so the final visible arm uses
    /// this frame's controller/grip pose.</summary>
    internal static void SolveCurrentPose(Player player)
    {
        if (!HowToFishBody.Active || player == null || player != HowToFishBody.BoundPlayer) return;
        try
        {
            Refresh(player);
            _ikRight?.ResolveIK();
            _ikLeft?.ResolveIK();
        }
        catch { }
    }

    /// <summary>Discard temporal state before an instantaneous playspace rotation. The next same-frame
    /// solve computes fresh elbow poles in the post-turn frame instead of blending from pre-turn space.</summary>
    internal static void PrepareForSnapTurn()
    {
        try
        {
            RestoreShoulderBase(_ikLeft, 0);
            RestoreShoulderBase(_ikRight, 1);
            if (_ikLeft != null) _ikLeft._stopResolving = false;
            if (_ikRight != null) _ikRight._stopResolving = false;
        }
        catch { }
        _havePrevR = _havePrevL = false;
    }

    /// <summary>
    /// Per-frame, fully SELF-HEALING IK maintenance. Re-fetches the arm solvers from the (possibly rebuilt)
    /// player every frame, re-creates the elbow poles if a rebuild destroyed them, re-points the targets at
    /// the current hand bones, and re-asserts the config. Because everything is re-derived each frame, the
    /// IK can't stay broken after death/respawn, boat enter/exit, recenter, or rejoin — the exact moments
    /// the game re-inits the arms and the old cached-reference wiring went stale ("IK gets messed up after
    /// dying / entering-exiting the boat"). Cheap.
    /// </summary>
    internal static void Refresh(Player player)
    {
        try
        {
            if (player == null) return;
            var arms = player.Arms;
            if (arms == null) return;

            // Re-fetch the solvers every frame. If they changed (body was rebuilt), reset the pole
            // smoothing so it doesn't lerp the elbow from a stale previous-body pose.
            IK ikR = arms._ikRight, ikL = arms._ikLeft;
            if (ikR == null || ikL == null) return;
            if (ikR != _lastIkR || ikL != _lastIkL)
            {
                RestoreShoulderBase(_ikLeft, 0);
                RestoreShoulderBase(_ikRight, 1);
                _havePrevR = _havePrevL = false;
                _lastIkR = ikR;
                _lastIkL = ikL;
            }
            _ikRight = ikR; _ikLeft = ikL;

            // Ensure the elbow pole transforms exist — a rebuild can destroy the old ones (Unity fake-null).
            if (_poleRight == null || _poleLeft == null)
            {
                if (_poleRight == null) _poleRight = new GameObject("VRRightElbowPole").transform;
                if (_poleLeft == null) _poleLeft = new GameObject("VRLeftElbowPole").transform;
                try { _poleRight.SetParent(player.Transform, false); _poleLeft.SetParent(player.Transform, false); } catch { }
                _havePrevR = _havePrevL = false;
            }

            var hands = player.Hands;
            if (hands == null || hands.HandBoneRight == null || hands.HandBoneLeft == null) return;

            _ikRight.Target = hands.HandBoneRight;
            _ikLeft.Target = hands.HandBoneLeft;
            if (!_ikRight.enabled) _ikRight.enabled = true;
            if (!_ikLeft.enabled) _ikLeft.enabled = true;
            _ikRight._enableStretch = false; _ikRight._snapBackStrength = 0f; _ikRight._iterations = 6; _ikRight._pole = _poleRight;
            _ikLeft._enableStretch = false;  _ikLeft._snapBackStrength = 0f;  _ikLeft._iterations = 6;  _ikLeft._pole = _poleLeft;

            PlacePoles(player);
        }
        catch { }
    }

    /// <summary>
    /// DYNAMIC elbow-pole placement — the rewrite based on VR-FullBody-IK research (VRArmIK + FRIK +
    /// VRIK + LCVR all converge here). A STATIC pole collapses the elbow inward when the hand moves
    /// fore/aft; the fix is to compute the elbow bend DIRECTION every frame, anchored to the TORSO,
    /// biased out+down+back, and pushed further OUTWARD as the hand pulls back toward the body (the
    /// fore/aft term) or crosses the midline, with a fixed fallback near the shoulder singularity and
    /// temporal smoothing. Built as a direction so it can never point the elbow inward or up by accident.
    /// </summary>
    private static void PlacePoles(Player player)
    {
        try
        {
            Transform body = (player.Other != null && player.Other._transform != null)
                ? player.Other._transform : player.Transform;
            // Clean yaw-only torso basis: forward = view facing, up = world up, right = body-right.
            Quaternion torso = Quaternion.Euler(0f, body.eulerAngles.y, 0f);
            PlaceOnePole(_ikRight, _poleRight, torso, left: false, ref _prevPoleR, ref _havePrevR);
            PlaceOnePole(_ikLeft,  _poleLeft,  torso, left: true,  ref _prevPoleL, ref _havePrevL);
        }
        catch { }
    }

    private static void PlaceOnePole(IK ik, Transform pole, Quaternion torso, bool left,
                                     ref Vector3 prev, ref bool havePrev)
    {
        if (ik == null || pole == null) return;
        var bones = ik._bones;
        if (bones == null || bones.Length < 3 || bones[0] == null || bones[1] == null || bones[2] == null) return;
        Vector3 handPos = ik.Target != null ? ik.Target.position : bones[2].position;
        Quaternion handRot = ik.Target != null ? ik.Target.rotation : bones[2].rotation;
        float armLength = Vector3.Distance(bones[0].position, bones[1].position)
                        + Vector3.Distance(bones[1].position, bones[2].position);
        if (armLength < 1e-4f) return;

        // FRIK shoulder reach-slide: before reading the socket, slide the shoulder toward the hand on long
        // reaches (rotating the clavicle so there's no gap). This is the other half of the natural-arm look.
        SlideShoulder(bones[0], handPos, armLength, left);

        Vector3 shoulderPos = bones[0].position; // re-read — the slide may have moved the socket
        Vector3 target = ComputeElbowPole(shoulderPos, handPos, torso, handRot, armLength, left);
        // Fast exponential filtering removes controller micro-jitter without making the elbow trail the
        // hand. The previous 25/s filter had a visible multi-frame tail; 50/s settles within a VR frame
        // or two while remaining continuous.
        if (havePrev) target = Vector3.Lerp(prev, target, 1f - Mathf.Exp(-ElbowPoleFollow * Time.unscaledDeltaTime));
        prev = target; havePrev = true;
        pole.position = target;
    }

    /// <summary>
    /// Natural elbow-pole world position. Captures the FRIK/VRIK INTENT (DOWN is the base, outward/back are
    /// small, modulated by where the hand is) but computed DIRECTLY in the torso frame so it is manifestly
    /// correct in Unity Y-up — no coordinate-port sign traps. The earlier direct Z-up→Y-up port of FRIK's
    /// Skeleton.cpp had mirrored-axis sign bugs that made the elbows INVERT (bend up/backward). Here the
    /// down component is always -Y and only the OUTWARD X is mirrored per arm, so the elbow can never invert:
    /// right arm -> down+right+back, left arm -> down+left+back.
    /// </summary>
    internal static Vector3 ComputeElbowPole(Vector3 shoulderPos, Vector3 handPos, Quaternion torso,
                                            Quaternion handRot, float armLength, bool left)
    {
        float outSign = left ? -1f : 1f;                       // +X = body-right; outward is +X right, -X left
        float invLen = 1f / Mathf.Max(armLength, 1e-3f);
        // Hand in torso-local space (x=right, y=up, z=forward), normalized by arm length.
        Vector3 h = Quaternion.Inverse(torso) * (handPos - shoulderPos) * invLen;

        // DOWN dominates (anti-chicken-wing AND anti-invert); outward + back are small corrections.
        float downAmt = 0.85f, outAmt = 0.22f, backAmt = 0.28f;
        // Pull-in (hand drawn back toward the torso): drop the elbow more and push it out a little — the
        // classic fore/aft collapse cure (VRArmIK Z-term).
        float pullIn = Mathf.Clamp01(0.55f - h.z);
        downAmt += pullIn * 0.20f; outAmt += pullIn * 0.25f;
        // Across the midline (hand crossed to the far side): push the elbow further OUT so it doesn't fold in.
        float across = Mathf.Max(-outSign * h.x, 0f);
        outAmt += across * 0.55f;
        // Hand high: lift the elbow (less down) so it doesn't clip through the torso, never below a floor.
        downAmt = Mathf.Max(downAmt - Mathf.Clamp01(h.y) * 0.45f, 0.15f);

        // Build the direction in the torso frame — down is -Y for BOTH arms (cannot invert), outward mirrored.
        Vector3 dir = (torso * new Vector3(outSign * outAmt, -downAmt, -backAmt)).normalized;

        // Pole out along dir from the arm midpoint; the game's FABRIK only uses the DIRECTION.
        return (shoulderPos + handPos) * 0.5f + dir * (armLength * 0.5f);
    }

    /// <summary>
    /// FRIK shoulder reach-slide (Skeleton.cpp:1045-1059): on a long reach, slide the shoulder SOCKET
    /// toward the hand by up to 8% of arm length (ramping in past 50% reach), by rotating the CLAVICLE
    /// (the upper arm's parent) so the whole arm root turns — no gap opens between torso and arm. This is
    /// the half VRIK/FRIK add on top of the elbow that makes the arm read as a real shoulder instead of a
    /// pinned socket. Runs before the game's FABRIK solve, so the solver reaches from the slid socket.
    ///
    /// Accumulation-safe (I can't test in-headset): each frame we UNDO last frame's applied rotation first,
    /// so we always build on the animation's fresh clavicle pose; even if execution order differs, the error
    /// is bounded to one small (a few-degree) delta and self-corrects next frame. Skips if the "clavicle"
    /// looks like the chest/spine (more than 2 children) so we never rotate the torso by mistake.
    /// </summary>
    private static void SlideShoulder(Transform upperArm, Vector3 handPos, float armLength, bool left)
    {
        try
        {
            var clavicle = upperArm.parent;
            if (clavicle == null) return;
            if (clavicle.childCount > 2) return; // not a clavicle (chest/spine has arms+neck) — don't tilt the torso
            int i = left ? 0 : 1;

            // Restore the animation/base pose only when our exact previous local result is still present.
            // If the animator has already refreshed the clavicle, its new local pose is the base and must
            // not have an unrelated previous-frame delta removed from it.
            RestoreShoulderBase(ik: left ? _ikLeft : _ikRight, i);

            Vector3 socket = upperArm.position;
            float dist = Vector3.Distance(handPos, socket);
            float span = armLength * 0.85f;
            float adjust = span > 1e-4f ? Mathf.Clamp(dist - armLength * 0.5f, 0f, span) / span : 0f;
            if (adjust <= 1e-4f) return; // within 50% reach — no slide (socket stays on the body)

            Vector3 desired = socket + (handPos - socket).normalized * (adjust * armLength * 0.08f);
            Vector3 curDir = socket - clavicle.position;
            Vector3 newDir = desired - clavicle.position;
            if (curDir.sqrMagnitude < 1e-6f || newDir.sqrMagnitude < 1e-6f) return;
            Quaternion worldDelta = Quaternion.FromToRotation(curDir, newDir);
            Quaternion parentRot = clavicle.parent != null ? clavicle.parent.rotation : Quaternion.identity;
            Quaternion localDelta = Quaternion.Inverse(parentRot) * worldDelta * parentRot;
            clavicle.localRotation = localDelta * clavicle.localRotation;
            _shoulderLocalDelta[i] = localDelta;
            _shoulderLastAppliedLocal[i] = clavicle.localRotation;
            _shoulderApplied[i] = true;
        }
        catch { }
    }

    private static void RestoreShoulderBase(IK ik, int i)
    {
        try
        {
            var bones = ik != null ? ik._bones : null;
            var clavicle = bones != null && bones.Length > 0 && bones[0] != null ? bones[0].parent : null;
            if (clavicle != null && _shoulderApplied[i] &&
                Quaternion.Angle(clavicle.localRotation, _shoulderLastAppliedLocal[i]) < 0.01f)
                clavicle.localRotation = Quaternion.Inverse(_shoulderLocalDelta[i]) * clavicle.localRotation;
        }
        catch { }
        _shoulderLocalDelta[i] = Quaternion.identity;
        _shoulderApplied[i] = false;
    }
}
