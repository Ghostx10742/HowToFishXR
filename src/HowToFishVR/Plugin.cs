using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace HowToFishVR;

[BepInPlugin(Guid, Name, Version)]
[BepInProcess("How to Fish.exe")]
public class Plugin : BaseUnityPlugin
{
    public const string Guid = "com.jaxon.howtofishvr";
    public const string Name = "HowToFishXR";
    public const string Version = "1.2.0";

    public static Plugin Instance { get; private set; }
    public new static ManualLogSource Logger { get; private set; }
    public static Harmony HarmonyInstance { get; private set; }

    /// <summary>True once the OpenXR loader has been initialized and a headset is active.</summary>
    public static bool VREnabled { get; internal set; }

    /// <summary>
    /// True only while a REAL headset session is live RIGHT NOW: VR was enabled AND the XR display is
    /// active AND a tracked center-eye device exists. A "VR" launch with the headset powered off / on a
    /// desk never gets here — so the mod's local systems (hand placement, held-item pinning, camera
    /// posing) can't corrupt the body of a player who is effectively playing flatscreen in a VR-chosen
    /// session. This is the same gate the pose broadcast uses; local systems now use it too.
    /// </summary>
    public static bool HeadsetActive
    {
        get
        {
            if (!VREnabled) return false;
            try
            {
                if (!UnityEngine.XR.XRSettings.isDeviceActive) return false;
                return UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.CenterEye).isValid;
            }
            catch { return VREnabled; }
        }
    }

    /// <summary>Chosen once at startup from the persistent Disable VR setting or the one-launch
    /// --disable-vr command-line flag, following RepoXR's launch model.</summary>
    public static bool VRRequested { get; private set; }

    private void Awake()
    {
        Instance = this;
        Logger = base.Logger;

        // VR must keep its full Unity player loop alive when the desktop window loses focus. Set this
        // before OpenXR, the menu boat, physics, and input are initialized (VRRig also reasserts it).
        Application.runInBackground = true;

        // Ensure the managed XR assemblies (shipped next to this plugin) are resolvable.
        ResolveBundledAssemblies();

        VRConfig.Init(base.Config);
        VRRequested = ResolveVRRequested();

        HarmonyInstance = new Harmony(Guid);

        try
        {
            Entrypoint.Initialize();
        }
        catch (Exception ex)
        {
            Logger.LogError($"Fatal error during VR initialization: {ex}");
        }
    }

    // RepoXR-style startup gate: VR is the default, while config gives a persistent flatscreen mode and
    // --disable-vr gives launchers/Steam a one-run override. Nothing XR-related is touched when disabled.
    private bool ResolveVRRequested()
    {
        bool commandLineDisabled = Environment.GetCommandLineArgs()
            .Contains("--disable-vr", StringComparer.OrdinalIgnoreCase);
        bool disabled = VRConfig.DisableVR.Value || commandLineDisabled;
        return !disabled;
    }

    /// <summary>
    /// BepInEx resolves plugin dependencies from the plugins folder, but the managed Unity.XR.*
    /// assemblies are loaded reflectively by Unity's XR system and may not be found automatically.
    /// Hook AssemblyResolve to load them from our plugin directory on demand.
    /// </summary>
    private void ResolveBundledAssemblies()
    {
        var here = Path.GetDirectoryName(Info.Location);

        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            var name = new AssemblyName(args.Name).Name + ".dll";
            var candidate = Path.Combine(here, name);
            if (File.Exists(candidate))
            {
                try { return Assembly.LoadFrom(candidate); }
                catch { /* fall through */ }
            }

            var deps = Path.Combine(here, "RuntimeDeps", name);
            if (File.Exists(deps))
            {
                try { return Assembly.LoadFrom(deps); }
                catch { /* fall through */ }
            }

            return null;
        };
    }
}
