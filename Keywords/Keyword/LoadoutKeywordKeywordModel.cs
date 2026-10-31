#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Loadout.Patches.Cards.CardModification;
using Loadout.Services.CardModification;
using Loadout.UI.Managers;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

public enum LoadoutKeywordCardSelection
{
    Random,
    Chosen,
    All
}

public abstract class LoadoutKeywordKeywordModel : LoadoutCardKeywordModel
{
    public abstract LoadoutKeywordCardSelection Selection { get; }
    public override LoadoutKeywordEditorSection EditorSection => LoadoutKeywordEditorSection.Keyword;
    public override string AmountLabelLocKey => "CARD_MOD_KEYWORD_TARGET_AMOUNT";
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

    public static bool IsCombatPile(PileType pile) =>
        pile is PileType.Hand or PileType.Draw or PileType.Discard or PileType.Exhaust;

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

    public override object CaptureBeforeOnPlay(CardModel card, CardPlay cardPlay) =>
        LoadoutCardKeywordState.GetEffectiveEntries(card, StorageKey).ToArray();

    public override async Task AfterOnPlay(CardModel card, PlayerChoiceContext choiceContext,
        CardPlay cardPlay, object? capturedState)
    {
        var combat = card.CombatState;
        if (combat is null || capturedState is not LoadoutCardKeywordEntry[] entries)
            return;
        foreach (var entry in entries)
        {
            if (CombatManager.Instance.IsOverOrEnding || !ReferenceEquals(card.CombatState, combat))
                return;
            if (!IsCombatPile(entry.Pile) || entry.GrantedKeyword is null
                || !LoadoutKeywords.TryResolve(entry.GrantedKeyword.KeywordKey, out _))
                continue;
            CardPile pile = entry.Pile.GetPile(card.Owner);
            List<CardModel> candidates = pile.Cards.Where(candidate => !ReferenceEquals(candidate, card)).ToList();
            int count = Selection == LoadoutKeywordCardSelection.All ? candidates.Count : Math.Min(entry.Amount, candidates.Count);
            if (count <= 0)
                continue;
            IEnumerable<CardModel> selected;
            if (Selection == LoadoutKeywordCardSelection.Random)
                selected = candidates.TakeRandom(count, card.Owner.RunState.Rng.CombatCardSelection).ToArray();
            else if (Selection == LoadoutKeywordCardSelection.All)
                selected = candidates;
            else
            {
                LocString prompt = new("card_keywords", "LOADOUT-ADD_KEYWORD.selectionPrompt");
                prompt.AddObj("Keyword", entry.GrantedKeyword.GetLabel());
                CardSelectorPrefs prefs = new(prompt, count);
                selected = entry.Pile == PileType.Hand
                    ? await CardSelectCmd.FromHand(choiceContext, card.Owner, prefs, candidate => !ReferenceEquals(candidate, card), card)
                    : await CardSelectCmd.FromCombatPile(choiceContext, pile, card.Owner, prefs, candidate => !ReferenceEquals(candidate, card));
            }
            foreach (CardModel target in selected)
            {
                if (CombatManager.Instance.IsOverOrEnding || !ReferenceEquals(card.CombatState, combat))
                    return;
                if (ReferenceEquals(target.Owner, card.Owner) && ReferenceEquals(target.Pile, pile)
                    && ReferenceEquals(target.CombatState, combat) && !ReferenceEquals(target, card))
                    ApplyKeyword(target, entry.GrantedKeyword);
            }
        }
    }

    public static void ApplyKeyword(CardModel target, LoadoutKeywordPayload payload)
    {
        if (target.IsCanonical || target.Pile is null || !IsCombatPile(target.Pile.Type)
            || !LoadoutKeywords.TryResolve(payload.KeywordKey, out CardKeyword keyword))
            return;
        CardModificationSpec state = CardModificationRuntime.GetEffectiveSpec(target);
        state.KeywordOverrides[payload.KeywordKey] = true;
        foreach ((string name, decimal value) in payload.Values)
            state.DynamicVars[name] = value;
        if (payload.Power is { } power)
            (state.PowerKeywordEntries ??= []).Add(power.Clone());
        if (payload.Card is { } nested)
            (state.CardKeywordEntries ??= []).Add(nested.Clone());
        if (payload.Commands is { } commands)
        {
            List<string> effective = AltExecuteKeyword.GetCommands(target).ToList();
            effective.AddRange(commands);
            state.ExecuteCommands = effective;
            if (target.CurrentUpgradeLevel > 0)
                state.UpgradeModification.ExecuteCommands = effective.ToList();
        }
        CardModificationFields.Set(target, state);
        LoadoutPowerKeywordState.SetExplicitState(target, state);
        LoadoutCardKeywordState.SetExplicitState(target, state);
        AltExecuteKeyword.SetExplicitState(target, state);
        bool newlyAdded = !LoadoutKeywords.Has(target, keyword);
        LoadoutKeywordRegistry.SynchronizeDynamicVars(target, state.KeywordOverrides);
        foreach ((string name, decimal value) in payload.Values)
        {
            if (target.DynamicVars.TryGetValue(name, out var variable))
                variable.BaseValue = value;
        }
        if (LoadoutKeywordRegistry.TryGet(keyword, out LoadoutKeywordModel model))
        {
            if (newlyAdded)
                model.OnAdded(target);
            if ((model.ModificationFlags & LoadoutCardModificationFlags.OverrideXCost) != 0)
                XCostKeywordMechanics.SynchronizeEnergyCost(target, state.KeywordOverrides, state.EnergyCost);
        }
        CardCmd.ApplyKeyword(target, keyword);
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
