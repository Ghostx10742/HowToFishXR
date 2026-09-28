using HarmonyLib;
using UnityEngine;

namespace HowToFishVR.VR;

/// <summary>
/// One reflection for the entire locally held prefab. Keeping the original hierarchy means
/// Animator paths, meshes, attachments, grips, and the reel all receive the same reflection.
/// No item IDs, alternate grip selection, or per-item translation corrections are needed.
/// Item/VR network messages continue to contain their existing positions and rotations only.
/// </summary>
internal static class HeldItemMirror
{
    private static Item _item;
    private static Vector3 _originalScale;

    internal static bool IsMirrored(Item item) => item != null && item == _item;
    internal static bool Contains(Transform node) => _item != null && node != null &&
        (node == _item.transform || node.IsChildOf(_item.transform));

    internal static Vector3 Reflect(Vector3 v) => new Vector3(-v.x, v.y, v.z);
    internal static Quaternion Reflect(Quaternion q) => new Quaternion(q.x, -q.y, -q.z, q.w);

    internal static void Refresh()
    {
        Item next = null;
        if (Plugin.HeadsetActive && VRConfig.IsLeftHanded && !Boat.IsDrivingLocally)
        {
            var player = Player.LocalPlayer;
            if (player != null && player.Owner != null && player.Owner.IsLocalClient && player.Holding != null)
                next = player.Holding.HeldItem;
            if (next != null && !next.gameObject.activeInHierarchy) next = null;
        }
        SetItem(next);
    }

    private static void SetItem(Item next)
    {
        if (_item != next)
        {
            Restore();
            if (next == null) return;
            _item = next;
            _originalScale = next.transform.localScale;
        }
        if (_item != null)
            _item.transform.localScale = new Vector3(-_originalScale.x, _originalScale.y, _originalScale.z);
    }

    internal static void Restore(Item item = null)
    {
        if (item != null && item != _item) return;
        if (_item != null) _item.transform.localScale = _originalScale;
        _item = null;
    }

    // Pose solvers use rotation-only offsets, not InverseTransformPoint, which would undo
    // the very scale reflection the model and grip have just received.
    internal static Vector3 RootOffset(Item item, Vector3 worldPoint) =>
        Quaternion.Inverse(item.transform.rotation) * (worldPoint - item.transform.position);

    // Transform.rotation ignores negative scale. Read the reflected basis from the matrix.
    // The destination anatomical hand mirrors the source skeleton across hand-local Z.
    internal static Quaternion HandRotation(Transform parent, Quaternion localRotation)
    {
        if (!Contains(parent)) return parent.rotation * localRotation;
        return Quaternion.LookRotation(
            -parent.TransformVector(localRotation * Vector3.forward),
            parent.TransformVector(localRotation * Vector3.up));
    }

    internal static Quaternion HandRotation(Transform marker) => HandRotation(marker, Quaternion.identity);

    // Independently controller-driven parts (knife, knuckles, lighter) still live under the
    // reflected prefab. Solve their local rotation through that reflected parent matrix.
    internal static void SetPartPose(Transform node, Vector3 position, Quaternion reflectedRotation)
    {
        node.position = position;
        if (!Contains(node) || node.parent == null)
        {
            node.rotation = reflectedRotation;
            return;
        }
        node.localRotation = Quaternion.LookRotation(
            node.parent.InverseTransformVector(reflectedRotation * Vector3.forward),
            node.parent.InverseTransformVector(reflectedRotation * Vector3.up));
    }

    internal static void PlacePartFromHand(Item item, Transform node, Transform controller, bool authoredLeft,
        Vector3 fromHandPosition, Quaternion fromHandRotation)
    {
        bool mirror = IsMirrored(item);
        Quaternion authoredGrip = authoredLeft
            ? Patches.PlayerHandsPatches.LeftGripOffset : Patches.PlayerHandsPatches.RightGripOffset;
        Vector3 offset = authoredGrip * fromHandPosition;
        Quaternion rotation = authoredGrip * fromHandRotation;
        if (mirror)
        {
            offset = Reflect(offset);
            rotation = Reflect(rotation);
        }
        Vector3 anchor = Patches.PlayerHandsPatches.CalibratedHandPosition(controller, authoredLeft ^ mirror);
        SetPartPose(node, anchor + controller.rotation * offset, controller.rotation * rotation);
    }

    internal static void CapturePartPose(Item item, Transform part, Vector3 originalPosition,
        Quaternion originalRotation, HandTransforms grip, out Vector3 fromHandPosition, out Quaternion fromHandRotation)
    {
        Vector3 scale = item.transform.localScale;
        try
        {
            if (IsMirrored(item)) item.transform.localScale = _originalScale;
            Vector3 handPosition = grip.Parent.TransformPoint(grip.HandPos);
            Quaternion handRotation = grip.Parent.rotation * grip.HandRot;
            fromHandPosition = Quaternion.Inverse(handRotation) *
                (part.parent.TransformPoint(originalPosition) - handPosition);
            fromHandRotation = Quaternion.Inverse(handRotation) * part.parent.rotation * originalRotation;
        }
        finally { item.transform.localScale = scale; }
    }
}

[DefaultExecutionOrder(-1000)]
internal sealed class HeldItemMirrorLifecycle : MonoBehaviour
{
    private void Update() => HeldItemMirror.Refresh();
    private void FixedUpdate() => HeldItemMirror.Refresh();
    private void LateUpdate() => HeldItemMirror.Refresh();
    private void OnDisable() => HeldItemMirror.Restore();
    private void OnDestroy() => HeldItemMirror.Restore();
}

// Restore before the game detaches, inventories, transfers, or destroys an item. This also
// prevents reflected scale from being reused when a different player picks up the object.
[HarmonyPatch(typeof(Item))]
internal static class HeldItemMirrorReleasePatches
{
    [HarmonyTargetMethods]
    private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> Methods()
    {
        yield return AccessTools.Method(typeof(Item), "Drop");
        yield return AccessTools.Method(typeof(Item), "PickUp");
        yield return AccessTools.Method(typeof(Item), "PutInInventory");
        yield return AccessTools.Method(typeof(Item), "DestroyItem");
    }

    [HarmonyPrefix]
    private static void Release(Item __instance) => HeldItemMirror.Restore(__instance);
}
