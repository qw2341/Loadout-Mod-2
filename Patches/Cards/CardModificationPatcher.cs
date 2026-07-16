#nullable enable

namespace Loadout.Patches.Cards;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
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

public static class CardModificationPatcher
{
    private const int CurrentSchemaVersion = 1;
    private const string PermanentPath = "loadout/card_modifications/permanent_v4.json";

    private static readonly object SyncRoot = new();
    private static readonly object PristineGate = new();
    private static readonly Dictionary<ModelId, CardModel> PristineCanonicalCards = new();
    private static readonly ConditionalWeakTable<CardModel, PermanentAppliedMarker> PermanentApplied = new();
    private static PermanentSnapshot _activePermanentSnapshot = new(
        0,
        new Dictionary<string, CardModificationState>(StringComparer.Ordinal));
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
    private static Dictionary<string, CardModificationState> _hostPermanentOverlay = new(StringComparer.Ordinal);
    private static bool _hasHostPermanentOverlay;
    private static bool _registered;
    private static bool _permanentLoaded;
    private static bool _permanentSavePending;
    private static bool _saveFlushQueued;

    [ThreadStatic]
    private static int _targetedUpgradeDepth;

    [ThreadStatic]
    internal static bool SuppressAutomaticApply;

    public static event Action? StateChanged;
    public static event Action<ModelId>? PermanentCardDisplayChanged;

    public static void Register()
    {
        if (_registered)
            return;

        _registered = true;
        CardModificationFields.Initialize();
        SaveManager.Instance.ProfileIdChanged += OnProfileIdChanged;
        CardModificationNetworkPatch.Register();
        EnsureLoaded();
    }

    public static void Unregister()
    {
        if (!_registered)
            return;

        FlushPendingSaves();
        SaveManager.Instance.ProfileIdChanged -= OnProfileIdChanged;
        CardModificationNetworkPatch.Unregister();
        _registered = false;
    }

    public static void EnsureLoaded()
    {
        // This method sits under title/description/portrait patches, so the already-
        // loaded path must not take the global state lock on every card access.
        if (Volatile.Read(ref _permanentLoaded))
            return;

        // Per-card temporary data is imported directly by the CardModel.FromSerializable
        // patch. Only profile-wide permanent definitions need explicit loading here.
        ReloadPermanentIfNeeded();
    }

    public static CardModificationState GetEffectiveState(LoadoutOwnedItem<CardModel> item)
    {
        EnsureLoaded();
        return ComposeEffectiveState(item.Model, includeTemporary: true);
    }

    public static CardModificationState GetEffectiveStateForCard(CardModel card)
    {
        return GetEffectiveStateForCardReadOnly(card).Clone();
    }

    private static CardModificationState GetEffectiveStateForCardReadOnly(CardModel card)
    {
        // Used only by explicit editor/upgrade events. Gameplay reads use native
        // CardModel values plus sparse direct override fields.
        return ComposeEffectiveState(card, includeTemporary: true);
    }

    private static CardModificationState ComposeEffectiveState(CardModel card, bool includeTemporary)
    {
        PermanentSnapshot snapshot = Volatile.Read(ref _activePermanentSnapshot);
        CardModificationState effective = snapshot.Cards.TryGetValue(
            ToCardKey(card.Id),
            out CardModificationState? permanent)
            ? permanent.Clone()
            : new CardModificationState();

        if (includeTemporary && CardModificationFields.TryGetTemporary(card, out CardModificationState temporary))
            effective.MergeFrom(temporary);
        effective.Normalize();
        return effective;
    }

    public static bool TryGetCustomTitle(CardModel card, out string title)
    {
        return CardModificationFields.TryGetCustomTitle(card, out title);
    }

    public static bool TryGetPortraitPath(CardModel card, bool beta, string currentPath, out string path)
    {
        return CardModificationFields.TryGetPortraitPath(card, beta, out path);
    }

    public static bool TryGetCustomDescription(CardModel card, out string description)
    {
        return CardModificationFields.TryGetCustomDescription(card, out description);
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

    public static bool TryGetTemporaryState(CardModel card, out CardModificationState state)
    {
        if (CardModificationFields.TryGetTemporary(card, out CardModificationState attached))
        {
            state = attached.Clone();
            return true;
        }

        state = null!;
        return false;
    }

    public static void ApplyLoadoutTemporaryState(CardModel card, CardModificationState? state)
    {
        CardModificationState normalized = state?.Clone() ?? new CardModificationState();
        normalized.Normalize();
        CardModificationFields.SetTemporary(card, normalized);

        // This is a one-time loadout construction boundary. Rebuild this exact card
        // from its canonical permanent base plus its attached temporary state, then
        // let the caller apply the saved upgrade count. No player/deck scan occurs.
        ApplyLoadedStateToCard(card);
    }

    public static CardModificationState GetTemporaryState(LoadoutOwnedItem<CardModel> item)
    {
        return CardModificationFields.Get(item.Model);
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

        if (!changed)
            return;

        ApplyEffectiveStateToOwnedCard(item, previousState, refreshLiveVisuals: false);
        CardModificationState nextState = GetEffectiveState(item);
        LoadoutCardVisualRefreshKind refreshKind = GetVisualRefreshKind(previousState, nextState);
        RefreshLiveCardVisuals(item.Model, refreshKind);
        LoadoutRunContentChangeService.NotifyCardUpdated(item, refreshKind);
        CardModificationNetworkPatch.BroadcastTemporary(item, normalized);
    }

    public static void SavePermanent(ModelId cardId, CardModificationState state)
    {
        EnsureLoaded();
        EnsurePristineCanonical(cardId);
        CardModificationState previousPermanent = GetEffectivePermanentState(cardId);
        CardModificationState normalized = state.Clone();
        normalized.Normalize();
        if (!SavePermanentLocal(cardId, normalized))
            return;

        ApplyPermanentStateToLiveDeckCopies(cardId, previousPermanent, normalized);
        CardModificationNetworkPatch.BroadcastPermanentDelta(cardId, normalized);
        RaisePermanentCardDisplayChanged(cardId);
    }

    public static void CommitPermanent(LoadoutOwnedItem<CardModel> item, CardModificationState state)
    {
        EnsureLoaded();
        EnsurePristineCanonical(item.Model.Id);
        CardModificationState normalized = state.Clone();
        normalized.Normalize();
        CardModificationState previousSelectedState = GetEffectiveState(item);
        CardModificationState previousPermanent = GetEffectivePermanentState(item.Model.Id);
        bool temporaryChanged = ResetTemporaryLocal(item);
        bool permanentChanged = SavePermanentLocal(item.Model.Id, normalized);

        if (permanentChanged)
        {
            ApplyPermanentStateToLiveDeckCopies(
                item.Model.Id,
                previousPermanent,
                normalized,
                item.Model,
                previousSelectedState);
            CardModificationNetworkPatch.BroadcastPermanentDelta(item.Model.Id, normalized);
            RaisePermanentCardDisplayChanged(item.Model.Id);
        }
        else if (temporaryChanged)
        {
            ApplyEffectiveStateToOwnedCard(item, previousSelectedState, refreshLiveVisuals: false);
            CardModificationState nextState = GetEffectiveState(item);
            LoadoutCardVisualRefreshKind refreshKind = GetVisualRefreshKind(previousSelectedState, nextState);
            RefreshLiveCardVisuals(item.Model, refreshKind);
            LoadoutRunContentChangeService.NotifyCardUpdated(item, refreshKind);
        }

        if (temporaryChanged)
            CardModificationNetworkPatch.BroadcastTemporary(item, new CardModificationState());
    }

    public static bool StatesEquivalent(CardModificationState? left, CardModificationState? right)
    {
        CardModificationState normalizedLeft = left?.Clone() ?? new CardModificationState();
        CardModificationState normalizedRight = right?.Clone() ?? new CardModificationState();
        normalizedLeft.Normalize();
        normalizedRight.Normalize();
        return PermanentStatesEquivalent(normalizedLeft, normalizedRight);
    }

    public static void ResetTemporary(LoadoutOwnedItem<CardModel> item)
    {
        EnsureLoaded();
        CardModificationState previousState = GetEffectiveState(item);
        bool changed = ResetTemporaryLocal(item);

        if (!changed)
            return;

        ApplyEffectiveStateToOwnedCard(item, previousState, refreshLiveVisuals: false);
        CardModificationState nextState = GetEffectiveState(item);
        LoadoutCardVisualRefreshKind refreshKind = GetVisualRefreshKind(previousState, nextState);
        RefreshLiveCardVisuals(item.Model, refreshKind);
        LoadoutRunContentChangeService.NotifyCardUpdated(item, refreshKind);
        CardModificationNetworkPatch.BroadcastTemporary(item, new CardModificationState());
    }

    public static void ResetTemporaryToBasic(LoadoutOwnedItem<CardModel> item)
    {
        EnsureLoaded();
        CardModificationState permanentState = GetEffectivePermanentState(item.Model.Id);
        bool changed = ResetTemporaryLocal(item);
        ResetOwnedCardToBasicState(item, permanentState);

        if (changed)
            CardModificationNetworkPatch.BroadcastTemporary(item, new CardModificationState());

        LoadoutRunContentChangeService.NotifyCardUpdated(item, LoadoutCardVisualRefreshKind.Reload);
    }

    public static void ResetPermanent(ModelId cardId)
    {
        EnsureLoaded();
        CardModificationState previousPermanent = GetEffectivePermanentState(cardId);
        if (!ResetPermanentLocal(cardId))
            return;

        ApplyPermanentStateToLiveDeckCopies(cardId, previousPermanent, new CardModificationState());
        CardModificationNetworkPatch.BroadcastPermanentDelta(cardId, new CardModificationState());
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
            RebuildActivePermanentSnapshotLocked();
            SavePermanentState();
        }

        ApplyPermanentChangesToLiveDeckCopies(previousPermanentStates, changedPermanentKeys);
        CardModificationNetworkPatch.BroadcastPermanentSnapshot();
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
        CardModificationState previousPermanent = GetEffectivePermanentState(item.Model.Id);
        bool temporaryChanged = ResetTemporaryLocal(item);
        bool permanentChanged = ResetPermanentLocal(item.Model.Id);

        if (permanentChanged)
            ApplyPermanentStateToLiveDeckCopies(item.Model.Id, previousPermanent, new CardModificationState());
        else if (temporaryChanged)
            ResetOwnedCardToBasicState(item, new CardModificationState());

        if (permanentChanged)
            CardModificationNetworkPatch.BroadcastPermanentDelta(item.Model.Id, new CardModificationState());
        if (temporaryChanged)
            CardModificationNetworkPatch.BroadcastTemporary(item, new CardModificationState());

        if (permanentChanged)
            RaisePermanentCardDisplayChanged(item.Model.Id);
        if (!permanentChanged && temporaryChanged)
            LoadoutRunContentChangeService.NotifyCardUpdated(item, LoadoutCardVisualRefreshKind.Reload);
    }

    public static void ApplyPermanentToCard(CardModel? card)
    {
        if (card is null || SuppressAutomaticApply)
            return;

        // Constructor-only path: one lock-free dictionary lookup. The canonical
        // model receives native values once; future MutableClone calls inherit them.
        PermanentSnapshot snapshot = Volatile.Read(ref _activePermanentSnapshot);
        if (snapshot.Cards.Count == 0
            || !snapshot.Cards.TryGetValue(ToCardKey(card.Id), out CardModificationState? permanent))
        {
            return;
        }

        if (PermanentApplied.TryGetValue(card, out PermanentAppliedMarker? marker)
            && marker.Generation == snapshot.Generation)
        {
            return;
        }

        if (card.IsCanonical)
            CapturePristineCanonical(card);

        PrepareCardForState(card, permanent, includeAffliction: !card.IsCanonical);
        ApplyStateToCard(card, permanent, includeAffliction: !card.IsCanonical, allowCanonical: true);
        CardModificationFields.SetEffectiveOverrides(card, permanent);
        PermanentApplied.Remove(card);
        PermanentApplied.Add(card, new PermanentAppliedMarker(snapshot.Generation));
    }

    public static void ApplyLoadedStateToCard(CardModel? card)
    {
        if (card is null || card.IsCanonical)
            return;

        // Deserialization is the only place saved native fields can override the
        // canonical clone. Ordinary cards take two allocation-free sparse checks.
        PermanentSnapshot snapshot = Volatile.Read(ref _activePermanentSnapshot);
        bool hasTemporary = CardModificationFields.TryGetTemporary(
            card,
            out CardModificationState temporary);
        CardModificationState? permanent = null;
        bool hasPermanent = snapshot.Cards.Count > 0
                            && snapshot.Cards.TryGetValue(ToCardKey(card.Id), out permanent);
        if (!hasPermanent && !hasTemporary)
        {
            CardModificationFields.SetEffectiveOverrides(card, null);
            return;
        }

        CardModificationState next = hasPermanent
            ? permanent!.Clone()
            : new CardModificationState();
        if (hasTemporary)
            next.MergeFrom(temporary);
        next.Normalize();

        ResetCardToCanonicalBaseline(card, resetAttachments: true, resetUpgrade: true);
        ApplyStateToCard(card, next, includeAffliction: true);
        CardModificationFields.SetEffectiveOverrides(card, next);
    }

    public static void ApplyPermanentToExistingCatalog()
    {
        EnsureLoaded();
        try
        {
            foreach (CardModel canonical in ModelDb.AllCards)
                ApplyPermanentToCard(canonical);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: card catalog catch-up was deferred. {exception.Message}");
        }
    }

    public static void ApplyEffectiveStateToOwnedCard(
        LoadoutOwnedItem<CardModel> item,
        CardModificationState? previousState = null,
        bool refreshLiveVisuals = true)
    {
        CardModificationState state = ComposeEffectiveState(item.Model, includeTemporary: true);
        bool hasCardMutation = ApplyStateTransitionToCard(
            item.Model,
            state,
            previousState,
            includeAffliction: true);
        CardModificationFields.SetEffectiveOverrides(item.Model, state);

        if (refreshLiveVisuals
            && (hasCardMutation || HasVisualOverrides(state) || HasVisualOverrides(previousState)))
        {
            RefreshLiveCardVisuals(item.Model, GetVisualRefreshKind(previousState, state));
        }
    }

    public static void ApplyPreviewStateToOwnedCard(
        LoadoutOwnedItem<CardModel> item,
        CardModificationState state,
        CardModificationState? previousState = null)
    {
        CardModificationState normalized = state.Clone();
        normalized.Normalize();
        bool hasCardMutation = ApplyStateTransitionToCard(
            item.Model,
            normalized,
            previousState,
            includeAffliction: true);
        CardModificationFields.SetEffectiveOverrides(item.Model, normalized);

        if (hasCardMutation || HasVisualOverrides(normalized) || HasVisualOverrides(previousState))
            RefreshLiveCardVisuals(item.Model, GetVisualRefreshKind(previousState, normalized));
    }

    public static void ResetOwnedCardToBasicState(LoadoutOwnedItem<CardModel> item, CardModificationState? state)
    {
        CardModificationState normalized = state?.Clone() ?? new CardModificationState();
        normalized.Normalize();

        ResetCardToCanonicalBaseline(item.Model, resetAttachments: true, resetUpgrade: true);
        if (!normalized.IsEmpty)
            ApplyStateToCard(item.Model, normalized);
        CardModificationFields.SetEffectiveOverrides(item.Model, normalized);

        RefreshLiveCardVisuals(item.Model, LoadoutCardVisualRefreshKind.Reload);
    }

    public static void ReapplyEffectiveStateAfterUpgrade(IEnumerable<CardModel>? cards)
    {
        if (cards is null || IsCombatEnding())
            return;

        EnsureLoaded();
        HashSet<CardModel> uniqueCards = new(ReferenceEqualityComparer.Instance);
        foreach (CardModel? card in cards)
        {
            if (card is not null && !card.IsCanonical)
                uniqueCards.Add(card);
        }

        bool refreshVisualsHere = _targetedUpgradeDepth == 0;
        foreach (CardModel card in uniqueCards)
        {
            CardModificationState state = GetEffectiveStateForCardReadOnly(card);
            if (!HasCardMutations(state) && !HasVisualOverrides(state))
                continue;

            if (HasCardMutations(state))
            {
                // CardCmd.Upgrade has already performed the authoritative native
                // upgrade on this exact card. Its DynamicVars now contain the correct
                // result, including Infinite Upgrade's level-scaled addends. Replaying
                // a canonical baseline merely because Infinite Upgrade is present
                // performs the upgrade a second time and can collapse +2/+3 cards back
                // to their previous values.
                bool requiresAttachmentReplay = state.Enchantment is not null
                                                || state.Affliction is not null;
                Dictionary<string, decimal>? upgradedDynamicValues = null;
                if (requiresAttachmentReplay)
                {
                    upgradedDynamicValues = card.DynamicVars.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value.BaseValue,
                        StringComparer.Ordinal);
                    PrepareCardForState(card, state, includeAffliction: true);
                }

                // Editor dynamic-variable overrides are absolute values. Do not write
                // those stale values back over the result of the native upgrade. All
                // non-dynamic modifications (rarity, type, keywords, costs, attachments)
                // are still restored deterministically on every peer.
                ApplyStateToCard(card, state, includeAffliction: true, applyDynamicVars: false);

                // Attachment reconstruction needs a canonical replay. Restore the
                // post-native-upgrade values captured before that replay so enchantment
                // maintenance cannot erase Infinite Upgrade progress.
                if (upgradedDynamicValues is not null)
                    RestoreDynamicVarValues(card, upgradedDynamicValues);
            }

            CardModificationFields.SetEffectiveOverrides(card, state);

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

                CardModificationState committedPreviousState = GetEffectiveState(itemToCommit);
                bool committedTemporaryChanged = ResetTemporaryLocal(itemToCommit);

                if (authoritativeRemote)
                {
                    ApplyHostPermanentDelta(
                        modelId.ToString(),
                        permanentState,
                        itemToCommit.Model,
                        committedPreviousState);
                    return;
                }

                if (!IsPermanentModificationAuthority())
                    return;

                CardModificationState previousPermanent = GetEffectivePermanentState(modelId);
                if (SavePermanentLocal(modelId, permanentState))
                {
                    ApplyPermanentStateToLiveDeckCopies(
                        modelId,
                        previousPermanent,
                        permanentState,
                        itemToCommit.Model,
                        committedPreviousState);
                    RaisePermanentCardDisplayChanged(modelId);
                }
                else if (committedTemporaryChanged)
                {
                    // The permanent value may be unchanged while clearing the selected
                    // card's attached override still changes its effective state.
                    ApplyEffectiveStateToOwnedCard(itemToCommit, committedPreviousState, refreshLiveVisuals: false);
                    CardModificationState committedState = GetEffectiveState(itemToCommit);
                    LoadoutCardVisualRefreshKind committedRefresh = GetVisualRefreshKind(
                        committedPreviousState,
                        committedState);
                    RefreshLiveCardVisuals(itemToCommit.Model, committedRefresh);
                    LoadoutRunContentChangeService.NotifyCardUpdated(itemToCommit, committedRefresh);
                }
                break;

            case CardModificationOperation.ResetPermanentToBasic:
                if (TryResolveOwnedDeckCard(target, ownedItemIndex, expectedModelId, actionPlayer) is not { } resetPermanentItem)
                    return;

                CardModificationState resetPermanentPreviousState = GetEffectiveState(resetPermanentItem);
                bool resetPermanentTemporaryChanged = ResetTemporaryLocal(resetPermanentItem);
                if (authoritativeRemote)
                {
                    ApplyHostPermanentDelta(
                        modelId.ToString(),
                        new CardModificationState(),
                        resetPermanentItem.Model,
                        resetPermanentPreviousState);
                    return;
                }

                if (IsPermanentModificationAuthority())
                {
                    CardModificationState resetPreviousPermanent = GetEffectivePermanentState(modelId);
                    if (ResetPermanentLocal(modelId))
                    {
                        ApplyPermanentStateToLiveDeckCopies(
                            modelId,
                            resetPreviousPermanent,
                            new CardModificationState(),
                            resetPermanentItem.Model,
                            resetPermanentPreviousState);
                        RaisePermanentCardDisplayChanged(modelId);
                    }
                    else if (resetPermanentTemporaryChanged)
                    {
                        ApplyEffectiveStateToOwnedCard(resetPermanentItem, resetPermanentPreviousState, refreshLiveVisuals: false);
                        CardModificationState resetState = GetEffectiveState(resetPermanentItem);
                        LoadoutCardVisualRefreshKind resetRefresh = GetVisualRefreshKind(
                            resetPermanentPreviousState,
                            resetState);
                        RefreshLiveCardVisuals(resetPermanentItem.Model, resetRefresh);
                        LoadoutRunContentChangeService.NotifyCardUpdated(resetPermanentItem, resetRefresh);
                    }
                }
                else if (resetPermanentTemporaryChanged)
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

    public static void ApplyStateToCard(
        CardModel? card,
        CardModificationState? state,
        bool includeAffliction = true,
        bool applyDynamicVars = true,
        bool allowCanonical = false)
    {
        if (card is null || state is null || state.IsEmpty)
            return;

        if (card.IsCanonical && !allowCanonical)
        {
            GD.PushWarning($"CardModification: refused unexpected canonical mutation for '{card.Id}'.");
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

            if (applyDynamicVars)
            {
                foreach ((string name, decimal value) in state.DynamicVars)
                {
                    if (card.DynamicVars.TryGetValue(name, out var dynamicVar))
                        dynamicVar.BaseValue = value;
                }
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

    internal static CardModel CloneCanonicalCardWithoutPermanent(CardModel canonical)
    {
        bool previous = SuppressAutomaticApply;
        SuppressAutomaticApply = true;
        try
        {
            return canonical.ToMutable();
        }
        finally
        {
            SuppressAutomaticApply = previous;
        }
    }

    private static void RestoreDynamicVarValues(
        CardModel card,
        IReadOnlyDictionary<string, decimal> values)
    {
        foreach ((string name, decimal value) in values)
        {
            if (card.DynamicVars.TryGetValue(name, out var dynamicVar))
                dynamicVar.BaseValue = value;
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
            CardModificationFields.SetEffectiveOverrides(preview, normalized);
            return preview;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: failed to create card modification preview for '{card.Id}'. {exception.Message}");
            return card;
        }
    }

    public static void CopyRuntimeStateToClone(CardModel source, CardModel clone)
    {
        if (source is null || clone is null || clone.IsCanonical)
            return;

        CardModificationFields.CopyRuntimeToClone(source, clone);
    }

    public static void CarryEffectiveStateToClone(CardModel source, CardModel clone)
    {
        CopyRuntimeStateToClone(source, clone);
    }

    public static CardModel GetEffectivePermanentCardForDisplay(CardModel card)
    {
        // Canonical cards are permanently modified at constructor/catalog-delta time.
        // Returning the original model avoids display clones, cache churn and state reads.
        return card;
    }


    private static bool SaveTemporaryLocal(LoadoutOwnedItem<CardModel> item, CardModificationState normalized)
    {
        normalized.Normalize();
        return CardModificationFields.Set(item.Model, normalized);
    }

    private static bool ResetTemporaryLocal(LoadoutOwnedItem<CardModel> item)
    {
        return CardModificationFields.Clear(item.Model);
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
                RebuildActivePermanentSnapshotLocked();
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
                RebuildActivePermanentSnapshotLocked();
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

        if (targetPlayer is null
            || ownedItemIndex < 0
            || ownedItemIndex >= targetPlayer.Deck.Cards.Count)
        {
            return null;
        }

        CardModel card = targetPlayer.Deck.Cards[ownedItemIndex];
        if (!ModelIdMatches(card, expectedModelId) || card.Pile?.Type != PileType.Deck)
            return null;

        return new LoadoutOwnedItem<CardModel>(targetPlayer, ownedItemIndex, card);
    }

    private static void ApplyPermanentStateToLiveDeckCopies(
        ModelId cardId,
        CardModificationState previousPermanent,
        CardModificationState nextPermanent,
        CardModel? selectedCard = null,
        CardModificationState? selectedPreviousState = null)
    {
        Dictionary<string, PermanentStateTransition> transitions = new(StringComparer.Ordinal)
        {
            [ToCardKey(cardId)] = new PermanentStateTransition(
                cardId,
                previousPermanent.Clone(),
                nextPermanent.Clone())
        };
        ApplyPermanentTransitionsToLiveDeckCopies(
            transitions,
            selectedCard,
            selectedPreviousState);
    }

    private static void ApplyPermanentChangesToLiveDeckCopies(
        IReadOnlyDictionary<string, CardModificationState> previousPermanentStates,
        IReadOnlyCollection<string> changedPermanentKeys)
    {
        Dictionary<string, PermanentStateTransition> transitions = new(StringComparer.Ordinal);
        foreach (string key in changedPermanentKeys)
        {
            if (!TryResolveModel(key, ModelDb.AllCards, out CardModel? canonical) || canonical is null)
                continue;

            CardModificationState previousPermanent = previousPermanentStates.TryGetValue(
                key,
                out CardModificationState? previous)
                ? previous.Clone()
                : new CardModificationState();
            transitions[key] = new PermanentStateTransition(
                canonical.Id,
                previousPermanent,
                GetEffectivePermanentState(canonical.Id));
        }

        ApplyPermanentTransitionsToLiveDeckCopies(transitions);
    }

    private static void ApplyPermanentChangesToCanonicalCards(
        IReadOnlyDictionary<string, CardModificationState> previousPermanentStates,
        IReadOnlyCollection<string> changedPermanentKeys)
    {
        foreach (string key in changedPermanentKeys)
        {
            if (!TryResolveModel(key, ModelDb.AllCards, out CardModel? canonical) || canonical is null)
                continue;

            CardModificationState previous = previousPermanentStates.TryGetValue(
                key,
                out CardModificationState? stored)
                ? stored.Clone()
                : new CardModificationState();
            RebuildCanonicalForPermanentTransition(new PermanentStateTransition(
                canonical.Id,
                previous,
                GetEffectivePermanentState(canonical.Id)));
        }
    }

    private static void ApplyPermanentTransitionsToLiveDeckCopies(
        IReadOnlyDictionary<string, PermanentStateTransition> transitions,
        CardModel? selectedCard = null,
        CardModificationState? selectedPreviousState = null)
    {
        if (transitions.Count == 0)
            return;

        try
        {
            List<LoadoutChangedCard> changedCards = [];
            HashSet<ulong> changedPlayers = [];
            Dictionary<Player, Dictionary<CardModel, int>> deckIndicesByOwner = new();

            foreach (PermanentStateTransition transition in transitions.Values)
            {
                RebuildCanonicalForPermanentTransition(transition);

                HashSet<CardModel> matchingCards = new(ReferenceEqualityComparer.Instance);
                if (selectedCard is not null && selectedCard.Id.Equals(transition.CardId))
                    matchingCards.Add(selectedCard);

                // No live registry is maintained. A permanent edit is rare, so that
                // event performs one exact-model scan of deck and current combat piles,
                // updates existing copies once, and retains no tracking structure.
                try
                {
                    RunState? runState = RunManager.Instance.IsInProgress
                        ? RunManager.Instance.DebugOnlyGetState()
                        : null;
                    if (runState is not null)
                    {
                        foreach (Player player in runState.Players)
                        {
                            foreach (CardModel card in player.Deck.Cards)
                            {
                                if (card.Id.Equals(transition.CardId))
                                    matchingCards.Add(card);
                            }

                            if (player.PlayerCombatState is { } combatState)
                            {
                                foreach (CardModel card in combatState.AllCards)
                                {
                                    if (card.Id.Equals(transition.CardId))
                                        matchingCards.Add(card);
                                }
                            }
                        }
                    }
                }
                catch (Exception exception)
                {
                    GD.PushWarning($"CardModification: failed to enumerate existing copies for '{transition.CardId}'. {exception.Message}");
                }

                foreach (CardModel card in matchingCards)
                {
                    CardModificationState attached = CardModificationFields.GetReadOnly(card);
                    CardModificationState previousState;
                    if (selectedCard is not null
                        && selectedPreviousState is not null
                        && ReferenceEquals(card, selectedCard))
                    {
                        previousState = selectedPreviousState.Clone();
                    }
                    else
                    {
                        previousState = transition.Previous.Clone();
                        previousState.MergeFrom(attached);
                    }

                    CardModificationState nextState = transition.Next.Clone();
                    nextState.MergeFrom(attached);
                    bool hasMutation = ApplyStateTransitionToCard(
                        card,
                        nextState,
                        previousState,
                        includeAffliction: true);
                    CardModificationFields.SetEffectiveOverrides(card, nextState);

                    LoadoutCardVisualRefreshKind refreshKind = GetVisualRefreshKind(previousState, nextState);
                    if (hasMutation || HasVisualOverrides(previousState) || HasVisualOverrides(nextState))
                        RefreshLiveCardVisuals(card, refreshKind);

                    if (!TryGetDeckLocationCached(
                            card,
                            deckIndicesByOwner,
                            out Player? owner,
                            out int index)
                        || owner is null)
                    {
                        continue;
                    }

                    changedPlayers.Add(owner.NetId);
                    changedCards.Add(new LoadoutChangedCard(owner.NetId, index, transition.CardId, refreshKind));
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
            GD.PushWarning($"CardModification: failed to apply permanent card refresh. {exception.Message}");
        }
    }

    private static void ApplySelectedCardAfterAttachedStateChange(
        CardModel? selectedCard,
        CardModificationState? selectedPreviousState)
    {
        if (selectedCard is null
            || selectedPreviousState is null
            || !TryGetDeckLocation(selectedCard, out Player? owner, out int index)
            || owner is null)
        {
            return;
        }

        LoadoutOwnedItem<CardModel> item = new(owner, index, selectedCard);
        CardModificationState nextState = GetEffectiveState(item);
        if (StatesEquivalent(selectedPreviousState, nextState))
            return;

        ApplyEffectiveStateToOwnedCard(item, selectedPreviousState, refreshLiveVisuals: false);
        LoadoutCardVisualRefreshKind refreshKind = GetVisualRefreshKind(selectedPreviousState, nextState);
        RefreshLiveCardVisuals(selectedCard, refreshKind);
        LoadoutRunContentChangeService.NotifyCardUpdated(item, refreshKind);
    }

    private static bool TryGetDeckLocationCached(
        CardModel card,
        IDictionary<Player, Dictionary<CardModel, int>> indicesByOwner,
        out Player? owner,
        out int index)
    {
        owner = card.Owner;
        index = -1;
        if (owner is null || card.Pile?.Type != PileType.Deck)
            return false;

        if (!indicesByOwner.TryGetValue(owner, out Dictionary<CardModel, int>? indices))
        {
            indices = new Dictionary<CardModel, int>(ReferenceEqualityComparer.Instance);
            IReadOnlyList<CardModel> deck = owner.Deck.Cards;
            for (int deckIndex = 0; deckIndex < deck.Count; deckIndex++)
                indices[deck[deckIndex]] = deckIndex;
            indicesByOwner[owner] = indices;
        }

        return indices.TryGetValue(card, out index);
    }

    private static bool TryGetDeckLocation(CardModel card, out Player? owner, out int index)
    {
        owner = card.Owner;
        index = -1;
        if (owner is null || card.Pile?.Type != PileType.Deck)
            return false;

        IReadOnlyList<CardModel> deck = owner.Deck.Cards;
        for (int i = 0; i < deck.Count; i++)
        {
            if (!ReferenceEquals(deck[i], card))
                continue;

            index = i;
            return true;
        }

        return false;
    }

    private static bool TryResolveLiveDeckCard(
        ulong ownerNetId,
        int index,
        string cardId,
        out LoadoutOwnedItem<CardModel> item)
    {
        item = default;
        if (!RunManager.Instance.IsInProgress)
            return false;

        Player? player = RunManager.Instance.DebugOnlyGetState()?.GetPlayer(ownerNetId);
        if (player is null || index < 0 || index >= player.Deck.Cards.Count)
            return false;

        CardModel card = player.Deck.Cards[index];
        if (!string.Equals(card.Id.ToString(), cardId, StringComparison.Ordinal)
            && !string.Equals(card.Id.Entry, cardId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        item = new LoadoutOwnedItem<CardModel>(player, index, card);
        return true;
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
            RebuildActivePermanentSnapshotLocked();
        }

        if (changedPermanentKeys.Count == 0)
            return;

        if (applyMode == HostPermanentSnapshotApplyMode.LiveDecks)
            ApplyPermanentChangesToLiveDeckCopies(previousPermanentStates, changedPermanentKeys);
        else
            ApplyPermanentChangesToCanonicalCards(previousPermanentStates, changedPermanentKeys);

        RaisePermanentCardDisplayChanged(ResolvePermanentCardIds(changedPermanentKeys));
        if (applyMode == HostPermanentSnapshotApplyMode.LiveDecks)
            RaiseStateChanged();
    }

    public static void ApplyHostPermanentDelta(
        string cardId,
        CardModificationState? state,
        CardModel? selectedCard = null,
        CardModificationState? selectedPreviousState = null)
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
        CardModificationState previousPermanent;
        bool changed;
        lock (SyncRoot)
        {
            previousPermanent = GetEffectivePermanentStateLocked(card.Id);
            if (!_hasHostPermanentOverlay)
                _hostPermanentOverlay = CloneStateDictionary(_permanent.Cards);
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
                RebuildActivePermanentSnapshotLocked();
        }

        if (!changed)
        {
            ApplySelectedCardAfterAttachedStateChange(selectedCard, selectedPreviousState);
            return;
        }

        ApplyPermanentStateToLiveDeckCopies(
            card.Id,
            previousPermanent,
            normalized,
            selectedCard,
            selectedPreviousState);
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
            RebuildActivePermanentSnapshotLocked();
        }

        if (changedPermanentKeys.Count == 0)
            return;

        ApplyPermanentChangesToLiveDeckCopies(previousPermanentStates, changedPermanentKeys);
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
                RebuildActivePermanentSnapshotLocked();
                SavePermanentState();
            }
        }

        if (changedPermanentKeys.Count == 0)
            return [];

        IReadOnlyList<ModelId> changedCardIds = ResolvePermanentCardIds(changedPermanentKeys);
        if (!hasHostOverlay)
        {
            ApplyPermanentChangesToLiveDeckCopies(previousPermanentStates, changedPermanentKeys);
            RaisePermanentCardDisplayChanged(changedCardIds);
            RaiseStateChanged();
        }

        return changedCardIds;
    }

    public static void ApplyRemoteTemporaryState(ulong ownerNetId, int index, string cardId, CardModificationState? state)
    {
        EnsureLoaded();
        if (!TryResolveLiveDeckCard(ownerNetId, index, cardId, out LoadoutOwnedItem<CardModel> item))
            return;

        CardModificationState previousState = GetEffectiveState(item);
        CardModificationState normalized = state?.Clone() ?? new CardModificationState();
        normalized.Normalize();
        bool changed = CardModificationFields.Set(item.Model, normalized);
        if (!changed)
            return;

        ApplyEffectiveStateToOwnedCard(item, previousState, refreshLiveVisuals: false);
        CardModificationState nextState = GetEffectiveState(item);
        LoadoutCardVisualRefreshKind refreshKind = GetVisualRefreshKind(previousState, nextState);
        RefreshLiveCardVisuals(item.Model, refreshKind);
        LoadoutRunContentChangeService.NotifyCardUpdated(item, refreshKind);
    }

    public static void ClearTemporaryStatesForPlayer(ulong ownerNetId)
    {
        if (!RunManager.Instance.IsInProgress)
            return;

        Player? player = RunManager.Instance.DebugOnlyGetState()?.GetPlayer(ownerNetId);
        if (player is null)
            return;

        bool changed = false;
        IReadOnlyList<CardModel> cards = player.Deck.Cards;
        for (int index = 0; index < cards.Count; index++)
        {
            CardModel card = cards[index];
            if (!CardModificationFields.TryGetSnapshot(card, out _))
                continue;

            CardModificationState previous = GetEffectiveStateForCard(card);
            if (!CardModificationFields.Clear(card))
                continue;

            changed = true;
            ApplyEffectiveStateToOwnedCard(
                new LoadoutOwnedItem<CardModel>(player, index, card),
                previous,
                refreshLiveVisuals: false);
        }

        if (changed)
            RaiseStateChanged();
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

    private static void EnsurePristineCanonical(ModelId cardId)
    {
        lock (PristineGate)
        {
            if (PristineCanonicalCards.ContainsKey(cardId))
                return;
        }

        CardModel? canonical = LoadoutModelRegistry.ResolveCard(cardId);
        if (canonical is not null)
            CapturePristineCanonical(canonical);
    }

    private static void CapturePristineCanonical(CardModel canonical)
    {
        if (!canonical.IsCanonical)
            return;

        lock (PristineGate)
        {
            if (PristineCanonicalCards.ContainsKey(canonical.Id))
                return;

            try
            {
                CardModel pristine = CloneCanonicalCardWithoutPermanent(canonical);
                CardModificationFields.Clear(pristine);
                CardModificationFields.SetEffectiveOverrides(pristine, null);
                PristineCanonicalCards[canonical.Id] = pristine;
            }
            catch (Exception exception)
            {
                GD.PushWarning($"CardModification: failed to capture pristine '{canonical.Id}'. {exception.Message}");
            }
        }
    }

    private static CardModel? GetPristineCanonical(ModelId cardId)
    {
        lock (PristineGate)
            return PristineCanonicalCards.GetValueOrDefault(cardId);
    }

    private static void RebuildCanonicalForPermanentTransition(PermanentStateTransition transition)
    {
        CardModel? canonical = LoadoutModelRegistry.ResolveCard(transition.CardId);
        if (canonical is null)
            return;

        EnsurePristineCanonical(transition.CardId);
        ResetCardToCanonicalBaseline(canonical, resetAttachments: true, resetUpgrade: true);
        if (!transition.Next.IsEmpty)
            ApplyStateToCard(canonical, transition.Next, includeAffliction: false, allowCanonical: true);
        CardModificationFields.SetEffectiveOverrides(canonical, transition.Next);

        PermanentApplied.Remove(canonical);
        if (!transition.Next.IsEmpty)
        {
            PermanentSnapshot snapshot = Volatile.Read(ref _activePermanentSnapshot);
            PermanentApplied.Add(canonical, new PermanentAppliedMarker(snapshot.Generation));
        }
    }

    private static void ResetCardToCanonicalBaseline(
        CardModel card,
        bool resetAttachments,
        bool resetUpgrade = false,
        bool? infiniteUpgradeOverride = null)
    {
        CardModel? canonical = GetPristineCanonical(card.Id)
                               ?? LoadoutModelRegistry.ResolveCard(card.Id);
        if (canonical is null)
            return;

        CardModel baseline = resetUpgrade
            ? CloneCanonicalCardWithoutPermanent(canonical)
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
        CardModel baseline = CloneCanonicalCardWithoutPermanent(canonical);
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

    private static void OnProfileIdChanged(int _)
    {
        FlushPendingSaves();

        Dictionary<string, CardModificationState> previous;
        lock (SyncRoot)
        {
            previous = _hasHostPermanentOverlay
                ? CloneStateDictionary(_hostPermanentOverlay)
                : CloneStateDictionary(_permanent.Cards);
            Volatile.Write(ref _permanentLoaded, false);
            _permanent = new PermanentSaveData();
            _hostPermanentOverlay.Clear();
            _hasHostPermanentOverlay = false;
            RebuildActivePermanentSnapshotLocked();
        }

        EnsureLoaded();

        Dictionary<string, CardModificationState> current;
        lock (SyncRoot)
            current = CloneStateDictionary(_permanent.Cards);

        List<string> changedKeys = GetChangedPermanentKeys(previous, current);
        if (changedKeys.Count == 0)
            return;

        ApplyPermanentChangesToLiveDeckCopies(previous, changedKeys);
        RaisePermanentCardDisplayChanged(ResolvePermanentCardIds(changedKeys));
        RaiseStateChanged();
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
            RebuildActivePermanentSnapshotLocked();
            Volatile.Write(ref _permanentLoaded, true);

            if (loaded.Loaded && loaded.Value.SchemaVersion != CurrentSchemaVersion)
                SavePermanentState();
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
        }

        // File IO deliberately happens outside SyncRoot. Constructor and display
        // patches can continue reading the immutable snapshot while JSON is written.
        if (permanentSnapshot.HasValue)
            SaveUtility.SaveProfileJson(PermanentPath, permanentSnapshot.Value);
    }

    private static PermanentSaveData NormalizePermanent(PermanentSaveData save)
    {
        save.SchemaVersion = CurrentSchemaVersion;
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

    private static void RebuildActivePermanentSnapshotLocked()
    {
        Dictionary<string, CardModificationState> source = _hasHostPermanentOverlay
            ? _hostPermanentOverlay
            : _permanent.Cards;
        Dictionary<string, CardModificationState> snapshot = CloneStateDictionary(source);
        int generation = _activePermanentSnapshot.Generation + 1;
        Volatile.Write(ref _activePermanentSnapshot, new PermanentSnapshot(generation, snapshot));
    }

    private sealed record PermanentSnapshot(
        int Generation,
        IReadOnlyDictionary<string, CardModificationState> Cards);

    private sealed class PermanentAppliedMarker(int generation)
    {
        public int Generation { get; } = generation;
    }

    private readonly record struct PermanentStateTransition(
        ModelId CardId,
        CardModificationState Previous,
        CardModificationState Next);

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
