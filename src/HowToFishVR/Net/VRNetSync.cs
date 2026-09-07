using System;
using System.Collections.Generic;
using FishNet;
using FishNet.Connection;
using FishNet.Transporting;
using HowToFishVR.Body;
using HowToFishVR.Input;
using HowToFishVR.VR;
using UnityEngine;

namespace HowToFishVR.Net;

/// <summary>
/// VR pose syncing transport (MAVR-style, using the game's own FishNet). Broadcasts ride the game's
/// existing transport so no game code is touched:
///  1. Client sends <see cref="VRHelloBroadcast"/> (reliable, every 3s) with its player ObjectId.
///  2. The host's ServerManager — running the mod — answers <see cref="VRHelloResponse"/> and adds the
///     connection to the relay set. A vanilla host never answers, so no pose spam ever reaches it.
///  3. VR clients then stream <see cref="VRPoseBroadcast"/> (unreliable, ~VRSyncRate Hz).
///  4. The server relays each pose to every OTHER modded connection and stores it locally (so the host
///     sees remote VR players too). Receivers store the latest pose per ObjectId; RemoteBodyDriver
///     applies them to the remote player bodies.
///
/// Runs on every modded client regardless of <c>Plugin.VREnabled</c> — the receiving + relaying side is
/// IDENTICAL for VR and flatscreen players (MAVR-style): a flatscreen player with the mod still relays
/// and still SEES VR players' full bodies (hands, arms, feet, body yaw). Only the pose SENDING
/// requires an active VR session — flatscreen players have no poses to broadcast, and the game already
/// syncs their normal bodies to everyone else.
/// </summary>
public class VRNetSync : MonoBehaviour
{
    public static VRNetSync Instance { get; private set; }

    private readonly HashSet<NetworkConnection> _modded = new HashSet<NetworkConnection>();
    private static readonly Dictionary<int, VRPoseBroadcast> _latest = new Dictionary<int, VRPoseBroadcast>();
    private static readonly Dictionary<int, float> _lastSeen = new Dictionary<int, float>();

    // ---- Snapshot-interpolation buffers ----
    // Poses arrive tick-capped (~16-18Hz on FishNet regardless of send rate), so the receiver renders the
    // remote body a fraction of a second IN THE PAST and interpolates between the two bracketing snapshots
    // (entity/snapshot interpolation). This is smooth AND low-latency — unlike exponential smoothing, it
    // adds only one small, constant, chosen delay instead of a variable trailing lag, and it's immune to
    // packet jitter/loss. Brief capped extrapolation covers a missed packet without popping.
    private struct Snap { public float T; public VRPoseBroadcast P; }
    private static readonly Dictionary<int, System.Collections.Generic.List<Snap>> _buffers = new Dictionary<int, System.Collections.Generic.List<Snap>>();
    private static readonly Dictionary<int, float> _avgInterval = new Dictionary<int, float>();

    private bool _clientRegistered;
    private bool _serverRegistered;
    private bool _relayActive;   // hello response received -> safe to stream poses
    private float _helloTimer;
    private float _poseTimer;
    private int _myObjectId = -1;

    private const float POSE_STALE = 1.5f; // poses older than this are treated as gone

    public static VRNetSync Create()
    {
        var go = new GameObject("HowToFishVR NetSync");
        DontDestroyOnLoad(go);
        return go.AddComponent<VRNetSync>();
    }

    private void Awake()
    {
        Instance = this;
        VRBroadcastSerializers.Register();
    }

    /// <summary>Latest pose for a player ObjectId, or false if none / stale (older than ~1.5s). Used for
    /// existence/state gating; use <see cref="TryGetInterpolatedPose"/> to APPLY a pose to a remote body.</summary>
    public static bool TryGetPose(int objectId, out VRPoseBroadcast pose)
    {
        pose = default;
        if (!_latest.TryGetValue(objectId, out pose)) return false;
        if (_lastSeen.TryGetValue(objectId, out var t) && Time.unscaledTime - t > POSE_STALE) return false;
        return true;
    }

    /// <summary>Record a received remote pose into its interpolation buffer (timestamped, trimmed).</summary>
    private static void AddSample(int id, VRPoseBroadcast p)
    {
        float now = Time.unscaledTime;
        if (!_buffers.TryGetValue(id, out var buf)) { buf = new System.Collections.Generic.List<Snap>(8); _buffers[id] = buf; }
        if (buf.Count > 0)
        {
            float dt = now - buf[buf.Count - 1].T;
            if (dt > 0.0005f && dt < 1f)
            {
                _avgInterval.TryGetValue(id, out var ai);
                _avgInterval[id] = ai > 0f ? Mathf.Lerp(ai, dt, 0.2f) : dt;
            }
        }
        buf.Add(new Snap { T = now, P = p });
        // Trim samples older than ~0.6s, but always keep the last two so interpolation/extrapolation works.
        float cutoff = now - 0.6f;
        int rm = 0;
        while (rm < buf.Count - 2 && buf[rm].T < cutoff) rm++;
        if (rm > 0) buf.RemoveRange(0, rm);
    }

    /// <summary>
    /// The remote pose to RENDER this frame: snapshot-interpolated at (now - interpolationDelay), where
    /// the delay is ~1.4x the observed packet interval (so two snapshots always bracket the render time).
    /// Between snapshots it lerps; past the newest it briefly extrapolates (capped at one interval) to
    /// cover a late/lost packet without popping. This is the smooth+responsive replacement for the old
    /// per-receiver exponential smoothing.
    /// </summary>
    public static bool TryGetInterpolatedPose(int objectId, out VRPoseBroadcast pose)
    {
        pose = default;
        if (!_buffers.TryGetValue(objectId, out var buf) || buf.Count == 0) return false;
        float now = Time.unscaledTime;
        if (now - buf[buf.Count - 1].T > POSE_STALE) return false; // sender gone

        float interval = _avgInterval.TryGetValue(objectId, out var ai) && ai > 0f ? ai : 1f / 16f;
        float delay = Mathf.Clamp(interval * 1.4f, 0.04f, 0.14f);
        float renderT = now - delay;

        if (buf.Count == 1 || renderT <= buf[0].T) { pose = buf[0].P; return true; }
        for (int i = buf.Count - 1; i >= 1; i--)
        {
            if (buf[i - 1].T <= renderT && renderT <= buf[i].T)
            {
                float span = buf[i].T - buf[i - 1].T;
                float f = span > 1e-5f ? (renderT - buf[i - 1].T) / span : 1f;
                pose = LerpPose(buf[i - 1].P, buf[i].P, f);
                return true;
            }
        }
        // renderT is past the newest snapshot -> extrapolate from the last two, capped at one interval.
        var a = buf[buf.Count - 2]; var b = buf[buf.Count - 1];
        float sp = b.T - a.T;
        float over = sp > 1e-5f ? Mathf.Min((renderT - b.T) / sp, 1f) : 0f;
        pose = LerpPose(a.P, b.P, 1f + over);
        return true;
    }

    /// <summary>Interpolate/extrapolate a pose (t may exceed [0,1] for capped extrapolation). Continuous
    /// fields are lerped; discrete/state fields take the newer snapshot.</summary>
    private static VRPoseBroadcast LerpPose(VRPoseBroadcast a, VRPoseBroadcast b, float t)
    {
        return new VRPoseBroadcast
        {
            ObjectId = b.ObjectId,
            BodyYaw = a.BodyYaw + Mathf.DeltaAngle(a.BodyYaw, b.BodyYaw) * t, // unclamped, wrap-safe
            BodyPos = Vector3.LerpUnclamped(a.BodyPos, b.BodyPos, t),
            HasBody = b.HasBody,
            LeftPos = Vector3.LerpUnclamped(a.LeftPos, b.LeftPos, t),
            LeftRot = Quaternion.SlerpUnclamped(a.LeftRot, b.LeftRot, t),
            RightPos = Vector3.LerpUnclamped(a.RightPos, b.RightPos, t),
            RightRot = Quaternion.SlerpUnclamped(a.RightRot, b.RightRot, t),
            FootLPos = Vector3.LerpUnclamped(a.FootLPos, b.FootLPos, t),
            FootLRot = Quaternion.SlerpUnclamped(a.FootLRot, b.FootLRot, t),
            FootLPole = Vector3.LerpUnclamped(a.FootLPole, b.FootLPole, t),
            FootRPos = Vector3.LerpUnclamped(a.FootRPos, b.FootRPos, t),
            FootRRot = Quaternion.SlerpUnclamped(a.FootRRot, b.FootRRot, t),
            FootRPole = Vector3.LerpUnclamped(a.FootRPole, b.FootRPole, t),
            Held = b.Held,
            HeldIsTool = b.HeldIsTool,
            HeldPos = Vector3.LerpUnclamped(a.HeldPos, b.HeldPos, t),
            HeldRot = Quaternion.SlerpUnclamped(a.HeldRot, b.HeldRot, t),
            LaserActive = b.LaserActive,
            LaserStart = Vector3.LerpUnclamped(a.LaserStart, b.LaserStart, t),
            LaserEnd = Vector3.LerpUnclamped(a.LaserEnd, b.LaserEnd, t),
            LineCount = b.LineCount,
            Line0 = b.Line0, Line1 = b.Line1, Line2 = b.Line2, Line3 = b.Line3,
        };
    }

    private void Update()
    {
        try
        {
            if (VRConfig.MultiplayerVRSync.Value) EnsureRegistered();
            if (!VRConfig.MultiplayerVRSync.Value) return;

            // The HOST's own client always has the mod (it IS the modded server) — its loopback
            // hello/response handshake can be dropped by FishNet's auth filter, which left the host
            // silent (clients never saw the host). Force the host's client to stream unconditionally.
            try { if (InstanceFinder.IsServerStarted) _relayActive = true; } catch { }

            SendHello();
            if (_relayActive && Plugin.VREnabled) SendPose();
            SweepStale();
        }
        catch { }
    }

    private void EnsureRegistered()
    {
        try
        {
            if (!_clientRegistered && InstanceFinder.ClientManager != null && InstanceFinder.IsClientStarted)
            {
                InstanceFinder.ClientManager.RegisterBroadcast<VRHelloResponse>(OnClientHelloResponse);
                InstanceFinder.ClientManager.RegisterBroadcast<VRPoseBroadcast>(OnClientPose);
                _clientRegistered = true;
            }
            if (!_serverRegistered && InstanceFinder.ServerManager != null && InstanceFinder.IsServerStarted)
            {
                InstanceFinder.ServerManager.RegisterBroadcast<VRHelloBroadcast>(OnServerHello, true);
                InstanceFinder.ServerManager.RegisterBroadcast<VRPoseBroadcast>(OnServerPose, true);
                _serverRegistered = true;
            }
        }
        catch { }
    }

    private void SendHello()
    {
        _helloTimer -= Time.unscaledDeltaTime;
        if (_helloTimer > 0f) return;
        _helloTimer = 3f;
        try
        {
            if (!InstanceFinder.IsClientStarted) return;
            int id = GetMyObjectId();
            if (id < 0) return;
            InstanceFinder.ClientManager.Broadcast(new VRHelloBroadcast { ObjectId = id }, Channel.Reliable);
        }
        catch { }
    }

    private void SendPose()
    {
        _poseTimer -= Time.unscaledDeltaTime;
        if (_poseTimer > 0f) return;
        float rate = Mathf.Clamp(VRConfig.VRSyncRate.Value, 5f, 60f);
        _poseTimer = 1f / rate;
        try
        {
            var p = Player.LocalPlayer;
            if (p == null || p.Hands == null) return;
            if (p.Dying != null && p.Dying.IsDead) return;
            try { if (Boat.IsDrivingLocally) return; } catch { }

            // FLATSCREEN GUARD: only broadcast REAL VR poses. A genuine flatscreen session (Disable VR
            // config / --disable-vr) already has VREnabled=false and never reaches here, but OpenXR can
            // initialize without an actively-worn headset — in that case the "poses" are the game-
            // animated hand bones and a frozen HMD on a
            // desk, and broadcasting them makes the remote player look wrong to VR players. Require an
            // ACTIVE XR display session, a live tracked HMD device, AND that the game camera is actually
            // being driven by the headset (a player who chose VR but is playing on the monitor has the
            // camera following the mouse, not the HMD — mouse-look camera diverges from the HMD pose).
            try
            {
                if (!UnityEngine.XR.XRSettings.isDeviceActive) return;
                if (!UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.CenterEye).isValid) return;
                var rig = VRRig.Instance;
                if (rig != null && rig.Root != null && p.Camera != null && p.Camera.CamTransform != null)
                {
                    Quaternion hmdWorld = rig.Root.rotation * VRActions.Instance.HeadRot;
                    if (Mathf.Abs(Quaternion.Dot(p.Camera.CamTransform.rotation, hmdWorld)) < 0.8f) return;
                }
            }
            catch { return; }
            var l = p.Hands.HandBoneLeft;
            var r = p.Hands.HandBoneRight;
            if (l == null || r == null) return;
            if (p.Camera == null || p.Camera.CamTransform == null) return;

            // The exact root frame the game replicates (UpdatePlayerPosRot): origin = the player ROOT
            // position (or the body root when the full body is active — the receiver places the remote
            // body root at the synced root + the broadcast body offset, so the body root is the frame
            // that matches), rotation = the BODY'S WORLD YAW (the rendered view yaw incl. snap/smooth
            // turns). The game only syncs the camera's raw LOCAL yaw, so the body yaw is sent explicitly
            // and the receiver pins the remote body to it — hands/feet composed in the same frame land
            // exactly.
            bool hasBody = HowToFishBody.Active;
            Vector3 framePos = hasBody && p.Other != null && p.Other.Transform != null
                ? p.Other.Transform.position
                : (p.Transform != null ? p.Transform.position : Vector3.zero);
            float bodyYaw = p.Camera.CamTransform.eulerAngles.y;
            Quaternion frameRot = Quaternion.Euler(0f, bodyYaw, 0f);

            // ABSOLUTE body-root world position (== framePos: the frame everything else is composed
            // against). Broadcasting the absolute root — not a delta from the game-replicated player root —
            // lets the receiver snapshot-INTERPOLATE the whole body smoothly, decoupled from the steppy
            // tick-rate game replication. The receiver drives the remote body root straight to this
            // interpolated value and composes hands/feet/item against it.
            Vector3 bodyPos = framePos;

            // Held item (ANY item — gun/rod AND fish/food/body): the game only syncs held items at the
            // flatscreen camera-relative spot, so broadcast the item's actual VR pose (placed on the hand
            // by PlayerToolMovementPatches / VRHeldItem) in the same frame as the hands — the receiver
            // pins the remote's held item to it. Read in Update (one frame after the item patches placed
            // it) at the pose rate — that's fine.
            bool heldTool = false;
            bool heldIsTool = false;
            Vector3 heldPos = Vector3.zero;
            Quaternion heldRot = Quaternion.identity;
            bool laserActive = false;
            Vector3 laserStart = Vector3.zero;
            Vector3 laserEnd = Vector3.zero;
            int lineCount = 0;
            Vector3 l0 = Vector3.zero, l1 = Vector3.zero, l2 = Vector3.zero, l3 = Vector3.zero;
            try
            {
                var heldItem = p.Holding != null ? p.Holding.HeldItem : null;
                if (heldItem != null && heldItem.transform != null && heldItem.gameObject.activeInHierarchy)
                {
                    heldTool = true;
                    heldIsTool = heldItem.Tool != null;
                    heldPos = Quaternion.Inverse(frameRot) * (heldItem.transform.position - framePos);
                    heldRot = Quaternion.Inverse(frameRot) * heldItem.transform.rotation;

                    if (heldIsTool && heldItem.Tool != null)
                    {
                        // Gun laser-sight beam: the line the VR player actually sees (emitter -> hit/decal
                        // or max length). The receiver draws this exact beam instead of raycasting itself.
                        try
                        {
                            if (heldItem.Weapon != null && heldItem.Weapon.Attachments != null)
                            {
                                var ls = heldItem.Weapon.Attachments._laserSight;
                                if (ls != null && ls.enabled && ls._line != null && ls._line.enabled &&
                                    ls._line.positionCount >= 2)
                                {
                                    laserActive = true;
                                    laserStart = Quaternion.Inverse(frameRot) * (ls._line.GetPosition(0) - framePos);
                                    laserEnd = Quaternion.Inverse(frameRot) * (ls._line.GetPosition(1) - framePos);
                                }
                            }
                        }
                        catch { }

                        // Fishing-rod line: the actual line points (bait + rod guides) the VR player sees.
                        try
                        {
                            if (heldItem is FishingRod rod && rod._line != null && rod._line.enabled)
                            {
                                int n = Mathf.Clamp(rod._line.positionCount, 0, 4);
                                if (n > 0)
                                {
                                    lineCount = n;
                                    Vector3[] pts = { l0, l1, l2, l3 };
                                    for (int i = 0; i < n; i++)
                                        pts[i] = Quaternion.Inverse(frameRot) * (rod._line.GetPosition(i) - framePos);
                                    l0 = pts[0]; l1 = pts[1]; l2 = pts[2]; l3 = pts[3];
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            // Per-leg foot IK target / foot model rotation / IK pole, root-relative.
            Vector3 flPos = Vector3.zero, frPos = Vector3.zero, flPole = Vector3.zero, frPole = Vector3.zero;
            Quaternion flRot = Quaternion.identity, frRot = Quaternion.identity;
            if (hasBody && p.Legs != null && p.Legs._legTargets != null && p.Legs._legTargets.Length >= 2 &&
                p.Legs._ikPoles != null && p.Legs._ikPoles.Length >= 2 &&
                p.Legs._footModels != null && p.Legs._footModels.Length >= 2 &&
                p.Legs._legTargets[0] != null && p.Legs._legTargets[1] != null &&
                p.Legs._ikPoles[0] != null && p.Legs._ikPoles[1] != null &&
                p.Legs._footModels[0] != null && p.Legs._footModels[1] != null)
            {
                flPos = Quaternion.Inverse(frameRot) * (p.Legs._legTargets[0].position - framePos);
                flRot = Quaternion.Inverse(frameRot) * p.Legs._footModels[0].rotation;
                flPole = Quaternion.Inverse(frameRot) * (p.Legs._ikPoles[0].position - framePos);
                frPos = Quaternion.Inverse(frameRot) * (p.Legs._legTargets[1].position - framePos);
                frRot = Quaternion.Inverse(frameRot) * p.Legs._footModels[1].rotation;
                frPole = Quaternion.Inverse(frameRot) * (p.Legs._ikPoles[1].position - framePos);
            }

            _latest[_myObjectId] = new VRPoseBroadcast
            {
                ObjectId = _myObjectId,
                BodyYaw = bodyYaw,
                BodyPos = bodyPos,
                HasBody = hasBody,
                LeftPos = Quaternion.Inverse(frameRot) * (l.position - framePos),
                LeftRot = Quaternion.Inverse(frameRot) * l.rotation,
                RightPos = Quaternion.Inverse(frameRot) * (r.position - framePos),
                RightRot = Quaternion.Inverse(frameRot) * r.rotation,
                FootLPos = flPos,
                FootLRot = flRot,
                FootLPole = flPole,
                FootRPos = frPos,
                FootRRot = frRot,
                FootRPole = frPole,
                Held = heldTool,
                HeldIsTool = heldIsTool,
                HeldPos = heldPos,
                HeldRot = heldRot,
                LaserActive = laserActive,
                LaserStart = laserStart,
                LaserEnd = laserEnd,
                LineCount = lineCount,
                Line0 = l0,
                Line1 = l1,
                Line2 = l2,
                Line3 = l3,
            };
            _lastSeen[_myObjectId] = Time.unscaledTime;

            InstanceFinder.ClientManager.Broadcast(_latest[_myObjectId], Channel.Unreliable);
        }
        catch { }
    }

    private int GetMyObjectId()
    {
        try
        {
            var p = Player.LocalPlayer;
            if (p == null) return _myObjectId;
            var no = p.NetworkObject;
            if (no == null) return _myObjectId;
            int id = no.ObjectId;
            if (id != _myObjectId)
            {
                _myObjectId = id;
                _relayActive = false; // new player -> re-handshake before streaming
                _helloTimer = 0f;
            }
            return _myObjectId;
        }
        catch { return _myObjectId; }
    }

    // ---- Server side (host) ----

    private void OnServerHello(NetworkConnection conn, VRHelloBroadcast msg, Channel channel)
    {
        try
        {
            if (conn == null || !conn.IsValid) return;
            bool fresh = _modded.Add(conn);
            InstanceFinder.ServerManager.Broadcast(conn, new VRHelloResponse(), true, Channel.Reliable);
            if (!fresh) return;

            // Newly-registered modded client: immediately send it the latest pose of every known VR
            // player (including the host's own), so it sees existing VR players right away instead of
            // waiting for the next relay tick.
            foreach (var kv in _latest)
            {
                if (kv.Key == msg.ObjectId) continue;
                if (_lastSeen.TryGetValue(kv.Key, out var t) && Time.unscaledTime - t > POSE_STALE) continue;
                InstanceFinder.ServerManager.Broadcast(conn, kv.Value, true, Channel.Unreliable);
            }
        }
        catch { }
    }

    private void OnServerPose(NetworkConnection conn, VRPoseBroadcast msg, Channel channel)
    {
        try
        {
            // Store locally so the HOST's own client sees remote VR players too.
            _latest[msg.ObjectId] = msg;
            _lastSeen[msg.ObjectId] = Time.unscaledTime;
            AddSample(msg.ObjectId, msg);
            // Relay to every other modded connection, never back to the sender. NOTE: BroadcastExcept's
            // first argument is the EXCLUSION SET — passing _modded there excluded exactly the clients
            // that should receive poses (that's why clients never saw the host/other players). Send
            // explicitly to each modded conn instead.
            foreach (var c in _modded)
            {
                if (c == null || !c.IsValid || c == conn) continue;
                InstanceFinder.ServerManager.Broadcast(c, msg, true, Channel.Unreliable);
            }
        }
        catch { }
    }

    // ---- Client side ----

    private void OnClientHelloResponse(VRHelloResponse msg, Channel channel)
    {
        _relayActive = true;
    }

    private void OnClientPose(VRPoseBroadcast msg, Channel channel)
    {
        _latest[msg.ObjectId] = msg;
        _lastSeen[msg.ObjectId] = Time.unscaledTime;
        AddSample(msg.ObjectId, msg);
    }

    private void SweepStale()
    {
        if (_latest.Count == 0) return;
        var dead = new List<int>();
        foreach (var kv in _lastSeen)
            if (Time.unscaledTime - kv.Value > POSE_STALE + 2f) dead.Add(kv.Key);
        foreach (var id in dead)
        {
            _latest.Remove(id);
            _lastSeen.Remove(id);
        }
    }

    private void OnDestroy()
    {
        try
        {
            if (_clientRegistered && InstanceFinder.ClientManager != null)
            {
                InstanceFinder.ClientManager.UnregisterBroadcast<VRHelloResponse>(OnClientHelloResponse);
                InstanceFinder.ClientManager.UnregisterBroadcast<VRPoseBroadcast>(OnClientPose);
            }
            if (_serverRegistered && InstanceFinder.ServerManager != null)
            {
                InstanceFinder.ServerManager.UnregisterBroadcast<VRHelloBroadcast>(OnServerHello);
                InstanceFinder.ServerManager.UnregisterBroadcast<VRPoseBroadcast>(OnServerPose);
            }
        }
        catch { }
        _latest.Clear();
        _lastSeen.Clear();
    }
}
