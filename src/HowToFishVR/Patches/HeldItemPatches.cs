using HarmonyLib;
using HowToFishVR.Net;

namespace HowToFishVR.Patches;

/// <summary>
/// Non-tool held items are physics-driven by the game every FixedUpdate. For the local VR player,
/// <see cref="HeldItemHoldPatches"/> redirects that solver to the main hand, so the original method MUST
/// run. Manual-only exceptions (currently TNT) are still posed by <see cref="VR.VRHeldItem"/> and skip it.
/// This patch ALSO skips the solver for REMOTE players being driven by VR pose sync
/// (RemoteBodyDriver pins their held non-tool item to the broadcast pose) — without this the receiver's
/// own copy of the game's physics hold steers the remote fish toward the camera spot every FixedUpdate
/// and fights the pin.
/// </summary>
[HarmonyPatch(typeof(PlayerHolding))]
internal static class HeldItemPatches
{
    [HarmonyPrefix]
    [HarmonyPatch("MoveItemToHoldPosRot")]
    private static bool SkipGameHold(PlayerHolding __instance)
    {
        if (!Plugin.VREnabled) return true;
        try
        {
            var item = __instance.HeldItem;
            if (item == null || item.Tool != null) return true; // tools are handled by the tool patch
            var p = __instance._player;
            if (p == null || p.Owner == null) return true;

            // Local player: ordinary non-tools (including dead bodies) use the game's dynamic hold with
            // a VR-hand target. Manual-only items such as TNT still skip it so VRHeldItem can own them.
            if (p.Owner.IsLocalClient)
            {
                if (!Plugin.HeadsetActive) return true;
                return HeldItemHoldPatches.ShouldHandHold(__instance);
            }

            // Remote player currently driven by VR pose sync: RemoteBodyDriver pins their held item to
            // the broadcast pose — skip the game's hold so it doesn't fight the pin. EXCEPT boat players:
            // the remote body driver skips them (OtherPlayer.OnBoat, the REMOTE boat flag), so the game's
            // own hold must run or their item would be left un-held.
            try
            {
                if (p.Other != null && p.Other.OnBoat) return true;
                if (p.NetworkObject != null && VRNetSync.TryGetPose(p.NetworkObject.ObjectId, out var pose) &&
                    pose.Held && !pose.HeldIsTool)
                    return false;
            }
            catch { }
            return true;
        }
        catch { return true; }
    }
}
