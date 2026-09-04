using System.Collections;
using UnityEngine;

namespace HowToFishVR.VR;

/// <summary>
/// Makes the game's REAL frosted-glass UI blur (the "Unified Universal Blur" asset — pixel/downsampled
/// Kawase blur, material UniversalBlurUI / shader Unify/UI/Tinted Blur) render on the VR world-space UI
/// panels, 1:1 with flatscreen — WITHOUT touching the render pipeline (so it can never black the view).
///
/// How the effect works: a URP ScriptableRendererFeature blurs the eye color at
/// RenderPassEvent.AfterRenderingPostProcessing and binds the result to the GLOBAL shader texture
/// "_GlobalUniversalBlurTexture". The UI material samples that texture in screen space. On flatscreen the
/// UI is a screen-space-overlay canvas drawn AFTER post, so the texture is bound when it samples. Our mod
/// reparents the UI to WORLD-SPACE head-locked panels, which draw in the eye camera's TRANSPARENT queue —
/// EARLIER in the frame than the blur pass — AND the transient blur RTHandle is released at frame end, so
/// at the start of the next frame the global points at freed memory → the panels sample BLACK.
///
/// The fix (read-only, zero pipeline change — the safe approach from the research): every frame, after the
/// game has produced the blur, COPY the global blur texture into a PERSISTENT RenderTexture, and re-bind
/// that persistent copy as "_GlobalUniversalBlurTexture" BEFORE the next frame's transparent queue draws.
/// The panels then sample a valid (one-frame-old) blur — the true pixel-frosted look — in both eyes. We
/// never move the feature's injection point, never toggle XR/post, never re-plumb the eye color targets —
/// the exact things that blacked the view last time. Worst case (if the copy is ever empty) the panels are
/// no worse than the current flat tint; the view itself is untouchable.
/// </summary>
internal sealed class BlurCapture : MonoBehaviour
{
    internal static BlurCapture Instance { get; private set; }

    private int _globalId;
    private RenderTexture _rt;
    private bool _haveContent;

    /// <summary>True once a non-empty blur has actually been captured — VRUIManager uses this to decide
    /// whether to keep the real blur material (show it) or fall back to the flat tint (never leave black).</summary>
    internal bool HasBlur => _haveContent && _rt != null && _rt.IsCreated();

    internal static void Create()
    {
        if (Instance != null) return;
        var go = new GameObject("HowToFishVR BlurCapture");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<BlurCapture>();
    }

    private void Awake()
    {
        _globalId = Shader.PropertyToID("_GlobalUniversalBlurTexture");
        // Create the persistent target up-front and seed it with a dark frosted grey. This is the
        // never-black guarantee: the panels always sample SOMETHING readable (a dark translucent backing,
        // the look the blur tint intends) even before the first live blur is captured, or if capture never
        // succeeds on a given machine. A real blur simply blits over this the moment it's available.
        int w = Mathf.Max(2, Screen.width / 2), h = Mathf.Max(2, Screen.height / 2);
        EnsureRT(w, h);
        ClearRT(new Color(0.10f, 0.13f, 0.16f, 1f));
    }

    private void OnEnable()  { UnityEngine.Rendering.RenderPipelineManager.endCameraRendering += OnEndCamera; }
    private void OnDisable() { UnityEngine.Rendering.RenderPipelineManager.endCameraRendering -= OnEndCamera; }

    private void Update()
    {
        // Re-bind the persistent copy as the global blur texture BEFORE this frame's cameras render, so the
        // world-space UI panels (transparent queue) sample a VALID texture instead of the released transient.
        // The game feature overwrites the global with the fresh blur mid-frame (captured below), harmless.
        if (_rt != null && _rt.IsCreated()) Shader.SetGlobalTexture(_globalId, _rt);
    }

    /// <summary>
    /// Capture the game's blur the instant a camera finishes rendering — the transient blur RTHandle is
    /// still ALIVE here (mid-frame), unlike WaitForEndOfFrame which fired after it was released and copied
    /// BLACK (that was the "still just black" bug, and the pitch-black underwater). We copy it into the
    /// persistent RT that Update re-binds next frame, so the world-space UI + underwater image sample a real
    /// blur. Fires per eye in multipass; the last valid one wins (fine for a flat head-locked panel).
    /// </summary>
    private void OnEndCamera(UnityEngine.Rendering.ScriptableRenderContext ctx, Camera cam)
    {
        try
        {
            if (cam == null || cam.cameraType != CameraType.Game) return; // skip reflection/preview cams
            var src = Shader.GetGlobalTexture(_globalId) as RenderTexture;
            if (src == null || src == _rt || !src.IsCreated()) return;      // nothing valid produced this cam
            EnsureRT(src.width, src.height);
            if (_rt == null) return;
            Graphics.Blit(src, _rt);
            _haveContent = true;
        }
        catch { }
    }

    private void EnsureRT(int w, int h)
    {
        if (w < 2 || h < 2) return;
        if (_rt != null && _rt.width == w && _rt.height == h && _rt.IsCreated()) return;
        if (_rt != null) { try { _rt.Release(); } catch { } }
        _rt = new RenderTexture(w, h, 0, RenderTextureFormat.DefaultHDR)
        {
            name = "HowToFishVR_BlurCapture",
            useMipMap = false,
            autoGenerateMips = false,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        try { _rt.Create(); } catch { }
        ClearRT(new Color(0.10f, 0.13f, 0.16f, 1f)); // never flash black on (re)allocate
    }

    private void ClearRT(Color c)
    {
        if (_rt == null || !_rt.IsCreated()) return;
        var prev = RenderTexture.active;
        try { RenderTexture.active = _rt; GL.Clear(true, true, c); }
        catch { }
        finally { RenderTexture.active = prev; }
    }

    private void OnDestroy()
    {
        if (_rt != null) { try { _rt.Release(); } catch { } _rt = null; }
        if (Instance == this) Instance = null;
    }
}
