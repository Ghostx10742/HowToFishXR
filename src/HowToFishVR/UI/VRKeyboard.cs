using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace HowToFishVR.UI;

/// <summary>
/// In-headset keyboard ported from Schedule 1 VR's proven code-built keyboard. It opens when the
/// right-hand pointer selects a TMP input field and writes directly at that field's caret.
/// </summary>
public sealed class VRKeyboard : MonoBehaviour
{
    public static VRKeyboard Instance { get; private set; }
    public bool IsOpen => _canvas != null && _canvas.gameObject.activeSelf;

    private static readonly string[][] Rows =
    {
        new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" },
        new[] { "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P" },
        new[] { "A", "S", "D", "F", "G", "H", "J", "K", "L" },
        new[] { "Z", "X", "C", "V", "B", "N", "M" },
    };

    private static readonly string[] ShiftedDigits = { "!", "@", "#", "$", "%", "^", "&", "*", "(", ")" };

    private Canvas _canvas;
    private GameObject _panel;
    private object _targetInputField;
    private Type _tmpInputFieldType;
    private PropertyInfo _tmpTextProperty;
    private PropertyInfo _tmpCaretProperty;
    private MethodInfo _tmpActivateMethod;
    private FieldInfo _tmpOnSubmitField;
    private PropertyInfo _tmpOnEndEditProperty;
    private GameObject _watchedCanvas;
    private bool _shifted;
    private bool _capsLock;
    private readonly List<Text> _keyLabels = new List<Text>();

    public static void Create()
    {
        if (Instance != null) return;
        var go = new GameObject("HowToFishVR Keyboard");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<VRKeyboard>();
    }

    private void Awake()
    {
        Instance = this;
        FindInputFieldType();
        BuildKeyboard();
        _canvas.gameObject.SetActive(false);
        Application.onBeforeRender += Reposition;
    }

    private void OnDestroy()
    {
        Application.onBeforeRender -= Reposition;
        if (Instance == this) Instance = null;
    }

    private void FindInputFieldType()
    {
        const BindingFlags instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                _tmpInputFieldType = assembly.GetType("TMPro.TMP_InputField");
                if (_tmpInputFieldType == null) continue;
                _tmpTextProperty = _tmpInputFieldType.GetProperty("text", instance);
                _tmpCaretProperty = _tmpInputFieldType.GetProperty("caretPosition", instance);
                _tmpActivateMethod = _tmpInputFieldType.GetMethod("ActivateInputField", instance);
                _tmpOnSubmitField = _tmpInputFieldType.GetField("m_OnSubmit", instance);
                _tmpOnEndEditProperty = _tmpInputFieldType.GetProperty("onEndEdit", instance);
                return;
            }
            catch { }
        }
    }

    private void BuildKeyboard()
    {
        var canvasObject = new GameObject("HowToFishVR Keyboard Canvas", typeof(RectTransform));
        canvasObject.transform.SetParent(transform, false);
        canvasObject.layer = 5;

        _canvas = canvasObject.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.WorldSpace;
        _canvas.overrideSorting = true;
        _canvas.sortingOrder = 32000;
        canvasObject.AddComponent<CanvasScaler>();
        var raycaster = canvasObject.AddComponent<GraphicRaycaster>();
        raycaster.ignoreReversedGraphics = false;
        raycaster.blockingObjects = GraphicRaycaster.BlockingObjects.None;

        var canvasRect = (RectTransform)canvasObject.transform;
        canvasRect.sizeDelta = new Vector2(800f, 380f);
        canvasRect.localScale = Vector3.one * 0.001f;

        _panel = new GameObject("Keyboard Panel", typeof(RectTransform));
        _panel.transform.SetParent(canvasObject.transform, false);
        _panel.layer = 5;
        var panelRect = (RectTransform)_panel.transform;
        panelRect.anchorMin = Vector2.zero;
        panelRect.anchorMax = Vector2.one;
        panelRect.offsetMin = Vector2.zero;
        panelRect.offsetMax = Vector2.zero;
        var panelImage = _panel.AddComponent<Image>();
        panelImage.color = new Color(0.08f, 0.08f, 0.12f, 0.95f);
        // Catch the pointer between individual keys so the keyboard-only aiming beam remains visible
        // across the entire board. Child buttons still sort above this background and receive clicks.
        panelImage.raycastTarget = true;

        const float keyWidth = 65f;
        const float keyHeight = 55f;
        const float gap = 5f;
        const float startY = 100f;

        for (int row = 0; row < Rows.Length; row++)
        {
            float rowWidth = Rows[row].Length * (keyWidth + gap) - gap;
            float startX = -rowWidth * 0.5f + keyWidth * 0.5f;
            for (int column = 0; column < Rows[row].Length; column++)
            {
                string key = Rows[row][column];
                float x = startX + column * (keyWidth + gap);
                float y = startY - row * (keyHeight + gap);
                _keyLabels.Add(CreateKey(key, new Vector2(x, y), new Vector2(keyWidth, keyHeight), () => TypeCharacter(key)));
            }
        }

        float bottomY = startY - 4f * (keyHeight + gap);
        CreateKey("SHIFT", new Vector2(-280f, bottomY), new Vector2(90f, keyHeight), ToggleShift,
            new Color(0.3f, 0.3f, 0.5f));
        CreateKey("SPACE", new Vector2(-50f, bottomY), new Vector2(250f, keyHeight), () => TypeCharacter(" "),
            new Color(0.2f, 0.2f, 0.3f));
        CreateKey("BACK", new Vector2(165f, bottomY), new Vector2(90f, keyHeight), Backspace,
            new Color(0.5f, 0.2f, 0.2f));
        CreateKey("ENTER", new Vector2(300f, bottomY), new Vector2(120f, keyHeight), Enter,
            new Color(0.2f, 0.5f, 0.3f));

        CreateKey("-", new Vector2(350f, startY), new Vector2(keyWidth, keyHeight), () => TypeCharacter("-"));
        CreateKey(".", new Vector2(350f, startY - keyHeight - gap), new Vector2(keyWidth, keyHeight), () => TypeCharacter("."));
        CreateKey("/", new Vector2(350f, startY - 2f * (keyHeight + gap)), new Vector2(keyWidth, keyHeight), () => TypeCharacter("/"));
        CreateKey("_", new Vector2(350f, startY - 3f * (keyHeight + gap)), new Vector2(keyWidth, keyHeight), () => TypeCharacter("_"));

        // Schedule 1 VR's keyboard did not include a way to dismiss it. Keep the same layout and add
        // an explicit non-submitting Close key above it so closing never types or confirms anything.
        CreateKey("CLOSE", new Vector2(0f, 165f), new Vector2(130f, 42f), Close,
            new Color(0.45f, 0.16f, 0.16f));

        SetLayerRecursively(canvasObject, 5);
        VRUIManager.RegisterLaserCanvas(_canvas);
    }

    private Text CreateKey(string label, Vector2 position, Vector2 size, Action onClick, Color? background = null)
    {
        var keyObject = new GameObject("Key " + label, typeof(RectTransform));
        keyObject.transform.SetParent(_panel.transform, false);
        keyObject.layer = 5;
        var rect = (RectTransform)keyObject.transform;
        rect.anchoredPosition = position;
        rect.sizeDelta = size;

        var image = keyObject.AddComponent<Image>();
        image.color = background ?? new Color(0.18f, 0.18f, 0.25f, 1f);
        var button = keyObject.AddComponent<Button>();
        var colors = button.colors;
        colors.highlightedColor = new Color(0.4f, 0.4f, 0.6f);
        colors.pressedColor = new Color(0.5f, 0.5f, 0.8f);
        button.colors = colors;
        button.onClick.AddListener(() => onClick());

        var textObject = new GameObject("Text", typeof(RectTransform));
        textObject.transform.SetParent(keyObject.transform, false);
        textObject.layer = 5;
        var textRect = (RectTransform)textObject.transform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;
        var text = textObject.AddComponent<Text>();
        text.text = label;
        text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        text.fontSize = 22;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;
        text.raycastTarget = false;
        return text;
    }

    internal bool TryOpenFor(GameObject selected)
    {
        if (_tmpInputFieldType == null || selected == null) return false;
        Transform current = selected.transform;
        while (current != null)
        {
            var input = current.GetComponent(_tmpInputFieldType);
            if (input != null)
            {
                Show(input);
                return true;
            }
            current = current.parent;
        }

        // Some menu prefabs put the raycastable background on a wrapper above the actual input field.
        // The parent walk handles TMP's normal Text Area hierarchy; this fallback handles those wrappers.
        try
        {
            var input = selected.GetComponentInChildren(_tmpInputFieldType, true);
            if (input != null)
            {
                Show(input);
                return true;
            }
        }
        catch { }
        return false;
    }

    internal bool Owns(GameObject target)
    {
        if (target == null || _canvas == null) return false;
        return target.transform == _canvas.transform || target.transform.IsChildOf(_canvas.transform);
    }

    private void Show(object inputField)
    {
        if (_tmpInputFieldType == null || inputField == null || !_tmpInputFieldType.IsInstanceOfType(inputField)) return;

        _targetInputField = inputField;
        _canvas.gameObject.SetActive(true);
        try { _tmpActivateMethod?.Invoke(_targetInputField, null); } catch { }

        try
        {
            var component = inputField as Component;
            var parentCanvas = component != null ? component.GetComponentInParent<Canvas>() : null;
            if (parentCanvas != null && parentCanvas.gameObject != _watchedCanvas)
            {
                _watchedCanvas = parentCanvas.gameObject;
                var watcher = _watchedCanvas.GetComponent<CanvasCloseWatcher>();
                if (watcher == null) watcher = _watchedCanvas.AddComponent<CanvasCloseWatcher>();
                watcher.Keyboard = this;
                watcher.Canvas = _watchedCanvas;
            }
        }
        catch { }

        Reposition();
        _canvas.worldCamera = FindViewCamera();
    }

    /// <summary>
    /// Keep the keyboard at the same comfortable head-relative position. This must run after the camera
    /// poser's render-time menu-boat update: a one-time world position gets left behind as the sailing
    /// main menu moves, while this absolute placement follows without accumulating drift.
    /// </summary>
    private void Reposition()
    {
        if (!IsOpen) return;
        Transform head = VR.VRRig.Instance != null ? VR.VRRig.Instance.Head : null;
        if (head != null)
        {
            Vector3 forward = head.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            forward.Normalize();
            Vector3 position = head.position + forward * 0.8f;
            position.y = head.position.y - 0.6f;
            _canvas.transform.SetPositionAndRotation(
                position,
                Quaternion.LookRotation(forward, Vector3.up) * Quaternion.Euler(30f, 0f, 0f));
        }
        _canvas.worldCamera = FindViewCamera();
    }

    public void Close()
    {
        if (_canvas != null) _canvas.gameObject.SetActive(false);
        _targetInputField = null;
        _watchedCanvas = null;
        _shifted = false;
        _capsLock = false;
        UpdateKeyLabels();
    }

    private void TypeCharacter(string character)
    {
        if (_targetInputField == null) return;
        string value = character;
        if (character.Length == 1 && char.IsLetter(character[0]))
            value = (_shifted || _capsLock) ? character.ToUpperInvariant() : character.ToLowerInvariant();
        else if (_shifted && character.Length == 1 && char.IsDigit(character[0]))
            value = ShiftedDigits[character[0] - '1' >= 0 ? character[0] - '1' : 9];

        try
        {
            string current = _tmpTextProperty?.GetValue(_targetInputField) as string ?? string.Empty;
            int caret = (int)(_tmpCaretProperty?.GetValue(_targetInputField) ?? current.Length);
            caret = Mathf.Clamp(caret, 0, current.Length);
            _tmpTextProperty?.SetValue(_targetInputField, current.Insert(caret, value));
            _tmpCaretProperty?.SetValue(_targetInputField, caret + value.Length);
        }
        catch { }

        if (_shifted && !_capsLock)
        {
            _shifted = false;
            UpdateKeyLabels();
        }
    }

    private void Backspace()
    {
        if (_targetInputField == null) return;
        try
        {
            string current = _tmpTextProperty?.GetValue(_targetInputField) as string ?? string.Empty;
            int caret = (int)(_tmpCaretProperty?.GetValue(_targetInputField) ?? current.Length);
            caret = Mathf.Clamp(caret, 0, current.Length);
            if (caret <= 0) return;
            _tmpTextProperty?.SetValue(_targetInputField, current.Remove(caret - 1, 1));
            _tmpCaretProperty?.SetValue(_targetInputField, caret - 1);
        }
        catch { }
    }

    private void Enter()
    {
        if (_targetInputField == null) return;
        try
        {
            string text = _tmpTextProperty?.GetValue(_targetInputField) as string ?? string.Empty;
            InvokeStringEvent(_tmpOnSubmitField?.GetValue(_targetInputField), text);
            InvokeStringEvent(_tmpOnEndEditProperty?.GetValue(_targetInputField), text);
        }
        catch { }
        Close();
    }

    private static void InvokeStringEvent(object eventObject, string value)
    {
        try { eventObject?.GetType().GetMethod("Invoke", new[] { typeof(string) })?.Invoke(eventObject, new object[] { value }); }
        catch { }
    }

    private void ToggleShift()
    {
        if (_shifted)
        {
            _capsLock = !_capsLock;
            _shifted = _capsLock;
        }
        else
        {
            _shifted = true;
        }
        UpdateKeyLabels();
    }

    private void UpdateKeyLabels()
    {
        int index = 0;
        for (int row = 0; row < Rows.Length; row++)
        {
            for (int column = 0; column < Rows[row].Length; column++)
            {
                if (index >= _keyLabels.Count) return;
                if (row == 0)
                    _keyLabels[index].text = (_shifted || _capsLock) ? ShiftedDigits[column] : Rows[row][column];
                else
                    _keyLabels[index].text = (_shifted || _capsLock)
                        ? Rows[row][column].ToUpperInvariant()
                        : Rows[row][column].ToLowerInvariant();
                index++;
            }
        }
    }

    private void CloseIfWatching(GameObject canvas)
    {
        if (canvas == _watchedCanvas && IsOpen) Close();
    }

    private static Camera FindViewCamera()
    {
        try { if (GameInfo.CurCamera != null) return GameInfo.CurCamera; } catch { }
        if (Camera.main != null) return Camera.main;
        try { if (MainMenuManager.MenuCam != null) return MainMenuManager.MenuCam; } catch { }
        foreach (var camera in Camera.allCameras)
            if (camera != null && camera.isActiveAndEnabled && camera.targetTexture == null) return camera;
        return null;
    }

    private static void SetLayerRecursively(GameObject root, int layer)
    {
        root.layer = layer;
        foreach (Transform child in root.transform) SetLayerRecursively(child.gameObject, layer);
    }

    private sealed class CanvasCloseWatcher : MonoBehaviour
    {
        internal VRKeyboard Keyboard;
        internal GameObject Canvas;

        private void OnDisable()
        {
            if (Keyboard != null) Keyboard.CloseIfWatching(Canvas);
        }
    }
}
