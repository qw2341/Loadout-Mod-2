#nullable enable

namespace Loadout.Services.CardModification;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Loadout.Keywords;
using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class LoadoutCardKeywordEntry
{
    [JsonPropertyName("k")]
    public string KeywordKey { get; set; } = string.Empty;

    [JsonPropertyName("c")]
    public string CardId { get; set; } = string.Empty;

    [JsonPropertyName("a")]
    public int Amount { get; set; } = 1;

    [JsonPropertyName("u")]
    public bool Upgraded { get; set; }

    [JsonPropertyName("pool")]
    public string PoolId { get; set; } = string.Empty;

    [JsonPropertyName("rarity")]
    public CardRarity Rarity { get; set; } = CardRarity.None;

    [JsonIgnore]
    public bool IsRandom => string.Equals(KeywordKey, LoadoutKeywords.AddRandomCardKey, StringComparison.OrdinalIgnoreCase);

    [JsonPropertyName("p")]
    public MegaCrit.Sts2.Core.Entities.Cards.PileType Pile { get; set; } = MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand;

    [JsonIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(KeywordKey)
        || (!IsRandom && string.IsNullOrWhiteSpace(CardId));

    public LoadoutCardKeywordEntry Clone()
    {
        return new LoadoutCardKeywordEntry
        {
            KeywordKey = KeywordKey,
            CardId = CardId,
            Amount = Amount,
            Upgraded = Upgraded,
            PoolId = PoolId,
            Rarity = Rarity,
            Pile = Pile
        };
    }

    public static List<LoadoutCardKeywordEntry>? CloneList(
        IReadOnlyList<LoadoutCardKeywordEntry>? source) =>
        source?.Select(entry => entry.Clone()).ToList();

    public static List<LoadoutCardKeywordEntry>? NormalizeList(
        List<LoadoutCardKeywordEntry>? source)
    {
        if (source is null)
            return null;

        List<LoadoutCardKeywordEntry> result = [];
        foreach (LoadoutCardKeywordEntry? entry in source)
        {
            if (entry is null)
                continue;

            entry.KeywordKey = entry.KeywordKey.Trim();
            entry.CardId = entry.CardId.Trim();
            entry.PoolId = (entry.PoolId ?? string.Empty).Trim();
            entry.Amount = Math.Max(0, entry.Amount);
            if (!Loadout.Keywords.LoadoutCardKeywordState.IsSupportedPile(entry.Pile))
                entry.Pile = MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand;
            if (!entry.IsEmpty)
                result.Add(entry);
        }
        return result;
    }
}

public sealed class LoadoutCardKeywordEntryUpgrade
{
    [JsonPropertyName("k")]
    public string KeywordKey { get; set; } = string.Empty;

    [JsonPropertyName("c")]
    public string OriginalCardId { get; set; } = string.Empty;

    [JsonPropertyName("o")]
    public int OccurrenceIndex { get; set; }

    [JsonPropertyName("d")]
    public int AmountDelta { get; set; }

    [JsonPropertyName("r")]
    public string? ReplacementCardId { get; set; }

    [JsonPropertyName("u")]
    public bool? ReplacementUpgraded { get; set; }

    [JsonPropertyName("t")]
    public MegaCrit.Sts2.Core.Entities.Cards.PileType? ReplacementPile { get; set; }

    [JsonPropertyName("pool")]
    public string? ReplacementPoolId { get; set; }

    [JsonPropertyName("rarity")]
    public CardRarity? ReplacementRarity { get; set; }

    [JsonIgnore]
    public bool HasIdentity =>
        !string.IsNullOrWhiteSpace(KeywordKey)
        && (!string.IsNullOrWhiteSpace(OriginalCardId)
            || string.Equals(KeywordKey, LoadoutKeywords.AddRandomCardKey, StringComparison.OrdinalIgnoreCase))
        && OccurrenceIndex >= 0;

    [JsonIgnore]
    public bool IsEmpty =>
        !HasIdentity
        || (AmountDelta == 0 && string.IsNullOrWhiteSpace(ReplacementCardId)
            && ReplacementUpgraded is null && ReplacementPile is null
            && ReplacementPoolId is null && ReplacementRarity is null);

    public LoadoutCardKeywordEntryUpgrade Clone()
    {
        return new LoadoutCardKeywordEntryUpgrade
        {
            KeywordKey = KeywordKey,
            OriginalCardId = OriginalCardId,
            OccurrenceIndex = OccurrenceIndex,
            AmountDelta = AmountDelta,
            ReplacementCardId = ReplacementCardId,
            ReplacementUpgraded = ReplacementUpgraded,
            ReplacementPoolId = ReplacementPoolId,
            ReplacementRarity = ReplacementRarity,
            ReplacementPile = ReplacementPile
        };
    }

    public static List<LoadoutCardKeywordEntryUpgrade>? CloneList(
        IReadOnlyList<LoadoutCardKeywordEntryUpgrade>? source) =>
        source?.Select(entry => entry.Clone()).ToList();

    public static List<LoadoutCardKeywordEntryUpgrade>? NormalizeList(
        List<LoadoutCardKeywordEntryUpgrade>? source)
    {
        if (source is null)
            return null;

        List<LoadoutCardKeywordEntryUpgrade> result = [];
        foreach (LoadoutCardKeywordEntryUpgrade? entry in source)
        {
            if (entry is null)
                continue;

            entry.KeywordKey = entry.KeywordKey.Trim();
            entry.OriginalCardId = entry.OriginalCardId.Trim();
            entry.ReplacementPoolId = entry.ReplacementPoolId?.Trim();
            if (entry.ReplacementPile.HasValue && !Loadout.Keywords.LoadoutCardKeywordState.IsSupportedPile(entry.ReplacementPile.Value))
                entry.ReplacementPile = null;
            entry.ReplacementCardId = string.IsNullOrWhiteSpace(entry.ReplacementCardId)
                || string.Equals(
                    entry.ReplacementCardId.Trim(),
                    entry.OriginalCardId,
                    StringComparison.OrdinalIgnoreCase)
                ? null
                : entry.ReplacementCardId.Trim();
            if (!entry.IsEmpty)
                result.Add(entry);
        }
        return result;
    }
}
