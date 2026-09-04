using HarmonyLib;
using HowToFishVR.VR;
using UnityEngine;

namespace HowToFishVR.Patches;

/// <summary>
/// Head tracking is handled by a <see cref="VRCameraDriver"/> (TrackedPoseDriver) on the camera, so we
/// stop the game from positioning the camera itself and let the driver + origin drive it. This is the
/// RepoXR approach for Unity 6 URP.
/// </summary>
[HarmonyPatch(typeof(PlayerCamera))]
internal static class PlayerCameraPatches
{
    // NOTE: SetCamPosRot is intentionally NOT blocked — it sets the camera to the player's eye
    // position each frame, which VRCameraPoser reads as the anchor before adding the head pose.

    /// <summary>On camera start: kill view bob (the pose is applied late by VRCameraPoser).</summary>
    [HarmonyPostfix]
    [HarmonyPatch("OnStartClient")]
    private static void OnStart(PlayerCamera __instance)
    {
        if (!Plugin.VREnabled) return;
        try { PlayerCamera.SetViewBobbing(false); } catch { }
    }

    /// <summary>
    /// Keep the game's accumulated camera yaw (_rot.y) locked to the HMD heading, bounded to [-180,180].
    /// The game adds aim-assist deltas into _rot EVERY frame and writes it as the camera's LOCAL euler
    /// (SetCamPosRot) — without this, _rot.y drifts unboundedly over a session and every game system that
    /// reads the pre-pose camera state (body springs, network send) chases a yaw that no longer matches
    /// the view. Syncing it here makes the game's OWN camera write produce the correct world yaw before
    /// the postfix/onBeforeRender re-pose takes over.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch("SetCamPosRot")]
    private static void SyncRotY(PlayerCamera __instance)
    {
        if (!Plugin.VREnabled) return;
        try
        {
            var rig = VRRig.Instance;
            var a = HowToFishVR.Input.VRActions.Instance;
            if (rig == null || a == null || rig.Root == null || !rig.IsCalibrated) return;
            var cam = __instance.CamTransform;
            if (cam == null) return;
            float parentYaw = cam.parent != null ? cam.parent.eulerAngles.y : 0f;
            float viewYaw = (rig.Root.rotation * a.HeadRot).eulerAngles.y;
            // Local yaw that makes the game's own write (local euler) land on the view yaw in world space.
            __instance._rot.y = Mathf.DeltaAngle(parentYaw, viewYaw);
        }
        catch { }
    }

    /// <summary>Ignore real mouse look while VR is active — the HMD drives the view.</summary>
    [HarmonyPrefix]
    [HarmonyPatch("MouseInput")]
    private static bool BlockMouseLook()
    {
        return !Plugin.VREnabled;
    }

    /// <summary>
    /// Head-track the camera transform in the game's own LateUpdate too (not only in VRCameraPoser's
    /// onBeforeRender), so GAME LOGIC that reads <c>CamObject.forward</c> — the fishing cast, weapon aim,
    /// interaction raycasts — uses your actual GAZE instead of the frozen mouse-look/body-forward. The
    /// render pose is still re-applied crisply in onBeforeRender, so this doesn't warp the view.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch("SetCamPosRot")]
    private static void HeadTrackForGameLogic(PlayerCamera __instance)
    {
        if (!Plugin.VREnabled) return;
        try
        {
            var rig = VRRig.Instance;
            var a = HowToFishVR.Input.VRActions.Instance;
            if (rig == null || a == null || rig.Root == null || !rig.IsCalibrated) return;
            var cam = __instance.CamTransform;
            if (cam == null) return;
            cam.SetPositionAndRotation(rig.Root.TransformPoint(a.HeadPos), rig.Root.rotation * a.HeadRot);
        }
        catch { }
    }
}
