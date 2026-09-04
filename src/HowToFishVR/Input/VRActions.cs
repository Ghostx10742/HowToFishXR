using UnityEngine;
using UnityEngine.InputSystem;

namespace HowToFishVR.Input;

/// <summary>
/// Code-built Input System actions for XR poses and VR-specific buttons (turn, recenter).
/// We do NOT author a .inputactions asset — everything is created in code so the mod ships as a
/// single DLL. Gameplay buttons (fire, move, jump...) are instead injected onto the game's own
/// action names by <see cref="VRBindings"/> so the game's handlers fire unchanged.
/// </summary>
public class VRActions
{
    public static VRActions Instance { get; private set; }

    private readonly InputActionMap _map;

    // Poses
    public readonly InputAction HeadPosition;
    public readonly InputAction HeadRotation;
    public readonly InputAction LeftPosition;
    public readonly InputAction LeftRotation;
    public readonly InputAction RightPosition;
    public readonly InputAction RightRotation;

    // Analog / buttons used directly by VR systems
    public readonly InputAction TurnAxis;       // right thumbstick X (turn)
    public readonly InputAction TurnAxisY;      // right thumbstick Y (inventory scroll)
    public readonly InputAction MoveAxis;       // left thumbstick (also injected to PlayerMove)
    public readonly InputAction LeftGrip;       // 0..1
    public readonly InputAction RightGrip;      // 0..1
    public readonly InputAction LeftTrigger;    // 0..1
    public readonly InputAction RightTrigger;   // 0..1
    public readonly InputAction SprintButton;   // left thumbstick click
    public readonly InputAction CrouchButton;   // right thumbstick click
    public readonly InputAction Recenter;       // left secondary button (Y)

    public static void Initialize()
    {
        if (Instance != null) return;

        Instance = new VRActions();
        Instance._map.Enable();
    }

    private VRActions()
    {
        _map = new InputActionMap("HowToFishVR-XR");

        HeadPosition  = _map.AddAction("HeadPosition",  binding: "<XRHMD>/centerEyePosition");
        HeadRotation  = _map.AddAction("HeadRotation",  binding: "<XRHMD>/centerEyeRotation");

        LeftPosition  = _map.AddAction("LeftPosition",  binding: "<XRController>{LeftHand}/devicePosition");
        LeftRotation  = _map.AddAction("LeftRotation",  binding: "<XRController>{LeftHand}/deviceRotation");
        RightPosition = _map.AddAction("RightPosition", binding: "<XRController>{RightHand}/devicePosition");
        RightRotation = _map.AddAction("RightRotation", binding: "<XRController>{RightHand}/deviceRotation");

        TurnAxis = _map.AddAction("TurnAxis");
        TurnAxis.AddBinding("<XRController>{RightHand}/thumbstick/x");

        TurnAxisY = _map.AddAction("TurnAxisY");
        TurnAxisY.AddBinding("<XRController>{RightHand}/thumbstick/y");

        MoveAxis = _map.AddAction("MoveAxis", binding: "<XRController>{LeftHand}/thumbstick");

        LeftGrip     = _map.AddAction("LeftGrip",     binding: "<XRController>{LeftHand}/grip");
        RightGrip    = _map.AddAction("RightGrip",    binding: "<XRController>{RightHand}/grip");
        LeftTrigger  = _map.AddAction("LeftTrigger",  binding: "<XRController>{LeftHand}/trigger");
        RightTrigger = _map.AddAction("RightTrigger", binding: "<XRController>{RightHand}/trigger");

        SprintButton = _map.AddAction("SprintButton", type: InputActionType.Button, binding: "<XRController>{LeftHand}/thumbstickClicked");
        CrouchButton = _map.AddAction("CrouchButton", type: InputActionType.Button, binding: "<XRController>{RightHand}/thumbstickClicked");
        Recenter     = _map.AddAction("Recenter",     type: InputActionType.Button, binding: "<XRController>{LeftHand}/secondaryButton");
    }

    // Convenience reads — prefer Unity's XR InputDevices (reads the XR subsystem directly and always
    // works), fall back to the Input System action if a device feature is unavailable.
    public Vector3 HeadPos => NodePos(UnityEngine.XR.XRNode.CenterEye, HeadPosition);
    public Quaternion HeadRot => NodeRot(UnityEngine.XR.XRNode.CenterEye, HeadRotation);
    public Vector3 LeftPos => NodePos(UnityEngine.XR.XRNode.LeftHand, LeftPosition);
    public Quaternion LeftRot => NodeRot(UnityEngine.XR.XRNode.LeftHand, LeftRotation);
    public Vector3 RightPos => NodePos(UnityEngine.XR.XRNode.RightHand, RightPosition);
    public Quaternion RightRot => NodeRot(UnityEngine.XR.XRNode.RightHand, RightRotation);

    private static Vector3 NodePos(UnityEngine.XR.XRNode node, InputAction fallback)
    {
        var d = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(node);
        if (d.isValid)
        {
            if (node == UnityEngine.XR.XRNode.CenterEye &&
                d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.centerEyePosition, out var eye))
                return eye;
            if (d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.devicePosition, out var p))
                return p;
        }
        return fallback.ReadValue<Vector3>();
    }

    private static Quaternion NodeRot(UnityEngine.XR.XRNode node, InputAction fallback)
    {
        var d = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(node);
        if (d.isValid)
        {
            if (node == UnityEngine.XR.XRNode.CenterEye &&
                d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.centerEyeRotation, out var eye))
                return eye;
            if (d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.deviceRotation, out var r))
                return r;
        }
        return ReadQuat(fallback);
    }

    public Vector3 MainHandPos => VRConfig.Handedness.Value == DominantHand.Right ? RightPos : LeftPos;
    public Quaternion MainHandRot => VRConfig.Handedness.Value == DominantHand.Right ? RightRot : LeftRot;
    public Vector3 OffHandPos => VRConfig.Handedness.Value == DominantHand.Right ? LeftPos : RightPos;
    public Quaternion OffHandRot => VRConfig.Handedness.Value == DominantHand.Right ? LeftRot : RightRot;
    public float OffHandGrip => VRConfig.Handedness.Value == DominantHand.Right ? LeftGrip.ReadValue<float>() : RightGrip.ReadValue<float>();
    public float MainHandGrip => VRConfig.Handedness.Value == DominantHand.Right ? RightGrip.ReadValue<float>() : LeftGrip.ReadValue<float>();

    private static Quaternion ReadQuat(InputAction a)
    {
        var q = a.ReadValue<Quaternion>();
        // Guard against an all-zero quaternion before the device reports.
        return (q.x == 0 && q.y == 0 && q.z == 0 && q.w == 0) ? Quaternion.identity : q;
    }
}
