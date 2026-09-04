using BepInEx.Configuration;
using UnityEngine;

namespace HowToFishVR;

public enum TurnMode
{
    Snap,
    Smooth
}

/// <summary>Full body = restored body + VR IK arms. Hands only = base game, no body. Snap/smooth turning
/// works in BOTH modes (MAVR-style playspace yaw — the body follows the view).</summary>
public enum BodyMode
{
    FullBodyIK,
    HandsOnly
}

/// <summary>Hold = active while the button is held; Toggle = press once to turn on, again to turn off.</summary>
public enum ButtonMode
{
    Hold,
    Toggle
}

/// <summary>How the death camera behaves in VR: first-person on the ragdoll, or the game's default
/// third-person orbit (made head-tracked for VR).</summary>
public enum DeathView
{
    FirstPerson,
    ThirdPerson
}

/// <summary>
/// All user-facing settings. Plain BepInEx config entries, rendered by our OWN in-game VR Settings
/// panel (UI/VRSettingsPanel.cs) — there is no ModMenu dependency. The panel rows mirror this list;
/// keyboard-only options (Recenter/Calibrate keys) live in the config file only.
/// </summary>
public static class VRConfig
{
    // ---- Launch ----
    public static ConfigEntry<bool> DisableVR;

    // ---- General ----
    public static ConfigEntry<float> WorldScale;

    // ---- Calibration ----
    public static ConfigEntry<bool> RoomscaleMovement;
    public static ConfigEntry<KeyboardShortcut> RecenterKey;
    public static ConfigEntry<KeyboardShortcut> HeightCalibrateKey;
    // Persisted calibration (written on F3, restored at launch): the measured eye height and the head
    // reference height. Saved so death/respawn and game reloads keep your calibration instead of
    // resetting it.
    public static ConfigEntry<float> SavedEyeHeight;
    public static ConfigEntry<float> SavedHeadCalibY;

    // ---- Turning ----
    public static ConfigEntry<TurnMode> Turning;
    public static ConfigEntry<int> SnapTurnAngle;
    public static ConfigEntry<float> SmoothTurnSpeed;
    public static ConfigEntry<float> TurnDeadzone;

    // ---- Locomotion ----
    public static ConfigEntry<float> MovementDeadzone;
    public static ConfigEntry<bool> HeadRelativeMovement; // else hand-relative
    public static ConfigEntry<float> BoatSteerSensitivity;
    public static ConfigEntry<ButtonMode> SprintMode;     // left thumbstick click
    public static ConfigEntry<ButtonMode> CrouchMode;     // right thumbstick click

    // ---- HUD ----
    public static ConfigEntry<bool> HudFollowsHead;   // yaw-only follow
    public static ConfigEntry<float> HudFollowSmoothing;
    public static ConfigEntry<float> HudScale;

    // ---- Interaction ----
    public static ConfigEntry<float> GripThreshold;

    // ---- UI ----
    // Keep the game's real UniversalBlurUI frosted-glass shader on converted panels (the actual pixel-blur
    // the game does) instead of replacing it with a flat dark tint. The blur is a URP RendererFeature that
    // writes a global blur texture the UI shader samples in screen space (stereo-aware, works per-eye in
    // multipass) — so it can render in world-space VR. If it comes out black on your setup, turn this OFF
    // for the flat dark-tint fallback.
    public static ConfigEntry<bool> RealUIBlur;

    // ---- Body ----
    public static ConfigEntry<BodyMode> BodyMode;   // full body + IK arms (turn disabled) vs hands only (base game)
    public static ConfigEntry<UnityEngine.Vector3> BodyOffset; // body position nudge (metres, body space)

    // ---- Death ----
    public static ConfigEntry<DeathView> DeathView; // first-person on the ragdoll vs the default third-person orbit (VR head-tracked)

    // ---- Multiplayer VR sync ----
    public static ConfigEntry<bool> MultiplayerVRSync; // broadcast/apply VR hand poses to modded players
    public static ConfigEntry<float> VRSyncRate;       // pose broadcast rate (Hz)

    public static void Init(ConfigFile cfg)
    {
        DisableVR = cfg.Bind("General", "Disable VR", false,
            "Start How to Fish in flatscreen mode while keeping the mod installed. Can also be overridden for one launch with the --disable-vr Steam launch option. Restart required.");
        WorldScale = cfg.Bind("General", "World Scale", 1.0f,
            new ConfigDescription("Scales your perceived size in the world. 1.0 = default.",
                new AcceptableValueRange<float>(0.5f, 2.0f)));

        RoomscaleMovement = cfg.Bind("Calibration", "Roomscale Movement", true,
            "Allow physically walking/crouching to move the in-game body.");
        RecenterKey = cfg.Bind("Calibration", "Recenter", new KeyboardShortcut(KeyCode.F2),
            "Re-center the headset forward direction.");
        HeightCalibrateKey = cfg.Bind("Calibration", "Calibrate Height", new KeyboardShortcut(KeyCode.F3),
            "Measure and store your standing height / floor level.");
        SavedEyeHeight = cfg.Bind("Calibration", "Saved Eye Height", 0f,
            "Your calibrated eye height (m) above the player root, saved by F3 and restored on launch so death/respawn/reloads keep it.");
        SavedHeadCalibY = cfg.Bind("Calibration", "Saved Head Reference Height", 0f,
            "Your calibrated standing head height (m), saved by F3. Informational; the live head reference is re-captured each launch.");

        Turning = cfg.Bind("Turning", "Turn Mode", TurnMode.Snap,
            "Snap = instant rotation; Smooth = continuous rotation.");
        SnapTurnAngle = cfg.Bind("Turning", "Snap Angle", 45,
            new ConfigDescription("Degrees per snap turn.", new AcceptableValueRange<int>(15, 90)));
        SmoothTurnSpeed = cfg.Bind("Turning", "Smooth Speed", 120f,
            new ConfigDescription("Degrees per second for smooth turning.", new AcceptableValueRange<float>(30f, 360f)));
        TurnDeadzone = cfg.Bind("Turning", "Turn Deadzone", 0.75f,
            new ConfigDescription("Thumbstick threshold to trigger a snap turn (MAVR-style: must return below 0.35 to re-arm).", new AcceptableValueRange<float>(0.1f, 0.95f)));

        MovementDeadzone = cfg.Bind("Locomotion", "Move Deadzone", 0.15f,
            new ConfigDescription("Thumbstick deadzone for movement.", new AcceptableValueRange<float>(0.05f, 0.5f)));
        HeadRelativeMovement = cfg.Bind("Locomotion", "Head Relative Movement", true,
            "Movement direction follows head gaze (off = follows off-hand controller).");
        BoatSteerSensitivity = cfg.Bind("Locomotion", "Boat Steer Sensitivity", 1.0f,
            new ConfigDescription("Multiplier for boat steering from the movement stick.",
                new AcceptableValueRange<float>(0.3f, 2.5f)));
        SprintMode = cfg.Bind("Locomotion", "Sprint Mode", ButtonMode.Hold,
            "Left thumbstick click. Hold = sprint while held; Toggle = click to toggle.");
        CrouchMode = cfg.Bind("Locomotion", "Crouch Mode", ButtonMode.Toggle,
            "Right thumbstick click. Toggle = click to toggle (default); Hold = crouch while held.");

        HudFollowsHead = cfg.Bind("HUD", "HUD Follows Head", true,
            "HUD rotates with head yaw but stays level (not pitched up/down), floating in front of you.");
        HudFollowSmoothing = cfg.Bind("HUD", "HUD Smoothing", 0.1f,
            new ConfigDescription("Lower = snappier HUD follow.", new AcceptableValueRange<float>(0.02f, 0.5f)));
        HudScale = cfg.Bind("HUD", "HUD Scale", 1.0f,
            new ConfigDescription("Scale multiplier for the HUD.", new AcceptableValueRange<float>(0.5f, 2.0f)));

        GripThreshold = cfg.Bind("Interaction", "Grip Threshold", 0.6f,
            new ConfigDescription("Grip pull required to grab.", new AcceptableValueRange<float>(0.1f, 0.95f)));

        RealUIBlur = cfg.Bind("UI", "Real UI Blur", true,
            "Keep the game's real frosted-glass pixel-blur on UI panels (the actual UniversalBlurUI effect) instead of a flat dark tint. Turn OFF if panels render black on your headset.");

        BodyMode = cfg.Bind("Body", "Body Mode", HowToFishVR.BodyMode.FullBodyIK,
            "FullBodyIK = restored body with VR IK arms that bend naturally. HandsOnly = base game: no body shown, hands only. Snap/smooth turning works in both modes (MAVR-style).");
        BodyOffset = cfg.Bind("Body", "Body Offset", new UnityEngine.Vector3(0f, 0f, -0.2f),
            "x, y, z nudge for the whole body in body space, metres. Positive z is forward. Default sits it 20cm back so you are not inside your own collar.");

        DeathView = cfg.Bind("Death", "Death View", HowToFishVR.DeathView.FirstPerson,
            "FirstPerson = camera stays on the ragdoll's head (free head-look, head model hidden). ThirdPerson = the game's default third-person orbit around your body, head-tracked for VR.");

        MultiplayerVRSync = cfg.Bind("Multiplayer", "VR Pose Sync", true,
            "VR players broadcast their full-body poses (head, arms, hands, feet, body yaw) and every modded client — VR or flatscreen — applies them to remote players, so everyone sees VR players exactly as they see themselves (one-hand/two-hand grips included). Head look already syncs via the base game. Flatscreen modded players receive the same syncing as VR players; vanilla hosts are unaffected.");
        VRSyncRate = cfg.Bind("Multiplayer", "VR Pose Sync Rate", 60f,
            new ConfigDescription("Pose broadcast rate in Hz (5-60). MAVR-class smoothness is 60 Hz — higher = smoother but more bandwidth.", new AcceptableValueRange<float>(5f, 60f)));
    }
}
