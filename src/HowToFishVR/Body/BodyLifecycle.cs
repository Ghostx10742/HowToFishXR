using System;
using UnityEngine;

namespace HowToFishVR.Body;

/// <summary>
/// PERSISTENT, per-frame body/IK lifecycle monitor — the "self-heal" mechanism every top full-body VR mod
/// uses, translated to this game. It is the fix for "the full-body IK breaks after death / boat / joining a
/// new game and never realigns."
///
/// The doctrine (verified from source across all the references we studied):
///  - Fallout 4 VR Body (FRIK): every frame re-checks the skeleton root is still valid
///    (isRootNodeValid) and, when it changed (death/respawn, power-armor, new-game body swap, scene load),
///    releases and lazily rebuilds ONLY once every dependency exists (isGameReadyForSkeletonInitialization),
///    after a 1-frame settle delay. FRIK.cpp:155/166/286/343/391.
///  - Valheim VR (vhvr): a persistent Update calls ensurePlayerInstance() + maybeAddVrik() every frame —
///    "create the rig only if the handle is null" — so a missed transition self-corrects next frame.
///    VRPlayer.cs:379-405 / 1485-1497.
///  - LCVR / CWVR / RepoXR: same — rig maintenance runs every frame in Update/LateUpdate; discrete events
///    only construct the session or invalidate it.
///
/// Why our old approach failed: our REBUILD lived in a one-shot event (BodyPatches.InitializePlayer) and
/// our per-frame SOLVE lived on LocalBodyDriver, a component ATTACHED TO THE PLAYER — so when the game
/// destroyed/recreated the player, the driver died with it and, if the event didn't re-fire correctly, the
/// body was never rebuilt. This monitor is DontDestroyOnLoad, so it survives every transition and re-asserts
/// the invariant: "if Full Body is on and there's a live player, the restored body must be built and bound
/// to THAT player — if it isn't, rebuild it the moment all dependencies are ready." Events still fire as a
/// fast path; this is the safety net that can never be missed.
///
/// Calibration (height/scale) is deliberately NOT re-run here — the references all preserve it across
/// transitions and only recalibrate on explicit user request (FRIK/VRIK both do this).
/// </summary>
internal sealed class BodyLifecycle : MonoBehaviour
{
    private static BodyLifecycle _instance;
    private Player _lastPlayer;
    private int _reinitDelay;
    private bool _wasBound;

    internal static void Create()
    {
        if (_instance != null) return;
        var go = new GameObject("HowToFishVR BodyLifecycle");
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<BodyLifecycle>();
    }

    private void Update()
    {
        if (!Plugin.VREnabled) return;
        try { Tick(); }
        catch { }
    }

    private void Tick()
    {
        // The restored body only exists in Full Body IK mode; ApplyBodyMode owns the Hands-Only path.
        if (VRConfig.BodyMode.Value != BodyMode.FullBodyIK) { _lastPlayer = null; _wasBound = false; return; }

        Player player;
        try { player = Player.LocalPlayer; } catch { player = null; }
        if (player == null) { _lastPlayer = null; _wasBound = false; return; } // between sessions / not spawned yet

        // INVALIDATE (FRIK isRootNodeValid): the player object identity changed — a respawn that recreates
        // it, a new scene, or joining a game. Drop the binding and wait one frame before rebuilding so we
        // never bind against a half-constructed player (FRIK kSkeletonInitDelayFramesAfterRelease = 1).
        if (player != _lastPlayer)
        {
            _lastPlayer = player;
            _reinitDelay = 1;
            _wasBound = false;
        }

        // BOUND & LIVE: the body is built for THIS player and every reference is alive. The per-frame solve
        // (LocalBodyDriver.Sync + ArmRig.Refresh) maintains it from here — nothing to rebuild.
        if (HowToFishBody.Active && HowToFishBody.BoundPlayer == player && HowToFishBody.RefsAlive())
        {
            if (!_wasBound) { _wasBound = true; }
            return;
        }

        // STALE / UNBOUND. Wait out the settle delay, then rebuild ONLY once every dependency exists
        // (FRIK isGameReadyForSkeletonInitialization — never rebuild against a partially-built player).
        _wasBound = false;
        if (_reinitDelay > 0) { _reinitDelay--; return; }
        if (!DepsReady(player)) return;

        var objs = player._otherObjects;
        if (objs == null || objs.Count == 0) return;
        try
        {
            HowToFishBody.Build(player, objs);
        }
        catch { }
    }

    /// <summary>All dependencies the restored body needs must be non-null before we rebuild — the exact
    /// gate FRIK's isGameReadyForSkeletonInitialization applies (player root, both hands, arm chain, camera,
    /// the saved body objects). Prevents binding against a player the game is still constructing.</summary>
    private static bool DepsReady(Player p)
    {
        try
        {
            return p.Transform != null && p.Camera != null
                && p.Arms != null && p.Arms._ikRight != null && p.Arms._ikLeft != null
                && p.Hands != null && p.Hands.HandBoneLeft != null && p.Hands.HandBoneRight != null
                && p.Other != null && p.Other._transform != null
                && p._otherObjects != null && p._otherObjects.Count > 0;
        }
        catch { return false; }
    }
}
