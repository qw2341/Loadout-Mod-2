#nullable enable

using MegaCrit.Sts2.Core.Models;

namespace Loadout.Services.CardModification;

using System;
using System.Runtime.CompilerServices;
using System.Text.Json;
using BaseLib.Utils;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;

/// <summary>
/// Stores a temporary/per-copy card modification directly on the CardModel.
///
/// The saved field is a JSON string instead of CardModificationState itself so it uses
/// BaseLib's base-game-supported SavedProperties string path. This avoids custom
/// save-type registration, and explicit Loadout deltas still provide ordered live sync.
/// </summary>
internal static class CardModificationInstanceState
{
    private const string FieldName = "loadout_card_modification_state_v2";

    private static readonly SavedSpireField<CardModel, string> SerializedState = CreateSavedField();
    private static readonly ConditionalWeakTable<CardModel, ParsedStateCache> ParsedStates = new();

    public static void Initialize()
    {
        _ = SerializedState.Name;
    }

    private static SavedSpireField<CardModel, string> CreateSavedField()
    {
        SavedSpireField<CardModel, string> field = new(() => (string?)null, FieldName);
        field.CopyOnClone((_, destination, value) =>
        {
            if (value is null)
                return;

            field.Set(destination, value);
            ParsedStates.Remove(destination);
        });
        return field;
    }

    public static string? GetFingerprint(CardModel card)
    {
        return NormalizeSerialized(SerializedState.Get(card));
    }

    public static CardModificationState Get(CardModel card)
    {
        return GetReadOnly(card).Clone();
    }

    public static CardModificationState GetReadOnly(CardModel card)
    {
        string? serialized = GetFingerprint(card);
        ParsedStateCache cache = ParsedStates.GetValue(card, _ => new ParsedStateCache());
        if (string.Equals(cache.Serialized, serialized, StringComparison.Ordinal))
            return cache.State;

        CardModificationState state = Deserialize(serialized);
        cache.Serialized = serialized;
        cache.State = state;
        return state;
    }

    public static bool Set(CardModel card, CardModificationState? state)
    {
        CardModificationState normalized = state?.Clone() ?? new CardModificationState();
        normalized.Normalize();

        string? nextSerialized = normalized.IsEmpty
            ? null
            : JsonSerializer.Serialize(normalized);
        string? previousSerialized = GetFingerprint(card);
        if (string.Equals(previousSerialized, nextSerialized, StringComparison.Ordinal))
            return false;

        SerializedState.Set(card, nextSerialized);
        ParsedStates.Remove(card);
        if (nextSerialized is not null)
        {
            ParsedStates.Add(card, new ParsedStateCache
            {
                Serialized = nextSerialized,
                State = normalized
            });
        }

        return true;
    }

    public static bool Clear(CardModel card)
    {
        return Set(card, null);
    }

    public static bool HasState(CardModel card)
    {
        return GetFingerprint(card) is not null;
    }

    private static CardModificationState Deserialize(string? serialized)
    {
        if (string.IsNullOrWhiteSpace(serialized))
            return new CardModificationState();

        try
        {
            CardModificationState state = JsonSerializer.Deserialize<CardModificationState>(serialized)
                                          ?? new CardModificationState();
            state.Normalize();
            return state;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: ignored invalid attached card state. {exception.Message}");
            return new CardModificationState();
        }
    }

    private static string? NormalizeSerialized(string? serialized)
    {
        return string.IsNullOrWhiteSpace(serialized) ? null : serialized;
    }

    private sealed class ParsedStateCache
    {
        public string? Serialized { get; set; }

        public CardModificationState State { get; set; } = new();
    }
}
