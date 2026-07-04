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
using Loadout.Services.Saving;
using Loadout.Services.Targets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

public enum CardModificationOperation
{
    None,
    SaveTemporary,
    ResetTemporary,
    ApplyPermanent
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
    private static bool _registered;
    private static bool _permanentLoaded;
    private static bool _runLoaded;
    private static long? _loadedRunStartTime;
    private static int _displayRevision;

    [ThreadStatic]
    private static Stack<CardModel>? _locStringContext;

    public static event Action? StateChanged;

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
        EnsureLoaded();
        lock (SyncRoot)
        {
            CardModificationState effective = IsLocalCatalogDisplayCard(card)
                ? GetCatalogPermanentStateLocked(card.Id)
                : GetEffectivePermanentStateLocked(card.Id);

            if (!IsLocalCatalogDisplayCard(card)
                && TryGetCopyKey(card, out string? copyKey)
                && copyKey is not null
                && _run.Cards.TryGetValue(copyKey, out CardModificationState? temporary))
            {
                effective.MergeFrom(temporary);
            }

            return effective;
        }
    }

    public static bool TryGetCustomTitle(CardModel card, out string title)
    {
        CardModificationState state = GetEffectiveStateForCard(card);
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
        CardModificationState state = GetEffectiveStateForCard(card);

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
        CardModificationState state = GetEffectiveStateForCard(card);
        string? overridePath = beta ? state.BetaPortraitPath : state.PortraitPath;
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            path = overridePath!;
            return true;
        }

        if (string.IsNullOrWhiteSpace(state.PoolId))
            return false;

        CardModel? canonical = ModelDb.AllCards.FirstOrDefault(candidate => candidate.Id.Equals(card.Id));
        if (canonical is null || ReferenceEquals(canonical, card))
        {
            path = currentPath;
            return true;
        }

        path = beta ? canonical.BetaPortraitPath : canonical.PortraitPath;
        return true;
    }

    public static bool TryGetCustomDescription(CardModel card, out string description)
    {
        CardModificationState state = GetEffectiveStateForCard(card);
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

    public static void SaveTemporary(LoadoutOwnedItem<CardModel> item, CardModificationState state)
    {
        EnsureLoaded();
        CardModificationState normalized = state.Clone();
        normalized.Normalize();
        bool changed = SaveTemporaryLocal(item, normalized);

        if (changed)
        {
            CardModificationMultiplayerSyncService.BroadcastTemporary(item, normalized);
            RaiseStateChanged();
        }
    }

    public static void SavePermanent(ModelId cardId, CardModificationState state)
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            string key = ToCardKey(cardId);
            CardModificationState normalized = state.Clone();
            normalized.Normalize();
            if (normalized.IsEmpty)
                _permanent.Cards.Remove(key);
            else
                _permanent.Cards[key] = normalized;

            InvalidateDisplayCacheLocked();
            SavePermanentState();
        }

        CardModificationMultiplayerSyncService.BroadcastPermanentSnapshot();
        RaiseStateChanged();
    }

    public static void ResetTemporary(LoadoutOwnedItem<CardModel> item)
    {
        EnsureLoaded();
        bool changed = ResetTemporaryLocal(item);

        if (changed)
        {
            CardModificationMultiplayerSyncService.BroadcastTemporary(item, new CardModificationState());
            RaiseStateChanged();
        }
    }

    public static void ResetPermanent(ModelId cardId)
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            if (_permanent.Cards.Remove(ToCardKey(cardId)))
            {
                InvalidateDisplayCacheLocked();
                SavePermanentState();
            }
        }

        CardModificationMultiplayerSyncService.BroadcastPermanentSnapshot();
        RaiseStateChanged();
    }

    public static void ApplyPermanentToCard(CardModel? card)
    {
        if (card is null || card.IsCanonical)
            return;

        EnsureLoaded();
        CardModificationState state;
        lock (SyncRoot)
            state = GetEffectivePermanentStateLocked(card.Id);

        if (!HasCardMutations(state))
            return;

        PrepareCardForState(card, state, includeAffliction: true);
        ApplyStateToCard(card, state);
    }

    public static void ApplyEffectiveStateToOwnedCard(LoadoutOwnedItem<CardModel> item, CardModificationState? previousState = null)
    {
        CardModificationState state = GetEffectiveState(item);
        bool hasCardMutation = HasCardMutations(state) || HasCardMutations(previousState);
        if (hasCardMutation)
        {
            PrepareCardForState(item.Model, state, includeAffliction: true, previousState);
            ApplyStateToCard(item.Model, state);
        }

        if (hasCardMutation || HasVisualOverrides(state) || HasVisualOverrides(previousState))
            RefreshLiveCardVisuals(item.Model);
    }

    public static void ReapplyEffectiveStateAfterUpgrade(IEnumerable<CardModel>? cards)
    {
        if (cards is null)
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

        bool changed = false;
        foreach (CardModel card in uniqueCards)
        {
            CardModificationState state = GetEffectiveStateForCard(card);
            bool hasCardMutation = HasCardMutations(state);
            bool hasVisualOverride = HasVisualOverrides(state);
            if (!hasCardMutation && !hasVisualOverride)
                continue;

            if (hasCardMutation)
            {
                PrepareCardForState(card, state, includeAffliction: true);
                ApplyStateToCard(card, state);
            }

            RefreshLiveCardVisuals(card);
            changed = true;
        }

        if (changed)
            RaiseStateChanged();
    }

    public static void ApplySynchronizedOperation(
        CardModificationOperation operation,
        ModelId modelId,
        LoadoutTargetSelection target,
        int ownedItemIndex,
        ModelId expectedModelId,
        CardModificationState? state,
        Player actionPlayer)
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
                SaveTemporaryLocal(saveItem, normalized);
                ApplyEffectiveStateToOwnedCard(saveItem, savePreviousState);
                RaiseStateChanged();
                break;

            case CardModificationOperation.ResetTemporary:
                if (TryResolveOwnedDeckCard(target, ownedItemIndex, expectedModelId, actionPlayer) is not { } resetItem)
                    return;

                CardModificationState resetPreviousState = GetEffectiveState(resetItem);
                ResetTemporaryLocal(resetItem);
                ApplyEffectiveStateToOwnedCard(resetItem, resetPreviousState);
                RaiseStateChanged();
                break;

            case CardModificationOperation.ApplyPermanent:
                CardModificationState permanentState = state?.Clone() ?? new CardModificationState();
                permanentState.Normalize();
                ApplyPermanentStateToLiveCardsFromAction(modelId, permanentState);
                RaiseStateChanged();
                break;
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
            PrepareCardForState(preview, state, includeAffliction: true);
            ApplyStateToCard(preview, state);
            MarkLocalCatalogDisplayCard(preview);
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
        ApplySavedRunStateToPlayerDeck(player, null);
    }

    private static void ApplySavedRunStateToPlayerDeck(
        Player? player,
        IReadOnlyDictionary<string, CardModificationState>? previousPermanentStates)
    {
        if (player is null)
            return;

        EnsureLoaded();
        IReadOnlyList<CardModel> cards = player.Deck.Cards;
        for (int index = 0; index < cards.Count; index++)
        {
            CardModel card = cards[index];
            CardModificationState state;
            CardModificationState? previousState = null;
            lock (SyncRoot)
            {
                state = GetEffectivePermanentStateLocked(card.Id);
                if (_run.Cards.TryGetValue(GetCopyKey(player.NetId, index, card.Id), out CardModificationState? temporary))
                    state.MergeFrom(temporary);

                if (previousPermanentStates is not null
                    && previousPermanentStates.TryGetValue(ToCardKey(card.Id), out CardModificationState? previousPermanent))
                {
                    previousState = previousPermanent.Clone();
                    if (_run.Cards.TryGetValue(GetCopyKey(player.NetId, index, card.Id), out CardModificationState? temporaryForPrevious))
                        previousState.MergeFrom(temporaryForPrevious);
                }
            }

            bool hasCardMutation = HasCardMutations(state) || HasCardMutations(previousState);
            if (hasCardMutation)
            {
                PrepareCardForState(card, state, includeAffliction: true, previousState);
                ApplyStateToCard(card, state);
            }

            if (hasCardMutation || HasVisualOverrides(state) || HasVisualOverrides(previousState))
                RefreshLiveCardVisuals(card);
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
                _run.Cards[key] = normalized.Clone();
                changed = true;
            }

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

    private static LoadoutOwnedItem<CardModel>? TryResolveOwnedDeckCard(
        LoadoutTargetSelection target,
        int ownedItemIndex,
        ModelId expectedModelId,
        Player actionPlayer)
    {
        Player? targetPlayer = target.Scope == LoadoutTargetScope.Player && target.PlayerNetId.HasValue
            ? actionPlayer.RunState.GetPlayer(target.PlayerNetId.Value)
            : actionPlayer;

        if (targetPlayer is null || ownedItemIndex < 0 || ownedItemIndex >= targetPlayer.Deck.Cards.Count)
            return null;

        CardModel card = targetPlayer.Deck.Cards[ownedItemIndex];
        return ModelIdMatches(card, expectedModelId) && card.Pile?.Type == PileType.Deck
            ? new LoadoutOwnedItem<CardModel>(targetPlayer, ownedItemIndex, card)
            : null;
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

                    bool hasCardMutation = HasCardMutations(effective) || HasCardMutations(previousState);
                    if (hasCardMutation)
                    {
                        PrepareCardForState(card, effective, includeAffliction: true, previousState);
                        ApplyStateToCard(card, effective);
                    }

                    if (hasCardMutation || HasVisualOverrides(effective) || HasVisualOverrides(previousState))
                        RefreshLiveCardVisuals(card);
                }
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

    public static void ApplyHostPermanentSnapshotJson(string? snapshotJson)
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
        lock (SyncRoot)
        {
            previousPermanentStates = _hasHostPermanentOverlay
                ? CloneStateDictionary(_hostPermanentOverlay)
                : CloneStateDictionary(_permanent.Cards);
            _hostPermanentOverlay = overlay;
            _hasHostPermanentOverlay = true;
            InvalidateDisplayCacheLocked();
        }

        ApplySavedRunStateToLiveDecks(previousPermanentStates);
        RaiseStateChanged();
    }

    public static void ClearHostPermanentOverlay()
    {
        lock (SyncRoot)
        {
            if (!_hasHostPermanentOverlay && _hostPermanentOverlay.Count == 0)
                return;

            _hostPermanentOverlay.Clear();
            _hasHostPermanentOverlay = false;
            InvalidateDisplayCacheLocked();
        }

        RaiseStateChanged();
    }

    public static void ApplyRemoteTemporaryState(ulong ownerNetId, int index, string cardId, CardModificationState? state)
    {
        EnsureLoaded();
        CardModificationState normalized = state?.Clone() ?? new CardModificationState();
        normalized.Normalize();
        string key = $"{ownerNetId}:{index}:{cardId}";
        CardModificationState previousState = GetEffectiveStateForCopy(ownerNetId, index, cardId);

        lock (SyncRoot)
        {
            if (_loadedRunStartTime is not null)
            {
                if (normalized.IsEmpty)
                    _run.Cards.Remove(key);
                else
                    _run.Cards[key] = normalized;

                SaveRunState();
            }
        }

        TryApplyRemoteTemporaryToLiveCard(ownerNetId, index, cardId, previousState);
        RaiseStateChanged();
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

            bool hasCardMutation = HasCardMutations(effective) || HasCardMutations(previousState);
            if (hasCardMutation)
            {
                PrepareCardForState(card, effective, includeAffliction: true, previousState);
                ApplyStateToCard(card, effective);
            }

            if (hasCardMutation || HasVisualOverrides(effective) || HasVisualOverrides(previousState))
                RefreshLiveCardVisuals(card);
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

        ResetCardToCanonicalBaseline(card, resetAttachments: false);
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

    private static void ResetCardToCanonicalBaseline(CardModel card, bool resetAttachments)
    {
        if (card.IsCanonical)
            return;

        CardModel? canonical = ModelDb.AllCards.FirstOrDefault(candidate => candidate.Id.Equals(card.Id));
        if (canonical is null)
            return;

        CardModel baseline = CreateCanonicalBaselineForCurrentUpgrade(card, canonical);
        try
        {
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

    private static CardModel CreateCanonicalBaselineForCurrentUpgrade(CardModel card, CardModel canonical)
    {
        CardModel baseline = canonical.ToMutable();
        int upgradeLevel = Math.Max(0, Math.Min(card.CurrentUpgradeLevel, baseline.MaxUpgradeLevel));
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

    private static void ApplySavedRunStateToLiveDecks(IReadOnlyDictionary<string, CardModificationState>? previousPermanentStates)
    {
        try
        {
            if (!RunManager.Instance.IsInProgress)
                return;

            RunState? runState = RunManager.Instance.DebugOnlyGetState();
            if (runState is null)
                return;

            foreach (Player player in runState.Players)
                ApplySavedRunStateToPlayerDeck(player, previousPermanentStates);

            RaiseStateChanged();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: failed to refresh live decks after permanent overlay update. {exception.Message}");
        }
    }

    private static void RefreshLiveCardVisuals(CardModel card)
    {
        try
        {
            object? cardNodeObject = NCardFindOnTableByCard?.Invoke(null, [card]);
            cardNodeObject ??= NCardFindOnTableByCardAndPile?.Invoke(null, [card, card.Pile?.Type ?? PileType.None]);
            if (cardNodeObject is not NCard cardNode)
                return;

            PileType pileType = card.Pile?.Type ?? PileType.None;
            cardNode.Model = null;
            cardNode.Model = card;
            cardNode.UpdateVisuals(pileType, CardPreviewMode.Normal);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: updated card '{card.Id}', but could not refresh its live visual. {exception.Message}");
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
        }
        catch
        {
            // Weak-table marking is best effort; canonical cards remain local-catalog by default.
        }
    }

    private static Dictionary<string, CardModificationState> CloneStateDictionary(
        Dictionary<string, CardModificationState> states)
    {
        return states.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone(),
            StringComparer.Ordinal);
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
    }

    private static void ApplyKeywordOverrides(CardModel card, Dictionary<string, bool> keywordOverrides)
    {
        foreach ((string rawKeyword, bool enabled) in keywordOverrides)
        {
            if (!TryParseEnum(rawKeyword, out CardKeyword keyword) || keyword == CardKeyword.None)
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
        lock (SyncRoot)
        {
            _permanentLoaded = false;
            _runLoaded = false;
            _loadedRunStartTime = null;
            _permanent = new PermanentSaveData();
            _run = new RunSaveData();
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
                return;
            }

            string path = GetRunPath(currentRunStartTime.Value);
            SaveUtility.LoadResult<RunSaveData> loaded =
                SaveUtility.LoadProfileJson(path, new RunSaveData { RunStartTime = currentRunStartTime.Value });
            _run = NormalizeRun(loaded.Value, currentRunStartTime.Value);

            if (loaded.Loaded && loaded.Value.SchemaVersion != CurrentSchemaVersion)
                SaveRunState();
        }
    }

    private static void SavePermanentState()
    {
        _permanent.SchemaVersion = CurrentSchemaVersion;
        _permanent = NormalizePermanent(_permanent);
        SaveUtility.SaveProfileJson(PermanentPath, _permanent);
    }

    private static void SaveRunState()
    {
        if (_loadedRunStartTime is null)
            return;

        _run.SchemaVersion = CurrentSchemaVersion;
        _run.RunStartTime = _loadedRunStartTime.Value;
        _run = NormalizeRun(_run, _loadedRunStartTime.Value);
        SaveUtility.SaveProfileJson(GetRunPath(_loadedRunStartTime.Value), _run);
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
            Player? owner = card.Owner;
            if (owner is null)
                return false;

            IReadOnlyList<CardModel> deckCards = owner.Deck.Cards;
            for (int index = 0; index < deckCards.Count; index++)
            {
                if (!ReferenceEquals(deckCards[index], card))
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
