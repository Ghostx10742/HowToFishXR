using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;

namespace HowToFishVR.Preload;

/// <summary>
/// BepInEx preloader patcher. Runs before the Unity engine finishes booting so we can drop the
/// native OpenXR runtime libraries into the game files and register the XR subsystem manifest.
/// Modelled on RepoXR's preloader (which does the same for the Unity 6 game R.E.P.O.).
/// </summary>
public static class Preload
{
    // Required by BepInEx patcher contract. We patch no assemblies; we only stage runtime assets.
    public static IEnumerable<string> TargetDLLs { get; } = new string[0];

    private const string DATA_DIR = "How to Fish_Data";

    // Describes the OpenXR subsystem provided by the native UnityOpenXR plugin.
    private const string VR_MANIFEST = @"{
  ""name"": ""OpenXR XR Plugin"",
  ""version"": ""1.14.3"",
  ""libraryName"": ""UnityOpenXR"",
  ""displays"": [ { ""id"": ""OpenXR Display"" } ],
  ""inputs"": [ { ""id"": ""OpenXR Input"" } ]
}";

    private static readonly ManualLogSource Logger =
        BepInEx.Logging.Logger.CreateLogSource("HowToFishVR.Preload");

    public static void Initialize()
    {
        try
        {
            SetupRuntimeAssets();
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"Failed to stage VR runtime assets: {ex}");
        }
    }

    /// <summary>
    /// Copies the native OpenXR libraries into &lt;Game&gt;_Data/Plugins and writes the subsystem
    /// manifest that tells Unity's XR system that an OpenXR provider is available.
    /// </summary>
    private static void SetupRuntimeAssets()
    {
        var root = Path.Combine(Paths.GameRootPath, DATA_DIR);

        // 1. Subsystem manifest
        var subsystems = Path.Combine(root, "UnitySubsystems");
        var openXrManifestDir = Path.Combine(subsystems, "UnityOpenXR");
        Directory.CreateDirectory(openXrManifestDir);

        var manifest = Path.Combine(openXrManifestDir, "UnitySubsystemsManifest.json");
        File.WriteAllText(manifest, VR_MANIFEST);

        // 2. Native plugins into <Game>_Data/Plugins
        var plugins = Path.Combine(root, "Plugins");
        Directory.CreateDirectory(plugins);

        var runtimeDeps = FindRuntimeDeps();

        CopyIfNewer(Path.Combine(runtimeDeps, "UnityOpenXR.dll"), Path.Combine(plugins, "UnityOpenXR.dll"));
        CopyIfNewer(Path.Combine(runtimeDeps, "openxr_loader.dll"), Path.Combine(plugins, "openxr_loader.dll"));
    }

    /// <summary>
    /// RuntimeDeps ships alongside the preloader assembly (BepInEx/patchers/HowToFishVR/RuntimeDeps).
    /// </summary>
    private static string FindRuntimeDeps()
    {
        var here = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        var candidate = Path.Combine(here, "RuntimeDeps");
        if (Directory.Exists(candidate))
            return candidate;

        // Fallback: search the BepInEx tree for a RuntimeDeps folder that contains UnityOpenXR.dll
        var bep = Paths.BepInExRootPath;
        foreach (var dir in Directory.GetDirectories(bep, "RuntimeDeps", SearchOption.AllDirectories))
            if (File.Exists(Path.Combine(dir, "UnityOpenXR.dll")))
                return dir;

        return candidate;
    }

    private static void CopyIfNewer(string src, string dst)
    {
        if (!File.Exists(src))
        {
            Logger.LogError($"Required VR runtime dependency is missing: {src}");
            return;
        }

        if (File.Exists(dst) &&
            new FileInfo(dst).Length == new FileInfo(src).Length)
            return; // already staged

        File.Copy(src, dst, true);
    }

    // Required by BepInEx patcher contract.
    public static void Patch(AssemblyDefinition assembly) { }
}
