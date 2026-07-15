#nullable enable

namespace Loadout.Services.CardModification;

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using BaseLib.Utils;
using Godot;
using Loadout.Keywords;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

/// <summary>
/// Sparse per-card storage for the card modifier.
///
/// Temporary state is serialized on the CardModel through BaseLib so it follows the
/// game's own card save/load path. Effective runtime state is attached only to cards
/// that are actually modified. Numeric/native values are written directly to CardModel;
/// the runtime entry exists only for reset/clone metadata and features CardModel does
/// not natively expose (custom text, portrait paths and Infinite Upgrade).
///
/// Nothing in this class polls, walks a deck, or recomputes state each frame.
/// </summary>
internal static class CardModificationInstanceState
{
    private const string FieldName = "loadout_card_modification_state_v2";

    private static readonly JsonSerializerOptions SerializedStateJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly SavedSpireField<CardModel, string> SerializedTemporaryState = CreateSavedField();
    private static ConditionalWeakTable<CardModel, ParsedStateCache> _parsedTemporaryStates = new();
    private static ConditionalWeakTable<CardModel, RuntimeStateHolder> _runtimeStates = new();
    private static readonly CardModificationState EmptyState = new();
    private static readonly CardModificationInstanceSnapshot EmptySnapshot = new(EmptyState, 0);
    private static int _runtimeFeatureHints;

    public static void Initialize()
    {
        _ = SerializedTemporaryState.Name;
    }

    private static SavedSpireField<CardModel, string> CreateSavedField()
    {
        SavedSpireField<CardModel, string> field = new(() => (string?)null, FieldName);
        field.CopyOnClone((source, destination, value) =>
            CopyTemporaryStateOnClone(field, source, destination, value));
        return field;
    }

    private static void CopyTemporaryStateOnClone(
        SavedSpireField<CardModel, string> field,
        CardModel source,
        CardModel destination,
        string? value)
    {
        string? serialized = NormalizeSerialized(value);
        ParsedStateEntry? sourceEntry = null;

        if (serialized is null)
        {
            if (!_parsedTemporaryStates.TryGetValue(source, out ParsedStateCache? existingCache))
                return;

            sourceEntry = Volatile.Read(ref existingCache.Entry);
            if (sourceEntry is null || sourceEntry.Serialized is null || sourceEntry.State.IsEmpty)
                return;
        }
        else
        {
            ParsedStateCache sourceCache =
                _parsedTemporaryStates.GetValue(source, static _ => new ParsedStateCache());
            sourceEntry = GetOrInitialize(sourceCache, serialized);
            if (!string.Equals(serialized, sourceEntry.Serialized, StringComparison.Ordinal))
                field.Set(source, sourceEntry.Serialized);
        }

        if (sourceEntry.Serialized is null || sourceEntry.State.IsEmpty)
            return;

        field.Set(destination, sourceEntry.Serialized);
        ParsedStateCache destinationCache =
            _parsedTemporaryStates.GetValue(destination, static _ => new ParsedStateCache());
        Volatile.Write(
            ref destinationCache.Entry,
            new ParsedStateEntry(sourceEntry.Revision, sourceEntry.Serialized, sourceEntry.State));
    }

    public static CardModificationInstanceSnapshot GetSnapshot(CardModel card)
    {
        return TryGetSnapshot(card, out CardModificationInstanceSnapshot snapshot)
            ? snapshot
            : EmptySnapshot;
    }

    public static bool TryGetSnapshot(CardModel card, out CardModificationInstanceSnapshot snapshot)
    {
        if (_parsedTemporaryStates.TryGetValue(card, out ParsedStateCache? existingCache))
        {
            ParsedStateEntry? existing = Volatile.Read(ref existingCache.Entry);
            if (existing is not null)
            {
                snapshot = new CardModificationInstanceSnapshot(existing.State, existing.Revision);
                return !existing.State.IsEmpty;
            }
        }

        string? serialized = NormalizeSerialized(SerializedTemporaryState.Get(card));
        if (serialized is null)
        {
            snapshot = EmptySnapshot;
            return false;
        }

        ParsedStateCache cache = existingCache
                                 ?? _parsedTemporaryStates.GetValue(card, static _ => new ParsedStateCache());
        ParsedStateEntry entry = GetOrInitialize(cache, serialized);
        if (!string.Equals(serialized, entry.Serialized, StringComparison.Ordinal))
            SerializedTemporaryState.Set(card, entry.Serialized);

        snapshot = new CardModificationInstanceSnapshot(entry.State, entry.Revision);
        return !entry.State.IsEmpty;
    }

    public static string? GetFingerprint(CardModel card)
    {
        if (_parsedTemporaryStates.TryGetValue(card, out ParsedStateCache? existingCache))
        {
            ParsedStateEntry? existing = Volatile.Read(ref existingCache.Entry);
            if (existing is not null)
                return existing.Serialized;
        }

        string? serialized = NormalizeSerialized(SerializedTemporaryState.Get(card));
        if (serialized is null)
            return null;

        ParsedStateCache cache = existingCache
                                 ?? _parsedTemporaryStates.GetValue(card, static _ => new ParsedStateCache());
        ParsedStateEntry entry = GetOrInitialize(cache, serialized);
        if (!string.Equals(serialized, entry.Serialized, StringComparison.Ordinal))
            SerializedTemporaryState.Set(card, entry.Serialized);
        return entry.Serialized;
    }

    public static CardModificationState Get(CardModel card)
    {
        return GetSnapshot(card).State.Clone();
    }

    public static CardModificationState GetReadOnly(CardModel card)
    {
        return GetSnapshot(card).State;
    }

    public static bool Set(CardModel card, CardModificationState? state)
    {
        CardModificationState normalized = state?.Clone() ?? new CardModificationState();
        normalized.Normalize();

        if (normalized.IsEmpty && !TryGetSnapshot(card, out _))
            return false;

        string? nextSerialized = normalized.IsEmpty ? null : Serialize(normalized);
        ParsedStateCache cache =
            _parsedTemporaryStates.GetValue(card, static _ => new ParsedStateCache());
        lock (cache)
        {
            ParsedStateEntry current = GetOrInitializeLocked(card, cache);
            if (string.Equals(current.Serialized, nextSerialized, StringComparison.Ordinal))
                return false;

            SerializedTemporaryState.Set(card, nextSerialized);
            Volatile.Write(
                ref cache.Entry,
                new ParsedStateEntry(
                    unchecked(current.Revision + 1),
                    nextSerialized,
                    normalized));
            return true;
        }
    }

    public static bool Clear(CardModel card)
    {
        return Set(card, null);
    }

    public static bool HasState(CardModel card)
    {
        return TryGetSnapshot(card, out _);
    }

    /// <summary>
    /// Publishes the already-applied effective state for this exact CardModel.
    /// This is called only when a card is created, loaded, cloned or explicitly edited.
    /// </summary>
    public static void SetAppliedState(CardModel card, CardModificationState? state)
    {
        CardModificationState normalized = state?.Clone() ?? new CardModificationState();
        normalized.Normalize();
        if (normalized.IsEmpty)
        {
            _runtimeStates.Remove(card);
            return;
        }

        RuntimeStateHolder holder = RuntimeStateHolder.FromState(normalized);
        _runtimeStates.Remove(card);
        _runtimeStates.Add(card, holder);
        MarkRuntimeFeatureHints(holder.Features);
    }

    public static void ClearAppliedState(CardModel card)
    {
        _runtimeStates.Remove(card);
    }

    public static bool TryGetAppliedState(CardModel card, out CardModificationState state)
    {
        if (_runtimeStates.TryGetValue(card, out RuntimeStateHolder? holder))
        {
            state = holder.State;
            return true;
        }

        state = EmptyState;
        return false;
    }

    public static CardModificationState GetAppliedStateClone(CardModel card)
    {
        return TryGetAppliedState(card, out CardModificationState state)
            ? state.Clone()
            : new CardModificationState();
    }

    public static void CopyAppliedState(CardModel source, CardModel destination)
    {
        if (!_runtimeStates.TryGetValue(source, out RuntimeStateHolder? sourceHolder))
            return;

        RuntimeStateHolder copy = sourceHolder.Clone();
        _runtimeStates.Remove(destination);
        _runtimeStates.Add(destination, copy);
        MarkRuntimeFeatureHints(copy.Features);
    }

    public static bool HasAnyCustomText => HasFeatureHint(CardRuntimeFeature.CustomText);
    public static bool HasAnyPortraitOverride => HasFeatureHint(CardRuntimeFeature.Portrait);
    public static bool HasAnyInfiniteUpgrade => HasFeatureHint(CardRuntimeFeature.InfiniteUpgrade);

    public static bool HasCustomText(CardModel card)
    {
        return HasAnyCustomText
               && _runtimeStates.TryGetValue(card, out RuntimeStateHolder? holder)
               && (holder.Features & CardRuntimeFeature.CustomText) != 0;
    }

    public static bool HasPortraitOverride(CardModel card)
    {
        return HasAnyPortraitOverride
               && _runtimeStates.TryGetValue(card, out RuntimeStateHolder? holder)
               && (holder.Features & CardRuntimeFeature.Portrait) != 0;
    }

    public static bool HasInfiniteUpgrade(CardModel card)
    {
        return HasAnyInfiniteUpgrade
               && _runtimeStates.TryGetValue(card, out RuntimeStateHolder? holder)
               && (holder.Features & CardRuntimeFeature.InfiniteUpgrade) != 0;
    }

    public static bool TryGetCustomTitle(CardModel card, out string title)
    {
        if (HasAnyCustomText
            && _runtimeStates.TryGetValue(card, out RuntimeStateHolder? holder)
            && holder.CustomTitle is { Length: > 0 } customTitle)
        {
            title = customTitle;
            return true;
        }

        title = string.Empty;
        return false;
    }

    public static bool TryGetCustomDescription(CardModel card, out string description)
    {
        if (HasAnyCustomText
            && _runtimeStates.TryGetValue(card, out RuntimeStateHolder? holder)
            && holder.CustomDescription is { Length: > 0 } customDescription)
        {
            description = customDescription;
            return true;
        }

        description = string.Empty;
        return false;
    }

    public static bool TryGetPortraitPath(CardModel card, bool beta, out string path)
    {
        if (HasAnyPortraitOverride
            && _runtimeStates.TryGetValue(card, out RuntimeStateHolder? holder))
        {
            string? selected = beta
                ? holder.BetaPortraitPath ?? holder.PortraitPath
                : holder.PortraitPath;
            if (!string.IsNullOrWhiteSpace(selected))
            {
                path = selected!;
                return true;
            }
        }

        path = string.Empty;
        return false;
    }

    public static void ResetRuntimeCaches()
    {
        _parsedTemporaryStates = new ConditionalWeakTable<CardModel, ParsedStateCache>();
        _runtimeStates = new ConditionalWeakTable<CardModel, RuntimeStateHolder>();
        Volatile.Write(ref _runtimeFeatureHints, 0);
    }

    private static ParsedStateEntry GetOrInitialize(ParsedStateCache cache, string? serialized)
    {
        ParsedStateEntry? entry = Volatile.Read(ref cache.Entry);
        if (entry is not null)
            return entry;

        lock (cache)
        {
            entry = Volatile.Read(ref cache.Entry);
            if (entry is not null)
                return entry;

            entry = CreateInitialEntry(serialized);
            Volatile.Write(ref cache.Entry, entry);
            return entry;
        }
    }

    private static ParsedStateEntry GetOrInitializeLocked(CardModel card, ParsedStateCache cache)
    {
        ParsedStateEntry? entry = Volatile.Read(ref cache.Entry);
        if (entry is not null)
            return entry;

        string? serialized = NormalizeSerialized(SerializedTemporaryState.Get(card));
        entry = CreateInitialEntry(serialized);
        if (!string.Equals(serialized, entry.Serialized, StringComparison.Ordinal))
            SerializedTemporaryState.Set(card, entry.Serialized);
        Volatile.Write(ref cache.Entry, entry);
        return entry;
    }

    private static ParsedStateEntry CreateInitialEntry(string? serialized)
    {
        CardModificationState state = Deserialize(serialized);
        string? compactSerialized = state.IsEmpty ? null : Serialize(state);
        return new ParsedStateEntry(0, compactSerialized, state);
    }

    private static string Serialize(CardModificationState state)
    {
        return JsonSerializer.Serialize(
            CompactCardModificationState.FromState(state),
            SerializedStateJsonOptions);
    }

    private static CardModificationState Deserialize(string? serialized)
    {
        if (string.IsNullOrWhiteSpace(serialized))
            return new CardModificationState();

        try
        {
            // Older v2 payloads used the default long property names; newer writes keep the same saved-field key but use compact names.
            if (LooksLikeLegacyPayload(serialized))
            {
                CardModificationState legacy =
                    JsonSerializer.Deserialize<CardModificationState>(serialized)
                    ?? new CardModificationState();
                legacy.Normalize();
                return legacy;
            }

            CompactCardModificationState? compact =
                JsonSerializer.Deserialize<CompactCardModificationState>(serialized);
            CardModificationState compactState = compact?.ToState() ?? new CardModificationState();
            compactState.Normalize();
            return compactState;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: ignored invalid attached card state. {exception.Message}");
            return new CardModificationState();
        }
    }

    private static bool LooksLikeLegacyPayload(string serialized)
    {
        return serialized.Contains("\"energyCost\":", StringComparison.Ordinal)
               || serialized.Contains("\"dynamicVars\":", StringComparison.Ordinal)
               || serialized.Contains("\"keywordOverrides\":", StringComparison.Ordinal)
               || serialized.Contains("\"customDescription\":", StringComparison.Ordinal);
    }

    private static string? NormalizeSerialized(string? serialized)
    {
        return string.IsNullOrWhiteSpace(serialized) ? null : serialized;
    }

    private static bool HasFeatureHint(CardRuntimeFeature feature)
    {
        return ((CardRuntimeFeature)Volatile.Read(ref _runtimeFeatureHints) & feature) != 0;
    }

    private static void MarkRuntimeFeatureHints(CardRuntimeFeature features)
    {
        int added = (int)features;
        if (added == 0)
            return;

        int current;
        int updated;
        do
        {
            current = Volatile.Read(ref _runtimeFeatureHints);
            updated = current | added;
            if (updated == current)
                return;
        }
        while (Interlocked.CompareExchange(ref _runtimeFeatureHints, updated, current) != current);
    }

    [Flags]
    private enum CardRuntimeFeature
    {
        None = 0,
        CustomText = 1 << 0,
        Portrait = 1 << 1,
        InfiniteUpgrade = 1 << 2
    }

    private sealed class RuntimeStateHolder
    {
        private RuntimeStateHolder(
            CardModificationState state,
            CardRuntimeFeature features,
            string? customTitle,
            string? customDescription,
            string? portraitPath,
            string? betaPortraitPath)
        {
            State = state;
            Features = features;
            CustomTitle = customTitle;
            CustomDescription = customDescription;
            PortraitPath = portraitPath;
            BetaPortraitPath = betaPortraitPath;
        }

        public CardModificationState State { get; }
        public CardRuntimeFeature Features { get; }
        public string? CustomTitle { get; }
        public string? CustomDescription { get; }
        public string? PortraitPath { get; }
        public string? BetaPortraitPath { get; }

        public static RuntimeStateHolder FromState(CardModificationState state)
        {
            CardRuntimeFeature features = CardRuntimeFeature.None;
            if (!string.IsNullOrWhiteSpace(state.CustomTitle)
                || !string.IsNullOrWhiteSpace(state.CustomDescription))
            {
                features |= CardRuntimeFeature.CustomText;
            }

            if (!string.IsNullOrWhiteSpace(state.PortraitPath)
                || !string.IsNullOrWhiteSpace(state.BetaPortraitPath))
            {
                features |= CardRuntimeFeature.Portrait;
            }

            if (state.KeywordOverrides.TryGetValue(
                    LoadoutKeywords.InfiniteUpgradeKey,
                    out bool infiniteUpgrade)
                && infiniteUpgrade)
            {
                features |= CardRuntimeFeature.InfiniteUpgrade;
            }

            return new RuntimeStateHolder(
                state.Clone(),
                features,
                state.CustomTitle,
                state.CustomDescription,
                state.PortraitPath,
                state.BetaPortraitPath);
        }

        public RuntimeStateHolder Clone()
        {
            return new RuntimeStateHolder(
                State.Clone(),
                Features,
                CustomTitle,
                CustomDescription,
                PortraitPath,
                BetaPortraitPath);
        }
    }

    private sealed class CompactCardModificationState
    {
        [JsonPropertyName("e")]
        public int? EnergyCost { get; set; }

        [JsonPropertyName("r")]
        public int? BaseReplayCount { get; set; }

        [JsonPropertyName("s")]
        public int? BaseStarCost { get; set; }

        [JsonPropertyName("d")]
        public Dictionary<string, decimal>? DynamicVars { get; set; }

        [JsonPropertyName("p")]
        public string? PoolId { get; set; }

        [JsonPropertyName("t")]
        public string? Type { get; set; }

        [JsonPropertyName("y")]
        public string? Rarity { get; set; }

        [JsonPropertyName("n")]
        public string? CustomTitle { get; set; }

        [JsonPropertyName("x")]
        public string? CustomDescription { get; set; }

        [JsonPropertyName("o")]
        public string? PortraitPath { get; set; }

        [JsonPropertyName("b")]
        public string? BetaPortraitPath { get; set; }

        [JsonPropertyName("k")]
        public Dictionary<string, bool>? KeywordOverrides { get; set; }

        [JsonPropertyName("q")]
        public CompactCardAttachmentSpec? Enchantment { get; set; }

        [JsonPropertyName("f")]
        public CompactCardAttachmentSpec? Affliction { get; set; }

        public static CompactCardModificationState FromState(CardModificationState state)
        {
            return new CompactCardModificationState
            {
                EnergyCost = state.EnergyCost,
                BaseReplayCount = state.BaseReplayCount,
                BaseStarCost = state.BaseStarCost,
                DynamicVars = state.DynamicVars.Count == 0
                    ? null
                    : new Dictionary<string, decimal>(state.DynamicVars, StringComparer.Ordinal),
                PoolId = state.PoolId,
                Type = state.Type,
                Rarity = state.Rarity,
                CustomTitle = state.CustomTitle,
                CustomDescription = state.CustomDescription,
                PortraitPath = state.PortraitPath,
                BetaPortraitPath = state.BetaPortraitPath,
                KeywordOverrides = state.KeywordOverrides.Count == 0
                    ? null
                    : new Dictionary<string, bool>(state.KeywordOverrides, StringComparer.Ordinal),
                Enchantment = CompactCardAttachmentSpec.FromSpec(state.Enchantment),
                Affliction = CompactCardAttachmentSpec.FromSpec(state.Affliction)
            };
        }

        public CardModificationState ToState()
        {
            return new CardModificationState
            {
                EnergyCost = EnergyCost,
                BaseReplayCount = BaseReplayCount,
                BaseStarCost = BaseStarCost,
                DynamicVars = DynamicVars is null
                    ? new Dictionary<string, decimal>(StringComparer.Ordinal)
                    : new Dictionary<string, decimal>(DynamicVars, StringComparer.Ordinal),
                PoolId = PoolId,
                Type = Type,
                Rarity = Rarity,
                CustomTitle = CustomTitle,
                CustomDescription = CustomDescription,
                PortraitPath = PortraitPath,
                BetaPortraitPath = BetaPortraitPath,
                KeywordOverrides = KeywordOverrides is null
                    ? new Dictionary<string, bool>(StringComparer.Ordinal)
                    : new Dictionary<string, bool>(KeywordOverrides, StringComparer.Ordinal),
                Enchantment = Enchantment?.ToSpec(),
                Affliction = Affliction?.ToSpec()
            };
        }
    }

    private sealed class CompactCardAttachmentSpec
    {
        [JsonPropertyName("m")]
        public string? ModelId { get; set; }

        [JsonPropertyName("a")]
        public int? Amount { get; set; }

        [JsonPropertyName("c")]
        public bool? Clear { get; set; }

        public static CompactCardAttachmentSpec? FromSpec(CardAttachmentSpec? spec)
        {
            if (spec is null || spec.IsEmpty)
                return null;

            return new CompactCardAttachmentSpec
            {
                ModelId = spec.ModelId,
                Amount = spec.Amount == 1 ? null : spec.Amount,
                Clear = spec.Clear ? true : null
            };
        }

        public CardAttachmentSpec ToSpec()
        {
            return new CardAttachmentSpec
            {
                ModelId = ModelId,
                Amount = Amount ?? 1,
                Clear = Clear ?? false
            };
        }
    }

    private sealed class ParsedStateCache
    {
        public ParsedStateEntry? Entry;
    }

    private sealed record ParsedStateEntry(
        int Revision,
        string? Serialized,
        CardModificationState State);
}

internal readonly record struct CardModificationInstanceSnapshot(
    CardModificationState State,
    int Revision);
