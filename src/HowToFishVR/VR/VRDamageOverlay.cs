using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace HowToFishVR.VR;

/// <summary>
/// Hurt/dying/near-death vignette for VR. The game draws its damage vignette through
/// <c>ShaderManager._vignetteMaterial</c> — a screen-space shader that never renders into the HMD eye
/// textures, so blood-on-hit / poison / fire / near-death feedback was invisible in the headset.
/// This mod-owned overlay mirrors <c>ShaderManager.UpdateVignette</c> into a head-locked full-view
/// translucent quad (the same approach that fixed the underwater tint), so the hurt/dying vignette
/// shows exactly like flatscreen.
/// </summary>
[HarmonyPatch(typeof(ShaderManager))]
public class VRDamageOverlay : MonoBehaviour
{
    public static VRDamageOverlay Instance { get; private set; }

    private Canvas _canvas;
    private Image _img;
    private Image _deathBackdrop;
    private float _deathBackdropAlpha;
    private readonly float _alphaMul = 0.95f;
    private const float NativeDeathBackdropAlpha = 0.4f;
    private const float NativeDeathFadeSpeed = 2.5f;

    public static void Create()
    {
        if (Instance != null) return;
        var go = new GameObject("HowToFishVR DamageOverlay");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<VRDamageOverlay>();
        Instance.Build();
    }

    private void Build()
    {
        var canvasGo = new GameObject("Canvas");
        canvasGo.transform.SetParent(transform, false);
        canvasGo.layer = 5;
        _canvas = canvasGo.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.WorldSpace;
        _canvas.sortingOrder = 600;
        canvasGo.AddComponent<CanvasScaler>();

        var rt = (RectTransform)canvasGo.transform;
        rt.sizeDelta = new Vector2(1920f, 1080f);
        rt.localScale = Vector3.one * 0.0016f; // ~3.1m wide / 1.73m tall — massively overfills at 0.35m so no quad edge shows

        // The flatscreen death darkening is PlayerEffects' BLACK death vignette: target intensity 0.4,
        // lerped at 2.5/s. Its screen shader does not cover the stereo eyes reliably, so reproduce that
        // exact black/alpha/fade as a plain stereo-safe UI layer behind the already-working death UI.
        var deathGo = new GameObject("DeathBackdrop");
        deathGo.transform.SetParent(canvasGo.transform, false);
        deathGo.layer = 5;
        _deathBackdrop = deathGo.AddComponent<Image>();
        var drt = (RectTransform)deathGo.transform;
        drt.anchorMin = Vector2.zero; drt.anchorMax = Vector2.one;
        drt.offsetMin = Vector2.zero; drt.offsetMax = Vector2.zero;
        _deathBackdrop.color = Color.clear;
        _deathBackdrop.raycastTarget = false;
        try
        {
            _deathBackdrop.material = new Material(Canvas.GetDefaultCanvasMaterial())
            {
                name = "VR Native Death Backdrop",
                hideFlags = HideFlags.HideAndDontSave
            };
        }
        catch { }

        var imgGo = new GameObject("Vignette");
        imgGo.transform.SetParent(canvasGo.transform, false);
        imgGo.layer = 5;
        _img = imgGo.AddComponent<Image>();
        var irt = (RectTransform)imgGo.transform;
        irt.anchorMin = Vector2.zero; irt.anchorMax = Vector2.one;
        irt.offsetMin = Vector2.zero; irt.offsetMax = Vector2.zero;
        _img.color = new Color(0f, 0f, 0f, 0f);
        _img.raycastTarget = false;
        // RADIAL sprite (transparent centre, dark edges) — the game's vignette shader is a classic
        // vignette: edges dark, the CENTRE of the view stays clear. A uniform black quad made the whole
        // screen go "practically super dark" at low health; the radial gradient keeps the middle of
        // your view readable exactly like the flatscreen effect. The damage falloff is GENTLE and only
        // reaches full darkness at/behind the quad's border — a tight ring left a visible circle
        // ("you can see the border").
        try { _img.sprite = MakeDamageVignetteSprite(); } catch { }

        // Plain default UI material — the ZTest-Always override made the quad render OPAQUE BLACK in
        // URP world space (the "solid black" death/hit screen). The quad is head-locked 0.45m in front
        // of the face, so nothing can occlude it without the override; the underwater tint uses the same
        // plain material and renders correctly.
        try
        {
            var mat = new Material(Canvas.GetDefaultCanvasMaterial())
            {
                name = "VR DamageOverlay",
                hideFlags = HideFlags.HideAndDontSave
            };
            _img.material = mat;
        }
        catch { }

        canvasGo.SetActive(false);
    }

    private void LateUpdate()
    {
        if (!Plugin.VREnabled) return;
        RepositionNow();
        UpdateDeathBackdrop();
    }

    /// <summary>Place the overlay after the final VR camera pose too. The game resets its camera earlier
    /// in the frame while dead, so LateUpdate-only placement left the translucent death vignette at the
    /// death location when the camera was moved again immediately before rendering.</summary>
    public void RepositionNow()
    {
        if (!Plugin.VREnabled || _canvas == null) return;
        var rig = VRRig.Instance;
        if (rig == null || rig.Head == null) return;
        // Full head-lock, close to the face, overfilling the view. Anchored to the ACTUAL rendered camera
        // (== rig.Head while alive) so the vignette also stays centred on the view while dead, where the
        // camera is driven to the ragdoll and rig.Head diverges to the death spot.
        Transform view = rig.Head;
        try { var pv = Player.LocalPlayer; if (pv != null && pv.Camera != null && pv.Camera.Cam != null) view = pv.Camera.Cam.transform; } catch { }
        _canvas.transform.position = view.position + view.forward * 0.35f;
        _canvas.transform.rotation = view.rotation;
    }

    private void UpdateCanvasActive()
    {
        if (_canvas == null || _img == null) return;
        bool show = _img.color.a > 0.005f || _deathBackdropAlpha > 0.005f;
        if (_canvas.gameObject.activeSelf != show) _canvas.gameObject.SetActive(show);
    }

    private void UpdateDeathBackdrop()
    {
        if (_deathBackdrop == null) return;
        bool dead = false;
        try
        {
            var player = Player.LocalPlayer;
            dead = player != null && player.Dying != null && player.Dying.IsDead;
        }
        catch { }
        float target = dead ? NativeDeathBackdropAlpha : 0f;
        _deathBackdropAlpha = Mathf.Lerp(_deathBackdropAlpha, target,
            Mathf.Clamp01(Time.deltaTime * NativeDeathFadeSpeed));
        if (Mathf.Abs(_deathBackdropAlpha - target) < 0.001f) _deathBackdropAlpha = target;
        _deathBackdrop.color = new Color(0f, 0f, 0f, _deathBackdropAlpha);
        UpdateCanvasActive();
    }

    /// <summary>
    /// Damage/near-death vignette sprite: the centre stays CLEAR and darkness only builds up slowly
    /// toward the border, reaching full strength at/behind the quad's edge. A steeper ring (like the
    /// tighter ring made a visible circle inside the view — this one has NO visible edge, just a
    /// gentle darkening of the periphery like the game's own vignette shader. The Image's color alpha
    /// (the game's intensity) multiplies on top, so the whole thing scales with damage.
    /// </summary>
    private static Sprite MakeDamageVignetteSprite()
    {
        const int size = 256;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = "VR Damage Vignette Gradient",
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp
        };
        // Ramp 0 at d=0.55 to 1 at d=1.3 (beyond the quad's edges at d=1.0 / corners 1.41): the fully
        // dark region sits behind the border, so no ring line is visible — only smooth edge darkening.
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) / size * 2f - 1f;
                float dy = (y + 0.5f) / size * 2f - 1f;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float t = Mathf.Clamp01((d - 0.55f) / 0.75f);
                float a = t * t * (3f - 2f * t); // smoothstep
                tex.SetPixel(x, y, new Color(0f, 0f, 0f, a));
            }
        }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f));
    }

    private void Apply(float intensity, Color color)
    {
        if (_img == null || _canvas == null) return;
        // The grey/near-death vignette is KEPT while dead — it's part of the death look, and the death
        // screen (DeathCanvas, converted to a full-screen overlay with higher sorting order) renders on
        // top of it, so the respawn text is always readable. No black-screen: the death canvas is a
        // full-view overlay now, not a small panel hidden behind a black quad.
        float a = Mathf.Clamp01(intensity * _alphaMul);
        _img.color = new Color(color.r, color.g, color.b, a);
        UpdateCanvasActive();
    }

    [HarmonyPostfix]
    [HarmonyPatch("UpdateVignette")]
    private static void OnUpdateVignette(float intensity, Color color)
    {
        if (Plugin.VREnabled && Instance != null) Instance.Apply(intensity, color);
    }
}
