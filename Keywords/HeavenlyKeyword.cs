#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

public sealed class HeavenlyKeyword : LoadoutKeywordModel
{
    public const string EnergyVar = "LoadoutHeavenlyEnergy";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                EnergyVar,
                4m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_HEAVENLY_ENERGY")
        ];

    public static HeavenlyKeyword Instance { get; } = new();

    private HeavenlyKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.Heavenly;

    public override string StorageKey => LoadoutKeywords.HeavenlyKey;

    public override string TitleLocKey => "LOADOUT-HEAVENLY.title";

    public override LoadoutKeywordPresentation Presentation =>
        LoadoutKeywordPresentation.DescriptionOnly;

    public override string? CardTextLocKey => "LOADOUT-HEAVENLY.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.ResolveEnergyXValue))]
internal static class HeavenlyResolveEnergyXValuePatch
{
    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, ref int __result)
    {
        if (XValueKeywordRuntime.TryGetValue(__instance, out _))
            return;
        bool hasXValue = LoadoutKeywords.Has(__instance, LoadoutKeywords.XValue);
        if (LoadoutKeywords.Has(__instance, LoadoutKeywords.AltHeavenly)
            && (hasXValue || LoadoutKeywordRegistry.TryGetValue(
                __instance,
                AltHeavenlyKeyword.EnergyVar,
                out DynamicVar altEnergyVar)
            && __result >= altEnergyVar.IntValue))
        {
            __result = AltHeavenlyKeyword.Factorialize(__result);
            CardEffectAnimationScope.MarkAltHeavenlyResult(
                __instance,
                __result);
            return;
        }

        if (LoadoutKeywords.Has(__instance, LoadoutKeywords.Heavenly)
            && (hasXValue || LoadoutKeywordRegistry.TryGetValue(
                __instance,
                HeavenlyKeyword.EnergyVar,
                out DynamicVar energyVar)
            && __result >= energyVar.IntValue))
        {
            __result *= 2;
        }
    }
}
