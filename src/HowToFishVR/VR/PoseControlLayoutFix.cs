using System;
using System.Collections;
using HarmonyLib;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Layouts;
using UnityEngine.InputSystem.Utilities;
using UnityEngine.XR.OpenXR.Features.Interactions;
using PoseControl = UnityEngine.XR.OpenXR.Input.PoseControl;

namespace HowToFishVR.VR;

/// <summary>
/// Works around the Unity Input System / OpenXR "PoseControl" layout conflict
/// (https://issuetracker.unity3d.com/issues/175/posecontrol-in-inputsystemxr-and-posecontrol-in-openxr-conflict,
/// HowToFishXR issues #1 and #2: Valve Index controllers track but produce no input).
///
/// The OpenXR plugin registers its <see cref="PoseControl"/> under the layout name "Pose", while
/// Input System 1.19 ships a conflicting UnityEngine.InputSystem.XR.PoseControl. When a controller
/// device layout is built, the referenced "Pose" layout can resolve to the Input System's type, so
/// the device's devicePose/pointer controls are instantiated as the wrong PoseControl class and
/// ValveIndexController.FinishSetup throws
/// "Expected control 'devicePose' to be of type 'PoseControl' but is of type 'PoseControl' instead" —
/// the device is discarded and every stick/trigger/grip on the controller is dead while head
/// tracking and menu lasers (which don't use these devices) keep working.
///
/// The trap: InputSystem.RegisterLayout writes to InputManager.m_Layouts, but the device builder
/// resolves layouts through InputControlLayout.s_Layouts, a struct snapshot whose dictionaries can
/// be a different (older) set than the manager's. Repairing only the public world changes nothing
/// the builder can see, so this fix writes the correct registration into BOTH collections, once at
/// OpenXR startup and again before every native device build.
/// </summary>
public static class PoseControlLayoutFix
{
    private static readonly Type OpenXRPoseControl = typeof(PoseControl);
    private static readonly Type OpenXRIndexController = typeof(ValveIndexControllerProfile.ValveIndexController);
    private static Harmony _harmony;

    /// <summary>Installs the repair and the device-build guard. Safe to call once VR is up.</summary>
    public static void Install()
    {
        try
        {
            Repair();
            _harmony ??= new Harmony("com.jaxon.howtofishvr.posecontrolfix");
            var discovered = AccessTools.Method(
                AccessTools.TypeByName("UnityEngine.InputSystem.InputManager"),
                "OnNativeDeviceDiscovered");
            if (discovered != null)
            {
                _harmony.Patch(discovered,
                    prefix: new HarmonyMethod(typeof(PoseControlLayoutFix), nameof(BeforeNativeDeviceDiscovered)));
            }
        }
        catch { /* best effort: older/newer package combinations may not need this */ }
    }

    private static void BeforeNativeDeviceDiscovered()
    {
        Repair();
    }

    /// <summary>
    /// Ensures the "Pose" and "PoseControl" layout names resolve to the OpenXR plugin's own
    /// PoseControl in both layout collections. Each write is skipped when already correct, so on
    /// package combinations without the conflict this is a no-op.
    /// </summary>
    private static void Repair()
    {
        try
        {
            InputSystem.RegisterLayout(OpenXRPoseControl);
            InputSystem.RegisterLayout(OpenXRIndexController);

            PatchCollection(ManagerCollection());
            PatchCollection(StaticCollection());
        }
        catch { /* layout repair is best effort */ }
    }

    private static object ManagerCollection()
    {
        try
        {
            var manager = AccessTools.Property(typeof(InputSystem), "manager")?.GetValue(null, null)
                          ?? AccessTools.Field(typeof(InputSystem), "s_Manager")?.GetValue(null);
            return manager == null ? null : AccessTools.Field(manager.GetType(), "m_Layouts").GetValue(manager);
        }
        catch { return null; }
    }

    private static object StaticCollection()
    {
        try
        {
            return AccessTools.Field(typeof(InputControlLayout), "s_Layouts").GetValue(null);
        }
        catch { return null; }
    }

    private static void PatchCollection(object collection)
    {
        if (collection == null) return;
        var t = collection.GetType();
        if (!(AccessTools.Field(t, "layoutTypes")?.GetValue(collection) is IDictionary types)) return;

        var poseKey = new InternedString("Pose");
        var poseControlKey = new InternedString("PoseControl");

        // layoutStrings is consulted before layoutTypes; drop any stale JSON registrations.
        if (AccessTools.Field(t, "layoutStrings")?.GetValue(collection) is IDictionary strings)
        {
            strings.Remove(poseKey);
            strings.Remove(poseControlKey);
        }

        if (!ContainsType(types, poseKey)) types[poseKey] = OpenXRPoseControl;
        if (!ContainsType(types, poseControlKey)) types[poseControlKey] = OpenXRPoseControl;
    }

    private static bool ContainsType(IDictionary types, InternedString key)
    {
        return types.Contains(key) && types[key] as Type == OpenXRPoseControl;
    }
}
