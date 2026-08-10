#nullable enable

namespace Loadout.Patches.Cards.CardModification;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Godot;
using HarmonyLib;
using Loadout.Keywords;
using Loadout.Services.Actions;
using Loadout.Services.CardModification;
using Loadout.Services.Compatibility;
using Loadout.Services.Targets;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Runs;

/// <summary>
/// Event-driven card modification operations. CardModel fields are gameplay truth;
/// this helper is called only by creation/save/clone/upgrade/editor/network boundaries.
/// </summary>
public static class CardModificationRuntime
{
    private static readonly FieldInfo? CardPileCardsField = AccessTools.Field(typeof(CardPile), "_cards");
    private static readonly FieldInfo? CardTypeField = AccessTools.Field(typeof(CardModel), "<Type>k__BackingField");
    private static readonly FieldInfo? CardRarityField = AccessTools.Field(typeof(CardModel), "<Rarity>k__BackingField");
    private static readonly FieldInfo? CardPoolField = AccessTools.Field(typeof(CardModel), "_pool");
    private static readonly FieldInfo? CardEnergyCostField = AccessTools.Field(typeof(CardModel), "_energyCost");
    private static readonly FieldInfo? CardKeywordsField = AccessTools.Field(typeof(CardModel), "_keywords");
    private static readonly FieldInfo? CardCurrentUpgradeLevelField =
        AccessTools.Field(typeof(CardModel), "_currentUpgradeLevel");
    private static readonly FieldInfo? DynamicVarDictionaryField =
        AccessTools.Field(typeof(DynamicVarSet), "_vars");
    private static readonly FieldInfo? EnergyCostCanonicalField = AccessTools.Field(typeof(CardEnergyCost), "<Canonical>k__BackingField");
    private static readonly MethodInfo? BaseStarCostSetter = AccessTools.PropertySetter(typeof(CardModel), nameof(CardModel.BaseStarCost));
    private static readonly MethodInfo? NCardFindOnTableByCard = AccessTools.Method(typeof(NCard), nameof(NCard.FindOnTable), [typeof(CardModel)]);
    private static readonly MethodInfo? NCardFindOnTableByCardAndPile = AccessTools.Method(typeof(NCard), nameof(NCard.FindOnTable), [typeof(CardModel), typeof(PileType)]);
    private static readonly Dictionary<ModelId, CardModel> AttachmentDisplayCards = new();
    private static ConditionalWeakTable<CardModel, CardModificationDelta> PreviewDeltas = new();

    [ThreadStatic]
    private static int _suppressPermanentApplyDepth;

    [ThreadStatic]
    private static Stack<CardModel>? _locStringContext;

    private static bool _registered;
    private static bool _customTextOverridesMayExist;
    private static long _permanentDisplayRevision;

    public static event Action<ModelId>? PermanentCardDisplayChanged;
    public static event Action<LoadoutOwnedItem<CardModel>, LoadoutCardVisualRefreshKind>? OwnedCardChanged;

    public static long PermanentDisplayRevision => Interlocked.Read(ref _permanentDisplayRevision);

    public static void NotifyCombatCardUpdated(LoadoutOwnedItem<CardModel> item)
    {
        if (item.CardPileType is null or PileType.Deck)
            return;

        RefreshLiveCardVisuals(item.Model, LoadoutCardVisualRefreshKind.Lightweight);
        OwnedCardChanged?.Invoke(item, LoadoutCardVisualRefreshKind.Lightweight);
    }

    public static bool IsPermanentApplicationSuppressed => _suppressPermanentApplyDepth > 0;

    public static bool HasActiveLocStringContext => _locStringContext is { Count: > 0 };

    public static void Register()
    {
        if (_registered)
            return;

        _registered = true;
        PermanentCardModificationStore.CardChanged += OnPermanentCardChanged;
        PermanentCardModificationStore.Reloaded += OnPermanentStoreReloaded;
        PermanentCardModificationStore.Register();
        _customTextOverridesMayExist = PermanentCardModificationStore.HasAnyCustomText;
        LoadoutKeywordRuntimePatches.Reconcile();
        if (_customTextOverridesMayExist) CardModificationDynamicPatches.EnableTextPatches();
        if (PermanentCardModificationStore.HasAnyPortraitOverrides) CardModificationDynamicPatches.EnablePortraitPatches();
        if (PermanentCardModificationStore.HasAnyUpgradeModifications) CardUpgradeModificationRuntimePatches.Enable();
        CardModificationNetProtocol.Register();
    }

    public static void Unregister()
    {
        if (!_registered)
            return;

        CardModificationNetProtocol.Unregister();
        PermanentCardModificationStore.Unregister();
        PermanentCardModificationStore.CardChanged -= OnPermanentCardChanged;
        PermanentCardModificationStore.Reloaded -= OnPermanentStoreReloaded;
        AttachmentDisplayCards.Clear();
        PreviewDeltas = new ConditionalWeakTable<CardModel, CardModificationDelta>();
        CanonicalCardModificationRegistry.RestoreAll();
        CardModificationDynamicPatches.ClearAll();
        CardUpgradeModificationRuntimePatches.ClearAll();
        LoadoutKeywordRuntimePatches.ResetRunPatches();
        _customTextOverridesMayExist = false;
        _registered = false;
    }

    public static CardModificationSpec GetEffectiveSpec(LoadoutOwnedItem<CardModel> item)
    {
        return GetEffectiveSpec(item.Model);
    }

    public static CardModificationSpec GetEffectiveSpec(CardModel card)
    {
        CardModificationSpec effective = PermanentCardModificationStore.Get(card.Id);
        if (CardModificationFields.TryGet(card, out CardModificationCardData data))
            effective.MergeFrom(MaterializeTemporarySpec(card, data.Delta));
        effective.Normalize();
        return effective;
    }

    public static CardModificationSpec GetTemporarySpec(CardModel card)
    {
        return CardModificationFields.GetSpec(card);
    }

    public static CardModificationSpec GetTemporarySpec(LoadoutOwnedItem<CardModel> item)
    {
        return GetTemporarySpec(item.Model);
    }

    public static CardModificationSpec GetPermanentSpec(ModelId cardId)
    {
        return PermanentCardModificationStore.Get(cardId);
    }

    public static bool HasCustomTextOverrides(CardModel card)
    {
        if (!_registered || !_customTextOverridesMayExist)
            return false;

        if (PreviewDeltas.TryGetValue(card, out CardModificationDelta? preview) && preview.HasCustomText)
            return true;
        if (CardModificationFields.TryGet(card, out CardModificationCardData data) && data.Delta.HasCustomText)
            return true;

        return PermanentCardModificationStore.TryGetDelta(card.Id, out CardModificationDelta? permanent)
               && permanent.HasCustomText;
    }

    internal static void MarkCustomTextOverridesPresent()
    {
        _customTextOverridesMayExist = true;
        CardModificationDynamicPatches.EnableTextPatches();
    }

    public static bool HasPortraitOverrides(CardModel card)
    {
        if (PreviewDeltas.TryGetValue(card, out CardModificationDelta? preview) && preview.HasPortraitOverride)
            return true;
        if (CardModificationFields.TryGet(card, out CardModificationCardData data) && data.Delta.HasPortraitOverride)
            return true;

        return PermanentCardModificationStore.TryGetDelta(card.Id, out CardModificationDelta? permanent)
               && permanent.HasPortraitOverride;
    }

    public static void PushLocStringContext(CardModel card)
    {
        _locStringContext ??= new Stack<CardModel>();
        _locStringContext.Push(card);
    }

    public static void PopLocStringContext()
    {
        if (_locStringContext is { Count: > 0 })
            _locStringContext.Pop();
    }

    public static bool TryGetCustomRawLocString(LocString locString, out string rawText)
    {
        rawText = string.Empty;
        if (!string.Equals(locString.LocTable, "cards", StringComparison.Ordinal)
            || _locStringContext is not { Count: > 0 })
        {
            return false;
        }

        CardModel card = _locStringContext.Peek();
        string titleKey = $"{card.Id.Entry}.title";
        string descriptionKey = $"{card.Id.Entry}.description";
        if (string.Equals(locString.LocEntryKey, titleKey, StringComparison.Ordinal)
            && TryGetEffectiveValue(card, static spec => spec.CustomTitle, out rawText))
        {
            return true;
        }

        return string.Equals(locString.LocEntryKey, descriptionKey, StringComparison.Ordinal)
               && TryGetEffectiveValue(card, static spec => spec.CustomDescription, out rawText);
    }

    public static bool TryGetPortraitPath(
        CardModel card,
        bool beta,
        string currentPath,
        out string path)
    {
        path = string.Empty;
        string? direct = null;
        string? regular = null;
        string? poolId = null;
        if (PreviewDeltas.TryGetValue(card, out CardModificationDelta? preview))
        {
            direct = beta ? preview.BetaPortraitPath : preview.PortraitPath;
            regular = preview.PortraitPath;
            poolId = preview.PoolId;
        }
        if (CardModificationFields.TryGet(card, out CardModificationCardData data))
        {
            direct = beta ? data.Delta.BetaPortraitPath : data.Delta.PortraitPath;
            regular = data.Delta.PortraitPath;
            poolId = data.Delta.PoolId;
        }

        if (PermanentCardModificationStore.TryGetDelta(card.Id, out CardModificationDelta? permanent))
        {
            direct ??= beta ? permanent.BetaPortraitPath : permanent.PortraitPath;
            regular ??= permanent.PortraitPath;
            poolId ??= permanent.PoolId;
        }

        if (!string.IsNullOrWhiteSpace(direct))
        {
            path = direct;
            return true;
        }

        if (beta && !string.IsNullOrWhiteSpace(regular))
        {
            path = regular;
            return true;
        }

        if (string.IsNullOrWhiteSpace(poolId))
            return false;

        CardModel? canonical = LoadoutModelRegistry.ResolveCard(card.Id);
        path = canonical is null || ReferenceEquals(canonical, card)
            ? currentPath
            : canonical.PortraitPath;
        return true;
    }

    public static LoadoutCardVisualRefreshKind GetVisualRefreshKind(
        CardModificationSpec? previous,
        CardModificationSpec? next)
    {
        return SameStructuralValue(previous?.PoolId, next?.PoolId)
               && SameStructuralValue(previous?.Type, next?.Type)
               && SameStructuralValue(previous?.Rarity, next?.Rarity)
               && SameStructuralValue(previous?.PortraitPath, next?.PortraitPath)
               && SameStructuralValue(previous?.BetaPortraitPath, next?.BetaPortraitPath)
               && KeywordOverridesEquivalent(previous?.KeywordOverrides, next?.KeywordOverrides)
               && KeywordOverridesEquivalent(
                   previous?.UpgradeModification.KeywordOverrides,
                   next?.UpgradeModification.KeywordOverrides)
            ? LoadoutCardVisualRefreshKind.Lightweight
            : LoadoutCardVisualRefreshKind.Reload;
    }

    private static bool KeywordOverridesEquivalent(
        IReadOnlyDictionary<string, bool>? left,
        IReadOnlyDictionary<string, bool>? right)
    {
        int leftCount = left?.Count ?? 0;
        if (leftCount != (right?.Count ?? 0))
            return false;
        if (leftCount == 0)
            return true;

        return left!.All(pair => right!.TryGetValue(pair.Key, out bool value) && value == pair.Value);
    }

    public static bool SpecsEquivalent(CardModificationSpec? left, CardModificationSpec? right)
    {
        string a = left is null ? string.Empty : CardModificationCodec.Serialize(Normalize(left));
        string b = right is null ? string.Empty : CardModificationCodec.Serialize(Normalize(right));
        return string.Equals(a, b, StringComparison.Ordinal);
    }

    /// <summary>Called only while a permanent attachment definition exists.</summary>
    public static void ApplyPermanentResidualAtCreation(CardModel card)
    {
        if (card.IsCanonical || IsPermanentApplicationSuppressed)
            return;

        if (PermanentCardModificationStore.TryGetDelta(card.Id, out CardModificationDelta? permanent))
            ApplyPermanentResidual(card, permanent);
    }

    public static void ApplySpecToCard(CardModel? card, CardModificationSpec? spec, bool includeAffliction = true)
    {
        if (card is null || spec is null || spec.IsEmpty || card.IsCanonical)
            return;

        try
        {
            if (spec.EnergyCost.HasValue && !card.EnergyCost.CostsX)
                SetEnergyCost(card, spec.EnergyCost.Value);
            if (spec.BaseReplayCount.HasValue)
                card.BaseReplayCount = spec.BaseReplayCount.Value;
            if (spec.BaseStarCost.HasValue)
                SetBaseStarCost(card, spec.BaseStarCost.Value);
            ApplyKeywordOverrides(card, spec.KeywordOverrides);
            LoadoutKeywordRegistry.SynchronizeDynamicVars(card);
            foreach ((string name, decimal value) in spec.DynamicVars)
            {
                if (card.DynamicVars.TryGetValue(name, out var dynamicVar))
                    dynamicVar.BaseValue = value;
            }

            if (TryResolveModel(spec.PoolId, ModelDb.AllCardPools, out CardPoolModel? pool))
                CardPoolField?.SetValue(card, pool);
            if (TryParseEnum(spec.Type, out CardType type))
                CardTypeField?.SetValue(card, type);
            if (TryParseEnum(spec.Rarity, out CardRarity rarity))
                CardRarityField?.SetValue(card, rarity);

            LoadoutKeywordRuntimePatches.EnableFromOverrides(spec.KeywordOverrides);
            XCostKeywordMechanics.SynchronizeEnergyCost(card, spec.KeywordOverrides, spec.EnergyCost);
            ApplyEnchantmentSpecs(card, spec.Enchantments);
            if (includeAffliction)
                ApplyAfflictionSpec(card, spec.Affliction);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: failed applying fields to '{card.Id}'. {exception.Message}");
        }
    }

    public static void ApplyCustomRunStartingState(CardModel card, CardModificationSpec? state)
    {
        if (state is null || state.IsEmpty || card.IsCanonical)
            return;

        CardModificationSpec normalized = state.Clone();
        normalized.Normalize();
        CardModificationSpec previous = GetEffectiveSpec(card);
        if (CardModificationFields.Set(card, normalized))
            RebuildCard(card, previous, forceAllOwnedFields: true);
    }

    public static void ApplyDeltaToCard(CardModel? card, CardModificationDelta? delta, bool includeAffliction = true)
    {
        if (card is null || delta is null || delta.IsEmpty || card.IsCanonical) return;
        try
        {
            if (!card.EnergyCost.CostsX)
            {
                if (delta.EnergyOverride.HasValue) SetEnergyCost(card, delta.EnergyOverride.Value);
                else if (delta.EnergyDelta.HasValue) SetEnergyCost(card, card.EnergyCost.Canonical + delta.EnergyDelta.Value);
            }
            if (delta.BaseReplayCountDelta.HasValue)
                card.BaseReplayCount += delta.BaseReplayCountDelta.Value;
            if (delta.BaseStarCostDelta.HasValue)
                SetBaseStarCost(card, card.BaseStarCost + delta.BaseStarCostDelta.Value);
            ApplyKeywordOverrides(card, delta.KeywordOverrides);
            LoadoutKeywordRegistry.SynchronizeDynamicVars(card);
            // Preview cards reach this path before their first visual refresh.
            // Enable description/runtime patches immediately even when no live
            // deck card currently carries the keyword.
            LoadoutKeywordRuntimePatches.EnableFromOverrides(delta.KeywordOverrides);
            foreach ((string name, decimal value) in delta.DynamicVarDeltas)
            {
                if (card.DynamicVars.TryGetValue(name, out var dynamicVar)) dynamicVar.BaseValue += value;
            }

            if (TryResolveModel(delta.PoolId, ModelDb.AllCardPools, out CardPoolModel? pool)) CardPoolField?.SetValue(card, pool);
            if (TryParseEnum(delta.Type, out CardType type)) CardTypeField?.SetValue(card, type);
            if (TryParseEnum(delta.Rarity, out CardRarity rarity)) CardRarityField?.SetValue(card, rarity);
            XCostKeywordMechanics.SynchronizeEnergyCost(card, delta.KeywordOverrides, delta.EnergyOverride);
            ApplyEnchantmentSpecs(card, delta.Enchantments);
            if (includeAffliction) ApplyAfflictionSpec(card, delta.Affliction);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: failed applying delta to '{card.Id}'. {exception.Message}");
        }
    }

    public static CardModificationDelta CreatePermanentDelta(ModelId cardId, CardModificationSpec? desired)
    {
        return CanonicalCardModificationRegistry.TryGetBaseline(cardId, out CanonicalCardBaseline? baseline)
            ? CreatePermanentDelta(baseline, desired)
            : new CardModificationDelta();
    }

    public static CardModificationDelta CreateTemporaryDelta(CardModel card, CardModificationSpec? desired)
    {
        if (desired is null || desired.IsEmpty) return new CardModificationDelta();
        CardModel? canonical = LoadoutModelRegistry.ResolveCard(card.Id);
        if (canonical is null) return new CardModificationDelta();
        CardModificationSpec permanent = PermanentCardModificationStore.Get(card.Id);
        CardModel baseline = CreateBaseline(
            canonical,
            card.CurrentUpgradeLevel,
            LoadoutKeywordRuntimePatches.GetInfiniteUpgradeOverride(desired),
            desired.UpgradeModification,
            desired.KeywordOverrides);
        return CreateDelta(baseline, desired, permanent);
    }

    public static CardModificationSpec MaterializePermanentSpec(ModelId cardId, CardModificationDelta delta)
    {
        return CanonicalCardModificationRegistry.TryGetBaseline(cardId, out CanonicalCardBaseline? baseline)
            ? MaterializePermanentSpec(baseline, delta)
            : new CardModificationSpec();
    }

    public static CardModificationSpec MaterializeTemporarySpec(CardModel card, CardModificationDelta delta)
    {
        CardModel? canonical = LoadoutModelRegistry.ResolveCard(card.Id);
        if (canonical is null) return new CardModificationSpec();
        CardModificationSpec permanent = PermanentCardModificationStore.Get(card.Id);
        CardUpgradeModificationSpec temporaryUpgrade = MaterializeUpgradeModification(
            permanent.UpgradeModification,
            delta.UpgradeModification);
        CardUpgradeModificationSpec upgradeModification = permanent.UpgradeModification.Clone();
        upgradeModification.MergeFrom(temporaryUpgrade);
        CardModel baseline = CreateBaseline(
            canonical,
            card.CurrentUpgradeLevel,
            LoadoutKeywordRuntimePatches.GetInfiniteUpgradeOverride(delta),
            upgradeModification,
            delta.KeywordOverrides);
        return MaterializeSpec(baseline, delta, permanent);
    }

    public static void ReapplyTemporaryDelta(CardModel card)
    {
        if (!CardModificationFields.TryGet(card, out CardModificationCardData data)) return;
        CardModel? canonical = LoadoutModelRegistry.ResolveCard(card.Id);
        if (canonical is null) return;
        CardModificationSpec previous = GetEffectiveSpec(card);
        CardModificationSpec permanent = PermanentCardModificationStore.Get(card.Id);
        CardUpgradeModificationSpec temporaryUpgrade = MaterializeUpgradeModification(
            permanent.UpgradeModification,
            data.Delta.UpgradeModification);
        CardUpgradeModificationSpec upgradeModification = permanent.UpgradeModification.Clone();
        upgradeModification.MergeFrom(temporaryUpgrade);
        CardModel baseline = CreateBaseline(
            canonical,
            card.CurrentUpgradeLevel,
            LoadoutKeywordRuntimePatches.GetInfiniteUpgradeOverride(data.Delta),
            upgradeModification,
            data.Delta.KeywordOverrides);
        CardModificationSpec desired = MaterializeSpec(baseline, data.Delta, permanent);
        CopyNativeFields(baseline, card, previous, desired);
        ApplySpecToCard(card, desired);
    }

    public static CardUpgradeModificationSpec ResolveUpgradeModification(
        CardModificationSpec? permanent,
        CardModificationDelta? temporary,
        CardModificationSpec? legacyTemporary = null)
    {
        CardUpgradeModificationSpec result =
            permanent?.UpgradeModification.Clone() ?? new CardUpgradeModificationSpec();
        if (temporary is not null)
        {
            result.MergeFrom(MaterializeUpgradeModification(
                permanent?.UpgradeModification,
                temporary.UpgradeModification));
        }
        result.MergeFrom(legacyTemporary?.UpgradeModification);
        result.Normalize();
        return result;
    }

    private static CardUpgradeModificationSpec MaterializeUpgradeModification(
        CardUpgradeModificationSpec? permanent,
        CardUpgradeModificationSpec delta)
    {
        CardUpgradeModificationSpec result = new();
        if (delta.EnergyCostDelta.HasValue)
        {
            result.EnergyCostDelta = AddIntDeltaClamped(
                permanent?.EnergyCostDelta ?? 0,
                delta.EnergyCostDelta.Value);
        }
        if (delta.BaseReplayCountDelta.HasValue)
        {
            result.BaseReplayCountDelta = AddIntDeltaClamped(
                permanent?.BaseReplayCountDelta ?? 0,
                delta.BaseReplayCountDelta.Value);
        }
        if (delta.BaseStarCostDelta.HasValue)
        {
            result.BaseStarCostDelta = AddIntDeltaClamped(
                permanent?.BaseStarCostDelta ?? 0,
                delta.BaseStarCostDelta.Value);
        }
        foreach ((string name, decimal difference) in delta.DynamicVarDeltas)
        {
            result.DynamicVarDeltas[name] =
                (permanent?.DynamicVarDeltas.GetValueOrDefault(name) ?? 0m) + difference;
        }
        foreach ((string key, bool value) in delta.KeywordOverrides)
            result.KeywordOverrides[key] = value;
        result.Normalize();
        return result;
    }

    private static CardModificationDelta CreateDelta(
        CardModel baseline,
        CardModificationSpec? desired,
        CardModificationSpec? structuralBaseline)
    {
        CardModificationDelta delta = new();
        if (desired is null) return delta;
        if (desired.EnergyCost.HasValue)
        {
            int difference = desired.EnergyCost.Value - baseline.EnergyCost.Canonical;
            if (baseline.EnergyCost.CostsX || desired.KeywordOverrides.ContainsKey(LoadoutKeywords.XCostKey))
                delta.EnergyOverride = desired.EnergyCost.Value;
            else if (difference != 0) delta.EnergyDelta = difference;
        }
        if (desired.BaseReplayCount.HasValue)
        {
            int difference = desired.BaseReplayCount.Value - baseline.BaseReplayCount;
            if (difference != 0) delta.BaseReplayCountDelta = difference;
        }
        if (desired.BaseStarCost.HasValue)
        {
            int difference = desired.BaseStarCost.Value - baseline.BaseStarCost;
            if (difference != 0) delta.BaseStarCostDelta = difference;
        }
        foreach ((string name, decimal value) in desired.DynamicVars)
        {
            if (baseline.DynamicVars.TryGetValue(name, out var baselineVar))
            {
                decimal difference = value - baselineVar.BaseValue;
                if (difference != 0m) delta.DynamicVarDeltas[name] = difference;
            }
            else if (LoadoutKeywordRegistry.TryGetDynamicVar(
                         name,
                         out var keywordVarDefinition))
            {
                decimal difference =
                    value - keywordVarDefinition.DefaultValue;
                if (difference != 0m) delta.DynamicVarDeltas[name] = difference;
            }
        }
        if (!SameStructuralValue(desired.PoolId, structuralBaseline?.PoolId)
            && !string.Equals(desired.PoolId, baseline.Pool.Id.ToString(), StringComparison.Ordinal))
            delta.PoolId = desired.PoolId;
        if (!SameStructuralValue(desired.Type, structuralBaseline?.Type)
            && !string.Equals(desired.Type, baseline.Type.ToString(), StringComparison.OrdinalIgnoreCase))
            delta.Type = desired.Type;
        if (!SameStructuralValue(desired.Rarity, structuralBaseline?.Rarity)
            && !string.Equals(desired.Rarity, baseline.Rarity.ToString(), StringComparison.OrdinalIgnoreCase))
            delta.Rarity = desired.Rarity;
        if (!SameStructuralValue(desired.CustomTitle, structuralBaseline?.CustomTitle)) delta.CustomTitle = desired.CustomTitle;
        if (!SameStructuralValue(desired.CustomDescription, structuralBaseline?.CustomDescription)) delta.CustomDescription = desired.CustomDescription;
        if (!SameStructuralValue(desired.PortraitPath, structuralBaseline?.PortraitPath)) delta.PortraitPath = desired.PortraitPath;
        if (!SameStructuralValue(desired.BetaPortraitPath, structuralBaseline?.BetaPortraitPath)) delta.BetaPortraitPath = desired.BetaPortraitPath;
        foreach ((string key, bool value) in desired.KeywordOverrides)
        {
            if (structuralBaseline?.KeywordOverrides.TryGetValue(key, out bool baselineValue) != true || baselineValue != value)
                delta.KeywordOverrides[key] = value;
        }
        AddUpgradeScalarResiduals(
            desired.UpgradeModification,
            structuralBaseline?.UpgradeModification,
            delta.UpgradeModification);
        foreach ((string name, decimal value) in desired.UpgradeModification.DynamicVarDeltas)
        {
            decimal baselineValue = structuralBaseline?.UpgradeModification.DynamicVarDeltas
                .GetValueOrDefault(name) ?? 0m;
            decimal difference = value - baselineValue;
            if (difference != 0m)
                delta.UpgradeModification.DynamicVarDeltas[name] = difference;
        }
        foreach ((string key, bool value) in desired.UpgradeModification.KeywordOverrides)
        {
            if (structuralBaseline?.UpgradeModification.KeywordOverrides.TryGetValue(
                    key,
                    out bool baselineValue) != true
                || baselineValue != value)
            {
                delta.UpgradeModification.KeywordOverrides[key] = value;
            }
        }
        if (!AttachmentListsEqual(desired.Enchantments, structuralBaseline?.Enchantments))
            delta.Enchantments = CardAttachmentSpec.CloneList(desired.Enchantments);
        if (!AttachmentEquals(desired.Affliction, structuralBaseline?.Affliction)) delta.Affliction = desired.Affliction?.Clone();
        delta.Normalize();
        return delta;
    }

    private static void AddUpgradeScalarResiduals(
        CardUpgradeModificationSpec desired,
        CardUpgradeModificationSpec? baseline,
        CardUpgradeModificationSpec residual)
    {
        if (desired.EnergyCostDelta.HasValue)
        {
            int difference = SubtractIntClamped(
                desired.EnergyCostDelta.Value,
                baseline?.EnergyCostDelta ?? 0);
            if (difference != 0)
                residual.EnergyCostDelta = difference;
        }
        if (desired.BaseReplayCountDelta.HasValue)
        {
            int difference = SubtractIntClamped(
                desired.BaseReplayCountDelta.Value,
                baseline?.BaseReplayCountDelta ?? 0);
            if (difference != 0)
                residual.BaseReplayCountDelta = difference;
        }
        if (desired.BaseStarCostDelta.HasValue)
        {
            int difference = SubtractIntClamped(
                desired.BaseStarCostDelta.Value,
                baseline?.BaseStarCostDelta ?? 0);
            if (difference != 0)
                residual.BaseStarCostDelta = difference;
        }
    }

    private static CardModificationDelta CreatePermanentDelta(
        CanonicalCardBaseline baseline,
        CardModificationSpec? desired)
    {
        CardModificationDelta delta = new();
        if (desired is null)
            return delta;

        if (desired.EnergyCost.HasValue)
        {
            int difference = desired.EnergyCost.Value - baseline.EnergyCost;
            if (baseline.CostsX || desired.KeywordOverrides.ContainsKey(LoadoutKeywords.XCostKey))
                delta.EnergyOverride = desired.EnergyCost.Value;
            else if (difference != 0)
                delta.EnergyDelta = difference;
        }
        if (desired.BaseReplayCount.HasValue)
        {
            int difference = desired.BaseReplayCount.Value - baseline.BaseReplayCount;
            if (difference != 0) delta.BaseReplayCountDelta = difference;
        }
        if (desired.BaseStarCost.HasValue)
        {
            int difference = desired.BaseStarCost.Value - baseline.BaseStarCost;
            if (difference != 0) delta.BaseStarCostDelta = difference;
        }
        foreach ((string name, decimal value) in desired.DynamicVars)
        {
            if (baseline.DynamicVars.TryGetValue(name, out decimal original))
            {
                decimal difference = value - original;
                if (difference != 0m) delta.DynamicVarDeltas[name] = difference;
            }
            else if (LoadoutKeywordRegistry.TryGetDynamicVar(
                         name,
                         out var keywordVarDefinition))
            {
                decimal difference =
                    value - keywordVarDefinition.DefaultValue;
                if (difference != 0m) delta.DynamicVarDeltas[name] = difference;
            }
        }
        if (!string.IsNullOrWhiteSpace(desired.PoolId)
            && !string.Equals(desired.PoolId, baseline.Pool.Id.ToString(), StringComparison.Ordinal))
            delta.PoolId = desired.PoolId;
        if (!string.IsNullOrWhiteSpace(desired.Type)
            && !string.Equals(desired.Type, baseline.Type.ToString(), StringComparison.OrdinalIgnoreCase))
            delta.Type = desired.Type;
        if (!string.IsNullOrWhiteSpace(desired.Rarity)
            && !string.Equals(desired.Rarity, baseline.Rarity.ToString(), StringComparison.OrdinalIgnoreCase))
            delta.Rarity = desired.Rarity;
        delta.CustomTitle = desired.CustomTitle;
        delta.CustomDescription = desired.CustomDescription;
        delta.PortraitPath = desired.PortraitPath;
        delta.BetaPortraitPath = desired.BetaPortraitPath;
        delta.KeywordOverrides = new Dictionary<string, bool>(desired.KeywordOverrides, StringComparer.Ordinal);
        delta.Enchantments = CardAttachmentSpec.CloneList(desired.Enchantments);
        delta.Affliction = desired.Affliction?.Clone();
        delta.UpgradeModification = desired.UpgradeModification.Clone();
        delta.Normalize();
        return delta;
    }

    private static bool AttachmentEquals(CardAttachmentSpec? left, CardAttachmentSpec? right)
    {
        if (left is null || left.IsEmpty) return right is null || right.IsEmpty;
        if (right is null || right.IsEmpty) return false;
        return left.Clear == right.Clear
               && left.Amount == right.Amount
               && string.Equals(left.ModelId, right.ModelId, StringComparison.Ordinal);
    }

    private static bool AttachmentListsEqual(
        IReadOnlyList<CardAttachmentSpec>? left,
        IReadOnlyList<CardAttachmentSpec>? right)
    {
        if (left is null)
            return right is null;
        if (right is null || left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
        {
            if (!AttachmentEquals(left[i], right[i]))
                return false;
        }
        return true;
    }

    private static CardModificationSpec MaterializeSpec(
        CardModel baseline,
        CardModificationDelta delta,
        CardModificationSpec? structuralBaseline = null)
    {
        CardModificationSpec spec = new()
        {
            EnergyCost = delta.EnergyOverride ?? (delta.EnergyDelta.HasValue ? baseline.EnergyCost.Canonical + delta.EnergyDelta.Value : null),
            BaseReplayCount = delta.BaseReplayCountDelta.HasValue ? baseline.BaseReplayCount + delta.BaseReplayCountDelta.Value : null,
            BaseStarCost = delta.BaseStarCostDelta.HasValue ? baseline.BaseStarCost + delta.BaseStarCostDelta.Value : null,
            PoolId = delta.PoolId,
            Type = delta.Type,
            Rarity = delta.Rarity,
            CustomTitle = delta.CustomTitle,
            CustomDescription = delta.CustomDescription,
            PortraitPath = delta.PortraitPath,
            BetaPortraitPath = delta.BetaPortraitPath,
            KeywordOverrides = new Dictionary<string, bool>(delta.KeywordOverrides, StringComparer.Ordinal),
            Enchantments = CardAttachmentSpec.CloneList(delta.Enchantments),
            Affliction = delta.Affliction?.Clone(),
            UpgradeModification = MaterializeUpgradeModification(
                structuralBaseline?.UpgradeModification,
                delta.UpgradeModification)
        };
        foreach ((string name, decimal difference) in delta.DynamicVarDeltas)
        {
            if (baseline.DynamicVars.TryGetValue(name, out var baselineVar))
                spec.DynamicVars[name] = baselineVar.BaseValue + difference;
            else if (LoadoutKeywordRegistry.TryGetDynamicVar(
                         name,
                         out var keywordVarDefinition))
                spec.DynamicVars[name] =
                    keywordVarDefinition.DefaultValue + difference;
        }
        spec.Normalize();
        return spec;
    }

    private static CardModificationSpec MaterializePermanentSpec(
        CanonicalCardBaseline baseline,
        CardModificationDelta delta)
    {
        CardModificationSpec spec = new()
        {
            EnergyCost = delta.EnergyOverride
                         ?? (delta.EnergyDelta.HasValue ? baseline.EnergyCost + delta.EnergyDelta.Value : null),
            BaseReplayCount = delta.BaseReplayCountDelta.HasValue
                ? baseline.BaseReplayCount + delta.BaseReplayCountDelta.Value
                : null,
            BaseStarCost = delta.BaseStarCostDelta.HasValue
                ? baseline.BaseStarCost + delta.BaseStarCostDelta.Value
                : null,
            PoolId = delta.PoolId,
            Type = delta.Type,
            Rarity = delta.Rarity,
            CustomTitle = delta.CustomTitle,
            CustomDescription = delta.CustomDescription,
            PortraitPath = delta.PortraitPath,
            BetaPortraitPath = delta.BetaPortraitPath,
            KeywordOverrides = new Dictionary<string, bool>(delta.KeywordOverrides, StringComparer.Ordinal),
            Enchantments = CardAttachmentSpec.CloneList(delta.Enchantments),
            Affliction = delta.Affliction?.Clone(),
            UpgradeModification = delta.UpgradeModification.Clone()
        };
        foreach ((string name, decimal difference) in delta.DynamicVarDeltas)
        {
            if (baseline.DynamicVars.TryGetValue(name, out decimal original))
                spec.DynamicVars[name] = original + difference;
            else if (LoadoutKeywordRegistry.TryGetDynamicVar(
                         name,
                         out var keywordVarDefinition))
                spec.DynamicVars[name] =
                    keywordVarDefinition.DefaultValue + difference;
        }
        spec.Normalize();
        return spec;
    }

    public static CardModel CreatePreviewCard(CardModel source, CardModificationSpec state)
    {
        try
        {
            CardModel? canonical = LoadoutModelRegistry.ResolveCard(source.Id);
            if (canonical is null)
                return source;

            CardModificationSpec permanent = PermanentCardModificationStore.Get(source.Id);
            CardModel preview = CreateBaseline(
                canonical,
                source.CurrentUpgradeLevel,
                LoadoutKeywordRuntimePatches.GetInfiniteUpgradeOverride(state),
                state.UpgradeModification,
                state.KeywordOverrides);
            CardModificationDelta temporary = CreateDelta(preview, state, permanent);
            ApplyDeltaToCard(preview, temporary);
            if (temporary.HasCustomText || temporary.HasPortraitOverride)
            {
                PreviewDeltas.Add(preview, temporary);
                if (temporary.HasCustomText) MarkCustomTextOverridesPresent();
                if (temporary.HasPortraitOverride) CardModificationDynamicPatches.EnablePortraitPatches();
            }
            return preview;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: failed creating preview for '{source.Id}'. {exception.Message}");
            return source;
        }
    }

    public static CardModel? CreateUpgradePreviewSource(
        CardModel source,
        CardModificationSpec state)
    {
        CardModel? canonical = LoadoutModelRegistry.ResolveCard(source.Id);
        ICardScope? cardScope = source.CardScope;
        if (canonical is null
            || source.Owner is null
            || source.Pile is null
            || cardScope is null)
            return null;

        int upgradeLevel = source.IsUpgradable ? source.CurrentUpgradeLevel : 0;
        CardModel baseline = CreateBaseline(
            canonical,
            upgradeLevel,
            LoadoutKeywordRuntimePatches.GetInfiniteUpgradeOverride(state),
            state.UpgradeModification,
            state.KeywordOverrides);
        if (!baseline.IsUpgradable)
            return null;

        CardModel scratch = cardScope.CloneCard(source);
        CardModificationFields.Clear(scratch);
        CardCurrentUpgradeLevelField?.SetValue(scratch, upgradeLevel);
        CopyNativeFields(
            baseline,
            scratch,
            new CardModificationSpec(),
            state,
            forceAllOwnedFields: true);
        ApplySpecToCard(scratch, state);
        scratch.FinalizeUpgradeInternal();

        CardModificationDelta previewDelta =
            CreateTemporaryDelta(scratch, state);
        if (previewDelta.HasCustomText || previewDelta.HasPortraitOverride)
        {
            PreviewDeltas.Remove(scratch);
            PreviewDeltas.Add(scratch, previewDelta);
            if (previewDelta.HasCustomText) MarkCustomTextOverridesPresent();
            if (previewDelta.HasPortraitOverride) CardModificationDynamicPatches.EnablePortraitPatches();
        }
        return scratch;
    }

    public static bool CanModifyUpgrade(
        CardModel source,
        CardModificationSpec state)
    {
        CardModel? canonical = LoadoutModelRegistry.ResolveCard(source.Id);
        if (canonical is null || source.Owner is null || source.Pile is null)
            return false;

        int upgradeLevel = source.IsUpgradable
            ? source.CurrentUpgradeLevel
            : 0;
        CardModel baseline = CreateBaseline(
            canonical,
            upgradeLevel,
            LoadoutKeywordRuntimePatches.GetInfiniteUpgradeOverride(state),
            state.UpgradeModification,
            state.KeywordOverrides);
        return baseline.IsUpgradable;
    }

    public static void ReleaseUpgradePreviewCard(CardModel? card)
    {
        if (card is null || card.IsCanonical)
            return;

        try
        {
            ICardScope? cardScope = card.CardScope;
            if (cardScope is null)
                return;
            cardScope.RemoveCard(card);
            card.HasBeenRemovedFromState = true;
        }
        catch (Exception exception)
        {
            GD.PushWarning(
                $"CardModification: could not release upgrade preview card '{card.Id}'. {exception.Message}");
        }
    }

    internal static void ApplyUpgradeModification(
        CardModel card,
        CardUpgradeModificationSpec modification)
    {
        if (modification.IsEmpty)
            return;

        ApplyKeywordOverrides(card, modification.KeywordOverrides);
        LoadoutKeywordRegistry.SynchronizeDynamicVars(card);
        LoadoutKeywordRuntimePatches.EnableFromOverrides(
            modification.KeywordOverrides);

        int? energyCost = modification.EnergyCostDelta.HasValue
            ? AddIntDeltaClamped(
                card.EnergyCost.Canonical,
                modification.EnergyCostDelta.Value)
            : null;
        if (modification.BaseReplayCountDelta.HasValue)
        {
            card.BaseReplayCount = AddIntDeltaClamped(
                card.BaseReplayCount,
                modification.BaseReplayCountDelta.Value);
        }
        if (modification.BaseStarCostDelta.HasValue)
        {
            SetBaseStarCost(
                card,
                AddIntDeltaClamped(
                    card.BaseStarCost,
                    modification.BaseStarCostDelta.Value));
        }
        foreach ((string name, decimal delta) in modification.DynamicVarDeltas)
        {
            if (card.DynamicVars.TryGetValue(name, out var dynamicVar))
                dynamicVar.UpgradeValueBy(delta);
        }
        XCostKeywordMechanics.SynchronizeEnergyCost(
            card,
            modification.KeywordOverrides,
            energyCost);
        if (energyCost.HasValue && !card.EnergyCost.CostsX)
            SetEnergyCost(card, energyCost.Value);
    }

    public static CardModel GetPermanentCardForDisplay(CardModel card)
    {
        if (!card.IsCanonical)
            return card;

        if (!PermanentCardModificationStore.TryGetDelta(card.Id, out CardModificationDelta? delta)
            || (delta.Enchantments is null && delta.Affliction is null))
        {
            return card;
        }

        if (AttachmentDisplayCards.TryGetValue(card.Id, out CardModel? display))
            return display;

        try
        {
            using (SuppressPermanentApplication())
                display = card.ToMutable();
            ApplyPermanentResidual(display, delta);
            AttachmentDisplayCards[card.Id] = display;
            return display;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: failed creating catalog card '{card.Id}'. {exception.Message}");
            return card;
        }
    }

    public static void SaveTemporary(LoadoutOwnedItem<CardModel> item, CardModificationSpec state)
    {
        CardModificationSpec previous = GetEffectiveSpec(item.Model);
        if (!CardModificationFields.Set(item.Model, state))
            return;

        RebuildOwnedCard(item, previous);
        CardModificationSpec next = GetEffectiveSpec(item.Model);
        NotifyCardUpdated(item, previous, next);
        LoadoutKeywordRuntimePatches.Reconcile();
        CardModificationNetProtocol.BroadcastTemporary(item, state);
    }

    public static void ResetTemporaryToBasic(LoadoutOwnedItem<CardModel> item)
    {
        if (ResetTemporaryWithoutBroadcast(item, out LoadoutOwnedItem<CardModel>? replacement)
            && replacement is not null)
        {
            CardModificationNetProtocol.BroadcastTemporary(replacement, next: null);
        }
    }

    public static void CommitPermanent(LoadoutOwnedItem<CardModel> item, CardModificationSpec state)
    {
        CardModificationSpec previousPermanent = PermanentCardModificationStore.Get(item.Model.Id);
        CardModificationSpec selectedPrevious = GetEffectiveSpec(item.Model);
        bool temporaryChanged = CardModificationFields.Clear(item.Model);
        bool permanentChanged = PermanentCardModificationStore.SetProfile(item.Model.Id, state);
        if (permanentChanged)
        {
            RetrofitLiveDeckCopies(item.Model.Id, previousPermanent, item.Model, selectedPrevious);
            CardModificationNetProtocol.BroadcastPermanentDelta(item.Model.Id, state);
        }
        else if (temporaryChanged)
        {
            RebuildOwnedCard(item, selectedPrevious);
            NotifyCardUpdated(item, selectedPrevious, GetEffectiveSpec(item.Model));
        }

        if (temporaryChanged)
            CardModificationNetProtocol.BroadcastTemporary(item, next: null);
        LoadoutKeywordRuntimePatches.Reconcile();
    }

    public static void ResetPermanentToBasic(LoadoutOwnedItem<CardModel> item)
    {
        CardModificationSpec previousPermanent = PermanentCardModificationStore.Get(item.Model.Id);
        CardModificationSpec selectedPrevious = GetEffectiveSpec(item.Model);
        bool temporaryChanged = CardModificationFields.Clear(item.Model);
        bool permanentChanged = PermanentCardModificationStore.SetProfile(item.Model.Id, null);
        if (permanentChanged)
        {
            RetrofitLiveDeckCopies(item.Model.Id, previousPermanent, item.Model, selectedPrevious);
            CardModificationNetProtocol.BroadcastPermanentDelta(item.Model.Id, null);
        }
        else
        {
            RebuildOwnedCard(item, selectedPrevious, forceAllOwnedFields: true);
            NotifyCardUpdated(item, selectedPrevious, GetEffectiveSpec(item.Model));
        }

        if (temporaryChanged)
            CardModificationNetProtocol.BroadcastTemporary(item, next: null);
        LoadoutKeywordRuntimePatches.Reconcile();
    }

    public static int GetPermanentModificationCount()
    {
        return PermanentCardModificationStore.Count;
    }

    public static IReadOnlyList<ModelId> ResetAllPermanent()
    {
        Dictionary<ModelId, CardModificationSpec> previous = ModelDb.AllCards
            .Where(card => PermanentCardModificationStore.TryGet(card.Id, out _))
            .ToDictionary(card => card.Id, card => PermanentCardModificationStore.Get(card.Id));
        IReadOnlyList<ModelId> changed = PermanentCardModificationStore.ResetAllProfile();
        RetrofitChangedPermanentCards(changed, previous);
        CardModificationNetProtocol.BroadcastPermanentSnapshot();
        return changed;
    }

    public static void ApplySynchronizedOperation(
        CardModificationOperation operation,
        ModelId modelId,
        LoadoutTargetSelection target,
        int deckIndex,
        LoadoutCardPileTarget pileTarget,
        uint combatCardIndex,
        ModelId expectedModelId,
        CardModificationSpec? state,
        Player actionPlayer,
        bool authoritativeRemote = false)
    {
        LoadoutOwnedItem<CardModel>? resolved = TryResolveOwnedCard(
            target,
            deckIndex,
            pileTarget,
            combatCardIndex,
            expectedModelId,
            actionPlayer);
        if (resolved is not { } item)
            return;

        switch (operation)
        {
            case CardModificationOperation.SaveTemporary:
                ApplyTemporaryWithoutBroadcast(item, state);
                break;
            case CardModificationOperation.ResetTemporary:
            case CardModificationOperation.ResetTemporaryToBasic:
                ResetTemporaryWithoutBroadcast(item, out _);
                break;
            case CardModificationOperation.ApplyPermanent:
                ApplyPermanentWithoutBroadcast(item, modelId, state, authoritativeRemote);
                break;
            case CardModificationOperation.ResetPermanentToBasic:
                ApplyPermanentWithoutBroadcast(item, modelId, null, authoritativeRemote);
                break;
        }
    }

    public static void ApplySynchronizedDeltaOperation(
        CardModificationOperation operation,
        ModelId modelId,
        LoadoutTargetSelection target,
        int deckIndex,
        LoadoutCardPileTarget pileTarget,
        uint combatCardIndex,
        ModelId expectedModelId,
        CardModificationDelta? delta,
        Player actionPlayer,
        bool authoritativeRemote = false)
    {
        LoadoutOwnedItem<CardModel>? resolved = TryResolveOwnedCard(
            target,
            deckIndex,
            pileTarget,
            combatCardIndex,
            expectedModelId,
            actionPlayer);
        if (resolved is not { } item)
            return;

        switch (operation)
        {
            case CardModificationOperation.SaveTemporary:
                ApplyTemporaryDeltaWithoutBroadcast(item, delta);
                break;
            case CardModificationOperation.ResetTemporary:
            case CardModificationOperation.ResetTemporaryToBasic:
                ResetTemporaryWithoutBroadcast(item, out _);
                break;
            case CardModificationOperation.ApplyPermanent:
                ApplyPermanentDeltaWithoutBroadcast(item, modelId, delta, authoritativeRemote);
                break;
            case CardModificationOperation.ResetPermanentToBasic:
                ApplyPermanentDeltaWithoutBroadcast(item, modelId, null, authoritativeRemote);
                break;
        }
    }

    public static void ApplyRemoteTemporaryState(
        ulong ownerNetId,
        int deckIndex,
        LoadoutCardPileTarget pileTarget,
        uint combatCardIndex,
        string cardId,
        CardModificationSpec? state)
    {
        if (!TryResolveLiveCard(ownerNetId, deckIndex, pileTarget, combatCardIndex, cardId, out LoadoutOwnedItem<CardModel>? item)
            || item is null)
            return;

        ApplyTemporaryWithoutBroadcast(item, state);
    }

    public static void ApplyRemoteTemporaryDelta(
        ulong ownerNetId,
        int deckIndex,
        LoadoutCardPileTarget pileTarget,
        uint combatCardIndex,
        string cardId,
        CardModificationDelta? delta)
    {
        if (!TryResolveLiveCard(ownerNetId, deckIndex, pileTarget, combatCardIndex, cardId, out LoadoutOwnedItem<CardModel>? item)
            || item is null)
            return;

        ApplyTemporaryDeltaWithoutBroadcast(item, delta);
    }

    public static bool ReplaceTemporaryStatesForPlayer(
        Player player,
        IReadOnlyDictionary<CardModel, CardModificationSpec> states)
    {
        bool changed = false;
        foreach (CardModel card in player.Deck.Cards)
        {
            states.TryGetValue(card, out CardModificationSpec? state);
            changed |= CardModificationFields.Set(card, state);
            if (state is not null && !state.IsEmpty)
                ApplySpecToCard(card, state);
        }
        return changed;
    }

    public static void RetrofitLiveDeckCopies(
        ModelId cardId,
        CardModificationSpec? previousPermanent = null,
        CardModel? selectedCard = null,
        CardModificationSpec? selectedPrevious = null)
    {
        if (!TryGetRunState(out RunState? runState))
            return;

        List<LoadoutChangedCard> changedCards = [];
        HashSet<ulong> changedPlayers = [];
        HashSet<CardModel> refreshedCombatCards = [];
        bool restoreAfterEnchantmentClear = RemovedPermanentEnchantments(
            previousPermanent,
            PermanentCardModificationStore.Get(cardId));
        foreach (Player owner in runState!.Players)
        {
            IReadOnlyList<CardModel> deck = owner.Deck.Cards;
            for (int index = 0; index < deck.Count; index++)
            {
                CardModel card = deck[index];
                if (!ModelIdMatches(card, cardId))
                    continue;

                CardModificationSpec previous = ReferenceEquals(card, selectedCard) && selectedPrevious is not null
                    ? selectedPrevious.Clone()
                    : Merge(previousPermanent, GetTemporarySpec(card));
                RebuildCard(
                    card,
                    previous,
                    forceAllOwnedFields: restoreAfterEnchantmentClear
                        || ReferenceEquals(card, selectedCard));
                CardModificationSpec next = GetEffectiveSpec(card);
                LoadoutCardVisualRefreshKind kind = GetVisualRefreshKind(previous, next);
                RefreshLiveCardVisuals(card, kind);
                changedPlayers.Add(owner.NetId);
                changedCards.Add(new LoadoutChangedCard(owner.NetId, index, card.Id, kind));

                foreach (CardModel combatCard in owner.PlayerCombatState?.AllCards
                             .Where(candidate => ReferenceEquals(candidate.DeckVersion, card)) ?? [])
                {
                    CardModificationSpec combatPrevious = ReferenceEquals(combatCard, selectedCard) && selectedPrevious is not null
                        ? selectedPrevious.Clone()
                        : Merge(previousPermanent, GetTemporarySpec(combatCard));
                    RebuildCard(
                        combatCard,
                        combatPrevious,
                        forceAllOwnedFields: restoreAfterEnchantmentClear
                            || ReferenceEquals(combatCard, selectedCard));
                    NotifyCombatCardChanged(combatCard, combatPrevious);
                    refreshedCombatCards.Add(combatCard);
                }
            }
        }

        if (selectedCard is not null
            && selectedCard.Pile?.Type is not null and not PileType.Deck
            && !refreshedCombatCards.Contains(selectedCard)
            && ModelIdMatches(selectedCard, cardId))
        {
            CardModificationSpec previous = selectedPrevious?.Clone() ?? Merge(previousPermanent, GetTemporarySpec(selectedCard));
            RebuildCard(selectedCard, previous, forceAllOwnedFields: true);
            NotifyCombatCardChanged(selectedCard, previous);
        }

        if (changedCards.Count > 0)
        {
            LoadoutRunContentChangeService.Notify(
                LoadoutRunContentKind.Cards,
                changedPlayers,
                LoadoutRunContentChangeMode.Update,
                changedCards);
        }
        LoadoutKeywordRuntimePatches.Reconcile();
    }

    public static void RetrofitChangedPermanentCards(
        IReadOnlyList<ModelId> changedIds,
        IReadOnlyDictionary<ModelId, CardModificationSpec>? previous = null,
        bool forceAllOwnedFields = false)
    {
        if (changedIds.Count == 0 || !TryGetRunState(out RunState? runState))
            return;

        HashSet<ModelId> changedIdsSet = new(changedIds);
        HashSet<ModelId> restoreAfterEnchantmentClear = forceAllOwnedFields
            ? []
            : changedIds
                .Where(cardId => RemovedPermanentEnchantments(
                    previous?.GetValueOrDefault(cardId),
                    PermanentCardModificationStore.Get(cardId)))
                .ToHashSet();
        List<LoadoutChangedCard> changedCards = [];
        HashSet<ulong> changedPlayers = [];
        foreach (Player owner in runState!.Players)
        {
            IReadOnlyList<CardModel> deck = owner.Deck.Cards;
            for (int index = 0; index < deck.Count; index++)
            {
                CardModel card = deck[index];
                if (!changedIdsSet.Contains(card.Id))
                    continue;

                CardModificationSpec previousEffective = Merge(
                    previous?.GetValueOrDefault(card.Id),
                    GetTemporarySpec(card));
                bool restoreAllOwnedFields = forceAllOwnedFields
                    || restoreAfterEnchantmentClear.Contains(card.Id);
                RebuildCard(card, previousEffective, restoreAllOwnedFields);
                CardModificationSpec next = GetEffectiveSpec(card);
                LoadoutCardVisualRefreshKind kind = GetVisualRefreshKind(previousEffective, next);
                RefreshLiveCardVisuals(card, kind);
                changedPlayers.Add(owner.NetId);
                changedCards.Add(new LoadoutChangedCard(owner.NetId, index, card.Id, kind));

                foreach (CardModel combatCard in owner.PlayerCombatState?.AllCards
                             .Where(candidate => ReferenceEquals(candidate.DeckVersion, card)) ?? [])
                {
                    CardModificationSpec combatPrevious = Merge(
                        previous?.GetValueOrDefault(combatCard.Id),
                        GetTemporarySpec(combatCard));
                    RebuildCard(combatCard, combatPrevious, restoreAllOwnedFields);
                    NotifyCombatCardChanged(combatCard, combatPrevious);
                }
            }
        }

        if (changedCards.Count > 0)
        {
            LoadoutRunContentChangeService.Notify(
                LoadoutRunContentKind.Cards,
                changedPlayers,
                LoadoutRunContentChangeMode.Update,
                changedCards);
        }
        LoadoutKeywordRuntimePatches.Reconcile();
    }

    public static void ReconcileAuthoritativeDeckDeltas(
        IReadOnlyDictionary<LoadoutDeckCardIdentity, CardModificationDelta> temporaryDeltas)
    {
        if (!TryGetRunState(out RunState? runState))
            return;

        List<LoadoutChangedCard> changedCards = [];
        HashSet<ulong> changedPlayers = [];
        bool anyChanged = false;
        foreach (Player owner in runState!.Players)
        {
            IReadOnlyList<CardModel> deck = owner.Deck.Cards;
            for (int index = 0; index < deck.Count; index++)
            {
                CardModel card = deck[index];
                LoadoutDeckCardIdentity identity = new(owner.NetId, index, card.Id.ToString());
                temporaryDeltas.TryGetValue(identity, out CardModificationDelta? delta);

                if (!CardModificationFields.MatchesDelta(card, delta))
                {
                    CardModificationSpec previous = GetEffectiveSpec(card);
                    CardModificationFields.SetDelta(card, delta);
                    RebuildCard(card, previous);
                    CardModificationSpec next = GetEffectiveSpec(card);
                    LoadoutCardVisualRefreshKind kind = GetVisualRefreshKind(previous, next);
                    RefreshLiveCardVisuals(card, kind);
                    changedPlayers.Add(owner.NetId);
                    changedCards.Add(new LoadoutChangedCard(owner.NetId, index, card.Id, kind));
                    anyChanged = true;
                }

                foreach (CardModel combatCard in owner.PlayerCombatState?.AllCards
                             .Where(candidate => ReferenceEquals(candidate.DeckVersion, card)) ?? [])
                {
                    if (CardModificationFields.MatchesDelta(combatCard, delta))
                        continue;

                    CardModificationSpec combatPrevious = GetEffectiveSpec(combatCard);
                    CardModificationFields.SetDelta(combatCard, delta);
                    RebuildCard(combatCard, combatPrevious);
                    NotifyCombatCardChanged(combatCard, combatPrevious);
                    anyChanged = true;
                }
            }
        }

        if (!anyChanged)
            return;

        if (changedCards.Count > 0)
        {
            LoadoutRunContentChangeService.Notify(
                LoadoutRunContentKind.Cards,
                changedPlayers,
                LoadoutRunContentChangeMode.Update,
                changedCards);
        }
        LoadoutKeywordRuntimePatches.Reconcile();
    }

    private static bool RemovedPermanentEnchantments(
        CardModificationSpec? previous,
        CardModificationSpec? current)
    {
        return previous?.Enchantments is { Count: > 0 }
            && current?.Enchantments is not { Count: > 0 };
    }

    private static void ApplyTemporaryWithoutBroadcast(
        LoadoutOwnedItem<CardModel> item,
        CardModificationSpec? state)
    {
        CardModificationSpec previous = GetEffectiveSpec(item.Model);
        bool changed = CardModificationFields.Set(item.Model, state);
        if (!changed && state is { IsEmpty: false })
            return;

        RebuildOwnedCard(item, previous, forceAllOwnedFields: state is null || state.IsEmpty);
        NotifyCardUpdated(item, previous, GetEffectiveSpec(item.Model));
        LoadoutKeywordRuntimePatches.Reconcile();
    }

    private static void ApplyTemporaryDeltaWithoutBroadcast(
        LoadoutOwnedItem<CardModel> item,
        CardModificationDelta? delta)
    {
        CardModificationSpec previous = GetEffectiveSpec(item.Model);
        bool changed = CardModificationFields.SetDelta(item.Model, delta);
        if (!changed && delta is { IsEmpty: false })
            return;

        RebuildOwnedCard(item, previous, forceAllOwnedFields: delta is null || delta.IsEmpty);
        NotifyCardUpdated(item, previous, GetEffectiveSpec(item.Model));
        LoadoutKeywordRuntimePatches.Reconcile();
    }

    private static bool ResetTemporaryWithoutBroadcast(
        LoadoutOwnedItem<CardModel> item,
        out LoadoutOwnedItem<CardModel>? replacement)
    {
        replacement = null;
        if (item.CardPileType is not null and not PileType.Deck)
        {
            if (item.Model.Pile?.Type != item.CardPileType
                || item.Model.Owner?.NetId != item.OwnerNetId)
            {
                return false;
            }

            CardModificationSpec previous = GetEffectiveSpec(item.Model);
            CardModificationFields.Clear(item.Model);
            RebuildCard(item.Model, previous, forceAllOwnedFields: true);
            replacement = item;
            NotifyCardUpdated(item, previous, GetEffectiveSpec(item.Model));
            LoadoutKeywordRuntimePatches.Reconcile();
            return true;
        }

        if (!TryReplaceOwnedCardWithFresh(item, out CardModel? freshCard) || freshCard is null)
            return false;

        replacement = new LoadoutOwnedItem<CardModel>(item.Owner, item.Index, freshCard);
        RefreshLiveCardVisuals(freshCard, LoadoutCardVisualRefreshKind.Reload);
        LoadoutRunContentChangeService.NotifyCardUpdated(replacement, LoadoutCardVisualRefreshKind.Reload);
        OwnedCardChanged?.Invoke(replacement, LoadoutCardVisualRefreshKind.Reload);
        LoadoutKeywordRuntimePatches.Reconcile();
        return true;
    }

    private static bool TryReplaceOwnedCardWithFresh(
        LoadoutOwnedItem<CardModel> item,
        out CardModel? freshCard)
    {
        freshCard = null;
        Player owner = item.Owner;
        CardModel oldCard = item.Model;
        CardModel? canonical = LoadoutModelRegistry.ResolveCard(oldCard.Id);
        if (canonical is null
            || item.Index < 0
            || item.Index >= owner.Deck.Cards.Count
            || !ReferenceEquals(owner.Deck.Cards[item.Index], oldCard)
            || oldCard.Pile?.Type != PileType.Deck
            || CardPileCardsField?.GetValue(owner.Deck) is not List<CardModel> deckCards
            || item.Index >= deckCards.Count
            || !ReferenceEquals(deckCards[item.Index], oldCard))
        {
            GD.PushWarning($"CardModification: fresh reset rejected stale card '{oldCard.Id}' at deck index {item.Index} for player {owner.NetId}.");
            return false;
        }

        CardModel created;
        try
        {
            created = owner.RunState.CreateCard(canonical, owner);
            created.FloorAddedToDeck = oldCard.FloorAddedToDeck;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: failed creating fresh card '{oldCard.Id}'. {exception.Message}");
            return false;
        }

        bool deckReplaced = false;
        bool oldUnregistered = false;
        List<CardModel> linkedCombatCards = owner.PlayerCombatState?.AllCards
            .Where(card => ReferenceEquals(card.DeckVersion, oldCard))
            .ToList()
            ?? [];
        try
        {
            deckCards[item.Index] = created;
            deckReplaced = true;
            foreach (CardModel combatCard in linkedCombatCards)
                combatCard.DeckVersion = created;
            owner.RunState.RemoveCard(oldCard);
            oldUnregistered = true;
            oldCard.HasBeenRemovedFromState = true;
            owner.Deck.InvokeContentsChanged();
            owner.Deck.InvokeCardRemoveFinished();
            owner.Deck.InvokeCardAddFinished();
            freshCard = created;
            return true;
        }
        catch (Exception exception)
        {
            try
            {
                if (deckReplaced && item.Index < deckCards.Count && ReferenceEquals(deckCards[item.Index], created))
                    deckCards[item.Index] = oldCard;
                foreach (CardModel combatCard in linkedCombatCards)
                {
                    if (ReferenceEquals(combatCard.DeckVersion, created))
                        combatCard.DeckVersion = oldCard;
                }

                if (oldUnregistered && !owner.RunState.ContainsCard(oldCard))
                {
                    oldCard.HasBeenRemovedFromState = false;
                    owner.RunState.AddCard(oldCard, owner);
                }

                if (owner.RunState.ContainsCard(created))
                {
                    owner.RunState.RemoveCard(created);
                    created.HasBeenRemovedFromState = true;
                }
            }
            catch (Exception rollbackException)
            {
                GD.PushError($"CardModification: fresh reset rollback failed for '{oldCard.Id}'. {rollbackException}");
            }

            GD.PushWarning($"CardModification: failed replacing '{oldCard.Id}' with a fresh card. {exception.Message}");
            return false;
        }
    }

    private static void ApplyPermanentWithoutBroadcast(
        LoadoutOwnedItem<CardModel> item,
        ModelId cardId,
        CardModificationSpec? state,
        bool authoritativeRemote)
    {
        CardModificationSpec previousPermanent = PermanentCardModificationStore.Get(cardId);
        CardModificationSpec selectedPrevious = GetEffectiveSpec(item.Model);
        bool temporaryChanged = CardModificationFields.Clear(item.Model);
        bool permanentChanged;
        if (authoritativeRemote)
            permanentChanged = PermanentCardModificationStore.ApplyHostDelta(cardId, state);
        else if (IsPermanentAuthority())
            permanentChanged = PermanentCardModificationStore.SetProfile(cardId, state);
        else
            permanentChanged = false;

        if (permanentChanged)
            RetrofitLiveDeckCopies(cardId, previousPermanent, item.Model, selectedPrevious);
        else if (temporaryChanged || state is null || state.IsEmpty)
        {
            RebuildOwnedCard(item, selectedPrevious, forceAllOwnedFields: state is null || state.IsEmpty);
            NotifyCardUpdated(item, selectedPrevious, GetEffectiveSpec(item.Model));
        }
        LoadoutKeywordRuntimePatches.Reconcile();
    }

    private static void ApplyPermanentDeltaWithoutBroadcast(
        LoadoutOwnedItem<CardModel> item,
        ModelId cardId,
        CardModificationDelta? delta,
        bool authoritativeRemote)
    {
        CardModificationSpec previousPermanent = PermanentCardModificationStore.Get(cardId);
        CardModificationSpec selectedPrevious = GetEffectiveSpec(item.Model);
        bool temporaryChanged = CardModificationFields.Clear(item.Model);
        bool permanentChanged;
        if (authoritativeRemote)
            permanentChanged = PermanentCardModificationStore.ApplyHostDelta(cardId, delta);
        else if (IsPermanentAuthority())
            permanentChanged = PermanentCardModificationStore.SetProfileDelta(cardId, delta);
        else
            permanentChanged = false;

        if (permanentChanged)
            RetrofitLiveDeckCopies(cardId, previousPermanent, item.Model, selectedPrevious);
        else if (temporaryChanged || delta is null || delta.IsEmpty)
        {
            RebuildOwnedCard(item, selectedPrevious, forceAllOwnedFields: delta is null || delta.IsEmpty);
            NotifyCardUpdated(item, selectedPrevious, GetEffectiveSpec(item.Model));
        }
        LoadoutKeywordRuntimePatches.Reconcile();
    }

    private static void RebuildOwnedCard(
        LoadoutOwnedItem<CardModel> item,
        CardModificationSpec previous,
        bool forceAllOwnedFields = false)
    {
        RebuildCard(item.Model, previous, forceAllOwnedFields);
    }

    private static void RebuildCard(
        CardModel card,
        CardModificationSpec previous,
        bool forceAllOwnedFields = false)
    {
        CardModificationSpec permanent = PermanentCardModificationStore.Get(card.Id);
        CardModificationSpec temporary = GetTemporarySpec(card);
        CardModificationSpec next = Merge(permanent, temporary);
        CardModel? canonical = LoadoutModelRegistry.ResolveCard(card.Id);
        if (canonical is null)
            return;

        CardModel baseline = CreateBaseline(
            canonical,
            card.CurrentUpgradeLevel,
            LoadoutKeywordRuntimePatches.GetInfiniteUpgradeOverride(next),
            next.UpgradeModification,
            next.KeywordOverrides);
        CopyNativeFields(baseline, card, previous, next, forceAllOwnedFields);
        ApplySpecToCard(card, temporary);
        card.FinalizeUpgradeInternal();
    }

    private static CardModel CreateBaseline(
        CardModel canonical,
        int upgradeLevel,
        bool? infiniteUpgradeOverride = null,
        CardUpgradeModificationSpec? upgradeModification = null,
        IReadOnlyDictionary<string, bool>? baseKeywordOverrides = null)
    {
        CardModel baseline;
        using (SuppressPermanentApplication())
            baseline = canonical.ToMutable();

        if (PermanentCardModificationStore.TryGetDelta(canonical.Id, out CardModificationDelta? permanent))
            ApplyPermanentResidual(baseline, permanent);

        if (baseKeywordOverrides is not null)
        {
            ApplyKeywordOverrides(baseline, baseKeywordOverrides);
            LoadoutKeywordRegistry.SynchronizeDynamicVars(baseline);
        }

        if (infiniteUpgradeOverride.HasValue)
        {
            bool hasInfiniteUpgrade = LoadoutKeywords.Has(baseline, LoadoutKeywords.InfiniteUpgrade);
            if (infiniteUpgradeOverride.Value && !hasInfiniteUpgrade)
                baseline.AddKeyword(LoadoutKeywords.InfiniteUpgrade);
            else if (!infiniteUpgradeOverride.Value && hasInfiniteUpgrade)
                baseline.RemoveKeyword(LoadoutKeywords.InfiniteUpgrade);
        }

        bool useInfiniteUpgradeValues = infiniteUpgradeOverride
                                        ?? LoadoutKeywords.Has(
                                            baseline,
                                            LoadoutKeywords.InfiniteUpgrade);
        bool? useUpgradedInfiniteUpgradeValues =
            LoadoutKeywordRuntimePatches.GetInfiniteUpgradeOverride(
                upgradeModification);
        if (useInfiniteUpgradeValues
            || useUpgradedInfiniteUpgradeValues == true)
        {
            LoadoutKeywordRuntimePatches.EnsureInfiniteUpgradeEnabled();
        }

        int count = Math.Max(0, upgradeLevel);
        InfiniteUpgradeDeserializationState deserializationState =
            InfiniteUpgradeMaxLevelPatch.BeginDeserialization(
                count,
                useInfiniteUpgradeValues,
                useUpgradedInfiniteUpgradeValues);
        IDisposable upgradeScope =
            CardUpgradeModificationRuntimePatches.BeginOverride(upgradeModification);
        try
        {
            for (int i = 0; i < count && baseline.IsUpgradable; i++)
            {
                baseline.UpgradeInternal();
                baseline.FinalizeUpgradeInternal();
            }
        }
        finally
        {
            upgradeScope.Dispose();
            InfiniteUpgradeMaxLevelPatch.EndDeserialization(deserializationState);
        }
        return baseline;
    }

    private static void CopyNativeFields(
        CardModel source,
        CardModel destination,
        CardModificationSpec previous,
        CardModificationSpec next,
        bool forceAllOwnedFields = false)
    {
        HashSet<string> keywordKeys = new(previous.KeywordOverrides.Keys, StringComparer.Ordinal);
        keywordKeys.UnionWith(next.KeywordOverrides.Keys);
        keywordKeys.UnionWith(previous.UpgradeModification.KeywordOverrides.Keys);
        keywordKeys.UnionWith(next.UpgradeModification.KeywordOverrides.Keys);
        bool hasUpgradeEnergyDefinition =
            previous.UpgradeModification.EnergyCostDelta.HasValue
            || next.UpgradeModification.EnergyCostDelta.HasValue;
        bool hasUpgradeReplayDefinition =
            previous.UpgradeModification.BaseReplayCountDelta.HasValue
            || next.UpgradeModification.BaseReplayCountDelta.HasValue;
        bool hasUpgradeStarDefinition =
            previous.UpgradeModification.BaseStarCostDelta.HasValue
            || next.UpgradeModification.BaseStarCostDelta.HasValue;

        if (forceAllOwnedFields)
        {
            CardEnergyCostField?.SetValue(
                destination,
                new CardEnergyCost(destination, source.EnergyCost.Canonical, source.EnergyCost.CostsX));
            destination.BaseReplayCount = source.BaseReplayCount;
            SetBaseStarCost(destination, source.BaseStarCost);
            CardPoolField?.SetValue(destination, source.Pool);
            CardTypeField?.SetValue(destination, source.Type);
            CardRarityField?.SetValue(destination, source.Rarity);
            CardKeywordsField?.SetValue(
                destination,
                source.GetKeywordsWithSources(KeywordSources.Local).ToHashSet());
        }

        if (!forceAllOwnedFields
            && (previous.EnergyCost.HasValue
             || next.EnergyCost.HasValue
             || hasUpgradeEnergyDefinition
             || keywordKeys.Contains(LoadoutKeywords.XCostKey))
            && !destination.EnergyCost.CostsX)
        {
            SetEnergyCost(destination, source.EnergyCost.Canonical);
        }
        if (!forceAllOwnedFields
            && (previous.BaseReplayCount.HasValue
                || next.BaseReplayCount.HasValue
                || hasUpgradeReplayDefinition))
            destination.BaseReplayCount = source.BaseReplayCount;
        if (!forceAllOwnedFields
            && (previous.BaseStarCost.HasValue
                || next.BaseStarCost.HasValue
                || hasUpgradeStarDefinition))
            SetBaseStarCost(destination, source.BaseStarCost);

        if (!forceAllOwnedFields && (previous.PoolId is not null || next.PoolId is not null))
            CardPoolField?.SetValue(destination, source.Pool);
        if (!forceAllOwnedFields && (previous.Type is not null || next.Type is not null))
            CardTypeField?.SetValue(destination, source.Type);
        if (!forceAllOwnedFields && (previous.Rarity is not null || next.Rarity is not null))
            CardRarityField?.SetValue(destination, source.Rarity);

        if (!forceAllOwnedFields)
        {
            IReadOnlySet<CardKeyword> desiredKeywords = source.GetKeywordsWithSources(KeywordSources.Local);
            IReadOnlySet<CardKeyword> currentKeywords = destination.GetKeywordsWithSources(KeywordSources.Local);
            foreach (string rawKeyword in keywordKeys)
            {
                if (!LoadoutKeywords.TryResolve(rawKeyword, out CardKeyword keyword))
                    continue;

                if (desiredKeywords.Contains(keyword) && !currentKeywords.Contains(keyword))
                    destination.AddKeyword(keyword);
                else if (!desiredKeywords.Contains(keyword) && currentKeywords.Contains(keyword))
                    destination.RemoveKeyword(keyword);
            }
        }

        LoadoutKeywordRegistry.SynchronizeDynamicVars(destination);
        if (forceAllOwnedFields)
        {
            if (DynamicVarDictionaryField?.GetValue(destination.DynamicVars)
                    is Dictionary<string, DynamicVar> destinationVars)
            {
                destinationVars.Clear();
                foreach ((string name, DynamicVar sourceVar) in source.DynamicVars)
                {
                    DynamicVar clone = sourceVar.Clone();
                    clone.SetOwner(destination);
                    destinationVars[name] = clone;
                }
            }
        }
        else
        {
            HashSet<string> dynamicVarNames = new(previous.DynamicVars.Keys, StringComparer.Ordinal);
            dynamicVarNames.UnionWith(next.DynamicVars.Keys);
            dynamicVarNames.UnionWith(previous.UpgradeModification.DynamicVarDeltas.Keys);
            dynamicVarNames.UnionWith(next.UpgradeModification.DynamicVarDeltas.Keys);
            foreach (string name in dynamicVarNames)
            {
                if (source.DynamicVars.TryGetValue(name, out var sourceVar)
                    && destination.DynamicVars.TryGetValue(name, out var destinationVar))
                {
                    destinationVar.BaseValue = sourceVar.BaseValue;
                }
            }
        }

        if (keywordKeys.Contains(LoadoutKeywords.XCostKey))
        {
            Dictionary<string, bool> xCostOverrides =
                new(next.KeywordOverrides, StringComparer.Ordinal)
                {
                    [LoadoutKeywords.XCostKey] =
                        LoadoutKeywords.Has(source, LoadoutKeywords.XCost)
                };
            XCostKeywordMechanics.SynchronizeEnergyCost(
                destination,
                xCostOverrides,
                next.EnergyCost);
        }
        if (!forceAllOwnedFields
            && (previous.EnergyCost.HasValue
             || next.EnergyCost.HasValue
             || hasUpgradeEnergyDefinition
             || keywordKeys.Contains(LoadoutKeywords.XCostKey))
            && !source.EnergyCost.CostsX
            && !destination.EnergyCost.CostsX)
        {
            SetEnergyCost(destination, source.EnergyCost.Canonical);
        }

        if (previous.Enchantments is not null || next.Enchantments is not null)
            CopyEnchantments(source, destination);
        if (previous.Affliction is not null || next.Affliction is not null)
            CopyAffliction(source, destination);
    }

    private static void CopyEnchantments(CardModel source, CardModel destination)
    {
        if (!MultiEnchantmentBridge.Available)
        {
            if (destination.Enchantment is not null)
                CardCmd.ClearEnchantment(destination);
            if (source.Enchantment is not null
                && TryResolveModel(source.Enchantment.Id.ToString(), ModelDb.DebugEnchantments, out EnchantmentModel? canonical))
            {
                ForceApplyEnchantment(destination, canonical!, Math.Max(1, source.Enchantment.Amount));
            }
            return;
        }

        IReadOnlyList<EnchantmentModel> desired = MultiEnchantmentBridge.GetAll(source);
        ReconcileEnchantments(
            destination,
            desired,
            enchantment => MultiEnchantmentBridge.Copy(destination, enchantment),
            stackAmountIncreases: false);
    }

    private static void CopyAffliction(CardModel source, CardModel destination)
    {
        if (destination.Affliction is not null)
            CardCmd.ClearAffliction(destination);
        if (source.Affliction is not null
            && TryResolveModel(source.Affliction.Id.ToString(), ModelDb.DebugAfflictions, out AfflictionModel? canonical))
        {
            ForceApplyAffliction(destination, canonical!, Math.Max(1, source.Affliction.Amount));
        }
    }

    private static void NotifyCardUpdated(
        LoadoutOwnedItem<CardModel> item,
        CardModificationSpec previous,
        CardModificationSpec next)
    {
        LoadoutCardVisualRefreshKind kind = GetVisualRefreshKind(previous, next);
        RefreshLiveCardVisuals(item.Model, kind);
        if (item.CardPileType is null or PileType.Deck)
            LoadoutRunContentChangeService.NotifyCardUpdated(item, kind);
        OwnedCardChanged?.Invoke(item, kind);
    }

    private static void NotifyCombatCardChanged(CardModel card, CardModificationSpec previous)
    {
        LoadoutCardVisualRefreshKind kind = GetVisualRefreshKind(previous, GetEffectiveSpec(card));
        RefreshLiveCardVisuals(card, kind);
        if (card.Owner is null
            || card.Pile is null
            || !NetCombatCardDb.Instance.TryGetCardId(card, out uint combatCardIndex))
        {
            return;
        }

        int index = card.Pile.Cards.ToList().IndexOf(card);
        if (index < 0)
            return;

        OwnedCardChanged?.Invoke(
            new LoadoutOwnedItem<CardModel>(card.Owner, index, card, card.Pile.Type, combatCardIndex),
            kind);
    }

    private static void RefreshLiveCardVisuals(
        CardModel card,
        LoadoutCardVisualRefreshKind refreshKind)
    {
        try
        {
            object? found = NCardFindOnTableByCard?.Invoke(null, [card]);
            found ??= NCardFindOnTableByCardAndPile?.Invoke(null, [card, card.Pile?.Type ?? PileType.None]);
            if (found is not NCard node)
                return;

            PileType pileType = card.Pile?.Type ?? PileType.None;
            if (refreshKind == LoadoutCardVisualRefreshKind.Reload)
            {
                node.Model = null;
                node.Model = card;
            }
            node.UpdateVisuals(pileType, CardPreviewMode.Normal);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: could not refresh visible card '{card.Id}'. {exception.Message}");
        }
    }

    private static LoadoutOwnedItem<CardModel>? TryResolveOwnedCard(
        LoadoutTargetSelection target,
        int deckIndex,
        LoadoutCardPileTarget pileTarget,
        uint combatCardIndex,
        ModelId expectedModelId,
        Player actionPlayer)
    {
        Player? owner = target.Scope == LoadoutTargetScope.Player && target.PlayerNetId.HasValue
            ? actionPlayer.RunState.GetPlayer(target.PlayerNetId.Value)
            : actionPlayer;
        if (owner is null || !pileTarget.NormalizeForOwnedCard().TryGetPileType(out PileType expectedPileType))
            return null;

        if (expectedPileType == PileType.Deck)
        {
            if (deckIndex < 0 || deckIndex >= owner.Deck.Cards.Count)
                return null;

            CardModel deckCard = owner.Deck.Cards[deckIndex];
            return ModelIdMatches(deckCard, expectedModelId) && deckCard.Pile?.Type == PileType.Deck
                ? new LoadoutOwnedItem<CardModel>(owner, deckIndex, deckCard, PileType.Deck, null)
                : null;
        }

        if (!NetCombatCardDb.Instance.TryGetCard(combatCardIndex, out CardModel? combatCard)
            || combatCard is null
            || combatCard.Owner?.NetId != owner.NetId
            || combatCard.Pile?.Type != expectedPileType
            || !ModelIdMatches(combatCard, expectedModelId))
        {
            return null;
        }

        int pileIndex = combatCard.Pile.Cards.ToList().IndexOf(combatCard);
        return pileIndex < 0
            ? null
            : new LoadoutOwnedItem<CardModel>(owner, pileIndex, combatCard, expectedPileType, combatCardIndex);
    }

    private static bool TryResolveLiveCard(
        ulong ownerNetId,
        int deckIndex,
        LoadoutCardPileTarget pileTarget,
        uint combatCardIndex,
        string cardId,
        out LoadoutOwnedItem<CardModel>? item)
    {
        item = default;
        if (!TryGetRunState(out RunState? runState))
            return false;

        Player? owner = runState!.GetPlayer(ownerNetId);
        if (owner is null || !pileTarget.NormalizeForOwnedCard().TryGetPileType(out PileType expectedPileType))
            return false;

        CardModel? card;
        int resolvedIndex;
        if (expectedPileType == PileType.Deck)
        {
            if (deckIndex < 0 || deckIndex >= owner.Deck.Cards.Count)
                return false;
            card = owner.Deck.Cards[deckIndex];
            resolvedIndex = deckIndex;
        }
        else
        {
            if (!NetCombatCardDb.Instance.TryGetCard(combatCardIndex, out card)
                || card is null
                || card.Owner?.NetId != ownerNetId)
            {
                return false;
            }
            resolvedIndex = card.Pile?.Cards.ToList().IndexOf(card) ?? -1;
        }

        if (!MatchesModelId(card, cardId) || card.Pile?.Type != expectedPileType || resolvedIndex < 0)
            return false;

        item = new LoadoutOwnedItem<CardModel>(
            owner,
            resolvedIndex,
            card,
            expectedPileType,
            expectedPileType == PileType.Deck ? null : combatCardIndex);
        return true;
    }

    private static bool TryGetRunState(out RunState? runState)
    {
        runState = null;
        try
        {
            if (!RunManager.Instance.IsInProgress)
                return false;
            runState = RunManager.Instance.DebugOnlyGetState();
            return runState is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPermanentAuthority()
    {
        try
        {
            return !RunManager.Instance.IsInProgress
                   || RunManager.Instance.NetService.Type != NetGameType.Client;
        }
        catch
        {
            return true;
        }
    }

    private static CardModificationSpec Merge(CardModificationSpec? permanent, CardModificationSpec? temporary)
    {
        CardModificationSpec result = permanent?.Clone() ?? new CardModificationSpec();
        result.MergeFrom(temporary);
        result.Normalize();
        return result;
    }

    private static CardModificationSpec Normalize(CardModificationSpec value)
    {
        CardModificationSpec result = value.Clone();
        result.Normalize();
        return result;
    }

    private static bool TryGetEffectiveValue(
        CardModel card,
        Func<CardModificationDelta, string?> selector,
        out string value)
    {
        if (PreviewDeltas.TryGetValue(card, out CardModificationDelta? preview))
        {
            string? previewValue = selector(preview);
            if (!string.IsNullOrWhiteSpace(previewValue))
            {
                value = previewValue;
                return true;
            }
        }

        if (CardModificationFields.TryGet(card, out CardModificationCardData data))
        {
            string? attached = selector(data.Delta);
            if (!string.IsNullOrWhiteSpace(attached))
            {
                value = attached;
                return true;
            }
        }

        if (PermanentCardModificationStore.TryGetDelta(card.Id, out CardModificationDelta? permanent))
        {
            string? stored = selector(permanent);
            if (!string.IsNullOrWhiteSpace(stored))
            {
                value = stored;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool SameStructuralValue(string? left, string? right)
    {
        return string.Equals(
            string.IsNullOrWhiteSpace(left) ? string.Empty : left.Trim(),
            string.IsNullOrWhiteSpace(right) ? string.Empty : right.Trim(),
            StringComparison.Ordinal);
    }

    private static int AddIntDeltaClamped(int value, int delta)
    {
        return (int)Math.Clamp((long)value + delta, int.MinValue, int.MaxValue);
    }

    private static int SubtractIntClamped(int value, int baseline)
    {
        return (int)Math.Clamp((long)value - baseline, int.MinValue, int.MaxValue);
    }

    private static void SetEnergyCost(CardModel card, int value)
    {
        card.EnergyCost.SetCustomBaseCost(value);
        EnergyCostCanonicalField?.SetValue(card.EnergyCost, value);
    }

    private static void SetBaseStarCost(CardModel card, int value)
    {
        if (BaseStarCostSetter is not null)
            BaseStarCostSetter.Invoke(card, [value]);
    }

    private static void ApplyKeywordOverrides(
        CardModel card,
        IReadOnlyDictionary<string, bool> overrides)
    {
        foreach ((string rawKeyword, bool enabled) in overrides)
        {
            if (!LoadoutKeywords.TryResolve(rawKeyword, out CardKeyword keyword) || keyword == CardKeyword.None)
                continue;
            bool present = card.GetKeywordsWithSources(KeywordSources.Local).Contains(keyword);
            if (enabled && !present)
                card.AddKeyword(keyword);
            else if (!enabled && present)
                card.RemoveKeyword(keyword);
        }
    }

    private static void ApplyEnchantmentSpecs(
        CardModel card,
        IReadOnlyList<CardAttachmentSpec>? specs)
    {
        if (specs is null)
            return;

        if (!MultiEnchantmentBridge.Available)
        {
            CardAttachmentSpec? spec = specs.FirstOrDefault();
            if (spec is null)
            {
                if (card.Enchantment is not null)
                    CardCmd.ClearEnchantment(card);
                return;
            }

            if (!TryResolveModel(spec.ModelId, ModelDb.DebugEnchantments, out EnchantmentModel? canonical))
                return;
            if (card.Enchantment is not null)
                CardCmd.ClearEnchantment(card);
            ForceApplyEnchantment(card, canonical!, Math.Max(1, spec.Amount));
            return;
        }

        IReadOnlyList<EnchantmentModel> current = MultiEnchantmentBridge.GetAll(card);
        List<EnchantmentModel> desired = [];
        foreach (CardAttachmentSpec spec in specs)
        {
            EnchantmentModel? source;
            if (TryResolveModel(spec.ModelId, ModelDb.DebugEnchantments, out EnchantmentModel? canonical))
            {
                source = canonical;
            }
            else
            {
                source = current.FirstOrDefault(enchantment =>
                    spec.ModelId is not null && MatchesModelId(enchantment, spec.ModelId));
            }
            if (source is null)
                continue;

            EnchantmentModel mutable = CloneEnchantmentForApplication(source);
            mutable.Amount = Math.Max(1, spec.Amount);
            desired.Add(mutable);
        }

        ReconcileEnchantments(
            card,
            desired,
            enchantment => MultiEnchantmentBridge.Add(card, enchantment, enchantment.Amount),
            stackAmountIncreases: true);
    }

    private static void ApplyAfflictionSpec(CardModel card, CardAttachmentSpec? spec)
    {
        if (spec is null)
            return;
        if (spec.Clear)
        {
            if (card.Affliction is not null)
                CardCmd.ClearAffliction(card);
            return;
        }
        if (!TryResolveModel(spec.ModelId, ModelDb.DebugAfflictions, out AfflictionModel? canonical))
            return;
        if (card.Affliction is not null)
            CardCmd.ClearAffliction(card);
        ForceApplyAffliction(card, canonical!, Math.Max(1, spec.Amount));
    }

    private static void ForceApplyEnchantment(CardModel card, EnchantmentModel canonical, int amount)
    {
        if (card.Enchantment is null)
            card.EnchantInternal(canonical.ToMutable(), amount);
        else
            card.Enchantment.Amount = amount;
        card.Enchantment?.ModifyCard();
        card.FinalizeUpgradeInternal();
    }

    private static void ReconcileEnchantments(
        CardModel card,
        IReadOnlyList<EnchantmentModel> desired,
        Func<EnchantmentModel, bool> add,
        bool stackAmountIncreases)
    {
        List<EnchantmentModel> current = MultiEnchantmentBridge.GetAll(card).ToList();
        bool[] matched = new bool[desired.Count];

        foreach (EnchantmentModel existing in current)
        {
            int desiredIndex = FindUnmatchedEnchantment(desired, matched, existing);
            if (desiredIndex < 0)
            {
                MultiEnchantmentBridge.Remove(card, existing);
                continue;
            }

            matched[desiredIndex] = true;
            EnchantmentModel target = desired[desiredIndex];
            int currentAmount = Math.Max(1, existing.Amount);
            int desiredAmount = Math.Max(1, target.Amount);
            if (currentAmount == desiredAmount)
                continue;

            if (stackAmountIncreases && currentAmount < desiredAmount)
            {
                EnchantmentModel additional = CloneEnchantmentForApplication(target);
                additional.Amount = desiredAmount - currentAmount;
                MultiEnchantmentBridge.Add(card, additional, additional.Amount);
                continue;
            }

            if (MultiEnchantmentBridge.Remove(card, existing))
                add(target);
        }

        for (int i = 0; i < desired.Count; i++)
        {
            if (!matched[i])
                add(desired[i]);
        }
    }

    private static int FindUnmatchedEnchantment(
        IReadOnlyList<EnchantmentModel> desired,
        IReadOnlyList<bool> matched,
        EnchantmentModel existing)
    {
        for (int i = 0; i < desired.Count; i++)
        {
            if (!matched[i] && MatchesModelId(desired[i], existing.Id.ToString()))
                return i;
        }
        return -1;
    }

    private static EnchantmentModel CloneEnchantmentForApplication(EnchantmentModel source)
    {
        return source.IsCanonical
            ? source.ToMutable()
            : (EnchantmentModel)source.ClonePreservingMutability();
    }

    private static void ForceApplyAffliction(CardModel card, AfflictionModel canonical, int amount)
    {
        if (card.Affliction is null)
        {
            AfflictionModel mutable = canonical.ToMutable();
            mutable.Amount = amount;
            card.AfflictInternal(mutable, amount);
        }
        else
        {
            card.Affliction.Amount = amount;
        }
        card.Affliction?.AfterApplied();
    }

    private static void ApplyPermanentResidual(CardModel card, CardModificationDelta permanent)
    {
        ApplyEnchantmentSpecs(card, permanent.Enchantments);
        ApplyAfflictionSpec(card, permanent.Affliction);
    }

    private static bool TryResolveModel<TModel>(
        string? id,
        IEnumerable<TModel> models,
        out TModel? model)
        where TModel : AbstractModel
    {
        model = null;
        if (string.IsNullOrWhiteSpace(id))
            return false;
        foreach (TModel candidate in models)
        {
            if (MatchesModelId(candidate, id))
            {
                model = candidate;
                return true;
            }
        }
        return false;
    }

    private static bool MatchesModelId(AbstractModel model, string id)
    {
        return string.Equals(model.Id.ToString(), id, StringComparison.Ordinal)
               || string.Equals(model.Id.Entry, id, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ModelIdMatches(AbstractModel model, ModelId id)
    {
        return id == ModelId.none
               || model.Id == id
               || string.Equals(model.Id.ToString(), id.ToString(), StringComparison.Ordinal)
               || string.Equals(model.Id.Entry, id.Entry, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseEnum<TEnum>(string? value, out TEnum result)
        where TEnum : struct, Enum
    {
        return Enum.TryParse(value, ignoreCase: true, out result);
    }

    private static IDisposable SuppressPermanentApplication()
    {
        _suppressPermanentApplyDepth++;
        return new PermanentSuppressionScope();
    }

    private static void OnPermanentCardChanged(ModelId cardId)
    {
        PermanentCardModificationStore.TryGetDelta(cardId, out CardModificationDelta? delta);
        CanonicalCardModificationRegistry.Reconcile(cardId, delta);
        if (delta is not null)
        {
            if (delta.HasCustomText) MarkCustomTextOverridesPresent();
            if (delta.HasPortraitOverride) CardModificationDynamicPatches.EnablePortraitPatches();
            if (!delta.UpgradeModification.IsEmpty) CardUpgradeModificationRuntimePatches.Enable();
        }
        AttachmentDisplayCards.Remove(cardId);
        Interlocked.Increment(ref _permanentDisplayRevision);
        PermanentCardDisplayChanged?.Invoke(cardId);
        LoadoutKeywordRuntimePatches.Reconcile();
    }

    private static void OnPermanentStoreReloaded()
    {
        IReadOnlyList<ModelId> reconciledIds = CanonicalCardModificationRegistry.ReconcileAll();
        // New permanent Infinite Upgrade definitions must install their upgrade
        // boundary before existing live copies are rebuilt below.
        LoadoutKeywordRuntimePatches.Reconcile();
        AttachmentDisplayCards.Clear();
        if (PermanentCardModificationStore.HasAnyCustomText) MarkCustomTextOverridesPresent();
        if (PermanentCardModificationStore.HasAnyPortraitOverrides) CardModificationDynamicPatches.EnablePortraitPatches();
        if (PermanentCardModificationStore.HasAnyUpgradeModifications) CardUpgradeModificationRuntimePatches.Enable();
        // Profile swaps replace the whole durable snapshot, so there is no old
        // per-field spec left to drive a selective copy. Rebuild all fields owned
        // by the card editor for matching live cards once, preserving their sparse
        // temporary deltas on top of the newly reconciled canonical model.
        RetrofitChangedPermanentCards(reconciledIds, forceAllOwnedFields: true);
        foreach (ModelId id in reconciledIds)
        {
            Interlocked.Increment(ref _permanentDisplayRevision);
            PermanentCardDisplayChanged?.Invoke(id);
        }
        LoadoutKeywordRuntimePatches.Reconcile();
    }

    private sealed class PermanentSuppressionScope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _suppressPermanentApplyDepth = Math.Max(0, _suppressPermanentApplyDepth - 1);
        }
    }
}
