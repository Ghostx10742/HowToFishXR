using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using HowToFishVR;
using HowToFishVR.Input;
using HowToFishVR.VR;
using UnityEngine;
using UnityEngine.InputSystem;

// Test-only plugin: never included in the release overlay. Opt in with HTF_TEST_OUTPUT.
[BepInPlugin("local.howtofishxr.handedness-smoke", "Handedness smoke tests", "1.0.0")]
[BepInDependency(Plugin.Guid)]
public class HandednessSmoke : BaseUnityPlugin
{
    private string output;
    private int failures;
    private void Awake()
    {
        output = Environment.GetEnvironmentVariable("HTF_TEST_OUTPUT");
        if (!string.IsNullOrEmpty(output)) StartCoroutine(Run());
    }
    private void Check(bool pass, string name)
    {
        File.AppendAllText(output, (pass ? "PASS " : "FAIL ") + name + "\n");
        if (!pass) failures++;
    }
    private static MethodInfo Method(string type, string name) => typeof(Plugin).Assembly.GetType(type)
        .GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    private void Binding(InputAction a, string side, string control) => Check(
        a.bindings.Count == 1 && a.bindings[0].effectivePath == "<XRController>{" + side + "Hand}/" + control, a.name);
    private IEnumerator Run()
    {
        yield return new WaitForSecondsRealtime(15f);
        try { Test(); }
        catch (Exception e) { Check(false, e.ToString()); }
        if (Plugin.HeadsetActive)
        {
            try { TestInputRouting(); }
            catch (Exception e) { Check(false, e.ToString()); }
        }
        File.AppendAllText(output, "RESULT failures=" + failures + "\n");
        Application.Quit();
    }
    private void TestInputRouting()
    {
        // Test at Unity's input boundary. xr-sim currently collapses shared OpenXR actions'
        // left/right subaction paths, so it cannot supply independent Unity controller values.
        InputSystem.RegisterLayout(@"{""name"":""XRHandednessTestController"",""extend"":""XRController"",
            ""controls"":[{""name"":""trigger"",""layout"":""Axis""},
            {""name"":""grip"",""layout"":""Axis""},{""name"":""thumbstick"",""layout"":""Stick""}]}");
        var devices = InputSystem.devices.Where(d => d is UnityEngine.InputSystem.XR.XRController && d.enabled).ToArray();
        InputDevice l = null, r = null;
        try
        {
            foreach (var device in devices) InputSystem.DisableDevice(device);
            l = InputSystem.AddDevice("XRHandednessTestController");
            r = InputSystem.AddDevice("XRHandednessTestController");
            InputSystem.SetDeviceUsage(l, UnityEngine.InputSystem.CommonUsages.LeftHand);
            InputSystem.SetDeviceUsage(r, UnityEngine.InputSystem.CommonUsages.RightHand);
            UnityEngine.InputSystem.LowLevel.InputState.Change((UnityEngine.InputSystem.Controls.AxisControl)l["trigger"], 0.1f);
            UnityEngine.InputSystem.LowLevel.InputState.Change((UnityEngine.InputSystem.Controls.AxisControl)r["trigger"], 0.2f);
            UnityEngine.InputSystem.LowLevel.InputState.Change((UnityEngine.InputSystem.Controls.AxisControl)l["grip"], 0.2f);
            UnityEngine.InputSystem.LowLevel.InputState.Change((UnityEngine.InputSystem.Controls.AxisControl)r["grip"], 0.4f);
            UnityEngine.InputSystem.LowLevel.InputState.Change((UnityEngine.InputSystem.Controls.StickControl)l["thumbstick"], new Vector2(0.4f,0.3f));
            UnityEngine.InputSystem.LowLevel.InputState.Change((UnityEngine.InputSystem.Controls.StickControl)r["thumbstick"], new Vector2(-0.4f,-0.3f));
            var actions = VRActions.Instance;
            bool left = VRConfig.IsLeftHanded;
            Check(Mathf.Abs(actions.MainTrigger.ReadValue<float>() - (left ? 0.1f : 0.2f)) < 0.01f, "Unity main trigger routing");
            Check(Mathf.Abs(actions.OffHandGrip - (left ? 0.4f : 0.2f)) < 0.01f, "Unity support grip routing");
            Check(Mathf.Abs(actions.TurnAxis.ReadValue<float>()) > 0.05f && Mathf.Sign(actions.TurnAxis.ReadValue<float>()) == (left ? 1f : -1f), "Unity turning stick routing");
            Check(actions.MoveAxis.ReadValue<Vector2>().magnitude > 0.05f && Mathf.Sign(actions.MoveAxis.ReadValue<Vector2>().x) == (left ? -1f : 1f), "Unity movement stick routing");
        }
        finally
        {
            if (l != null) InputSystem.RemoveDevice(l);
            if (r != null) InputSystem.RemoveDevice(r);
            foreach (var device in devices) InputSystem.EnableDevice(device);
        }
    }

    private void Test()
    {
        bool left = VRConfig.IsLeftHanded;
        bool flat = Environment.GetCommandLineArgs().Contains("--disable-vr");
        if (left && !flat)
        {
            HeldItemMirrorTests.Run(Check);
            MirroredPhysicsTests.Run(Check);
        }
        Check(VRConfig.LeftHanded.Value == left, "saved mode applied at startup");
        Check(Plugin.VREnabled == !flat, "VR startup mode");
        if (flat)
        {
            Check(VRRig.Instance == null, "flat-screen has no local VR rig");
            Check(!GameInfo.Input.actions.SelectMany(a => a.bindings).Any(b => (b.path ?? "").StartsWith("<XRController>")), "flat-screen gameplay bindings untouched");
            return;
        }
        Check(Plugin.HeadsetActive, "simulated headset active");
        string main = left ? "Left" : "Right", off = left ? "Right" : "Left";
        var a = VRActions.Instance;
        Binding(a.TurnAxis, main, "thumbstick/x");
        Binding(a.TurnAxisY, main, "thumbstick/y");
        Binding(a.MoveAxis, off, "thumbstick");
        Binding(a.SprintButton, off, "thumbstickClicked");
        Binding(a.CrouchButton, main, "thumbstickClicked");
        Binding(a.MainTrigger, main, "trigger");
        Binding(a.LeftPosition, "Left", "devicePosition");
        Binding(a.RightPosition, "Right", "devicePosition");
        var asset = GameInfo.Input.actions;
        var original = asset.SelectMany(x => x.bindings).Where(b => !(b.path ?? "").StartsWith("<XRController>"))
            .Select(b => b.id.ToString() + b.path + b.interactions).ToArray();
        var inject = Method("HowToFishVR.Input.VRBindings", "TryInject");
        Check((bool)inject.Invoke(null, null), "inject gameplay bindings");
        foreach (var row in new[] {
            ("PlayerMove", off, "thumbstick"), ("PlayerRightClick", off, "trigger"),
            ("PlayerPickUp", main, "grip"), ("PlayerJump", main, "primaryButton"),
            ("WeaponReload", off, "primaryButton"), ("InventoryScroll", main, "thumbstick/y"),
            ("Pause", off, "secondaryButton"), ("PlayerLeftClick", main, "trigger"),
            ("PlayerDrop", main, "secondaryButton"), ("ChangeBait", main, "secondaryButton"),
            ("WeaponInspect", off, "grip") })
        {
            var action = asset.FindAction(row.Item1);
            var paths = action.bindings.Where(b => (b.path ?? "").StartsWith("<XRController>")).ToArray();
            Check(paths.Length == 1 && paths[0].effectivePath == "<XRController>{" + row.Item2 + "Hand}/" + row.Item3, row.Item1);
        }
        int count = asset.SelectMany(x => x.bindings).Count();
        Method("HowToFishVR.Input.VRBindings", "Reset").Invoke(null, null);
        inject.Invoke(null, null);
        Check(count == asset.SelectMany(x => x.bindings).Count(), "reinjection creates no duplicates");
        Check(original.SequenceEqual(asset.SelectMany(x => x.bindings).Where(b => !(b.path ?? "").StartsWith("<XRController>"))
            .Select(b => b.id.ToString() + b.path + b.interactions)), "original game bindings preserved");
        var rig = VRRig.Instance;
        Check(rig.MainHand == (left ? rig.LeftHand : rig.RightHand), "main controller role");
        Check(rig.OffHand == (left ? rig.RightHand : rig.LeftHand), "support controller role");
        object[] aim = { Vector3.zero, Vector3.zero };
        Check((bool)Method("HowToFishVR.Patches.PunchPatches", "TryGetMainHandAim").Invoke(null, aim), "melee aim available");
        var expected = (Vector3)Method("HowToFishVR.Patches.PlayerHandsPatches", "CalibratedHandPosition")
            .Invoke(null, new object[] { rig.MainHand, left });
        Check(Vector3.Distance((Vector3)aim[0], expected) < 0.0001f, "melee origin uses calibrated main hand");
        Check(Vector3.Distance((Vector3)aim[1], rig.MainHand.rotation * Quaternion.Euler(45, 0, 0) * Vector3.forward) < 0.0001f, "melee direction uses main hand");
        VRConfig.LeftHanded.Value = !left;
        Check(VRConfig.IsLeftHanded == left, "changing setting defers roles until restart");
        VRConfig.LeftHanded.Value = left;
    }
}
