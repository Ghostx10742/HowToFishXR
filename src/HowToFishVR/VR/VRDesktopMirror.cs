using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

namespace HowToFishVR.VR;

/// <summary>
/// MAVR-style desktop mirror: shows the VR view on the PC window. The game's own XR mirror
/// (<c>XRSettings.gameViewRenderMode = LeftEye</c>) comes up BLACK with this game's manually-booted
/// OpenXR + URP — the window only shows the screen-space UI. MAVR solves this by drawing a full-screen
/// RawImage that displays a RenderTexture of the VR view, so we do the same:
///   - a dedicated mirror CAMERA (allowXRRendering off, renders to a RenderTexture) whose transform
///     copies the actual view camera (player cam in game, menu cam in menus) every frame — posed by
///     VRCameraPoser in the same onBeforeRender pass, so the mirror is never behind,
///   - a Screen Space Overlay canvas (sorting order -1000, behind the game's UI) with a full-screen
///     RawImage showing that RenderTexture.
/// The screen-space game UI (main menu, pause, etc.) still renders on top of the RawImage, exactly
/// like the headset view composes the world + panels.
/// </summary>
public class VRDesktopMirror : MonoBehaviour
{
    public static VRDesktopMirror Instance { get; private set; }

    private Camera _mirrorCam;
    private RenderTexture _rt;

    public static void Create()
    {
        if (Instance != null) return;
        var go = new GameObject("HowToFishVR DesktopMirror");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<VRDesktopMirror>();
    }

    private void Awake()
    {
        try
        {
            _rt = new RenderTexture(1280, 720, 24) { name = "HowToFishVR DesktopMirror RT" };

            var camGo = new GameObject("HowToFishVR DesktopMirrorCam");
            camGo.transform.SetParent(transform, false);
            _mirrorCam = camGo.AddComponent<Camera>();
            _mirrorCam.targetTexture = _rt;
            _mirrorCam.enabled = true;
            _mirrorCam.depth = -100;      // renders first in the frame, well before the view camera
            _mirrorCam.stereoTargetEye = StereoTargetEyeMask.None;
            try
            {
                var data = _mirrorCam.GetUniversalAdditionalCameraData();
                if (data != null)
                {
                    data.allowXRRendering = false; // flat render into the RT, never the headset
                    data.renderPostProcessing = false;
                }
            }
            catch { }

            var canvasGo = new GameObject("HowToFishVR DesktopMirrorCanvas");
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = -1000;  // behind the game's own UI

            var imgGo = new GameObject("Mirror", typeof(RectTransform));
            imgGo.transform.SetParent(canvasGo.transform, false);
            var rt = (RectTransform)imgGo.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            var image = imgGo.AddComponent<RawImage>();
            image.texture = _rt;
            image.raycastTarget = false;  // never block clicks
            image.uvRect = new Rect(0f, 0f, 1f, 1f);
        }
        catch { }
    }

    /// <summary>
    /// Point the mirror camera at the view camera. Called from the camera poser's onBeforeRender pass
    /// AFTER the view camera has been posed, so the mirror renders this frame's exact view (zero lag).
    /// </summary>
    public void SyncToViewCamera()
    {
        try
        {
            if (_mirrorCam == null || _rt == null) return;
            var view = GetViewCamera();
            if (view == null) return;
            _mirrorCam.transform.SetPositionAndRotation(view.transform.position, view.transform.rotation);
            _mirrorCam.cullingMask = view.cullingMask;
            _mirrorCam.fieldOfView = view.fieldOfView;
            _mirrorCam.nearClipPlane = view.nearClipPlane;
            _mirrorCam.farClipPlane = view.farClipPlane;
        }
        catch { }
    }

    /// <summary>Same camera selection the poser uses: the player camera in game, the first active
    /// screen camera in menus.</summary>
    private static Camera GetViewCamera()
    {
        try
        {
            var p = Player.LocalPlayer;
            if (p != null && p.Camera != null && p.Camera.Cam != null && p.Camera.Cam.isActiveAndEnabled)
                return p.Camera.Cam;
        }
        catch { }
        foreach (var cam in Camera.allCameras)
            if (cam != null && cam.isActiveAndEnabled && cam.targetTexture == null) return cam;
        return null;
    }

    private void OnDestroy()
    {
        try { if (_rt != null) _rt.Release(); } catch { }
    }
}
