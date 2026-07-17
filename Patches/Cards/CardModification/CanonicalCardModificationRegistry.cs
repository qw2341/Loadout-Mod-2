#nullable enable

namespace Loadout.Patches.Cards.CardModification;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Loadout.Keywords;
using Loadout.Services.CardModification;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

/// <summary>
/// Pristine canonical data plus the one-shot reconciler that writes permanent
/// definitions into ModelDb. No mutable/live card references are retained.
/// </summary>
internal sealed class CanonicalCardBaseline
{
    public required int EnergyCost { get; init; }
    public required bool CostsX { get; init; }
    public required int BaseReplayCount { get; init; }
    public required int BaseStarCost { get; init; }
    public required Dictionary<string, decimal> DynamicVars { get; init; }
    public required CardPoolModel Pool { get; init; }
    public required CardType Type { get; init; }
    public required CardRarity Rarity { get; init; }
    public required HashSet<CardKeyword> Keywords { get; init; }
}

internal static class CanonicalCardModificationRegistry
{
    private static readonly FieldInfo EnergyCostField = AccessTools.Field(typeof(CardModel), "_energyCost")
                                                        ?? throw new MissingFieldException(typeof(CardModel).FullName, "_energyCost");
    private static readonly FieldInfo ReplayCountField = AccessTools.Field(typeof(CardModel), "_baseReplayCount")
                                                         ?? throw new MissingFieldException(typeof(CardModel).FullName, "_baseReplayCount");
    private static readonly FieldInfo StarCostField = AccessTools.Field(typeof(CardModel), "_baseStarCost")
                                                       ?? throw new MissingFieldException(typeof(CardModel).FullName, "_baseStarCost");
    private static readonly FieldInfo StarCostSetField = AccessTools.Field(typeof(CardModel), "_starCostSet")
                                                          ?? throw new MissingFieldException(typeof(CardModel).FullName, "_starCostSet");
    private static readonly FieldInfo PoolField = AccessTools.Field(typeof(CardModel), "_pool")
                                                   ?? throw new MissingFieldException(typeof(CardModel).FullName, "_pool");
    private static readonly FieldInfo TypeField = AccessTools.Field(typeof(CardModel), "<Type>k__BackingField")
                                                   ?? throw new MissingFieldException(typeof(CardModel).FullName, "<Type>k__BackingField");
    private static readonly FieldInfo RarityField = AccessTools.Field(typeof(CardModel), "<Rarity>k__BackingField")
                                                     ?? throw new MissingFieldException(typeof(CardModel).FullName, "<Rarity>k__BackingField");
    private static readonly FieldInfo KeywordsField = AccessTools.Field(typeof(CardModel), "_keywords")
                                                       ?? throw new MissingFieldException(typeof(CardModel).FullName, "_keywords");

    private static readonly Dictionary<ModelId, CanonicalCardBaseline> Baselines = new();
    private static readonly Dictionary<ModelId, int> CanonicalStarCosts = new();

    public static CanonicalCardBaseline GetBaseline(CardModel canonical)
    {
        if (Baselines.TryGetValue(canonical.Id, out CanonicalCardBaseline? existing))
            return existing;

        CanonicalCardBaseline captured = new()
        {
            EnergyCost = canonical.EnergyCost.Canonical,
            CostsX = canonical.EnergyCost.CostsX,
            BaseReplayCount = canonical.BaseReplayCount,
            BaseStarCost = canonical.BaseStarCost,
            DynamicVars = canonical.DynamicVars.ToDictionary(pair => pair.Key, pair => pair.Value.BaseValue, StringComparer.Ordinal),
            Pool = canonical.Pool,
            Type = canonical.Type,
            Rarity = canonical.Rarity,
            Keywords = canonical.GetKeywordsWithSources(KeywordSources.Local).ToHashSet()
        };
        Baselines[canonical.Id] = captured;
        return captured;
    }

    public static bool TryGetBaseline(ModelId cardId, out CanonicalCardBaseline baseline)
    {
        CardModel? canonical = ResolveCanonical(cardId);
        if (canonical is null)
        {
            baseline = null!;
            return false;
        }

        baseline = GetBaseline(canonical);
        return true;
    }

    public static void Reconcile(ModelId cardId, CardModificationDelta? delta)
    {
        CardModel? canonical = ResolveCanonical(cardId);
        if (canonical is null)
            return;

        CanonicalCardBaseline baseline = GetBaseline(canonical);
        Restore(canonical, baseline);
        if (delta is { IsEmpty: false })
            Apply(canonical, baseline, delta);
        ConfigurePatches();
    }

    public static IReadOnlyList<ModelId> ReconcileAll()
    {
        IReadOnlyDictionary<ModelId, CardModificationDelta> effective = PermanentCardModificationStore.GetEffectiveDeltasSnapshot();
        HashSet<ModelId> ids = Baselines.Keys.ToHashSet();
        ids.UnionWith(effective.Keys);
        foreach (ModelId id in ids)
        {
            effective.TryGetValue(id, out CardModificationDelta? delta);
            Reconcile(id, delta);
        }
        ConfigurePatches();
        return ids.ToList();
    }

    public static void RestoreAll()
    {
        foreach ((ModelId id, CanonicalCardBaseline baseline) in Baselines)
        {
            CardModel? canonical = ResolveCanonical(id);
            if (canonical is not null)
                Restore(canonical, baseline);
        }
        CanonicalStarCosts.Clear();
        CardModificationPermanentPatches.Reset();
        Baselines.Clear();
    }

    public static bool TryGetCanonicalStarCost(CardModel card, out int value)
    {
        if (card.IsCanonical && CanonicalStarCosts.TryGetValue(card.Id, out value))
            return true;
        value = 0;
        return false;
    }

    public static bool TryGetModifiedStarCost(ModelId cardId, out int value) =>
        CanonicalStarCosts.TryGetValue(cardId, out value);

    private static void Restore(CardModel canonical, CanonicalCardBaseline baseline)
    {
        EnergyCostField.SetValue(canonical, new CardEnergyCost(canonical, baseline.EnergyCost, baseline.CostsX));
        ReplayCountField.SetValue(canonical, baseline.BaseReplayCount);
        StarCostField.SetValue(canonical, baseline.BaseStarCost);
        StarCostSetField.SetValue(canonical, true);
        foreach ((string name, decimal value) in baseline.DynamicVars)
        {
            if (canonical.DynamicVars.TryGetValue(name, out DynamicVar? variable))
                variable.BaseValue = value;
        }
        PoolField.SetValue(canonical, baseline.Pool);
        TypeField.SetValue(canonical, baseline.Type);
        RarityField.SetValue(canonical, baseline.Rarity);
        KeywordsField.SetValue(canonical, new HashSet<CardKeyword>(baseline.Keywords));
        CanonicalStarCosts.Remove(canonical.Id);
    }

    private static void Apply(CardModel canonical, CanonicalCardBaseline baseline, CardModificationDelta delta)
    {
        bool costsX = baseline.CostsX;
        if (delta.KeywordOverrides.TryGetValue(LoadoutKeywords.XCostKey, out bool xCostOverride))
            costsX = xCostOverride;
        int energy = delta.EnergyOverride
                     ?? (delta.EnergyDelta.HasValue ? baseline.EnergyCost + delta.EnergyDelta.Value : baseline.EnergyCost);
        if (delta.EnergyOverride.HasValue || delta.EnergyDelta.HasValue || costsX != baseline.CostsX)
            EnergyCostField.SetValue(canonical, new CardEnergyCost(canonical, costsX ? 0 : energy, costsX));

        if (delta.BaseReplayCountDelta.HasValue)
            ReplayCountField.SetValue(canonical, baseline.BaseReplayCount + delta.BaseReplayCountDelta.Value);
        if (delta.BaseStarCostDelta.HasValue)
        {
            int starCost = baseline.BaseStarCost + delta.BaseStarCostDelta.Value;
            StarCostField.SetValue(canonical, starCost);
            StarCostSetField.SetValue(canonical, true);
            CanonicalStarCosts[canonical.Id] = starCost;
        }

        foreach ((string name, decimal difference) in delta.DynamicVarDeltas)
        {
            if (baseline.DynamicVars.TryGetValue(name, out decimal original)
                && canonical.DynamicVars.TryGetValue(name, out DynamicVar? variable))
            {
                variable.BaseValue = original + difference;
            }
        }

        if (ResolvePool(delta.PoolId) is { } pool)
            PoolField.SetValue(canonical, pool);
        if (Enum.TryParse(delta.Type, true, out CardType type))
            TypeField.SetValue(canonical, type);
        if (Enum.TryParse(delta.Rarity, true, out CardRarity rarity))
            RarityField.SetValue(canonical, rarity);

        HashSet<CardKeyword> keywords = new(baseline.Keywords);
        foreach ((string rawKeyword, bool enabled) in delta.KeywordOverrides)
        {
            if (!LoadoutKeywords.TryResolve(rawKeyword, out CardKeyword keyword) || keyword == CardKeyword.None)
                continue;
            if (enabled) keywords.Add(keyword);
            else keywords.Remove(keyword);
        }
        KeywordsField.SetValue(canonical, keywords);
    }

    private static void ConfigurePatches()
    {
        CardModificationPermanentPatches.Configure(
            PermanentCardModificationStore.HasAnyCreationResidual,
            CanonicalStarCosts.Count > 0);
    }

    private static CardModel? ResolveCanonical(ModelId cardId) =>
        ModelDb.AllCards.FirstOrDefault(card => card.Id == cardId
                                                || string.Equals(card.Id.ToString(), cardId.ToString(), StringComparison.Ordinal));

    private static CardPoolModel? ResolvePool(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        return ModelDb.AllCardPools.FirstOrDefault(pool =>
            string.Equals(pool.Id.ToString(), id, StringComparison.Ordinal)
            || string.Equals(pool.Id.Entry, id, StringComparison.OrdinalIgnoreCase));
    }
}
