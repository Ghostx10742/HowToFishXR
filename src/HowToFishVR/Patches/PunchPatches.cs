using HarmonyLib;
using HowToFishVR.VR;
using UnityEngine;

namespace HowToFishVR.Patches;

/// <summary>
/// Makes empty-hand punching originate from the calibrated right hand and use the same corrected
/// controller-forward axis as VR throwing. The game's original 1.5 m ray is too short for common VR
/// poses such as pointing down at a crab from standing height, so VR targeting receives a 3 m minimum
/// while preserving the game's sphere radius, obstruction checks, damage, timing, and effects.
///
/// These patches also guarantee a punch can never target or damage the local player's own restored
/// body: the target is dropped at selection time and damage is skipped at hit time.
/// </summary>
[HarmonyPatch(typeof(PlayerPunching))]
internal static class PunchPatches
{
    internal const float MinimumVRPunchRange = 3f;
    internal const float MinimumVRAimAssistRadius = 0.35f;

    private static Creature _inputHitCreature;
    private static float _inputHitExpires;
    private static Transform _guideAssistTarget;
    private static Vector3 _guideAssistLocalPoint;
    private static float _guideAssistExpires;

    internal struct CameraPoseState
    {
        internal Transform Camera;
        internal Vector3 Position;
        internal Quaternion Rotation;
        internal bool Changed;
    }

    internal struct TargetAimState
    {
        internal CameraPoseState CameraPose;
        internal float OriginalRange;
        internal bool RangeChanged;
    }

    /// <summary>Temporarily put the game's camera-based melee calculations on the current right-hand
    /// pose. The 45-degree correction is deliberately identical to hand-directed throwing: OpenXR's
    /// raw grip-forward axis is not the natural direction in which the palm points.</summary>
    internal static CameraPoseState BeginRightHandAim(Transform camera)
    {
        var state = new CameraPoseState { Camera = camera };
        if (!Plugin.HeadsetActive || camera == null) return state;
        try
        {
            var rig = VRRig.Instance;
            var hand = rig != null ? rig.RightHand : null;
            if (hand == null) return state;
            state.Position = camera.position;
            state.Rotation = camera.rotation;
            if (!TryGetRightHandAim(out Vector3 origin, out Vector3 direction)) return state;
            camera.SetPositionAndRotation(origin, Quaternion.LookRotation(direction, hand.rotation * Vector3.up));
            state.Changed = true;
        }
        catch { state.Changed = false; }
        return state;
    }

    internal static bool TryGetRightHandAim(out Vector3 origin, out Vector3 direction)
    {
        origin = Vector3.zero;
        direction = Vector3.forward;
        if (!Plugin.HeadsetActive) return false;
        try
        {
            var rig = VRRig.Instance;
            var hand = rig != null ? rig.RightHand : null;
            if (hand == null) return false;
            origin = PlayerHandsPatches.CalibratedHandPosition(hand, isLeft: false);
            direction = hand.rotation * Quaternion.Euler(45f, 0f, 0f) * Vector3.forward;
            if (direction.sqrMagnitude < 1e-6f) return false;
            direction.Normalize();
            return true;
        }
        catch { return false; }
    }

    internal static void EndRightHandAim(CameraPoseState state)
    {
        if (!state.Changed || state.Camera == null) return;
        try { state.Camera.SetPositionAndRotation(state.Position, state.Rotation); } catch { }
    }

    internal static TargetAimState BeginRightHandTarget(Transform camera, ref float range)
    {
        var state = new TargetAimState
        {
            CameraPose = BeginRightHandAim(camera),
            OriginalRange = range
        };
        if (!state.CameraPose.Changed) return state;
        if (range < MinimumVRPunchRange)
        {
            range = MinimumVRPunchRange;
            state.RangeChanged = true;
        }
        return state;
    }

    internal static void EndRightHandTarget(ref float range, TargetAimState state)
    {
        if (state.RangeChanged) range = state.OriginalRange;
        EndRightHandAim(state.CameraPose);
    }

    /// <summary>The base game sphere-casts for items but uses a zero-width ray for NPCs and the level.
    /// Small creatures on the ground are therefore commonly skipped while the pond floor is selected.
    /// Search a modest corridor around the exact hand ray and prefer a real damageable target; a normal
    /// level ray still rejects anything hidden behind terrain or walls.</summary>
    internal static bool TryFindAssistedTarget(
        Player attacker,
        Vector3 origin,
        Vector3 direction,
        float authoredRadius,
        float range,
        out Transform target,
        out Vector3 worldHitPoint)
    {
        target = null;
        worldHitPoint = Vector3.zero;
        if (direction.sqrMagnitude < 1e-6f) return false;
        direction.Normalize();
        float radius = Mathf.Max(MinimumVRAimAssistRadius, authoredRadius);

        // Creature prefabs do not share a reliable physics-layer/ItemManager layout. Resolve live
        // Creature components geometrically first so crabs and other floor fauna use the exact same
        // assisted hand ray as fish, regardless of which child colliders their prefab registered.
        if (TryFindCreatureAlongAim(attacker, origin, direction, radius,
                Mathf.Max(MinimumVRPunchRange, range), groundOnly: false, out target, out worldHitPoint))
        {
            RememberGuideAssist(target, worldHitPoint);
            return true;
        }

        // Ground-creature prefab colliders span Item, ItemPart, and Unity's Default layer. Query every
        // physics layer, then strictly filter through ResolvePunchableTarget; this cannot select scenery,
        // UI, water, or arbitrary triggers because only registered/parented Items, players and NPCs pass.
        int mask = Physics.AllLayers;
        RaycastHit[] hits = Physics.SphereCastAll(
            origin, radius, direction, Mathf.Max(MinimumVRPunchRange, range), mask,
            QueryTriggerInteraction.Collide);

        float nearest = float.PositiveInfinity;
        for (int i = 0; i < hits.Length; i++)
        {
            var hit = hits[i];
            Transform resolved = ResolvePunchableTarget(hit, attacker);
            if (resolved == null || hit.distance >= nearest) continue;
            Vector3 point = hit.point != Vector3.zero
                ? hit.point
                : hit.collider.ClosestPoint(origin + direction * Mathf.Max(hit.distance, 0.01f));
            if (IsLevelBlocked(origin, point, radius)) continue;
            nearest = hit.distance;
            target = resolved;
            worldHitPoint = point;
        }
        if (target != null) RememberGuideAssist(target, worldHitPoint);
        return target != null;
    }

    /// <summary>Expose the target already selected by the input-time assist to the visual guide. This is
    /// an O(1) transform lookup, so the guide bends to the actual assisted point without bringing back
    /// the expensive per-frame scan across every live creature and renderer.</summary>
    internal static bool TryGetRecentAssistedAimPoint(out Vector3 worldPoint)
    {
        worldPoint = Vector3.zero;
        if (_guideAssistTarget == null || Time.unscaledTime > _guideAssistExpires) return false;
        try
        {
            worldPoint = _guideAssistTarget.TransformPoint(_guideAssistLocalPoint);
            return true;
        }
        catch { return false; }
    }

    private static void RememberGuideAssist(Transform target, Vector3 worldPoint)
    {
        if (target == null) return;
        try
        {
            _guideAssistTarget = target;
            _guideAssistLocalPoint = target.InverseTransformPoint(worldPoint);
            _guideAssistExpires = Time.unscaledTime + 0.75f;
        }
        catch { }
    }

    private static bool TryFindCreatureAlongAim(
        Player attacker,
        Vector3 origin,
        Vector3 direction,
        float radius,
        float range,
        bool groundOnly,
        out Transform target,
        out Vector3 worldHitPoint)
    {
        target = null;
        worldHitPoint = Vector3.zero;
        float bestScore = float.PositiveInfinity;
        Item held = null;
        try { held = attacker != null && attacker.Holding != null ? attacker.Holding.HeldItem : null; } catch { }

        Creature[] creatures;
        try { creatures = Object.FindObjectsByType<Creature>(FindObjectsSortMode.None); }
        catch { return false; }

        for (int i = 0; i < creatures.Length; i++)
        {
            var creature = creatures[i];
            if (creature == null || creature == held || !creature.gameObject.activeInHierarchy) continue;
            if (groundOnly && !IsGroundCreature(creature)) continue;

            // Do not depend solely on the prefab's colliders. Ground creatures use small, offset child
            // colliders that can sit below the visible body/pond floor. Their rigidbody/render bounds
            // provide the stable visible-body point the player is actually aiming at.
            Vector3 bodyPoint = creature.transform.position;
            try
            {
                if (creature.Rig != null) bodyPoint = creature.Rig.worldCenterOfMass;
                Renderer[] renderers = creature.GetComponentsInChildren<Renderer>(false);
                if (renderers.Length > 0)
                {
                    Bounds visible = renderers[0].bounds;
                    for (int r = 1; r < renderers.Length; r++)
                        if (renderers[r] != null && renderers[r].enabled) visible.Encapsulate(renderers[r].bounds);
                    bodyPoint = visible.center;
                }
            }
            catch { }

            float bodyAlong = Vector3.Dot(bodyPoint - origin, direction);
            if (bodyAlong >= 0f && bodyAlong <= range + 0.5f)
            {
                Vector3 bodyRayPoint = origin + direction * Mathf.Clamp(bodyAlong, 0f, range);
                float bodySeparation = Vector3.Distance(bodyRayPoint, bodyPoint);
                // A roughly 20-degree cone at normal standing distance catches a crab that is visibly
                // under the guide without turning the attack into a general nearest-creature hit.
                float coneRadius = Mathf.Max(creature is Crab ? 1.0f : 0.65f, bodyAlong * 0.36f);
                if (bodySeparation <= coneRadius && (groundOnly || !IsWallBlocked(origin, bodyPoint)))
                {
                    float bodyScore = bodyAlong + bodySeparation * 0.4f;
                    if (bodyScore < bestScore)
                    {
                        bestScore = bodyScore;
                        target = creature.transform;
                        worldHitPoint = bodyPoint;
                    }
                }
            }

            Collider[] colliders;
            try { colliders = creature.GetComponentsInChildren<Collider>(false); }
            catch { continue; }

            for (int j = 0; j < colliders.Length; j++)
            {
                var collider = colliders[j];
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                Bounds bounds = collider.bounds;
                float along = Vector3.Dot(bounds.center - origin, direction);
                if (along < 0f || along > range + radius) continue;
                along = Mathf.Clamp(along, 0f, range);
                Vector3 rayPoint = origin + direction * along;
                Vector3 point;
                try { point = collider.ClosestPoint(rayPoint); }
                catch { point = bounds.ClosestPoint(rayPoint); }
                float separation = Vector3.Distance(rayPoint, point);
                // Crabs and other ground creatures are tiny and move between the trigger press and the
                // delayed punch impact. Give those creatures a slightly wider corridor, and ignore only
                // upward-facing ground underneath them; vertical walls remain blockers.
                float allowedRadius = creature is Crab ? Mathf.Max(radius, 0.55f) : radius + 0.05f;
                if (separation > allowedRadius || (!groundOnly && IsWallBlocked(origin, point))) continue;

                float score = along + separation * 0.25f;
                if (score >= bestScore) continue;
                bestScore = score;
                target = creature.transform;
                worldHitPoint = point;
            }
        }
        return target != null;
    }

    private static bool IsGroundCreature(Creature creature)
    {
        if (creature == null) return false;
        if (creature is Crab) return true; // brown/rock crabs and lobsters
        try
        {
            string n = creature.gameObject.name;
            if (n.IndexOf("Crab", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("Lobster", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("Shrimp", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("Snail", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        catch { }
        try
        {
            Vector3 center = creature.Rig != null ? creature.Rig.worldCenterOfMass : creature.transform.position;
            return Physics.Raycast(center + Vector3.up * 0.2f, Vector3.down, 1.2f,
                (int)GameInfo.LevelLayer | (int)GameInfo.BoatLayer, QueryTriggerInteraction.Ignore);
        }
        catch { return false; }
    }

    private static bool IsWallBlocked(Vector3 origin, Vector3 point)
    {
        Vector3 toPoint = point - origin;
        float distance = toPoint.magnitude;
        if (distance <= 0.03f) return false;
        try
        {
            RaycastHit[] blockers = Physics.RaycastAll(
                origin, toPoint / distance, distance, GameInfo.LevelLayer, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < blockers.Length; i++)
            {
                var blocker = blockers[i];
                if (blocker.distance + 0.03f >= distance) continue;
                // Ground and pond-bed surfaces are expected when aiming downward. Only wall-like faces
                // should prevent the assisted creature target.
                if (blocker.normal.y >= 0.45f) continue;
                return true;
            }
        }
        catch { }
        return false;
    }

    private static Transform ResolvePunchableTarget(RaycastHit hit, Player attacker)
    {
        if (hit.collider == null || hit.transform == null) return null;
        try
        {
            // Creatures (including crabs and pond fish) inherit Item. Do not require IsInteractable
            // here: a creature can temporarily reject pickup interaction while remaining punchable.
            var item = ItemManager.Get(hit.collider);
            if (item == null)
            {
                // Some creature render colliders (notably both crab variants) are children on the
                // Default layer and are not guaranteed to be present in ItemManager's collider map.
                // Resolve their owning Creature/Item through the hierarchy just as projectile impacts do.
                item = hit.collider.GetComponentInParent<Item>();
            }
            if (item != null)
            {
                var held = attacker != null && attacker.Holding != null ? attacker.Holding.HeldItem : null;
                // ItemManager's collider lookup can resolve a creature from one of its unregistered
                // child colliders, but HitTarget later performs a transform lookup. Pass the registered
                // Item root so that fish, crabs, and every other Creature reach Item.LocalHit.
                return item == held ? null : item.transform;
            }
        }
        catch { }
        try
        {
            var player = PlayerManager.GetPlayerFromBodyPart(hit.transform);
            if (player != null) return player == attacker ? null : hit.transform;
        }
        catch { }
        try
        {
            if (attacker != null && attacker.Transform != null && hit.transform.root == attacker.Transform.root)
                return null;
        }
        catch { }
        try
        {
            for (Transform t = hit.transform; t != null; t = t.parent)
                if (t.CompareTag("NPC")) return t;
        }
        catch { }
        return null;
    }

    private static bool IsLevelBlocked(Vector3 origin, Vector3 point, float assistRadius)
    {
        Vector3 toPoint = point - origin;
        float distance = toPoint.magnitude;
        if (distance <= 0.03f) return false;
        try
        {
            RaycastHit[] blockers = Physics.RaycastAll(
                origin, toPoint / distance, distance, GameInfo.LevelLayer, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < blockers.Length; i++)
            {
                var blocker = blockers[i];
                if (blocker.distance + 0.03f >= distance) continue;

                // A crab/frog resting on the pond floor has its closest collider point almost flush
                // with that floor. The old line test called those last few centimetres an obstruction,
                // even though the assist sphere had legitimately reached the creature. Permit only an
                // upward-facing ground surface immediately beside the target; vertical walls and terrain
                // farther in front remain hard blockers, so this does not enable through-wall punches.
                float remaining = distance - blocker.distance;
                if (blocker.normal.y >= 0.45f && remaining <= assistRadius + 0.12f) continue;
                return true;
            }
        }
        catch { }
        return false;
    }

    internal static void PreferAssistedTarget(
        Player attacker,
        Transform camera,
        float radius,
        float range,
        float hitOffset,
        ref Transform finalTarget,
        ref Vector3 finalHitPoint)
    {
        if (camera == null) return;
        // The game's native item sphere-cast records hit.transform. For compound creatures that may
        // be a collider child even though ItemManager resolved it through the Collider overload. The
        // later damage path only has the Transform overload, so normalize native selections too (the
        // assist path already returns the Item root). Preserve the same world-space impact point.
        NormalizeItemTarget(ref finalTarget, ref finalHitPoint);

        // Keep a valid item/player/NPC selected by the original game. Replace only a miss or the level
        // hit that commonly sits directly behind a tiny ground creature.
        bool needsAssist = finalTarget == null;
        try { needsAssist |= finalTarget != null && (finalTarget.CompareTag("Level") || finalTarget.CompareTag("Boat")); }
        catch { }
        if (!needsAssist) return;
        if (!TryFindAssistedTarget(attacker, camera.position, camera.forward, radius, range,
                out Transform assisted, out Vector3 hitPoint)) return;
        finalTarget = assisted;
        finalHitPoint = assisted.InverseTransformPoint(hitPoint - camera.forward * hitOffset);
    }

    /// <summary>Re-check a missed/level punch at the exact impact frame. Small moving ground creatures
    /// can leave the initial sphere cast during the attack animation; this makes the delayed damage use
    /// the current hand ray and current creature position instead of the stale trigger-time result.</summary>
    internal static void SupplyGroundCreatureAtImpact(
        Player attacker,
        float authoredRadius,
        float range,
        float hitOffset,
        ref Transform target,
        ref Vector3 localHitPoint)
    {
        if (attacker == null) return;
        if (ResolveCreature(target) != null) return;
        try
        {
            if (target != null)
            {
                if (PlayerManager.GetPlayerFromBodyPart(target) != null || target.CompareTag("NPC")) return;
            }
        }
        catch { }
        if (!TryGetRightHandAim(out Vector3 origin, out Vector3 direction)) return;
        if (!TryFindCreatureAlongAim(attacker, origin, direction,
                Mathf.Max(MinimumVRAimAssistRadius, authoredRadius),
                Mathf.Max(MinimumVRPunchRange, range), groundOnly: false,
                out Transform creature, out Vector3 point)) return;
        target = creature;
        localHitPoint = creature.InverseTransformPoint(point - direction * hitOffset);
    }

    /// <summary>Ground creatures are damaged directly from the attack input because their prefab
    /// colliders are not accepted by the game's delayed melee target pipeline. The normal attack still
    /// animates and syncs; its later HitTarget is consumed so this cannot deal double damage.</summary>
    internal static void DirectGroundCreatureHit(Player attacker, int damage, float forceMagnitude)
    {
        if (attacker == null || !TryGetRightHandAim(out Vector3 origin, out Vector3 direction)) return;
        if (!TryFindCreatureAlongAim(attacker, origin, direction, MinimumVRAimAssistRadius,
                MinimumVRPunchRange, groundOnly: true, out Transform target, out Vector3 point)) return;
        Creature creature = ResolveCreature(target);
        if (creature == null) return;
        RememberGuideAssist(target, point);
        Vector3 localPoint = target.InverseTransformPoint(point);
        if (!TryHitCreatureDirect(target, localPoint, attacker, damage, forceMagnitude)) return;
        _inputHitCreature = creature;
        _inputHitExpires = Time.unscaledTime + 1f;
    }

    internal static bool ConsumeDirectGroundCreatureHit(Transform target)
    {
        Creature creature = ResolveCreature(target);
        if (creature == null || creature != _inputHitCreature || Time.unscaledTime > _inputHitExpires)
            return false;
        _inputHitCreature = null;
        _inputHitExpires = 0f;
        return true;
    }

    private static void NormalizeItemTarget(ref Transform target, ref Vector3 localHitPoint)
    {
        if (target == null) return;
        try
        {
            Item item = ItemManager.Get(target);
            if (item == null)
            {
                // Some pickup colliders are registered only in ItemManager's collider map. Walking
                // upward also covers those compound creature hierarchies once the Collider is gone.
                for (Transform current = target; current != null && item == null; current = current.parent)
                {
                    item = ItemManager.Get(current);
                    if (item == null) item = current.GetComponent<Item>();
                }
            }
            if (item == null || item.transform == target) return;

            Vector3 worldHitPoint = target.TransformPoint(localHitPoint);
            target = item.transform;
            localHitPoint = target.InverseTransformPoint(worldHitPoint);
        }
        catch { }
    }

    private static bool IsLocalPuncher(PlayerPunching punching)
    {
        try
        {
            return punching != null && punching._player != null && punching._player.Owner != null &&
                   punching._player.Owner.IsLocalClient;
        }
        catch { return false; }
    }

    internal static bool IsKnife(Melee melee)
    {
        if (melee == null || melee._useBothHands) return false;
        try { return melee.gameObject.name.StartsWith("Knife", System.StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    internal static bool IsHandAimedMelee(Melee melee) => melee != null && (melee._useBothHands || IsKnife(melee));

    private static Creature ResolveCreature(Transform target)
    {
        if (target == null) return null;
        try
        {
            var item = ItemManager.Get(target);
            if (item != null && item.Creature != null) return item.Creature;
        }
        catch { }
        try { return target.GetComponentInParent<Creature>(); }
        catch { return null; }
    }

    /// <summary>Apply creature damage through Creature.LocalHit itself. The vanilla HitTarget method
    /// first re-resolves an Item using only a Transform; that is the final failure point for compound
    /// ground-creature colliders. This is otherwise the same damage/force path and timing.</summary>
    internal static bool TryHitCreatureDirect(
        Transform selectedTarget,
        Vector3 selectedLocalHitPoint,
        Player attacker,
        int damage,
        float forceMagnitude)
    {
        if (selectedTarget == null || attacker == null || attacker.CamObject == null) return false;
        Creature creature = ResolveCreature(selectedTarget);
        if (creature == null) return false;

        Vector3 point = selectedTarget.TransformPoint(selectedLocalHitPoint);
        Vector3 direction = attacker.CamObject.forward;
        Vector3 force = direction * forceMagnitude;
        if (force.y < 0f) force.y = 0f;
        creature.LocalHit(creature.transform, point, direction, attacker, damage, rangedHit: false, force);
        return true;
    }

    private static bool IsLocalPlayerBody(Transform t)
    {
        if (t == null) return false;
        try
        {
            var p = PlayerManager.GetPlayerFromBodyPart(t);
            if (p != null) return p == Player.LocalPlayer;
        }
        catch { }
        // Unregistered colliders (e.g. the restored body) may not map through PlayerManager — fall back
        // to comparing the transform root with the local player's root.
        try
        {
            var lp = Player.LocalPlayer;
            if (lp != null && lp.Transform != null && t.root != null && t.root == lp.Transform.root) return true;
        }
        catch { }
        return false;
    }

    /// <summary>Drop the punch target if it resolved to the local player's own body.</summary>
    [HarmonyPostfix]
    [HarmonyPatch("CheckForPlayersAndLevel")]
    private static void ExcludeSelf(PlayerPunching __instance, ref Transform finalTarget, ref Vector3 finalHitPoint)
    {
        if (!Plugin.VREnabled) return;
        if (IsLocalPlayerBody(finalTarget))
        {
            finalTarget = null;
            finalHitPoint = Vector3.zero;
        }
    }

    /// <summary>Skip damage/effects against our own body. For a valid local hit, keep the right-hand
    /// direction active while the game calculates impact direction and force.</summary>
    [HarmonyPrefix]
    [HarmonyPatch("HitTarget")]
    private static bool NoSelfHit(PlayerPunching __instance, int side, out CameraPoseState __state)
    {
        __state = default;
        if (!Plugin.VREnabled) return true;
        if (__instance._curTarget == null || side < 0 || side >= __instance._curTarget.Length) return true;
        if (IsLocalPlayerBody(__instance._curTarget[side])) return false; // skip entirely — no effects, no damage
        if (ConsumeDirectGroundCreatureHit(__instance._curTarget[side])) return false;
        if (IsLocalPuncher(__instance))
        {
            __state = BeginRightHandAim(__instance._player != null ? __instance._player.CamObject : null);
            int damage = ServerSettings.OneShotEnabled ? 99999 : __instance._damage;
            if (TryHitCreatureDirect(__instance._curTarget[side], __instance._targetHitPoint[side],
                    __instance._player, damage, __instance._force))
                return false;
        }
        return true;
    }

    [HarmonyPostfix]
    [HarmonyPatch("HitTarget")]
    private static void RestoreAfterHit(CameraPoseState __state) => EndRightHandAim(__state);

    [HarmonyPrefix]
    [HarmonyPatch("PunchInput")]
    private static void DirectGroundCreatureOnInput(PlayerPunching __instance)
    {
        if (!Plugin.VREnabled || !IsLocalPuncher(__instance)) return;
        try
        {
            if (__instance._player.BlockInputs || __instance._player.Holding.HeldItem != null ||
                Boat.IsDrivingLocally || Boat.WantToDrive || !__instance.CanPunch()) return;
            int damage = ServerSettings.OneShotEnabled ? 99999 : __instance._damage;
            DirectGroundCreatureHit(__instance._player, damage, __instance._force);
        }
        catch { }
    }

    // Make the creature the authoritative attack target before the game sends/stores StartPunching.
    // This avoids depending on the ground creature's child collider being registered in ItemManager.
    [HarmonyPrefix]
    [HarmonyPatch("StartPunching")]
    private static void RetargetGroundCreatureOnStart(
        PlayerPunching __instance,
        ref Transform target,
        ref Vector3 targetHitPoint,
        bool calledFromLocal)
    {
        if (!Plugin.VREnabled || !calledFromLocal || !IsLocalPuncher(__instance)) return;
        SupplyGroundCreatureAtImpact(__instance._player, __instance._hitRadius, __instance._range,
            __instance._hitOffsetDist, ref target, ref targetHitPoint);
    }

    [HarmonyPrefix]
    [HarmonyPatch("StartReturning")]
    private static void RetargetGroundCreatureAtImpact(PlayerPunching __instance, int side)
    {
        if (!Plugin.VREnabled || !IsLocalPuncher(__instance) || __instance._curTarget == null ||
            __instance._targetHitPoint == null || side < 0 || side >= __instance._curTarget.Length ||
            side >= __instance._targetHitPoint.Length) return;
        SupplyGroundCreatureAtImpact(__instance._player, __instance._hitRadius, __instance._range,
            __instance._hitOffsetDist, ref __instance._curTarget[side], ref __instance._targetHitPoint[side]);
    }

    // Scope the temporary hand pose to the two target-query methods. FindPunchTarget also starts the
    // game's attack animation, and leaving the camera at the hand for that whole method would corrupt
    // the animation's camera-relative start pose.
    [HarmonyPrefix]
    [HarmonyPatch("CheckForItems")]
    private static void ItemAimPrefix(PlayerPunching __instance, out TargetAimState __state)
    {
        __state = default;
        if (!IsLocalPuncher(__instance)) return;
        Transform cam = __instance._player != null ? __instance._player.CamObject : null;
        __state = BeginRightHandTarget(cam, ref __instance._range);
    }

    [HarmonyPostfix]
    [HarmonyPatch("CheckForItems")]
    private static void ItemAimPostfix(
        PlayerPunching __instance,
        ref Transform finalTarget,
        ref Vector3 finalHitPoint,
        TargetAimState __state)
    {
        if (__state.CameraPose.Changed)
            PreferAssistedTarget(__instance._player, __instance._player.CamObject, __instance._hitRadius,
                __instance._range, __instance._hitOffsetDist, ref finalTarget, ref finalHitPoint);
        EndRightHandTarget(ref __instance._range, __state);
    }

    [HarmonyPrefix]
    [HarmonyPatch("CheckForPlayersAndLevel")]
    private static void WorldAimPrefix(PlayerPunching __instance, out TargetAimState __state)
    {
        __state = default;
        if (!IsLocalPuncher(__instance)) return;
        Transform cam = __instance._player != null ? __instance._player.CamObject : null;
        __state = BeginRightHandTarget(cam, ref __instance._range);
    }

    [HarmonyPostfix]
    [HarmonyPatch("CheckForPlayersAndLevel")]
    private static void WorldAimPostfix(
        PlayerPunching __instance,
        ref Transform finalTarget,
        ref Vector3 finalHitPoint,
        TargetAimState __state)
    {
        if (__state.CameraPose.Changed)
            PreferAssistedTarget(__instance._player, __instance._player.CamObject, __instance._hitRadius,
                __instance._range, __instance._hitOffsetDist, ref finalTarget, ref finalHitPoint);
        EndRightHandTarget(ref __instance._range, __state);
    }
}

/// <summary>Brass knuckles and the knife use the same right-hand targeting and minimum range as
/// empty-hand punching. Other melee tools keep their normal tool behavior.</summary>
[HarmonyPatch(typeof(Melee))]
internal static class HandMeleeAimPatches
{
    private static bool IsLocalHandMelee(Melee melee)
    {
        try
        {
            return PunchPatches.IsHandAimedMelee(melee) && melee.Holder != null && melee.Holder.Owner != null &&
                   melee.Holder.Owner.IsLocalClient;
        }
        catch { return false; }
    }

    [HarmonyPrefix]
    [HarmonyPatch("CheckForItems")]
    private static void ItemAimPrefix(Melee __instance, out PunchPatches.TargetAimState __state)
    {
        __state = default;
        if (!IsLocalHandMelee(__instance)) return;
        __state = PunchPatches.BeginRightHandTarget(__instance.Holder.CamObject, ref __instance._range);
    }

    [HarmonyPostfix]
    [HarmonyPatch("CheckForItems")]
    private static void ItemAimPostfix(
        Melee __instance,
        ref Transform finalTarget,
        ref Vector3 finalHitPoint,
        PunchPatches.TargetAimState __state)
    {
        if (__state.CameraPose.Changed)
            PunchPatches.PreferAssistedTarget(__instance.Holder, __instance.Holder.CamObject,
                __instance._hitRadius, __instance._range, __instance._hitOffsetDist,
                ref finalTarget, ref finalHitPoint);
        PunchPatches.EndRightHandTarget(ref __instance._range, __state);
    }

    [HarmonyPrefix]
    [HarmonyPatch("CheckForPlayersAndLevel")]
    private static void WorldAimPrefix(Melee __instance, out PunchPatches.TargetAimState __state)
    {
        __state = default;
        if (!IsLocalHandMelee(__instance)) return;
        __state = PunchPatches.BeginRightHandTarget(__instance.Holder.CamObject, ref __instance._range);
    }

    [HarmonyPostfix]
    [HarmonyPatch("CheckForPlayersAndLevel")]
    private static void WorldAimPostfix(
        Melee __instance,
        ref Transform finalTarget,
        ref Vector3 finalHitPoint,
        PunchPatches.TargetAimState __state)
    {
        if (__state.CameraPose.Changed)
            PunchPatches.PreferAssistedTarget(__instance.Holder, __instance.Holder.CamObject,
                __instance._hitRadius, __instance._range, __instance._hitOffsetDist,
                ref finalTarget, ref finalHitPoint);
        PunchPatches.EndRightHandTarget(ref __instance._range, __state);
    }

    [HarmonyPrefix]
    [HarmonyPatch("HitTarget")]
    private static bool HitAimPrefix(Melee __instance, int side, out PunchPatches.CameraPoseState __state)
    {
        __state = default;
        if (!IsLocalHandMelee(__instance)) return true;
        if (__instance._curTarget != null && side >= 0 && side < __instance._curTarget.Length &&
            PunchPatches.ConsumeDirectGroundCreatureHit(__instance._curTarget[side])) return false;
        __state = PunchPatches.BeginRightHandAim(__instance.Holder.CamObject);
        if (__instance._curTarget != null && side >= 0 && side < __instance._curTarget.Length)
        {
            int damage = ServerSettings.OneShotEnabled ? 99999 : __instance.GetCurSharpness().Damage;
            if (PunchPatches.TryHitCreatureDirect(
                    __instance._curTarget[side], __instance._targetHitPoint[side], __instance.Holder,
                    damage, __instance._force))
                return false;
        }
        return true;
    }

    [HarmonyPostfix]
    [HarmonyPatch("HitTarget")]
    private static void HitAimPostfix(PunchPatches.CameraPoseState __state) =>
        PunchPatches.EndRightHandAim(__state);

    [HarmonyPrefix]
    [HarmonyPatch("PrimaryInput")]
    private static void DirectGroundCreatureOnInput(Melee __instance)
    {
        if (!Plugin.VREnabled || !IsLocalHandMelee(__instance)) return;
        try
        {
            if (!__instance.CanAttack()) return;
            int damage = ServerSettings.OneShotEnabled ? 99999 : __instance.GetCurSharpness().Damage;
            PunchPatches.DirectGroundCreatureHit(__instance.Holder, damage, __instance._force);
        }
        catch { }
    }

    [HarmonyPrefix]
    [HarmonyPatch("StartAttacking")]
    private static void RetargetGroundCreatureOnStart(
        Melee __instance,
        ref Transform target,
        ref Vector3 targetHitPoint,
        bool calledFromLocal)
    {
        if (!Plugin.VREnabled || !calledFromLocal || !IsLocalHandMelee(__instance)) return;
        PunchPatches.SupplyGroundCreatureAtImpact(__instance.Holder, __instance._hitRadius,
            __instance._range, __instance._hitOffsetDist, ref target, ref targetHitPoint);
    }

    [HarmonyPrefix]
    [HarmonyPatch("StartReturning")]
    private static void RetargetGroundCreatureAtImpact(Melee __instance, int side)
    {
        if (!Plugin.VREnabled || !IsLocalHandMelee(__instance) || __instance._curTarget == null ||
            __instance._targetHitPoint == null || side < 0 || side >= __instance._curTarget.Length ||
            side >= __instance._targetHitPoint.Length) return;
        PunchPatches.SupplyGroundCreatureAtImpact(__instance.Holder, __instance._hitRadius,
            __instance._range, __instance._hitOffsetDist,
            ref __instance._curTarget[side], ref __instance._targetHitPoint[side]);
    }
}
