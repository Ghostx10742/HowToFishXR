using System.Collections.Generic;
using HowToFishVR.Net;
using UnityEngine;

namespace HowToFishVR.Body;

/// <summary>
/// Applies received VR poses (<see cref="VRNetSync"/>) to REMOTE player bodies so every client with the
/// mod — VR or flatscreen — sees a VR player's body exactly as the player sees it: one-hand grips,
/// two-hand grips, hands at the sides, reaching, etc. Grip state is already encoded in the broadcast
/// hand poses. The body yaw, body offset and feet are applied by <see cref="Patches.RemoteOtherPlayerPatches"/>
/// and <see cref="Patches.RemoteLegsPatches"/>; this component handles the hands/arms.
///
/// The mechanism mirrors the LOCAL body driver (BO2-style arms): the remote player's own arm IK
/// (<c>PlayerArms.SetIKTarget</c>, which the game already points at the hand bones) is fed by the
/// hand bones, so setting the remote hand bones to the received world poses makes the FABRIK bend the
/// upper/forearm to reach them; elbow poles below the shoulders keep the elbows bending down naturally.
/// Head look needs no work — the base game already replicates the camera yaw/pitch (SetReceivedPosRot).
///
/// Runs on EVERY modded client regardless of <c>Plugin.VREnabled</c>: a flatscreen player with the mod
/// still sees VR players' arms. Runs at a late execution order so it wins over the game's own
/// PlayerHands.LateUpdate animation placement.
/// </summary>
[DefaultExecutionOrder(500)]
public class RemoteBodyDriver : MonoBehaviour
{
    private class RemotePlayer
    {
        public Player Player;
        public Transform Root;
        public Transform HandR;
        public Transform HandL;
        public IK IkR;
        public IK IkL;
        public Transform PoleR;
        public Transform PoleL;
        // The remote's held item we froze (kinematic) to pin it — restored the moment the pose stops
        // carrying it so the game's physics takes over again.
        public Item PinnedItem;
    }

    private readonly Dictionary<int, RemotePlayer> _players = new Dictionary<int, RemotePlayer>();
    private float _rescanTimer = 0.5f;

    public static RemoteBodyDriver Create()
    {
        var go = new GameObject("HowToFishVR RemoteBodyDriver");
        DontDestroyOnLoad(go);
        return go.AddComponent<RemoteBodyDriver>();
    }

    private void LateUpdate()
    {
        try
        {
            if (!VRConfig.MultiplayerVRSync.Value) return;

            _rescanTimer -= Time.unscaledDeltaTime;
            if (_rescanTimer <= 0f)
            {
                _rescanTimer = 1.5f;
                Rescan();
            }

            foreach (var kv in _players)
            {
                var rp = kv.Value;
                if (rp == null || rp.Root == null) continue;
                if (!VRNetSync.TryGetInterpolatedPose(kv.Key, out var pose)) continue;

                // Skip dead players (ragdoll takes over) and boat players (the seat model takes over;
                // the game positions boat riders boat-relative). NOTE: the game tracks a REMOTE player's
                // boat state in OtherPlayer.OnBoat (SetReceivedPosRot onBoat flag) — PlayerMovement.OnBoat
                // is only set for the local player, so gating on it alone never fired and remote boat
                // players were pose-driven to a garbage position ("invisible on the boat").
                try
                {
                    if (rp.Player.Dying != null && rp.Player.Dying.IsDead) continue;
                    if ((rp.Player.Other != null && rp.Player.Other.OnBoat) ||
                        (rp.Player.Movement != null && rp.Player.Movement.OnBoat)) continue;
                }
                catch { }

                // SNAPSHOT-INTERPOLATED absolute body root: drive the remote body root straight to it every
                // frame (smooth between the game's tick-rate network callbacks), and compose hands/feet/item
                // against the SAME interpolated frame so everything moves together with zero trailing lag.
                var frameRot = Quaternion.Euler(0f, pose.BodyYaw, 0f);
                var framePos = pose.BodyPos;
                rp.Root.SetPositionAndRotation(framePos, frameRot);

                Vector3 rPos = framePos + frameRot * pose.RightPos;
                Quaternion rRot = frameRot * pose.RightRot;
                Vector3 lPos = framePos + frameRot * pose.LeftPos;
                Quaternion lRot = frameRot * pose.LeftRot;

                if (rp.HandR != null) rp.HandR.SetPositionAndRotation(rPos, rRot);
                if (rp.HandL != null) rp.HandL.SetPositionAndRotation(lPos, lRot);

                // Held item (gun/rod AND fish/food/body): pin the remote player's held item to the
                // broadcast pose so it appears where the VR player actually holds it (one/two-hand).
                // Runs in this LateUpdate AFTER the game glued the item to the flat camera spot, so our
                // placement wins for the rendered frame. Non-tool items are physics-steered by the game
                // on the receiver too (PlayerHolding.FixedUpdate -> MoveItemToHoldPosRot), so we freeze
                // the root rigidbody (kinematic + zeroed velocities, mirroring the local VRHeldItem) and
                // restore it when the pose stops carrying the item.
                try
                {
                    var remoteHeld = rp.Player.Holding != null ? rp.Player.Holding.HeldItem : null;
                    if (pose.Held && remoteHeld != null && remoteHeld.transform != null)
                    {
                        remoteHeld.transform.SetPositionAndRotation(
                            framePos + frameRot * pose.HeldPos,
                            frameRot * pose.HeldRot);
                        if (!pose.HeldIsTool && remoteHeld.Rig != null)
                        {
                            if (rp.PinnedItem != remoteHeld)
                            {
                                RestorePinned(rp);
                                rp.PinnedItem = remoteHeld;
                                try { remoteHeld.Rig.isKinematic = true; } catch { }
                            }
                            // NOTE: don't zero velocity here — the body is kinematic, which ignores velocity
                            // and logs a "Setting velocity of a kinematic body is not supported" warning per
                            // frame (log spam / lag). Kinematic already means it won't drift.
                        }
                        else if (rp.PinnedItem != null)
                        {
                            RestorePinned(rp);
                        }
                    }
                    else if (rp.PinnedItem != null)
                    {
                        RestorePinned(rp);
                    }

                    // Gun laser-sight beam: draw the remote's laser EXACTLY where the VR player's laser
                    // points (the broadcast beam), so other players see the same red line — not whatever
                    // the receiver's own raycast happens to produce. Written after the held-tool pin so
                    // the emitter (a child of the gun) is already in place.
                    if (pose.LaserActive && remoteHeld != null && remoteHeld.Weapon != null &&
                        remoteHeld.Weapon.Attachments != null)
                    {
                        var ls = remoteHeld.Weapon.Attachments._laserSight;
                        if (ls != null && ls._line != null)
                        {
                            if (!ls.enabled) ls.enabled = true;
                            if (!ls._line.enabled) ls._line.enabled = true;
                            if (ls._line.positionCount < 2) ls._line.positionCount = 2;
                            ls._line.SetPosition(0, framePos + frameRot * pose.LaserStart);
                            ls._line.SetPosition(1, framePos + frameRot * pose.LaserEnd);
                        }
                    }

                    // Fishing-rod line: DO NOT override it. The game's FishingRod.LateUpdate already redraws
                    // the line every frame from its own _linePoints (rod tip -> ... -> bait) for remote
                    // holders too (it even calls SetReceivedBaitPos()). Because we reposition the whole rod
                    // to the broadcast pose here, the rod TIP is already correct, and the bait is
                    // replicated — so the game's native draw connects them correctly. Our old broadcast-line
                    // override was a second, fragile frame-composition layered on top of that native draw,
                    // and a small frame/timing mismatch threw the far bait point (10m+ when cast) way off
                    // the rod. Letting the native line stand fixes "the rod line is way off the rod".
                }
                catch { }

                // Re-point the game's arm IK at the hand bones (enables them) and tune it like the
                // local BO2 arms: no stretch, no snap-back, elbow pole below the shoulder.
                if (rp.IkR != null && rp.IkL != null)
                {
                    rp.IkR.Target = rp.HandR;
                    rp.IkL.Target = rp.HandL;
                    if (!rp.IkR.enabled) rp.IkR.enabled = true;
                    if (!rp.IkL.enabled) rp.IkL.enabled = true;
                    rp.IkR._enableStretch = false;
                    rp.IkL._enableStretch = false;
                    rp.IkR._snapBackStrength = 0f;
                    rp.IkL._snapBackStrength = 0f;
                    rp.IkR._iterations = 6;
                    rp.IkL._iterations = 6;
                    PlacePoles(rp);
                }
            }
        }
        catch { }
    }

    private static void RestorePinned(RemotePlayer rp)
    {
        if (rp.PinnedItem == null) return;
        try { if (rp.PinnedItem.Rig != null && rp.PinnedItem.Rig.isKinematic) rp.PinnedItem.Rig.isKinematic = false; } catch { }
        rp.PinnedItem = null;
    }

    private void Rescan()
    {
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<Player>(true);
            var seen = new HashSet<int>();
            foreach (var p in all)
            {
                if (p == null || p == Player.LocalPlayer) continue;
                var no = p.NetworkObject;
                if (no == null) continue;
                int id = no.ObjectId;
                seen.Add(id);
                if (_players.ContainsKey(id)) continue;
                if (!VRNetSync.TryGetPose(id, out _)) continue; // only track players that broadcast VR poses

                var hands = p.Hands;
                if (hands == null) continue;
                // Root = the remote BODY transform (p.Other.Transform) — the frame the sender composed
                // poses against and the transform the game moves to the synced position. The player root
                // (p.Transform) is a different object that isn't positioned by the body sync.
                Transform root = p.Other != null && p.Other.Transform != null ? p.Other.Transform : p.Transform;
                var rp = new RemotePlayer
                {
                    Player = p,
                    Root = root,
                    HandR = hands.HandBoneRight,
                    HandL = hands.HandBoneLeft,
                    IkR = p.Arms != null ? p.Arms._ikRight : null,
                    IkL = p.Arms != null ? p.Arms._ikLeft : null,
                };
                if (rp.HandR == null || rp.HandL == null || rp.IkR == null || rp.IkL == null) continue;

                var pr = new GameObject("VR Remote Elbow Pole R").transform;
                pr.SetParent(p.Transform, false);
                var pl = new GameObject("VR Remote Elbow Pole L").transform;
                pl.SetParent(p.Transform, false);
                rp.PoleR = pr;
                rp.PoleL = pl;

                _players[id] = rp;
            }

            // Drop records for players that left / no longer broadcast.
            var dead = new List<int>();
            foreach (var id in _players.Keys)
                if (!seen.Contains(id)) dead.Add(id);
            foreach (var id in dead)
            {
                if (_players.TryGetValue(id, out var gone))
                {
                    RestorePinned(gone);
                    _players.Remove(id);
                }
            }
        }
        catch { }
    }

    private static void PlacePoles(RemotePlayer rp)
    {
        try
        {
            // Same DYNAMIC, research-based elbow pole as the local player (ArmRig.ComputeElbowPole) so a
            // remote VR player's synced arms bend as naturally as their own — no more inward collapse on
            // the received poses. Torso basis = the body's yaw (rp.Root is pinned to the broadcast yaw).
            Quaternion torso = Quaternion.Euler(0f, rp.Root.eulerAngles.y, 0f);
            PlaceOnePole(rp.IkR, rp.PoleR, torso, left: false);
            PlaceOnePole(rp.IkL, rp.PoleL, torso, left: true);
        }
        catch { }
    }

    private static void PlaceOnePole(IK ik, Transform pole, Quaternion torso, bool left)
    {
        if (ik == null || pole == null) return;
        var bones = ik._bones;
        if (bones == null || bones.Length < 3 || bones[0] == null || bones[1] == null || bones[2] == null) return;
        Vector3 shoulderPos = bones[0].position;
        Vector3 handPos = ik.Target != null ? ik.Target.position : bones[2].position;
        Quaternion handRot = ik.Target != null ? ik.Target.rotation : bones[2].rotation;
        float armLength = Vector3.Distance(bones[0].position, bones[1].position)
                        + Vector3.Distance(bones[1].position, bones[2].position);
        if (armLength < 1e-4f) return;
        pole.position = ArmRig.ComputeElbowPole(shoulderPos, handPos, torso, handRot, armLength, left);
    }
}
