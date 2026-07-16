#nullable enable

namespace Loadout.Services.CardModification;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

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

/// <summary>
/// A sparse description of fields changed by the card modifier. Permanent specs are
/// stored once per ModelId; temporary specs are attached only to modified card copies.
/// Runtime CardModel fields remain authoritative for gameplay.
/// </summary>
public sealed class CardModificationSpec
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

    [JsonIgnore]
    public bool HasNativeMutations =>
        EnergyCost.HasValue
        || BaseReplayCount.HasValue
        || BaseStarCost.HasValue
        || DynamicVars.Count > 0
        || !string.IsNullOrWhiteSpace(PoolId)
        || !string.IsNullOrWhiteSpace(Type)
        || !string.IsNullOrWhiteSpace(Rarity)
        || KeywordOverrides.Count > 0
        || Enchantment is not null
        || Affliction is not null;

    [JsonIgnore]
    public bool HasCustomText =>
        !string.IsNullOrWhiteSpace(CustomTitle)
        || !string.IsNullOrWhiteSpace(CustomDescription);

    [JsonIgnore]
    public bool HasPortraitOverride =>
        !string.IsNullOrWhiteSpace(PortraitPath)
        || !string.IsNullOrWhiteSpace(BetaPortraitPath)
        || !string.IsNullOrWhiteSpace(PoolId);

    public CardModificationSpec Clone()
    {
        return new CardModificationSpec
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

    public void MergeFrom(CardModificationSpec? other)
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

        CardAttachmentSpec? enchantment = Enchantment;
        NormalizeAttachment(ref enchantment);
        Enchantment = enchantment;
        CardAttachmentSpec? affliction = Affliction;
        NormalizeAttachment(ref affliction);
        Affliction = affliction;
        PoolId = NormalizeText(PoolId);
        Type = NormalizeText(Type);
        Rarity = NormalizeText(Rarity);
        CustomTitle = NormalizeText(CustomTitle);
        CustomDescription = NormalizeText(CustomDescription);
        PortraitPath = NormalizeText(PortraitPath);
        BetaPortraitPath = NormalizeText(BetaPortraitPath);
    }

    private static void NormalizeAttachment(ref CardAttachmentSpec? spec)
    {
        if (spec?.IsEmpty == true)
        {
            spec = null;
            return;
        }

        if (spec is null)
            return;

        spec.Amount = Math.Max(1, spec.Amount);
        if (spec.Clear)
            spec.ModelId = null;
        else
            spec.ModelId = NormalizeText(spec.ModelId);
    }

    private static string? NormalizeText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
