#nullable enable

using System.Collections.Generic;
using MegaCrit.Sts2.Core.Models;

namespace Loadout.Services.CardModification;

using System;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using BaseLib.Utils;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;

/// <summary>
/// Stores a temporary/per-copy card modification directly on the CardModel.
///
/// The SavedSpireField remains the persistence source of truth, while ParsedStates is
/// the runtime source of truth after the first access. Reads therefore do not repeatedly
/// enter BaseLib's SavedProperties dictionaries or deserialize JSON. CopyOnClone carries
/// the parsed read-only snapshot to the clone in O(1), which is important because the
/// game creates short-lived CardModel clones while cards are inspected and played.
/// </summary>
internal static class CardModificationInstanceState
{
    private const string FieldName = "loadout_card_modification_state_v2";

    private static readonly JsonSerializerOptions SerializedStateJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly SavedSpireField<CardModel, string> SerializedState = CreateSavedField();
    private static readonly ConditionalWeakTable<CardModel, ParsedStateCache> ParsedStates = new();
    private static readonly CardModificationState EmptyState = new();
    private static readonly CardModificationInstanceSnapshot EmptySnapshot = new(EmptyState, 0);

    public static void Initialize()
    {
        _ = SerializedState.Name;
    }

    private static SavedSpireField<CardModel, string> CreateSavedField()
    {
        SavedSpireField<CardModel, string> field = new(() => (string?)null, FieldName);
        field.CopyOnClone((source, destination, value) =>
        {
            string? serialized = NormalizeSerialized(value);
            ParsedStateEntry? sourceEntry = null;

            if (serialized is null)
            {
                // SavedSpireField invokes clone callbacks for every CardModel, including
                // the overwhelmingly common unmodified case. Do not create weak-table
                // entries, state objects, or destination metadata for an empty card.
                if (!ParsedStates.TryGetValue(source, out ParsedStateCache? existingCache))
                    return;

                sourceEntry = Volatile.Read(ref existingCache.Entry);
                if (sourceEntry is null || sourceEntry.Serialized is null || sourceEntry.State.IsEmpty)
                    return;
            }
            else
            {
                // Modified cards keep the parsed snapshot when cloned so they do not
                // deserialize their compact payload again on first use.
                ParsedStateCache sourceCache = ParsedStates.GetValue(source, static _ => new ParsedStateCache());
                sourceEntry = GetOrInitialize(sourceCache, serialized);
                if (!string.Equals(serialized, sourceEntry.Serialized, StringComparison.Ordinal))
                    field.Set(source, sourceEntry.Serialized);
            }

            if (sourceEntry.Serialized is null || sourceEntry.State.IsEmpty)
                return;

            field.Set(destination, sourceEntry.Serialized);
            ParsedStateCache destinationCache = ParsedStates.GetValue(destination, static _ => new ParsedStateCache());
            Volatile.Write(
                ref destinationCache.Entry,
                new ParsedStateEntry(
                    sourceEntry.Revision,
                    sourceEntry.Serialized,
                    sourceEntry.State));
        });
        return field;
    }

    public static CardModificationInstanceSnapshot GetSnapshot(CardModel card)
    {
        return TryGetSnapshot(card, out CardModificationInstanceSnapshot snapshot)
            ? snapshot
            : EmptySnapshot;
    }

    public static bool TryGetSnapshot(CardModel card, out CardModificationInstanceSnapshot snapshot)
    {
        if (ParsedStates.TryGetValue(card, out ParsedStateCache? existingCache))
        {
            ParsedStateEntry? existing = Volatile.Read(ref existingCache.Entry);
            if (existing is not null)
            {
                snapshot = new CardModificationInstanceSnapshot(existing.State, existing.Revision);
                return !existing.State.IsEmpty;
            }
        }

        string? serialized = NormalizeSerialized(SerializedState.Get(card));
        if (serialized is null)
        {
            snapshot = EmptySnapshot;
            return false;
        }

        ParsedStateCache cache = existingCache
                                 ?? ParsedStates.GetValue(card, static _ => new ParsedStateCache());
        ParsedStateEntry entry = GetOrInitialize(cache, serialized);
        if (!string.Equals(serialized, entry.Serialized, StringComparison.Ordinal))
            SerializedState.Set(card, entry.Serialized);
        snapshot = new CardModificationInstanceSnapshot(entry.State, entry.Revision);
        return !entry.State.IsEmpty;
    }

    public static string? GetFingerprint(CardModel card)
    {
        if (ParsedStates.TryGetValue(card, out ParsedStateCache? existingCache))
        {
            ParsedStateEntry? existing = Volatile.Read(ref existingCache.Entry);
            if (existing is not null)
                return existing.Serialized;
        }

        string? serialized = NormalizeSerialized(SerializedState.Get(card));
        if (serialized is null)
            return null;

        ParsedStateCache cache = existingCache
                                 ?? ParsedStates.GetValue(card, static _ => new ParsedStateCache());
        ParsedStateEntry entry = GetOrInitialize(cache, serialized);
        if (!string.Equals(serialized, entry.Serialized, StringComparison.Ordinal))
            SerializedState.Set(card, entry.Serialized);
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

        string? nextSerialized = normalized.IsEmpty
            ? null
            : Serialize(normalized);

        ParsedStateCache cache = ParsedStates.GetValue(card, static _ => new ParsedStateCache());
        lock (cache)
        {
            ParsedStateEntry current = GetOrInitializeLocked(card, cache);
            if (string.Equals(current.Serialized, nextSerialized, StringComparison.Ordinal))
                return false;

            SerializedState.Set(card, nextSerialized);
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

    private static ParsedStateEntry GetOrInitialize(CardModel card, ParsedStateCache cache)
    {
        ParsedStateEntry? entry = Volatile.Read(ref cache.Entry);
        if (entry is not null)
            return entry;

        lock (cache)
            return GetOrInitializeLocked(card, cache);
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

        string? serialized = NormalizeSerialized(SerializedState.Get(card));
        entry = CreateInitialEntry(serialized);
        if (!string.Equals(serialized, entry.Serialized, StringComparison.Ordinal))
            SerializedState.Set(card, entry.Serialized);
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
        return JsonSerializer.Serialize(CompactCardModificationState.FromState(state), SerializedStateJsonOptions);
    }

    private static CardModificationState Deserialize(string? serialized)
    {
        if (string.IsNullOrWhiteSpace(serialized))
            return new CardModificationState();

        try
        {
            // New attached states use short property names because this string travels
            // with the card through BaseLib's SavedProperties path. Old v2 payloads were
            // emitted by the default serializer and always used the long property names.
            if (LooksLikeLegacyPayload(serialized))
            {
                CardModificationState legacy = JsonSerializer.Deserialize<CardModificationState>(serialized)
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
