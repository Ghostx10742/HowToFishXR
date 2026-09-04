using System;
using System.Collections.Generic;
using HowToFishVR.Input;
using UnityEngine;

namespace HowToFishVR.VR;

/// <summary>
/// Writes the HMD head pose onto every active render camera in <see cref="Application.onBeforeRender"/>
/// — the latest possible moment before the stereo render — so both eyes use the same fresh pose.
///
///  - GAME (a local player exists): the gameplay camera shares the rig origin so the hands line up with
///    the controllers; full 6DoF, the world renders normally.
///  - MENU (no local player): the menu scene renders directly in stereo; the camera is head-tracked
///    device-relative from the live menu-camera position. NOTE: the menu camera orbits, so this currently
///    drags the view around ("resetting"). Stabilization is being designed separately.
/// </summary>
public static class VRCameraPoser
{
    public static VRCameraPoser_Behaviour Instance;

    public static void Create()
    {
        if (Instance != null) return;
        var go = new GameObject("HowToFishVR CameraPoser");
        UnityEngine.Object.DontDestroyOnLoad(go);
        Instance = go.AddComponent<VRCameraPoser_Behaviour>();
    }
}

public class VRCameraPoser_Behaviour : MonoBehaviour
{

    // First-person-on-ragdoll anchor: the dead body's head (mouth transform sits on the face). Returns
    // false when not dead / no ragdoll yet, so normal 6DoF is used.
    private static bool TryGetRagdollHead(out Vector3 pos)
    {
        pos = default;
        try
        {
            var p = Player.LocalPlayer;
            if (p == null || p.Dying == null || !p.Dying.IsDead) return false;
            var rag = p.Dying.DeadPlayer;
            if (rag == null) return false;
            if (rag._mouthUpper != null) { pos = rag._mouthUpper.position; return true; }
            pos = rag.transform.position + Vector3.up * 0.2f; return true;
        }
        catch { return false; }
    }

    // ---- Dead-head hiding (first-person death): the camera is locked to the ragdoll's mouth, so the
    // head model would sit in your face. Fold the head bone to zero scale while dead (the same "camera
    // inside the skull" trick the restored body uses) and restore it on respawn. ----
    private static Transform _deadHead;
    private static Vector3 _deadHeadScale = Vector3.one;
    private static bool _deadHeadHidden;

    private static void HideDeadHead()
    {
        try
        {
            if (_deadHeadHidden && _deadHead != null) return; // already hidden
            if (_deadHead == null)
            {
                var p = Player.LocalPlayer;
                if (p == null || p.Dying == null || p.Dying.DeadPlayer == null) return;
                _deadHead = FindHeadBone(p.Dying.DeadPlayer.transform);
                if (_deadHead == null) { _deadHeadHidden = true; return; }
                _deadHeadScale = _deadHead.localScale;
            }
            if (_deadHead != null) _deadHead.localScale = Vector3.zero;
            _deadHeadHidden = true;
        }
        catch { }
    }

    private static void RestoreDeadHead()
    {
        if (_deadHead != null && _deadHeadHidden) _deadHead.localScale = _deadHeadScale;
        _deadHead = null;
        _deadHeadHidden = false;
    }

    private static Transform FindHeadBone(Transform root)
    {
        if (root == null) return null;
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t == null || t.name == null) continue;
            string n = t.name;
            if (n.IndexOf("Head", StringComparison.OrdinalIgnoreCase) >= 0 &&
                n.IndexOf("Holder", StringComparison.OrdinalIgnoreCase) < 0 &&
                n.IndexOf("hold", StringComparison.OrdinalIgnoreCase) < 0 &&
                n.IndexOf("mouth", StringComparison.OrdinalIgnoreCase) < 0 &&
                n.IndexOf("hair", StringComparison.OrdinalIgnoreCase) < 0)
                return t;
        }
        return null;
    }


    // Exact menu camera target follow. Reading frame-to-frame boat deltas failed after focus gaps: a large
    // amount of legitimate travel arrived in one callback and was mistaken for a teleport, leaving VR
    // behind. The boat's authored camera target is never written by the mod, so following it absolutely
    // is feedback-free and catches up immediately regardless of render/update cadence.
    private static SelfDrivingBoat _menuBoat;
    private static Transform _menuCameraTarget;
    private static Vector3 _menuTargetLocalOffset;
    private static bool _menuAnchorInit;
    private static bool _customizationPoseActive;
    private static SelfDrivingBoat _customizationBoat;
    private static float _customizationNeutralHeadYaw;
    private static float _customizationStartTurnYaw;

    /// <summary>
    /// Feedback-free absolute menu anchor. It follows the boat's unwritten default/customization camera
    /// target, including translation and turning. The small initial authored camera offset is stored in
    /// target-local space, so the view rides the target rigidly without ever reading our overwritten camera
    /// again. This remains exact after focus loss because it does not integrate per-frame deltas.
    /// </summary>
    private static Vector3 MenuBoatAnchor(Camera primary)
    {
        SelfDrivingBoat boat = null;
        Transform target = null;
        Vector3 targetPosition = default;
        try
        {
            var mm = MainMenuManager._instance;
            if (mm != null) boat = mm._boat;
            if (boat != null)
            {
                bool customization = LocalSkin.IsInSkinCustomization;
                target = customization ? boat._skinCustomizationPos : boat._defaultCamPos;
                if (target == null) target = boat.transform;
                targetPosition = target.position;
                targetPosition.y = customization ? boat._skinCustomizationHeight : boat._defaultHeight;
            }
        }
        catch { }

        if (boat == null || target == null)
        {
            return primary.transform.position;
        }

        if (!_menuAnchorInit || _menuBoat != boat)
        {
            _menuBoat = boat;
            // Preserve the small authored offset only for the default sailing camera. When switching
            // to character customization the game intentionally chooses a different camera target;
            // carrying the old offset across that switch is what kept the avatar out of view.
            if (!LocalSkin.IsInSkinCustomization)
                _menuTargetLocalOffset = Quaternion.Inverse(target.rotation) * (primary.transform.position - targetPosition);
            _menuAnchorInit = true;
        }
        _menuCameraTarget = target;
        return LocalSkin.IsInSkinCustomization
            ? targetPosition
            : targetPosition + target.rotation * _menuTargetLocalOffset;
    }

    /// <summary>During customization, face the boat's authored customization look-at while retaining
    /// full head motion. VRRig calls this in Update so the camera, hands, UI, and laser share one basis.</summary>
    internal static bool TryGetCustomizationOrigin(Quaternion headRotation, float turnYaw, out Quaternion origin)
    {
        origin = Quaternion.identity;
        try
        {
            if (!MainMenuManager.IsInMenu || !LocalSkin.IsInSkinCustomization)
            {
                _customizationPoseActive = false;
                _customizationBoat = null;
                return false;
            }
            var mm = MainMenuManager._instance;
            var boat = mm != null ? mm._boat : null;
            if (boat == null || boat._skinCustomizationPos == null || boat._skinCustomizationLookAt == null)
                return false;
            if (!_customizationPoseActive || _customizationBoat != boat)
            {
                _customizationPoseActive = true;
                _customizationBoat = boat;
                _customizationNeutralHeadYaw = headRotation.eulerAngles.y;
                _customizationStartTurnYaw = turnYaw;
            }
            Vector3 cameraPos = boat._skinCustomizationPos.position;
            cameraPos.y = boat._skinCustomizationHeight;
            Vector3 towardModel = boat._skinCustomizationLookAt.position - cameraPos;
            towardModel.y = 0f;
            if (towardModel.sqrMagnitude < 1e-6f) return false;
            float desiredYaw = Quaternion.LookRotation(towardModel, Vector3.up).eulerAngles.y;
            float artificialDelta = Mathf.DeltaAngle(_customizationStartTurnYaw, turnYaw);
            origin = Quaternion.Euler(0f, desiredYaw - _customizationNeutralHeadYaw + artificialDelta, 0f);
            return true;
        }
        catch { return false; }
    }

    private static void ClearTp()
    {
        _menuAnchorInit = false;
        _menuBoat = null;
        _menuCameraTarget = null;
        _customizationPoseActive = false;
        _customizationBoat = null;
    }

    private void OnEnable() => Application.onBeforeRender += Pose;
    private void OnDisable() => Application.onBeforeRender -= Pose;

    public void Recalibrate() { /* rig owns calibration */ }

    private void Pose()
    {
        // NEVER let an exception escape — it would unregister the onBeforeRender callback and freeze
        // the view to a glued flat frame.
        try
        {
            if (!Plugin.HeadsetActive) return; // no LIVE headset -> don't pose the camera to a desk HMD
            var a = VRActions.Instance;
            var rig = VRRig.Instance;
            if (a == null || rig == null || rig.Root == null || !rig.IsCalibrated) return;

            Vector3 headPos = a.HeadPos;
            Quaternion headRot = a.HeadRot;
            Quaternion originRot = rig.Root.rotation;
            Vector3 headCalib = rig.HeadCalib;

            Camera gameCam = null;
            try { var p = Player.LocalPlayer; if (p != null && p.Camera != null) gameCam = p.Camera.Cam; } catch { }

            if (rig.Head != null) { rig.Head.localPosition = headPos; rig.Head.localRotation = headRot; }
            if (rig.LeftHand != null) { rig.LeftHand.localPosition = a.LeftPos; rig.LeftHand.localRotation = a.LeftRot; }
            if (rig.RightHand != null) { rig.RightHand.localPosition = a.RightPos; rig.RightHand.localRotation = a.RightRot; }

            // SURGICAL: touch ONLY the single camera that is actually your view. Never null the target
            // texture of other cameras (water reflections / previews activate a few seconds into play and
            // nulling those makes them render over your eyes = the "freeze"/takeover).
            if (gameCam != null)
            {
                // GAME: drive the real player camera from the rig origin -> full 6DoF, hands line up.
                ClearTp();

                // THE FREEZE FIX: the game deactivates the player camera's GameObject (or an ancestor) a
                // few seconds after spawn, leaving ZERO active cameras — which stalls the XR frame loop and
                // freezes the headset pose. Force the whole chain active so first-person rendering (and
                // head tracking) NEVER stops. This also enforces "always first-person".
                if (!gameCam.isActiveAndEnabled)
                {
                    for (var t = gameCam.transform; t != null; t = t.parent)
                        if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                    if (!gameCam.enabled) gameCam.enabled = true;
                }

                if (gameCam.targetTexture != null) gameCam.targetTexture = null;
                Patches.CameraOutputPatches.EnsureXR(gameCam);

                // DEATH. FirstPerson: lock the camera to the dead body's head (free head-look, IRL
                // walking doesn't move you) and fold the head model away so you don't see it. ThirdPerson:
                // the GAME'S DEFAULT third-person death cam, head-tracked for VR: the camera POSITION
                // orbits your dead body driven by your HEAD yaw/pitch (the vanilla PlayerDeathCam.
                // OrbitCamera sphere — body at centre, camera on the far side of it) but the camera
                // ROTATION is your full head rotation, exactly like first person. So when you LOOK at
                // your body it is dead-centre in front of you (the camera sits behind it relative to your
                // look), and when you look away you can look anywhere — nothing is glued to the body.
                Vector3 ragdollHead;
                if (TryGetRagdollHead(out ragdollHead))
                {
                    if (VRConfig.DeathView.Value == DeathView.ThirdPerson)
                    {
                        RestoreDeadHead();
                        // Anchor the orbit to the RAGDOLL ROOT (the fallen body), not the head — the head
                        // swings as the ragdoll settles, and the old head+0.15 anchor aimed the camera at
                        // empty air above the body ("I can't see my body"). Chest-ish centre reads well
                        // for both a standing and a sprawled ragdoll.
                        Vector3 bodyPos = ragdollHead;
                        try
                        {
                            var rag = Player.LocalPlayer != null && Player.LocalPlayer.Dying != null
                                ? Player.LocalPlayer.Dying.DeadPlayer : null;
                            if (rag != null && rag.transform != null) bodyPos = rag.transform.position;
                        }
                        catch { }
                        bodyPos += Vector3.up * 0.55f;

                        // Orbit position from the headset's yaw+pitch (vanilla OrbitCamera): the camera
                        // sits on the OPPOSITE side of the body from where you're looking, so the body is
                        // centred whenever you face it.
                        Vector3 hmdEuler = (originRot * headRot).eulerAngles;
                        float pitch = hmdEuler.x > 180f ? hmdEuler.x - 360f : hmdEuler.x;
                        pitch = Mathf.Clamp(pitch, -85f, 85f);
                        Quaternion look = Quaternion.Euler(pitch, hmdEuler.y, 0f);
                        float dist = 2.6f;
                        Vector3 orbit = bodyPos + look * (Vector3.forward * -dist);
                        Vector3 toBody = (bodyPos - orbit).normalized;
                        if (Physics.Raycast(bodyPos, toBody, out var hit, dist, GameInfo.LevelLayer))
                            orbit = hit.point - toBody * 0.25f;
                        gameCam.transform.position = orbit;
                        // FULL 6DoF: the view is your real head rotation, exactly like first-person death
                        // — you can look away from the body, up, down, anywhere. Nothing locks onto it.
                        gameCam.transform.rotation = originRot * headRot;
                    }
                    else
                    {
                        HideDeadHead();
                        gameCam.transform.position = ragdollHead;
                        gameCam.transform.rotation = originRot * headRot;
                    }
                }
                else
                {
                    RestoreDeadHead();
                    gameCam.transform.position = rig.Root.TransformPoint(headPos);
                    gameCam.transform.rotation = originRot * headRot;
                }
            }
            else
            {
                // MENU: pick the primary screen camera, follow its SMOOTH sailing motion (teleport-
                // absorbed) and head-track from that. The rig shares this same anchor, so the UI panel and
                // laser (which hang off the rig) stay locked to your head/controllers.
                Camera primary = null;
                // Prefer the game's actual menu camera. Camera.allCameras ordering is not stable and can
                // select a reflection/utility camera, especially after focus changes or UI transitions.
                try
                {
                    var mm = MainMenuManager._instance;
                    if (mm != null && mm._menuCam != null && mm._menuCam.isActiveAndEnabled &&
                        mm._menuCam.targetTexture == null)
                        primary = mm._menuCam;
                }
                catch { }
                foreach (var cam in Camera.allCameras)
                    if (primary == null && cam != null && cam.isActiveAndEnabled && cam.targetTexture == null)
                    { primary = cam; break; }
                if (primary != null)
                {
                    Vector3 anchor = MenuBoatAnchor(primary); // ride the boat's motion (no feedback), absorb the loop
                    rig.Root.position = anchor - originRot * headCalib;
                    Patches.CameraOutputPatches.EnsureXR(primary);
                    primary.transform.position = anchor + originRot * (headPos - headCalib);
                    primary.transform.rotation = originRot * headRot;
                }
            }

            // Finalize camera-dependent effects NOW, after the actual rendered pose is known. During
            // death the game can reset the camera earlier in the frame; checking/placing these before
            // this point made water state and the translucent death vignette use that stale position.
            Patches.WaterUnderwaterPatches.UpdateFromRenderedVRView();
            VRDamageOverlay.Instance?.RepositionNow();

            // FINAL TRACKING PASS. The camera and rig above use OpenXR's newest predicted render pose,
            // which is newer than the pose sampled during the normal Update/LateUpdate loop. Re-seat the
            // visible hand bones from those freshly updated rig hands BEFORE solving the arms. Previously
            // only the rig transforms were refreshed here, while the hand-bone IK targets stayed on the
            // prior normal-frame sample; the camera therefore moved smoothly but the arms visibly stepped
            // and felt slow. Hand placement + IK now consume the same render-timed pose as the camera.
            try
            {
                var localPlayer = Player.LocalPlayer;
                if (localPlayer != null && localPlayer.Hands != null)
                    Patches.PlayerHandsPatches.ApplyTrackedHands(localPlayer.Hands);
                Body.ArmRig.SolveCurrentPose(localPlayer);
            }
            catch { }

            // Place the UI panels NOW (same onBeforeRender pass, after the camera + rig are posed) so they
            // are locked to the view with zero lag — a one-frame lag (placing them in LateUpdate) is what
            // made them "stutter" relative to the world at the loop point. The laser draws itself in its
            // own onBeforeRender callback, which is registered after this one, so it is already in sync.
            UI.VRUIManager.Instance?.RepositionNow();

            // Desktop mirror: the PC window should show the VR view (MAVR-style RawImage of a mirror
            // camera). Re-assert the built-in mirror mode every frame (it can come up black / reset), and
            // point the mirror camera at the just-posed view camera so the window shows this exact frame.
            try { UnityEngine.XR.XRSettings.gameViewRenderMode = UnityEngine.XR.GameViewRenderMode.LeftEye; } catch { }
            VRDesktopMirror.Instance?.SyncToViewCamera();

        }
        catch { }
    }
}
