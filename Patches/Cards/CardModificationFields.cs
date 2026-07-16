#nullable enable

namespace Loadout.Patches.Cards;

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;

/// <summary>
/// Sparse data attached to an exact CardModel.
///
/// Ordinary cards never enter either ConditionalWeakTable: every gameplay getter
/// uses TryGetValue and immediately falls back to the base-game result. Any API that
/// materializes a default entry on a miss is deliberately avoided on hot paths.
/// </summary>
internal static class CardModificationFields
{
    internal const string TemporaryPropertyName = "loadout_card_modification_state_v4";
    private const string OverrideLocTableName = "loadout_card_title_overrides";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly ConditionalWeakTable<CardModel, RuntimeData> Runtime = new();
    private static readonly object LocGate = new();
    private static readonly Dictionary<string, string> LocEntries = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> LocKeysByText = new(StringComparer.Ordinal);
    private static readonly FieldInfo? LocTablesField = AccessTools.Field(typeof(LocManager), "_tables");
    private static readonly FieldInfo? EnglishLocTablesField = AccessTools.Field(typeof(LocManager), "_engTables");
    private static LocTable? _overrideLocTable;
    private static int _nextLocKey;

    internal static void Initialize() { }

    internal static bool TryGetTemporary(CardModel card, out CardModificationState state)
    {
        if (Runtime.TryGetValue(card, out RuntimeData? data) && data.TemporaryState is not null)
        {
            state = data.TemporaryState;
            return true;
        }

        state = null!;
        return false;
    }

    internal static CardModificationState GetTemporaryCopy(CardModel card)
        => TryGetTemporary(card, out CardModificationState state)
            ? state.Clone()
            : new CardModificationState();

    internal static bool SetTemporary(CardModel card, CardModificationState? state)
    {
        CardModificationState? normalized = Normalize(state);
        string? nextJson = normalized is null ? null : JsonSerializer.Serialize(normalized, JsonOptions);

        if (Runtime.TryGetValue(card, out RuntimeData? existing)
            && string.Equals(existing.TemporaryJson, nextJson, StringComparison.Ordinal))
        {
            return false;
        }

        if (normalized is null)
        {
            if (existing is null)
                return false;

            existing.TemporaryState = null;
            existing.TemporaryJson = null;
            RemoveIfEmpty(card, existing);
            return true;
        }

        RuntimeData data = Runtime.GetValue(card, static _ => new RuntimeData());
        data.TemporaryState = normalized;
        data.TemporaryJson = nextJson;
        return true;
    }

    internal static bool ClearTemporary(CardModel card) => SetTemporary(card, null);

    internal static bool HasTemporary(CardModel card)
        => Runtime.TryGetValue(card, out RuntimeData? data) && data.TemporaryState is not null;

    /// <summary>
    /// Writes temporary state only for a card that actually owns temporary data.
    /// Ordinary cards add no custom property and perform no table lookup.
    /// </summary>
    internal static void ExportTemporaryToSave(CardModel card, SerializableCard save)
    {
        if (!Runtime.TryGetValue(card, out RuntimeData? data)
            || string.IsNullOrWhiteSpace(data.TemporaryJson))
        {
            return;
        }

        (save.Props.strings ??= []).RemoveAll(property => property.name == TemporaryPropertyName);
        save.Props.strings.Add(new(TemporaryPropertyName, data.TemporaryJson));
    }

    /// <summary>
    /// Imports the v4 property directly from SerializableCard. No global saved-field
    /// registry and no ordinary-card default entry are involved.
    /// </summary>
    internal static bool ImportTemporaryFromSave(CardModel card, SerializableCard save)
    {
        string? json = null;
        if (save.Props?.strings is { } stringProperties)
        {
            foreach (var property in stringProperties)
            {
                if (!string.Equals(property.name, TemporaryPropertyName, StringComparison.Ordinal))
                    continue;

                json = property.value;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            CardModificationState? parsed = JsonSerializer.Deserialize<CardModificationState>(json, JsonOptions);
            parsed?.Normalize();
            if (parsed is null || parsed.IsEmpty)
                return false;

            RuntimeData data = Runtime.GetValue(card, static _ => new RuntimeData());
            data.TemporaryState = parsed;
            data.TemporaryJson = json;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Copies only data the game's native clone cannot carry. Native damage,
    /// block, cost, draw, rarity, type, keywords, etc. are already present on the
    /// source CardModel and are copied by the game's own clone implementation.
    /// </summary>
    internal static void CopyRuntimeToClone(CardModel source, CardModel destination)
    {
        if (!Runtime.TryGetValue(source, out RuntimeData? sourceData))
            return;

        RuntimeData destinationData = Runtime.GetValue(destination, static _ => new RuntimeData());

        if (sourceData.TemporaryState is not null)
        {
            destinationData.TemporaryState = sourceData.TemporaryState.Clone();
            destinationData.TemporaryJson = sourceData.TemporaryJson;
        }

        destinationData.CustomTitle = sourceData.CustomTitle;
        destinationData.CustomDescription = sourceData.CustomDescription;
        destinationData.Portrait = sourceData.Portrait;
        destinationData.BetaPortrait = sourceData.BetaPortrait;
        destinationData.InfiniteUpgrade = sourceData.InfiniteUpgrade;
        destinationData.CustomTitleLoc = null;

        RemoveIfEmpty(destination, destinationData);
    }

    /// <summary>
    /// Writes only non-native overrides to the exact card. No full modification
    /// state is retained after native fields have been applied.
    /// </summary>
    internal static void SetEffectiveOverrides(CardModel card, CardModificationState? state)
    {
        string? title = NormalizeText(state?.CustomTitle);
        string? description = NormalizeText(state?.CustomDescription);
        string? portrait = NormalizeText(state?.PortraitPath);
        string? betaPortrait = NormalizeText(state?.BetaPortraitPath);
        bool infiniteUpgrade = state?.KeywordOverrides.TryGetValue(
            Loadout.Keywords.LoadoutKeywords.InfiniteUpgradeKey,
            out bool enabled) == true && enabled;

        bool hasOverrides = title is not null
                            || description is not null
                            || portrait is not null
                            || betaPortrait is not null
                            || infiniteUpgrade;

        if (!hasOverrides)
        {
            if (Runtime.TryGetValue(card, out RuntimeData? existing))
            {
                existing.CustomTitle = null;
                existing.CustomDescription = null;
                existing.Portrait = null;
                existing.BetaPortrait = null;
                existing.InfiniteUpgrade = false;
                existing.CustomTitleLoc = null;
                RemoveIfEmpty(card, existing);
            }
            return;
        }

        RuntimeData data = Runtime.GetValue(card, static _ => new RuntimeData());
        if (!string.Equals(data.CustomTitle, title, StringComparison.Ordinal))
            data.CustomTitleLoc = null;
        data.CustomTitle = title;
        data.CustomDescription = description;
        data.Portrait = portrait;
        data.BetaPortrait = betaPortrait;
        data.InfiniteUpgrade = infiniteUpgrade;
    }

    internal static bool HasCustomText(CardModel card)
        => Runtime.TryGetValue(card, out RuntimeData? data)
           && (data.CustomTitle is not null || data.CustomDescription is not null);

    internal static bool HasPortrait(CardModel card)
        => Runtime.TryGetValue(card, out RuntimeData? data)
           && (data.Portrait is not null || data.BetaPortrait is not null);

    internal static bool HasInfiniteUpgrade(CardModel card)
        => Runtime.TryGetValue(card, out RuntimeData? data) && data.InfiniteUpgrade;

    internal static bool TryGetCustomTitle(CardModel card, out string value)
    {
        if (Runtime.TryGetValue(card, out RuntimeData? data) && data.CustomTitle is { Length: > 0 } title)
        {
            value = title;
            return true;
        }

        value = string.Empty;
        return false;
    }

    internal static bool TryGetCustomDescription(CardModel card, out string value)
    {
        if (Runtime.TryGetValue(card, out RuntimeData? data)
            && data.CustomDescription is { Length: > 0 } description)
        {
            value = description;
            return true;
        }

        value = string.Empty;
        return false;
    }

    internal static bool TryGetPortrait(CardModel card, bool beta, out string value)
    {
        if (Runtime.TryGetValue(card, out RuntimeData? data))
        {
            string? path = beta ? data.BetaPortrait ?? data.Portrait : data.Portrait;
            if (!string.IsNullOrWhiteSpace(path))
            {
                value = path;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    internal static LocString GetOrCreateTitleLocString(CardModel card, LocString original, string rawText)
    {
        RuntimeData data = Runtime.GetValue(card, static _ => new RuntimeData());
        if (data.CustomTitleLoc is not null
            && string.Equals(data.CustomTitleLoc.RawText, rawText, StringComparison.Ordinal))
        {
            lock (LocGate)
            {
                if (EnsureOverrideLocTable())
                    return data.CustomTitleLoc.LocString;
            }
            return original;
        }

        if (!TryGetOverrideLocString(rawText, out LocString? replacement))
            return original;

        replacement.AddVariablesFrom(original);
        data.CustomTitleLoc = new TitleLocOverride(rawText, replacement);
        return replacement;
    }

    // Compatibility helpers used only by editor/event code. These are sparse direct
    // field operations; they are not read-through gameplay caches.
    internal static CardModificationInstanceSnapshot GetSnapshot(CardModel card)
        => TryGetTemporary(card, out CardModificationState state)
            ? new CardModificationInstanceSnapshot(state, 0)
            : new CardModificationInstanceSnapshot(new CardModificationState(), 0);

    internal static bool TryGetSnapshot(CardModel card, out CardModificationInstanceSnapshot snapshot)
    {
        if (TryGetTemporary(card, out CardModificationState state))
        {
            snapshot = new CardModificationInstanceSnapshot(state, 0);
            return true;
        }

        snapshot = default;
        return false;
    }

    internal static CardModificationState Get(CardModel card) => GetTemporaryCopy(card);
    internal static CardModificationState GetReadOnly(CardModel card)
        => TryGetTemporary(card, out CardModificationState state) ? state : EmptyState.Instance;
    internal static bool Set(CardModel card, CardModificationState? state) => SetTemporary(card, state);
    internal static bool Clear(CardModel card) => ClearTemporary(card);
    internal static bool HasState(CardModel card) => HasTemporary(card);
    internal static bool HasPortraitOverride(CardModel card) => HasPortrait(card);
    internal static bool TryGetPortraitPath(CardModel card, bool beta, out string path)
        => TryGetPortrait(card, beta, out path);
    internal static void ResetRuntimeCaches() { }

    private static bool TryGetOverrideLocString(string rawText, out LocString? locString)
    {
        lock (LocGate)
        {
            if (!EnsureOverrideLocTable())
            {
                locString = null;
                return false;
            }

            if (!LocKeysByText.TryGetValue(rawText, out string? key))
            {
                key = $"title_{++_nextLocKey}";
                LocKeysByText[rawText] = key;
                LocEntries[key] = rawText;
            }

            locString = new LocString(OverrideLocTableName, key);
            return true;
        }
    }

    private static bool EnsureOverrideLocTable()
    {
        try
        {
            LocManager manager = LocManager.Instance;
            if (LocTablesField?.GetValue(manager) is not IDictionary<string, LocTable> tables)
                return false;

            if (_overrideLocTable is null)
                _overrideLocTable = new LocTable(OverrideLocTableName, LocEntries, null!);

            tables[OverrideLocTableName] = _overrideLocTable;
            if (EnglishLocTablesField?.GetValue(manager) is IDictionary<string, LocTable> englishTables)
                englishTables[OverrideLocTableName] = _overrideLocTable;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void RemoveIfEmpty(CardModel card, RuntimeData data)
    {
        if (data.TemporaryState is null
            && data.CustomTitle is null
            && data.CustomDescription is null
            && data.Portrait is null
            && data.BetaPortrait is null
            && !data.InfiniteUpgrade)
        {
            Runtime.Remove(card);
        }
    }

    private static CardModificationState? Normalize(CardModificationState? state)
    {
        CardModificationState? normalized = state?.Clone();
        normalized?.Normalize();
        return normalized is null || normalized.IsEmpty ? null : normalized;
    }

    private static string? NormalizeText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class RuntimeData
    {
        public CardModificationState? TemporaryState;
        public string? TemporaryJson;
        public string? CustomTitle;
        public string? CustomDescription;
        public string? Portrait;
        public string? BetaPortrait;
        public bool InfiniteUpgrade;
        public TitleLocOverride? CustomTitleLoc;
    }

    private sealed class TitleLocOverride(string rawText, LocString locString)
    {
        public string RawText { get; } = rawText;
        public LocString LocString { get; } = locString;
    }

    private static class EmptyState
    {
        internal static readonly CardModificationState Instance = new();
    }
}

internal readonly record struct CardModificationInstanceSnapshot(CardModificationState State, int Revision);
