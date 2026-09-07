using System;
using System.Collections.Generic;
using HowToFishVR.Body;
using HowToFishVR.Input;
using HowToFishVR.VR;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace HowToFishVR.UI;

/// <summary>
/// Our own in-game "VR Settings" — reworked the way the ModMenu mod does it (no ModMenu dependency):
///  - The entry button is a CLONE OF THE GAME'S REAL "Options" button, inserted into the actual button
///    layout (pause screen AND main menu), so it looks and behaves like one of the game's buttons.
///  - The settings page is built INSIDE the game's canvas (parented to the pause holder / main menu),
///    stretched over the screen with the game's native backdrop + button styles and the game's TMP font.
///    Because it lives inside the converted VR panel, it is world-space, always-visible and laser-
///    clickable — the separate custom canvas we used before rendered but wasn't clickable.
///  - Every setting row is a native-styled "< value >" stepper (ModMenu's interaction): left/right
///    arrows change the value, exactly like ModMenu.
/// Opened via the injected "VR Settings" buttons, F1, or both thumbstick clicks.
/// </summary>
public class VRSettingsPanel : MonoBehaviour
{
    public static VRSettingsPanel Instance { get; private set; }
    public static bool IsOpen => Instance != null && Instance._pageOpen;

    // Entry buttons: clones of the game's real "Options" button.
    private GameObject _pauseEntryButton;
    private GameObject _mainMenuEntryButton;

    // The game's real "Options" button GameObject — the style source for our button AND every row.
    private GameObject _buttonTemplate;
    private Image _nativeBackdropStyle;

    // Native control templates cloned from the game's own Options screen (ModMenu-style): booleans
    // become real Toggles, numbers become real Sliders, enums keep the arrow steppers, and action
    // rows are full-width buttons. Discovered once from the scene (the options screen is loaded in the
    // main menu); rows fall back to steppers if a template is missing.
    private GameObject _nativeToggleTemplate;
    private GameObject _nativeSliderTemplate;
    private bool _templatesScanned;

    // The settings page, parented INSIDE the game's canvas.
    private GameObject _page;
    private bool _pageOpen;
    private bool _openedFromPause;

    // Host canvas objects (found by component, like ModMenu).
    private GameObject _pauseHolder;
    private GameObject _pauseMainScreen;
    private bool _pauseMainScreenWasActive;
    private Transform _mainCanvasRoot;
    private GameObject _mainMenuPage;
    private GameObject _mainMenuTitle;
    private GameObject _discordButton;
    private bool _mainMenuHidden;

    // Value buttons on the current page, refreshed after a setting changes.
    private readonly List<(GameObject, Func<string>)> _valueRefreshers = new();

    private float _nextProbe;
    private bool _prevBothSticks;
    private bool _wasPaused;
    private bool _wasInMenu;

    public static void Create()
    {
        if (Instance != null) return;
        var go = new GameObject("HowToFishVR Settings");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<VRSettingsPanel>();
    }

    private void Update()
    {
        bool toggle = false;
        try { if (UnityEngine.Input.GetKeyDown(KeyCode.F1)) toggle = true; } catch { }

        var a = VRActions.Instance;
        if (a != null)
        {
            bool both = a.SprintButton.IsPressed() && a.CrouchButton.IsPressed();
            if (both && !_prevBothSticks) toggle = true;
            _prevBothSticks = both;
        }

        if (toggle)
        {
            if (_pageOpen) Close();
            else
            {
                bool inMenu = false; try { inMenu = MainMenuManager.IsInMenu; } catch { }
                bool paused = false; try { paused = PauseManager.IsPaused; } catch { }
                if (inMenu) Open(fromPause: false);
                else if (paused) Open(fromPause: true);
            }
        }

        Tick();
    }

    /// <summary>Keep the entry buttons + page state in sync (ModMenu's Tick).</summary>
    private void Tick()
    {
        if (!Plugin.VREnabled) return;

        // INSTANT injection on menu-open transitions: the pause and main-menu canvases are rebuilt
        // (destroyed + re-instantiated) every time they open, which destroys our injected button.
        // Re-inject the MOMENT the fresh menu appears — not up to a second later via the probe below.
        // (Pratfall's menu persists, so it injects once; ours is rebuilt each open, so we re-inject
        // instantly per open — same "always there" result.)
        bool inMenu = IsInMenu();
        bool paused = IsPaused() && !inMenu;
        if (paused && !_wasPaused && _pauseEntryButton == null) TryInjectPause();
        if (inMenu && !_wasInMenu && _mainMenuEntryButton == null) TryInjectMainMenu();
        _wasPaused = paused;
        _wasInMenu = inMenu;

        // Keep the buttons visible only while their menu is actually up (and not covered by our page).
        if (_mainMenuEntryButton != null)
            _mainMenuEntryButton.SetActive(inMenu && !_pageOpen);
        if (_pauseEntryButton != null)
            _pauseEntryButton.SetActive(paused && _pauseMainScreen != null && _pauseMainScreen.activeSelf && !_pageOpen);

        // Safety-net probe: fast while a button is missing (e.g. the menu rebuilt mid-transition and the
        // first injection attempt ran before its buttons existed), slow otherwise.
        if (Time.unscaledTime < _nextProbe) return;
        _nextProbe = Time.unscaledTime + ((_pauseEntryButton == null || _mainMenuEntryButton == null) ? 0.2f : 1f);
        if (_mainMenuEntryButton == null && inMenu) TryInjectMainMenu();
        if (_pauseEntryButton == null && paused) TryInjectPause();

        // PAGE SELF-HEAL: the game rebuilds the pause/main-menu canvas on some transitions, which
        // destroys our page AND the button template. Rebuild the page the moment it's lost (instead of
        // "never shows again"), and re-activate it if anything deactivated it (e.g. a stray scan pass).
        if (_pageOpen)
        {
            if (_page == null)
            {
                BuildPage();
            }
            else if (!_page.activeSelf)
            {
                _page.SetActive(true);
            }
            else if (_page.transform.parent != null)
            {
                _page.transform.SetAsLastSibling(); // keep it on top of the menu behind it
            }
        }

        if (_pageOpen && _openedFromPause && !paused)
        {
            Close();
        }
        else if (_pageOpen && !_openedFromPause && !inMenu)
        {
            Close();
        }
    }

    private static bool IsInMenu() { try { return MainMenuManager.IsInMenu; } catch { return false; } }
    private static bool IsPaused() { try { return PauseManager.IsPaused; } catch { return false; } }

    // ---- Entry button injection (ModMenu style: clone the game's real Options button) ----

    private void TryInjectPause()
    {
        try
        {
            var pm = UnityEngine.Object.FindObjectOfType<PauseManager>(true);
            if (pm == null || pm._pauseHolder == null || pm._mainScreen == null) return;
            _pauseHolder = pm._pauseHolder;
            _pauseMainScreen = pm._mainScreen;
            var txt = FindMenuText(_pauseMainScreen.transform, "Options");
            if (txt == null) return;
            var srcBtn = txt.GetComponentInParent<Button>(true);
            if (srcBtn == null) return;
            if (_buttonTemplate == null) _buttonTemplate = srcBtn.gameObject;
            DiscoverBackdropStyle(pm._pauseHolder.transform);
            DiscoverNativeTemplates();
            RemoveStaleButtons(srcBtn.transform.parent); // never leave a duplicate entry button behind
            _pauseEntryButton = CreateStyledButton(srcBtn.gameObject, srcBtn.transform.parent, "VR Settings", OpenFromPause);
            InsertAfter(srcBtn.GetComponent<RectTransform>(), _pauseEntryButton.GetComponent<RectTransform>());
            _pauseEntryButton.SetActive(false);
        }
        catch { }
    }

    private void TryInjectMainMenu()
    {
        try
        {
            var cm = UnityEngine.Object.FindObjectOfType<CanvasManager>(true);
            if (cm == null || cm._mainMenuCanvas == null) return;
            _mainCanvasRoot = cm._mainMenuCanvas.transform;
            if (cm._allMainMenuButtons != null && cm._allMainMenuButtons.Count > 0)
                _mainMenuPage = cm._allMainMenuButtons[0];
            _mainMenuTitle = cm._howToFishTitle;
            _discordButton = cm._discordButton;
            var txt = FindMenuText(_mainCanvasRoot, "Options");
            if (txt == null) return;
            var srcBtn = txt.GetComponentInParent<Button>(true);
            if (srcBtn == null) return;
            _buttonTemplate = srcBtn.gameObject;
            DiscoverBackdropStyle(_mainCanvasRoot);
            DiscoverNativeTemplates();
            RemoveStaleButtons(srcBtn.transform.parent); // never leave a duplicate entry button behind
            _mainMenuEntryButton = CreateStyledButton(srcBtn.gameObject, srcBtn.transform.parent, "VR Settings", OpenFromMainMenu);
            InsertAfter(srcBtn.GetComponent<RectTransform>(), _mainMenuEntryButton.GetComponent<RectTransform>());
        }
        catch { }
    }

    /// <summary>
    /// Re-find the game's real "Options" button if our stored template was destroyed (the pause/main-
    /// menu canvas is rebuilt on some transitions, killing every reference into it). Tries the current
    /// menu first, then any "Options"-labeled button in the scene.
    /// </summary>
    private void RediscoverTemplate()
    {
        if (_buttonTemplate != null) return;
        try
        {
            GameObject found = null;
            if (_openedFromPause && _pauseMainScreen != null)
            {
                var txt = FindMenuText(_pauseMainScreen.transform, "Options");
                if (txt != null)
                {
                    var b = txt.GetComponentInParent<Button>(true);
                    if (b != null) found = b.gameObject;
                }
            }
            if (found == null && _mainCanvasRoot != null)
            {
                var txt = FindMenuText(_mainCanvasRoot, "Options");
                if (txt != null)
                {
                    var b = txt.GetComponentInParent<Button>(true);
                    if (b != null) found = b.gameObject;
                }
            }
            if (found == null)
            {
                foreach (var t in UnityEngine.Object.FindObjectsOfType<TextMeshProUGUI>(true))
                {
                    if (t == null) continue;
                    if (!string.Equals((t.text ?? "").Trim(), "Options", StringComparison.OrdinalIgnoreCase)) continue;
                    var b = t.GetComponentInParent<Button>(true);
                    if (b != null) { found = b.gameObject; break; }
                }
            }
            if (found != null)
            {
                _buttonTemplate = found;
            }
        }
        catch { }
    }

    /// <summary>Destroy any previously-injected entry buttons under this layout (the pause screen is
    /// sometimes rebuilt on later opens, which used to leave a second 'VR Settings' button behind).</summary>
    private static void RemoveStaleButtons(Transform parent)
    {
        if (parent == null) return;
        try
        {
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                var child = parent.GetChild(i);
                if (child != null && child.name != null &&
                    child.name.StartsWith("VRSettingsButton_", StringComparison.Ordinal))
                    Destroy(child.gameObject);
            }
        }
        catch { }
    }

    // ---- Open / Close (hide the host screen while our page is up, restore after) ----

    private void OpenFromPause() => Open(fromPause: true);
    private void OpenFromMainMenu() => Open(fromPause: false);

    private void Open(bool fromPause)
    {
        _pageOpen = true;
        _openedFromPause = fromPause;
        if (fromPause)
        {
            if (_pauseEntryButton != null) _pauseEntryButton.SetActive(false);
            if (_pauseMainScreen != null)
            {
                _pauseMainScreenWasActive = _pauseMainScreen.activeSelf;
                _pauseMainScreen.SetActive(false);
            }
        }
        else
        {
            if (_mainMenuEntryButton != null) _mainMenuEntryButton.SetActive(false);
            HideMainMenu();
        }
        BuildPage();
    }

    private void Close()
    {
        _pageOpen = false;
        if (_page != null) Destroy(_page);
        _page = null;
        _valueRefreshers.Clear();
        if (_openedFromPause)
        {
            if (_pauseMainScreen != null && IsPaused() && !IsInMenu()) _pauseMainScreen.SetActive(_pauseMainScreenWasActive);
            if (_pauseEntryButton != null && IsPaused() && !IsInMenu()) _pauseEntryButton.SetActive(true);
        }
        else
        {
            RestoreMainMenu();
            if (_mainMenuEntryButton != null && IsInMenu()) _mainMenuEntryButton.SetActive(true);
        }
        _openedFromPause = false;
    }

    private void HideMainMenu()
    {
        _mainMenuHidden = true;
        if (_mainMenuPage != null) { _mainMenuPage.SetActive(false); }
        if (_mainMenuTitle != null) { _mainMenuTitle.SetActive(false); }
        if (_discordButton != null) { _discordButton.SetActive(false); }
    }

    private void RestoreMainMenu()
    {
        if (_mainMenuHidden)
        {
            if (_mainMenuPage != null) _mainMenuPage.SetActive(true);
            if (_mainMenuTitle != null) _mainMenuTitle.SetActive(true);
            if (_discordButton != null) _discordButton.SetActive(true);
            _mainMenuHidden = false;
        }
    }

    // ---- Page construction (ModMenu style: native backdrop + native button rows, inside the game canvas) ----

    private void BuildPage()
    {
        if (_page != null) Destroy(_page);

        Transform host = null;
        if (_openedFromPause && _pauseHolder != null) host = _pauseHolder.transform;
        else if (_mainCanvasRoot != null) host = _mainCanvasRoot;
        else if (_mainMenuEntryButton != null) host = _mainMenuEntryButton.transform.parent;
        else if (_pauseEntryButton != null) host = _pauseEntryButton.transform.parent;
        if (host == null)
        {
            _pageOpen = false;
            return;
        }

        // The game can rebuild the host canvas mid-session, destroying our stored button template —
        // without a template every row build throws and the page silently never appears again.
        RediscoverTemplate();
        if (_buttonTemplate == null)
        {
            _pageOpen = false;
            return;
        }

        _page = new GameObject("HowToFishVR SettingsPage", typeof(RectTransform), typeof(CanvasGroup), typeof(Image));
        var rt = (RectTransform)_page.transform;
        rt.SetParent(host, false);
        Stretch(rt);
        rt.SetAsLastSibling();

        // Native dark page backdrop: a full-screen near-black Image behind the rows, like the game's own
        // option screens and ModMenu's page. It dims the world behind the settings (the page sits in the
        // converted VR panel) and blocks laser clicks from falling through to the deactivated menu behind.
        var img = _page.GetComponent<Image>();
        img.color = new Color(0f, 0f, 0f, 0.78f);
        img.raycastTarget = true;

        var title = CreateText("Title", _page.transform, "VR Settings", 58, TextAlignmentOptions.TopLeft);
        SetRect(title.rectTransform, new Vector2(0.06f, 0.9f), new Vector2(0.94f, 0.985f), Vector2.zero, Vector2.zero);

        var rows = new GameObject("Rows", typeof(RectTransform));
        var rowsRt = (RectTransform)rows.transform;
        rowsRt.SetParent(_page.transform, false);
        rowsRt.anchorMin = new Vector2(0.06f, 0.07f);
        rowsRt.anchorMax = new Vector2(0.94f, 0.885f);
        rowsRt.offsetMin = Vector2.zero;
        rowsRt.offsetMax = Vector2.zero;

        float y = 4f;
        foreach (var row in BuildSettingsList())
        {
            CreateSettingRow(rowsRt, row, y);
            y += 48f;
        }

        var back = CreateStyledButton(_buttonTemplate, _page.transform, "Back", Close);
        SetRect(back.GetComponent<RectTransform>(), new Vector2(0.06f, 0.015f), new Vector2(0.25f, 0.062f), Vector2.zero, Vector2.zero);
        SetButtonText(back, "Back");

    }

    /// <summary>A single setting row, built from the correct native control for its type (ModMenu's
    /// approach): Toggle = real toggle, Slider = real slider, Stepper = "< value >" arrows (enums),
    /// Button = full-width clickable button (actions like Calibrate/Close).</summary>
    private void CreateSettingRow(RectTransform parent, SettingRow row, float y)
    {
        var go = new GameObject("Row_" + row.Label, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = new Vector2(0f, -y);
        rt.sizeDelta = new Vector2(0f, 44f);

        var lbl = CreateText("Label", rt, row.Label, 24, TextAlignmentOptions.Left);
        SetRect(lbl.rectTransform, Vector2.zero, new Vector2(0.5f, 1f), Vector2.zero, Vector2.zero);

        switch (row.Kind)
        {
            case RowKind.Toggle:
                CreateToggleControl(rt, row);
                break;
            case RowKind.Slider:
                CreateSliderControl(rt, row);
                break;
            case RowKind.Button:
                var btn = CreateStyledButton(_buttonTemplate, rt, row.Label, () => row.OnClick());
                SetRect(btn.GetComponent<RectTransform>(), new Vector2(0.52f, 0f), Vector2.one, Vector2.zero, Vector2.zero);
                SetButtonText(btn, row.Label);
                break;
            default: // Stepper
                var prev = CreateStyledButton(_buttonTemplate, rt, "<", () => { row.OnPrev(); RefreshValues(); });
                SetRect(prev.GetComponent<RectTransform>(), new Vector2(0.52f, 0.02f), new Vector2(0.6f, 0.98f), Vector2.zero, Vector2.zero);
                SetButtonText(prev, "<");

                var val = CreateStyledButton(_buttonTemplate, rt, row.Value(), () => { row.OnNext(); RefreshValues(); });
                SetRect(val.GetComponent<RectTransform>(), new Vector2(0.61f, 0.02f), new Vector2(0.88f, 0.98f), Vector2.zero, Vector2.zero);
                SetButtonText(val, row.Value());
                _valueRefreshers.Add((val, row.Value));

                var next = CreateStyledButton(_buttonTemplate, rt, ">", () => { row.OnNext(); RefreshValues(); });
                SetRect(next.GetComponent<RectTransform>(), new Vector2(0.89f, 0.02f), new Vector2(0.97f, 0.98f), Vector2.zero, Vector2.zero);
                SetButtonText(next, ">");
                break;
        }
    }

    /// <summary>A real native Toggle for boolean settings (the game's own toggle look).</summary>
    private void CreateToggleControl(RectTransform rt, SettingRow row)
    {
        if (_nativeToggleTemplate == null)
        {
            // No native toggle discovered — fall back to a plain value button that flips the bool.
            var btn = CreateStyledButton(_buttonTemplate, rt, row.Value(), () => { row.OnNext(); RefreshValues(); });
            SetRect(btn.GetComponent<RectTransform>(), new Vector2(0.52f, 0f), Vector2.one, Vector2.zero, Vector2.zero);
            SetButtonText(btn, row.Value());
            _valueRefreshers.Add((btn, row.Value));
            return;
        }
        var tgl = Instantiate(_nativeToggleTemplate, rt, false);
        tgl.name = "VRSettingsToggle_" + row.Label;
        tgl.SetActive(false);
        DisableLocalization(tgl);
        var toggle = tgl.GetComponent<Toggle>();
        if (toggle != null)
        {
            toggle.onValueChanged = new Toggle.ToggleEvent();
            toggle.isOn = row.GetBool();
            toggle.onValueChanged.AddListener(v => row.SetBool(v));
        }
        SetRect(tgl.GetComponent<RectTransform>(), new Vector2(0.52f, 0.04f), new Vector2(0.7f, 0.96f), Vector2.zero, Vector2.zero);
        tgl.SetActive(true);
    }

    /// <summary>A real native Slider for numeric settings, with the current value shown beside it.</summary>
    private void CreateSliderControl(RectTransform rt, SettingRow row)
    {
        if (_nativeSliderTemplate == null)
        {
            // Fall back to the stepper (still functional).
            var prev = CreateStyledButton(_buttonTemplate, rt, "<", () => { row.OnPrev(); RefreshValues(); });
            SetRect(prev.GetComponent<RectTransform>(), new Vector2(0.52f, 0.02f), new Vector2(0.6f, 0.98f), Vector2.zero, Vector2.zero);
            var val = CreateStyledButton(_buttonTemplate, rt, row.Value(), () => { row.OnNext(); RefreshValues(); });
            SetRect(val.GetComponent<RectTransform>(), new Vector2(0.61f, 0.02f), new Vector2(0.88f, 0.98f), Vector2.zero, Vector2.zero);
            var next = CreateStyledButton(_buttonTemplate, rt, ">", () => { row.OnNext(); RefreshValues(); });
            SetRect(next.GetComponent<RectTransform>(), new Vector2(0.89f, 0.02f), new Vector2(0.97f, 0.98f), Vector2.zero, Vector2.zero);
            _valueRefreshers.Add((val, row.Value));
            return;
        }
        var sld = Instantiate(_nativeSliderTemplate, rt, false);
        sld.name = "VRSettingsSlider_" + row.Label;
        sld.SetActive(false);
        DisableLocalization(sld);
        var slider = sld.GetComponent<Slider>();
        if (slider != null)
        {
            slider.onValueChanged = new Slider.SliderEvent();
            slider.minValue = row.Min;
            slider.maxValue = row.Max;
            slider.wholeNumbers = row.WholeNumbers;
            slider.value = row.GetFloat();
            slider.onValueChanged.AddListener(v => row.SetFloat(v));
        }
        SetRect(sld.GetComponent<RectTransform>(), new Vector2(0.52f, 0.2f), new Vector2(0.82f, 0.8f), Vector2.zero, Vector2.zero);
        sld.SetActive(true);

        var valTxt = CreateText("Value", rt, row.Value(), 22, TextAlignmentOptions.Right);
        SetRect(valTxt.rectTransform, new Vector2(0.84f, 0f), Vector2.one, Vector2.zero, Vector2.zero);
        _valueRefreshers.Add((valTxt.gameObject, row.Value));
    }

    private void RefreshValues()
    {
        foreach (var (btn, getter) in _valueRefreshers)
            if (btn != null) SetButtonText(btn, getter());
    }

    // ---- The settings list (same options as before, driven by the same config entries) ----

    private enum RowKind { Stepper, Toggle, Slider, Button }

    private struct SettingRow
    {
        public string Label;
        public RowKind Kind;
        public Func<string> Value;   // current value text (steppers / value labels)
        public Action OnPrev;        // stepper: previous
        public Action OnNext;        // stepper: next / toggle fallback flip
        public Action OnClick;       // button rows
        public Func<bool> GetBool;   // toggle
        public Action<bool> SetBool; // toggle
        public Func<float> GetFloat; // slider
        public Action<float> SetFloat;
        public float Min;
        public float Max;
        public bool WholeNumbers;
    }

    private List<SettingRow> BuildSettingsList()
    {
        var list = new List<SettingRow>();

        list.Add(Stepper("Body", () => VRConfig.BodyMode.Value == BodyMode.FullBodyIK ? "Full Body" : "Hands Only",
            () => { VRConfig.BodyMode.Value = NextEnum(VRConfig.BodyMode.Value); HowToFishBody.ApplyBodyMode(); }));
        list.Add(Stepper("Turning", () => VRConfig.Turning.Value.ToString(), () => VRConfig.Turning.Value = NextEnum(VRConfig.Turning.Value)));
        list.Add(Slider("Snap Angle", () => VRConfig.SnapTurnAngle.Value + "°", 15f, 90f, true,
            () => VRConfig.SnapTurnAngle.Value, v => VRConfig.SnapTurnAngle.Value = Mathf.RoundToInt(v)));
        list.Add(Slider("Smooth Speed", () => VRConfig.SmoothTurnSpeed.Value.ToString("F0") + "°/s", 30f, 360f, false,
            () => VRConfig.SmoothTurnSpeed.Value, v => VRConfig.SmoothTurnSpeed.Value = v));
        list.Add(Toggle("Roomscale Movement", () => VRConfig.RoomscaleMovement.Value, v => VRConfig.RoomscaleMovement.Value = v));
        // Real UI Blur has no panel toggle — the game's real frosted blur is ALWAYS used now (the
        // render-order fix in BlurFix makes it render on the world-space panels). It stays only as a hidden
        // config-file fallback (default on) in case a headset needs the flat-tint stand-in.
        list.Add(Stepper("Sprint Mode", () => VRConfig.SprintMode.Value.ToString(), () => VRConfig.SprintMode.Value = NextEnum(VRConfig.SprintMode.Value)));
        list.Add(Stepper("Crouch Mode", () => VRConfig.CrouchMode.Value.ToString(), () => VRConfig.CrouchMode.Value = NextEnum(VRConfig.CrouchMode.Value)));
        list.Add(Toggle("Melee Aim Guide", () => VRConfig.MeleeAimGuide.Value, v => VRConfig.MeleeAimGuide.Value = v));
        list.Add(Slider("HUD Scale", () => VRConfig.HudScale.Value.ToString("0.00"), 0.5f, 2f, false,
            () => VRConfig.HudScale.Value, v => VRConfig.HudScale.Value = v));
        list.Add(Stepper("Death View", () => VRConfig.DeathView.Value == DeathView.FirstPerson ? "First Person" : "Third Person",
            () => VRConfig.DeathView.Value = NextEnum(VRConfig.DeathView.Value)));

        // Action rows: full-width native buttons.
        list.Add(ButtonRow("Calibrate (F3 only)", () => { try { VRCalibration.Instance?.Begin(); } catch { } }));
        list.Add(ButtonRow("Close", Close));

        return list;
    }

    private static SettingRow Stepper(string label, Func<string> value, Action change)
        => new SettingRow { Label = label, Kind = RowKind.Stepper, Value = value, OnPrev = change, OnNext = change };

    private static SettingRow Toggle(string label, Func<bool> get, Action<bool> set)
        => new SettingRow { Label = label, Kind = RowKind.Toggle, GetBool = get, SetBool = set,
                            Value = () => OnOff(get()), OnNext = () => set(!get()) };

    private static SettingRow Slider(string label, Func<string> value, float min, float max, bool whole, Func<float> get, Action<float> set)
        => new SettingRow { Label = label, Kind = RowKind.Slider, Value = value, Min = min, Max = max, WholeNumbers = whole, GetFloat = get, SetFloat = set };

    private static SettingRow ButtonRow(string label, Action onClick)
        => new SettingRow { Label = label, Kind = RowKind.Button, OnClick = onClick };

    /// <summary>
    /// Grab the game's real Toggle + Slider prefabs from the loaded Options screen (ModMenu does the
    /// same). The options screen is instantiated with the main menu, so by the time the user can open
    /// VR Settings it's usually present. Falls back to steppers when missing.
    /// </summary>
    private void DiscoverNativeTemplates()
    {
        if (_templatesScanned) return;
        _templatesScanned = true;
        try
        {
            if (_nativeToggleTemplate == null)
            {
                foreach (var t in UnityEngine.Object.FindObjectsOfType<Toggle>(true))
                {
                    if (t == null || t.name.StartsWith("VRSettings", StringComparison.Ordinal)) continue;
                    if (t.transform.root.name.StartsWith("HowToFishVR", StringComparison.Ordinal)) continue;
                    _nativeToggleTemplate = t.gameObject;
                    break;
                }
            }
            if (_nativeSliderTemplate == null)
            {
                foreach (var s in UnityEngine.Object.FindObjectsOfType<Slider>(true))
                {
                    if (s == null || s.name.StartsWith("VRSettings", StringComparison.Ordinal)) continue;
                    if (s.transform.root.name.StartsWith("HowToFishVR", StringComparison.Ordinal)) continue;
                    _nativeSliderTemplate = s.gameObject;
                    break;
                }
            }
        }
        catch { }
    }

    // ---- Native style helpers (ported from ModMenu) ----

    /// <summary>Clone a game button (template), strip game wiring, rewire to our action, set text.</summary>
    private static GameObject CreateStyledButton(GameObject template, Transform parent, string text, UnityAction action)
    {
        if (template == null) throw new InvalidOperationException("No native button template (the game's Options button was not found).");
        var clone = Instantiate(template, parent, false);
        clone.name = "VRSettingsButton_" + text;
        clone.SetActive(false);
        DisableLocalization(clone);
        var btn = clone.GetComponent<Button>();
        if (btn == null) throw new InvalidOperationException("Native Options template has no Button component.");
        btn.onClick = new Button.ButtonClickedEvent();
        btn.onClick.AddListener(action);
        btn.navigation = new Navigation { mode = Navigation.Mode.None };
        var uib = clone.GetComponent<UIButton>();
        if (uib != null) uib._selectOnSubmit = null; // don't let submit re-select a game object
        SetButtonText(clone, text);
        clone.SetActive(true);
        return clone;
    }

    private static void DisableLocalization(GameObject clone)
    {
        var comps = clone.GetComponentsInChildren<Component>(true);
        foreach (var c in comps)
        {
            if (c == null) continue;
            var t = c.GetType();
            string n = t.FullName ?? t.Name;
            if (n.IndexOf("Localiz", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var b = c as Behaviour;
                if (b != null) b.enabled = false;
            }
        }
    }

    private static void SetButtonText(GameObject button, string value)
    {
        if (button == null) return;
        var txt = button.GetComponentInChildren<TextMeshProUGUI>(true);
        if (txt != null) txt.text = value;
    }

    private static TextMeshProUGUI FindMenuText(Transform root, string value)
    {
        if (root == null) return null;
        foreach (var t in root.GetComponentsInChildren<TextMeshProUGUI>(true))
            if (t != null && string.Equals((t.text ?? "").Trim(), value, StringComparison.OrdinalIgnoreCase))
                return t;
        return null;
    }

    /// <summary>Insert clone right after the source button (layout-aware; shifts siblings down if no layout group).</summary>
    private static void InsertAfter(RectTransform source, RectTransform clone)
    {
        clone.SetSiblingIndex(source.GetSiblingIndex() + 1);
        if (source.parent != null && source.parent.GetComponent<LayoutGroup>() != null) return;
        float h = Math.Max(44f, Math.Abs(source.rect.height) * 1.12f);
        var pos = source.anchoredPosition;
        pos.y -= h;
        clone.anchoredPosition = pos;
        if (source.parent == null) return;
        foreach (var other in source.parent.GetComponentsInChildren<RectTransform>(true))
        {
            if (other == null) continue;
            if (other.parent == source.parent && other != source && other != clone && other.anchoredPosition.y < source.anchoredPosition.y)
            {
                var p2 = other.anchoredPosition;
                p2.y -= h;
                other.anchoredPosition = p2;
            }
        }
    }

    private TextMeshProUGUI CreateText(string name, Transform parent, string value, float size, TextAlignmentOptions alignment)
    {
        TextMeshProUGUI templateText = null;
        if (_buttonTemplate != null) templateText = _buttonTemplate.GetComponentInChildren<TextMeshProUGUI>(true);
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        var t = go.GetComponent<TextMeshProUGUI>();
        t.text = value;
        t.fontSize = size;
        t.alignment = alignment;
        t.enableWordWrapping = true;
        t.raycastTarget = false;
        if (templateText != null)
        {
            t.font = templateText.font;
            t.fontSharedMaterial = templateText.fontSharedMaterial;
            t.color = templateText.color;
            t.outlineColor = templateText.outlineColor;
            t.outlineWidth = templateText.outlineWidth;
        }
        return t;
    }

    /// <summary>Find the biggest full-screen non-button Image in the host — the native page backdrop.</summary>
    private void DiscoverBackdropStyle(Transform root)
    {
        if (root == null || _nativeBackdropStyle != null) return;
        Image best = null;
        float bestScore = 0f;
        foreach (var img in root.GetComponentsInChildren<Image>(true))
        {
            if (img == null) continue;
            if (img.GetComponentInParent<Button>(true) != null) continue;
            var r = img.rectTransform.rect;
            float w = Mathf.Abs(r.width);
            float h = Mathf.Abs(r.height);
            if (w < 400f || h < 280f) continue;
            float score = w * h + img.color.a * 10000f;
            if (score > bestScore) { bestScore = score; best = img; }
        }
        _nativeBackdropStyle = best;
    }

    private static void CopyImageStyle(Image source, Image target)
    {
        if (source == null || target == null) return;
        target.sprite = source.sprite;
        target.overrideSprite = source.overrideSprite;
        target.material = source.material;
        target.color = source.color;
        target.type = source.type;
        target.preserveAspect = source.preserveAspect;
        target.fillCenter = source.fillCenter;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    private static void SetRect(RectTransform rect, Vector2 min, Vector2 max, Vector2 offsetMin, Vector2 offsetMax)
    {
        rect.anchorMin = min;
        rect.anchorMax = max;
        rect.offsetMin = offsetMin;
        rect.offsetMax = offsetMax;
    }

    private static string OnOff(bool b) => b ? "On" : "Off";

    private static T NextEnum<T>(T cur) where T : Enum
    {
        var vals = (T[])Enum.GetValues(typeof(T));
        int i = Array.IndexOf(vals, cur);
        return vals[(i + 1) % vals.Length];
    }

    private static int NextIn(int[] set, int cur)
    {
        for (int i = 0; i < set.Length; i++) if (set[i] == cur) return set[(i + 1) % set.Length];
        return set[set.Length / 2];
    }

    private static float NextIn(float[] set, float cur)
    {
        for (int i = 0; i < set.Length; i++) if (Mathf.Approximately(set[i], cur)) return set[(i + 1) % set.Length];
        return set[set.Length / 2];
    }
}
