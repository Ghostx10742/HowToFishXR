using FishNet.Broadcast;
using FishNet.Serializing;
using UnityEngine;

namespace HowToFishVR.Net;

/// <summary>
/// Client -> Server: "I have the mod installed, my player ObjectId is X". Sent periodically (reliable).
/// The server answers with <see cref="VRHelloResponse"/> and adds this connection to its relay set.
/// </summary>
public struct VRHelloBroadcast : IBroadcast
{
    public int ObjectId;
}

/// <summary>Server -> Client: "relay active — start sending poses". The client only streams poses
/// after receiving this, so a vanilla host (no mod) never receives pose spam.</summary>
public struct VRHelloResponse : IBroadcast
{
}

/// <summary>
/// Client -> Server (relayed to every other modded client): the VR player's FULL body pose. All
/// positions/rotations are expressed in the player's SYNCED ROOT FRAME: origin = <c>Player.Transform.position</c>
/// (the exact position the game replicates), rotation = <c>Quaternion.Euler(0, BodyYaw, 0)</c> where
/// BodyYaw is the BODY'S WORLD YAW — the rendered view yaw INCLUDING the snap/smooth-turn offset (the
/// base game only replicates the camera's raw local yaw, which excludes it, so the body must be pinned
/// to this yaw explicitly). The receiver composes with the same frame, so hands and feet land exactly
/// where they are locally — including right after a snap turn.
///
/// BodyYaw + BodyPos move the whole remote body; the per-leg data pins the feet (foot IK target, foot
/// model rotation, IK pole); the hand poses carry the grip state (one/two-hand, gun grips) with no
/// separate channel; the held-tool pose (Held/HeldPos/HeldRot) pins the gun/rod the VR player is
/// holding so it appears where they actually hold it (the base game only syncs the flatscreen spot).
/// <c>HasBody</c> is false when the sender's full body is hidden (Hands Only mode) — the receiver then
/// leaves the body to the game defaults while still using the hand poses.
/// </summary>
public struct VRPoseBroadcast : IBroadcast
{
    public int ObjectId;
    public float BodyYaw;    // body world yaw (view yaw incl. snap/smooth turns)
    public Vector3 BodyPos;  // body root world delta from player root (0 when body hidden)
    public bool HasBody;     // sender's full body is active (BodyPos + feet are valid)

    public Vector3 LeftPos;
    public Quaternion LeftRot;
    public Vector3 RightPos;
    public Quaternion RightRot;

    public Vector3 FootLPos;
    public Quaternion FootLRot;
    public Vector3 FootLPole;
    public Vector3 FootRPos;
    public Quaternion FootRRot;
    public Vector3 FootRPole;

    // Held item (ANY item — gun/rod OR fish/food/body) world pose in the same body frame. The base game
    // only syncs held items at their flatscreen camera-relative spot, so without this the remote sees the
    // gun floating in front of the face / the fish hanging at the camera instead of where the VR player
    // is actually holding it (one- or two-handed). HeldIsTool lets the receiver decide how to pin it
    // (tools are transform-glued by PlayerToolMovement; non-tools are physics-steered by PlayerHolding).
    public bool Held;
    public bool HeldIsTool;
    public Vector3 HeldPos;
    public Quaternion HeldRot;

    // Gun laser-sight beam (world, body frame). LaserActive=false when the sender isn't holding a laser
    // gun — the receiver then leaves the remote's laser to its own raycast. When active, the receiver
    // draws the beam EXACTLY where the VR player's laser points (deterministic, no receiver-side
    // raycast differences).
    public bool LaserActive;
    public Vector3 LaserStart;
    public Vector3 LaserEnd;

    // Held fishing-rod LINE (world, body frame) — the actual line the VR player sees: bait + rod points.
    // LineCount=0 when not holding a rod with a live line; otherwise the first LineCount entries are
    // valid, in sender order (receiver writes them into the remote rod's LineRenderer verbatim).
    public int LineCount;
    public Vector3 Line0;
    public Vector3 Line1;
    public Vector3 Line2;
    public Vector3 Line3;
}

/// <summary>Registers the broadcast serializers once (before any broadcast is sent or received).</summary>
public static class VRBroadcastSerializers
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        try
        {
            GenericWriter<VRHelloBroadcast>.SetWrite((Writer w, VRHelloBroadcast v) => w.WriteInt32(v.ObjectId));
            GenericReader<VRHelloBroadcast>.SetRead((Reader r) => new VRHelloBroadcast { ObjectId = r.ReadInt32() });

            GenericWriter<VRHelloResponse>.SetWrite((Writer w, VRHelloResponse v) => { });
            GenericReader<VRHelloResponse>.SetRead((Reader r) => new VRHelloResponse());

            GenericWriter<VRPoseBroadcast>.SetWrite((Writer w, VRPoseBroadcast v) =>
            {
                w.WriteInt32(v.ObjectId);
                w.WriteSingle(v.BodyYaw);
                w.WriteVector3(v.BodyPos);
                w.WriteBoolean(v.HasBody);
                w.WriteVector3(v.LeftPos);
                w.WriteQuaternion32(v.LeftRot);
                w.WriteVector3(v.RightPos);
                w.WriteQuaternion32(v.RightRot);
                w.WriteVector3(v.FootLPos);
                w.WriteQuaternion32(v.FootLRot);
                w.WriteVector3(v.FootLPole);
                w.WriteVector3(v.FootRPos);
                w.WriteQuaternion32(v.FootRRot);
                w.WriteVector3(v.FootRPole);
                w.WriteBoolean(v.Held);
                w.WriteBoolean(v.HeldIsTool);
                w.WriteVector3(v.HeldPos);
                w.WriteQuaternion32(v.HeldRot);
                w.WriteBoolean(v.LaserActive);
                w.WriteVector3(v.LaserStart);
                w.WriteVector3(v.LaserEnd);
                w.WriteInt32(v.LineCount);
                w.WriteVector3(v.Line0);
                w.WriteVector3(v.Line1);
                w.WriteVector3(v.Line2);
                w.WriteVector3(v.Line3);
            });
            GenericReader<VRPoseBroadcast>.SetRead((Reader r) => new VRPoseBroadcast
            {
                ObjectId = r.ReadInt32(),
                BodyYaw = r.ReadSingle(),
                BodyPos = r.ReadVector3(),
                HasBody = r.ReadBoolean(),
                LeftPos = r.ReadVector3(),
                LeftRot = r.ReadQuaternion32(),
                RightPos = r.ReadVector3(),
                RightRot = r.ReadQuaternion32(),
                FootLPos = r.ReadVector3(),
                FootLRot = r.ReadQuaternion32(),
                FootLPole = r.ReadVector3(),
                FootRPos = r.ReadVector3(),
                FootRRot = r.ReadQuaternion32(),
                FootRPole = r.ReadVector3(),
                Held = r.ReadBoolean(),
                HeldIsTool = r.ReadBoolean(),
                HeldPos = r.ReadVector3(),
                HeldRot = r.ReadQuaternion32(),
                LaserActive = r.ReadBoolean(),
                LaserStart = r.ReadVector3(),
                LaserEnd = r.ReadVector3(),
                LineCount = r.ReadInt32(),
                Line0 = r.ReadVector3(),
                Line1 = r.ReadVector3(),
                Line2 = r.ReadVector3(),
                Line3 = r.ReadVector3(),
            });
        }
        catch (System.Exception ex)
        {
            Plugin.Logger.LogError($"VR pose serializer registration failed: {ex}");
        }
    }
}
