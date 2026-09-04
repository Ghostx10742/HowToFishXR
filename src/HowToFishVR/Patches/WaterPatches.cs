using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HowToFishVR.Patches;

/// <summary>
/// Underwater state in VR must follow the ACTUAL rendered view camera, not <c>Player.CurCam</c>. On death
/// the game sets <c>CurCam = _deathCam</c>, but in VR we skip the death-cam orbit, so <c>_deathCam</c>
/// FREEZES at the death spot — if you died underwater, <c>WaterManager.CheckUnderwater</c> keeps reading
/// that frozen submerged position and the fog / tint / underwater sounds stick ON forever ("dying in the
/// water shouldn't make it so no matter what all you see is the water view"). Re-run the check against the
/// mod-posed VR view camera (<c>Player.Camera.Cam</c>, locked to the ragdoll while dead) so the underwater
/// effect turns on/off by where you ACTUALLY look — alive or dead, exactly like flatscreen.
/// </summary>
[HarmonyPatch(typeof(WaterManager))]
internal static class WaterUnderwaterPatches
{
    // TogglePlayerUnderwater(bool to, bool forced) is private and not method-publicized — call it by reflection.
    private static readonly MethodInfo _toggle =
        AccessTools.Method(typeof(WaterManager), "TogglePlayerUnderwater", new[] { typeof(bool), typeof(bool) });

    private static void Toggle(WaterManager wm, bool under)
    {
        try { _toggle?.Invoke(wm, new object[] { under, false }); } catch { }
    }

    /// <summary>Finalize underwater state after the VR camera has been placed for this rendered frame.
    /// WaterManager.LateUpdate runs earlier, when the game may still have the camera at its pre-VR or
    /// death-camera position, which made tint/fog stick after a dead player's view left the water.</summary>
    internal static void UpdateFromRenderedVRView()
    {
        if (!Plugin.VREnabled) return;
        try
        {
            var wm = WaterManager._instance;
            var p = Player.LocalPlayer;
            if (wm == null || p == null) return;
            Transform view = p.Camera != null && p.Camera.Cam != null ? p.Camera.Cam.transform
                           : (p.CurCam != null ? p.CurCam.transform : null);
            if (view == null) return;
            bool under = view.position.y < WaterManager.GetWaterHeight(view.position) + 0.1f;
            Toggle(wm, under);
        }
        catch { }
    }

    [HarmonyPrefix]
    [HarmonyPatch("CheckPlayerUnderwater")]
    private static bool CheckUnderwaterVR(WaterManager __instance)
    {
        if (!Plugin.VREnabled || _toggle == null) return true; // flatscreen (or method not found): game logic
        try
        {
            var p = Player.LocalPlayer;
            if (p == null) { Toggle(__instance, false); return false; }
            // The camera the mod actually renders from (VRCameraPoser drives Player.Camera.Cam to the
            // ragdoll head / third-person orbit while dead). CurCam would be the frozen _deathCam.
            Transform view = p.Camera != null && p.Camera.Cam != null ? p.Camera.Cam.transform
                           : (p.CurCam != null ? p.CurCam.transform : null);
            if (view == null) return true;
            bool under = view.position.y < WaterManager.GetWaterHeight(view.position) + 0.1f;
            Toggle(__instance, under); // no-ops when unchanged (SetFog/tint/sounds only on transition)
            return false; // handled — skip the original CurCam-based check
        }
        catch { return true; }
    }
}
