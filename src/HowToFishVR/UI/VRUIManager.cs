using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace HowToFishVR.UI;

/// <summary>
/// HUD/menu VR panels, ported faithfully from Shift At Midnight VR's VRHUD (the approach that works
/// without stacking). Key differences from a naive approach:
///  - Canvases are NOT parented to an anchor — each canvas's world position/rotation is set directly
///    every frame. (Parenting everything to one anchor is what piles them up = "stacking".)
///  - UI is moved to layer 0 (the layer the XR camera renders) so it is visible in the headset.
///  - Menus (have buttons) are placed ONCE and stay put so you can interact; HUD (no buttons) follows
///    head yaw each frame.
///  - Always-visible: copy each Graphic material and force ZTest Always so nothing occludes the UI.
/// </summary>
public class VRUIManager : MonoBehaviour
{
    public static VRUIManager Instance { get; private set; }

    private const float Distance = 2.0f;  // metres in front of the head
    private const float Width = 2.2f;      // panel world width (metres)
    private const float DeathDistance = 1.5f;
    private const float DeathWidth = 3.0f;

    // Only these canvases become VR panels. Everything else (diegetic/world UIs like the casino
    // machine, contextual popups) is left in the world untouched.
    // Exact canvas names to show as VR panels. FXCanvas (full-screen damage
    // effects) and ItemDotsCanvas (world item markers) are intentionally excluded so they stay put.
    private static readonly HashSet<string> Whitelist = new HashSet<string>
    {
        "MainCanvas", "BossCanvas", "ThinkingCanvas", "NPCCanvas",
        "PermaCanvas", "ChatCanvas", "SkipCanvas",
        "MainMenuCanvas", "PauseCanvas", "EndGameCanvas",
        // full-screen effect overlays (blood-when-hurt, underwater tint image) — head-locked, close to
        // face. The underwater look uses the game's global fog plus its authored UnderwaterImage colour
        // overlay (a Default-UI-Material image rendered head-locked with no user-facing processing toggle).
        "FXCanvas", "UnderwaterCanvas", "DeathCanvas",
        // item dots: converted to a world-anchored panel (stays in the world over items, not head-glued)
        "ItemDotsCanvas",
    };

    // Full-screen effect overlays follow the head fully (pitch included) and sit close, so the blood
    // vignette / underwater tint / DEATH screen cover your view like they do on flatscreen. DeathCanvas
    // IS an overlay: the death screen (grey + respawn prompt) must fill your whole VR view, not sit as a
    // small panel in front of you.
    private static readonly HashSet<string> Overlays = new HashSet<string> { "FXCanvas", "UnderwaterCanvas" };

    // Canvases that must behave as in-front panels which ROTATE WITH YOU (head-yaw following) even
    // though they contain buttons — ALL the game's menus behave this way: they sit in front of you and
    // turn with your view, never stay behind you.
    private static readonly HashSet<string> HeadFollowMenus = new HashSet<string>
    {
        "MainMenuCanvas", "ChatCanvas", "SkipCanvas",
        "ThinkingCanvas", "NPCCanvas", "EndGameCanvas",
    };

    // Only canvases explicitly converted for laser input are allowed into the pointer raycast.
    private static readonly HashSet<Canvas> LaserManagedCanvases = new HashSet<Canvas>();

    // Mod-owned world-space canvas that never moves — the anchor for diegetic world UI that must stay
    // glued to a world object (e.g. the item price tag, like the item dots). Children are placed at
    // world coordinates directly (scale 1).
    private static Canvas _priceAnchor;

    public static Canvas GetPriceAnchor(Camera cam)
    {
        if (_priceAnchor == null)
        {
            var go = new GameObject("HowToFishVR PriceAnchor");
            DontDestroyOnLoad(go);
            go.layer = 5;
            _priceAnchor = go.AddComponent<Canvas>();
            _priceAnchor.renderMode = RenderMode.WorldSpace;
            _priceAnchor.sortingOrder = 50; // above the HUD panels, below effect overlays
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(1920f, 1080f);
            // Same world scale as the converted panels, slightly larger so the tag reads clearly over
            // the item. At this scale the game's own 0<->1 show/hide tweens render at the authored
            // size — no tween fighting.
            float s = (Width * VRConfig.HudScale.Value) / 1920f * 1.5f;
            rt.localScale = new Vector3(s, s, s);
        }
        if (cam != null) _priceAnchor.worldCamera = cam;
        return _priceAnchor;
    }

    // Mod-owned head-following panel for the "you caught this fish/creature" popup. The game positions
    // that popup with raw screen PIXELS (WorldToScreenPoint written into transform.position); inside a
    // converted world-space panel those pixels are meaningless (the popup flew off to world coords
    // equal to the pixel numbers — "goes somewhere else"). Instead of fighting the game's pixel math
    // through nested canvases, the popup's holder is reparented HERE — a panel exactly like the HUD
    // panels (2m in front, rotates with head yaw) — and pinned at a fixed spot by ScreenUIPatches.
    private static Canvas _caughtFishPanel;

    public static Canvas GetCaughtFishPanel(Camera cam)
    {
        if (_caughtFishPanel == null)
        {
            var go = new GameObject("HowToFishVR CaughtFishPanel", typeof(RectTransform));
            DontDestroyOnLoad(go);
            go.layer = 5;
            _caughtFishPanel = go.AddComponent<Canvas>();
            _caughtFishPanel.renderMode = RenderMode.WorldSpace;
            _caughtFishPanel.sortingOrder = 60; // above the HUD panels, below effect overlays
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(1920f, 1080f);
            // Same world scale as the converted HUD panels, so the popup's authored layout reads 1:1.
            rt.localScale = Vector3.one * ((Width * VRConfig.HudScale.Value) / 1920f);
            go.AddComponent<GraphicRaycaster>();
        }
        if (cam != null) _caughtFishPanel.worldCamera = cam;
        return _caughtFishPanel;
    }

    // Canvases that stay in the WORLD like the base game (converted to world space + scaled so their
    // screen-sized children read correctly, but never head-followed). Item dots are anchored over the
    // items by ScreenUIPatches.
    private static readonly HashSet<string> WorldAnchored = new HashSet<string> { "ItemDotsCanvas" };

    // Unlisted full-screen canvases with names like these are almost always static backdrops — never
    // convert them (a face-glued opaque backdrop is what hid the main menu).
    private static readonly string[] BackdropNameHints = { "background", "backdrop", "bkg", "fade", "loading", "splash", "frame" };

    private readonly List<Canvas> _menus = new List<Canvas>();
    private readonly List<Canvas> _hudPanels = new List<Canvas>();
    private readonly List<Canvas> _overlays = new List<Canvas>();
    private readonly HashSet<Canvas> _deathOverlays = new HashSet<Canvas>();
    private readonly HashSet<int> _activeMenus = new HashSet<int>();
    private Canvas _deathPanel;
    private DeathUI _deathPanelOwner;

    private float _rescan;
    private float _transitionScanUntil = 3f;

    public static void Create()
    {
        if (Instance != null) return;
        var go = new GameObject("HowToFishVR UI");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<VRUIManager>();
    }

    /// <summary>
    /// True if this canvas is the death screen — detected by NAME ("Death") OR by hosting a DeathUI
    /// component (the canvas name isn't guaranteed and can change between game versions). The death
    /// screen must be treated as a full-view head-locked OVERLAY, never a head-following panel.
    /// </summary>
    private static bool IsDeathCanvas(Canvas c)
    {
        if (c == null) return false;
        if (c.name.IndexOf("Death", System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
        try { return c.GetComponentInChildren<DeathUI>(true) != null; } catch { return false; }
    }

    /// <summary>True if any converted menu panel is currently shown (used to toggle the laser).</summary>
    public bool AnyMenuActive()
    {
        for (int i = 0; i < _menus.Count; i++)
            if (_menus[i] != null && _menus[i].isActiveAndEnabled) return true;
        return false;
    }

    internal static void ReassignLaserEventCamera(Camera eventCamera)
    {
        if (eventCamera == null) return;
        LaserManagedCanvases.RemoveWhere(c => c == null);
        foreach (var canvas in LaserManagedCanvases)
            if (canvas != null) canvas.worldCamera = eventCamera;
    }

    internal static bool IsLaserManaged(GameObject target)
    {
        if (target == null) return false;
        try
        {
            foreach (var canvas in target.GetComponentsInParent<Canvas>(true))
                if (canvas != null && LaserManagedCanvases.Contains(canvas)) return true;
        }
        catch { }
        return false;
    }

    private static Camera Cam()
    {
        Camera c = null;
        try { c = GameInfo.CurCamera; } catch { }
        if (c != null) return c;
        if (Camera.main != null) return Camera.main;
        try { if (MainMenuManager.MenuCam != null) return MainMenuManager.MenuCam; } catch { }
        foreach (var cam in Camera.allCameras)
            if (cam != null && cam.isActiveAndEnabled && cam.targetTexture == null) return cam;
        return null;
    }

    private void LateUpdate()
    {
        if (!Plugin.VREnabled) return;
        var cam = Cam();
        if (cam == null) return;

        _rescan -= Time.unscaledDeltaTime;
        if (_rescan <= 0f)
        {
            _rescan = Time.unscaledTime < _transitionScanUntil ? 0.1f : 0.75f;
            Scan(cam);
        }
        // Placement happens in RepositionNow(), called from the camera poser's onBeforeRender pass so the
        // panels are locked to the freshly-posed view with zero lag.
    }

    /// <summary>
    /// Place all panels relative to the STABLE rig head (your HMD) — driven by the poser in onBeforeRender.
    /// Anchoring to the rig head (not the game camera) keeps the UI locked to you, and running it in the
    /// same pass as the camera pose removes the one-frame lag that made panels stutter at the menu loop.
    /// </summary>
    public void RepositionNow()
    {
        if (!Plugin.VREnabled) return;
        var rig = VR.VRRig.Instance;
        if (rig == null || rig.Head == null) return;
        // Head-lock everything to the ACTUAL rendered view camera (VRCameraPoser drives it to the ragdoll
        // while dead), NOT rig.Head — rig.Head goes stale on death (body deactivated), which is what pinned
        // the death screen / effect overlays where you died. While alive the view camera == rig.Head.
        Transform view = rig.Head;
        try { var pv = Player.LocalPlayer; if (pv != null && pv.Camera != null && pv.Camera.Cam != null) view = pv.Camera.Cam.transform; } catch { }
        Reposition(view);
        // Character-customization arrows are screen projections in the flatscreen game. Re-project them
        // onto the already-positioned VR menu plane now that both the camera and canvas have their final
        // render poses, so they point at the visible avatar attachment targets without one-frame drift.
        Patches.ScreenUIPatches.RePinCustomizationButtons();
        // Diegetic world UI (item dots) must stay glued to their items at render time — the game writes
        // screen-pixel positions into them before the camera pose, so re-pin them in this same pass.
        Patches.ScreenUIPatches.RePinWorldDots();
    }

    private void Scan(Camera cam)
    {
        try
        {
            // BlurFix disabled (it broke the whole view) — see Entrypoint. Blur rework pending.
            // VR.BlurFix.Apply();
            // WORLD-SPACE canvases (item dots and contextual popups) must use the layer rendered by the
            // XR camera. Screen-space canvases are converted selectively below.
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Canvas>(true))
            {
                if (c == null || !c.gameObject.scene.IsValid()) continue;
                if (c.name.StartsWith("HowToFishVR", StringComparison.Ordinal)) continue;
                if (c.renderMode == RenderMode.WorldSpace) SetLayerRecursive(c.transform, 5);
            }

            // Include INACTIVE canvases: effect overlays (FXCanvas/UnderwaterCanvas) and the death UI are
            // turned OFF until you take damage / die, so if we only scanned active ones they'd never get
            // converted and would render flat-to-desktop (invisible in VR). We convert whitelisted canvases
            // while inactive so they're ready the instant the game turns them on.
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Canvas>(true))
            {
                if (c == null || !c.isRootCanvas) continue;
                if (!c.gameObject.scene.IsValid()) continue;
                if (c.name.StartsWith("HowToFishVR", StringComparison.Ordinal)) continue;

                // DELIBERATE CLASSIFICATION — every canvas falls into exactly one bucket:
                //  - Whitelist + Overlays  -> head-locked full-view effect overlays (blood, underwater,
                //    death screen)
                //  - Whitelist + buttons   -> placed-once laser menu (main menu, pause, chat, ...)
                //  - Whitelist + no buttons-> head-following HUD (health/ammo, perma canvas)
                //  - WorldSpace            -> left in the world like the base game (item dots, contextual
                //    popups); made visible via the layer-5 pass above
                //  - anything else         -> LEFT UNCONVERTED exactly as the game made it (no blanket
                //    conversion — that glued full-screen canvases over the main menu). Logged once so it
                //    can be whitelisted deliberately.
                bool known = Whitelist.Contains(c.name) || _menus.Contains(c) || _hudPanels.Contains(c) || _overlays.Contains(c);

                // World-anchored canvases (item dots): converted to world space + scaled, kept in the
                // world (the dots are anchored to items by ScreenUIPatches — never head-followed).
                if (WorldAnchored.Contains(c.name) && !known)
                {
                    ConvertWorldAnchored(c, c.renderOrder, cam);
                    continue;
                }

                if (c.renderMode == RenderMode.WorldSpace)
                {
                    if (!known)
                    {
                    }
                    continue;
                }

                if (!known)
                {
                    // SELECTIVE fallback: unlisted screen-space canvases become head-following panels so
                    // ANY menu or sub-menu that opens (options, settings, inventory, etc.) appears in
                    // front of you and is laser-clickable — including button-less popups. Full-screen
                    // button-less canvases and obvious backdrops are LEFT unconverted — those are what
                    // glued an opaque face over the main menu before.
                    if (!IsFullscreen(c) || HasButtons(c))
                    {
                        if (!LooksLikeBackdrop(c.name))
                        {
                            ConvertHUD(c, c.renderOrder, cam);
                        }
                        else
                        {
                        }
                    }
                    else
                    {
                    }
                    continue;
                }

                int renderOrder = c.renderOrder;

                // Re-assert overlay layer every scan: blood vignettes, hit popups, kill info, damage
                // numbers etc. are INSTANTIATED into FXCanvas at RUNTIME (after conversion), so they
                // would otherwise keep the source layer and render invisible to the XR camera.
                for (int oi = 0; oi < _overlays.Count; oi++)
                {
                    var ov = _overlays[oi];
                    if (ov == null || ov.renderMode != RenderMode.WorldSpace) continue;
                    SetLayerRecursive(ov.transform, 5);
                }

                if (c.renderMode == RenderMode.WorldSpace)
                {
                    if ((c.gameObject.layer == 5 || (c.worldCamera != null && c.worldCamera.name == "UICam")) &&
                        !_hudPanels.Contains(c))
                    {
                        c.worldCamera = cam;
                        ApplySorting(c, renderOrder);
                        SetLayerRecursive(c.transform, 5);
                        ScaleCanvas(c);
                        _hudPanels.Add(c);
                    }
                }
                else if (!_menus.Contains(c) && !_hudPanels.Contains(c) && !_overlays.Contains(c))
                {
                    // The DEATH screen must be a full-view head-locked OVERLAY (reparented out of the pinned
                    // DeathCamera and centered on your view), NOT a small ConvertMenu/ConvertHUD panel — that
                    // routing is exactly why the respawn text sat off-centre and the backdrop stayed pinned
                    // where you died (only ConvertOverlay does the death reparent + full-view head-lock).
                    if (IsDeathCanvas(c)) ConvertOverlay(c, renderOrder, cam, forceDeath: true);
                    else if (Overlays.Contains(c.name)) ConvertOverlay(c, renderOrder, cam);
                    else if (HeadFollowMenus.Contains(c.name)) ConvertHUD(c, renderOrder, cam); // main menu: in-front panel that rotates with you
                    else if (HasButtons(c)) ConvertMenu(c, renderOrder, cam);
                    else ConvertHUD(c, renderOrder, cam);
                }
            }

            // BLACK-PANEL SELF-HEAL: a full-screen dark backdrop can appear AFTER conversion (the game
            // activates the image later — e.g. a menu fade or a sub-menu backdrop that was inactive at
            // convert time). Re-run the neutralize + blur-material passes on every converted menu/HUD
            // panel each scan so any late-activated dark backdrop is hidden and any late-spawned blur
            // element renders (both are idempotent — already-hidden images are inactive and skipped,
            // replaced materials are already the default). Overlays (FX/blood/underwater/death) keep
            // their effects.
            for (int mi = 0; mi < _menus.Count; mi++) if (_menus[mi] != null) { NeutralizeFullscreenPanels(_menus[mi]); NormalizeBlurMaterials(_menus[mi]); }
            for (int hi = 0; hi < _hudPanels.Count; hi++) if (_hudPanels[hi] != null) { NeutralizeFullscreenPanels(_hudPanels[hi]); NormalizeBlurMaterials(_hudPanels[hi]); }

            // UNDERWATER (verified against the decompiled game — WaterManager.TogglePlayerUnderwater): the
            // flatscreen underwater look is NOT a URP post-process. It is exactly two things, both of which
            // render natively in the VR eyes with post-processing OFF:
            //   1. GLOBAL FOG — ShaderManager.SetFog sets RenderSettings.fogDensity(0.3)/fogColor; URP's
            //      forward pass applies it per-fragment per-eye on its own.
            //   2. The "UnderwaterCanvas" UI Image (blue/green tint + animated wave) — a plain screen-space
            //      Image the game SetActive()s underwater. It is whitelisted as an Overlay above, so it is
            //      converted to a head-locked full-view overlay (positioned each frame in RepositionNow) and
            //      shows the real tint/wave in VR. NormalizeBlurMaterials no longer flattens it, so its own
            //      wave material survives. WaterUnderwaterPatches drives the underwater toggle from the real
            //      VR view camera (alive and dead). No post-processing, no custom tint — the real effect.

            // Screen-positioned popup canvases (fishing reel / caught-fish / price popups) whose names
            // aren't guaranteed: convert their hosting canvas as a head-following HUD if not handled.
            foreach (var f in UnityEngine.Object.FindObjectsOfType<FishingUI>(true))
                if (f != null) ConvertHostAsHud(f, cam);
            foreach (var iu in UnityEngine.Object.FindObjectsOfType<ItemUI>(true))
                if (iu != null) ConvertHostAsHud(iu, cam);

            // SUB-MENUS: nested canvases (e.g. the options/settings screens inside the pause or main-menu
            // canvas) are skipped by the root-only pass above. A nested canvas left in ScreenSpaceOverlay
            // mode renders only to the desktop — invisible in VR and its buttons unclickable. Force any
            // nested canvas under a converted root to render in the same world-space panel (it inherits
            // the panel's transform, so it follows your head like the panel and its buttons raycast).
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Canvas>(true))
            {
                if (c == null || c.isRootCanvas) continue;
                if (!c.gameObject.scene.IsValid()) continue;
                if (c.name.StartsWith("HowToFishVR", StringComparison.Ordinal)) continue;
                if (c.renderMode == RenderMode.WorldSpace) continue;
                var root = c.rootCanvas;
                if (root == null || root.name.StartsWith("HowToFishVR", StringComparison.Ordinal)) continue;
                if (!_menus.Contains(root) && !_hudPanels.Contains(root) && !_overlays.Contains(root)) continue;
                c.renderMode = RenderMode.WorldSpace;
                c.worldCamera = cam;
                SetLayerRecursive(c.transform, 5);
                NormalizeBlurMaterials(c);
                // Use the same single tracked-device raycaster path as root panels. Re-enabling the
                // ordinary GraphicRaycaster here made nested/sub-menu canvases compete with the XR
                // raycaster and produced intermittent or stale targets.
                AddLaserRaycaster(c);
            }
        }
        catch { }
    }

    private void ConvertMenu(Canvas c, int order, Camera cam)
    {
        PrepareLayout(c);
        c.renderMode = RenderMode.WorldSpace;
        c.worldCamera = cam;
        ApplySorting(c, order);
        SetLayerRecursive(c.transform, 5);
        NeutralizeFullscreenPanels(c);
        NormalizeBlurMaterials(c);
        AddLaserRaycaster(c);
        ScaleCanvas(c);
        _menus.Add(c);
        PlaceMenu(c, cam.transform);
        if (c.isActiveAndEnabled) _activeMenus.Add(c.GetInstanceID());
    }

    private void ConvertHUD(Canvas c, int order, Camera cam)
    {
        PrepareLayout(c);
        c.renderMode = RenderMode.WorldSpace;
        c.worldCamera = cam;
        ApplySorting(c, order);
        SetLayerRecursive(c.transform, 5);
        NeutralizeFullscreenPanels(c);
        NormalizeBlurMaterials(c);
        ScaleCanvas(c);
        AddLaserRaycaster(c);
        _hudPanels.Add(c);
    }

    // Full-screen effect overlays (blood/damage vignette, near-death grey, underwater blur) shown head-
    // locked and filling the view — same world-space render path the working HUD uses (ScreenSpaceCamera
    // did not render here). Reposition() head-locks + sizes them each frame.
    private void ConvertOverlay(Canvas c, int order, Camera cam, bool forceDeath = false)
    {
        try
        {
            PrepareLayout(c);
            c.renderMode = RenderMode.WorldSpace;
            c.worldCamera = cam;
            ApplySorting(c, order + 1000); // draw on top of the world/HUD
            SetLayerRecursive(c.transform, 5);
            if (c.name.IndexOf("Underwater", StringComparison.OrdinalIgnoreCase) >= 0)
                MakeUnderwaterStereoSafe(c);
            NormalizeBlurMaterials(c);
            var rt = c.GetComponent<RectTransform>();
            if (rt != null)
            {
                // Scale from the LAID-OUT rect width, not sizeDelta: full-screen canvases are often authored
                // with stretch anchors (sizeDelta ~= 0), which made the overlay scale wrong (a tiny or giant
                // plane = "black screen" / nothing). rect.width is the actual on-screen size.
                float w = rt.rect.width > 1f ? rt.rect.width : Mathf.Max(1f, rt.sizeDelta.x);
                float s = (2.6f * VRConfig.HudScale.Value) / w;
                rt.localScale = new Vector3(s, s, s);
            }
            // The death screen starts under the game's stationary DeathCamera. Parent it directly to the
            // ACTIVE VR view camera instead of merely detaching it into world space. This makes the lock
            // structural: even if an onBeforeRender placement is skipped, head movement still carries the
            // overlay and it can never remain pinned at the death location.
            bool isDeath = forceDeath || IsDeathCanvas(c);
            if (isDeath)
            {
                if (cam != null) c.transform.SetParent(cam.transform, false);
                _deathOverlays.Add(c);
            }
            // Track it FIRST so a failure below (softening) can never leave it as an untracked world-
            // space canvas — an untracked world-space canvas at scale 1 IS the "giant black panel stuck
            // in the world" bug.
            _overlays.Add(c);
            if (isDeath)
            {
                try { SoftenDeathBackdrop(c); } catch { }
            }
        }
        catch { }
    }

    /// <summary>Reassert the native death UI at the instant the game toggles it. This is intentionally
    /// event-triggered in addition to the render-time invariant: the original canvas starts beneath an
    /// inactive death-camera object and the game can change that hierarchy again during death setup.</summary>
    public void SyncDeathOverlay(DeathUI deathUI, bool show)
    {
        try
        {
            if (deathUI == null || deathUI._deathCanvas == null) return;
            var cam = Cam();
            var root = EnsureDedicatedDeathPanel(deathUI, cam);
            if (root == null) return;
            SetLayerRecursive(root.transform, 5);
            root.enabled = true;
            root.gameObject.SetActive(true);
            deathUI._deathCanvas.gameObject.SetActive(true);
            if (show)
            {
                Transform view = cam != null ? cam.transform : null;
                try
                {
                    var p = Player.LocalPlayer;
                    if (p != null && p.Camera != null && p.Camera.Cam != null)
                        view = p.Camera.Cam.transform;
                }
                catch { }
                if (view != null)
                {
                    root.transform.SetParent(view, false);
                    root.transform.localPosition = Vector3.forward * DeathDistance;
                    root.transform.localRotation = Quaternion.identity;
                    ScaleDeathPanel(root);
                }
                SoftenDeathBackdrop(root);
            }
        }
        catch { }
    }

    /// <summary>Moves the game's real DeathUIHolder into a canvas with deterministic VR geometry.</summary>
    private Canvas EnsureDedicatedDeathPanel(DeathUI deathUI, Camera cam)
    {
        if (_deathPanel != null && _deathPanelOwner == deathUI)
        {
            _deathPanel.worldCamera = cam;
            return _deathPanel;
        }

        var oldRoot = deathUI._deathCanvas.GetComponentInParent<Canvas>(true);
        var go = new GameObject("HowToFishVR DeathPanel", typeof(RectTransform));
        go.layer = 5;
        var panel = go.AddComponent<Canvas>();
        panel.renderMode = RenderMode.WorldSpace;
        panel.worldCamera = cam;
        panel.overrideSorting = true;
        panel.sortingOrder = (oldRoot != null ? oldRoot.sortingOrder : 12) + 1000;
        ((RectTransform)go.transform).sizeDelta = new Vector2(1920f, 1080f);

        // Move, do not clone: every native DeathUI reference and its respawn hold animation stays live.
        deathUI._deathCanvas.transform.SetParent(panel.transform, false);
        deathUI._deathCanvas.transform.localScale = Vector3.one;
        SetLayerRecursive(panel.transform, 5);
        if (oldRoot != null && oldRoot != panel)
        {
            _overlays.Remove(oldRoot);
            _deathOverlays.Remove(oldRoot);
            oldRoot.enabled = false;
        }

        _deathPanel = panel;
        _deathPanelOwner = deathUI;
        _overlays.Add(panel);
        _deathOverlays.Add(panel);
        ScaleDeathPanel(panel);
        return panel;
    }

    private static void ScaleDeathPanel(Canvas panel)
    {
        var rt = panel != null ? panel.GetComponent<RectTransform>() : null;
        if (rt == null) return;
        float w = rt.rect.width > 1f ? rt.rect.width : Mathf.Max(1f, rt.sizeDelta.x);
        rt.localScale = Vector3.one * ((DeathWidth * VRConfig.HudScale.Value) / w);
    }

    /// <summary>
    /// The game's Underwater Shader Graph samples _GlobalUniversalBlurTexture. That screen-copy is a
    /// flatscreen render target; in the converted stereo/world-space UI pass it resolves black, making
    /// the entire headset view pitch black. Keep the game's actual underwater fog (driven by
    /// WaterManager) and replace only this unsafe screen-sampling pass with its authored cyan tint on
    /// Unity's stereo-safe default UI shader.
    /// </summary>
    private static void MakeUnderwaterStereoSafe(Canvas c)
    {
        try
        {
            var def = Canvas.GetDefaultCanvasMaterial();
            if (def == null) return;
            foreach (var img in c.GetComponentsInChildren<Image>(true))
            {
                if (img == null || img.material == null) continue;
                var source = img.material;
                string materialName = source.name ?? "";
                string shaderName = source.shader != null ? source.shader.name ?? "" : "";
                if (materialName.IndexOf("Underwater", StringComparison.OrdinalIgnoreCase) < 0 &&
                    shaderName.IndexOf("Underwater", StringComparison.OrdinalIgnoreCase) < 0) continue;

                // Read the exact colour from the shipped material (0, .466, .670 in the current game).
                // A light translucent wash supplies the missing colour layer while the game's dense
                // underwater fog supplies most of the look without hiding the rendered world.
                Color tint = source.HasProperty("_UnderwaterColor")
                    ? source.GetColor("_UnderwaterColor")
                    : new Color(0f, 0.466f, 0.670f, 1f);
                float authoredAlpha = Mathf.Clamp01(img.color.a);
                img.material = def;
                img.color = new Color(tint.r, tint.g, tint.b, 0.20f * authoredAlpha);
            }
        }
        catch { }
    }

    /// <summary>
    /// The death screen's full-screen dark backdrop renders as SOLID BLACK in the converted overlay
    /// (the alpha the flatscreen relies on doesn't survive the world-space conversion). Force every
    /// full-screen dark image in the death overlay to a semi-transparent grey (alpha ~0.55) so the world
    /// shows through like the flatscreen grey-out — without it the death screen is just a black wall.
    /// The actual content (respawn prompt, downed text) is untouched.
    /// </summary>
    private static void SoftenDeathBackdrop(Canvas c)
    {
        try
        {
            var cRt = c.GetComponent<RectTransform>();
            if (cRt == null) return;
            float cw = cRt.rect.width;
            float chh = cRt.rect.height;
            if (cw < 1f || chh < 1f) return;
            Vector3[] wc = new Vector3[4];
            foreach (var img in c.GetComponentsInChildren<Image>(true))
            {
                if (img == null || !img.gameObject.activeSelf) continue;
                var col = img.color;
                float avg = (col.r + col.g + col.b) / 3f;
                // Also catch dark SPRITES with a white Image color (a black texture tinted white is
                // how most game backdrops are authored — the color-only check missed them, which is
                // why the death screen could stay a solid black wall after the earlier fix).
                if (avg > 0.35f)
                {
                    if (img.sprite != null && img.sprite.texture != null)
                    {
                        float lum = SampleSpriteCenter(img.sprite);
                        if (lum > 0.35f) continue;
                        avg = Mathf.Min(avg, lum);
                    }
                    else continue;
                }
                var irt = img.rectTransform;
                if (irt == null) continue;
                irt.GetWorldCorners(wc);
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                for (int i = 0; i < 4; i++)
                {
                    Vector3 lp = c.transform.InverseTransformPoint(wc[i]);
                    minX = Mathf.Min(minX, lp.x); minY = Mathf.Min(minY, lp.y);
                    maxX = Mathf.Max(maxX, lp.x); maxY = Mathf.Max(maxY, lp.y);
                }
                if (maxX - minX >= cw * 0.85f && maxY - minY >= chh * 0.85f)
                {
                    img.color = new Color(col.r, col.g, col.b, Mathf.Min(col.a, 0.55f));
                }
            }
        }
        catch { }
    }

    private static void AddLaserRaycaster(Canvas c)
    {
        var gr = c.GetComponent<GraphicRaycaster>();
        if (gr == null) gr = c.gameObject.AddComponent<GraphicRaycaster>();
        gr.enabled = true;
        gr.ignoreReversedGraphics = false;
        gr.blockingObjects = GraphicRaycaster.BlockingObjects.None;
        var tracked = c.GetComponent<UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster>();
        if (tracked != null) tracked.enabled = false;
        LaserManagedCanvases.Add(c);
    }

    /// <summary>
    /// Freeze the canvas at its designed resolution so switching to WorldSpace doesn't collapse the
    /// layout (which piled every element into the centre = the "stacking"). Elements keep their screen
    /// regions, so multiple active canvases compose correctly instead of overlapping.
    /// </summary>
    private static void PrepareLayout(Canvas c)
    {
        var rt = c.GetComponent<RectTransform>();
        Vector2 refRes = new Vector2(1920f, 1080f);
        var scaler = c.GetComponent<CanvasScaler>();
        if (scaler != null)
        {
            if (scaler.uiScaleMode == CanvasScaler.ScaleMode.ScaleWithScreenSize &&
                scaler.referenceResolution.x > 1f && scaler.referenceResolution.y > 1f)
                refRes = scaler.referenceResolution;
            scaler.enabled = false; // stop it rescaling based on (now irrelevant) screen size
        }
        if (rt != null) rt.sizeDelta = refRes;
    }

    private static bool HasButtons(Canvas c)
    {
        try { var s = c.GetComponentsInChildren<Selectable>(true); return s != null && s.Length > 0; }
        catch { return false; }
    }

    /// <summary>True for full-screen-sized canvases (widescreen aspect) — those become head-locked
    /// overlays that fill the view rather than small floating HUD panels.</summary>
    private static bool IsFullscreen(Canvas c)
    {
        try
        {
            var rt = c.GetComponent<RectTransform>();
            if (rt == null) return false;
            float w = rt.rect.width > 1f ? rt.rect.width : rt.sizeDelta.x;
            float h = rt.rect.height > 1f ? rt.rect.height : rt.sizeDelta.y;
            return w >= 800f && w / Mathf.Max(1f, h) >= 1.5f;
        }
        catch { return false; }
    }

    private static bool LooksLikeBackdrop(string name)
    {
        foreach (var h in BackdropNameHints)
            if (name.IndexOf(h, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>
    /// Convert the root canvas hosting <paramref name="t"/> as a head-following HUD panel RIGHT NOW
    /// (used by popup postfixes whose canvas may not have been scanned yet — e.g. the caught-fish
    /// popup the frame it appears). No-op if the host is already converted or already world-space.
    /// </summary>
    public static void ForceHostAsHud(Transform t)
    {
        try
        {
            if (t == null || Instance == null) return;
            var canvas = t.GetComponentInParent<Canvas>(true);
            if (canvas == null) return;
            var host = canvas.rootCanvas;
            if (host == null || host.name.StartsWith("HowToFishVR", StringComparison.Ordinal)) return;
            if (Instance._menus.Contains(host) || Instance._hudPanels.Contains(host) || Instance._overlays.Contains(host)) return;
            if (host.renderMode == RenderMode.WorldSpace) return;
            var cam = Cam();
            if (cam == null) return;
            Instance.ConvertHUD(host, host.renderOrder, cam);
        }
        catch { }
    }

    /// <summary>
    /// The "black-panel" fix: many game canvases carry a full-screen DARK Image — an opaque letterbox /
    /// mask, but more often the flatscreen MENU DIM: a semi-transparent dark tint (alpha ~0.4-0.7) or a
    /// grey overlay (death screen) that dims the whole screen behind the UI. In flatscreen that's the
    /// intended dark look; inside a converted VR panel the same image is a 2.2m dark rectangle in front
    /// of your face — it reads as a "black panel" no matter how translucent it is. So ANY full-screen
    /// image that is dark-tinted with meaningful alpha is hidden; the panel keeps its real content
    /// (text, buttons, icons) over the world. Effect overlays (FXCanvas blood, underwater tint) are NOT
    /// converted through this path, so their effects stay. Full-screen LIGHT backgrounds (white paper
    /// panels, logos) are untouched — only dark tints become black panels.
    /// </summary>
    private static void NeutralizeFullscreenPanels(Canvas c)
    {
        try
        {
            var cRt = c.GetComponent<RectTransform>();
            if (cRt == null) return;
            float cw = cRt.rect.width;
            float chh = cRt.rect.height;
            if (cw < 1f || chh < 1f) return;
            Vector3[] wc = new Vector3[4];
            foreach (var img in c.GetComponentsInChildren<Image>(true))
            {
                if (img == null || !img.gameObject.activeSelf) continue;
                // A full-screen frosted-glass panel is a blur-material Image with a dark TINT — do NOT hide
                // it as a "black backdrop", it's the real effect. BlurCapture guarantees the blur material
                // now samples a valid texture (real captured blur, or a dark-frosted fallback), so keeping
                // it can no longer render solid black.
                if (img.material != null && img.material.shader != null &&
                    ((img.material.name ?? "").IndexOf("Blur", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                     (img.material.shader.name ?? "").IndexOf("Blur", System.StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;
                // NEVER touch the mod's own UI. The VR Settings page's backdrop is a full-screen dark
                // Image placed ON THE PAGE ROOT itself — hiding it deactivates the whole settings page
                // the moment a scan runs (~0.75s after it opens). That was the "VR Settings shows for
                // one second then closes" bug.
                var ownT = img.transform;
                bool modOwned = false;
                while (ownT != null)
                {
                    if (ownT.name.StartsWith("HowToFishVR", StringComparison.Ordinal)) { modOwned = true; break; }
                    ownT = ownT.parent;
                }
                if (modOwned) continue;
                // A button's background is CONTENT, never a backdrop panel — never hide it.
                if (img.GetComponentInParent<Button>(true) != null) continue;
                var col = img.color;
                if (col.a < 0.15f) continue;                // invisible, nothing to hide
                float avg = (col.r + col.g + col.b) / 3f;
                // Also sample the SPRITE: a full-screen dark backdrop is often a dark TEXTURE with a
                // white Image color (avg=1.0, which the old color-only check missed — that's the panels
                // that stayed solid black after the always-visible removal). Sample the sprite's centre
                // pixel (respecting the sprite's rect in the atlas); no sprite / white texture = not a
                // backdrop.
                if (avg <= 0.35f) { /* dark tint — hide below */ }
                else if (img.sprite != null && img.sprite.texture != null)
                {
                    float lum = SampleSpriteCenter(img.sprite);
                    if (lum > 0.35f) continue;
                    avg = Mathf.Min(avg, lum);
                }
                else continue;
                var irt = img.rectTransform;
                if (irt == null) continue;
                irt.GetWorldCorners(wc);
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                for (int i = 0; i < 4; i++)
                {
                    Vector3 lp = c.transform.InverseTransformPoint(wc[i]);
                    minX = Mathf.Min(minX, lp.x); minY = Mathf.Min(minY, lp.y);
                    maxX = Mathf.Max(maxX, lp.x); maxY = Mathf.Max(maxY, lp.y);
                }
                if (maxX - minX >= cw * 0.85f && maxY - minY >= chh * 0.85f)
                {
                    img.gameObject.SetActive(false);
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Replace the game's BLUR materials (UIBlur / UniversalBlurUI) with the default UI material on
    /// converted canvases. Those shaders sample the screen/blur buffer in screen space — in a world-
    /// space canvas that buffer doesn't exist, so the element rendered as a SOLID BLACK rectangle
    /// (chat background, boss HP bar, HUD backgrounds, the VR Settings buttons cloned from the game's
    /// Options button — all the "menus and buttons that go black"). The default UI material alone
    /// renders the raw sprite, which for these dark-panel sprites comes out WHITE (the dark look was
    /// baked into the blur shader), so the image is also tinted to the dark translucent panel the
    /// game intends (chat box, HP-bar backing, HUD dim). Idempotent: after replacement the material is
    /// the default, so later passes skip it. The mod's own materials (VR ... vignettes), map materials
    /// and dot materials are untouched.
    /// </summary>
    private static void NormalizeBlurMaterials(Canvas c) { }

    /// <summary>Sample the centre pixel of a sprite's own rect (handles atlases) and return its luminance.</summary>
    private static float SampleSpriteCenter(Sprite s)
    {
        try
        {
            var tex = s.texture;
            var rect = s.textureRect;
            if (tex == null || rect.width < 2f || rect.height < 2f) return 1f;
            int px = Mathf.Clamp((int)(rect.x + rect.width * 0.5f), 0, tex.width - 1);
            int py = Mathf.Clamp((int)(rect.y + rect.height * 0.5f), 0, tex.height - 1);
            var p = tex.GetPixel(px, py);
            return (p.r + p.g + p.b) / 3f;
        }
        catch { return 1f; }
    }

    /// <summary>Item-dots canvas: world space + scaled, stays where it is (never head-followed).</summary>
    private void ConvertWorldAnchored(Canvas c, int order, Camera cam)
    {
        c.renderMode = RenderMode.WorldSpace;
        c.worldCamera = cam;
        ApplySorting(c, order);
        SetLayerRecursive(c.transform, 5);
        var rt = c.GetComponent<RectTransform>();
        if (rt != null)
        {
            float w = rt.rect.width > 1f ? rt.rect.width : Mathf.Max(1f, rt.sizeDelta.x);
            float s = (2.2f * VRConfig.HudScale.Value) / w;
            rt.localScale = new Vector3(s, s, s);
        }
    }

    /// <summary>Convert the root canvas hosting a screen-positioned popup component (name unknown) as a
    /// head-following HUD, so e.g. the fishing reel / caught-fish / price popups show in VR.</summary>
    private static void ConvertHostAsHud(Component src, Camera cam)
    {
        if (src == null) return;
        try
        {
            var host = src.GetComponentInParent<Canvas>(true);
            if (host == null) return;
            host = host.rootCanvas;
            if (host == null || host.name.StartsWith("HowToFishVR", System.StringComparison.Ordinal)) return;
            if (Whitelist.Contains(host.name)) return;         // handled by the whitelist path
            if (host.name.StartsWith("ItemDots", System.StringComparison.Ordinal)) return; // world-anchored separately
            if (Instance != null && (Instance._menus.Contains(host) || Instance._hudPanels.Contains(host) || Instance._overlays.Contains(host))) return;
            if (host.renderMode == RenderMode.WorldSpace) return;
            Instance.ConvertHUD(host, host.renderOrder, cam);
        }
        catch { }
    }

    private static void ApplySorting(Canvas canvas, int order)
    {
        if (canvas == null) return;
        canvas.overrideSorting = true;
        canvas.sortingOrder = order;
    }

    private void Reposition(Transform head)
    {
        try
        {
            float yaw = head.eulerAngles.y;
            Quaternion rot = Quaternion.Euler(0f, yaw, 0f);

            // In-game pause menus are placed once so they stay steady while the player is stationary.
            // The MAIN MENU is different: its boat keeps sailing. Options reuses PauseCanvas and sets
            // PauseManager.IsPaused while MainMenuManager.IsInMenu remains true, so treating that canvas
            // like an in-game pause panel leaves it behind in world space. While the main-menu scene is
            // active, carry every visible menu canvas with the same continuously-updated head/boat anchor.
            bool sailingMainMenu = false;
            try { sailingMainMenu = MainMenuManager.IsInMenu; } catch { }
            for (int i = 0; i < _menus.Count; i++)
            {
                var m = _menus[i];
                if (m == null || m.renderMode != RenderMode.WorldSpace) continue;
                int id = m.GetInstanceID();
                bool visible = m.isActiveAndEnabled;
                // PauseCanvas itself is permanently active; only its child pages toggle. Treat the
                // actual pause state as activation so entering gameplay always places it at the current
                // HMD rather than retaining the position recorded in the main-menu scene.
                if (m.name == "PauseCanvas")
                {
                    try { visible = PauseManager.IsPaused || VRSettingsPanel.IsOpen; } catch { }
                }
                if (!visible) _activeMenus.Remove(id);
                else
                {
                    bool newlyVisible = _activeMenus.Add(id);
                    if (sailingMainMenu || newlyVisible) PlaceMenu(m, head);
                }
            }

            // HUD: follows head yaw every frame, floating in front.
            Vector3 pos = head.position + rot * Vector3.forward * Distance;
            for (int j = 0; j < _hudPanels.Count; j++)
            {
                var h = _hudPanels[j];
                if (h == null || !h.isActiveAndEnabled || h.renderMode != RenderMode.WorldSpace) continue;
                // Keep head-following panels at their intended distance even during a trigger hold. The
                // pointer's frozen drag projection now rides the pressed canvas itself, so freezing the
                // canvas is unnecessary and made MainMenuCanvas recede while its boat/camera kept moving.
                h.transform.position = pos;
                h.transform.rotation = rot;
                // Re-assert the panel scale every frame: the game's own UI code sometimes scales
                // converted canvases (e.g. DeathUI's 2->1 "zoom in" flourish on the death screen) — at
                // scale 1 a 1920x1080 world-space canvas is a 1920-metre wall in front of the face (the
                // "respawn screen is a black screen" bug). Panel scale wins for rendering.
                var rt = h.GetComponent<RectTransform>();
                if (rt != null)
                {
                    float w = Mathf.Max(1f, rt.sizeDelta.x);
                    float s = (Width * VRConfig.HudScale.Value) / w;
                    if (Mathf.Abs(rt.localScale.x - s) > 1e-5f) rt.localScale = new Vector3(s, s, s);
                }
            }

            // Caught-fish popup panel: same placement as the HUD panels (in front, rotates with yaw).
            if (_caughtFishPanel != null && _caughtFishPanel.isActiveAndEnabled)
            {
                _caughtFishPanel.transform.position = pos;
                _caughtFishPanel.transform.rotation = rot;
            }

            // Effect overlays: full head-lock (pitch too), close to the face, filling the view. Kept
            // ACTIVE while dead so dying in water renders exactly like being alive (underwater tint +
            // fog stay on, the death overlay + grey vignette compose on top of them) — suppressing them
            // on death is what made water look "fucked up" when you died in it.
            bool deadNow = false;
            try { var pl = Player.LocalPlayer; deadNow = pl != null && pl.Dying != null && pl.Dying.IsDead; } catch { }
            // Camera-underwater state, using the GAME'S OWN check (WaterManager.CheckUnderwater: the
            // camera below water height + 0.1m). The dead-state tint must appear only while the camera
            // is actually underwater — exactly like when alive — never because the body/head happens to
            // be in the water while the view is above it, and it must turn OFF again when the camera
            // leaves the water.
            bool camUnder = false;
            try
            {
                var ucam = Cam();
                if (ucam != null)
                    camUnder = ucam.transform.position.y < WaterManager.GetWaterHeight(ucam.transform.position) + 0.1f;
            }
            catch { }
            for (int k = 0; k < _overlays.Count; k++)
            {
                var o = _overlays[k];
                if (o == null || o.renderMode != RenderMode.WorldSpace) continue;
                bool isDeathOverlay = _deathOverlays.Contains(o) || IsDeathCanvas(o);
                bool isUnderwater = o.name.IndexOf("Underwater", System.StringComparison.OrdinalIgnoreCase) >= 0;
                // The underwater tint is driven by the SAME camera check the game uses when alive — on
                // only while the camera is in the water, off the moment it isn't (so it can never stick
                // after you float up / swim out). The death screen is a CUSTOM overlay now (VRDeathOverlay),
                // so there is no game death canvas in the overlay list anymore.
                if (isUnderwater && deadNow)
                {
                    if (camUnder)
                    {
                        if (!o.gameObject.activeSelf) o.gameObject.SetActive(true);
                    }
                    else if (o.gameObject.activeSelf)
                    {
                        o.gameObject.SetActive(false); // camera is above water — tint must be off, like alive
                    }
                }
                if (!o.isActiveAndEnabled) continue;
                if (isDeathOverlay)
                {
                    if (o.transform.parent != head) o.transform.SetParent(head, false);
                    o.transform.localPosition = Vector3.forward * DeathDistance;
                    o.transform.localRotation = Quaternion.identity;

                    // DeathUI animates its inner CanvasGroup from scale 2 -> 1. Keep only the alpha fade
                    // in VR; forcing the inner content to its authored scale prevents the prompt from
                    // appearing offset/oversized while the root canvas remains correctly eye-filling.
                    try
                    {
                        var group = o.GetComponentInChildren<CanvasGroup>(true);
                        if (group != null) group.transform.localScale = Vector3.one;
                    }
                    catch { }
                }
                else
                {
                    o.transform.position = head.position + head.forward * 0.4f;
                    o.transform.rotation = head.rotation;
                }
                // Re-assert the overlay scale every frame: the game's own UI code sometimes scales
                // converted canvases (e.g. DeathUI's 2->1 "zoom in" flourish) — at scale 1 a full-
                // screen canvas is a giant wall in front of the face. Overlay scale wins for rendering.
                var ort = o.GetComponent<RectTransform>();
                if (ort != null)
                {
                    float ow = ort.rect.width > 1f ? ort.rect.width : Mathf.Max(1f, ort.sizeDelta.x);
                    float os = ((isDeathOverlay ? DeathWidth : 2.6f) * VRConfig.HudScale.Value) / ow;
                    if (Mathf.Abs(ort.localScale.x - os) > 1e-5f) ort.localScale = new Vector3(os, os, os);
                }
            }
        }
        catch { }
    }

    private static void PlaceMenu(Canvas canvas, Transform head)
    {
        if (canvas == null || head == null) return;
        Quaternion rot = Quaternion.Euler(0f, head.eulerAngles.y, 0f);
        canvas.transform.position = head.position + rot * Vector3.forward * Distance;
        canvas.transform.rotation = rot;
    }

    private static void ScaleCanvas(Canvas c)
    {
        var rt = c != null ? c.GetComponent<RectTransform>() : null;
        if (rt == null) return;
        float w = Mathf.Max(1f, rt.sizeDelta.x);
        float s = (Width * VRConfig.HudScale.Value) / w;
        rt.localScale = new Vector3(s, s, s);
    }

    private static void SetLayerRecursive(Transform root, int layer)
    {
        if (root == null) return;
        root.gameObject.layer = layer;
        for (int i = 0; i < root.childCount; i++)
            SetLayerRecursive(root.GetChild(i), layer);
    }
}
