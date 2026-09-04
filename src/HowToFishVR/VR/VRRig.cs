using HowToFishVR.Input;
using UnityEngine;

namespace HowToFishVR.VR;

/// <summary>
/// The persistent VR tracking space. A root transform is placed at the local player's feet each
/// frame (rotated by the accumulated turn yaw), with head + hand child transforms driven from the
/// XR device poses. Game patches read <see cref="Head"/>, <see cref="LeftHand"/>, <see cref="RightHand"/>
/// (world poses) to drive the camera, hand bones and held tools.
/// </summary>
[DefaultExecutionOrder(-1000)] // update the rig before the game's camera/hands each frame
public class VRRig : MonoBehaviour
{
    public static VRRig Instance { get; private set; }

    public Transform Root { get; private set; }
    public Transform Head { get; private set; }
    public Transform LeftHand { get; private set; }
    public Transform RightHand { get; private set; }

    public Transform MainHand => VRConfig.Handedness.Value == DominantHand.Right ? RightHand : LeftHand;
    public Transform OffHand  => VRConfig.Handedness.Value == DominantHand.Right ? LeftHand : RightHand;

    /// <summary>Accumulated artificial yaw from snap/smooth turning and recentering (degrees).</summary>
    public float YawOffset;

    /// <summary>True on the exact frame a snap turn fired (so the visible torso spring can be snapped to
    /// the post-snap yaw instead of swinging behind the view). Cleared every frame in HandleTurning.</summary>
    public bool SnapTurned { get; private set; }

    /// <summary>The view yaw (rig yaw + head yaw) AFTER the snap fired this frame.</summary>
    public float SnapTurnYaw { get; private set; }

    public bool IsCalibrated => _calibrated;
    public Vector3 HeadCalib => _headCalib;

    /// <summary>The game's default first-person eye height above the player root (from PlayerCamera.CamHeight).</summary>
    public float GameEyeHeight = 1.5f;

    private bool _snapReady = true;   // MAVR stick re-arm: must return below 0.35 before it can snap again
    private float _invScrollCd;
    private bool _calibrated;
    private Vector3 _headCalib;
    private float _yHoldTime;
    // System (Meta-button) recenter detection: the HMD DEVICE pose jumps when the runtime re-centers the
    // tracking origin; we catch that and re-capture the head reference so the view/body/IK stay aligned.
    private Vector3 _lastHead;
    private bool _lastHeadValid;

    // Roomscale ("CapsuleFollowHeadset"): the body absorbs physical walking via physics MovePosition;
    // _roomOffset is the accumulated world XZ shift so the tracking origin stays put and the camera
    // isn't double-moved. Bounded by the size of your physical play space.
    private Vector3 _roomOffset;
    private Vector2 _lastHmdPlanar;
    private bool _planarInit;

    // DEFERRED RE-ANCHOR (VHVR's headPositionInitialized model — the pattern every top VR mod uses).
    // Transitions that move the body out from under the view — death/respawn, boat exit, new scene/join —
    // must NOT reset the roomscale origin in the event handler itself: the game is often still teleporting
    // the body for a few frames after the event, and tracking may not be settled that frame, so an
    // immediate reset lands early and the view drifts and "never heals". Instead the event REQUESTS a
    // re-anchor and Update CONSUMES it on the next frame that has valid tracking — always after the
    // transition has actually completed. This is the fix for "IK/body not staying accurate after dying,
    // exiting the boat, or joining a new game".
    private bool _needsReanchor;

    // Death: the body ragdolls, so lock the view position (IRL walking does nothing) but keep looking
    // around. _deathAnchor is the head world position captured at the moment of death.
    private bool _wasDead;
    private Vector3 _deathAnchor;
    private bool _setGameView;
    private bool _gameHeightSet;

    public static void Create()
    {
        if (Instance != null) return;

        var go = new GameObject("HowToFishVR Rig");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<VRRig>();
        Instance.Build();
    }

    private void Build()
    {
        Root = transform;
        Head = new GameObject("Head").transform;
        LeftHand = new GameObject("LeftHand").transform;
        RightHand = new GameObject("RightHand").transform;
        Head.SetParent(Root, false);
        LeftHand.SetParent(Root, false);
        RightHand.SetParent(Root, false);

        // Restore the saved calibration: the eye height was persisted by the last F3 calibrate, so a
        // death/respawn or a full game reload keeps your measured height instead of re-deriving it.
        try
        {
            if (VRConfig.SavedEyeHeight.Value > 0.2f)
            {
                GameEyeHeight = VRConfig.SavedEyeHeight.Value;
                _gameHeightSet = true; // keep OUR value; don't let the game's first CamHeight overwrite it
            }
        }
        catch { }
    }

    private void Update()
    {
        var a = VRActions.Instance;
        if (a == null) return;

        // Keep running (and XR tracking updating) even if the game thinks it lost focus — a focus pause
        // freezes the HMD pose while rendering continues (the "freeze a few seconds after spawn").
        if (!Application.runInBackground) Application.runInBackground = true;

        // Desktop mirror: show ONE eye (a clean flat view of what the game sees) instead of the two-lens
        // stereo split.
        if (!_setGameView)
        {
            _setGameView = true;
            try { UnityEngine.XR.XRSettings.gameViewRenderMode = UnityEngine.XR.GameViewRenderMode.LeftEye; } catch { }
        }

        var player = Player.LocalPlayer;
        if (player != null) VRBindings.TryInject();

        // Use the GAME's real eye height (default was too tall). PlayerCamera.CamHeight is the game's eye
        // height above the player root.
        if (player != null && !_gameHeightSet)
        {
            try { if (player.Camera != null) { GameEyeHeight = player.Camera.CamHeight; _gameHeightSet = true; } } catch { }
        }
        // NOTE: mouse-look is already blocked by PlayerCameraPatches.MouseInput; we no longer disable the
        // Mouse InputSystem device (that churned device state and is a suspect for the XR pose freeze).

        // Force XR rendering on every screen camera each frame (menu cam, player cam, etc.).
        Patches.CameraOutputPatches.EnsureAll();
        bool dead = false;
        try { dead = player != null && player.Dying != null && player.Dying.IsDead; } catch { }

        var head = a.HeadPos;

        // Calibrate the head reference once we have a valid tracked pose.
        if (!_calibrated && head.sqrMagnitude > 1e-6f) { _headCalib = head; _calibrated = true; }

        // Consume a pending re-anchor request (death/respawn, boat exit, new scene) now that tracking is
        // valid — reset the roomscale walk origin so the view/body snap back into alignment at the new
        // position. Deferred to here (not to the event handler) so it always lands AFTER the game has
        // finished teleporting the body; that timing is why the immediate reset "never healed".
        if (_needsReanchor && head.sqrMagnitude > 1e-6f)
        {
            _needsReanchor = false;
            _roomOffset = Vector3.zero;
            _lastHmdPlanar = new Vector2(head.x, head.z);
            _planarInit = true;
        }

        // SELF-HEAL bounds backstop (last resort for a transition the deferred re-anchor didn't catch).
        // IMPORTANT for ROOMSCALE: _roomOffset is the player's PHYSICAL walk offset, which is legitimately
        // as large as the play space — so this must NOT clip real walking. The threshold is set well beyond
        // any real room (10 m); only a genuine glitch (a missed map teleport, a tracking explosion) reaches
        // it. Real roomscale walking (a few metres) is always preserved; death/boat/scene transitions are
        // handled by the deferred re-anchor above, not here.
        if (_roomOffset.magnitude > 10f)
        {
            _roomOffset = Vector3.zero;
            _planarInit = false;
        }

        // SYSTEM (Meta-button) RECENTER auto-handling. The HMD DEVICE pose cannot jump far in one frame
        // from real head movement, so a large sudden jump means the runtime re-centered the tracking
        // origin. Left unhandled, head - _headCalib (the roomscale/lean delta the camera is posed by)
        // jumps by the recenter amount and the whole view + full-body IK fly apart ("Meta recenter fucks
        // up the full body IK"). Re-capture the head reference (an auto-recenter) so the view snaps back to
        // the body anchor and the arms/legs stay aligned. Not triggered by game teleports/respawns — those
        // move the WORLD position, not the device pose (which is relative to the tracking origin).
        if (_calibrated && _lastHeadValid && !dead)
        {
            if ((head - _lastHead).magnitude > 0.35f)
            {
                _headCalib = head;
                _roomOffset = Vector3.zero;
                _lastHmdPlanar = new Vector2(head.x, head.z);
            }
        }
        _lastHead = head; _lastHeadValid = true;

        // Apply snap/smooth turns BEFORE placing the rig so the YawOffset is final when the root is
        // placed. That way the view (posed from Root), the restored body (LocalBodyDriver reads the rig
        // pose), the torso/head springs (CurPlayerRot = HMD yaw) and the networked camera yaw all see the
        // new heading IN THE SAME FRAME — no one-frame mismatch, no swing, no double-count.
        HandleTurning();
        FollowBoatYaw();

        var originRot = Quaternion.Euler(0f, YawOffset, 0f);
        // Character customization has its own boat-mounted camera/look-at pair. Use that authored yaw
        // as the whole tracking-space basis so the avatar is centred and the hands/laser remain aligned.
        if (player == null)
        {
            try
            {
                if (VRCameraPoser_Behaviour.TryGetCustomizationOrigin(a.HeadRot, YawOffset, out var customOrigin))
                    originRot = customOrigin;
            }
            catch { }
        }

        // DEVICE-RELATIVE origin: Root is placed so the calibrated head maps to the anchor (eye level)
        // and only the head DELTA moves the view. Camera AND hands share this same Root. NOTHING freezes
        // the view — even on death you keep full 6DoF/stereo (the old dead-branch froze it, which read
        // as the "glued screen" glitch when hurt).
        _wasDead = dead;
        if (player != null && player.Transform != null)
        {
            Vector3 basePos = player.Transform.position;
            // DRIVING THE BOAT: glue the camera to the DRIVER SEAT. The old anchor math rotated the
            // head's device offset (head - _headCalib) around the seat by the accumulated yaw — and that
            // yaw includes the BOAT's own turning (FollowBoatYaw adds it to YawOffset), so every boat
            // turn swung the camera around the seat and it drifted away from the wheel. While driving,
            // map the head EXACTLY onto the seat eye point: Root.position compensates the head offset
            // each frame, so the camera can never leave the wheel no matter how much yaw builds up. The
            // view still rotates with the boat (Root.rotation = originRot incl. FollowBoatYaw) and 6DoF
            // look/lean still works (Head.localPosition/localRotation are untouched below) — you ride
            // the boat 1:1 but stay put in the seat.
            bool drivingSeat = false;
            if (_drivingBoat != null)
            {
                try { if (_drivingBoat.DriverPos != null) { basePos = _drivingBoat.DriverPos.position; drivingSeat = true; } } catch { }
            }
            if (drivingSeat)
            {
                Vector3 seatEye = basePos + Vector3.up * GameEyeHeight;
                Root.position = seatEye - originRot * head;
            }
            else
            {
                Vector3 anchor = basePos - _roomOffset + Vector3.up * GameEyeHeight;
                Root.position = anchor - originRot * _headCalib;
            }
        }
        // menus: keep last eye position (Root.position untouched)
        Root.rotation = originRot;
        Head.localPosition = head; Head.localRotation = a.HeadRot;

        LeftHand.localPosition = a.LeftPos; LeftHand.localRotation = a.LeftRot;
        RightHand.localPosition = a.RightPos; RightHand.localRotation = a.RightRot;

        ApplySprintCrouch();

        // Calibration is deliberately F3-only. VRCalibration owns the edge-triggered F3 state and
        // performs the full recenter/height capture; no controller or other keyboard shortcut starts it.
        if (VRConfig.RecenterKey.Value.IsDown()) Recenter();
    }

    /// <summary>
    /// Collision-safe roomscale. Move the player rigidbody by the headset's physical XZ delta via
    /// physics (so walls block it) and accumulate the same shift into _roomOffset so the origin stays
    /// put. If a wall blocks the body, the camera is pushed back out of the wall automatically.
    /// </summary>
    private void FixedUpdate()
    {
        if (!Plugin.VREnabled) return;
        var a = VRActions.Instance;
        if (a == null) return;

        if (!VRConfig.RoomscaleMovement.Value) { _planarInit = false; return; }

        var player = Player.LocalPlayer;
        var rb = player != null ? player.Rigidbody : null;
        if (player == null || rb == null) { _planarInit = false; return; }

        bool blocked = false;
        try { blocked = Boat.IsDrivingLocally || player.Dying.IsDead || player.BlockInputs; } catch { }

        var hp = a.HeadPos;
        var planar = new Vector2(hp.x, hp.z);

        if (!_planarInit || blocked)
        {
            _lastHmdPlanar = planar;
            _planarInit = true;
            return;
        }

        var deltaLocal = planar - _lastHmdPlanar;
        _lastHmdPlanar = planar;
        if (deltaLocal.sqrMagnitude < 1e-8f) return;

        // Clamp against absurd tracking jumps.
        if (deltaLocal.magnitude > 0.5f) return;

        var deltaWorld = Root.rotation * new Vector3(deltaLocal.x, 0f, deltaLocal.y);
        rb.MovePosition(rb.position + deltaWorld);
        _roomOffset += deltaWorld;
    }

    /// <summary>Capture the standing height (called after the calibration countdown) and recenter.</summary>
    public void CalibrateNow()
    {
        var a = VRActions.Instance;
        if (a != null)
        {
            // Capture the complete tracking baseline used by camera, roomscale, and body IK. The
            // headset's current local pose becomes the neutral pose; hands are explicitly sampled too
            // so the restored body starts from the same frame rather than an old roomscale delta.
            _headCalib = a.HeadPos;
            _lastHmdPlanar = new Vector2(_headCalib.x, _headCalib.z);
            _planarInit = true;
        }
        // Re-capture the GAME eye height from the current player's camera (the anchor uses it), so F3
        // is a FULL calibration — head reference + eye height + roomscale + body. Falls back to the
        // measured head height if no player camera is available yet (same physical measurement).
        try
        {
            var p = Player.LocalPlayer;
            if (p != null && p.Camera != null && p.Camera.CamHeight > 0.2f)
            {
                GameEyeHeight = p.Camera.CamHeight;
                _gameHeightSet = true;
            }
            else GameEyeHeight = _headCalib.y;
        }
        catch { }
        _roomOffset = Vector3.zero;
        _calibrated = true;
        Recenter();
        _lastHmdPlanar = a != null ? new Vector2(a.HeadPos.x, a.HeadPos.z) : _lastHmdPlanar;
        _planarInit = true;
        // PERSIST: save the calibrated values so death/respawn and game reloads keep them.
        try
        {
            VRConfig.SavedEyeHeight.Value = GameEyeHeight;
            VRConfig.SavedHeadCalibY.Value = _headCalib.y;
        }
        catch { }
        try { Body.HowToFishBody.ApplyBodyMode(); } catch { }
    }

    /// <summary>
    /// Request a re-anchor after a transition that moves the body out from under the view — respawn
    /// (ResurrectEffect), boat exit, or a new scene/join. The game teleports the body to a new position,
    /// so the accumulated roomscale walk offset (<c>_roomOffset</c>) no longer matches the new origin and
    /// the view is left displaced ("never heals"). This does NOT reset immediately: it flags the request,
    /// and <see cref="Update"/> consumes it on the next valid-tracking frame — always AFTER the game has
    /// finished teleporting the body (the robust per-frame pattern every top VR mod uses). Calibration
    /// itself (head reference + eye height) is UNTOUCHED — it persists.
    /// </summary>
    public void OnRespawn()
    {
        _needsReanchor = true;
    }

    private bool _sprintToggle;
    private bool _crouchToggle;
    private bool _physCrouch;
    // Physical (IRL) crouch detection: HMD must drop this far below the calibrated standing height
    // before crouching engages, and must rise back above the release line before standing again
    // (hysteresis so a slight head bob while standing doesn't flicker crouch).
    private const float PhysCrouchEngageDrop = 0.22f;
    private const float PhysCrouchReleaseDrop = 0.14f;

    /// <summary>
    /// Drives the game's sprint/crouch state from the thumbstick clicks, honouring the configured
    /// Hold vs Toggle mode. Sprint = left stick click; Crouch = right stick click.
    /// </summary>
    private void ApplySprintCrouch()
    {
        var player = Player.LocalPlayer;
        if (player == null || player.Movement == null) return;

        var a = VRActions.Instance;

        bool sprint;
        if (VRConfig.SprintMode.Value == ButtonMode.Hold)
            sprint = a.SprintButton.IsPressed();
        else
        {
            if (a.SprintButton.WasPressedThisFrame()) _sprintToggle = !_sprintToggle;
            sprint = _sprintToggle;
        }

        bool crouch;
        if (VRConfig.CrouchMode.Value == ButtonMode.Hold)
            crouch = a.CrouchButton.IsPressed();
        else
        {
            if (a.CrouchButton.WasPressedThisFrame()) _crouchToggle = !_crouchToggle;
            crouch = _crouchToggle;
        }

        // Physical (IRL) crouch: when the headset drops below the calibrated standing height, the game
        // character crouches too (capsule + synced IsCrouching, so the restored body's legs animate it
        // exactly like the flatscreen crouch). OR'd with the stick input, so the stick toggle still
        // works on top of physical crouching. Gated on Roomscale Movement (the "physically walk/crouch"
        // setting), like the roomscale walk.
        if (VRConfig.RoomscaleMovement.Value && _calibrated)
        {
            float drop = _headCalib.y - a.HeadPos.y;
            if (_physCrouch)
            {
                if (drop < PhysCrouchReleaseDrop) _physCrouch = false;
            }
            else if (drop >= PhysCrouchEngageDrop) _physCrouch = true;
            if (_physCrouch) crouch = true;
        }

        player.Movement._sprintInput = sprint;
        player.Movement._crouchInput = crouch;
    }

    private Boat _drivingBoat;
    private float _lastBoatYaw;
    private bool _boatYawInit;

    /// <summary>
    /// While driving, rotate the rig WITH the boat so your view follows it (like the flatscreen game) —
    /// turning the boat turns your view. This is a smooth follow (we add the boat's per-frame yaw delta to
    /// the turn offset), NOT a hard lock: you can still free-look with your head. No jump on enter/exit.
    /// </summary>
    private void FollowBoatYaw()
    {
        bool driving = false;
        try { driving = Boat.IsDrivingLocally; } catch { }
        if (!driving) { _drivingBoat = null; _boatYawInit = false; return; }

        if (_drivingBoat == null)
        {
            try
            {
                foreach (var b in Object.FindObjectsByType<Boat>(FindObjectsSortMode.None))
                    if (b != null && b.Driver == Player.LocalPlayer) { _drivingBoat = b; break; }
            }
            catch { }
        }
        if (_drivingBoat == null) return;

        // Use the DRIVER SEAT's yaw (it faces the boat's forward and turns with the boat). The boat root
        // doesn't rotate — only its physics/visual rig does.
        Transform yawSrc = null;
        try { yawSrc = _drivingBoat.DriverPos; } catch { }
        if (yawSrc == null) { try { yawSrc = _drivingBoat.VisualBoat; } catch { } }
        if (yawSrc == null) return;

        float yaw = yawSrc.eulerAngles.y;
        if (!_boatYawInit) { _lastBoatYaw = yaw; _boatYawInit = true; return; }
        YawOffset += Mathf.DeltaAngle(_lastBoatYaw, yaw);
        _lastBoatYaw = yaw;
    }

    private void HandleTurning()
    {
        SnapTurned = false;
        var a = VRActions.Instance;
        if (a == null) return;

        // ARTIFICIAL TURNING (MAVR's approach — Mage Arena VR): works in BOTH body modes, full-body IK
        // included. MAVR never touches the player transform; it accumulates a playspace yaw that rotates
        // the HMD/controller poses, and the body follows because it is placed from those poses. We do the
        // same: YawOffset rotates the rig, LocalBodyDriver sets the body's world yaw from the rig pose,
        // and PlayerBodyPatches snaps the torso/head springs to the post-snap yaw on the exact frame the
        // snap fires — so the whole body rotates with the view and nothing swings behind.
        //
        // SNAP (MAVR TurningProvider): push past the engage threshold to fire; the stick must return
        // below the re-arm threshold before it can snap again — holding the stick does NOT keep spinning
        // you. A haptic pulse fires on each snap (MAVR's haptic on snap).
        // SMOOTH (MAVR): deadzone 0.15, magnitude normalized 0.15..1 so small deflections turn slowly.
        float value = a.TurnAxis.ReadValue<float>();
        if (VRConfig.Turning.Value == TurnMode.Snap)
        {
            if (Mathf.Abs(value) < 0.35f)
            {
                _snapReady = true;
            }
            else if (_snapReady && Mathf.Abs(value) >= 0.75f)
            {
                _snapReady = false;
                Body.ArmRig.PrepareForSnapTurn();
                // Rotate AROUND THE HEAD (MAVR's pivot), not the feet anchor: after roomscale-walking,
                // the head sits far from the anchor, so a yaw-only change would swing the camera by
                // (head offset × angle) — straight through walls ("snap turn messes with collisions").
                RotateYawAboutHead(Mathf.Sign(value) * VRConfig.SnapTurnAngle.Value);
                SnapTurned = true;
                // SnapTurnYaw = the TRUE compound view yaw the rig will render this frame (YawOffset
                // already includes the delta). Compound quaternion, not YawOffset+headYaw — the simple sum
                // drifts whenever the head has pitch/roll, which made the torso "settle" after each snap.
                SnapTurnYaw = (Quaternion.Euler(0f, YawOffset, 0f) * a.HeadRot).eulerAngles.y;
                // MAVR fires a haptic on each snap so you FEEL the turn.
                try
                {
                    var d = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.RightHand);
                    if (d.isValid) d.SendHapticImpulse(0u, 0.35f, 0.06f);
                }
                catch { }
            }
        }
        else // Smooth (MAVR: normalized magnitude past a 0.15 deadzone)
        {
            if (Mathf.Abs(value) >= 0.15f)
            {
                float mag = (Mathf.Abs(value) - 0.15f) / 0.85f;
                RotateYawAboutHead(Mathf.Sign(value) * mag * VRConfig.SmoothTurnSpeed.Value * Time.deltaTime);
            }
        }

        // INVENTORY scroll: the right thumbstick Y (up/down) is the ONLY stick scroll now — the side-to-
        // side (X) switch is gone because X is the turn axis again. Same discrete flick + rate-limit as
        // the old X-scroll that worked well; the game's own ScrollSlotInput is gated off in VR
        // (InventoryPatches) so this is the single path — no double-fire, no glitched double-switches.
        // Up = previous slot, down = next (matches the game's own scroll direction).
        if (_invScrollCd > 0f) _invScrollCd -= Time.deltaTime;
        float y = a.TurnAxisY.ReadValue<float>();
        bool canScroll = true;
        try
        {
            var p = Player.LocalPlayer;
            if (p == null) canScroll = false;
            else if (p.Dying != null && p.Dying.IsDead) canScroll = false;
            else if (p.BlockInputs) canScroll = false;
            else if (Boat.IsDrivingLocally) canScroll = false;
        }
        catch { }
        if (canScroll && Mathf.Abs(y) >= 0.7f && _invScrollCd <= 0f)
        {
            _invScrollCd = 0.25f;
            ScrollInventory(y > 0f ? -1 : 1);
        }
    }

    /// <summary>Cycle the game's inventory one slot (full-body mode R-stick X steering).</summary>
    private static void ScrollInventory(int dir)
    {
        try
        {
            var p = Player.LocalPlayer;
            if (p == null || p.Inventory == null) return;
            var avail = p.Inventory._availableSlots;
            if (avail == null || avail.Count == 0) return;
            int next = p.Inventory._localCurSlot + dir;
            if (next < 0) next = avail.Count - 1;
            else if (next >= avail.Count) next = 0;
            p.Inventory.LocalTrySelectSlot(next);
        }
        catch { }
    }

    /// <summary>
    /// Change the accumulated turn yaw by <paramref name="deltaDeg"/> while keeping the HEAD's world
    /// position FIXED — the rotation pivots around the head (MAVR's model), not the feet anchor. The
    /// head's device offset from the calibrated pose (<c>head - _headCalib</c>) is what a roomscale walk
    /// accumulates; rotating that offset around the anchor would swing the camera sideways by
    /// (walk distance × angle) — through walls. Compensating the roomscale origin keeps the view in
    /// place while the world rotates around you.
    /// </summary>
    private void RotateYawAboutHead(float deltaDeg)
    {
        if (Mathf.Abs(deltaDeg) < 1e-4f) return;
        var a = VRActions.Instance;
        if (a == null) return;
        Vector3 d = a.HeadPos - _headCalib;              // device-relative head offset from calibration
        Quaternion r0 = Quaternion.Euler(0f, YawOffset, 0f);
        YawOffset += deltaDeg;
        Quaternion r1 = Quaternion.Euler(0f, YawOffset, 0f);
        // headWorld = anchor + R*d must stay invariant:
        //   anchor' + R1*d == anchor + R0*d  =>  _roomOffset += (R1*d - R0*d)
        _roomOffset += (r1 * d) - (r0 * d);
    }

    /// <summary>Zero out the current head yaw relative to the body so "forward" faces the play area.</summary>
    public void Recenter()
    {
        var a = VRActions.Instance;
        if (a == null) return;
        Body.ArmRig.PrepareForSnapTurn();
        Vector3 d = a.HeadPos - _headCalib;
        Quaternion r0 = Quaternion.Euler(0f, YawOffset, 0f);
        var headYaw = a.HeadRot.eulerAngles.y;
        YawOffset = -headYaw;
        Quaternion r1 = Quaternion.Euler(0f, YawOffset, 0f);
        // Same head-anchored rotation as snap/smooth turns — recenter must not swing the camera either.
        _roomOffset += (r1 * d) - (r0 * d);
        _planarInit = false;
        VRCameraPoser.Instance?.Recalibrate();
    }

    /// <summary>Planar heading used for locomotion, honouring the head/off-hand config.</summary>
    public Quaternion MovementHeading()
    {
        var src = VRConfig.HeadRelativeMovement.Value ? Head : OffHand;
        var fwd = src.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-4f) fwd = Root.forward;
        return Quaternion.LookRotation(fwd.normalized, Vector3.up);
    }
}
