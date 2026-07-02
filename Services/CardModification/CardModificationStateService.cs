#nullable enable

namespace Loadout.Services.CardModification;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Godot;
using HarmonyLib;
using Loadout.Services.Saving;
using Loadout.Services.Targets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

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
    private static readonly MethodInfo? BaseStarCostSetter = AccessTools.PropertySetter(typeof(CardModel), nameof(CardModel.BaseStarCost));

    private static PermanentSaveData _permanent = new();
    private static RunSaveData _run = new();
    private static bool _registered;
    private static bool _permanentLoaded;
    private static bool _runLoaded;
    private static long? _loadedRunStartTime;

    public static void Register()
    {
        if (_registered)
            return;

        _registered = true;
        RunManager.Instance.RunStarted += OnRunStarted;
        SaveManager.Instance.ProfileIdChanged += OnProfileIdChanged;
        CombatManager.Instance.CombatSetUp += OnCombatSetUp;
        EnsureLoaded();
    }

    public static void Unregister()
    {
        if (!_registered)
            return;

        RunManager.Instance.RunStarted -= OnRunStarted;
        SaveManager.Instance.ProfileIdChanged -= OnProfileIdChanged;
        CombatManager.Instance.CombatSetUp -= OnCombatSetUp;
        _registered = false;
    }

    public static void EnsureLoaded()
    {
        ReloadPermanentIfNeeded();
        ReloadRunIfNeeded();
    }

    public static CardModificationState GetEffectiveState(LoadoutOwnedItem<CardModel> item)
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            CardModificationState effective = GetPermanentStateLocked(item.Model.Id).Clone();
            if (_run.Cards.TryGetValue(GetCopyKey(item), out CardModificationState? temporary))
                effective.MergeFrom(temporary);

            return effective;
        }
    }

    public static CardModificationState GetPermanentState(ModelId cardId)
    {
        EnsureLoaded();
        lock (SyncRoot)
            return GetPermanentStateLocked(cardId).Clone();
    }

    public static void SaveTemporary(LoadoutOwnedItem<CardModel> item, CardModificationState state)
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            if (_loadedRunStartTime is null)
            {
                GD.PushWarning("CardModification: no active run; skipped saving temporary card modifications.");
                return;
            }

            string key = GetCopyKey(item);
            CardModificationState normalized = state.Clone();
            normalized.Normalize();
            if (normalized.IsEmpty)
                _run.Cards.Remove(key);
            else
                _run.Cards[key] = normalized;

            SaveRunState();
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

            SavePermanentState();
        }
    }

    public static void ResetTemporary(LoadoutOwnedItem<CardModel> item)
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            if (_run.Cards.Remove(GetCopyKey(item)))
                SaveRunState();
        }
    }

    public static void ResetPermanent(ModelId cardId)
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            if (_permanent.Cards.Remove(ToCardKey(cardId)))
                SavePermanentState();
        }
    }

    public static void ApplyPermanentToCard(CardModel? card)
    {
        if (card is null || card.IsCanonical)
            return;

        EnsureLoaded();
        CardModificationState state;
        lock (SyncRoot)
            state = GetPermanentStateLocked(card.Id).Clone();

        ApplyStateToCard(card, state);
    }

    public static void ApplyEffectiveStateToOwnedCard(LoadoutOwnedItem<CardModel> item)
    {
        ApplyStateToCard(item.Model, GetEffectiveState(item));
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
                card.EnergyCost.SetCustomBaseCost(state.EnergyCost.Value);

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
        if (!card.IsCanonical)
            return card;

        EnsureLoaded();
        CardModificationState state;
        lock (SyncRoot)
            state = GetPermanentStateLocked(card.Id).Clone();

        if (state.IsEmpty)
            return card;

        try
        {
            CardModel preview = card.ToMutable();
            ApplyStateToCard(preview, state, includeAffliction: false);
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
        if (player is null)
            return;

        EnsureLoaded();
        IReadOnlyList<CardModel> cards = player.Deck.Cards;
        for (int index = 0; index < cards.Count; index++)
        {
            CardModel card = cards[index];
            CardModificationState state;
            lock (SyncRoot)
            {
                state = GetPermanentStateLocked(card.Id).Clone();
                if (_run.Cards.TryGetValue(GetCopyKey(player.NetId, index, card.Id), out CardModificationState? temporary))
                    state.MergeFrom(temporary);
            }

            ApplyStateToCard(card, state);
        }
    }

    public static string GetCopyKey(LoadoutOwnedItem<CardModel> item)
    {
        return GetCopyKey(item.OwnerNetId, item.Index, item.Model.Id);
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
                GD.PushWarning($"CardModification: '{card.Id}' already has enchantment '{card.Enchantment.Id}'; skipped '{canonical.Id}'.");

            return;
        }

        try
        {
            CardCmd.Enchant(canonical.ToMutable(), card, amount);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: could not enchant '{card.Id}' with '{canonical.Id}'. {exception.Message}");
        }
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
                GD.PushWarning($"CardModification: '{card.Id}' already has affliction '{card.Affliction.Id}'; skipped '{canonical.Id}'.");

            return;
        }

        TaskHelper.RunSafely(ApplyAfflictionAsync(card, canonical, amount));
    }

    private static async System.Threading.Tasks.Task ApplyAfflictionAsync(CardModel card, AfflictionModel canonical, int amount)
    {
        try
        {
            await CardCmd.Afflict(canonical.ToMutable(), card, amount);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: could not afflict '{card.Id}' with '{canonical.Id}'. {exception.Message}");
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

    private static string GetRunPath(long runStartTime)
    {
        return SaveUtility.GetRunSidecarPath(RunDirectory, RunFilePrefix, runStartTime);
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

        if (Affliction?.IsEmpty == true)
            Affliction = null;
    }
}
