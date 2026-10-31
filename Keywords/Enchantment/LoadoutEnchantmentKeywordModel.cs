#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using Loadout.Services.CardModification;
using Loadout.UI.Managers;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;

public abstract class LoadoutEnchantmentKeywordModel : LoadoutCardGrantKeywordModel
{
    public override LoadoutKeywordEditorSection EditorSection => LoadoutKeywordEditorSection.Enchantment;
    public override string? CardTextLocKey => "LOADOUT-ADD_ENCHANTMENT.cardText";

    public override LoadoutCardKeywordEntry CreateDefaultEntry() => new()
    {
        KeywordKey = StorageKey,
        GrantedEnchantment = new CardAttachmentSpec { ModelId = ModelDb.GetId<Sharp>().ToString() }
    };

    public static bool IsGrantKey(string? key) =>
        string.Equals(key, LoadoutKeywords.AddEnchantmentToRandomCardKey, StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, LoadoutKeywords.AddEnchantmentToChosenCardKey, StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, LoadoutKeywords.AddEnchantmentToAllCardsKey, StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<EnchantmentModel> GetAvailableEnchantments() =>
        ModelDb.DebugEnchantments.Where(model =>
            !model.GetType().Name.StartsWith("Mock", StringComparison.Ordinal)
            && !model.GetType().Name.StartsWith("Deprecated", StringComparison.Ordinal));

    public static bool TryResolveEnchantment(string? id, out EnchantmentModel model)
    {
        model = null!;
        if (string.IsNullOrWhiteSpace(id))
            return false;
        string entry = id.Trim();
        int separator = entry.IndexOf('.');
        if (separator >= 0)
        {
            if (!entry[..separator].Equals("ENCHANTMENT", StringComparison.OrdinalIgnoreCase))
                return false;
            entry = entry[(separator + 1)..];
        }
        model = ModelDb.GetByIdOrNull<EnchantmentModel>(new ModelId("ENCHANTMENT", entry))!;
        return model is not null;
    }

    public static string GetLabel(CardAttachmentSpec? spec) =>
        TryResolveEnchantment(spec?.ModelId, out EnchantmentModel model)
            ? GetLabel(model) : spec?.ModelId ?? string.Empty;

    public static string GetLabel(EnchantmentModel model) =>
        LocMan.GameLoc("enchantments", model.Id.Entry + ".title", model.Id.Entry);

    public static IEnumerable<IHoverTip> GetHoverTips(CardAttachmentSpec? spec)
    {
        if (spec is null || spec.Clear || !TryResolveEnchantment(spec.ModelId, out EnchantmentModel canonical))
            return [];
        EnchantmentModel model = canonical.ToMutable();
        model.Amount = Math.Max(1, spec.Amount);
        model.RecalculateValues();
        return model.HoverTips;
    }

    public static bool CanApply(CardModel target, CardAttachmentSpec? spec) =>
        spec is { IsEmpty: false, Clear: false }
        && TryResolveEnchantment(spec.ModelId, out EnchantmentModel model) && model.CanEnchant(target);

    public static bool ApplyEnchantment(CardModel target, CardAttachmentSpec? spec)
    {
        if (target.IsCanonical || spec is null || spec.Clear
            || !TryResolveEnchantment(spec.ModelId, out EnchantmentModel model) || !model.CanEnchant(target))
            return false;
        return CardCmd.Enchant(model.ToMutable(), target, Math.Max(1, spec.Amount)) is not null;
    }

    protected override void AddCardTextVariables(CardModel card, LocString cardText) =>
        cardText.AddObj("GrantedEnchantments", string.Join(" ",
            LoadoutCardKeywordState.GetEffectiveEntries(card, StorageKey).Select(FormatEntry)));

    public static string FormatEntry(LoadoutCardKeywordEntry entry)
    {
        LoadoutKeywordCardSelection selection = entry.KeywordKey == LoadoutKeywords.AddEnchantmentToRandomCardKey
            ? LoadoutKeywordCardSelection.Random
            : entry.KeywordKey == LoadoutKeywords.AddEnchantmentToChosenCardKey
                ? LoadoutKeywordCardSelection.Chosen : LoadoutKeywordCardSelection.All;
        string single = entry.Amount == 1 && selection != LoadoutKeywordCardSelection.All ? "_SINGLE" : string.Empty;
        string enchantment = LocMan.Loc("CARD_MOD_ENCHANTMENT_AMOUNT_FORMAT", "{0} {1}",
            entry.GrantedEnchantment?.Amount ?? 1, $"[gold]{GetLabel(entry.GrantedEnchantment)}[/gold]");
        return LocMan.Loc("CARD_MOD_ENCHANT_" + selection.ToString().ToUpperInvariant() + single,
            selection switch
            {
                LoadoutKeywordCardSelection.Random => entry.Amount == 1
                    ? "Enchant a random eligible card in your {2} with {0}."
                    : "Enchant {1} random eligible cards in your {2} with {0}.",
                LoadoutKeywordCardSelection.Chosen => entry.Amount == 1
                    ? "Enchant an eligible card you choose in your {2} with {0}."
                    : "Enchant {1} eligible cards you choose in your {2} with {0}.",
                _ => "Enchant all other eligible cards in your {2} with {0}."
            }, enchantment, entry.Amount, LoadoutCardKeywordState.GetPileLabel(entry.Pile));
    }

    public override bool HasPayload(LoadoutCardKeywordEntry entry) =>
        entry.GrantedEnchantment is { IsEmpty: false, Clear: false }
        && TryResolveEnchantment(entry.GrantedEnchantment.ModelId, out _);

    public override bool CanTarget(CardModel target, LoadoutCardKeywordEntry entry) =>
        CanApply(target, entry.GrantedEnchantment);

    public override void ApplyToTarget(CardModel target, LoadoutCardKeywordEntry entry) =>
        ApplyEnchantment(target, entry.GrantedEnchantment);

    protected override LocString CreateSelectionPrompt(LoadoutCardKeywordEntry entry)
    {
        LocString prompt = new("card_keywords", "LOADOUT-ADD_ENCHANTMENT.selectionPrompt");
        prompt.AddObj("Enchantment", GetLabel(entry.GrantedEnchantment));
        return prompt;
    }
}

public sealed class AddEnchantmentToRandomCardKeyword : LoadoutEnchantmentKeywordModel
{
    public static AddEnchantmentToRandomCardKeyword Instance { get; } = new();
    public override CardKeyword Keyword => LoadoutKeywords.AddEnchantmentToRandomCard;
    public override string StorageKey => LoadoutKeywords.AddEnchantmentToRandomCardKey;
    public override string TitleLocKey => "LOADOUT-ADD_ENCHANTMENT_TO_RANDOM_CARD.title";
    public override LoadoutKeywordCardSelection Selection => LoadoutKeywordCardSelection.Random;
}

public sealed class AddEnchantmentToChosenCardKeyword : LoadoutEnchantmentKeywordModel
{
    public static AddEnchantmentToChosenCardKeyword Instance { get; } = new();
    public override CardKeyword Keyword => LoadoutKeywords.AddEnchantmentToChosenCard;
    public override string StorageKey => LoadoutKeywords.AddEnchantmentToChosenCardKey;
    public override string TitleLocKey => "LOADOUT-ADD_ENCHANTMENT_TO_CHOSEN_CARD.title";
    public override LoadoutKeywordCardSelection Selection => LoadoutKeywordCardSelection.Chosen;
}

public sealed class AddEnchantmentToAllCardsKeyword : LoadoutEnchantmentKeywordModel
{
    public static AddEnchantmentToAllCardsKeyword Instance { get; } = new();
    public override CardKeyword Keyword => LoadoutKeywords.AddEnchantmentToAllCards;
    public override string StorageKey => LoadoutKeywords.AddEnchantmentToAllCardsKey;
    public override string TitleLocKey => "LOADOUT-ADD_ENCHANTMENT_TO_ALL_CARDS.title";
    public override LoadoutKeywordCardSelection Selection => LoadoutKeywordCardSelection.All;
}
