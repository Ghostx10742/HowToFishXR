using HarmonyLib;
using UnityEngine;

namespace HowToFishVR.Patches;

/// <summary>
/// Disables the flatscreen sniper view in VR. The game's sniper scope is a 2D screen-space overlay
/// (crosshair lines + a zoom shader drawn over the flat image) that is useless in the headset — it
/// would render as a head-locked flat image over the real 3D view. In VR the player aims with the
/// gun's OWN scope model (brought to the eye), so the overlay must never appear: the 2D overlay calls
/// are neutralized and the gun's sight/scope models are kept visible (the game hides them the moment
/// the overlay would show). The sniper FOV zoom is also dropped — while ADSing a sniper-scoped weapon
/// you stay in the regular view. Non-sniper weapons keep their normal ADS zoom.
/// </summary>
[HarmonyPatch(typeof(PlayerUI))]
internal static class SniperUIPatches
{
    // Never show the 2D sniper scope overlay (crosshair + zoomed flat image).
    [HarmonyPrefix]
    [HarmonyPatch("ToggleSniperUI")]
    private static bool NoSniperOverlay(bool to)
    {
        return !Plugin.VREnabled; // run the original only in flatscreen
    }

    // Never position/update the overlay lines or the scope zoom shader either.
    [HarmonyPrefix]
    [HarmonyPatch("SetSniperUI")]
    private static bool NoSniperOverlayUpdate(Vector3 position, float posWeight, float scale)
    {
        return !Plugin.VREnabled;
    }
}

/// <summary>Keep the gun's sight/scope models visible in VR — the game hides them when the sniper UI
/// shows, but we never show it, so hiding them would leave the scoped gun bare.</summary>
[HarmonyPatch(typeof(Attachments))]
internal static class SightModelPatches
{
    [HarmonyPrefix]
    [HarmonyPatch("ToggleSightModels")]
    private static void KeepSightsVisible(ref bool to)
    {
        if (!Plugin.VREnabled) return;
        to = true;
    }
}

/// <summary>No sniper zoom: while ADSing a sniper-scoped weapon, skip the FOV lerp so the view stays at
/// the regular FOV instead of pulling in to the scope's narrow zoom.</summary>
[HarmonyPatch(typeof(PlayerCamera))]
internal static class SniperFovPatches
{
    [HarmonyPrefix]
    [HarmonyPatch("SetFov")]
    private static bool NoSniperZoom(PlayerCamera __instance)
    {
        if (!Plugin.VREnabled) return true;
        try
        {
            var p = __instance._player;
            if (p == null) return true;
            var held = p.Holding != null ? p.Holding.HeldItem : null;
            if (held == null || held.Weapon == null) return true;
            var w = held.Weapon;
            if (!w.IsAds) return true;
            if (!w._attachments.UseSniperUi) return true;
            return false; // sniper-scoped ADS in VR: skip the zoom, stay in regular view
        }
        catch { return true; }
    }
}

/// <summary>Scoped weapons normally replace FirePoint aim with head-camera aim once ADS is fully raised.
/// In VR the physical scope and two-hand gun pose own aim, so force only the Shoot method's branch to use
/// the real muzzle, then immediately restore the game's aim percentage.</summary>
[HarmonyPatch(typeof(Weapon), "Shoot")]
internal static class SniperProjectileAimPatches
{
    [HarmonyPrefix]
    private static void UsePhysicalMuzzle(Weapon __instance, out float __state)
    {
        __state = -1f;
        if (!Plugin.HeadsetActive) return;
        try
        {
            if (__instance == null || __instance.Holder == null || __instance.Holder.Owner == null ||
                !__instance.Holder.Owner.IsLocalClient || __instance.Attachments == null ||
                !__instance.Attachments.UseSniperUi || __instance._aimPercent <= 0.9f)
                return;

            __state = __instance._aimPercent;
            __instance._aimPercent = 0.9f; // vanilla camera-aim branch is strictly > 0.9
        }
        catch { __state = -1f; }
    }

    [HarmonyPostfix]
    private static void RestoreAimPercent(Weapon __instance, float __state)
    {
        if (__state >= 0f && __instance != null) __instance._aimPercent = __state;
    }
}
