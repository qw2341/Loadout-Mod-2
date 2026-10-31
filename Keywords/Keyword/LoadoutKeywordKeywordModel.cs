#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using Loadout.Services.CardModification;
using Loadout.UI.Managers;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

public enum LoadoutKeywordCardSelection
{
    Random,
    Chosen,
    All
}

public abstract class LoadoutKeywordKeywordModel : LoadoutCardGrantKeywordModel
{
    public override LoadoutKeywordEditorSection EditorSection => LoadoutKeywordEditorSection.Keyword;
    public override string? CardTextLocKey => "LOADOUT-ADD_KEYWORD.cardText";

    public override LoadoutCardKeywordEntry CreateDefaultEntry() => new()
    {
        KeywordKey = StorageKey,
        GrantedKeyword = LoadoutKeywordPayload.Create(CardKeyword.Retain)
    };

    public static bool IsGrantKey(string? key) =>
        string.Equals(key, LoadoutKeywords.AddKeywordToRandomCardKey, StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, LoadoutKeywords.AddKeywordToChosenCardKey, StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, LoadoutKeywords.AddKeywordToAllCardsKey, StringComparison.OrdinalIgnoreCase);

    protected override void AddCardTextVariables(CardModel card, LocString cardText)
    {
        string text = string.Join(" ", LoadoutCardKeywordState.GetEffectiveEntries(card, StorageKey)
            .Where(entry => entry.GrantedKeyword is not null)
            .Select(FormatEntry));
        cardText.AddObj("GrantedKeywords", text);
    }

    public static string FormatEntry(LoadoutCardKeywordEntry entry)
    {
        LoadoutKeywordCardSelection selection = entry.KeywordKey == LoadoutKeywords.AddKeywordToRandomCardKey
            ? LoadoutKeywordCardSelection.Random
            : entry.KeywordKey == LoadoutKeywords.AddKeywordToChosenCardKey
                ? LoadoutKeywordCardSelection.Chosen : LoadoutKeywordCardSelection.All;
        string single = entry.Amount == 1 && selection != LoadoutKeywordCardSelection.All ? "_SINGLE" : string.Empty;
        return LocMan.Loc("CARD_MOD_GRANT_" + selection.ToString().ToUpperInvariant() + single,
                selection switch
                {
                    LoadoutKeywordCardSelection.Random => entry.Amount == 1
                        ? "Add {0} to a random card in your {2}."
                        : "Add {0} to {1} random cards in your {2}.",
                    LoadoutKeywordCardSelection.Chosen => entry.Amount == 1
                        ? "Add {0} to a card you choose in your {2}."
                        : "Add {0} to {1} cards you choose in your {2}.",
                    _ => "Add {0} to all other cards in your {2}."
                }, entry.GrantedKeyword?.GetDescription() ?? string.Empty, entry.Amount, LoadoutCardKeywordState.GetPileLabel(entry.Pile));
    }

    public override bool HasPayload(LoadoutCardKeywordEntry entry) =>
        entry.GrantedKeyword is not null && LoadoutKeywords.TryResolve(entry.GrantedKeyword.KeywordKey, out _);

    public override void ApplyToTarget(CardModel target, LoadoutCardKeywordEntry entry) =>
        ApplyKeyword(target, entry.GrantedKeyword!);

    protected override LocString CreateSelectionPrompt(LoadoutCardKeywordEntry entry)
    {
        LocString prompt = new("card_keywords", "LOADOUT-ADD_KEYWORD.selectionPrompt");
        prompt.AddObj("Keyword", entry.GrantedKeyword!.GetLabel());
        return prompt;
    }

    public static void ApplyKeyword(CardModel target, LoadoutKeywordPayload payload)
    {
        if (target.Pile is not null && IsCombatPile(target.Pile.Type))
            payload.ApplyTo(target);
    }

    public override IEnumerable<IHoverTip> GetAdditionalCardHoverTips(CardModel card)
    {
        HashSet<CardKeyword> shown = [];
        foreach (var entry in LoadoutCardKeywordState.GetEffectiveEntries(card, StorageKey))
        {
            if (LoadoutKeywords.TryResolve(entry.GrantedKeyword?.KeywordKey, out CardKeyword keyword)
                && shown.Add(keyword))
                yield return HoverTipFactory.FromKeyword(keyword);
        }
    }
}

public sealed class AddKeywordToRandomCardKeyword : LoadoutKeywordKeywordModel
{
    public static AddKeywordToRandomCardKeyword Instance { get; } = new();
    public override CardKeyword Keyword => LoadoutKeywords.AddKeywordToRandomCard;
    public override string StorageKey => LoadoutKeywords.AddKeywordToRandomCardKey;
    public override string TitleLocKey => "LOADOUT-ADD_KEYWORD_TO_RANDOM_CARD.title";
    public override LoadoutKeywordCardSelection Selection => LoadoutKeywordCardSelection.Random;
}

public sealed class AddKeywordToChosenCardKeyword : LoadoutKeywordKeywordModel
{
    public static AddKeywordToChosenCardKeyword Instance { get; } = new();
    public override CardKeyword Keyword => LoadoutKeywords.AddKeywordToChosenCard;
    public override string StorageKey => LoadoutKeywords.AddKeywordToChosenCardKey;
    public override string TitleLocKey => "LOADOUT-ADD_KEYWORD_TO_CHOSEN_CARD.title";
    public override LoadoutKeywordCardSelection Selection => LoadoutKeywordCardSelection.Chosen;
}

public sealed class AddKeywordToAllCardsKeyword : LoadoutKeywordKeywordModel
{
    public static AddKeywordToAllCardsKeyword Instance { get; } = new();
    public override CardKeyword Keyword => LoadoutKeywords.AddKeywordToAllCards;
    public override string StorageKey => LoadoutKeywords.AddKeywordToAllCardsKey;
    public override string TitleLocKey => "LOADOUT-ADD_KEYWORD_TO_ALL_CARDS.title";
    public override LoadoutKeywordCardSelection Selection => LoadoutKeywordCardSelection.All;
}
