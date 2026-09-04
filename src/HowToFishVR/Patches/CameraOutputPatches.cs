using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;

namespace HowToFishVR.Patches;

/// <summary>
/// Makes the game's main camera render straight to the headset. Many Unity games render the main
/// camera into a RenderTexture (for UI compositing / post), which means it never reaches the HMD —
/// the symptom is grey/black eyes with the flat UI still visible. We null the target texture on the
/// managed cameras, force <c>stereoTargetEye = Both</c>, and block the game from re-assigning a
/// target texture to them. (Same technique as LCVR/RepoXR.)
/// </summary>
[HarmonyPatch]
internal static class CameraOutputPatches
{
    private static readonly HashSet<Camera> Managed = new HashSet<Camera>();

    /// <summary>Register a camera as a VR output camera and point it at the HMD.</summary>
    public static void SetupForVR(Camera cam) => Apply(cam);

    /// <summary>Lightweight per-frame XR-render ensure (called right before render).</summary>
    public static void EnsureXR(Camera cam)
    {
        if (cam == null) return;
        try
        {
            // Ensure the camera renders the VR UI layer (menu camera excludes it by default).
            cam.cullingMask |= (1 << 5);
            var data = cam.GetUniversalAdditionalCameraData();
            if (data != null)
            {
                if (!data.allowXRRendering) data.allowXRRendering = true;
                // Post-processing is STRIPPED on the forced-XR game camera — permanently, by design.
                // Enabling it blacked the eyes: URP's UberPost needs an intermediate color target sized for
                // the camera's XR/stereo config AT FRAME START, but we flip allowXRRendering / clear
                // targetTexture mid-frame from outside the pipeline, so post resolves against a mismatched
                // (single, non-array) target -> black/garbage eye. And it is NOT needed: the underwater look
                // is native fog + the UnderwaterCanvas overlay (see VRUIManager), not a post volume; the
                // health/death saturation is the only real post user and isn't worth blacking the view for.
                if (data.renderPostProcessing) data.renderPostProcessing = false;
            }
        }
        catch { }
    }

    /// <summary>
    /// Force XR rendering ON for every enabled camera that draws to the screen. Called every frame so
    /// the actual render camera (which may change: menu cam -> player cam) always gets configured.
    /// </summary>
    public static void EnsureAll()
    {
        var cams = Camera.allCameras;
        foreach (var cam in cams)
        {
            if (cam == null || !cam.isActiveAndEnabled) continue;
            if (cam.targetTexture != null) continue; // renders to a texture (previews/radar) — leave it
            Apply(cam);
        }
    }

    private static void Apply(Camera cam)
    {
        if (cam == null) return;
        Managed.Add(cam);
        if (cam.targetTexture != null) cam.targetTexture = null;
        // DO NOT set cam.stereoTargetEye here. This game is URP (a scriptable render pipeline), where
        // stereoTargetEye is invalid — Unity logs a stack-traced warning ("You can use Camera.stereoTargetEye
        // only with the built-in renderer") EVERY frame it's set, on EVERY camera. That was 125,000+ logged
        // stack traces per session — the "lags like hell" cause. XR stereo in URP is driven by
        // allowXRRendering (set below), not stereoTargetEye.

        try
        {
            var data = cam.GetUniversalAdditionalCameraData();
            if (data != null)
            {
                if (!data.allowXRRendering) data.allowXRRendering = true;
                // Post-processing is STRIPPED on the forced-XR game camera — permanently, by design.
                // Enabling it blacked the eyes: URP's UberPost needs an intermediate color target sized for
                // the camera's XR/stereo config AT FRAME START, but we flip allowXRRendering / clear
                // targetTexture mid-frame from outside the pipeline, so post resolves against a mismatched
                // (single, non-array) target -> black/garbage eye. And it is NOT needed: the underwater look
                // is native fog + the UnderwaterCanvas overlay (see VRUIManager), not a post volume; the
                // health/death saturation is the only real post user and isn't worth blacking the view for.
                if (data.renderPostProcessing) data.renderPostProcessing = false;
            }
        }
        catch { }
    }

    private static bool IsMainCamera(Camera cam)
    {
        if (cam == null) return false;
        if (Managed.Contains(cam)) return true;
        if (cam == GameInfo.CurCamera) return true;
        try { if (cam == MainMenuManager.MenuCam) return true; } catch { }
        return false;
    }

    /// <summary>Prevent the game from redirecting a main camera into an off-screen render texture.</summary>
    [HarmonyPatch(typeof(Camera), nameof(Camera.targetTexture), MethodType.Setter)]
    [HarmonyPrefix]
    private static void BlockMainTargetTexture(Camera __instance, ref RenderTexture value)
    {
        if (!Plugin.VREnabled) return;
        if (value != null && IsMainCamera(__instance))
            value = null;
    }
}
