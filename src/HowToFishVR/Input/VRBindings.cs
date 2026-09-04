using UnityEngine.InputSystem;

namespace HowToFishVR.Input;

/// <summary>
/// Injects XR controller bindings onto the game's existing action names. Every game subsystem binds
/// its handlers by action name (via <c>GameInfo.Input</c>), so adding a controller binding to
/// "PlayerMove", "PlayerLeftClick", etc. makes the vanilla handlers fire from the controllers with no
/// gameplay code changes.
///
/// Deliberately NOT bound (per design):
///  - a dedicated gun-aim button — gun ADS is driven by the two-hand grab instead
///    (<see cref="Patches.PlayerToolMovementPatches"/> forces the ADS state every frame).
///  - PushToTalk.
/// Right-thumbstick X is reserved for snap/smooth turning (handled in the rig), so it is not bound to
/// PlayerLook here.
/// </summary>
internal static class VRBindings
{
    private static bool _injected;

    // action name -> XR control path (canonical XRController controls; routing tuned in-headset)
    private static readonly (string action, string path)[] Map =
    {
        ("PlayerMove",       "<XRController>{LeftHand}/thumbstick"),

        ("PlayerRightClick", "<XRController>{LeftHand}/trigger"),    // secondary / fishing cast (gun ADS overridden by two-hand)

        ("PlayerPickUp",     "<XRController>{RightHand}/grip"),      // grab / interact / enter boat
        ("PlayerJump",       "<XRController>{RightHand}/primaryButton"),   // A
        // Sprint (left stick click) and Crouch (right stick click) are driven manually with
        // configurable Hold/Toggle modes in VRRig — not bound here.
        ("WeaponReload",     "<XRController>{LeftHand}/primaryButton"),    // X
        ("InventoryScroll",  "<XRController>{RightHand}/thumbstick/y"),    // right stick Y = cycle inventory

        ("Pause",            "<XRController>{LeftHand}/secondaryButton"),   // Y (menu button unusable on this runtime)
    };

    // action name -> XR control path + optional interactions ("" = default)
    //
    // DEDICATED CONTROLS (no dual-purpose B):
    //  - B (right secondary) = ChangeBait only. The game's own gamepad scheme puts ChangeBait on R3 and
    //    Drop on Y, but our VR layout keeps Y = Pause, so B is the switch-bait button (matches the
    //    flatscreen B key) and drop lives on the right grip.
    //  - Drop/throw = COMBO: hold the LEFT grip, then press B. B alone stays switch-bait (tap). The
    //    game's own charge-throw is driven by a plain press/release on PlayerDrop (DropInput on press =
    //    start charging, release = throw with the charge), gated in DropComboPatches so it only fires
    //    while the left grip is held — and ChangeBait is gated OFF while the grip is held so the two
    //    never collide on the same button.
    private static readonly (string action, string path, string interactions)[] MapWithInteractions =
    {
        // A lower explicit press point makes fishing/punching respond reliably to a deliberate trigger
        // squeeze instead of depending on the runtime/controller's comparatively finicky default.
        ("PlayerLeftClick", "<XRController>{RightHand}/trigger",         "Press(pressPoint=0.25)"),
        ("PlayerDrop",    "<XRController>{RightHand}/secondaryButton", ""),
        ("ChangeBait",    "<XRController>{RightHand}/secondaryButton", "Tap()"),
        ("WeaponInspect", "<XRController>{LeftHand}/grip",             "Tap()"),
    };

    /// <summary>Injects bindings once GameInfo.Input is available. Returns true when done.</summary>
    public static bool TryInject()
    {
        if (_injected) return true;

        // GameInfo.Input throws until the game's GameInfo singleton + PlayerInput exist (e.g. in the
        // main menu). Guard so we simply retry next frame instead of spamming NREs.
        UnityEngine.InputSystem.PlayerInput input = null;
        try { input = GameInfo.Input; } catch { return false; }

        var asset = input != null ? input.actions : null;
        if (asset == null) return false;

        foreach (var (name, path) in Map)
        {
            var action = asset.FindAction(name, throwIfNotFound: false);
            if (action == null)
            {
                continue;
            }

            // Skip if we've already added this exact binding (idempotent across re-injection).
            bool exists = false;
            foreach (var b in action.bindings)
                if (b.path == path) { exists = true; break; }
            if (exists) continue;

            bool wasEnabled = action.enabled;
            if (wasEnabled) action.Disable();
            action.AddBinding(path);
            if (wasEnabled) action.Enable();
        }

        // Interaction-based bindings (right grip tap = grab / hold = throw, B tap = change bait, left
        // grip tap = inspect). These must be checked separately because their interactions differ from
        // the plain Map entries and re-injection must not duplicate them.
        foreach (var (name, path, interactions) in MapWithInteractions)
        {
            var action = asset.FindAction(name, throwIfNotFound: false);
            if (action == null) continue;

            bool exists = false;
            foreach (var b in action.bindings)
                if (b.path == path && (b.interactions ?? "") == (interactions ?? "")) { exists = true; break; }
            if (exists) continue;

            bool wasEnabled = action.enabled;
            if (wasEnabled) action.Disable();
            action.AddBinding(path, interactions: interactions);
            if (wasEnabled) action.Enable();
        }

        // The injected bindings only read if the XR devices are PAIRED to the game's input user —
        // otherwise the game's PlayerInput ignores them (which is why the controls didn't work).
        try
        {
            foreach (var dev in UnityEngine.InputSystem.InputSystem.devices)
            {
                var layout = dev.layout ?? "";
                var cls = dev.description.deviceClass ?? "";
                if (layout.IndexOf("XR", System.StringComparison.OrdinalIgnoreCase) < 0 &&
                    cls.IndexOf("XR", System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                UnityEngine.InputSystem.Users.InputUser.PerformPairingWithDevice(dev, input.user);
            }
            input.neverAutoSwitchControlSchemes = true;
        }
        catch { }

        _injected = true;
        return true;
    }

    public static void Reset() => _injected = false;

    /// <summary>Fully disable the mouse device so it can't affect the game (look, clicks, UI).</summary>
    public static void SuppressMouse()
    {
        try
        {
            var m = UnityEngine.InputSystem.Mouse.current;
            if (m != null && m.enabled) UnityEngine.InputSystem.InputSystem.DisableDevice(m);
        }
        catch { }
    }
}
