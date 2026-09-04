using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
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
            EnsureColdSteamVrRuntimeIsReady();
        }
        catch (System.Exception ex)
        {
            Logger.LogError($"Failed to stage VR runtime assets: {ex}");
        }
    }

    /// <summary>
    /// SteamVR's OpenXR loader can accept the game connection before a cold-started compositor has a
    /// valid HMD render origin. On the affected path SteamVR reports startup failure -203 and Unity can
    /// keep the malformed first stereo orientation (including an upside-down view). Start only the
    /// configured SteamVR runtime early and let its server/compositor settle before Unity creates its
    /// OpenXR instance. Existing SteamVR sessions and every non-SteamVR runtime pass through unchanged.
    /// </summary>
    private static void EnsureColdSteamVrRuntimeIsReady()
    {
        try
        {
            string runtimeManifest = GetActiveOpenXrRuntime();

            if (string.IsNullOrWhiteSpace(runtimeManifest) ||
                (runtimeManifest.IndexOf("steamxr", System.StringComparison.OrdinalIgnoreCase) < 0 &&
                 runtimeManifest.IndexOf("steamvr", System.StringComparison.OrdinalIgnoreCase) < 0))
                return;

            if (SteamVrProcessesReady()) return;

            string steamVrRoot = Path.GetDirectoryName(runtimeManifest);
            string monitor = !string.IsNullOrWhiteSpace(steamVrRoot)
                ? Path.Combine(steamVrRoot, "bin", "win64", "vrmonitor.exe")
                : null;
            if (!string.IsNullOrWhiteSpace(monitor) && File.Exists(monitor))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = monitor,
                    UseShellExecute = true,
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "steam://rungameid/250820",
                    UseShellExecute = true,
                });
            }

            var deadline = System.DateTime.UtcNow.AddSeconds(30);
            while (System.DateTime.UtcNow < deadline && !SteamVrProcessesReady())
                Thread.Sleep(250);

            // Process creation happens slightly before SteamVR publishes a stable HMD render origin.
            // This delay occurs only for a cold SteamVR launch, before the Unity player starts.
            if (SteamVrProcessesReady()) Thread.Sleep(3000);
        }
        catch
        {
            // OpenXR's normal initialization remains the fallback if SteamVR cannot be pre-launched.
        }
    }

    private static bool SteamVrProcessesReady()
    {
        return Process.GetProcessesByName("vrserver").Length > 0 &&
               Process.GetProcessesByName("vrcompositor").Length > 0;
    }

    private static string GetActiveOpenXrRuntime()
    {
        const uint RrfRegSz = 0x00000002;
        uint bytes = 0;
        int result = RegGetValue(HkeyLocalMachine, @"SOFTWARE\Khronos\OpenXR\1", "ActiveRuntime",
            RrfRegSz, System.IntPtr.Zero, null, ref bytes);
        if (result != 0 || bytes < 2) return null;
        var value = new StringBuilder((int)(bytes / 2));
        result = RegGetValue(HkeyLocalMachine, @"SOFTWARE\Khronos\OpenXR\1", "ActiveRuntime",
            RrfRegSz, System.IntPtr.Zero, value, ref bytes);
        return result == 0 ? value.ToString() : null;
    }

    private static readonly System.IntPtr HkeyLocalMachine = new System.IntPtr(-2147483646);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegGetValue(System.IntPtr hkey, string subKey, string value,
        uint flags, System.IntPtr type, StringBuilder data, ref uint dataSize);

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
