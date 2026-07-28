#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

public sealed class XCostKeyword : LoadoutKeywordModel
{
    public static XCostKeyword Instance { get; } = new();

    private XCostKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.XCost;

    public override string StorageKey => LoadoutKeywords.XCostKey;

    public override string TitleLocKey => "LOADOUT-X_COST.title";
}

public static class XCostKeywordMechanics
{
    private static readonly FieldInfo? EnergyCostField =
        AccessTools.Field(typeof(CardModel), "_energyCost");

    public static void SynchronizeEnergyCost(
        CardModel card,
        IReadOnlyDictionary<string, bool> overrides,
        int? modifiedCost)
    {
        bool enabled = overrides.TryGetValue(LoadoutKeywords.XCostKey, out bool requested)
            ? requested
            : LoadoutKeywords.Has(card, LoadoutKeywords.XCost);

        CardModel? canonical =
            ModelDb.AllCards.FirstOrDefault(candidate => candidate.Id.Equals(card.Id));
        bool canonicalCostsX = canonical?.EnergyCost.CostsX ?? false;
        bool explicitlyDisabled =
            overrides.TryGetValue(LoadoutKeywords.XCostKey, out requested) && !requested;
        bool shouldCostX = enabled || (canonicalCostsX && !explicitlyDisabled);

        if (card.EnergyCost.CostsX == shouldCostX)
        {
            if (!shouldCostX && modifiedCost.HasValue)
                card.EnergyCost.SetCustomBaseCost(modifiedCost.Value);
            return;
        }

        if (EnergyCostField is null)
            throw new MissingFieldException(typeof(CardModel).FullName, "_energyCost");

        int normalCost = modifiedCost
                         ?? (canonicalCostsX ? 0 : canonical?.EnergyCost.Canonical)
                         ?? card.EnergyCost.Canonical;
        EnergyCostField.SetValue(
            card,
            new CardEnergyCost(card, shouldCostX ? 0 : normalCost, shouldCostX));
        card.InvokeEnergyCostChanged();
    }
}

public static class XCostPlayCountPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(CardModel),
            "GeneratePlayCount",
            [
                typeof(MegaCrit.Sts2.Core.Combat.ICombatState),
                typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature)
            ]);
    }

    [HarmonyPostfix]
    public static void Postfix(CardModel __instance, ref Task<int> __result)
    {
        if (LoadoutKeywords.Has(__instance, LoadoutKeywords.XCost))
            __result = MultiplyByXAsync(__instance, __result);
    }

    private static async Task<int> MultiplyByXAsync(CardModel card, Task<int> original)
    {
        int nativePlayCount = await original;
        int x = Math.Max(0, card.ResolveEnergyXValue());
        return checked(nativePlayCount * x);
    }
}
