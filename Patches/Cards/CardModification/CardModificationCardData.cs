#nullable enable

namespace Loadout.Patches.Cards.CardModification;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using Loadout.Keywords;
using Loadout.Services.CardModification;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

/// <summary>
/// Sparse per-copy data. Only cards with temporary/non-native overrides get an entry.
/// The holder is the state itself; there is no parallel parsed cache or revision map.
/// </summary>
internal sealed class CardModificationCardData
{
    public CardModificationCardData(CardModificationSpec spec)
    {
        Spec = spec.Clone();
        Spec.Normalize();
        Serialized = CardModificationCodec.Serialize(Spec);
        Fingerprint = CardModificationCodec.Fingerprint(Serialized);
    }

    public CardModificationSpec Spec { get; }

    public string Serialized { get; }

    public string Fingerprint { get; }

}

internal static class CardModificationFields
{
    private static readonly ConditionalWeakTable<CardModel, CardModificationCardData> Data = new();

    public static bool TryGet(CardModel card, out CardModificationCardData data)
    {
        if (Data.TryGetValue(card, out CardModificationCardData? value))
        {
            data = value;
            return true;
        }

        data = null!;
        return false;
    }

    public static CardModificationSpec GetSpec(CardModel card)
    {
        return TryGet(card, out CardModificationCardData data)
            ? data.Spec.Clone()
            : new CardModificationSpec();
    }

    public static bool Set(CardModel card, CardModificationSpec? spec)
    {
        CardModificationSpec normalized = spec?.Clone() ?? new CardModificationSpec();
        normalized.Normalize();
        if (normalized.IsEmpty)
            return Clear(card);

        if (TryGet(card, out CardModificationCardData current)
            && string.Equals(current.Serialized, CardModificationCodec.Serialize(normalized), StringComparison.Ordinal))
        {
            return false;
        }

        Data.Remove(card);
        Data.Add(card, new CardModificationCardData(normalized));
        return true;
    }

    public static bool Clear(CardModel card)
    {
        if (!TryGet(card, out _))
            return false;

        return Data.Remove(card);
    }

    public static void Copy(CardModel source, CardModel destination)
    {
        if (!TryGet(source, out CardModificationCardData data))
            return;

        Data.Remove(destination);
        // The holder and its spec are immutable. Exact clones can share it until
        // either card is edited, at which point Set replaces that card's entry.
        Data.Add(destination, data);
    }

    /// <summary>
    /// Native upgrades mutate the CardModel directly. Keep only the already-declared
    /// temporary fields synchronized so subsequent save/load reproduces that result.
    /// </summary>
    public static void CaptureUpgradedValues(CardModel card)
    {
        if (!TryGet(card, out CardModificationCardData data))
            return;

        CardModificationSpec next = data.Spec.Clone();
        if (next.EnergyCost.HasValue && !card.EnergyCost.CostsX)
            next.EnergyCost = card.EnergyCost.Canonical;
        if (next.BaseReplayCount.HasValue)
            next.BaseReplayCount = card.BaseReplayCount;
        if (next.BaseStarCost.HasValue)
            next.BaseStarCost = card.BaseStarCost;

        foreach (string name in new List<string>(next.DynamicVars.Keys))
        {
            if (card.DynamicVars.TryGetValue(name, out var dynamicVar))
                next.DynamicVars[name] = dynamicVar.BaseValue;
        }

        foreach (string rawKeyword in new List<string>(next.KeywordOverrides.Keys))
        {
            if (LoadoutKeywords.TryResolve(rawKeyword, out CardKeyword keyword))
                next.KeywordOverrides[rawKeyword] = LoadoutKeywords.Has(card, keyword);
        }

        if (next.PoolId is not null)
            next.PoolId = card.Pool.Id.ToString();
        if (next.Type is not null)
            next.Type = card.Type.ToString();
        if (next.Rarity is not null)
            next.Rarity = card.Rarity.ToString();
        if (next.Enchantment is not null)
        {
            next.Enchantment = card.Enchantment is null
                ? new CardAttachmentSpec { Clear = true }
                : new CardAttachmentSpec
                {
                    ModelId = card.Enchantment.Id.ToString(),
                    Amount = Math.Max(1, card.Enchantment.Amount)
                };
        }
        if (next.Affliction is not null)
        {
            next.Affliction = card.Affliction is null
                ? new CardAttachmentSpec { Clear = true }
                : new CardAttachmentSpec
                {
                    ModelId = card.Affliction.Id.ToString(),
                    Amount = Math.Max(1, card.Affliction.Amount)
                };
        }

        Set(card, next);
    }
}

internal static class CardModificationCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(CardModificationSpec spec)
    {
        return JsonSerializer.Serialize(CompactSpec.FromSpec(spec), JsonOptions);
    }

    public static bool TryDeserialize(string? payload, out CardModificationSpec spec)
    {
        spec = new CardModificationSpec();
        if (string.IsNullOrWhiteSpace(payload))
            return true;

        try
        {
            CardModificationSpec? parsed;
            if (LooksLikeLegacyPayload(payload))
            {
                parsed = JsonSerializer.Deserialize<CardModificationSpec>(payload, JsonOptions);
            }
            else
            {
                parsed = JsonSerializer.Deserialize<CompactSpec>(payload, JsonOptions)?.ToSpec();
            }

            spec = parsed ?? new CardModificationSpec();
            spec.Normalize();
            return true;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: ignored invalid attached card data. {exception.Message}");
            return false;
        }
    }

    public static string Fingerprint(string serialized)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(serialized));
        return Convert.ToHexString(hash.AsSpan(0, 16));
    }

    private static bool LooksLikeLegacyPayload(string serialized)
    {
        return serialized.Contains("\"energyCost\":", StringComparison.Ordinal)
               || serialized.Contains("\"dynamicVars\":", StringComparison.Ordinal)
               || serialized.Contains("\"keywordOverrides\":", StringComparison.Ordinal)
               || serialized.Contains("\"customDescription\":", StringComparison.Ordinal);
    }

    private sealed class CompactSpec
    {
        [JsonPropertyName("e")]
        public int? EnergyCost { get; set; }

        [JsonPropertyName("r")]
        public int? BaseReplayCount { get; set; }

        [JsonPropertyName("s")]
        public int? BaseStarCost { get; set; }

        [JsonPropertyName("d")]
        public SortedDictionary<string, decimal>? DynamicVars { get; set; }

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
        public SortedDictionary<string, bool>? KeywordOverrides { get; set; }

        [JsonPropertyName("q")]
        public CompactAttachment? Enchantment { get; set; }

        [JsonPropertyName("f")]
        public CompactAttachment? Affliction { get; set; }

        public static CompactSpec FromSpec(CardModificationSpec spec)
        {
            return new CompactSpec
            {
                EnergyCost = spec.EnergyCost,
                BaseReplayCount = spec.BaseReplayCount,
                BaseStarCost = spec.BaseStarCost,
                DynamicVars = spec.DynamicVars.Count == 0
                    ? null
                    : new SortedDictionary<string, decimal>(spec.DynamicVars, StringComparer.Ordinal),
                PoolId = spec.PoolId,
                Type = spec.Type,
                Rarity = spec.Rarity,
                CustomTitle = spec.CustomTitle,
                CustomDescription = spec.CustomDescription,
                PortraitPath = spec.PortraitPath,
                BetaPortraitPath = spec.BetaPortraitPath,
                KeywordOverrides = spec.KeywordOverrides.Count == 0
                    ? null
                    : new SortedDictionary<string, bool>(spec.KeywordOverrides, StringComparer.Ordinal),
                Enchantment = CompactAttachment.FromSpec(spec.Enchantment),
                Affliction = CompactAttachment.FromSpec(spec.Affliction)
            };
        }

        public CardModificationSpec ToSpec()
        {
            return new CardModificationSpec
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

    private sealed class CompactAttachment
    {
        [JsonPropertyName("m")]
        public string? ModelId { get; set; }

        [JsonPropertyName("a")]
        public int? Amount { get; set; }

        [JsonPropertyName("c")]
        public bool? Clear { get; set; }

        public static CompactAttachment? FromSpec(CardAttachmentSpec? spec)
        {
            if (spec is null || spec.IsEmpty)
                return null;

            return new CompactAttachment
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
}
