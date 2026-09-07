using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace HowToFishVR.Body;

/// <summary>
/// Restores the LOCAL player's full visible body (legs, torso, arms) that the game deletes on spawn.
/// The game leaves the first-person local player as a pair of floating hands; we save the body objects
/// before the destroy pass, re-enable the components the game disabled locally (OtherPlayer, PlayerBody,
/// PlayerLegs, PlayerArms, IK) and drive the body from the player. Conceptually a rework of the SeaLegs
/// idea, integrated into this mod so it ships together with the VR systems. In VR the restored arms are
/// driven by the controller-tracking IK (<see cref="LocalBodyDriver"/>) so elbows bend correctly and the
/// body follows roomscale movement 1:1.
/// </summary>
internal static class HowToFishBody
{
    private static Player _player;
    private static List<GameObject> _bodyObjects;
    private static LocalBodyDriver _driver;

    /// <summary>True once the local body has been saved and re-enabled.</summary>
    public static bool Active { get; private set; }

    /// <summary>The saved local body hierarchy (restored "other" player objects).</summary>
    public static List<GameObject> BodyObjects => _bodyObjects;

    /// <summary>The player the body is currently built for (for the per-frame lifecycle validity gate).</summary>
    public static Player BoundPlayer => _player;

    /// <summary>Apply the restored body's current parent/position/yaw immediately. Snap turns call this
    /// after the VR rig receives its new yaw so PlayerBody.LateUpdate cannot run once under a stale
    /// parent basis and leave a cumulative local-space rotation behind.</summary>
    internal static void SyncCurrentPose()
    {
        if (!Active || _driver == null) return;
        try { _driver.Sync(); } catch { }
    }

    /// <summary>
    /// FRIK-style validity check (isRootNodeValid + isGameReadyForSkeletonInitialization): are ALL the
    /// references the restored body depends on still LIVE? Used by <see cref="BodyLifecycle"/> every frame
    /// to detect that the game destroyed/recreated the player (death-respawn, new scene, joined a game) and
    /// the cached body/driver/IK references went stale — so it can drop them and rebuild. Returns false on
    /// any dangling reference (Unity fake-null included).
    /// </summary>
    public static bool RefsAlive()
    {
        try
        {
            if (_player == null || _driver == null || _bodyObjects == null) return false;
            for (int i = 0; i < _bodyObjects.Count; i++) if (_bodyObjects[i] == null) return false;
            if (_player.Other == null || _player.Other._transform == null) return false;
            var arms = _player.Arms;
            if (arms == null || arms._ikRight == null || arms._ikLeft == null) return false;
            var hands = _player.Hands;
            if (hands == null || hands.HandBoneLeft == null || hands.HandBoneRight == null) return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Remember the local player's body objects WITHOUT building. Called from the InitializePlayer
    /// postfix on EVERY spawn (Full Body or Hands Only): keeping the objects alive + remembered means
    /// toggling back to Full Body later can always rebuild instantly — the game never got to destroy
    /// them, and we never hold stale references across a respawn/scene change.
    /// </summary>
    internal static void Store(Player player, List<GameObject> bodyObjects)
    {
        _player = player;
        _bodyObjects = bodyObjects;
    }

    /// <summary>
    /// Called from the InitializePlayer postfix: the game was about to Destroy these local-only objects
    /// (body, legs, arms, IK); we saved them, re-activate them and turn on the allowed components.
    /// </summary>
    internal static void Build(Player player, List<GameObject> bodyObjects)
    {
        try
        {
            foreach (var go in bodyObjects)
                if (go != null)
                    go.SetActive(true);

            EnableAllowedComponents(player);

            player.Skin.InitializeOther();
            player.Body.ToggleOldModel(player._isBean.Value);

            var other = player.Other;
            if (other._col != null) other._col.enabled = false; // no collider on your own body
            if (player.Body._nameTextCanvasHolder != null) player.Body._nameTextCanvasHolder.gameObject.SetActive(false);
            if (other._otherPlayerCanvasHolder != null) other._otherPlayerCanvasHolder.gameObject.SetActive(false);

            HandleHead(player);

            // Drive the saved body every frame (yaw follows the camera/feet anchor, boat aware).
            _driver = player.GetComponent<LocalBodyDriver>();
            if (_driver == null) _driver = player.gameObject.AddComponent<LocalBodyDriver>();
            _driver.enabled = true;
            _driver.Init(player);

            // BO2-style VR arms: the moment the body exists, point the game's FABRIK solvers at the
            // controller-driven hand bones and give the elbows proper pole targets so they bend naturally
            // (down/back) instead of folding weirdly or stretching to reach.
            ArmRig.Setup(player);

            _player = player;
            _bodyObjects = bodyObjects;
            Active = true;
        }
        catch
        {
            Active = false;
        }
    }

    internal static void SetVisible(bool visible)
    {
        if (!Active || _bodyObjects == null) return;
        foreach (var go in _bodyObjects)
            if (go != null) go.SetActive(visible);
        if (_player != null && _player.Other._transform != null)
            _player.Other._transform.gameObject.SetActive(visible);
        if (visible && _player != null)
        {
            try { _player.Body.Reset(); _player.Legs.Reset(); } catch { }
            try { _player.GetComponent<LocalBodyDriver>()?.Sync(); } catch { }
            // Full IK re-wire on every re-show (respawn, revive, boat exit): the game can re-enable
            // stretch / re-route the arm solvers while the body was hidden (ragdoll, seat model, death
            // cam), so re-assert the BO2-style config (stretch off, pole targets, controller targets)
            // or the arms come back bending wrong / reaching.
            try { ArmRig.Setup(_player); } catch { }
        }
    }

    internal static void Clear()
    {
        Active = false;
        _player = null;
        _bodyObjects = null;
    }

    /// <summary>
    /// Apply the Body Mode setting LIVE (Full Body IK <-> Hands Only), no respawn needed. Hands Only
    /// hides the restored body and stops the driver/IK; switching back rebuilds from the saved objects.
    /// The saved objects are refreshed on every spawn (Store), so this always has something usable —
    /// the old "stays in hands only" bug was stale refs to a destroyed player after a respawn/scene
    /// change while Hands Only was active (the save was mode-gated, so the game destroyed the body and
    /// ApplyBodyMode silently no-opped).
    /// </summary>
    internal static void ApplyBodyMode()
    {
        bool fullBody = VRConfig.BodyMode.Value == BodyMode.FullBodyIK;
        if (!fullBody)
        {
            SetVisible(false);
            Active = false; // stops LocalBodyDriver + ArmRig (they gate on Active)
            if (_driver != null) _driver.enabled = false;
            return;
        }

        // Are the saved body objects still alive (not destroyed by a respawn/scene change)?
        bool objectsAlive = _bodyObjects != null;
        if (objectsAlive)
            foreach (var go in _bodyObjects)
                if (go == null) { objectsAlive = false; break; }
        bool playerAlive = _player != null;

        if (Active && objectsAlive)
        {
            // Body already restored — just re-show it (covers the boat-hide and any deactivation).
            SetVisible(true);
        }
        else if (objectsAlive && playerAlive)
        {
            // Saved objects are intact but the body is hidden — rebuild them in place.
            try { Build(_player, _bodyObjects); }
            catch { }
        }
        else
        {
            // Nothing usable (shouldn't happen now that Store runs on every spawn) — recover from the
            // CURRENT local player if one exists.
            var local = Player.LocalPlayer;
            if (local != null && local._otherObjects != null && local._otherObjects.Count > 0)
            {
                try { Build(local, local._otherObjects); }
                catch { }
            }
        }
    }

    /// <summary>
    /// The camera sits inside your skull, so the real head mesh is folded to zero scale; its shadow is
    /// preserved via a shadow-only clone so your shadow keeps a head (SeaLegs pattern).
    /// </summary>
    private static void HandleHead(Player player)
    {
        Transform head = null;
        try { head = player.Body.Head; } catch { }
        if (head == null) return;

        var parent = player.Other._transform != null ? player.Other._transform : head.root;

        // Shadow-only clone of the head (renders shadows only, follows the real head). Created ONCE per
        // body build: re-building (Body toggle) must not stack extra shadow heads.
        try
        {
            if (parent.Find("VRShadowHead") == null)
            {
                CreateShadowHead(head, parent);
            }
        }
        catch { }

        head.localScale = Vector3.zero; // hide the real head (camera is inside it)
    }

    private static void CreateShadowHead(Transform head, Transform parent)
    {
        try
        {
            var inst = UnityEngine.Object.Instantiate(head.gameObject, head.parent).transform;
            inst.name = "VRShadowHead";
            inst.localPosition = head.localPosition;
            inst.localRotation = head.localRotation;
            inst.localScale = Vector3.one;

            var dict = new Dictionary<Transform, Transform>();
            var src = head.GetComponentsInChildren<Transform>(true);
            var dst = inst.GetComponentsInChildren<Transform>(true);
            if (src.Length == dst.Length)
            {
                for (int i = 0; i < src.Length; i++) dict[src[i]] = dst[i];
                foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
                    if (r != null) r.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                foreach (var smr in parent.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (smr == null || smr.sharedMesh == null || smr.bones == null || !RidesHead(smr.bones, dict)) continue;
                    var clone = UnityEngine.Object.Instantiate(smr.gameObject, smr.transform.parent).GetComponent<SkinnedMeshRenderer>();
                    if (clone == null) continue;
                    clone.name = smr.name + " (shadow head)";
                    var bones = clone.bones;
                    for (int k = 0; k < bones.Length; k++)
                        if (bones[k] != null && dict.TryGetValue(bones[k], out var v))
                            bones[k] = v;
                    clone.bones = bones;
                    if (clone.rootBone != null && dict.TryGetValue(clone.rootBone, out var rb))
                        clone.rootBone = rb;
                    clone.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                }
            }
            inst.gameObject.AddComponent<ShadowHeadFollow>().Follow(head);
        }
        catch { }
    }

    private static bool RidesHead(Transform[] bones, Dictionary<Transform, Transform> map)
    {
        foreach (var b in bones)
            if (b != null && map.ContainsKey(b)) return true;
        return false;
    }

    private static void EnableAllowedComponents(Player player)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "OtherPlayer", "PlayerBody", "PlayerLegs", "PlayerArms", "IK",
        };
        foreach (var mb in player._disableWhenLocal)
        {
            if (mb == null) continue;
            if (allowed.Contains(mb.GetType().Name)) mb.enabled = true;
        }
    }
}

/// <summary>Follows the real head so the shadow-only head clone stays in place.</summary>
internal sealed class ShadowHeadFollow : MonoBehaviour
{
    private Transform _head;

    internal void Follow(Transform head) => _head = head;

    private void LateUpdate()
    {
        if (_head != null)
        {
            transform.localPosition = _head.localPosition;
            transform.localRotation = _head.localRotation;
        }
    }
}
