using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR;

namespace HowToFishVR.UI;

/// <summary>
/// GraphicRaycaster pointer adapted from the user's MuckVR source. The event camera and pressed
/// canvas plane are frozen for a trigger hold, producing stable pixel deltas for all uGUI drags.
/// Only the right controller owns menu input. During a press, the event-camera/plane reference frame
/// follows the pressed canvas, preserving stable drag pixels even when the main-menu boat is moving.
/// </summary>
internal sealed class VRPointerInputModule : BaseInputModule
{
    internal static VRPointerInputModule Instance { get; private set; }
    internal bool HasHit { get; private set; }
    internal Vector3 HitPoint { get; private set; }

    private const float MaxDistance = 25f;
    private const float ClickForgivenessSeconds = 0.18f;
    private Transform _rayOrigin;
    private Camera _eventCamera;
    private PointerEventData _pointer;
    private GameObject _hover;
    private GameObject _lastValidHover;
    private float _lastValidHoverTime;
    private Plane _pressPlane;
    private Transform _pressReference;
    private Vector3 _pressCameraLocalPosition;
    private Quaternion _pressCameraLocalRotation;
    private Vector3 _pressPointLocal;
    private bool _cameraFrozen;
    private bool _laserActive;
    private bool _lastTrigger;
    private bool _rightHeld;

    protected override void Awake()
    {
        base.Awake();
        Instance = this;
    }

    protected override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    protected override void OnEnable()
    {
        base.OnEnable();
        foreach (var module in GetComponents<BaseInputModule>())
            if (module != this) module.enabled = false;
    }

    internal void SetLaserActive(bool active) => _laserActive = active;

    /// <summary>
    /// Refresh only the geometric raycast at render time. Input events remain Update-driven, but the
    /// visible laser is drawn from the latest on-before-render controller pose after the menu canvases
    /// have followed the sailing boat. Without this refresh the click ray used an earlier Update pose
    /// while the line used the newest pose, so its displayed endpoint did not match the actual target.
    /// </summary>
    internal void RefreshRaycastForRender()
    {
        if (!_laserActive || !EnsureSetup()) { HasHit = false; return; }
        PoseRay();
        UpdatePointerPosition(_lastTrigger);
        VRUIManager.ReassignLaserEventCamera(_eventCamera);
        DoRaycast();
    }

    private bool EnsureSetup()
    {
        if (_rayOrigin != null && _eventCamera != null && _pointer != null) return true;
        var originGo = new GameObject("VRPointerRayOrigin");
        originGo.transform.SetParent(transform, false);
        _rayOrigin = originGo.transform;
        var camGo = new GameObject("VRPointerEventCamera");
        camGo.transform.SetParent(_rayOrigin, false);
        _eventCamera = camGo.AddComponent<Camera>();
        _eventCamera.enabled = false;
        _eventCamera.clearFlags = CameraClearFlags.Nothing;
        _eventCamera.cullingMask = 1 << 5;
        _eventCamera.nearClipPlane = 0.01f;
        _eventCamera.farClipPlane = MaxDistance;
        _eventCamera.fieldOfView = 60f;
        _eventCamera.stereoTargetEye = StereoTargetEyeMask.None;
        _pointer = new PointerEventData(eventSystem) { pointerId = -120 };
        return true;
    }

    public override void Process()
    {
        if (!EnsureSetup()) return;
        bool rightDown = ReadTrigger(XRNode.RightHand, _rightHeld);
        if (!_laserActive)
        {
            if (_lastTrigger) ReleasePointer();
            ClearHover();
            HasHit = false;
            _lastTrigger = false;
            _rightHeld = rightDown;
            return;
        }

        _rightHeld = rightDown;
        bool trigger = rightDown;
        bool pressedThisFrame = trigger && !_lastTrigger;
        bool releasedThisFrame = !trigger && _lastTrigger;

        PoseRay();
        UpdatePointerPosition(trigger);
        VRUIManager.ReassignLaserEventCamera(_eventCamera);
        DoRaycast();
        UpdateHover(trigger);
        if (pressedThisFrame && _hover != null) PressPointer();
        if (trigger) DragPointer();
        if (releasedThisFrame) ReleasePointer();
        _lastTrigger = trigger;
    }

    private static bool ReadTrigger(XRNode node, bool wasHeld)
    {
        float value = 0f;
        try
        {
            var device = InputDevices.GetDeviceAtXRNode(node);
            if (device.isValid) device.TryGetFeatureValue(CommonUsages.trigger, out value);
        }
        catch { }
        return wasHeld ? value >= 0.18f : value >= 0.35f;
    }

    private void PoseRay()
    {
        var rig = VR.VRRig.Instance;
        if (rig == null) return;
        var hand = rig.RightHand;
        if (hand == null) return;
        _rayOrigin.SetPositionAndRotation(hand.position, hand.rotation * VRLaser.PointerTilt);
        if (!_cameraFrozen)
        {
            _eventCamera.transform.SetParent(_rayOrigin, false);
            _eventCamera.transform.localPosition = Vector3.zero;
            _eventCamera.transform.localRotation = Quaternion.identity;
        }
        else
        {
            UpdateFrozenReferenceFrame();
        }
    }

    private void UpdateFrozenReferenceFrame()
    {
        if (!_cameraFrozen || _pressReference == null || _eventCamera == null) return;
        // Carry the frozen projection frame with the canvas. This keeps the camera/press plane stable
        // relative to the UI while still allowing the sailing menu and headset to move normally.
        _eventCamera.transform.SetPositionAndRotation(
            _pressReference.TransformPoint(_pressCameraLocalPosition),
            _pressReference.rotation * _pressCameraLocalRotation);
        Vector3 planePoint = _pressReference.TransformPoint(_pressPointLocal);
        _pressPlane = new Plane(-_pressReference.forward, planePoint);
    }

    private void UpdatePointerPosition(bool pressed)
    {
        Vector2 next;
        if (pressed && _cameraFrozen)
        {
            var ray = new UnityEngine.Ray(_rayOrigin.position, _rayOrigin.forward);
            if (_pressPlane.Raycast(ray, out float enter) && enter <= MaxDistance)
            {
                Vector3 sp = _eventCamera.WorldToScreenPoint(ray.GetPoint(enter));
                next = new Vector2(sp.x, sp.y);
            }
            else next = _pointer.position;
        }
        else next = new Vector2(_eventCamera.pixelWidth * 0.5f, _eventCamera.pixelHeight * 0.5f);
        _pointer.delta = next - _pointer.position;
        _pointer.position = next;
    }

    private void DoRaycast()
    {
        eventSystem.RaycastAll(_pointer, m_RaycastResultCache);
        for (int i = m_RaycastResultCache.Count - 1; i >= 0; i--)
            if (!VRUIManager.IsLaserManaged(m_RaycastResultCache[i].gameObject))
                m_RaycastResultCache.RemoveAt(i);
        var result = FindFirstRaycast(m_RaycastResultCache);
        m_RaycastResultCache.Clear();
        _pointer.pointerCurrentRaycast = result;
        if (result.gameObject == null) { HasHit = false; return; }
        var t = result.gameObject.transform;
        var plane = new Plane(-t.forward, t.position);
        var ray = new UnityEngine.Ray(_rayOrigin.position, _rayOrigin.forward);
        HasHit = plane.Raycast(ray, out float enter) && enter <= MaxDistance;
        HitPoint = HasHit ? ray.GetPoint(enter) : t.position;
    }

    private void UpdateHover(bool pressed)
    {
        GameObject next = _pointer.pointerCurrentRaycast.gameObject;
        if (pressed && _pointer.pointerDrag != null && next == null) next = _hover;
        if (next != _hover)
        {
            HandlePointerExitAndEnter(_pointer, next);
            _hover = next;
        }
        if (_hover != null)
        {
            _lastValidHover = _hover;
            _lastValidHoverTime = Time.unscaledTime;
        }
    }

    private void PressPointer()
    {
        GameObject go = _hover;
        _pointer.eligibleForClick = true;
        _pointer.pressPosition = _pointer.position;
        _pointer.pointerPressRaycast = _pointer.pointerCurrentRaycast;
        _pointer.useDragThreshold = true;
        _pointer.button = PointerEventData.InputButton.Left;
        GameObject pressed = ExecuteEvents.ExecuteHierarchy(go, _pointer, ExecuteEvents.pointerDownHandler);
        if (pressed == null) pressed = ExecuteEvents.GetEventHandler<IPointerClickHandler>(go);
        _pointer.pointerPress = pressed;
        _pointer.rawPointerPress = go;
        _pointer.clickTime = Time.unscaledTime;
        _pointer.pointerDrag = ExecuteEvents.GetEventHandler<IDragHandler>(go);
        if (_pointer.pointerDrag != null)
            ExecuteEvents.Execute(_pointer.pointerDrag, _pointer, ExecuteEvents.initializePotentialDrag);
        var canvas = go.GetComponentInParent<Canvas>();
        Transform planeTransform = canvas != null ? canvas.transform : go.transform;
        _pressReference = planeTransform;
        _pressCameraLocalPosition = planeTransform.InverseTransformPoint(_eventCamera.transform.position);
        _pressCameraLocalRotation = Quaternion.Inverse(planeTransform.rotation) * _eventCamera.transform.rotation;
        _pressPointLocal = planeTransform.InverseTransformPoint(HasHit ? HitPoint : planeTransform.position);
        _eventCamera.transform.SetParent(null, true);
        _cameraFrozen = true;
        UpdateFrozenReferenceFrame();
        eventSystem.SetSelectedGameObject(pressed, _pointer);
    }

    private void DragPointer()
    {
        if (_pointer.pointerDrag == null) return;
        float threshold = eventSystem.pixelDragThreshold;
        if (!_pointer.dragging && (_pointer.pressPosition - _pointer.position).sqrMagnitude >= threshold * threshold)
        {
            ExecuteEvents.Execute(_pointer.pointerDrag, _pointer, ExecuteEvents.beginDragHandler);
            _pointer.dragging = true;
        }
        if (!_pointer.dragging) return;
        if (_pointer.pointerPress != _pointer.pointerDrag)
        {
            ExecuteEvents.Execute(_pointer.pointerPress, _pointer, ExecuteEvents.pointerUpHandler);
            _pointer.eligibleForClick = false;
            _pointer.pointerPress = null;
            _pointer.rawPointerPress = null;
        }
        ExecuteEvents.Execute(_pointer.pointerDrag, _pointer, ExecuteEvents.dragHandler);
    }

    private void ReleasePointer()
    {
        if (_pointer == null) return;
        GameObject current = _pointer.pointerCurrentRaycast.gameObject;
        ExecuteEvents.Execute(_pointer.pointerPress, _pointer, ExecuteEvents.pointerUpHandler);
        GameObject resolve = current;
        if (resolve == null && Time.unscaledTime - _lastValidHoverTime <= ClickForgivenessSeconds)
            resolve = _lastValidHover;
        GameObject clickHandler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(resolve);
        if (_pointer.pointerPress != null && _pointer.eligibleForClick && clickHandler == _pointer.pointerPress)
        {
            ExecuteEvents.Execute(_pointer.pointerPress, _pointer, ExecuteEvents.pointerClickHandler);
        }
        else if (_pointer.pointerDrag != null && _pointer.dragging && current != null)
            ExecuteEvents.ExecuteHierarchy(current, _pointer, ExecuteEvents.dropHandler);
        if (_pointer.dragging)
            ExecuteEvents.Execute(_pointer.pointerDrag, _pointer, ExecuteEvents.endDragHandler);
        _pointer.dragging = false;
        _pointer.eligibleForClick = false;
        _pointer.pointerPress = null;
        _pointer.rawPointerPress = null;
        _pointer.pointerDrag = null;
        if (_eventCamera != null && _rayOrigin != null)
        {
            _eventCamera.transform.SetParent(_rayOrigin, false);
            _eventCamera.transform.localPosition = Vector3.zero;
            _eventCamera.transform.localRotation = Quaternion.identity;
        }
        _cameraFrozen = false;
        _pressReference = null;
    }

    private void ClearHover()
    {
        if (_pointer != null) HandlePointerExitAndEnter(_pointer, null);
        _hover = null;
    }
}
