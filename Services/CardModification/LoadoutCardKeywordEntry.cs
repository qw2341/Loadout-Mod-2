#nullable enable

namespace Loadout.Services.CardModification;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Loadout.Keywords;
using MegaCrit.Sts2.Core.Entities.Cards;

public enum LoadoutGeneratedCardCost
{
    None,
    FreeThisTurn,
    FreeThisCombat
}

public sealed class LoadoutCardKeywordEntry
{
    [JsonPropertyName("k")]
    public string KeywordKey { get; set; } = string.Empty;

    [JsonPropertyName("c")]
    public string CardId { get; set; } = string.Empty;

    [JsonPropertyName("orb")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OrbId { get; set; }

    [JsonPropertyName("grant")]
    public LoadoutKeywordPayload? GrantedKeyword { get; set; }

    [JsonPropertyName("ench")]
    public CardAttachmentSpec? GrantedEnchantment { get; set; }

    [JsonIgnore]
    public bool IsKeywordGrant => LoadoutKeywordKeywordModel.IsGrantKey(KeywordKey);

    [JsonIgnore]
    public bool IsEnchantmentGrant => LoadoutEnchantmentKeywordModel.IsGrantKey(KeywordKey);

    [JsonIgnore]
    public bool IsCardGrant => IsKeywordGrant || IsEnchantmentGrant;

    [JsonIgnore]
    public bool IsOrb => LoadoutBasicOrbKeywordModel.IsOrbKey(KeywordKey);

    [JsonIgnore]
    public bool IsTransform => LoadoutCardTransformKeywordModel.IsTransformKey(KeywordKey);

    [JsonPropertyName("type")]
    public CardType SourceCardType { get; set; } = CardType.Status;

    [JsonPropertyName("a")]
    public int Amount { get; set; } = 1;

    [JsonPropertyName("u")]
    public bool Upgraded { get; set; }

    [JsonPropertyName("pool")]
    public string PoolId { get; set; } = string.Empty;

    [JsonPropertyName("rarity")]
    public CardRarity Rarity { get; set; } = CardRarity.None;

    [JsonPropertyName("free")]
    public LoadoutGeneratedCardCost FreeToPlay { get; set; }

    [JsonIgnore]
    public LoadoutGeneratedCardCost EffectiveFreeToPlay =>
        Pile == PileType.Deck ? LoadoutGeneratedCardCost.None : FreeToPlay;

    [JsonIgnore]
    public bool IsRandom => string.Equals(KeywordKey, LoadoutKeywords.AddRandomCardKey, StringComparison.OrdinalIgnoreCase);

    [JsonPropertyName("p")]
    public MegaCrit.Sts2.Core.Entities.Cards.PileType Pile { get; set; } = MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand;

    [JsonIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(KeywordKey)
        || (IsOrb ? string.IsNullOrWhiteSpace(OrbId)
            : IsKeywordGrant ? GrantedKeyword is null || GrantedKeyword.IsEmpty
            : IsEnchantmentGrant ? GrantedEnchantment is null || GrantedEnchantment.IsEmpty || GrantedEnchantment.Clear
            : !IsRandom && string.IsNullOrWhiteSpace(CardId));

    public LoadoutCardKeywordEntry Clone()
    {
        return new LoadoutCardKeywordEntry
        {
            KeywordKey = KeywordKey,
            CardId = CardId,
            OrbId = OrbId,
            GrantedKeyword = GrantedKeyword?.Clone(),
            GrantedEnchantment = GrantedEnchantment?.Clone(),
            Amount = Amount,
            Upgraded = Upgraded,
            PoolId = PoolId,
            Rarity = Rarity,
            SourceCardType = SourceCardType,
            FreeToPlay = FreeToPlay,
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
            entry.OrbId = string.IsNullOrWhiteSpace(entry.OrbId) ? null : entry.OrbId.Trim();
            entry.PoolId = (entry.PoolId ?? string.Empty).Trim();
            entry.Amount = Math.Max(0, entry.Amount);
            entry.GrantedKeyword?.Normalize();
            entry.GrantedEnchantment?.Normalize();
            if (entry.GrantedEnchantment is { IsEmpty: true } or { Clear: true })
                entry.GrantedEnchantment = null;
            if (!entry.IsKeywordGrant && entry.GrantedKeyword is { IsEmpty: true })
                entry.GrantedKeyword = null;
            if ((entry.IsCardGrant || entry.IsTransform) && !LoadoutCardGrantKeywordModel.IsCombatPile(entry.Pile))
                entry.Pile = PileType.Hand;
            if (!Enum.IsDefined(entry.FreeToPlay))
                entry.FreeToPlay = LoadoutGeneratedCardCost.None;
            if (!Loadout.Keywords.LoadoutCardKeywordState.IsSupportedPile(entry.Pile))
                entry.Pile = MegaCrit.Sts2.Core.Entities.Cards.PileType.Hand;
            if (!entry.IsEmpty && (!entry.IsTransform || !result.Any(existing =>
                    string.Equals(existing.KeywordKey, entry.KeywordKey, StringComparison.OrdinalIgnoreCase))))
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

    [JsonPropertyName("orb")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReplacementOrbId { get; set; }

    [JsonPropertyName("grant")]
    public LoadoutKeywordPayload? ReplacementGrantedKeyword { get; set; }

    [JsonPropertyName("ench")]
    public CardAttachmentSpec? ReplacementGrantedEnchantment { get; set; }

    [JsonPropertyName("u")]
    public bool? ReplacementUpgraded { get; set; }

    [JsonPropertyName("t")]
    public MegaCrit.Sts2.Core.Entities.Cards.PileType? ReplacementPile { get; set; }

    [JsonPropertyName("pool")]
    public string? ReplacementPoolId { get; set; }

    [JsonPropertyName("rarity")]
    public CardRarity? ReplacementRarity { get; set; }

    [JsonPropertyName("type")]
    public CardType? ReplacementSourceCardType { get; set; }

    [JsonPropertyName("free")]
    public LoadoutGeneratedCardCost? ReplacementFreeToPlay { get; set; }

    [JsonIgnore]
    public bool HasIdentity =>
        !string.IsNullOrWhiteSpace(KeywordKey)
        && (!string.IsNullOrWhiteSpace(OriginalCardId)
            || LoadoutKeywordKeywordModel.IsGrantKey(KeywordKey)
            || LoadoutEnchantmentKeywordModel.IsGrantKey(KeywordKey)
            || LoadoutBasicOrbKeywordModel.IsOrbKey(KeywordKey)
            || string.Equals(KeywordKey, LoadoutKeywords.AddRandomCardKey, StringComparison.OrdinalIgnoreCase))
        && OccurrenceIndex >= 0;

    [JsonIgnore]
    public bool IsEmpty =>
        !HasIdentity
        || (AmountDelta == 0 && string.IsNullOrWhiteSpace(ReplacementCardId)
            && string.IsNullOrWhiteSpace(ReplacementOrbId)
            && ReplacementUpgraded is null && ReplacementPile is null
            && ReplacementPoolId is null && ReplacementRarity is null && ReplacementSourceCardType is null && ReplacementFreeToPlay is null
            && ReplacementGrantedKeyword is null && ReplacementGrantedEnchantment is null);

    public LoadoutCardKeywordEntryUpgrade Clone()
    {
        return new LoadoutCardKeywordEntryUpgrade
        {
            KeywordKey = KeywordKey,
            OriginalCardId = OriginalCardId,
            OccurrenceIndex = OccurrenceIndex,
            AmountDelta = AmountDelta,
            ReplacementCardId = ReplacementCardId,
            ReplacementOrbId = ReplacementOrbId,
            ReplacementGrantedKeyword = ReplacementGrantedKeyword?.Clone(),
            ReplacementGrantedEnchantment = ReplacementGrantedEnchantment?.Clone(),
            ReplacementUpgraded = ReplacementUpgraded,
            ReplacementPoolId = ReplacementPoolId,
            ReplacementRarity = ReplacementRarity,
            ReplacementSourceCardType = ReplacementSourceCardType,
            ReplacementFreeToPlay = ReplacementFreeToPlay,
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
            entry.ReplacementOrbId = string.IsNullOrWhiteSpace(entry.ReplacementOrbId)
                ? null : entry.ReplacementOrbId.Trim();
            entry.ReplacementGrantedKeyword?.Normalize();
            entry.ReplacementGrantedEnchantment?.Normalize();
            if (entry.ReplacementGrantedEnchantment is { IsEmpty: true })
                entry.ReplacementGrantedEnchantment = null;
            entry.ReplacementPoolId = entry.ReplacementPoolId?.Trim();
            if (entry.ReplacementFreeToPlay.HasValue && !Enum.IsDefined(entry.ReplacementFreeToPlay.Value))
                entry.ReplacementFreeToPlay = null;
            if (entry.ReplacementPile.HasValue && !Loadout.Keywords.LoadoutCardKeywordState.IsSupportedPile(entry.ReplacementPile.Value))
                entry.ReplacementPile = null;
            if (LoadoutCardTransformKeywordModel.IsTransformKey(entry.KeywordKey)
                && entry.ReplacementPile.HasValue && !LoadoutCardGrantKeywordModel.IsCombatPile(entry.ReplacementPile.Value))
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
