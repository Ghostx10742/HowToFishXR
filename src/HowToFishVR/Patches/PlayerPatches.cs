using HarmonyLib;
using HowToFishVR.Input;
using HowToFishVR.VR;
using UnityEngine;

namespace HowToFishVR.Patches;

/// <summary>
/// Makes locomotion HMD-relative. The game derives movement direction from <c>Player.CurPlayerRot</c>,
/// which it computes in <c>Player.LateUpdate</c> from the camera yaw — but that runs BEFORE our
/// onBeforeRender pose, so it used a stale/flat yaw. We overwrite it with the real HMD world yaw.
/// </summary>
[HarmonyPatch(typeof(Player))]
internal static class PlayerPatches
{
    [HarmonyPostfix]
    [HarmonyPatch("LateUpdate")]
    private static void HmdMovementYaw(Player __instance)
    {
        if (!Plugin.VREnabled) return;
        if (__instance != Player.LocalPlayer) return;
        var rig = VRRig.Instance;
        var a = VRActions.Instance;
        if (rig == null || rig.Root == null || a == null) return;

        float yaw = (rig.Root.rotation * a.HeadRot).eulerAngles.y;
        __instance.CurPlayerRot = Quaternion.Euler(0f, yaw, 0f);
    }
}
