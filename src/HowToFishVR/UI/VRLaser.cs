using UnityEngine;
using UnityEngine.EventSystems;

namespace HowToFishVR.UI;

/// <summary>Visible controller lasers. UI events are dispatched by the plane-locked VR pointer.</summary>
[DefaultExecutionOrder(-1000)]
public class VRLaser : MonoBehaviour
{
    public static VRLaser Instance { get; private set; }

    private sealed class Ray
    {
        public GameObject Go;
        public LineRenderer Line;
        public bool Left;
    }

    private Ray _right;
    private static EventSystem _configuredEventSystem;
    private const float Length = 5f;
    internal static readonly Quaternion PointerTilt = Quaternion.Euler(45f, 0f, 0f);

    public static void Create()
    {
        if (Instance != null) return;
        var go = new GameObject("HowToFishVR Laser");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<VRLaser>();
        Instance.Init();
    }

    private void Init()
    {
        _right = BuildRay(false);
        Application.onBeforeRender += PoseAndDraw;
    }

    private void OnDestroy()
    {
        Application.onBeforeRender -= PoseAndDraw;
    }

    private static VRPointerInputModule EnsureModule()
    {
        var es = EventSystem.current;
        if (es == null) return null;
        foreach (var m in es.GetComponents<BaseInputModule>())
            if (!(m is VRPointerInputModule)) m.enabled = false;
        var module = es.GetComponent<VRPointerInputModule>();
        if (module == null) module = es.gameObject.AddComponent<VRPointerInputModule>();
        module.enabled = true;
        if (_configuredEventSystem != es)
        {
            _configuredEventSystem = es;
        }
        return module;
    }

    private Ray BuildRay(bool left)
    {
        var go = new GameObject(left ? "LeftRay" : "RightRay");
        go.layer = 5;
        go.transform.SetParent(transform, false);
        var line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.positionCount = 2;
        line.widthMultiplier = 0.006f;
        line.numCapVertices = 2;
        var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
        if (shader != null)
        {
            line.material = new Material(shader);
            line.material.renderQueue = 4000;
        }
        var col = left ? new Color(1f, 0.72f, 0.2f) : new Color(0.25f, 0.85f, 1f);
        line.startColor = col;
        line.endColor = new Color(col.r, col.g, col.b, 0.3f);
        var result = new Ray { Go = go, Line = line, Left = left };
        go.SetActive(false);
        return result;
    }

    internal static bool LaserActive()
    {
        try { if (MainMenuManager.IsInMenu) return true; } catch { }
        try { if (PauseManager.IsPaused) return true; } catch { }
        try { if (VRSettingsPanel.IsOpen) return true; } catch { }
        return false;
    }

    private void Update()
    {
        var module = EnsureModule();
        bool active = LaserActive();
        SetRayActive(_right, active);
        if (module != null) module.SetLaserActive(active);
        if (!active) return;
        PoseTransform(_right);
    }

    private static void SetRayActive(Ray r, bool active)
    {
        if (r?.Go != null && r.Go.activeSelf != active) r.Go.SetActive(active);
    }

    private void PoseAndDraw()
    {
        bool show = LaserActive();
        if (_right?.Line != null) _right.Line.enabled = show;
        if (!show) return;
        Draw(_right);
    }

    private static void PoseTransform(Ray r)
    {
        if (r?.Go == null) return;
        var rig = VR.VRRig.Instance;
        if (rig == null) return;
        var hand = r.Left ? rig.LeftHand : rig.RightHand;
        if (hand != null) r.Go.transform.SetPositionAndRotation(hand.position, hand.rotation * PointerTilt);
    }

    private static void Draw(Ray r)
    {
        if (r?.Go == null || r.Line == null) return;
        PoseTransform(r);
        Vector3 origin = r.Go.transform.position;
        var pointer = VRPointerInputModule.Instance;
        pointer?.RefreshRaycastForRender();
        Vector3 end = origin + r.Go.transform.forward * Length;
        if (pointer != null && pointer.HasHit) end = pointer.HitPoint;
        r.Line.SetPosition(0, origin);
        // Use the exact GraphicRaycaster hit, not merely its distance projected down a separately
        // sampled controller direction. This makes the visible endpoint and clickable pixel identical.
        r.Line.SetPosition(1, end);
    }
}
