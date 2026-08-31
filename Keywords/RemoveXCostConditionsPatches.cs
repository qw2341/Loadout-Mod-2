#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

internal static class RemoveXCostConditionTranspiler
{
    private static readonly MethodInfo CostsXGetter =
        AccessTools.PropertyGetter(
            typeof(CardEnergyCost),
            nameof(CardEnergyCost.CostsX));

    public static IEnumerable<CodeInstruction> Apply(
        IEnumerable<CodeInstruction> instructions,
        string targetName)
    {
        List<CodeInstruction> patched = new(instructions);
        for (int index = 0; index < patched.Count - 1; index++)
        {
            if (!patched[index].Calls(CostsXGetter)
                || (patched[index + 1].opcode != OpCodes.Brtrue
                    && patched[index + 1].opcode != OpCodes.Brtrue_S))
            {
                continue;
            }

            patched[index].opcode = OpCodes.Pop;
            patched[index].operand = null;
            patched.Insert(index + 1, new CodeInstruction(OpCodes.Ldc_I4_1));
            return patched;
        }

        throw new MissingMethodException(
            $"Could not locate {targetName}'s X-cost guard.");
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.ResolveEnergyXValue))]
internal static class RemoveResolveEnergyXValueXCostConditionPatch
{
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions)
    {
        return RemoveXCostConditionTranspiler.Apply(
            instructions,
            "CardModel.ResolveEnergyXValue");
    }
}

[HarmonyPatch(
    typeof(CardEnergyCost),
    nameof(CardEnergyCost.CapturedXValue),
    MethodType.Getter)]
internal static class RemoveCapturedXValueGetterXCostConditionPatch
{
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions)
    {
        return RemoveXCostConditionTranspiler.Apply(
            instructions,
            "CardEnergyCost.CapturedXValue getter");
    }
}

[HarmonyPatch(
    typeof(CardEnergyCost),
    nameof(CardEnergyCost.CapturedXValue),
    MethodType.Setter)]
internal static class RemoveCapturedXValueSetterXCostConditionPatch
{
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions)
    {
        return RemoveXCostConditionTranspiler.Apply(
            instructions,
            "CardEnergyCost.CapturedXValue setter");
    }
}
