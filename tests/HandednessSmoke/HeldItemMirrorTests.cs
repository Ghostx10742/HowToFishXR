using System;
using System.Linq;
using System.Reflection;
using HowToFishVR;
using UnityEngine;

internal static class HeldItemMirrorTests
{
    private static readonly Type Mirror = typeof(Plugin).Assembly.GetType("HowToFishVR.VR.HeldItemMirror");
    private static MethodInfo Method(string name, params Type[] types) => Mirror.GetMethod(name,
        BindingFlags.Static | BindingFlags.NonPublic, null, types, null);
    private static Vector3 Reflect(Vector3 v) => new Vector3(-v.x, v.y, v.z);

    internal static void Run(Action<bool, string> check)
    {
        var set = Method("SetItem", typeof(Item));
        var restore = Method("Restore", typeof(Item));
        var rootOffset = Method("RootOffset", typeof(Item), typeof(Vector3));
        var handRotation = Method("HandRotation", typeof(Transform));
        var poseRotation = Method("HandRotation", typeof(Transform), typeof(Quaternion));
        var direction = typeof(Plugin).Assembly.GetType("HowToFishVR.Patches.MirroredItemAimPatches")
            .GetMethod("Forward", BindingFlags.Static | BindingFlags.NonPublic);
        var prefabs = Resources.LoadAll<Item>("Items");
        check(prefabs.Length > 20, "loaded actual game item prefabs for full hierarchy reflection tests");
        var holder = new GameObject("Mirror tests (inactive; no gameplay or save)");
        holder.SetActive(false);
        int gripChecks = 0, animationChecks = 0;
        try
        {
            foreach (var prefab in prefabs)
            {
                var clone = UnityEngine.Object.Instantiate(prefab.gameObject, holder.transform);
                var item = clone.GetComponent<Item>();
                var root = item.transform;
                Vector3 originalScale = root.localScale;
                root.SetPositionAndRotation(new Vector3(2f, 3f, -4f), Quaternion.Euler(17f, 39f, -11f));
                var nodes = root.GetComponentsInChildren<Transform>(true);
                Vector3 probe = new Vector3(0.021f, -0.033f, 0.07f);
                Vector3[] before = nodes.Select(n => n.TransformPoint(probe)).ToArray();
                Vector3 origin = root.position;
                Quaternion rotation = root.rotation;
                try
                {
                    set.Invoke(null, new object[] { item });
                    set.Invoke(null, new object[] { item }); // must be idempotent, not toggle
                    bool reflected = true;
                    for (int i = 0; i < nodes.Length; i++)
                    {
                        Vector3 expected = origin + rotation * Reflect(Quaternion.Inverse(rotation) * (before[i] - origin));
                        if (Vector3.Distance(nodes[i].TransformPoint(probe), expected) > 0.0005f) reflected = false;
                    }
                    check(reflected, prefab.name + ": complete hierarchy reflected once");

                    var grip = item.HandModelRight;
                    if (grip != null)
                    {
                        Vector3 offset = (Vector3)rootOffset.Invoke(null, new object[] { item, grip.position });
                        Vector3 hand = new Vector3(-0.4f, 1.3f, 0.7f);
                        Quaternion aim = Quaternion.Euler(-30f, 51f, 13f);
                        root.SetPositionAndRotation(hand - aim * offset, aim);
                        check(Vector3.Distance(grip.position, hand) < 0.0005f,
                            prefab.name + ": original MAIN grip sits exactly at left hand");
                        Quaternion wrist = (Quaternion)handRotation.Invoke(null, new object[] { grip });
                        check(Vector3.Dot(wrist * Vector3.forward, -grip.TransformVector(Vector3.forward).normalized) > 0.999f &&
                            Vector3.Dot(wrist * Vector3.up, grip.TransformVector(Vector3.up).normalized) > 0.999f,
                            prefab.name + ": wrist basis follows reflected grip");
                        gripChecks++;
                    }
                    else if (item.HandTransformsRight != null && item.HandTransformsRight.Exists)
                    {
                        var pose = item.HandTransformsRight;
                        var parent = pose.Parent != null ? pose.Parent : root;
                        Vector3 gripPosition = parent.TransformPoint(pose.HandPos);
                        Vector3 offset = (Vector3)rootOffset.Invoke(null, new object[] { item, gripPosition });
                        Quaternion actualGrip = (Quaternion)poseRotation.Invoke(null, new object[] { parent, pose.HandRot });
                        Quaternion localGrip = Quaternion.Inverse(root.rotation) * actualGrip;
                        Vector3 hand = new Vector3(-0.4f, 1.3f, 0.7f);
                        Quaternion wrist = Quaternion.Euler(41f, -73f, 19f);
                        Quaternion target = wrist * Quaternion.Inverse(localGrip);
                        root.SetPositionAndRotation(hand - target * offset, target);
                        check(Vector3.Distance(parent.TransformPoint(pose.HandPos), hand) < 0.0005f &&
                            Quaternion.Angle((Quaternion)poseRotation.Invoke(null, new object[] { parent, pose.HandRot }), wrist) < 0.1f,
                            prefab.name + ": authored main-hand pose seats position AND rotation");
                        gripChecks++;
                    }
                    var muzzle = item.GetComponent<Weapon>()?.Attachments?.FirePoint;
                    if (muzzle != null)
                        check(Vector3.Dot((Vector3)direction.Invoke(null, new object[] { muzzle }),
                            muzzle.TransformVector(Vector3.forward).normalized) > 0.999f,
                            prefab.name + ": muzzle direction follows reflected barrel");

                    // Sample real legacy clips while inactive: no Awake/network/gameplay callbacks.
                    foreach (var animation in root.GetComponentsInChildren<Animation>(true))
                    {
                        var clips = animation.Cast<AnimationState>().Select(s => s.clip).Where(c => c != null).Take(2).ToArray();
                        foreach (var clip in clips)
                        {
                            restore.Invoke(null, new object[] { item });
                            clip.SampleAnimation(animation.gameObject, clip.length * 0.37f);
                            Vector3[] sampled = nodes.Select(n => n.TransformPoint(probe)).ToArray();
                            origin = root.position;
                            rotation = root.rotation;
                            set.Invoke(null, new object[] { item });
                            clip.SampleAnimation(animation.gameObject, clip.length * 0.37f);
                            set.Invoke(null, new object[] { item }); // restore mirror if clip writes root scale
                            bool mirroredAnimation = true;
                            for (int i = 0; i < nodes.Length; i++)
                            {
                                Vector3 expected = origin + rotation * Reflect(Quaternion.Inverse(rotation) * (sampled[i] - origin));
                                if (Vector3.Distance(nodes[i].TransformPoint(probe), expected) > 0.001f) mirroredAnimation = false;
                            }
                            check(mirroredAnimation, prefab.name + ": animation reflected (" + clip.name + ")");
                            animationChecks++;
                        }
                    }
                    // Exercise the actual Harmony release hook, not just the mirror helper.
                    // The inactive clone has no holder; vanilla Drop returns without gameplay.
                    item.Drop();
                    check(root.localScale.x >= 0f, prefab.name + ": Drop hook removes reflection");
                }
                finally
                {
                    restore.Invoke(null, new object[] { item });
                    check(Vector3.Distance(root.localScale, originalScale) < 0.0001f,
                        prefab.name + ": release restores original scale");
                    UnityEngine.Object.DestroyImmediate(clone);
                }
            }
            check(gripChecks >= 5, "validated multiple real tool grips");
            check(animationChecks >= 5, "validated real item animation clips");
        }
        finally
        {
            restore.Invoke(null, new object[] { null });
            UnityEngine.Object.DestroyImmediate(holder);
        }
    }
}
