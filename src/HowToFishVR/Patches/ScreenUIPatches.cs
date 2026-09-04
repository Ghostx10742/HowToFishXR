using HarmonyLib;
using HowToFishVR.UI;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace HowToFishVR.Patches;

/// <summary>
/// Several game UIs position their elements with <c>transform.position = camera.WorldToScreenPoint(...)</c>
/// — raw screen PIXELS. Inside a converted world-space VR panel that's wrong (the element flies off to a
/// world coordinate equal to the pixel number), which is why the caught-fish popup, item look-at / price
/// text and hit markers never showed in the headset. These postfixes re-map those elements into their
/// canvas's LOCAL design space, and the item dots are anchored to the ITEMS in the world instead
/// (diegetic, like the base game's intent).
///
/// NOTE: only elements the game actually positions with screen pixels are remapped. Elements authored in
/// local canvas space (bait-change text, new-fish name, item-info text, drop circle) are left untouched —
/// they already land correctly once their canvas is converted to a VR panel.
/// </summary>
[HarmonyPatch]
internal static class ScreenUIPatches
{
    /// <summary>
    /// The game just wrote screen pixels into <paramref name="t"/>.position (world); convert that into the
    /// canvas's root LOCAL design coordinates (via the root transform, so nested children like the
    /// caught-fish text inside its holder are placed correctly without double-counting the parent).
    /// No-op for canvases the mod didn't convert (they keep the game's screen-space behaviour).
    /// </summary>
    private static void RemapScreenPx(Transform t)
    {
        if (t == null) return;
        var canvas = t.GetComponentInParent<Canvas>(true);
        if (canvas == null || canvas.renderMode != RenderMode.WorldSpace) return;
        var root = canvas.transform;
        var rt = canvas.GetComponent<RectTransform>();
        if (rt == null) return;
        var p = t.position; // == the screen px the game just wrote
        float w = Mathf.Max(1f, rt.rect.width);
        float h = Mathf.Max(1f, rt.rect.height);
        float sx = rt.sizeDelta.x > 1f ? rt.sizeDelta.x : w;
        float sy = rt.sizeDelta.y > 1f ? rt.sizeDelta.y : h;
        // Screen px are bottom-left origin; the world-space canvas uses a centred design rectangle.
        Vector3 rootLocal = new Vector3((p.x / w - 0.5f) * sx, (p.y / h - 0.5f) * sy, 0f);
        t.position = root.TransformPoint(rootLocal);
    }

    // ---- "You caught this fish/creature" popup (FishingUI) ----
    // The game positions this popup with RAW SCREEN PIXELS (WorldToScreenPoint written straight into
    // transform.position) every frame. Inside the converted VR panels those pixels are meaningless
    // (the popup flew off to world coordinates equal to the pixel numbers — "goes somewhere else").
    // Instead of trying to remap pixel numbers through the game's canvas hierarchy, the popup's holder
    // is reparented into a MOD-OWNED head-following panel (placed exactly like the HUD panels: 2m in
    // front, rotating with head yaw) and pinned at a fixed spot. The per-frame pixel writes are then
    // overridden by re-pinning the text to the holder.
    private static Transform _caughtHolder;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FishingUI), "LateUpdate")]
    private static void FishingCaughtText(FishingUI __instance)
    {
        if (!Plugin.VREnabled) return;
        try
        {
            if (_caughtHolder == null) return;
            var text = __instance._caughtFishText;
            if (text == null) return;
            // The game just wrote screen pixels into the text position — pin the text to the holder
            // (which sits at the fixed spot on the panel) so it can't fly off.
            text.transform.position = _caughtHolder.position;
            // FACE THE PLAYER: the holder was reparented with its WORLD rotation preserved (from the
            // game's screen-space canvas = identity/world-axis), so it faced world-forward while the
            // panel rotates with your head — the popup read sideways whenever you'd turned. Re-assert
            // identity in the PANEL's space every frame (the panel itself faces you), for the holder
            // and the text, so the popup always faces you directly.
            _caughtHolder.localRotation = Quaternion.identity;
            if (text.transform.parent == _caughtHolder)
                text.transform.localRotation = Quaternion.identity;
        }
        catch { }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FishingUI), "CaughtFishUI")]
    private static void FishingCaughtHolder(FishingUI __instance)
    {
        if (!Plugin.VREnabled) return;
        try
        {
            var holder = __instance._caughtFishHolder;
            var text = __instance._caughtFishText;
            if (holder == null || text == null) return;
            var panel = VRUIManager.GetCaughtFishPanel(GameInfo.CurCamera);
            if (panel == null) return;
            var holderT = holder.transform;
            if (holderT.parent != panel.transform)
            {
                // Reparent the whole popup into the mod-owned head-following panel (same 1920x1080
                // design space as the game's HUD canvas, so the authored layout reads 1:1).
                holderT.SetParent(panel.transform, true);
                if (text.transform.parent != holderT) text.transform.SetParent(holderT, true);
                _caughtHolder = holderT;
            }
            // Pin the popup to a fixed spot on the panel: centred, slightly above the middle (design
            // units on a 1920x1080 panel). The game's own 0->1 scale tween still plays on the holder's
            // localScale — untouched. Rotation is forced to identity IN THE PANEL'S SPACE so the popup
            // faces the player directly (the panel itself faces you); keeping the reparented world
            // rotation is what left it rotated to the side.
            holderT.localPosition = new Vector3(0f, 150f, 0f);
            holderT.localRotation = Quaternion.identity;
            if (text.transform.parent == holderT)
                text.transform.localRotation = Quaternion.identity;
        }
        catch { }
    }

    // ---- Item look-at / price popup (ItemUI): world-anchored over the item, like the item dots ----
    // The price/look-at tag is diegetic WORLD UI — it must float over the item in the world, not in the
    // head-following panel. The game positions it with screen pixels (meaningless in a converted world-
    // space canvas), so we reparent it into a mod-owned world-anchored canvas (scaled like the panels,
    // so the game's own 0<->1 show/hide tweens keep working) and glue it to the item each frame.

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ItemUI), "LateUpdate")]
    private static void WorldAnchorPriceTag(ItemUI __instance)
    {
        if (!Plugin.VREnabled) return;
        try
        {
            var t = __instance._lookingAtText;
            if (t == null || __instance._lookAtTarget == null) return;
            if (!__instance._isShowingLookAtText) return; // game hid it (scale tweens to zero)
            var anchor = VRUIManager.GetPriceAnchor(GameInfo.CurCamera);
            if (anchor == null) return;
            if (t.transform.parent != anchor.transform)
            {
                t.rectTransform.SetParent(anchor.transform, false);
                t.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
                t.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
                t.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                t.rectTransform.localPosition = Vector3.zero;
            }
            // The game wrote screen pixels here (meaningless in VR); pin the tag to the item in world
            // space so it reads like the dots over the shop items. The game's own _textOffset already
            // lifts the tag above the item (the old extra +0.5m made it float too high) — use it as-is.
            // Billboard the tag toward the player's camera so it stays readable from any angle (the
            // world-anchored canvas is axis-aligned, which left the text edge-on / "not rotated").
            t.transform.position = __instance._lookAtTarget.position + __instance._textOffset;
            var cam = GameInfo.CurCamera;
            if (cam != null)
            {
                Vector3 fwd = t.transform.position - cam.transform.position;
                if (fwd.sqrMagnitude < 1e-6f) fwd = cam.transform.forward;
                t.transform.rotation = Quaternion.LookRotation(fwd.normalized, cam.transform.up);
            }
        }
        catch { }
    }

    // ---- NPC dialog (NpcUI) ----
    // The talk bubble is DIALOGUE OVER THE NPC'S HEAD — diegetic world UI, not a head-following panel.
    // The game positions it with raw screen pixels (WorldToScreenPoint written into transform.position),
    // which is meaningless inside the converted world-space canvas (and NPCCanvas is whitelisted as a
    // head-following panel, so the bubble ended up floating in front of you instead of over the NPC).
    // Reparent the bubble (background + text) into the mod-owned world anchor and glue it above the
    // NPC's head each frame, billboarded so it reads from any angle — exactly like the price tag.

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NpcUI), "LateUpdate")]
    private static void NpcBackground(NpcUI __instance)
    {
        if (!Plugin.VREnabled) return;
        try
        {
            var bg = __instance._npcBackground;
            if (bg == null) return;
            var anchor = VRUIManager.GetPriceAnchor(GameInfo.CurCamera);
            if (anchor == null) return;

            var t = bg.transform;
            if (t.parent != anchor.transform)
            {
                t.SetParent(anchor.transform, false);
            }

            // The bubble was reparented OUT of its canvas, so the game's canvas-group alpha tween
            // (fade in/out) no longer reaches it — mirror it onto a CanvasGroup on the bubble (and the
            // text's own alpha if the text isn't a child of the bubble) so the show/hide fade still plays.
            float alpha = 0f;
            try { alpha = __instance._npcCanvas != null ? __instance._npcCanvas.alpha : 0f; } catch { }
            var cg = bg.GetComponent<CanvasGroup>();
            if (cg == null) cg = bg.gameObject.AddComponent<CanvasGroup>();
            cg.alpha = alpha;

            // Closed / behind the camera: the game faded it out — nothing more to do (the bubble stays
            // at its last spot, invisible).
            if (!NpcUI.NPCDialougeOpen || __instance._npcTarget == null) return;

            // Keep the corrected original target height (NpcUI projects target.position itself; it does
            // not add the old invented +0.45m lift), but retain the established shared world-UI sizing
            // and true world anchoring requested by the tester.
            Vector3 targetPos = __instance._npcTarget.position;
            t.position = targetPos;
            var cam = GameInfo.CurCamera;
            if (cam != null)
            {
                Vector3 direction = targetPos - cam.transform.position;
                if (direction.sqrMagnitude < 1e-6f) direction = cam.transform.forward;
                t.rotation = Quaternion.LookRotation(direction.normalized, cam.transform.up);
            }
            try
            {
                var text = __instance._npcText;
                if (text != null && !text.transform.IsChildOf(t))
                {
                    var c = text.color;
                    c.a = alpha;
                    text.color = c;
                }
            }
            catch { }
        }
        catch { }
    }

    // ---- Hit markers + damage numbers (OnHitUI, children of FXCanvas) ----

    [HarmonyPostfix]
    [HarmonyPatch(typeof(OnHitUI), "LateUpdate")]
    private static void HitMarkersAndDamage(OnHitUI __instance)
    {
        if (!Plugin.VREnabled) return;
        try
        {
            // Hit markers + damage numbers are DIEGETIC — they belong AT the hit point in the world (like
            // the item dots), not remapped onto the head-locked HUD panel. OnHitUI already stores the true
            // world position of each (_hitMarkerPositions / _damageTextPositions) and normally writes
            // WorldToScreenPoint(pos) into the element every LateUpdate; we run after that and instead
            // reparent the element into the mod's world anchor and place it AT the world position,
            // billboarded to the camera. The game's own scale pop / behind-camera hide (localScale) still
            // works untouched.
            var anchor = VRUIManager.GetPriceAnchor(GameInfo.CurCamera);
            if (anchor == null) return;
            var cam = GameInfo.CurCamera;

            var marks = __instance._hitMarkers;
            var markPos = __instance._hitMarkerPositions;
            if (marks != null && markPos != null)
                for (int i = 0; i < marks.Count && i < markPos.Count; i++)
                    WorldAnchorHit(marks[i] != null ? marks[i].transform : null, markPos[i], Vector3.zero, anchor.transform, cam);

            var dmg = __instance._damageTexts;
            var dmgPos = __instance._damageTextPositions;
            if (dmg != null && dmgPos != null)
                for (int j = 0; j < dmg.Count && j < dmgPos.Count; j++)
                    WorldAnchorHit(dmg[j] != null ? dmg[j].transform : null, dmgPos[j], Vector3.up * 0.18f, anchor.transform, cam);
        }
        catch { }
    }

    /// <summary>Place one hit marker / damage number AT its world hit position (diegetic), reparented into
    /// the mod's world anchor and billboarded to the camera. worldUpOffset lifts damage numbers above the
    /// hit point. Leaves the game's own localScale (pop-in / behind-camera hide) alone.</summary>
    private static void WorldAnchorHit(Transform t, Vector3 worldPos, Vector3 worldUpOffset, Transform anchor, Camera cam)
    {
        if (t == null || anchor == null) return;
        if (t.parent != anchor) t.SetParent(anchor, false);
        t.position = worldPos + worldUpOffset;
        if (cam != null)
        {
            Vector3 fwd = (worldPos - cam.transform.position);
            if (fwd.sqrMagnitude > 1e-6f)
                t.rotation = Quaternion.LookRotation(fwd.normalized, cam.transform.up); // billboard, upright
        }
    }

    // ---- Main-menu outfit buttons (SwapOutfitButtons) ----

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SwapOutfitButtons), "Update")]
    private static void OutfitButtons(SwapOutfitButtons __instance)
    {
        if (!Plugin.VREnabled) return;
        try
        {
            PinCustomizationButtons(__instance, MainMenuManager.MenuCam);
        }
        catch { }
    }

    /// <summary>
    /// Re-project the customization arrows after the final VR camera/UI pose. The original game projects
    /// each avatar attachment target into screen pixels. A converted world-space canvas cannot consume
    /// those pixels directly, so intersect the same camera-to-target ray with the actual VR menu plane.
    /// This preserves the original visual relationship to the hat/accessory/outfit in stereo instead of
    /// approximating it from the canvas resolution.
    /// </summary>
    public static void RePinCustomizationButtons()
    {
        if (!Plugin.VREnabled || !LocalSkin.IsInSkinCustomization) return;
        try
        {
            var cam = MainMenuManager.MenuCam;
            if (cam == null) return;
            foreach (var buttons in UnityEngine.Object.FindObjectsOfType<SwapOutfitButtons>(true))
                PinCustomizationButtons(buttons, cam);
        }
        catch { }
    }

    private static void PinCustomizationButtons(SwapOutfitButtons buttons, Camera cam)
    {
        if (buttons == null || cam == null || !LocalSkin.IsInSkinCustomization) return;
        PlaceCustomizationTarget(buttons._hatRect, LocalSkin.HatTarget, cam);
        PlaceCustomizationTarget(buttons._accessoryRect, LocalSkin.AccessoryTarget, cam);
        PlaceCustomizationTarget(buttons._outfitRect, LocalSkin.OutfitTarget, cam);
    }

    private static void PlaceCustomizationTarget(RectTransform rect, Transform target, Camera cam)
    {
        if (rect == null || target == null || cam == null) return;
        var canvas = rect.GetComponentInParent<Canvas>(true);
        if (canvas == null) return;
        canvas = canvas.rootCanvas != null ? canvas.rootCanvas : canvas;
        if (canvas.renderMode != RenderMode.WorldSpace) return;

        Vector3 direction = target.position - cam.transform.position;
        if (direction.sqrMagnitude < 1e-6f || Vector3.Dot(cam.transform.forward, direction) <= 0f)
            return;

        // The panel's forward vector is its geometric plane normal. Plane.Raycast is two-sided, so this
        // remains correct regardless of which canvas face Unity considers the visible side.
        var plane = new Plane(canvas.transform.forward, canvas.transform.position);
        var ray = new Ray(cam.transform.position, direction.normalized);
        if (!plane.Raycast(ray, out float distance) || distance <= 0f) return;

        rect.position = ray.GetPoint(distance);
        rect.rotation = canvas.transform.rotation;
    }

    // ---- Item dots: anchor to the ITEMS in the world (diegetic), not the screen ----

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CloseItemsUI), "LateUpdate")]
    private static void WorldAnchorDots(CloseItemsUI __instance)
    {
        if (!Plugin.VREnabled) return;
        try
        {
            PinDots(__instance, GameInfo.CurCamera);
        }
        catch { }
    }

    /// <summary>
    /// Re-pin every visible item dot to its item in the world. Called from the camera poser's
    /// onBeforeRender pass (the very last thing before the stereo render, after the camera was posed) —
    /// the game writes SCREEN-PIXEL positions into the dots in its own LateUpdate (before the camera
    /// pose), so without this final re-pin the dots can read as drifting off the items when you turn
    /// your head. Diegetic world UI must stay glued to the item, not the screen.
    /// </summary>
    public static void RePinWorldDots()
    {
        if (!Plugin.VREnabled) return;
        try
        {
            foreach (var ui in UnityEngine.Object.FindObjectsOfType<CloseItemsUI>(true))
            {
                PinDots(ui, GameInfo.CurCamera);
            }
        }
        catch { }
    }

    // Both item dots and NPC dialogue are fixed-pixel screen projections in the original game. A
    // two-metre stereo view plane preserves their angular size, avoids far-clip disappearance, and
    // still points at the exact world target from either eye/head pose.
    private const float ProjectionPlaneDistance = 2.0f;

    private static void PinDots(CloseItemsUI ui, Camera cam)
    {
        if (ui == null || cam == null || ui._closeItemsDots == null || ui._closestItems == null) return;
        bool globallyVisible = CloseItemsUI._dotsEnabled && Player.LocalPlayer &&
                               !EndGameEffects.IsShowingEndGame;
        int n = Mathf.Min(ui._closeItemsDots.Count, ui._closestItems.Length);
        for (int i = 0; i < n; i++)
        {
            var dot = ui._closeItemsDots[i];
            var item = ui._closestItems[i];
            if (dot == null) continue;
            if (!globallyVisible || ShouldHideDotItem(item))
            {
                dot.color = Color.clear;
                continue;
            }

            Vector3 direction = item.transform.position - cam.transform.position;
            if (direction.sqrMagnitude < 1e-6f || Vector3.Dot(cam.transform.forward, direction) <= 0f)
            {
                dot.color = Color.clear;
                continue;
            }

            bool aliveCreature = item.Creature && !item.Creature.IsDead;
            dot.color = aliveCreature ? ui._aliveColor : (item.DeadPlayer ? GameInfo.OrangeColor : Color.white);
            EnsureDotAlwaysVisible(dot);
            // True world pin: the marker lives at the item's actual position. The flatscreen game keeps
            // its dot at a fixed pixel size, so compensate for perspective by growing the world-space
            // transform linearly with distance. Its APPARENT/angular size therefore stays constant—it
            // does not become a giant dot on screen—and the ZTest-always material keeps it visible
            // through walls. Unlike the previous two-metre projection, stereo depth now matches the item.
            float distanceScale = direction.magnitude / ProjectionPlaneDistance;
            // Do not round-trip through stereo projection matrices here. During onBeforeRender those
            // matrices can alternate by eye, which made a stationary world marker shake left/right.
            dot.transform.position = item.transform.position;
            // Native dots all lie parallel to the screen. Tilting every marker toward its individual
            // view ray changed the perceived vertical placement, especially for items below eye level.
            dot.transform.rotation = cam.transform.rotation;
            dot.transform.localScale = Vector3.one * (DotWorldScale * distanceScale);
        }
        for (int i = n; i < ui._closeItemsDots.Count; i++)
            if (ui._closeItemsDots[i] != null) ui._closeItemsDots[i].color = Color.clear;
    }

    private static bool ShouldHideDotItem(Item item)
    {
        if (!item || item.Holder || item.IsDeinitializing) return true;
        if (item.Bird && !item.Bird.IsDead) return true;
        return item.IgnoredByCloseDots;
    }

    // Item-dot world size multiplier (the dots' authored size * this = on-screen world size).
    // Keep the game's original marker scale; distance compensation above changes only world size so
    // perspective does not make it shrink, not the marker's apparent size.
    private const float DotWorldScale = 3.0f;

    /// <summary>World pointers are guidance UI, not physical decals: keep their authored appearance but
    /// clone the material with depth testing disabled so terrain/walls cannot hide a nearby-item marker.</summary>
    private static void EnsureDotAlwaysVisible(Graphic dot)
    {
        if (dot == null) return;
        try
        {
            var source = dot.material != null ? dot.material : Canvas.GetDefaultCanvasMaterial();
            if (source == null) return;
            if ((source.name ?? "").EndsWith(" [VR Always Visible]", System.StringComparison.Ordinal)) return;

            var material = new Material(source)
            {
                name = (source.name ?? "ItemDot") + " [VR Always Visible]",
                hideFlags = HideFlags.HideAndDontSave,
            };
            material.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);
            if (material.HasProperty("_ZTest")) material.SetInt("_ZTest", (int)CompareFunction.Always);
            dot.material = material;
        }
        catch { }
    }
}
