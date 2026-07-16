#nullable enable

namespace Loadout.Patches.Cards.CardModification;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using Loadout.Keywords;
using Loadout.Services.Actions;
using Loadout.Services.CardModification;
using Loadout.Services.Targets;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
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
    private static readonly FieldInfo? CardTypeField = AccessTools.Field(typeof(CardModel), "<Type>k__BackingField");
    private static readonly FieldInfo? CardRarityField = AccessTools.Field(typeof(CardModel), "<Rarity>k__BackingField");
    private static readonly FieldInfo? CardPoolField = AccessTools.Field(typeof(CardModel), "_pool");
    private static readonly FieldInfo? EnergyCostCanonicalField = AccessTools.Field(typeof(CardEnergyCost), "<Canonical>k__BackingField");
    private static readonly MethodInfo? BaseStarCostSetter = AccessTools.PropertySetter(typeof(CardModel), nameof(CardModel.BaseStarCost));
    private static readonly MethodInfo? NCardFindOnTableByCard = AccessTools.Method(typeof(NCard), nameof(NCard.FindOnTable), [typeof(CardModel)]);
    private static readonly MethodInfo? NCardFindOnTableByCardAndPile = AccessTools.Method(typeof(NCard), nameof(NCard.FindOnTable), [typeof(CardModel), typeof(PileType)]);
    private static readonly Dictionary<ModelId, CardModel> DisplayCards = new();
    private static ConditionalWeakTable<CardModel, CardModificationDelta> PreviewDeltas = new();

    [ThreadStatic]
    private static int _suppressPermanentApplyDepth;

    [ThreadStatic]
    private static Stack<CardModel>? _locStringContext;

    private static bool _registered;

    public static event Action<ModelId>? PermanentCardDisplayChanged;

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
        if (PermanentCardModificationStore.HasAnyCustomText) CardModificationDynamicPatches.EnableTextPatches();
        if (PermanentCardModificationStore.HasAnyPortraitOverrides) CardModificationDynamicPatches.EnablePortraitPatches();
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
        DisplayCards.Clear();
        PreviewDeltas = new ConditionalWeakTable<CardModel, CardModificationDelta>();
        CardModificationDynamicPatches.ClearAll();
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
        if (PreviewDeltas.TryGetValue(card, out CardModificationDelta? preview) && preview.HasCustomText)
            return true;
        if (CardModificationFields.TryGet(card, out CardModificationCardData data) && data.Delta.HasCustomText)
            return true;

        return PermanentCardModificationStore.TryGetDelta(card.Id, out CardModificationDelta? permanent)
               && permanent.HasCustomText;
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
            ? LoadoutCardVisualRefreshKind.Lightweight
            : LoadoutCardVisualRefreshKind.Reload;
    }

    public static bool SpecsEquivalent(CardModificationSpec? left, CardModificationSpec? right)
    {
        string a = left is null ? string.Empty : CardModificationCodec.Serialize(Normalize(left));
        string b = right is null ? string.Empty : CardModificationCodec.Serialize(Normalize(right));
        return string.Equals(a, b, StringComparison.Ordinal);
    }

    /// <summary>Called once by the CardModel.ToMutable postfix.</summary>
    public static void ApplyPermanentAtCreation(CardModel card)
    {
        if (card.IsCanonical || IsPermanentApplicationSuppressed)
            return;

        if (PermanentCardModificationStore.TryGetDelta(card.Id, out CardModificationDelta? permanent))
            ApplyDeltaToCard(card, permanent);
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

            ApplyKeywordOverrides(card, spec.KeywordOverrides);
            LoadoutKeywordMechanics.SynchronizeEnergyCost(card, spec.KeywordOverrides, spec.EnergyCost);
            ApplyEnchantmentSpec(card, spec.Enchantment);
            if (includeAffliction)
                ApplyAfflictionSpec(card, spec.Affliction);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: failed applying fields to '{card.Id}'. {exception.Message}");
        }
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
            foreach ((string name, decimal value) in delta.DynamicVarDeltas)
            {
                if (card.DynamicVars.TryGetValue(name, out var dynamicVar)) dynamicVar.BaseValue += value;
            }

            if (TryResolveModel(delta.PoolId, ModelDb.AllCardPools, out CardPoolModel? pool)) CardPoolField?.SetValue(card, pool);
            if (TryParseEnum(delta.Type, out CardType type)) CardTypeField?.SetValue(card, type);
            if (TryParseEnum(delta.Rarity, out CardRarity rarity)) CardRarityField?.SetValue(card, rarity);
            ApplyKeywordOverrides(card, delta.KeywordOverrides);
            LoadoutKeywordMechanics.SynchronizeEnergyCost(card, delta.KeywordOverrides, delta.EnergyOverride);
            ApplyEnchantmentSpec(card, delta.Enchantment);
            if (includeAffliction) ApplyAfflictionSpec(card, delta.Affliction);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: failed applying delta to '{card.Id}'. {exception.Message}");
        }
    }

    public static CardModificationDelta CreatePermanentDelta(ModelId cardId, CardModificationSpec? desired)
    {
        CardModel? canonical = LoadoutModelRegistry.ResolveCard(cardId);
        return canonical is null ? new CardModificationDelta() : CreateDelta(canonical, desired, null);
    }

    public static CardModificationDelta CreateTemporaryDelta(CardModel card, CardModificationSpec? desired)
    {
        if (desired is null || desired.IsEmpty) return new CardModificationDelta();
        CardModel? canonical = LoadoutModelRegistry.ResolveCard(card.Id);
        if (canonical is null) return new CardModificationDelta();
        CardModificationSpec permanent = PermanentCardModificationStore.Get(card.Id);
        CardModel baseline = CreateBaseline(canonical, card.CurrentUpgradeLevel, permanent);
        return CreateDelta(baseline, desired, permanent);
    }

    public static CardModificationSpec MaterializePermanentSpec(ModelId cardId, CardModificationDelta delta)
    {
        CardModel? canonical = LoadoutModelRegistry.ResolveCard(cardId);
        return canonical is null ? new CardModificationSpec() : MaterializeSpec(canonical, delta);
    }

    public static CardModificationSpec MaterializeTemporarySpec(CardModel card, CardModificationDelta delta)
    {
        CardModel? canonical = LoadoutModelRegistry.ResolveCard(card.Id);
        if (canonical is null) return new CardModificationSpec();
        CardModel baseline = CreateBaseline(canonical, card.CurrentUpgradeLevel, PermanentCardModificationStore.Get(card.Id));
        return MaterializeSpec(baseline, delta);
    }

    public static void ReapplyTemporaryDelta(CardModel card)
    {
        if (!CardModificationFields.TryGet(card, out CardModificationCardData data)) return;
        CardModel? canonical = LoadoutModelRegistry.ResolveCard(card.Id);
        if (canonical is null) return;
        CardModificationSpec previous = GetEffectiveSpec(card);
        CardModel baseline = CreateBaseline(canonical, card.CurrentUpgradeLevel, PermanentCardModificationStore.Get(card.Id));
        CardModificationSpec desired = MaterializeSpec(baseline, data.Delta);
        CopyNativeFields(baseline, card, previous, desired);
        ApplySpecToCard(card, desired);
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
        if (!AttachmentEquals(desired.Enchantment, structuralBaseline?.Enchantment)) delta.Enchantment = desired.Enchantment?.Clone();
        if (!AttachmentEquals(desired.Affliction, structuralBaseline?.Affliction)) delta.Affliction = desired.Affliction?.Clone();
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

    private static CardModificationSpec MaterializeSpec(CardModel baseline, CardModificationDelta delta)
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
            Enchantment = delta.Enchantment?.Clone(),
            Affliction = delta.Affliction?.Clone()
        };
        foreach ((string name, decimal difference) in delta.DynamicVarDeltas)
        {
            if (baseline.DynamicVars.TryGetValue(name, out var baselineVar))
                spec.DynamicVars[name] = baselineVar.BaseValue + difference;
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
            CardModel preview = CreateBaseline(canonical, source.CurrentUpgradeLevel, permanent);
            CardModificationDelta temporary = CreateDelta(preview, state, permanent);
            ApplyDeltaToCard(preview, temporary);
            if (temporary.HasCustomText || temporary.HasPortraitOverride)
            {
                PreviewDeltas.Add(preview, temporary);
                if (temporary.HasCustomText) CardModificationDynamicPatches.EnableTextPatches();
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

    public static CardModel GetPermanentCardForDisplay(CardModel card)
    {
        if (!card.IsCanonical)
            return card;

        if (!PermanentCardModificationStore.TryGet(card.Id, out CardModificationSpec? spec)
            || !spec.HasNativeMutations)
        {
            return card;
        }

        if (DisplayCards.TryGetValue(card.Id, out CardModel? display))
            return display;

        try
        {
            display = card.ToMutable();
            DisplayCards[card.Id] = display;
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
        CardModificationNetProtocol.BroadcastTemporary(item, state);
    }

    public static void ResetTemporaryToBasic(LoadoutOwnedItem<CardModel> item)
    {
        CardModificationSpec previous = GetEffectiveSpec(item.Model);
        if (!CardModificationFields.Clear(item.Model))
            return;

        RebuildOwnedCard(item, previous);
        CardModificationSpec next = GetEffectiveSpec(item.Model);
        NotifyCardUpdated(item, previous, next);
        CardModificationNetProtocol.BroadcastTemporary(item, next: null);
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
        else if (temporaryChanged)
        {
            RebuildOwnedCard(item, selectedPrevious);
            NotifyCardUpdated(item, selectedPrevious, GetEffectiveSpec(item.Model));
        }

        if (temporaryChanged)
            CardModificationNetProtocol.BroadcastTemporary(item, next: null);
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
        ModelId expectedModelId,
        CardModificationSpec? state,
        Player actionPlayer,
        bool authoritativeRemote = false)
    {
        LoadoutOwnedItem<CardModel>? resolved = TryResolveOwnedDeckCard(
            target,
            deckIndex,
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
                ApplyTemporaryWithoutBroadcast(item, null);
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
        ModelId expectedModelId,
        CardModificationDelta? delta,
        Player actionPlayer,
        bool authoritativeRemote = false)
    {
        LoadoutOwnedItem<CardModel>? resolved = TryResolveOwnedDeckCard(
            target,
            deckIndex,
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
                ApplyTemporaryDeltaWithoutBroadcast(item, null);
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
        string cardId,
        CardModificationSpec? state)
    {
        if (!TryResolveLiveDeckCard(ownerNetId, deckIndex, cardId, out LoadoutOwnedItem<CardModel>? item)
            || item is null)
            return;

        ApplyTemporaryWithoutBroadcast(item, state);
    }

    public static void ApplyRemoteTemporaryDelta(
        ulong ownerNetId,
        int deckIndex,
        string cardId,
        CardModificationDelta? delta)
    {
        if (!TryResolveLiveDeckCard(ownerNetId, deckIndex, cardId, out LoadoutOwnedItem<CardModel>? item)
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
                RebuildCard(card, previous);
                CardModificationSpec next = GetEffectiveSpec(card);
                LoadoutCardVisualRefreshKind kind = GetVisualRefreshKind(previous, next);
                RefreshLiveCardVisuals(card, kind);
                changedPlayers.Add(owner.NetId);
                changedCards.Add(new LoadoutChangedCard(owner.NetId, index, card.Id, kind));
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
    }

    public static void RetrofitChangedPermanentCards(
        IReadOnlyList<ModelId> changedIds,
        IReadOnlyDictionary<ModelId, CardModificationSpec>? previous = null)
    {
        if (changedIds.Count == 0 || !TryGetRunState(out RunState? runState))
            return;

        HashSet<ModelId> changedIdsSet = new(changedIds);
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
                RebuildCard(card, previousEffective);
                CardModificationSpec next = GetEffectiveSpec(card);
                LoadoutCardVisualRefreshKind kind = GetVisualRefreshKind(previousEffective, next);
                RefreshLiveCardVisuals(card, kind);
                changedPlayers.Add(owner.NetId);
                changedCards.Add(new LoadoutChangedCard(owner.NetId, index, card.Id, kind));
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
    }

    private static void ApplyTemporaryWithoutBroadcast(
        LoadoutOwnedItem<CardModel> item,
        CardModificationSpec? state)
    {
        CardModificationSpec previous = GetEffectiveSpec(item.Model);
        bool changed = CardModificationFields.Set(item.Model, state);
        if (!changed)
            return;

        RebuildOwnedCard(item, previous);
        NotifyCardUpdated(item, previous, GetEffectiveSpec(item.Model));
    }

    private static void ApplyTemporaryDeltaWithoutBroadcast(
        LoadoutOwnedItem<CardModel> item,
        CardModificationDelta? delta)
    {
        CardModificationSpec previous = GetEffectiveSpec(item.Model);
        bool changed = CardModificationFields.SetDelta(item.Model, delta);
        if (!changed)
            return;

        RebuildOwnedCard(item, previous);
        NotifyCardUpdated(item, previous, GetEffectiveSpec(item.Model));
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
        else if (temporaryChanged)
        {
            RebuildOwnedCard(item, selectedPrevious);
            NotifyCardUpdated(item, selectedPrevious, GetEffectiveSpec(item.Model));
        }
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
        else if (temporaryChanged)
        {
            RebuildOwnedCard(item, selectedPrevious);
            NotifyCardUpdated(item, selectedPrevious, GetEffectiveSpec(item.Model));
        }
    }

    private static void RebuildOwnedCard(LoadoutOwnedItem<CardModel> item, CardModificationSpec previous)
    {
        RebuildCard(item.Model, previous);
        RefreshLiveCardVisuals(item.Model, GetVisualRefreshKind(previous, GetEffectiveSpec(item.Model)));
    }

    private static void RebuildCard(CardModel card, CardModificationSpec previous)
    {
        CardModificationSpec permanent = PermanentCardModificationStore.Get(card.Id);
        CardModificationSpec temporary = GetTemporarySpec(card);
        CardModificationSpec next = Merge(permanent, temporary);
        CardModel? canonical = LoadoutModelRegistry.ResolveCard(card.Id);
        if (canonical is null)
            return;

        CardModel baseline = CreateBaseline(canonical, card.CurrentUpgradeLevel, permanent);
        CopyNativeFields(baseline, card, previous, next);
        ApplySpecToCard(card, temporary);
    }

    private static CardModel CreateBaseline(
        CardModel canonical,
        int upgradeLevel,
        CardModificationSpec permanent)
    {
        CardModel baseline;
        using (SuppressPermanentApplication())
            baseline = canonical.ToMutable();

        ApplySpecToCard(baseline, permanent);
        int count = Math.Max(0, upgradeLevel);
        InfiniteUpgradeMaxLevelPatch.BeginDeserialization(count);
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
            InfiniteUpgradeMaxLevelPatch.EndDeserialization();
        }
        return baseline;
    }

    private static void CopyNativeFields(
        CardModel source,
        CardModel destination,
        CardModificationSpec previous,
        CardModificationSpec next)
    {
        HashSet<string> keywordKeys = new(previous.KeywordOverrides.Keys, StringComparer.Ordinal);
        keywordKeys.UnionWith(next.KeywordOverrides.Keys);

        if ((previous.EnergyCost.HasValue
             || next.EnergyCost.HasValue
             || keywordKeys.Contains(LoadoutKeywords.XCostKey))
            && !destination.EnergyCost.CostsX)
        {
            SetEnergyCost(destination, source.EnergyCost.Canonical);
        }
        if (previous.BaseReplayCount.HasValue || next.BaseReplayCount.HasValue)
            destination.BaseReplayCount = source.BaseReplayCount;
        if (previous.BaseStarCost.HasValue || next.BaseStarCost.HasValue)
            SetBaseStarCost(destination, source.BaseStarCost);

        HashSet<string> dynamicVarNames = new(previous.DynamicVars.Keys, StringComparer.Ordinal);
        dynamicVarNames.UnionWith(next.DynamicVars.Keys);
        foreach (string name in dynamicVarNames)
        {
            if (source.DynamicVars.TryGetValue(name, out var sourceVar)
                && destination.DynamicVars.TryGetValue(name, out var destinationVar))
            {
                destinationVar.BaseValue = sourceVar.BaseValue;
            }
        }

        if (previous.PoolId is not null || next.PoolId is not null)
            CardPoolField?.SetValue(destination, source.Pool);
        if (previous.Type is not null || next.Type is not null)
            CardTypeField?.SetValue(destination, source.Type);
        if (previous.Rarity is not null || next.Rarity is not null)
            CardRarityField?.SetValue(destination, source.Rarity);

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

        if (keywordKeys.Contains(LoadoutKeywords.XCostKey))
            LoadoutKeywordMechanics.SynchronizeEnergyCost(destination, next.KeywordOverrides, next.EnergyCost);
        if ((previous.EnergyCost.HasValue
             || next.EnergyCost.HasValue
             || keywordKeys.Contains(LoadoutKeywords.XCostKey))
            && !source.EnergyCost.CostsX
            && !destination.EnergyCost.CostsX)
        {
            SetEnergyCost(destination, source.EnergyCost.Canonical);
        }

        if (previous.Enchantment is not null || next.Enchantment is not null)
            CopyEnchantment(source, destination);
        if (previous.Affliction is not null || next.Affliction is not null)
            CopyAffliction(source, destination);
    }

    private static void CopyEnchantment(CardModel source, CardModel destination)
    {
        if (destination.Enchantment is not null)
            CardCmd.ClearEnchantment(destination);
        if (source.Enchantment is not null
            && TryResolveModel(source.Enchantment.Id.ToString(), ModelDb.DebugEnchantments, out EnchantmentModel? canonical))
        {
            ForceApplyEnchantment(destination, canonical!, Math.Max(1, source.Enchantment.Amount));
        }
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
        LoadoutRunContentChangeService.NotifyCardUpdated(item, kind);
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

    private static LoadoutOwnedItem<CardModel>? TryResolveOwnedDeckCard(
        LoadoutTargetSelection target,
        int deckIndex,
        ModelId expectedModelId,
        Player actionPlayer)
    {
        Player? owner = target.Scope == LoadoutTargetScope.Player && target.PlayerNetId.HasValue
            ? actionPlayer.RunState.GetPlayer(target.PlayerNetId.Value)
            : actionPlayer;
        if (owner is null || deckIndex < 0 || deckIndex >= owner.Deck.Cards.Count)
            return null;

        CardModel card = owner.Deck.Cards[deckIndex];
        return ModelIdMatches(card, expectedModelId) && card.Pile?.Type == PileType.Deck
            ? new LoadoutOwnedItem<CardModel>(owner, deckIndex, card)
            : null;
    }

    private static bool TryResolveLiveDeckCard(
        ulong ownerNetId,
        int deckIndex,
        string cardId,
        out LoadoutOwnedItem<CardModel>? item)
    {
        item = default;
        if (!TryGetRunState(out RunState? runState))
            return false;

        Player? owner = runState!.GetPlayer(ownerNetId);
        if (owner is null || deckIndex < 0 || deckIndex >= owner.Deck.Cards.Count)
            return false;

        CardModel card = owner.Deck.Cards[deckIndex];
        if (!MatchesModelId(card, cardId) || card.Pile?.Type != PileType.Deck)
            return false;

        item = new LoadoutOwnedItem<CardModel>(owner, deckIndex, card);
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

    private static void ApplyKeywordOverrides(CardModel card, Dictionary<string, bool> overrides)
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

    private static void ApplyEnchantmentSpec(CardModel card, CardAttachmentSpec? spec)
    {
        if (spec is null)
            return;
        if (spec.Clear)
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
        if (PermanentCardModificationStore.TryGetDelta(cardId, out CardModificationDelta? delta))
        {
            if (delta.HasCustomText) CardModificationDynamicPatches.EnableTextPatches();
            if (delta.HasPortraitOverride) CardModificationDynamicPatches.EnablePortraitPatches();
        }
        DisplayCards.Remove(cardId);
        PermanentCardDisplayChanged?.Invoke(cardId);
    }

    private static void OnPermanentStoreReloaded()
    {
        DisplayCards.Clear();
        if (PermanentCardModificationStore.HasAnyCustomText) CardModificationDynamicPatches.EnableTextPatches();
        if (PermanentCardModificationStore.HasAnyPortraitOverrides) CardModificationDynamicPatches.EnablePortraitPatches();
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
