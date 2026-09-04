using HowToFishVR.Input;
using UnityEngine;
using UnityEngine.UI;

namespace HowToFishVR.VR;

/// <summary>
/// Guided calibration. Triggered only by the F3 calibrate key. Shows an in-VR prompt to stand in a
/// T-pose; once the pose is held it counts down 3-2-1 and captures the standing height + recenters
/// the full tracking space (head reference, roomscale origin, full-body IK).
/// </summary>
public class VRCalibration : MonoBehaviour
{
    public static VRCalibration Instance { get; private set; }

    private enum State { Idle, Prompt, Countdown, Done }
    private bool _f3WasDown;
    private State _state = State.Idle;
    private float _timer;
    private int _count;

    private Canvas _canvas;
    private Text _text;

    public static void Create()
    {
        if (Instance != null) return;
        var go = new GameObject("HowToFishVR Calibration");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<VRCalibration>();
        Instance.BuildUI();
    }

    public void Begin()
    {
        if (_state != State.Idle) return;
        _state = State.Prompt;
        _timer = 0f;
    }

    private void BuildUI()
    {
        var canvasGo = new GameObject("Calibration Canvas");
        canvasGo.transform.SetParent(transform, false);
        canvasGo.layer = 5; // the layer the XR camera renders (default layer 0 was invisible in VR)
        _canvas = canvasGo.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.WorldSpace;
        canvasGo.AddComponent<CanvasScaler>();
        var rt = (RectTransform)_canvas.transform;
        rt.sizeDelta = new Vector2(800, 300);
        rt.localScale = Vector3.one * 0.0015f;

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(canvasGo.transform, false);
        textGo.layer = 5;
        _text = textGo.AddComponent<Text>();
        _text.font = FindFont();
        _text.fontSize = 48;
        _text.alignment = TextAnchor.MiddleCenter;
        _text.color = Color.white;
        _text.horizontalOverflow = HorizontalWrapMode.Wrap;
        var trt = (RectTransform)_text.transform;
        trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;

        canvasGo.SetActive(false);
    }

    private static Font FindFont()
    {
        try { var f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); if (f != null) return f; } catch { }
        try { var f = Font.CreateDynamicFontFromOSFont("Arial", 48); if (f != null) return f; } catch { }
        try { var f = Font.CreateDynamicFontFromOSFont("Segoe UI", 48); if (f != null) return f; } catch { }
        return null;
    }

    private void Update()
    {
        if (!Plugin.VREnabled) return;
        // Robust F3 detection: the config shortcut first, with a direct key fallback.
        bool f3Down = false;
        try { f3Down = VRConfig.HeightCalibrateKey.Value.IsDown(); } catch { }
        try { if (UnityEngine.Input.GetKey(KeyCode.F3)) f3Down = true; } catch { }
        if (f3Down && !_f3WasDown)
        {
            if (_state == State.Idle) Begin();
        }
        _f3WasDown = f3Down;
        var rig = VRRig.Instance;
        if (rig == null || rig.Head == null) return;

        bool show = _state != State.Idle;
        if (_canvas != null && _canvas.gameObject.activeSelf != show)
            _canvas.gameObject.SetActive(show);
        if (!show) return;

        // Float the panel in front of the head.
        var head = rig.Head;
        var flat = head.forward; flat.y = 0f;
        if (flat.sqrMagnitude > 1e-4f)
        {
            flat.Normalize();
            _canvas.transform.position = head.position + flat * 1.2f;
            _canvas.transform.rotation = Quaternion.LookRotation(flat, Vector3.up);
        }

        switch (_state)
        {
            case State.Prompt:
                _text.text = "Stand up straight and hold a T-pose\n(arms straight out to the sides)";
                if (IsTPose(rig))
                {
                    _timer += Time.deltaTime;
                    if (_timer >= 0.5f) { _state = State.Countdown; _count = 3; _timer = 0f; }
                }
                else _timer = 0f;
                break;

            case State.Countdown:
                if (!IsTPose(rig)) { _state = State.Prompt; _timer = 0f; break; }
                _text.text = $"Hold still...\n{_count}";
                _timer += Time.deltaTime;
                if (_timer >= 1f)
                {
                    _timer = 0f;
                    _count--;
                    if (_count <= 0)
                    {
                        rig.CalibrateNow();
                        _state = State.Done;
                        _text.text = "Calibrated!";
                        Pulse();
                    }
                }
                break;

            case State.Done:
                _timer += Time.deltaTime;
                if (_timer >= 1.5f) { _state = State.Idle; _timer = 0f; }
                break;
        }
    }

    /// <summary>Short haptic pulse on both controllers so the user FEELS the calibration land.</summary>
    private static void Pulse()
    {
        try
        {
            var l = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.LeftHand);
            var r = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.RightHand);
            if (l.isValid) l.SendHapticImpulse(0u, 0.6f, 0.25f);
            if (r.isValid) r.SendHapticImpulse(0u, 0.6f, 0.25f);
        }
        catch { }
    }

    /// <summary>Both hands near head height and extended out to opposite sides.</summary>
    private static bool IsTPose(VRRig rig)
    {
        var head = rig.Head.position;
        var l = rig.LeftHand.position;
        var r = rig.RightHand.position;

        bool heightOk = Mathf.Abs(l.y - head.y) < 0.45f && Mathf.Abs(r.y - head.y) < 0.45f;

        var lh = new Vector3(l.x - head.x, 0f, l.z - head.z);
        var rh = new Vector3(r.x - head.x, 0f, r.z - head.z);
        bool extended = lh.magnitude > 0.35f && rh.magnitude > 0.35f;
        bool opposite = Vector3.Dot(lh.normalized, rh.normalized) < -0.3f;

        return heightOk && extended && opposite;
    }
}
