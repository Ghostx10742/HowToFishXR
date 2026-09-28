using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using HowToFishVR.VR;
using UnityEngine;

namespace HowToFishVR.Patches;

// Unity's Transform.forward getter uses rotation and discards negative scale. The barrel
// and laser origin already move with the reflected hierarchy; use that same matrix for
// their direction so shooting still follows the visible muzzle, including animated bones.
[HarmonyPatch]
internal static class MirroredItemAimPatches
{
    [HarmonyTargetMethods]
    private static IEnumerable<MethodBase> Methods()
    {
        yield return AccessTools.Method(typeof(Weapon), "Shoot");
        yield return AccessTools.Method(typeof(Weapon), "GunClippingRay");
        yield return AccessTools.Method(typeof(LaserSight), "SetLine");
    }

    internal static Vector3 Forward(Transform transform) => HeldItemMirror.Contains(transform)
        ? transform.TransformVector(Vector3.forward).normalized : transform.forward;

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> UseReflectedDirection(IEnumerable<CodeInstruction> instructions)
    {
        var original = AccessTools.PropertyGetter(typeof(Transform), "forward");
        var replacement = AccessTools.Method(typeof(MirroredItemAimPatches), nameof(Forward));
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(original))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
            }
            yield return instruction;
        }
    }
}
