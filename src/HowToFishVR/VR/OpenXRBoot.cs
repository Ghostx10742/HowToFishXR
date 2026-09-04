using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.Features.Interactions;

namespace HowToFishVR.VR;

/// <summary>
/// Boots Unity's OpenXR runtime manually at plugin load. The game was not built with XR enabled,
/// so there are no serialized XRGeneralSettings — we construct them (and the OpenXR feature list)
/// in code, then initialize + start the loader. Modelled on RepoXR's OpenXR.cs but fully
/// code-driven (no AssetBundle-provided OpenXRFeaturePack).
/// </summary>
public static class OpenXRBoot
{
    private static XRGeneralSettings _general;
    private static XRManagerSettings _manager;
    private static OpenXRLoader _loader;

    public static bool Running { get; private set; }

    /// <summary>Attempts to start OpenXR. Returns true if a display subsystem became active.</summary>
    public static bool Start()
    {
        try
        {
            NeutralizeAnalytics();
            BuildSettings();

            if (!VerifySubsystemManifest())
            {
                Plugin.Logger.LogError(
                    "OpenXR subsystem descriptors not found. The native UnityOpenXR plugin / manifest " +
                    "was not staged into How to Fish_Data. Check the preloader ran.");
                return false;
            }

            // Initialize + start the loader.
            if (!InitAndStart())
            {
                Plugin.Logger.LogError("OpenXR loader failed to produce a display subsystem. " +
                                       "Is a headset connected and an OpenXR runtime active (SteamVR/Oculus/VD)?");
                return false;
            }

            // Keep XR alive when the game window loses focus.
            try
            {
                UnityEngine.InputSystem.InputSystem.settings.backgroundBehavior =
                    UnityEngine.InputSystem.InputSettings.BackgroundBehavior.IgnoreFocus;
            }
            catch { /* input system optional here */ }

            XRSettings.eyeTextureResolutionScale = 1.0f;

            SetFloorTrackingOrigin();

            Running = true;
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"Exception while starting OpenXR: {ex}");
            return false;
        }
    }

    public static void Stop()
    {
        if (!Running) return;
        try
        {
            _manager?.StopSubsystems();
            _manager?.DeinitializeLoader();
        }
        catch { }
        Running = false;
    }

    /// <summary>
    /// The OpenXR plugin's analytics code (<c>OpenXRAnalytics</c>) references
    /// <c>UnityEngine.Analytics.AnalyticsResult</c>, whose module this game stripped — it throws a
    /// TypeLoadException during loader init. Redirect every analytics method to a no-op so init
    /// proceeds. (Standard fix used by LCVR/RepoXR-style mods.)
    /// </summary>
    private static void NeutralizeAnalytics()
    {
        var t = AccessTools.TypeByName("UnityEngine.XR.OpenXR.OpenXRAnalytics");
        if (t == null) return;

        var skip = new HarmonyMethod(AccessTools.Method(typeof(OpenXRBoot), nameof(SkipOriginal)));
        foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                                       System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance))
        {
            if (m.DeclaringType != t || m.IsAbstract || m.ContainsGenericParameters) continue;
            try { Plugin.HarmonyInstance.Patch(m, prefix: skip); }
            catch { }
        }
    }

    // Harmony prefix: returning false skips the original method body.
    private static bool SkipOriginal() => false;

    private static void BuildSettings()
    {
        _general ??= ScriptableObject.CreateInstance<XRGeneralSettings>();
        _manager ??= ScriptableObject.CreateInstance<XRManagerSettings>();
        _loader  ??= ScriptableObject.CreateInstance<OpenXRLoader>();

        // Wire manager into general settings (Manager has an internal setter).
        Traverse.Create(_general).Property("Manager").SetValue(_manager);

        // Put our loader into the manager's active loader list.
        SetActiveLoaders(_manager, new List<XRLoader> { _loader });

        _manager.automaticLoading = false;
        _manager.automaticRunning = false;

        // Make Unity's XRGeneralSettings.Instance resolve to ours so engine systems find it.
        TrySetXRGeneralSettingsInstance(_general);

        ConfigureOpenXRSettings();
    }

    private static void ConfigureOpenXRSettings()
    {
        var settings = OpenXRSettings.Instance;
        if (settings == null)
        {
            settings = ScriptableObject.CreateInstance<OpenXRSettings>();
            // Best-effort: assign to the static instance backing field.
            var field = AccessTools.Field(typeof(OpenXRSettings), "s_RuntimeSettingsInstance")
                        ?? AccessTools.Field(typeof(OpenXRSettings), "s_Instance")
                        ?? AccessTools.Field(typeof(OpenXRSettings), "m_Instance");
            field?.SetValue(null, settings);
        }

        // Render mode: MultiPass is the most compatible with URP + custom post-processing. Single-pass
        // instanced frequently renders one eye grey/black under URP (exactly the reported symptom).
        try { settings.renderMode = OpenXRSettings.RenderMode.MultiPass; } catch { }
        try { settings.depthSubmissionMode = OpenXRSettings.DepthSubmissionMode.None; } catch { }

        // Interaction profiles so the runtime binds the motion controllers (required for hand poses
        // + buttons). Session bring-up was fixed by the InitXRSDK/Start sequence, not by stripping
        // these, so they are restored.
        var features = BuildInteractionFeatures();
        SetFeatures(settings, features);
    }

    /// <summary>
    /// Create one enabled OpenXRInteractionFeature per common controller profile. This is what the
    /// editor's OpenXR settings would otherwise serialize; we build it at runtime instead.
    /// </summary>
    private static OpenXRFeature[] BuildInteractionFeatures()
    {
        var wanted = new[]
        {
            typeof(OculusTouchControllerProfile),
            typeof(MetaQuestTouchPlusControllerProfile),
            typeof(MetaQuestTouchProControllerProfile),
            typeof(ValveIndexControllerProfile),
            typeof(HTCViveControllerProfile),
            typeof(MicrosoftMotionControllerProfile),
            typeof(HPReverbG2ControllerProfile),
            typeof(KHRSimpleControllerProfile),
        };

        var list = new List<OpenXRFeature>();
        foreach (var t in wanted)
        {
            try
            {
                if (!typeof(OpenXRFeature).IsAssignableFrom(t)) continue;
                var feature = (OpenXRFeature)ScriptableObject.CreateInstance(t);
                // enabled has a public/protected setter depending on version — set via Traverse.
                Traverse.Create(feature).Property("enabled").SetValue(true);
                list.Add(feature);
            }
            catch { }
        }
        if (list.Count == 0)
            Plugin.Logger.LogError("OpenXR started without any controller interaction profiles; motion-controller input will not work.");
        return list.ToArray();
    }

    private static bool InitAndStart()
    {
        // Ensure XRGeneralSettings.Instance is ours and manager-init-on-start is enabled — InitXRSDK
        // reads XRGeneralSettings.Instance and Instance.m_InitManagerOnStart.
        TrySetXRGeneralSettingsInstance(_general);
        AccessTools.Field(typeof(XRGeneralSettings), "m_InitManagerOnStart")?.SetValue(_general, true);

        // Use the system default OpenXR runtime.
        Environment.SetEnvironmentVariable("XR_RUNTIME_JSON", null);

        // RepoXR's proven Unity-6 sequence: XRGeneralSettings.InitXRSDK() then .Start() (both private).
        InvokePrivate(_general, "InitXRSDK");

        InvokePrivate(_general, "Start");

        var displays = new List<XRDisplaySubsystem>();
        SubsystemManager.GetSubsystems(displays);

        return displays.Count > 0;
    }

    private static void InvokePrivate(object target, string method)
    {
        try
        {
            var m = AccessTools.Method(target.GetType(), method);
            if (m == null) return;
            m.Invoke(target, null);
        }
        catch { }
    }

    /// <summary>Request a floor-level tracking origin so physical head height maps correctly.</summary>
    private static void SetFloorTrackingOrigin()
    {
        try
        {
            var inputs = new List<XRInputSubsystem>();
            SubsystemManager.GetSubsystems(inputs);
            foreach (var input in inputs)
            {
                if (input.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor))
                    input.TryRecenter();
            }
        }
        catch { }
    }

    private static bool VerifySubsystemManifest()
    {
        var descriptors = new List<ISubsystemDescriptor>();
        SubsystemManager.GetAllSubsystemDescriptors(descriptors);
        var ids = descriptors.Select(d => d.id).ToList();
        var ok = ids.Any(id => id.Contains("OpenXR Display")) && ids.Any(id => id.Contains("OpenXR Input"));
        return ok;
    }

    // ---- reflection helpers for internal XR members (version tolerant) ----

    private static void SetActiveLoaders(XRManagerSettings manager, List<XRLoader> loaders)
    {
        // activeLoaders getter returns the backing list in current versions.
        try
        {
            var current = manager.activeLoaders as List<XRLoader>;
            if (current != null)
            {
                current.Clear();
                current.AddRange(loaders);
                return;
            }
        }
        catch { }

        // Fallback: set the private m_Loaders field directly.
        var field = AccessTools.Field(typeof(XRManagerSettings), "m_Loaders");
        field?.SetValue(manager, loaders);
    }

    private static void TrySetXRGeneralSettingsInstance(XRGeneralSettings general)
    {
        var field = AccessTools.Field(typeof(XRGeneralSettings), "s_RuntimeSettingsInstance")
                    ?? AccessTools.Field(typeof(XRGeneralSettings), "s_Instance")
                    ?? AccessTools.Field(typeof(XRGeneralSettings), "k_SettingsInstance");
        field?.SetValue(null, general);
    }

    private static void SetFeatures(OpenXRSettings settings, OpenXRFeature[] features)
    {
        // 'features' is an internal property/field on OpenXRSettings.
        var prop = AccessTools.Property(typeof(OpenXRSettings), "features");
        if (prop != null && prop.CanWrite)
        {
            prop.SetValue(settings, features);
            return;
        }
        var field = AccessTools.Field(typeof(OpenXRSettings), "features")
                    ?? AccessTools.Field(typeof(OpenXRSettings), "m_features")
                    ?? AccessTools.Field(typeof(OpenXRSettings), "featureSets");
        field?.SetValue(settings, features);
    }
}
