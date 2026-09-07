using HowToFishVR.Input;
using HowToFishVR.Patches;
using HowToFishVR.VR;
using UnityEngine;
using UnityEngine.Rendering;

namespace HowToFishVR.UI;

/// <summary>A short-lived segmented guide for empty-hand punches, brass knuckles, and the knife. It visualizes the
/// exact calibrated right-hand origin and assisted target used by <see cref="PunchPatches"/> without
/// adding another permanent laser to normal gameplay.</summary>
[DefaultExecutionOrder(-900)]
public sealed class VRPunchAim : MonoBehaviour
{
    public static VRPunchAim Instance { get; private set; }

    private const int SegmentCount = 18;
    private const float DashFraction = 0.58f;
    private const float TriggerThreshold = 0.25f;
    private const float VisibleSeconds = 5f;
    private const float RedHoldSeconds = 1f;

    private static readonly Color RestColor = new(0.30f, 0.60f, 1f, 0.92f);
    private static readonly Color ActiveColor = new(1f, 0.20f, 0.16f, 0.96f);

    private readonly LineRenderer[] _segments = new LineRenderer[SegmentCount];
    private GameObject _visualRoot;
    private Material _material;
    private float _visibleUntil;
    private float _redUntil;
    private bool _triggerHeld;

    public static void Create()
    {
        if (Instance != null) return;
        var go = new GameObject("HowToFishVR Punch Aim");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<VRPunchAim>();
        Instance.Build();
    }

    private void Build()
    {
        _visualRoot = new GameObject("SegmentedRightHandPunchGuide");
        _visualRoot.layer = 5;
        _visualRoot.transform.SetParent(transform, false);

        // Sprites/Default honors LineRenderer vertex tint. URP/Unlit is retained only as a fallback
        // because its default white base color otherwise masks the red/blue state colors.
        var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit");
        if (shader != null)
        {
            _material = new Material(shader) { renderQueue = 4000 };
        }

        for (int i = 0; i < SegmentCount; i++)
        {
            var segmentObject = new GameObject($"PunchGuideSegment{i:00}");
            segmentObject.layer = 5;
            segmentObject.transform.SetParent(_visualRoot.transform, false);
            var line = segmentObject.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.startWidth = 0.009f;
            line.endWidth = 0.006f;
            line.numCapVertices = 4;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            if (_material != null) line.sharedMaterial = _material;
            _segments[i] = line;
        }

        _visualRoot.SetActive(false);
        Application.onBeforeRender += DrawLatestPose;
    }

    private void OnDestroy()
    {
        Application.onBeforeRender -= DrawLatestPose;
        if (_material != null) Destroy(_material);
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        bool triggerNow = ReadRightTrigger() >= TriggerThreshold;
        bool eligible = CanShowForCurrentState(out _);
        bool guideEnabled = true;
        try { guideEnabled = VRConfig.MeleeAimGuide == null || VRConfig.MeleeAimGuide.Value; } catch { }
        if (!eligible || !guideEnabled)
        {
            _visibleUntil = 0f;
            _redUntil = 0f;
            _triggerHeld = triggerNow;
            SetVisible(false);
            return;
        }

        float now = Time.unscaledTime;
        if (triggerNow)
        {
            _visibleUntil = now + VisibleSeconds;
            if (!_triggerHeld) _redUntil = now + RedHoldSeconds;
        }
        else if (_triggerHeld)
        {
            // Keep the active color for one calm second after release, then settle to blue.
            _redUntil = Mathf.Max(_redUntil, now + RedHoldSeconds);
        }
        _triggerHeld = triggerNow;

        bool visible = triggerNow || now < _visibleUntil;
        SetVisible(visible);
        if (visible) DrawLatestPose();
    }

    private void DrawLatestPose()
    {
        if (_visualRoot == null || !_visualRoot.activeSelf) return;
        if (!CanShowForCurrentState(out _) ||
            !PunchPatches.TryGetRightHandAim(out Vector3 origin, out Vector3 direction))
        {
            SetVisible(false);
            return;
        }

        Vector3 end = origin + direction * PunchPatches.MinimumVRPunchRange;
        // Reuse the exact target already selected by input-time aim assist. This makes the guide visibly
        // bend to the assisted creature while retaining the performance fix: no per-frame creature scan.
        if (PunchPatches.TryGetRecentAssistedAimPoint(out Vector3 assistedPoint))
        {
            end = assistedPoint;
        }
        else try
        {
            if (Physics.Raycast(origin, direction, out RaycastHit levelHit,
                    PunchPatches.MinimumVRPunchRange, GameInfo.LevelLayer,
                    QueryTriggerInteraction.Ignore))
                end = levelHit.point;
        }
        catch { }

        Color color = _triggerHeld || Time.unscaledTime < _redUntil ? ActiveColor : RestColor;
        DrawSegments(origin, end, color);
    }

    private void DrawSegments(Vector3 origin, Vector3 end, Color color)
    {
        Vector3 delta = end - origin;
        float length = delta.magnitude;
        if (length < 0.02f)
        {
            SetVisible(false);
            return;
        }

        bool materialTinted = false;
        if (_material != null)
        {
            if (_material.HasProperty("_Color"))
            {
                _material.SetColor("_Color", color);
                materialTinted = true;
            }
            if (_material.HasProperty("_BaseColor"))
            {
                _material.SetColor("_BaseColor", color);
                materialTinted = true;
            }
        }

        Color startColor = materialTinted ? Color.white : color;
        Color endColor = materialTinted
            ? new Color(1f, 1f, 1f, 0.72f)
            : new Color(color.r, color.g, color.b, color.a * 0.72f);
        for (int i = 0; i < SegmentCount; i++)
        {
            float startT = i / (float)SegmentCount;
            float endT = (i + DashFraction) / SegmentCount;
            var line = _segments[i];
            if (line == null) continue;
            line.startColor = startColor;
            line.endColor = endColor;
            line.SetPosition(0, origin + delta * startT);
            line.SetPosition(1, origin + delta * Mathf.Min(endT, 1f));
        }
    }

    private static float ReadRightTrigger()
    {
        try
        {
            if (VRActions.Instance != null) return VRActions.Instance.RightTrigger.ReadValue<float>();
        }
        catch { }
        try
        {
            var device = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.RightHand);
            if (device.isValid && device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.trigger, out float value))
                return value;
        }
        catch { }
        return 0f;
    }

    private static bool CanShowForCurrentState(out Player player)
    {
        player = null;
        if (!Plugin.HeadsetActive) return false;
        try { if (VRLaser.LaserActive()) return false; } catch { }
        try { if (MainMenuManager.IsInMenu) return false; } catch { }
        try { if (PauseManager.IsPaused) return false; } catch { }
        try { if (VRSettingsPanel.IsOpen) return false; } catch { }
        try { if (VRKeyboard.Instance != null && VRKeyboard.Instance.IsOpen) return false; } catch { }

        try
        {
            player = Player.LocalPlayer;
            if (player == null || player.Dying == null || player.Dying.IsDead) return false;
            var held = player.Holding != null ? player.Holding.HeldItem : null;
            if (held == null) return true;
            var tool = player.ToolMovement != null ? player.ToolMovement.CurrentTool : null;
            return tool is Melee melee && PunchPatches.IsHandAimedMelee(melee);
        }
        catch
        {
            player = null;
            return false;
        }
    }

    private void SetVisible(bool visible)
    {
        if (_visualRoot != null && _visualRoot.activeSelf != visible)
            _visualRoot.SetActive(visible);
    }
}
