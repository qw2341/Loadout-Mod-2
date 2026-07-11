#nullable enable

namespace Loadout.Services.CardModification;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using HarmonyLib;
using Loadout.Keywords;
using Loadout.Services.Actions;
using Loadout.Services.Saving;
using Loadout.Services.Targets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

public enum CardModificationOperation
{
    None,
    SaveTemporary,
    ResetTemporary,
    ResetTemporaryToBasic,
    ApplyPermanent,
    ResetPermanentToBasic
}

public enum CardModificationPermanentImportMode
{
    KeepMine,
    UseHost,
    MergeNonConflicting
}

public enum HostPermanentSnapshotApplyMode
{
    LiveDecks,
    CatalogOnly
}

public static class CardModificationStateService
{
    private const int CurrentSchemaVersion = 1;
    private const string PermanentPath = "loadout/services/card_modifications/permanent.json";
    private const string RunDirectory = "loadout/services/card_modifications";
    private const string RunFilePrefix = "card_modifications_run";

    private static readonly object SyncRoot = new();
    private static readonly FieldInfo? CardTypeField = AccessTools.Field(typeof(CardModel), "<Type>k__BackingField");
    private static readonly FieldInfo? CardRarityField = AccessTools.Field(typeof(CardModel), "<Rarity>k__BackingField");
    private static readonly FieldInfo? CardPoolField = AccessTools.Field(typeof(CardModel), "_pool");
    private static readonly FieldInfo? EnergyCostCanonicalField = AccessTools.Field(typeof(CardEnergyCost), "<Canonical>k__BackingField");
    private static readonly MethodInfo? BaseStarCostSetter = AccessTools.PropertySetter(typeof(CardModel), nameof(CardModel.BaseStarCost));
    private static readonly MethodInfo? NCardFindOnTableByCard = AccessTools.Method(typeof(NCard), nameof(NCard.FindOnTable), [typeof(CardModel)]);
    private static readonly MethodInfo? NCardFindOnTableByCardAndPile = AccessTools.Method(typeof(NCard), nameof(NCard.FindOnTable), [typeof(CardModel), typeof(PileType)]);
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static PermanentSaveData _permanent = new();
    private static RunSaveData _run = new();
    private static Dictionary<string, CardModificationState> _hostPermanentOverlay = new(StringComparer.Ordinal);
    private static bool _hasHostPermanentOverlay;
    private static readonly Dictionary<string, CachedDisplayCard> DisplayCardCache = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<CardModel, LocalCatalogDisplayCardMarker> LocalCatalogDisplayCards = new();
    private static readonly ConditionalWeakTable<CardModel, PreviewCardStateHolder> PreviewCardStates = new();
    private static readonly ConditionalWeakTable<CardModel, EffectiveStateCacheHolder> EffectiveStateCache = new();
    private static readonly Dictionary<string, WeakReference<CardModel>> RecentDeckCardIdentities = new(StringComparer.Ordinal);
    private static bool _registered;
    private static bool _permanentLoaded;
    private static bool _runLoaded;
    private static long? _loadedRunStartTime;
    private static int _displayRevision;
    private static int _effectiveStateRevision;
    private static bool _permanentSavePending;
    private static bool _runSavePending;
    private static bool _saveFlushQueued;

    [ThreadStatic]
    private static Stack<CardModel>? _locStringContext;

    [ThreadStatic]
    private static int _targetedUpgradeDepth;

    public static event Action? StateChanged;
    public static event Action<ModelId>? PermanentCardDisplayChanged;

    public static void Register()
    {
        if (_registered)
            return;

        _registered = true;
        RunManager.Instance.RunStarted += OnRunStarted;
        SaveManager.Instance.ProfileIdChanged += OnProfileIdChanged;
        CombatManager.Instance.CombatSetUp += OnCombatSetUp;
        CardModificationMultiplayerSyncService.Register();
        EnsureLoaded();
    }

    public static void Unregister()
    {
        if (!_registered)
            return;

        FlushPendingSaves();
        RunManager.Instance.RunStarted -= OnRunStarted;
        SaveManager.Instance.ProfileIdChanged -= OnProfileIdChanged;
        CombatManager.Instance.CombatSetUp -= OnCombatSetUp;
        CardModificationMultiplayerSyncService.Unregister();
        _registered = false;
    }

    public static void EnsureLoaded()
    {
        ReloadPermanentIfNeeded();
        ReloadRunIfNeeded();
    }

    public static void NotifyStateChanged()
    {
        RaiseStateChanged();
    }

    public static CardModificationState GetEffectiveState(LoadoutOwnedItem<CardModel> item)
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            CardModificationState effective = GetEffectivePermanentStateLocked(item.Model.Id);
            if (_run.Cards.TryGetValue(GetCopyKey(item), out CardModificationState? temporary))
                effective.MergeFrom(temporary);

            return effective;
        }
    }

    public static CardModificationState GetEffectiveStateForCard(CardModel card)
    {
        return GetEffectiveStateForCardReadOnly(card).Clone();
    }

    private static CardModificationState GetEffectiveStateForCardReadOnly(CardModel card)
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            if (!card.IsCanonical && PreviewCardStates.TryGetValue(card, out PreviewCardStateHolder? preview))
                return preview.State;

            EffectiveStateCacheHolder cache = EffectiveStateCache.GetValue(card, _ => new EffectiveStateCacheHolder());
            if (cache.Revision == _effectiveStateRevision)
                return cache.State;

            bool catalogDisplayCard = IsLocalCatalogDisplayCard(card);
            CardModificationState effective = catalogDisplayCard
                ? GetCatalogPermanentStateLocked(card.Id)
                : GetEffectivePermanentStateLocked(card.Id);

            if (!catalogDisplayCard
                && TryGetCopyKey(card, out string? copyKey)
                && copyKey is not null
                && _run.Cards.TryGetValue(copyKey, out CardModificationState? temporary))
            {
                effective.MergeFrom(temporary);
            }

            cache.Revision = _effectiveStateRevision;
            cache.State = effective;
            return cache.State;
        }
    }

    public static bool TryGetCustomTitle(CardModel card, out string title)
    {
        CardModificationState state = GetEffectiveStateForCardReadOnly(card);
        if (!string.IsNullOrWhiteSpace(state.CustomTitle))
        {
            title = state.CustomTitle!;
            return true;
        }

        title = string.Empty;
        return false;
    }

    public static void PushLocStringContext(CardModel card)
    {
        _locStringContext ??= new Stack<CardModel>();
        _locStringContext.Push(card);
    }

    public static void PopLocStringContext()
    {
        if (_locStringContext is null || _locStringContext.Count == 0)
            return;

        _locStringContext.Pop();
    }

    public static bool TryGetCustomRawLocString(LocString locString, out string rawText)
    {
        rawText = string.Empty;
        if (!string.Equals(locString.LocTable, "cards", StringComparison.Ordinal)
            || _locStringContext is null
            || _locStringContext.Count == 0)
        {
            return false;
        }

        CardModel card = _locStringContext.Peek();
        string titleKey = $"{card.Id.Entry}.title";
        string descriptionKey = $"{card.Id.Entry}.description";
        CardModificationState state = GetEffectiveStateForCardReadOnly(card);

        if (string.Equals(locString.LocEntryKey, titleKey, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(state.CustomTitle))
        {
            rawText = state.CustomTitle!;
            return true;
        }

        if (string.Equals(locString.LocEntryKey, descriptionKey, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(state.CustomDescription))
        {
            rawText = state.CustomDescription!;
            return true;
        }

        return false;
    }

    public static bool TryGetPortraitPath(CardModel card, bool beta, string currentPath, out string path)
    {
        path = string.Empty;
        CardModificationState state = GetEffectiveStateForCardReadOnly(card);
        string? overridePath = beta ? state.BetaPortraitPath : state.PortraitPath;
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            path = overridePath!;
            return true;
        }

        if (beta && !string.IsNullOrWhiteSpace(state.PortraitPath))
        {
            path = state.PortraitPath!;
            return true;
        }

        if (string.IsNullOrWhiteSpace(state.PoolId))
            return false;

        CardModel? canonical = LoadoutModelRegistry.ResolveCard(card.Id);
        if (canonical is null || ReferenceEquals(canonical, card))
        {
            path = currentPath;
            return true;
        }

        path = canonical.PortraitPath;
        return true;
    }

    public static bool TryGetCustomDescription(CardModel card, out string description)
    {
        CardModificationState state = GetEffectiveStateForCardReadOnly(card);
        if (!string.IsNullOrWhiteSpace(state.CustomDescription))
        {
            description = state.CustomDescription!;
            return true;
        }

        description = string.Empty;
        return false;
    }

    public static CardModificationState GetPermanentState(ModelId cardId)
    {
        EnsureLoaded();
        lock (SyncRoot)
            return GetPermanentStateLocked(cardId).Clone();
    }

    public static CardModificationState GetEffectivePermanentState(ModelId cardId)
    {
        EnsureLoaded();
        lock (SyncRoot)
            return GetEffectivePermanentStateLocked(cardId);
    }

    public static CardModificationState GetCatalogPermanentState(ModelId cardId)
    {
        EnsureLoaded();
        lock (SyncRoot)
            return GetCatalogPermanentStateLocked(cardId);
    }

    public static CardModificationState GetTemporaryState(LoadoutOwnedItem<CardModel> item)
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            return _run.Cards.TryGetValue(GetCopyKey(item), out CardModificationState? temporary)
                ? temporary.Clone()
                : new CardModificationState();
        }
    }

    public static LoadoutCardVisualRefreshKind GetVisualRefreshKind(
        CardModificationState? previousState,
        CardModificationState? nextState)
    {
        CardModificationState previous = previousState?.Clone() ?? new CardModificationState();
        CardModificationState next = nextState?.Clone() ?? new CardModificationState();
        previous.Normalize();
        next.Normalize();

        return SameStructuralValue(previous.PoolId, next.PoolId)
               && SameStructuralValue(previous.Type, next.Type)
               && SameStructuralValue(previous.Rarity, next.Rarity)
               && SameStructuralValue(previous.PortraitPath, next.PortraitPath)
               && SameStructuralValue(previous.BetaPortraitPath, next.BetaPortraitPath)
            ? LoadoutCardVisualRefreshKind.Lightweight
            : LoadoutCardVisualRefreshKind.Reload;
    }

    private static bool SameStructuralValue(string? left, string? right)
    {
        return string.Equals(
            string.IsNullOrWhiteSpace(left) ? string.Empty : left.Trim(),
            string.IsNullOrWhiteSpace(right) ? string.Empty : right.Trim(),
            StringComparison.Ordinal);
    }

    public static void SaveTemporary(LoadoutOwnedItem<CardModel> item, CardModificationState state)
    {
        EnsureLoaded();
        CardModificationState previousState = GetEffectiveState(item);
        CardModificationState normalized = state.Clone();
        normalized.Normalize();
        bool changed = SaveTemporaryLocal(item, normalized);
        ClearPreviewState(item.Model);

        if (!changed)
            return;

        ApplyEffectiveStateToOwnedCard(item, previousState, refreshLiveVisuals: false);
        CardModificationState nextState = GetEffectiveState(item);
        LoadoutCardVisualRefreshKind refreshKind = GetVisualRefreshKind(previousState, nextState);
        RefreshLiveCardVisuals(item.Model, refreshKind);
        LoadoutRunContentChangeService.NotifyCardUpdated(item, refreshKind);
        CardModificationMultiplayerSyncService.BroadcastTemporary(item, normalized);
    }

    public static void SavePermanent(ModelId cardId, CardModificationState state)
    {
        EnsureLoaded();
        CardModificationState normalized = state.Clone();
        normalized.Normalize();
        bool changed = SavePermanentLocal(cardId, normalized);

        if (!changed)
            return;

        CardModificationMultiplayerSyncService.BroadcastPermanentDelta(cardId, normalized);
        RaisePermanentCardDisplayChanged(cardId);
    }

    public static void ResetTemporary(LoadoutOwnedItem<CardModel> item)
    {
        EnsureLoaded();
        CardModificationState previousState = GetEffectiveState(item);
        bool changed = ResetTemporaryLocal(item);
        ClearPreviewState(item.Model);

        if (!changed)
            return;

        ApplyEffectiveStateToOwnedCard(item, previousState, refreshLiveVisuals: false);
        CardModificationState nextState = GetEffectiveState(item);
        LoadoutCardVisualRefreshKind refreshKind = GetVisualRefreshKind(previousState, nextState);
        RefreshLiveCardVisuals(item.Model, refreshKind);
        LoadoutRunContentChangeService.NotifyCardUpdated(item, refreshKind);
        CardModificationMultiplayerSyncService.BroadcastTemporary(item, new CardModificationState());
    }

    public static void ResetTemporaryToBasic(LoadoutOwnedItem<CardModel> item)
    {
        EnsureLoaded();
        CardModificationState permanentState = GetEffectivePermanentState(item.Model.Id);
        bool changed = ResetTemporaryLocal(item);
        ResetOwnedCardToBasicState(item, permanentState);

        if (changed)
            CardModificationMultiplayerSyncService.BroadcastTemporary(item, new CardModificationState());

        LoadoutRunContentChangeService.NotifyCardUpdated(item, LoadoutCardVisualRefreshKind.Reload);
    }

    public static void ResetPermanent(ModelId cardId)
    {
        EnsureLoaded();
        bool changed = ResetPermanentLocal(cardId);
        if (!changed)
            return;

        CardModificationMultiplayerSyncService.BroadcastPermanentDelta(cardId, new CardModificationState());
        RaisePermanentCardDisplayChanged(cardId);
    }

    public static IReadOnlyList<ModelId> ResetAllPermanent()
    {
        EnsureLoaded();

        Dictionary<string, CardModificationState> previousPermanentStates;
        List<string> changedPermanentKeys;
        lock (SyncRoot)
        {
            if (_permanent.Cards.Count == 0)
                return [];

            previousPermanentStates = CloneStateDictionary(_permanent.Cards);
            changedPermanentKeys = _permanent.Cards.Keys.ToList();
            _permanent.Cards.Clear();
            InvalidateDisplayCacheLocked();
            SavePermanentState();
        }

        HashSet<string> changedPermanentKeySet = new(changedPermanentKeys, StringComparer.Ordinal);
        ApplySavedRunStateToLiveDecks(
            previousPermanentStates,
            raiseStateChanged: false,
            changedPermanentKeys: changedPermanentKeySet);

        CardModificationMultiplayerSyncService.BroadcastPermanentSnapshot();
        IReadOnlyList<ModelId> changedCardIds = ResolvePermanentCardIds(changedPermanentKeys);
        RaisePermanentCardDisplayChanged(changedCardIds);
        RaiseStateChanged();
        return changedCardIds;
    }

    public static int GetPermanentModificationCount()
    {
        EnsureLoaded();
        lock (SyncRoot)
            return _permanent.Cards.Count;
    }

    public static void ResetPermanentToBasic(LoadoutOwnedItem<CardModel> item)
    {
        EnsureLoaded();
        bool temporaryChanged = ResetTemporaryLocal(item);
        bool permanentChanged = ResetPermanentLocal(item.Model.Id);
        ResetOwnedCardToBasicState(item, new CardModificationState());

        if (permanentChanged)
            CardModificationMultiplayerSyncService.BroadcastPermanentDelta(item.Model.Id, new CardModificationState());
        if (temporaryChanged)
            CardModificationMultiplayerSyncService.BroadcastTemporary(item, new CardModificationState());

        if (permanentChanged)
            RaisePermanentCardDisplayChanged(item.Model.Id);
        LoadoutRunContentChangeService.NotifyCardUpdated(item, LoadoutCardVisualRefreshKind.Reload);
    }

    public static void ApplyPermanentToCard(CardModel? card)
    {
        if (card is null || card.IsCanonical)
            return;

        if (!TryGetEffectivePermanentStateForMutation(card.Id, out CardModificationState state))
            return;

        PrepareCardForState(card, state, includeAffliction: true);
        ApplyStateToCard(card, state);
    }

    public static void ApplyEffectiveStateToOwnedCard(
        LoadoutOwnedItem<CardModel> item,
        CardModificationState? previousState = null,
        bool refreshLiveVisuals = true)
    {
        ClearPreviewState(item.Model);
        CardModificationState state = GetEffectiveState(item);
        bool hasCardMutation = ApplyStateTransitionToCard(item.Model, state, previousState, includeAffliction: true);

        if (refreshLiveVisuals
            && (hasCardMutation || HasVisualOverrides(state) || HasVisualOverrides(previousState)))
            RefreshLiveCardVisuals(item.Model);
    }

    public static void ApplyPreviewStateToOwnedCard(
        LoadoutOwnedItem<CardModel> item,
        CardModificationState state,
        CardModificationState? previousState = null)
    {
        CardModificationState normalized = state.Clone();
        normalized.Normalize();
        SetPreviewState(item.Model, normalized);
        bool hasCardMutation = ApplyStateTransitionToCard(item.Model, normalized, previousState, includeAffliction: true);

        if (hasCardMutation || HasVisualOverrides(normalized) || HasVisualOverrides(previousState))
            RefreshLiveCardVisuals(item.Model);
    }

    public static void ResetOwnedCardToBasicState(LoadoutOwnedItem<CardModel> item, CardModificationState? state)
    {
        ClearPreviewState(item.Model);
        CardModificationState normalized = state?.Clone() ?? new CardModificationState();
        normalized.Normalize();

        ResetCardToCanonicalBaseline(item.Model, resetAttachments: true, resetUpgrade: true);
        if (!normalized.IsEmpty)
            ApplyStateToCard(item.Model, normalized);

        RefreshLiveCardVisuals(item.Model);
    }

    public static void ReapplyEffectiveStateAfterUpgrade(IEnumerable<CardModel>? cards)
    {
        if (cards is null || IsCombatEnding())
            return;

        EnsureLoaded();
        List<CardModel> uniqueCards = new();
        foreach (CardModel? card in cards)
        {
            if (card is null
                || card.IsCanonical
                || uniqueCards.Any(existing => ReferenceEquals(existing, card)))
            {
                continue;
            }

            uniqueCards.Add(card);
        }

        bool refreshVisualsHere = _targetedUpgradeDepth == 0;
        foreach (CardModel card in uniqueCards)
        {
            CardModificationState state = GetEffectiveStateForCardReadOnly(card);
            bool hasCardMutation = HasCardMutations(state);
            bool hasVisualOverride = HasVisualOverrides(state);
            if (!hasCardMutation && !hasVisualOverride)
                continue;

            if (hasCardMutation)
            {
                PrepareCardForState(card, state, includeAffliction: true);
                ApplyStateToCard(card, state);
            }

            if (refreshVisualsHere)
            {
                LoadoutCardVisualRefreshKind refreshKind = GetVisualRefreshKind(new CardModificationState(), state);
                RefreshLiveCardVisuals(card, refreshKind);
            }
        }
    }

    public static IDisposable BeginTargetedUpgradeRefresh()
    {
        _targetedUpgradeDepth++;
        return new TargetedUpgradeRefreshScope();
    }

    public static void ApplySynchronizedOperation(
        CardModificationOperation operation,
        ModelId modelId,
        LoadoutTargetSelection target,
        int ownedItemIndex,
        ModelId expectedModelId,
        CardModificationState? state,
        Player actionPlayer,
        bool authoritativeRemote = false)
    {
        EnsureLoaded();

        switch (operation)
        {
            case CardModificationOperation.SaveTemporary:
                if (TryResolveOwnedDeckCard(target, ownedItemIndex, expectedModelId, actionPlayer) is not { } saveItem)
                    return;

                CardModificationState savePreviousState = GetEffectiveState(saveItem);
                CardModificationState normalized = state?.Clone() ?? new CardModificationState();
                normalized.Normalize();
                if (!SaveTemporaryLocal(saveItem, normalized))
                    return;

                ApplyEffectiveStateToOwnedCard(saveItem, savePreviousState, refreshLiveVisuals: false);
                CardModificationState saveNextState = GetEffectiveState(saveItem);
                LoadoutCardVisualRefreshKind saveRefreshKind = GetVisualRefreshKind(savePreviousState, saveNextState);
                RefreshLiveCardVisuals(saveItem.Model, saveRefreshKind);
                LoadoutRunContentChangeService.NotifyCardUpdated(saveItem, saveRefreshKind);
                break;

            case CardModificationOperation.ResetTemporary:
                if (TryResolveOwnedDeckCard(target, ownedItemIndex, expectedModelId, actionPlayer) is not { } resetItem)
                    return;

                CardModificationState resetPreviousState = GetEffectiveState(resetItem);
                if (!ResetTemporaryLocal(resetItem))
                    return;

                ApplyEffectiveStateToOwnedCard(resetItem, resetPreviousState, refreshLiveVisuals: false);
                CardModificationState resetNextState = GetEffectiveState(resetItem);
                LoadoutCardVisualRefreshKind resetRefreshKind = GetVisualRefreshKind(resetPreviousState, resetNextState);
                RefreshLiveCardVisuals(resetItem.Model, resetRefreshKind);
                LoadoutRunContentChangeService.NotifyCardUpdated(resetItem, resetRefreshKind);
                break;

            case CardModificationOperation.ResetTemporaryToBasic:
                if (TryResolveOwnedDeckCard(target, ownedItemIndex, expectedModelId, actionPlayer) is not { } resetBasicItem)
                    return;

                CardModificationState resetBasicPrevious = GetEffectiveState(resetBasicItem);
                ResetTemporaryLocal(resetBasicItem);
                CardModificationState basicPermanent = GetEffectivePermanentState(resetBasicItem.Model.Id);
                ResetOwnedCardToBasicState(resetBasicItem, basicPermanent);
                LoadoutRunContentChangeService.NotifyCardUpdated(
                    resetBasicItem,
                    GetVisualRefreshKind(resetBasicPrevious, basicPermanent));
                break;

            case CardModificationOperation.ApplyPermanent:
                CardModificationState permanentState = state?.Clone() ?? new CardModificationState();
                permanentState.Normalize();
                LoadoutOwnedItem<CardModel>? committedItem = TryResolveOwnedDeckCard(
                    target,
                    ownedItemIndex,
                    expectedModelId,
                    actionPlayer);
                if (committedItem is not { } itemToCommit)
                    return;

                ResetTemporaryLocal(itemToCommit);

                if (authoritativeRemote)
                {
                    ApplyHostPermanentDelta(modelId.ToString(), permanentState);
                    return;
                }

                if (!IsPermanentModificationAuthority())
                    return;

                ApplyPermanentStateToLiveCardsFromAction(modelId, permanentState);
                if (SavePermanentLocal(modelId, permanentState))
                    RaisePermanentCardDisplayChanged(modelId);
                break;

            case CardModificationOperation.ResetPermanentToBasic:
                if (TryResolveOwnedDeckCard(target, ownedItemIndex, expectedModelId, actionPlayer) is not { } resetPermanentItem)
                    return;

                CardModificationState resetPermanentPreviousState = GetEffectiveState(resetPermanentItem);
                ResetTemporaryLocal(resetPermanentItem);
                if (authoritativeRemote)
                {
                    ApplyHostPermanentDelta(modelId.ToString(), new CardModificationState());
                    return;
                }

                if (IsPermanentModificationAuthority())
                {
                    ApplyPermanentStateToLiveCardsFromAction(modelId, new CardModificationState());
                    if (ResetPermanentLocal(modelId))
                        RaisePermanentCardDisplayChanged(modelId);
                }
                else
                {
                    ApplyEffectiveStateToOwnedCard(resetPermanentItem, resetPermanentPreviousState, refreshLiveVisuals: false);
                    LoadoutRunContentChangeService.NotifyCardUpdated(
                        resetPermanentItem,
                        GetVisualRefreshKind(resetPermanentPreviousState, GetEffectiveState(resetPermanentItem)));
                }
                break;
        }
    }

    private static bool IsPermanentModificationAuthority()
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

    public static void ApplyStateToCard(CardModel? card, CardModificationState? state, bool includeAffliction = true)
    {
        if (card is null || state is null || state.IsEmpty)
            return;

        if (card.IsCanonical)
        {
            GD.PushWarning($"CardModification: refused to mutate canonical card '{card.Id}'.");
            return;
        }

        try
        {
            if (state.EnergyCost.HasValue && !card.EnergyCost.CostsX)
                SetEnergyCost(card, state.EnergyCost.Value);

            if (state.BaseReplayCount.HasValue)
                card.BaseReplayCount = state.BaseReplayCount.Value;

            if (state.BaseStarCost.HasValue)
                SetBaseStarCost(card, state.BaseStarCost.Value);

            foreach ((string name, decimal value) in state.DynamicVars)
            {
                if (card.DynamicVars.TryGetValue(name, out var dynamicVar))
                    dynamicVar.BaseValue = value;
            }

            if (TryResolvePool(state.PoolId, out CardPoolModel? pool))
                CardPoolField?.SetValue(card, pool);

            if (TryParseEnum(state.Type, out CardType type))
                CardTypeField?.SetValue(card, type);

            if (TryParseEnum(state.Rarity, out CardRarity rarity))
                CardRarityField?.SetValue(card, rarity);

            ApplyKeywordOverrides(card, state.KeywordOverrides);
            LoadoutKeywordMechanics.SynchronizeEnergyCost(card, state.KeywordOverrides, state.EnergyCost);
            ApplyEnchantmentSpec(card, state.Enchantment);

            if (includeAffliction)
                ApplyAfflictionSpec(card, state.Affliction);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: failed to apply modifications to '{card.Id}'. {exception.Message}");
        }
    }

    public static CardModel CreatePermanentPreviewCard(CardModel card)
    {
        return GetEffectivePermanentCardForDisplay(card);
    }

    public static CardModel CreatePreviewCard(CardModel card, CardModificationState state)
    {
        CardModificationState normalized = state.Clone();
        normalized.Normalize();

        try
        {
            CardModel? canonical = LoadoutModelRegistry.ResolveCard(card.Id);
            if (canonical is null)
                return card;

            CardModel preview = CreateCanonicalBaselineForCurrentUpgrade(
                card,
                canonical,
                IsKeywordEnabledAfterState(card, normalized, LoadoutKeywords.InfiniteUpgrade));

            if (normalized.Enchantment is null
                && card.Enchantment is not null
                && TryResolveModel(card.Enchantment.Id.ToString(), ModelDb.DebugEnchantments, out EnchantmentModel? canonicalEnchantment)
                && canonicalEnchantment is not null)
            {
                ForceApplyEnchantment(preview, canonicalEnchantment, Math.Max(1, card.Enchantment.Amount));
            }

            if (normalized.Affliction is null
                && card.Affliction is not null
                && TryResolveModel(card.Affliction.Id.ToString(), ModelDb.DebugAfflictions, out AfflictionModel? canonicalAffliction)
                && canonicalAffliction is not null)
            {
                ForceApplyAffliction(preview, canonicalAffliction, Math.Max(1, card.Affliction.Amount));
            }

            PrepareCardForState(preview, normalized, includeAffliction: true);
            ApplyStateToCard(preview, normalized);
            SetPreviewState(preview, normalized);
            return preview;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: failed to create card modification preview for '{card.Id}'. {exception.Message}");
            return card;
        }
    }

    public static void CarryEffectiveStateToClone(CardModel source, CardModel clone)
    {
        if (source is null || clone is null || clone.IsCanonical)
            return;

        CardModificationState state = GetEffectiveStateForCardReadOnly(source);
        if (!state.IsEmpty)
            SetPreviewState(clone, state);
    }

    public static CardModel GetEffectivePermanentCardForDisplay(CardModel card)
    {
        if (!card.IsCanonical)
            return card;

        EnsureLoaded();
        CardModificationState state;
        int revision;
        lock (SyncRoot)
        {
            state = GetCatalogPermanentStateLocked(card.Id);
            revision = _displayRevision;
            string key = ToCardKey(card.Id);
            if (DisplayCardCache.TryGetValue(key, out CachedDisplayCard cached) && cached.Revision == revision)
                return cached.Card;
        }

        if (!HasCardMutations(state))
            return card;

        try
        {
            CardModel preview = card.ToMutable();
            MarkLocalCatalogDisplayCard(preview);
            PrepareCardForState(preview, state, includeAffliction: true);
            ApplyStateToCard(preview, state);
            lock (SyncRoot)
                DisplayCardCache[ToCardKey(card.Id)] = new CachedDisplayCard(revision, preview);

            return preview;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: failed to create modified preview for '{card.Id}'. {exception.Message}");
            return card;
        }
    }

    public static void ApplySavedRunStateToPlayerDeck(Player? player)
    {
        ApplySavedRunStateToPlayerDeck(player, null, null);
    }

    private static void ApplySavedRunStateToPlayerDeck(
        Player? player,
        IReadOnlyDictionary<string, CardModificationState>? previousPermanentStates,
        IReadOnlySet<string>? changedPermanentKeys,
        ICollection<LoadoutChangedCard>? changedCards = null,
        ISet<ulong>? changedPlayers = null)
    {
        if (player is null)
            return;

        EnsureLoaded();
        IReadOnlyList<CardModel> cards = player.Deck.Cards;
        for (int index = 0; index < cards.Count; index++)
        {
            CardModel card = cards[index];
            string cardKey = ToCardKey(card.Id);
            if (changedPermanentKeys is not null && !changedPermanentKeys.Contains(cardKey))
                continue;

            CardModificationState state;
            CardModificationState? previousState = null;
            lock (SyncRoot)
            {
                state = GetEffectivePermanentStateLocked(card.Id);
                if (_run.Cards.TryGetValue(GetCopyKey(player.NetId, index, card.Id), out CardModificationState? temporary))
                    state.MergeFrom(temporary);

                if (previousPermanentStates is not null)
                {
                    previousState = previousPermanentStates.TryGetValue(cardKey, out CardModificationState? previousPermanent)
                        ? previousPermanent.Clone()
                        : new CardModificationState();
                    if (_run.Cards.TryGetValue(GetCopyKey(player.NetId, index, card.Id), out CardModificationState? temporaryForPrevious))
                        previousState.MergeFrom(temporaryForPrevious);
                }
            }

            bool hasCardMutation = ApplyStateTransitionToCard(card, state, previousState, includeAffliction: true);
            LoadoutCardVisualRefreshKind refreshKind = GetVisualRefreshKind(previousState, state);

            if (hasCardMutation || HasVisualOverrides(state) || HasVisualOverrides(previousState))
                RefreshLiveCardVisuals(card, refreshKind);

            if (changedPermanentKeys is not null)
            {
                changedPlayers?.Add(player.NetId);
                changedCards?.Add(new LoadoutChangedCard(player.NetId, index, card.Id, refreshKind));
            }
        }
    }

    public static string GetCopyKey(LoadoutOwnedItem<CardModel> item)
    {
        return GetCopyKey(item.OwnerNetId, item.Index, item.Model.Id);
    }

    private static bool SaveTemporaryLocal(LoadoutOwnedItem<CardModel> item, CardModificationState normalized)
    {
        normalized.Normalize();
        lock (SyncRoot)
        {
            if (_loadedRunStartTime is null)
            {
                GD.PushWarning("CardModification: no active run; skipped saving temporary card modifications.");
                return false;
            }

            string key = GetCopyKey(item);
            bool changed;
            if (normalized.IsEmpty)
                changed = _run.Cards.Remove(key);
            else
            {
                changed = !_run.Cards.TryGetValue(key, out CardModificationState? existing)
                          || !PermanentStatesEquivalent(existing, normalized);
                if (changed)
                    _run.Cards[key] = normalized.Clone();
            }

            if (changed)
                SaveRunState();

            return changed;
        }
    }

    private static bool ResetTemporaryLocal(LoadoutOwnedItem<CardModel> item)
    {
        lock (SyncRoot)
        {
            bool changed = _run.Cards.Remove(GetCopyKey(item));
            if (changed)
                SaveRunState();

            return changed;
        }
    }

    private static bool SavePermanentLocal(ModelId cardId, CardModificationState normalized)
    {
        normalized.Normalize();
        lock (SyncRoot)
        {
            string key = ToCardKey(cardId);
            bool changed;
            if (normalized.IsEmpty)
            {
                changed = _permanent.Cards.Remove(key);
            }
            else
            {
                changed = !_permanent.Cards.TryGetValue(key, out CardModificationState? existing)
                          || !PermanentStatesEquivalent(existing, normalized);
                if (changed)
                    _permanent.Cards[key] = normalized.Clone();
            }

            if (changed)
            {
                InvalidateDisplayCacheLocked();
                SavePermanentState();
            }

            return changed;
        }
    }

    private static bool ResetPermanentLocal(ModelId cardId)
    {
        lock (SyncRoot)
        {
            bool changed = _permanent.Cards.Remove(ToCardKey(cardId));
            if (changed)
            {
                InvalidateDisplayCacheLocked();
                SavePermanentState();
            }

            return changed;
        }
    }

    private static LoadoutOwnedItem<CardModel>? TryResolveOwnedDeckCard(
        LoadoutTargetSelection target,
        int ownedItemIndex,
        ModelId expectedModelId,
        Player actionPlayer)
    {
        Player? targetPlayer = target.Scope == LoadoutTargetScope.Player && target.PlayerNetId.HasValue
            ? actionPlayer.RunState.GetPlayer(target.PlayerNetId.Value)
            : actionPlayer;

        if (targetPlayer is null || ownedItemIndex < 0)
            return null;

        if (ownedItemIndex < targetPlayer.Deck.Cards.Count)
        {
            CardModel indexedCard = targetPlayer.Deck.Cards[ownedItemIndex];
            if (ModelIdMatches(indexedCard, expectedModelId) && indexedCard.Pile?.Type == PileType.Deck)
                return new LoadoutOwnedItem<CardModel>(targetPlayer, ownedItemIndex, indexedCard);
        }

        string previousKey = GetCopyKey(targetPlayer.NetId, ownedItemIndex, expectedModelId);
        lock (SyncRoot)
        {
            if (!RecentDeckCardIdentities.TryGetValue(previousKey, out WeakReference<CardModel>? reference)
                || !reference.TryGetTarget(out CardModel? trackedCard)
                || !ModelIdMatches(trackedCard, expectedModelId))
            {
                return null;
            }

            int currentIndex = -1;
            for (int index = 0; index < targetPlayer.Deck.Cards.Count; index++)
            {
                if (!ReferenceEquals(targetPlayer.Deck.Cards[index], trackedCard))
                    continue;

                currentIndex = index;
                break;
            }
            return currentIndex >= 0 && trackedCard.Pile?.Type == PileType.Deck
                ? new LoadoutOwnedItem<CardModel>(targetPlayer, currentIndex, trackedCard)
                : null;
        }
    }

    private static void ApplyPermanentStateToLiveCardsFromAction(ModelId cardId, CardModificationState permanentState)
    {
        try
        {
            if (!RunManager.Instance.IsInProgress)
                return;

            RunState? runState = RunManager.Instance.DebugOnlyGetState();
            if (runState is null)
                return;

            List<LoadoutChangedCard> changedCards = [];
            HashSet<ulong> changedPlayers = [];
            foreach (Player player in runState.Players)
            {
                IReadOnlyList<CardModel> cards = player.Deck.Cards;
                for (int index = 0; index < cards.Count; index++)
                {
                    CardModel card = cards[index];
                    if (!ModelIdMatches(card, cardId))
                        continue;

                    CardModificationState previousState = GetEffectiveStateForCopy(player.NetId, index, card.Id.ToString());
                    CardModificationState effective = permanentState.Clone();
                    lock (SyncRoot)
                    {
                        if (_run.Cards.TryGetValue(GetCopyKey(player.NetId, index, card.Id), out CardModificationState? temporary))
                            effective.MergeFrom(temporary);
                    }

                    ApplyStateTransitionToCard(card, effective, previousState, includeAffliction: true);
                    LoadoutCardVisualRefreshKind refreshKind = GetVisualRefreshKind(previousState, effective);
                    RefreshLiveCardVisuals(card, refreshKind);

                    changedPlayers.Add(player.NetId);
                    changedCards.Add(new LoadoutChangedCard(
                        player.NetId,
                        index,
                        card.Id,
                        refreshKind));
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
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: failed to apply synchronized permanent refresh for '{cardId}'. {exception.Message}");
        }
    }

    public static string ExportPermanentSnapshotJson()
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            PermanentSaveData snapshot = NormalizePermanent(new PermanentSaveData
            {
                Cards = _permanent.Cards.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Clone(),
                    StringComparer.Ordinal)
            });
            return JsonSerializer.Serialize(snapshot, SnapshotJsonOptions);
        }
    }

    public static void ApplyHostPermanentSnapshotJson(
        string? snapshotJson,
        HostPermanentSnapshotApplyMode applyMode = HostPermanentSnapshotApplyMode.LiveDecks)
    {
        Dictionary<string, CardModificationState> overlay = new(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(snapshotJson))
        {
            try
            {
                PermanentSaveData snapshot = JsonSerializer.Deserialize<PermanentSaveData>(snapshotJson, SnapshotJsonOptions);
                overlay = NormalizePermanent(snapshot).Cards;
            }
            catch (Exception exception)
            {
                GD.PushWarning($"CardModification: failed to read host permanent snapshot. {exception.Message}");
            }
        }

        Dictionary<string, CardModificationState> previousPermanentStates;
        List<string> changedPermanentKeys;
        lock (SyncRoot)
        {
            previousPermanentStates = _hasHostPermanentOverlay
                ? CloneStateDictionary(_hostPermanentOverlay)
                : CloneStateDictionary(_permanent.Cards);
            changedPermanentKeys = GetChangedPermanentKeys(previousPermanentStates, overlay);
            _hostPermanentOverlay = overlay;
            _hasHostPermanentOverlay = true;
            if (changedPermanentKeys.Count > 0)
                InvalidateDisplayCacheLocked();
        }

        if (changedPermanentKeys.Count == 0)
            return;

        if (applyMode == HostPermanentSnapshotApplyMode.LiveDecks)
        {
            HashSet<string> changedPermanentKeySet = new(changedPermanentKeys, StringComparer.Ordinal);
            ApplySavedRunStateToLiveDecks(previousPermanentStates, raiseStateChanged: false, changedPermanentKeys: changedPermanentKeySet);
        }

        RaisePermanentCardDisplayChanged(ResolvePermanentCardIds(changedPermanentKeys));
        if (applyMode == HostPermanentSnapshotApplyMode.LiveDecks)
            RaiseStateChanged();
    }

    public static void ApplyHostPermanentDelta(string cardId, CardModificationState? state)
    {
        EnsureLoaded();
        if (!TryResolveModel(cardId, ModelDb.AllCards, out CardModel? card) || card is null)
        {
            GD.PushWarning($"CardModification: ignored host delta for unknown card '{cardId}'.");
            return;
        }

        CardModificationState normalized = state?.Clone() ?? new CardModificationState();
        normalized.Normalize();
        string key = ToCardKey(card.Id);
        Dictionary<string, CardModificationState> previousPermanentStates;
        bool changed;
        lock (SyncRoot)
        {
            previousPermanentStates = _hasHostPermanentOverlay
                ? CloneStateDictionary(_hostPermanentOverlay)
                : CloneStateDictionary(_permanent.Cards);

            _hasHostPermanentOverlay = true;
            if (normalized.IsEmpty)
                changed = _hostPermanentOverlay.Remove(key);
            else
            {
                changed = !_hostPermanentOverlay.TryGetValue(key, out CardModificationState? existing)
                          || !PermanentStatesEquivalent(existing, normalized);
                if (changed)
                    _hostPermanentOverlay[key] = normalized.Clone();
            }

            if (changed)
                InvalidateDisplayCacheLocked();
        }

        if (!changed)
            return;

        ApplySavedRunStateToLiveDecks(
            previousPermanentStates,
            raiseStateChanged: false,
            changedPermanentKeys: new HashSet<string>([key], StringComparer.Ordinal));
        RaisePermanentCardDisplayChanged(card.Id);
    }

    public static void ClearHostPermanentOverlay()
    {
        Dictionary<string, CardModificationState> previousPermanentStates;
        List<string> changedPermanentKeys;
        lock (SyncRoot)
        {
            if (!_hasHostPermanentOverlay && _hostPermanentOverlay.Count == 0)
                return;

            previousPermanentStates = CloneStateDictionary(_hostPermanentOverlay);
            changedPermanentKeys = GetChangedPermanentKeys(_hostPermanentOverlay, _permanent.Cards);
            _hostPermanentOverlay.Clear();
            _hasHostPermanentOverlay = false;
            if (changedPermanentKeys.Count > 0)
                InvalidateDisplayCacheLocked();
        }

        if (changedPermanentKeys.Count == 0)
            return;

        HashSet<string> changedPermanentKeySet = new(changedPermanentKeys, StringComparer.Ordinal);
        ApplySavedRunStateToLiveDecks(previousPermanentStates, raiseStateChanged: false, changedPermanentKeys: changedPermanentKeySet);
        RaisePermanentCardDisplayChanged(ResolvePermanentCardIds(changedPermanentKeys));
        RaiseStateChanged();
    }

    public static IReadOnlyList<ModelId> ApplyPermanentSnapshotToProfile(
        string? snapshotJson,
        CardModificationPermanentImportMode mode)
    {
        EnsureLoaded();
        if (mode == CardModificationPermanentImportMode.KeepMine)
            return [];

        Dictionary<string, CardModificationState> incoming = ReadPermanentSnapshot(snapshotJson);
        Dictionary<string, CardModificationState> previousPermanentStates;
        Dictionary<string, CardModificationState> previousLocalStates;
        List<string> changedPermanentKeys;
        bool hasHostOverlay;
        lock (SyncRoot)
        {
            hasHostOverlay = _hasHostPermanentOverlay;
            previousPermanentStates = hasHostOverlay
                ? CloneStateDictionary(_hostPermanentOverlay)
                : CloneStateDictionary(_permanent.Cards);
            previousLocalStates = CloneStateDictionary(_permanent.Cards);
            if (mode == CardModificationPermanentImportMode.UseHost)
            {
                _permanent.Cards = CloneStateDictionary(incoming);
            }
            else
            {
                foreach ((string key, CardModificationState hostState) in incoming)
                {
                    if (!_permanent.Cards.TryGetValue(key, out CardModificationState? localState)
                        || PermanentStatesEquivalent(localState, hostState))
                    {
                        _permanent.Cards[key] = hostState.Clone();
                    }
                }
            }

            _permanent = NormalizePermanent(_permanent);
            changedPermanentKeys = GetChangedPermanentKeys(previousLocalStates, _permanent.Cards);
            if (changedPermanentKeys.Count > 0)
            {
                InvalidateDisplayCacheLocked();
                SavePermanentState();
            }
        }

        if (changedPermanentKeys.Count == 0)
            return [];

        IReadOnlyList<ModelId> changedCardIds = ResolvePermanentCardIds(changedPermanentKeys);
        if (!hasHostOverlay)
        {
            HashSet<string> changedPermanentKeySet = new(changedPermanentKeys, StringComparer.Ordinal);
            ApplySavedRunStateToLiveDecks(previousPermanentStates, raiseStateChanged: false, changedPermanentKeys: changedPermanentKeySet);
            RaisePermanentCardDisplayChanged(changedCardIds);
            RaiseStateChanged();
        }

        return changedCardIds;
    }

    public static void ApplyRemoteTemporaryState(ulong ownerNetId, int index, string cardId, CardModificationState? state)
    {
        EnsureLoaded();
        CardModificationState normalized = state?.Clone() ?? new CardModificationState();
        normalized.Normalize();
        string key = $"{ownerNetId}:{index}:{cardId}";
        CardModificationState previousState = GetEffectiveStateForCopy(ownerNetId, index, cardId);

        bool changed = false;
        lock (SyncRoot)
        {
            if (_loadedRunStartTime is not null)
            {
                if (normalized.IsEmpty)
                    changed = _run.Cards.Remove(key);
                else
                {
                    changed = !_run.Cards.TryGetValue(key, out CardModificationState? existing)
                              || !PermanentStatesEquivalent(existing, normalized);
                    if (changed)
                        _run.Cards[key] = normalized;
                }

                if (changed)
                    SaveRunState();
            }
        }

        if (changed)
            TryApplyRemoteTemporaryToLiveCard(ownerNetId, index, cardId, previousState);
    }

    public static void ClearTemporaryStatesForPlayer(ulong ownerNetId)
    {
        EnsureLoaded();
        bool changed = false;
        lock (SyncRoot)
        {
            string prefix = $"{ownerNetId}:";
            foreach (string key in _run.Cards.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                changed |= _run.Cards.Remove(key);

            if (changed)
                SaveRunState();
        }

        if (changed)
            RaiseStateChanged();
    }

    public static bool ReplaceTemporaryStatesForPlayer(
        Player? player,
        IReadOnlyDictionary<CardModel, CardModificationState>? statesByCard,
        bool reapplyCards = true)
    {
        if (player is null)
            return false;

        EnsureLoaded();

        Dictionary<string, CardModificationState> replacementStates = new(StringComparer.Ordinal);
        List<LoadoutOwnedItem<CardModel>> modifiedCards = new();
        IReadOnlyList<CardModel> cards = player.Deck.Cards;
        for (int index = 0; index < cards.Count; index++)
        {
            CardModel card = cards[index];
            if (statesByCard is null || !statesByCard.TryGetValue(card, out CardModificationState? state))
                continue;

            CardModificationState normalized = state.Clone();
            normalized.Normalize();
            if (normalized.IsEmpty)
                continue;

            replacementStates[GetCopyKey(player.NetId, index, card.Id)] = normalized;
            modifiedCards.Add(new LoadoutOwnedItem<CardModel>(player, index, card));
        }

        bool changed = false;
        lock (SyncRoot)
        {
            string prefix = $"{player.NetId}:";
            foreach (string key in _run.Cards.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                changed |= _run.Cards.Remove(key);

            foreach ((string key, CardModificationState state) in replacementStates)
            {
                _run.Cards[key] = state.Clone();
                changed = true;
            }

            if (changed)
                SaveRunState();
        }

        if (reapplyCards)
        {
            foreach (LoadoutOwnedItem<CardModel> item in modifiedCards)
                ApplyEffectiveStateToOwnedCard(item);
        }

        return changed;
    }

    public static IReadOnlyDictionary<CardModel, CardModificationState> CaptureTemporaryStatesForPlayer(Player player)
    {
        EnsureLoaded();
        Dictionary<CardModel, CardModificationState> states = new(ReferenceEqualityComparer.Instance);
        lock (SyncRoot)
        {
            IReadOnlyList<CardModel> cards = player.Deck.Cards;
            for (int index = 0; index < cards.Count; index++)
            {
                CardModel card = cards[index];
                RecentDeckCardIdentities[GetCopyKey(player.NetId, index, card.Id)] = new WeakReference<CardModel>(card);
                if (_run.Cards.TryGetValue(GetCopyKey(player.NetId, index, card.Id), out CardModificationState? state)
                    && !state.IsEmpty)
                {
                    states[card] = state.Clone();
                }
            }
        }

        return states;
    }

    private static void TryApplyRemoteTemporaryToLiveCard(
        ulong ownerNetId,
        int index,
        string cardId,
        CardModificationState? previousState = null)
    {
        try
        {
            if (!RunManager.Instance.IsInProgress)
                return;

            RunState? runState = RunManager.Instance.DebugOnlyGetState();
            Player? player = runState?.GetPlayer(ownerNetId);
            if (player is null || index < 0 || index >= player.Deck.Cards.Count)
                return;

            CardModel card = player.Deck.Cards[index];
            if (!string.Equals(card.Id.ToString(), cardId, StringComparison.Ordinal)
                && !string.Equals(card.Id.Entry, cardId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            CardModificationState effective;
            lock (SyncRoot)
            {
                effective = GetEffectivePermanentStateLocked(card.Id);
                if (_run.Cards.TryGetValue(GetCopyKey(ownerNetId, index, card.Id), out CardModificationState? temporary))
                    effective.MergeFrom(temporary);
            }

            ApplyStateTransitionToCard(card, effective, previousState, includeAffliction: true);

            LoadoutRunContentChangeService.NotifyCardUpdated(
                new LoadoutOwnedItem<CardModel>(player, index, card),
                GetVisualRefreshKind(previousState, effective));
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: failed to apply remote temporary card modification. {exception.Message}");
        }
    }

    private static CardModificationState GetEffectivePermanentStateLocked(ModelId cardId)
    {
        if (_hasHostPermanentOverlay)
        {
            return _hostPermanentOverlay.TryGetValue(ToCardKey(cardId), out CardModificationState? hostState)
                ? hostState.Clone()
                : new CardModificationState();
        }

        return GetLocalPermanentStateLocked(cardId);
    }

    private static CardModificationState GetCatalogPermanentStateLocked(ModelId cardId)
    {
        return _hasHostPermanentOverlay
            ? GetEffectivePermanentStateLocked(cardId)
            : GetLocalPermanentStateLocked(cardId);
    }

    private static CardModificationState GetLocalPermanentStateLocked(ModelId cardId)
    {
        return GetPermanentStateLocked(cardId).Clone();
    }

    private static CardModificationState GetEffectiveStateForCopy(ulong ownerNetId, int index, string cardId)
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            CardModel? canonical = ModelDb.AllCards.FirstOrDefault(candidate => MatchesModelId(candidate, cardId));
            CardModificationState effective = canonical is null
                ? new CardModificationState()
                : GetEffectivePermanentStateLocked(canonical.Id);
            string key = $"{ownerNetId}:{index}:{cardId}";
            if (_run.Cards.TryGetValue(key, out CardModificationState? temporary))
                effective.MergeFrom(temporary);

            return effective;
        }
    }

    private static void SetEnergyCost(CardModel card, int value)
    {
        card.EnergyCost.SetCustomBaseCost(value);
        EnergyCostCanonicalField?.SetValue(card.EnergyCost, value);
    }

    private static void PrepareCardForState(
        CardModel card,
        CardModificationState state,
        bool includeAffliction,
        CardModificationState? previousState = null)
    {
        bool clearEnchantment = ShouldClearAttachment(card.Enchantment, state.Enchantment, previousState?.Enchantment);
        bool clearAffliction = includeAffliction && ShouldClearAttachment(card.Affliction, state.Affliction, previousState?.Affliction);

        if (clearEnchantment && card.Enchantment is not null)
            CardCmd.ClearEnchantment(card);

        if (clearAffliction && card.Affliction is not null)
            CardCmd.ClearAffliction(card);

        bool replayInfiniteUpgrade = IsKeywordEnabledAfterState(
            card,
            state,
            LoadoutKeywords.InfiniteUpgrade);
        ResetCardToCanonicalBaseline(
            card,
            resetAttachments: false,
            infiniteUpgradeOverride: replayInfiniteUpgrade);
    }

    private static bool ApplyStateTransitionToCard(
        CardModel card,
        CardModificationState state,
        CardModificationState? previousState,
        bool includeAffliction)
    {
        bool hasCardMutation = HasCardMutations(state) || HasCardMutations(previousState);
        if (!hasCardMutation)
            return false;

        if (HasCardMutations(previousState))
        {
            bool resetAttachments = previousState!.Enchantment is not null
                                    || previousState.Affliction is not null;
            ResetCardToCanonicalBaseline(card, resetAttachments, resetUpgrade: false);
        }

        PrepareCardForState(card, state, includeAffliction, previousState);
        ApplyStateToCard(card, state, includeAffliction);
        return true;
    }

    private static bool ShouldClearAttachment(AbstractModel? current, CardAttachmentSpec? next, CardAttachmentSpec? previous)
    {
        if (next is not null)
            return true;

        return previous?.ModelId is not null
               && current is not null
               && MatchesModelId(current, previous.ModelId);
    }

    private static bool HasCardMutations(CardModificationState? state)
    {
        return state is not null
               && (state.EnergyCost.HasValue
                   || state.BaseReplayCount.HasValue
                   || state.BaseStarCost.HasValue
                   || state.DynamicVars.Count > 0
                   || !string.IsNullOrWhiteSpace(state.PoolId)
                   || !string.IsNullOrWhiteSpace(state.Type)
                   || !string.IsNullOrWhiteSpace(state.Rarity)
                   || state.KeywordOverrides.Count > 0
                   || state.Enchantment is not null
                   || state.Affliction is not null);
    }

    private static bool HasVisualOverrides(CardModificationState? state)
    {
        return state is not null
               && (!string.IsNullOrWhiteSpace(state.CustomTitle)
                   || !string.IsNullOrWhiteSpace(state.CustomDescription)
                   || !string.IsNullOrWhiteSpace(state.PortraitPath)
                   || !string.IsNullOrWhiteSpace(state.BetaPortraitPath));
    }

    private static bool TryGetEffectivePermanentStateForMutation(ModelId cardId, out CardModificationState state)
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            string key = ToCardKey(cardId);
            CardModificationState? storedState = _hasHostPermanentOverlay
                ? _hostPermanentOverlay.GetValueOrDefault(key)
                : _permanent.Cards.GetValueOrDefault(key);

            if (!HasCardMutations(storedState))
            {
                state = new CardModificationState();
                return false;
            }

            state = storedState!.Clone();
            return true;
        }
    }

    private static bool IsCombatEnding()
    {
        try
        {
            return CombatManager.Instance.IsEnding;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsKeywordEnabledAfterState(
        CardModel card,
        CardModificationState state,
        CardKeyword keyword)
    {
        foreach ((string rawKeyword, bool enabled) in state.KeywordOverrides)
        {
            if (LoadoutKeywords.TryResolve(rawKeyword, out CardKeyword resolved)
                && resolved.Equals(keyword))
            {
                return enabled;
            }
        }

        return LoadoutKeywords.Has(card, keyword);
    }

    private static void ResetCardToCanonicalBaseline(
        CardModel card,
        bool resetAttachments,
        bool resetUpgrade = false,
        bool? infiniteUpgradeOverride = null)
    {
        if (card.IsCanonical)
            return;

        CardModel? canonical = LoadoutModelRegistry.ResolveCard(card.Id);
        if (canonical is null)
            return;

        CardModel baseline = resetUpgrade
            ? canonical.ToMutable()
            : CreateCanonicalBaselineForCurrentUpgrade(card, canonical, infiniteUpgradeOverride);
        try
        {
            if (resetUpgrade && card.CurrentUpgradeLevel > 0)
            {
                card.DowngradeInternal();
                card.FinalizeUpgradeInternal();
            }

            LoadoutKeywordMechanics.SynchronizeEnergyCost(
                card,
                new Dictionary<string, bool>(StringComparer.Ordinal),
                baseline.EnergyCost.Canonical);

            if (!card.EnergyCost.CostsX)
                SetEnergyCost(card, baseline.EnergyCost.Canonical);

            card.BaseReplayCount = baseline.BaseReplayCount;
            SetBaseStarCost(card, baseline.BaseStarCost);

            foreach ((string name, var dynamicVar) in baseline.DynamicVars)
            {
                if (card.DynamicVars.TryGetValue(name, out var mutableDynamicVar))
                    mutableDynamicVar.BaseValue = dynamicVar.BaseValue;
            }

            CardPoolField?.SetValue(card, baseline.Pool);
            CardTypeField?.SetValue(card, baseline.Type);
            CardRarityField?.SetValue(card, baseline.Rarity);

            IReadOnlySet<CardKeyword> canonicalKeywords = baseline.GetKeywordsWithSources(KeywordSources.Local);
            foreach (CardKeyword keyword in card.GetKeywordsWithSources(KeywordSources.Local).ToList())
            {
                if (!canonicalKeywords.Contains(keyword))
                    card.RemoveKeyword(keyword);
            }

            foreach (CardKeyword keyword in canonicalKeywords)
            {
                if (!card.GetKeywordsWithSources(KeywordSources.Local).Contains(keyword))
                    card.AddKeyword(keyword);
            }

            if (resetAttachments)
            {
                ResetEnchantmentToCanonical(card, baseline);
                ResetAfflictionToCanonical(card, baseline);
            }
            else
            {
                try
                {
                    card.Enchantment?.ModifyCard();
                    card.FinalizeUpgradeInternal();
                }
                catch (Exception exception)
                {
                    GD.PushWarning($"CardModification: failed to reapply preserved enchantment for '{card.Id}'. {exception.Message}");
                }
            }
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: failed to reset '{card.Id}' to canonical baseline. {exception.Message}");
        }
    }

    private static CardModel CreateCanonicalBaselineForCurrentUpgrade(
        CardModel card,
        CardModel canonical,
        bool? infiniteUpgradeOverride = null)
    {
        CardModel baseline = canonical.ToMutable();
        bool replayInfiniteUpgrade = infiniteUpgradeOverride
                                     ?? LoadoutKeywords.Has(card, LoadoutKeywords.InfiniteUpgrade);

        // The modifier refresh rebuilds a card from its canonical model. The canonical
        // model normally caps at +1 and does not contain the custom keyword, so a +2
        // card used to be silently rebuilt as +1 here. Give the temporary baseline the
        // keyword before replaying upgrades so it follows the same deterministic path
        // as the live card.
        if (replayInfiniteUpgrade
            && !LoadoutKeywords.Has(baseline, LoadoutKeywords.InfiniteUpgrade))
        {
            baseline.AddKeyword(LoadoutKeywords.InfiniteUpgrade);
        }

        int upgradeLevel = Math.Max(0, card.CurrentUpgradeLevel);
        if (!replayInfiniteUpgrade)
            upgradeLevel = Math.Min(upgradeLevel, baseline.MaxUpgradeLevel);

        for (int i = 0; i < upgradeLevel; i++)
        {
            if (!baseline.IsUpgradable)
                break;

            baseline.UpgradeInternal();
            baseline.FinalizeUpgradeInternal();
        }

        return baseline;
    }

    private static void ResetEnchantmentToCanonical(CardModel card, CardModel canonical)
    {
        if (canonical.Enchantment is null)
        {
            if (card.Enchantment is not null)
                CardCmd.ClearEnchantment(card);

            return;
        }

        if (card.Enchantment is not null && !SameModelId(card.Enchantment, canonical.Enchantment))
            CardCmd.ClearEnchantment(card);

        if (card.Enchantment is null)
            ForceApplyEnchantment(card, canonical.Enchantment, Math.Max(1, canonical.Enchantment.Amount));
        else
            card.Enchantment.Amount = Math.Max(1, canonical.Enchantment.Amount);
    }

    private static void ResetAfflictionToCanonical(CardModel card, CardModel canonical)
    {
        if (canonical.Affliction is null)
        {
            if (card.Affliction is not null)
                CardCmd.ClearAffliction(card);

            return;
        }

        if (card.Affliction is not null && !SameModelId(card.Affliction, canonical.Affliction))
            CardCmd.ClearAffliction(card);

        if (card.Affliction is null)
            ForceApplyAffliction(card, canonical.Affliction, Math.Max(1, canonical.Affliction.Amount));
        else
            card.Affliction.Amount = Math.Max(1, canonical.Affliction.Amount);
    }

    public static void ApplySavedRunStateToLiveDecks()
    {
        ApplySavedRunStateToLiveDecks(null);
    }

    private static void ApplySavedRunStateToLiveDecks(
        IReadOnlyDictionary<string, CardModificationState>? previousPermanentStates,
        bool raiseStateChanged = true,
        IReadOnlySet<string>? changedPermanentKeys = null)
    {
        try
        {
            if (!RunManager.Instance.IsInProgress)
                return;

            RunState? runState = RunManager.Instance.DebugOnlyGetState();
            if (runState is null)
                return;

            List<LoadoutChangedCard>? changedCards = changedPermanentKeys is null ? null : [];
            HashSet<ulong>? changedPlayers = changedPermanentKeys is null ? null : [];
            foreach (Player player in runState.Players)
            {
                ApplySavedRunStateToPlayerDeck(
                    player,
                    previousPermanentStates,
                    changedPermanentKeys,
                    changedCards,
                    changedPlayers);
            }

            if (changedCards is { Count: > 0 } && changedPlayers is not null)
            {
                LoadoutRunContentChangeService.Notify(
                    LoadoutRunContentKind.Cards,
                    changedPlayers,
                    LoadoutRunContentChangeMode.Update,
                    changedCards);
            }

            if (raiseStateChanged)
                RaiseStateChanged();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: failed to refresh live decks after permanent overlay update. {exception.Message}");
        }
    }

    private static void RefreshLiveCardVisuals(
        CardModel card,
        LoadoutCardVisualRefreshKind refreshKind = LoadoutCardVisualRefreshKind.Reload)
    {
        try
        {
            object? cardNodeObject = NCardFindOnTableByCard?.Invoke(null, [card]);
            cardNodeObject ??= NCardFindOnTableByCardAndPile?.Invoke(null, [card, card.Pile?.Type ?? PileType.None]);
            if (cardNodeObject is not NCard cardNode)
                return;

            PileType pileType = card.Pile?.Type ?? PileType.None;
            if (refreshKind == LoadoutCardVisualRefreshKind.Reload)
            {
                cardNode.Model = null;
                cardNode.Model = card;
            }

            cardNode.UpdateVisuals(pileType, CardPreviewMode.Normal);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: updated card '{card.Id}', but could not refresh its live visual. {exception.Message}");
        }
    }

    private static void SetPreviewState(CardModel card, CardModificationState state)
    {
        if (card.IsCanonical)
            return;

        lock (SyncRoot)
        {
            PreviewCardStates.Remove(card);
            PreviewCardStates.Add(card, new PreviewCardStateHolder(state.Clone()));
            InvalidateEffectiveStateCacheLocked();
        }
    }

    private static void ClearPreviewState(CardModel card)
    {
        if (card.IsCanonical)
            return;

        lock (SyncRoot)
        {
            if (PreviewCardStates.Remove(card))
                InvalidateEffectiveStateCacheLocked();
        }
    }

    private static bool IsLocalCatalogDisplayCard(CardModel card)
    {
        return card.IsCanonical || LocalCatalogDisplayCards.TryGetValue(card, out _);
    }

    private static void MarkLocalCatalogDisplayCard(CardModel card)
    {
        try
        {
            LocalCatalogDisplayCards.GetValue(card, _ => new LocalCatalogDisplayCardMarker());
            EffectiveStateCache.Remove(card);
        }
        catch
        {
            // Weak-table marking is best effort; canonical cards remain local-catalog by default.
        }
    }

    private static List<string> GetChangedPermanentKeys(
        IReadOnlyDictionary<string, CardModificationState> before,
        IReadOnlyDictionary<string, CardModificationState> after)
    {
        HashSet<string> keys = new(before.Keys, StringComparer.Ordinal);
        keys.UnionWith(after.Keys);

        List<string> changed = new();
        foreach (string key in keys)
        {
            before.TryGetValue(key, out CardModificationState? beforeState);
            after.TryGetValue(key, out CardModificationState? afterState);
            if (!PermanentStatesEquivalent(beforeState, afterState))
                changed.Add(key);
        }

        return changed;
    }

    private static Dictionary<string, CardModificationState> ReadPermanentSnapshot(string? snapshotJson)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson))
            return new Dictionary<string, CardModificationState>(StringComparer.Ordinal);

        try
        {
            PermanentSaveData snapshot = JsonSerializer.Deserialize<PermanentSaveData>(snapshotJson, SnapshotJsonOptions);
            return NormalizePermanent(snapshot).Cards;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: failed to read permanent card modification snapshot. {exception.Message}");
            return new Dictionary<string, CardModificationState>(StringComparer.Ordinal);
        }
    }

    private static IReadOnlyList<ModelId> ResolvePermanentCardIds(IEnumerable<string> cardKeys)
    {
        List<ModelId> cardIds = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string cardKey in cardKeys)
        {
            if (!TryResolveModel(cardKey, ModelDb.AllCards, out CardModel? card) || card is null)
                continue;

            string resolvedKey = card.Id.ToString();
            if (seen.Add(resolvedKey))
                cardIds.Add(card.Id);
        }

        return cardIds;
    }

    private static bool PermanentStatesEquivalent(CardModificationState? left, CardModificationState? right)
    {
        if (ReferenceEquals(left, right))
            return true;

        if (left is null)
            return right is null || right.IsEmpty;

        if (right is null)
            return left.IsEmpty;

        return left.EnergyCost == right.EnergyCost
               && left.BaseReplayCount == right.BaseReplayCount
               && left.BaseStarCost == right.BaseStarCost
               && DictionaryEquals(left.DynamicVars, right.DynamicVars)
               && string.Equals(left.PoolId, right.PoolId, StringComparison.Ordinal)
               && string.Equals(left.Type, right.Type, StringComparison.Ordinal)
               && string.Equals(left.Rarity, right.Rarity, StringComparison.Ordinal)
               && string.Equals(left.CustomTitle, right.CustomTitle, StringComparison.Ordinal)
               && string.Equals(left.CustomDescription, right.CustomDescription, StringComparison.Ordinal)
               && string.Equals(left.PortraitPath, right.PortraitPath, StringComparison.Ordinal)
               && string.Equals(left.BetaPortraitPath, right.BetaPortraitPath, StringComparison.Ordinal)
               && DictionaryEquals(left.KeywordOverrides, right.KeywordOverrides)
               && AttachmentSpecsEquivalent(left.Enchantment, right.Enchantment)
               && AttachmentSpecsEquivalent(left.Affliction, right.Affliction);
    }

    private static bool DictionaryEquals<TValue>(
        IReadOnlyDictionary<string, TValue> left,
        IReadOnlyDictionary<string, TValue> right)
    {
        if (left.Count != right.Count)
            return false;

        EqualityComparer<TValue> comparer = EqualityComparer<TValue>.Default;
        foreach ((string key, TValue leftValue) in left)
        {
            if (!right.TryGetValue(key, out TValue? rightValue) || !comparer.Equals(leftValue, rightValue))
                return false;
        }

        return true;
    }

    private static bool AttachmentSpecsEquivalent(CardAttachmentSpec? left, CardAttachmentSpec? right)
    {
        if (ReferenceEquals(left, right))
            return true;

        if (left is null)
            return right is null || right.IsEmpty;

        if (right is null)
            return left.IsEmpty;

        return string.Equals(left.ModelId, right.ModelId, StringComparison.Ordinal)
               && left.Amount == right.Amount
               && left.Clear == right.Clear;
    }

    private static Dictionary<string, CardModificationState> CloneStateDictionary(
        Dictionary<string, CardModificationState> states)
    {
        return states.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone(),
            StringComparer.Ordinal);
    }

    private static void RaisePermanentCardDisplayChanged(ModelId cardId)
    {
        Action<ModelId>? handlers = PermanentCardDisplayChanged;
        if (handlers is null)
            return;

        foreach (Action<ModelId> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(cardId);
            }
            catch (Exception exception)
            {
                GD.PushWarning($"CardModification: permanent-card display handler failed. {exception.Message}");
            }
        }
    }

    private static void RaisePermanentCardDisplayChanged(IEnumerable<ModelId> cardIds)
    {
        HashSet<string> raised = new(StringComparer.Ordinal);
        foreach (ModelId cardId in cardIds)
        {
            string key = cardId.ToString();
            if (!string.IsNullOrWhiteSpace(key) && raised.Add(key))
                RaisePermanentCardDisplayChanged(cardId);
        }
    }

    private static void RaiseStateChanged()
    {
        Action? handlers = StateChanged;
        if (handlers is null)
            return;

        foreach (Action handler in handlers.GetInvocationList())
        {
            try
            {
                handler();
            }
            catch (Exception exception)
            {
                GD.PushWarning($"CardModification: state-changed handler failed. {exception.Message}");
            }
        }
    }

    private static void InvalidateDisplayCacheLocked()
    {
        _displayRevision++;
        DisplayCardCache.Clear();
        InvalidateEffectiveStateCacheLocked();
    }

    private static void InvalidateEffectiveStateCacheLocked()
    {
        unchecked
        {
            _effectiveStateRevision++;
        }
    }

    private static void ApplyKeywordOverrides(CardModel card, Dictionary<string, bool> keywordOverrides)
    {
        foreach ((string rawKeyword, bool enabled) in keywordOverrides)
        {
            if (!LoadoutKeywords.TryResolve(rawKeyword, out CardKeyword keyword) || keyword == CardKeyword.None)
                continue;

            if (enabled)
            {
                if (!card.GetKeywordsWithSources(KeywordSources.Local).Contains(keyword))
                    card.AddKeyword(keyword);
            }
            else
            {
                if (card.GetKeywordsWithSources(KeywordSources.Local).Contains(keyword))
                    card.RemoveKeyword(keyword);
            }
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

        if (!TryResolveModel(spec.ModelId, ModelDb.DebugEnchantments, out EnchantmentModel? canonical)
            || canonical is null)
            return;

        int amount = Math.Max(1, spec.Amount);
        if (card.Enchantment is not null)
        {
            if (SameModelId(card.Enchantment, canonical))
                card.Enchantment.Amount = amount;
            else
                CardCmd.ClearEnchantment(card);
        }

        ForceApplyEnchantment(card, canonical, amount);
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

        if (!TryResolveModel(spec.ModelId, ModelDb.DebugAfflictions, out AfflictionModel? canonical)
            || canonical is null)
            return;

        int amount = Math.Max(1, spec.Amount);
        if (card.Affliction is not null)
        {
            if (SameModelId(card.Affliction, canonical))
                card.Affliction.Amount = amount;
            else
                CardCmd.ClearAffliction(card);
        }

        ForceApplyAffliction(card, canonical, amount);
    }

    private static void ForceApplyEnchantment(CardModel card, EnchantmentModel canonical, int amount)
    {
        try
        {
            if (card.Enchantment is null)
                card.EnchantInternal(canonical.ToMutable(), amount);
            else
                card.Enchantment.Amount = amount;

            card.Enchantment?.ModifyCard();
            card.FinalizeUpgradeInternal();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: could not force enchant '{card.Id}' with '{canonical.Id}'. {exception.Message}");
        }
    }

    private static void ForceApplyAffliction(CardModel card, AfflictionModel canonical, int amount)
    {
        try
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

            try
            {
                card.Affliction?.AfterApplied();
            }
            catch (Exception exception)
            {
                GD.PushWarning($"CardModification: force affliction '{canonical.Id}' applied to '{card.Id}', but AfterApplied failed. {exception.Message}");
            }
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: could not force afflict '{card.Id}' with '{canonical.Id}'. {exception.Message}");
        }
    }

    private static bool TryResolvePool(string? poolId, out CardPoolModel? pool)
    {
        return TryResolveModel(poolId, ModelDb.AllCardPools, out pool);
    }

    private static bool TryResolveModel<TModel>(string? id, IEnumerable<TModel> models, out TModel? model)
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

    private static bool SameModelId(AbstractModel left, AbstractModel right)
    {
        return string.Equals(left.Id.ToString(), right.Id.ToString(), StringComparison.Ordinal);
    }

    private static bool TryParseEnum<TEnum>(string? value, out TEnum result)
        where TEnum : struct, Enum
    {
        if (Enum.TryParse(value, ignoreCase: true, out result))
            return true;

        result = default;
        return false;
    }

    private static void SetBaseStarCost(CardModel card, int value)
    {
        if (BaseStarCostSetter is not null)
        {
            BaseStarCostSetter.Invoke(card, [value]);
            return;
        }

        GD.PushWarning($"CardModification: could not set star cost for '{card.Id}' because BaseStarCost setter was not found.");
    }

    private static void OnCombatSetUp(CombatState combatState)
    {
        if (combatState is null)
            return;

        foreach (Player player in combatState.Players)
            ApplySavedRunStateToPlayerDeck(player);
    }

    private static void OnRunStarted(RunState _)
    {
        ReloadRun();
        ApplySavedRunStateToLiveDecks();
    }

    private static void OnProfileIdChanged(int _)
    {
        FlushPendingSaves();
        lock (SyncRoot)
        {
            _permanentLoaded = false;
            _runLoaded = false;
            _loadedRunStartTime = null;
            _permanent = new PermanentSaveData();
            _run = new RunSaveData();
            RecentDeckCardIdentities.Clear();
            _hostPermanentOverlay.Clear();
            _hasHostPermanentOverlay = false;
            InvalidateDisplayCacheLocked();
        }

        EnsureLoaded();
    }

    private static void ReloadPermanentIfNeeded()
    {
        lock (SyncRoot)
        {
            if (_permanentLoaded)
                return;

            SaveUtility.LoadResult<PermanentSaveData> loaded =
                SaveUtility.LoadProfileJson(PermanentPath, new PermanentSaveData());
            _permanent = NormalizePermanent(loaded.Value);
            _permanentLoaded = true;
            InvalidateDisplayCacheLocked();

            if (loaded.Loaded && loaded.Value.SchemaVersion != CurrentSchemaVersion)
                SavePermanentState();
        }
    }

    private static void ReloadRun()
    {
        lock (SyncRoot)
        {
            _runLoaded = false;
            _loadedRunStartTime = null;
            _run = new RunSaveData();
            RecentDeckCardIdentities.Clear();
            InvalidateEffectiveStateCacheLocked();
        }

        ReloadRunIfNeeded();
    }

    private static void ReloadRunIfNeeded()
    {
        long? currentRunStartTime = SaveUtility.GetCurrentRunStartTime();
        lock (SyncRoot)
        {
            if (_runLoaded && _loadedRunStartTime == currentRunStartTime)
                return;

            _runLoaded = true;
            _loadedRunStartTime = currentRunStartTime;
            if (currentRunStartTime is null)
            {
                _run = NormalizeRun(new RunSaveData(), 0);
                InvalidateEffectiveStateCacheLocked();
                return;
            }

            string path = GetRunPath(currentRunStartTime.Value);
            SaveUtility.LoadResult<RunSaveData> loaded =
                SaveUtility.LoadProfileJson(path, new RunSaveData { RunStartTime = currentRunStartTime.Value });
            _run = NormalizeRun(loaded.Value, currentRunStartTime.Value);
            InvalidateEffectiveStateCacheLocked();

            if (loaded.Loaded && loaded.Value.SchemaVersion != CurrentSchemaVersion)
                SaveRunState();
        }
    }

    private static void SavePermanentState()
    {
        lock (SyncRoot)
        {
            _permanentSavePending = true;
            QueueSaveFlushLocked();
        }
    }

    private static void SaveRunState()
    {
        lock (SyncRoot)
        {
            if (_loadedRunStartTime is null)
                return;

            InvalidateEffectiveStateCacheLocked();
            _runSavePending = true;
            QueueSaveFlushLocked();
        }
    }

    private static void QueueSaveFlushLocked()
    {
        if (_saveFlushQueued)
            return;

        _saveFlushQueued = true;
        Callable.From(FlushPendingSaves).CallDeferred();
    }

    public static void FlushPendingSaves()
    {
        PermanentSaveData? permanentSnapshot = null;
        RunSaveData? runSnapshot = null;
        string? runPath = null;

        lock (SyncRoot)
        {
            _saveFlushQueued = false;

            if (_permanentSavePending)
            {
                permanentSnapshot = NormalizePermanent(new PermanentSaveData
                {
                    Cards = CloneStateDictionary(_permanent.Cards)
                });
                _permanentSavePending = false;
            }

            if (_runSavePending && _loadedRunStartTime.HasValue)
            {
                long runStartTime = _loadedRunStartTime.Value;
                runSnapshot = NormalizeRun(new RunSaveData
                {
                    RunStartTime = runStartTime,
                    Cards = CloneStateDictionary(_run.Cards)
                }, runStartTime);
                runPath = GetRunPath(runStartTime);
                _runSavePending = false;
            }
        }

        // File IO deliberately happens outside SyncRoot. Rendering patches can
        // continue reading card state while the sidecar/profile JSON is written.
        if (permanentSnapshot.HasValue)
            SaveUtility.SaveProfileJson(PermanentPath, permanentSnapshot.Value);
        if (runSnapshot.HasValue && runPath is not null)
            SaveUtility.SaveProfileJson(runPath, runSnapshot.Value);
    }

    private static PermanentSaveData NormalizePermanent(PermanentSaveData save)
    {
        save.SchemaVersion = CurrentSchemaVersion;
        save.Cards = NormalizeStateDictionary(save.Cards);
        return save;
    }

    private static RunSaveData NormalizeRun(RunSaveData save, long runStartTime)
    {
        save.SchemaVersion = CurrentSchemaVersion;
        save.RunStartTime = runStartTime;
        save.Cards = NormalizeStateDictionary(save.Cards);
        return save;
    }

    private static Dictionary<string, CardModificationState> NormalizeStateDictionary(Dictionary<string, CardModificationState>? states)
    {
        Dictionary<string, CardModificationState> normalized = new(StringComparer.Ordinal);
        if (states is null)
            return normalized;

        foreach ((string key, CardModificationState? value) in states)
        {
            if (string.IsNullOrWhiteSpace(key) || value is null)
                continue;

            CardModificationState state = value.Clone();
            state.Normalize();
            if (!state.IsEmpty)
                normalized[key] = state;
        }

        return normalized;
    }

    private static CardModificationState GetPermanentStateLocked(ModelId cardId)
    {
        return _permanent.Cards.TryGetValue(ToCardKey(cardId), out CardModificationState? state)
            ? state
            : new CardModificationState();
    }

    private static string ToCardKey(ModelId cardId)
    {
        return cardId.ToString();
    }

    private static string GetCopyKey(ulong ownerNetId, int index, ModelId cardId)
    {
        return $"{ownerNetId}:{index}:{cardId}";
    }

    private static bool TryGetCopyKey(CardModel card, out string? copyKey)
    {
        copyKey = null;
        if (card.IsCanonical)
            return false;

        try
        {
            CardModel deckCard = card.DeckVersion ?? card;
            if (!deckCard.Id.Equals(card.Id))
                return false;

            Player? owner = deckCard.Owner ?? card.Owner;
            if (owner is null)
                return false;

            IReadOnlyList<CardModel> deckCards = owner.Deck.Cards;
            for (int index = 0; index < deckCards.Count; index++)
            {
                if (!ReferenceEquals(deckCards[index], deckCard))
                    continue;

                copyKey = GetCopyKey(owner.NetId, index, card.Id);
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static string GetRunPath(long runStartTime)
    {
        return SaveUtility.GetRunSidecarPath(RunDirectory, RunFilePrefix, runStartTime);
    }

    private sealed class LocalCatalogDisplayCardMarker
    {
    }

    private sealed class EffectiveStateCacheHolder
    {
        public int Revision { get; set; } = -1;

        public CardModificationState State { get; set; } = new();
    }

    private sealed class PreviewCardStateHolder(CardModificationState state)
    {
        public CardModificationState State { get; } = state;
    }

    private sealed class TargetedUpgradeRefreshScope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _targetedUpgradeDepth = Math.Max(0, _targetedUpgradeDepth - 1);
        }
    }

    private readonly record struct CachedDisplayCard(int Revision, CardModel Card);

    private struct PermanentSaveData : ISerializable
    {
        public PermanentSaveData()
        {
        }

        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [JsonPropertyName("cards")]
        public Dictionary<string, CardModificationState> Cards { get; set; } = new(StringComparer.Ordinal);

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue(nameof(SchemaVersion), SchemaVersion);
            info.AddValue(nameof(Cards), Cards);
        }
    }

    private struct RunSaveData : ISerializable
    {
        public RunSaveData()
        {
        }

        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [JsonPropertyName("runStartTime")]
        public long RunStartTime { get; set; }

        [JsonPropertyName("cards")]
        public Dictionary<string, CardModificationState> Cards { get; set; } = new(StringComparer.Ordinal);

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue(nameof(SchemaVersion), SchemaVersion);
            info.AddValue(nameof(RunStartTime), RunStartTime);
            info.AddValue(nameof(Cards), Cards);
        }
    }
}

public sealed class CardAttachmentSpec
{
    [JsonPropertyName("modelId")]
    public string? ModelId { get; set; }

    [JsonPropertyName("amount")]
    public int Amount { get; set; } = 1;

    [JsonPropertyName("clear")]
    public bool Clear { get; set; }

    [JsonIgnore]
    public bool IsEmpty => !Clear && string.IsNullOrWhiteSpace(ModelId);

    public CardAttachmentSpec Clone()
    {
        return new CardAttachmentSpec
        {
            ModelId = ModelId,
            Amount = Amount,
            Clear = Clear
        };
    }
}

public sealed class CardModificationState
{
    [JsonPropertyName("energyCost")]
    public int? EnergyCost { get; set; }

    [JsonPropertyName("baseReplayCount")]
    public int? BaseReplayCount { get; set; }

    [JsonPropertyName("baseStarCost")]
    public int? BaseStarCost { get; set; }

    [JsonPropertyName("dynamicVars")]
    public Dictionary<string, decimal> DynamicVars { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("poolId")]
    public string? PoolId { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("rarity")]
    public string? Rarity { get; set; }

    [JsonPropertyName("customTitle")]
    public string? CustomTitle { get; set; }

    [JsonPropertyName("customDescription")]
    public string? CustomDescription { get; set; }

    [JsonPropertyName("portraitPath")]
    public string? PortraitPath { get; set; }

    [JsonPropertyName("betaPortraitPath")]
    public string? BetaPortraitPath { get; set; }

    [JsonPropertyName("keywordOverrides")]
    public Dictionary<string, bool> KeywordOverrides { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("enchantment")]
    public CardAttachmentSpec? Enchantment { get; set; }

    [JsonPropertyName("affliction")]
    public CardAttachmentSpec? Affliction { get; set; }

    [JsonIgnore]
    public bool IsEmpty =>
        EnergyCost is null
        && BaseReplayCount is null
        && BaseStarCost is null
        && DynamicVars.Count == 0
        && string.IsNullOrWhiteSpace(PoolId)
        && string.IsNullOrWhiteSpace(Type)
        && string.IsNullOrWhiteSpace(Rarity)
        && string.IsNullOrWhiteSpace(CustomTitle)
        && string.IsNullOrWhiteSpace(CustomDescription)
        && string.IsNullOrWhiteSpace(PortraitPath)
        && string.IsNullOrWhiteSpace(BetaPortraitPath)
        && KeywordOverrides.Count == 0
        && (Enchantment is null || Enchantment.IsEmpty)
        && (Affliction is null || Affliction.IsEmpty);

    public CardModificationState Clone()
    {
        return new CardModificationState
        {
            EnergyCost = EnergyCost,
            BaseReplayCount = BaseReplayCount,
            BaseStarCost = BaseStarCost,
            DynamicVars = new Dictionary<string, decimal>(DynamicVars, StringComparer.Ordinal),
            PoolId = PoolId,
            Type = Type,
            Rarity = Rarity,
            CustomTitle = CustomTitle,
            CustomDescription = CustomDescription,
            PortraitPath = PortraitPath,
            BetaPortraitPath = BetaPortraitPath,
            KeywordOverrides = new Dictionary<string, bool>(KeywordOverrides, StringComparer.Ordinal),
            Enchantment = Enchantment?.Clone(),
            Affliction = Affliction?.Clone()
        };
    }

    public void MergeFrom(CardModificationState? other)
    {
        if (other is null)
            return;

        if (other.EnergyCost.HasValue)
            EnergyCost = other.EnergyCost;

        if (other.BaseReplayCount.HasValue)
            BaseReplayCount = other.BaseReplayCount;

        if (other.BaseStarCost.HasValue)
            BaseStarCost = other.BaseStarCost;

        foreach ((string key, decimal value) in other.DynamicVars)
            DynamicVars[key] = value;

        if (!string.IsNullOrWhiteSpace(other.PoolId))
            PoolId = other.PoolId;

        if (!string.IsNullOrWhiteSpace(other.Type))
            Type = other.Type;

        if (!string.IsNullOrWhiteSpace(other.Rarity))
            Rarity = other.Rarity;

        if (other.CustomTitle is not null)
            CustomTitle = other.CustomTitle;

        if (other.CustomDescription is not null)
            CustomDescription = other.CustomDescription;

        if (!string.IsNullOrWhiteSpace(other.PortraitPath))
            PortraitPath = other.PortraitPath;

        if (!string.IsNullOrWhiteSpace(other.BetaPortraitPath))
            BetaPortraitPath = other.BetaPortraitPath;

        foreach ((string key, bool value) in other.KeywordOverrides)
            KeywordOverrides[key] = value;

        if (other.Enchantment is not null)
            Enchantment = other.Enchantment.Clone();

        if (other.Affliction is not null)
            Affliction = other.Affliction.Clone();
    }

    public void Normalize()
    {
        DynamicVars = DynamicVars
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        KeywordOverrides = KeywordOverrides
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        if (Enchantment?.IsEmpty == true)
            Enchantment = null;
        else
            NormalizeAttachment(Enchantment);

        if (Affliction?.IsEmpty == true)
            Affliction = null;
        else
            NormalizeAttachment(Affliction);

        CustomTitle = NormalizeText(CustomTitle);
        CustomDescription = NormalizeText(CustomDescription);
        PortraitPath = NormalizeText(PortraitPath);
        BetaPortraitPath = NormalizeText(BetaPortraitPath);
    }

    private static void NormalizeAttachment(CardAttachmentSpec? spec)
    {
        if (spec is null)
            return;

        spec.Amount = Math.Max(1, spec.Amount);
        if (spec.Clear)
            spec.ModelId = null;
    }

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim();
    }
}
