using HarmonyLib;
using HowToFishVR.VR;
using UnityEngine;

namespace HowToFishVR.Patches;

/// <summary>
/// "Punching hurts us" fix. The game's punch (<see cref="PlayerPunching"/>) resolves its target with a
/// raycast from the camera. In VR the restored full-body sits exactly where that ray starts (the HMD is
/// at the body's head), and ownership edge-cases can make the local player's own body parts resolve as
/// the target — the punch then plays hit effects on us and (with friendly fire on) applies real damage
/// to ourselves. These patches guarantee a punch can never target or damage the local player's own body:
/// the target is dropped at selection time AND damage is skipped at hit time, so neither the hurt
/// vignette/flash nor self-damage can fire.
/// </summary>
[HarmonyPatch(typeof(PlayerPunching))]
internal static class PunchPatches
{
    internal struct CameraPoseState
    {
        internal Transform Camera;
        internal Vector3 Position;
        internal Quaternion Rotation;
        internal bool Changed;
    }

    /// <summary>Temporarily put the game's targeting transform on the current HMD pose. Gameplay target
    /// searches run before onBeforeRender, so CamObject can otherwise still hold an older camera pose.</summary>
    internal static CameraPoseState BeginHeadAim(Transform camera)
    {
        var state = new CameraPoseState { Camera = camera };
        if (!Plugin.VREnabled || camera == null) return state;
        try
        {
            var head = VRRig.Instance != null ? VRRig.Instance.Head : null;
            if (head == null) return state;
            state.Position = camera.position;
            state.Rotation = camera.rotation;
            state.Changed = true;
            camera.SetPositionAndRotation(head.position, head.rotation);
        }
        catch { state.Changed = false; }
        return state;
    }

    internal static void EndHeadAim(CameraPoseState state)
    {
        if (!state.Changed || state.Camera == null) return;
        try { state.Camera.SetPositionAndRotation(state.Position, state.Rotation); } catch { }
    }

    private static bool IsLocalPlayerBody(Transform t)
    {
        if (t == null) return false;
        try
        {
            var p = PlayerManager.GetPlayerFromBodyPart(t);
            if (p != null) return p == Player.LocalPlayer;
        }
        catch { }
        // Unregistered colliders (e.g. the restored body) may not map through PlayerManager — fall back
        // to comparing the transform root with the local player's root.
        try
        {
            var lp = Player.LocalPlayer;
            if (lp != null && lp.Transform != null && t.root != null && t.root == lp.Transform.root) return true;
        }
        catch { }
        return false;
    }

    /// <summary>Drop the punch target if it resolved to the local player's own body.</summary>
    [HarmonyPostfix]
    [HarmonyPatch("CheckForPlayersAndLevel")]
    private static void ExcludeSelf(PlayerPunching __instance, ref Transform finalTarget, ref Vector3 finalHitPoint)
    {
        if (!Plugin.VREnabled) return;
        if (IsLocalPlayerBody(finalTarget))
        {
            finalTarget = null;
            finalHitPoint = Vector3.zero;
        }
    }

    /// <summary>Skip the damage/effect application if the hit target is the local player's own body.</summary>
    [HarmonyPrefix]
    [HarmonyPatch("HitTarget")]
    private static bool NoSelfHit(PlayerPunching __instance, int side)
    {
        if (!Plugin.VREnabled) return true;
        if (__instance._curTarget == null || side < 0 || side >= __instance._curTarget.Length) return true;
        if (IsLocalPlayerBody(__instance._curTarget[side])) return false; // skip entirely — no effects, no damage
        return true;
    }

    // Empty-hand punches target from the live HMD direction. The temporary pose is restored immediately
    // after the game's own ray/sphere casts, so no camera/render behavior is changed.
    [HarmonyPrefix]
    [HarmonyPatch("FindPunchTarget")]
    private static void HeadAimPrefix(PlayerPunching __instance, out CameraPoseState __state)
    {
        Transform cam = null;
        try { cam = __instance._player != null ? __instance._player.CamObject : null; } catch { }
        __state = BeginHeadAim(cam);
    }

    [HarmonyPostfix]
    [HarmonyPatch("FindPunchTarget")]
    private static void HeadAimPostfix(CameraPoseState __state) => EndHeadAim(__state);
}

/// <summary>Brass-knuckle/two-fist melee uses the same HMD targeting as empty-hand punching. Other melee
/// tools keep their normal tool behavior.</summary>
[HarmonyPatch(typeof(Melee))]
internal static class KnuckleHeadAimPatches
{
    [HarmonyPrefix]
    [HarmonyPatch("FindAttackTarget")]
    private static void HeadAimPrefix(Melee __instance, out PunchPatches.CameraPoseState __state)
    {
        __state = default;
        try
        {
            if (!Plugin.VREnabled || !__instance._useBothHands) return;
            __state = PunchPatches.BeginHeadAim(__instance.Holder != null ? __instance.Holder.CamObject : null);
        }
        catch { }
    }

    [HarmonyPostfix]
    [HarmonyPatch("FindAttackTarget")]
    private static void HeadAimPostfix(PunchPatches.CameraPoseState __state) => PunchPatches.EndHeadAim(__state);
}
