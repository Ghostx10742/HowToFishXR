using HarmonyLib;
using UnityEngine;

namespace HowToFishVR.Patches;

/// <summary>
/// The tool's "animated hands" glue BOTH hands onto the tool, so the off hand can never be free. In VR
/// we want each hand to follow its own controller (off hand free until you two-hand grip), so we disable
/// the tool-glued hands and let our controller-driven hand bones (<see cref="PlayerHandsPatches"/>) place
/// each hand independently.
/// </summary>
[HarmonyPatch(typeof(Tool))]
internal static class ToolHandPatches
{
    [HarmonyPrefix]
    [HarmonyPatch("TryActivateAnimatedHands")]
    private static bool DisableAnimatedHands(Tool __instance, Player expectedHolder, ref bool __result)
    {
        if (!Plugin.VREnabled) return true;
        // LOCAL PLAYER ONLY. This runs on EVERY player's tool on our client — disabling a REMOTE player's
        // animated hands (especially a flatscreen player's, who must look exactly vanilla to us) is what
        // made other players' held tools/hands look broken on a VR client. expectedHolder is the player
        // about to hold the tool, so gate on it.
        try { if (expectedHolder == null || expectedHolder.Owner == null || !expectedHolder.Owner.IsLocalClient) return true; } catch { return true; }
        try { if (__instance.HandsMesh != null) __instance.HandsMesh.enabled = false; } catch { }
        __result = false;
        return false;
    }
}

/// <summary>
/// Disable the ADS aim animation. When two-hand aiming engages ADS, the game slides the weapon to a
/// fixed aim position (<c>PlayerToolMovement.SetAimPos</c>), which fights our two-hand hand placement.
/// Zeroing it keeps the accuracy benefit of ADS while the gun stays on your hands.
/// </summary>
[HarmonyPatch(typeof(PlayerToolMovement))]
internal static class AimAnimationPatches
{
    [HarmonyPrefix]
    [HarmonyPatch("SetAimPos")]
    private static void KillAimPose(PlayerToolMovement __instance, ref Vector3 aimPos)
    {
        if (!Plugin.VREnabled) return;
        // LOCAL PLAYER ONLY — zeroing a REMOTE (flatscreen) player's aim pose warps their gun on our VR
        // client. Only the local VR player's own ADS should be neutralised (our two-hand grip owns it).
        try { if (__instance._player == null || __instance._player.Owner == null || !__instance._player.Owner.IsLocalClient) return; } catch { return; }
        aimPos = Vector3.zero;
    }
}
