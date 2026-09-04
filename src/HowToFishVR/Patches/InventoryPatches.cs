using HarmonyLib;
using HowToFishVR.Input;
using UnityEngine;
using UnityEngine.InputSystem;

namespace HowToFishVR.Patches;

/// <summary>
/// Inventory scrolling is owned by the VR rig in VR: <c>VRRig.HandleTurning</c> reads the right stick Y
/// directly (discrete flick + rate-limit — the behaviour that worked well). The game's own
/// <c>ScrollSlotInput</c> (bound to the same right-stick Y by the game's controller scheme AND our
/// injected binding) would otherwise double-fire with it and glitch the selection, so it is fully
/// suppressed while VR is enabled. Flatscreen keeps the vanilla behaviour.
/// </summary>
[HarmonyPatch(typeof(PlayerInventory))]
internal static class InventoryPatches
{
    private const float Flick = 0.7f;    // stick must pass this to count as a deliberate flick
    private const float Interval = 0.3f; // min seconds between switches (kills the glitchy fast spam)
    private static float _last = -10f;

    [HarmonyPrefix]
    [HarmonyPatch("ScrollSlotInput")]
    private static bool DiscreteScroll(InputAction.CallbackContext context)
    {
        if (!Plugin.VREnabled) return true;

        // VR: the rig owns scrolling (right stick Y) — suppress the game's handler so the two paths
        // never double-fire and switch two slots per flick.
        return false;
    }
}
