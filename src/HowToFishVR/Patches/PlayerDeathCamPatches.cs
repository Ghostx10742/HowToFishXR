using HarmonyLib;
using HowToFishVR.UI;
using UnityEngine;

namespace HowToFishVR.Patches;

/// <summary>
/// Keeps the view first-person on death. Vanilla swaps to a third-person orbit camera
/// (<c>PlayerDeathCam</c>); in VR that is disorienting, so we immediately restore the main
/// first-person camera (which the HMD head-tracking patch keeps driving) and suppress the orbit cam.
/// </summary>
[HarmonyPatch(typeof(PlayerDeathCam))]
internal static class PlayerDeathCamPatches
{
    [HarmonyPostfix]
    [HarmonyPatch("EnableDeathCam")]
    private static void StayFirstPerson(PlayerDeathCam __instance)
    {
        if (!Plugin.VREnabled) return;

        var player = __instance._player;
        if (player == null) return;

        // IMPORTANT: the DeathCanvas (the "you're dead / hold to respawn" screen) is a CHILD of the
        // death cam GameObject ("DeathCamera (To toggle)"). Deactivating the whole object would kill
        // the death screen too — that's the "respawn screen is a black screen" bug. So only disable
        // the Camera COMPONENT (stops the orbit render) and keep the object + its canvas child alive;
        // the VR UI manager already converted the death canvas into a head-following panel.
        var deathCam = __instance._deathCam;
        if (deathCam != null) deathCam.enabled = false;

        var mainCam = player.Camera != null ? player.Camera.Cam : null;
        if (mainCam != null) player.SetCurCam(mainCam);
    }

    // Neutralize the per-frame orbit so nothing fights the first-person camera.
    [HarmonyPrefix]
    [HarmonyPatch("OrbitCamera")]
    private static bool SkipOrbit()
    {
        return !Plugin.VREnabled; // run original only in flatscreen
    }
}

/// <summary>
/// Keeps the death screen readable in VR. The game's <c>DeathUI.ToggleDeathUI</c> scales the death
/// canvas 2 -> 1 with LeanTween as a "zoom in" flourish. In flatscreen that's fine; the VR UI manager
/// converted that same canvas into a small world-space panel (localScale ~0.0011), and the game's
/// 2->1 scale tween would blow it up to a 1920-metre wall right in front of the face — the "respawn
/// screen is a black screen" bug. In VR we kill the scale tween and keep the panel's authored scale.
/// </summary>
[HarmonyPatch(typeof(DeathUI))]
internal static class DeathUIPatches
{
    [HarmonyPostfix]
    [HarmonyPatch("ToggleDeathUI")]
    private static void PreservePanelScale(DeathUI __instance, bool to)
    {
        if (!Plugin.VREnabled) return;
        // Networked player prefabs can each contain a DeathUI. Only the PlayerUI singleton initialized
        // for this client is allowed to create or drive the head-locked VR death panel.
        if (PlayerUI._instance == null || PlayerUI._instance._deathUI != __instance) return;
        // The game just started a 2 -> 1 LeanTween scale flourish on the death canvas. In VR that
        // canvas is a small world-space panel, so the flourish leaves it as a 1920-metre wall (the
        // "black respawn screen"). The per-frame scale re-assert in VRUIManager.Reposition overrides
        // it for rendering; this also kills the flourish immediately so it doesn't even try.
        try
        {
            var canvas = __instance._deathCanvas;
            if (canvas == null) return;
            var root = canvas.GetComponentInParent<Canvas>(true);
            if (root == null) return;
            VRUIManager.Instance?.SyncDeathOverlay(__instance, to);
            if (root.renderMode != RenderMode.WorldSpace) return;
            // The small VR scale belongs on the ROOT Canvas. Applying it again to the inner CanvasGroup
            // double-scaled the death content and made it appear pinned/off-centre. Keep the authored
            // inner scale; VRUIManager reasserts this while rendering so only the alpha fade remains.
            // Both native tweens target this same GameObject. Cancel them together and assert the final
            // VR state: this sacrifices only the two-second opening fade/zoom, while guaranteeing the
            // native respawn/downed UI cannot remain alpha-zero in the headset. The give-up hold mask
            // has its own GameObject/tween and remains fully functional.
            LeanTween.cancel(canvas.gameObject);
            canvas.transform.localScale = Vector3.one;
            canvas.alpha = to ? 1f : 0f;
        }
        catch { }
    }
}
