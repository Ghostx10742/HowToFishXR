using System;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

internal static class MirroredPhysicsTests
{
    internal static void Run(Action<bool, string> check)
    {
        var scene = SceneManager.CreateScene("Isolated mirror physics test", new CreateSceneParameters(LocalPhysicsMode.Physics3D));
        try
        {
            var prefabs = Resources.LoadAll<Item>("Items").Where(p => p.GetComponentsInChildren<Joint>(true).Length > 0).Take(6).ToArray();
            check(prefabs.Length > 0, "found articulated item prefabs for physics mirror check");
            foreach (var prefab in prefabs)
            {
                var container = new GameObject("physics test inactive parent");
                container.SetActive(false);
                SceneManager.MoveGameObjectToScene(container, scene);
                var normal = CreatePhysicsOnly(prefab.gameObject, container.transform, false);
                var mirrored = CreatePhysicsOnly(prefab.gameObject, container.transform, true);
                var normalBodies = normal.GetComponentsInChildren<Rigidbody>(true);
                var mirroredBodies = mirrored.GetComponentsInChildren<Rigidbody>(true);
                container.SetActive(true);
                for (int i = 0; i < normalBodies.Length; i++)
                {
                    normalBodies[i].WakeUp();
                    mirroredBodies[i].WakeUp();
                    if (normalBodies[i].transform == normal.transform) continue;
                    normalBodies[i].linearVelocity = new Vector3(0.2f, 0.1f, -0.15f);
                    mirroredBodies[i].linearVelocity = new Vector3(-0.2f, 0.1f, -0.15f);
                }
                var physics = scene.GetPhysicsScene();
                for (int step = 0; step < 30; step++) physics.Simulate(1f / 60f);
                float error = 0f;
                for (int i = 0; i < normalBodies.Length; i++)
                {
                    Vector3 a = Quaternion.Inverse(normal.transform.rotation) * (normalBodies[i].position - normal.transform.position);
                    Vector3 b = Quaternion.Inverse(mirrored.transform.rotation) * (mirroredBodies[i].position - mirrored.transform.position);
                    error = Mathf.Max(error, Vector3.Distance(new Vector3(-a.x, a.y, a.z), b));
                }
                check(error < 0.01f, prefab.name + ": articulated physics reflection error=" + error.ToString("F4") + "m");
                UnityEngine.Object.DestroyImmediate(container);
            }
        }
        finally { SceneManager.UnloadSceneAsync(scene); }
    }

    private static GameObject CreatePhysicsOnly(GameObject prefab, Transform parent, bool mirror)
    {
        var clone = UnityEngine.Object.Instantiate(prefab, parent);
        // Strip gameplay/network scripts before activating. The isolated scene contains only
        // the prefab's physical bodies/joints and cannot interact with the user's world or save.
        foreach (var script in clone.GetComponentsInChildren<MonoBehaviour>(true))
            if (script.GetType().Name != "NetworkObject") UnityEngine.Object.DestroyImmediate(script);
        foreach (var script in clone.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(script);
        foreach (var renderer in clone.GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;
        foreach (var collider in clone.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
        foreach (var animator in clone.GetComponentsInChildren<Animator>(true)) animator.enabled = false;
        foreach (var animation in clone.GetComponentsInChildren<Animation>(true)) animation.enabled = false;
        foreach (var audio in clone.GetComponentsInChildren<AudioSource>(true)) audio.enabled = false;
        foreach (var rb in clone.GetComponentsInChildren<Rigidbody>(true))
        {
            rb.isKinematic = false;
            rb.useGravity = false;
        }
        var rootBody = clone.GetComponent<Rigidbody>();
        if (rootBody != null) rootBody.constraints = RigidbodyConstraints.FreezeAll;
        clone.transform.SetPositionAndRotation(new Vector3(mirror ? 10f : 0f, 100f, 0f), Quaternion.identity);
        if (mirror)
        {
            var scale = clone.transform.localScale;
            clone.transform.localScale = new Vector3(-scale.x, scale.y, scale.z);
        }
        return clone;
    }
}
