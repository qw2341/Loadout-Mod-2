#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Loadout.PanelItems;
using Loadout.Services.CardModification;
using Loadout.UI.Managers;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

public abstract class LoadoutCardTransformKeywordModel : LoadoutCardKeywordModel
{
    public abstract bool TransformsAllOfType { get; }
    public override LoadoutKeywordEditorControlKind EditorControlKind => LoadoutKeywordEditorControlKind.Toggle;
    public override string CardLabelLocKey => "CARD_MOD_TRANSFORM_TARGET";
    public override string AmountLabelLocKey => "CARD_MOD_KEYWORD_TARGET_AMOUNT";
    public override string? CardTextLocKey => "LOADOUT-TRANSFORM_CARDS.cardText";

    public static bool IsTransformKey(string? key) =>
        string.Equals(key, LoadoutKeywords.TransformAllOfTypeKey, StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, LoadoutKeywords.TransformChosenCardsKey, StringComparison.OrdinalIgnoreCase);

    public override LoadoutCardKeywordEntry CreateDefaultEntry() => new()
    {
        KeywordKey = StorageKey,
        CardId = LoadoutCardKeywordState.GetDefaultCardId(),
        SourceCardType = CardType.Status
    };

    public LoadoutCardKeywordEntry GetEntry(CardModel card) =>
        LoadoutCardKeywordState.GetEffectiveEntries(card, StorageKey).FirstOrDefault() ?? CreateDefaultEntry();

    protected override void AddCardTextVariables(CardModel card, LocString cardText)
    {
        string text = LoadoutCardKeywordState.FormatEntryDescriptions(card, StorageKey, FormatEntry);
        cardText.AddObj("Transforms", string.IsNullOrEmpty(text) ? FormatEntry(CreateDefaultEntry(), "1") : text);
    }

    public static string FormatEntry(LoadoutCardKeywordEntry entry, string amount)
    {
        string target = LoadoutCardKeywordState.GetCardTitle(entry.CardId, entry.Upgraded);
        string pile = LoadoutCardKeywordState.GetPileLabel(entry.Pile);
        return string.Equals(entry.KeywordKey, LoadoutKeywords.TransformAllOfTypeKey, StringComparison.OrdinalIgnoreCase)
            ? LocMan.Loc("CARD_MOD_TRANSFORM_ALL_TEXT",
                "[gold]Transform[/gold] all [gold]{0}[/gold] cards in your [gold]{1}[/gold] into [gold]{2}[/gold].",
                CardPrinter.GetCardTypeLabel(entry.SourceCardType), pile, target)
            : LocMan.Loc(entry.Amount == 1 ? "CARD_MOD_TRANSFORM_CHOSEN_SINGLE_TEXT" : "CARD_MOD_TRANSFORM_CHOSEN_TEXT",
                entry.Amount == 1
                    ? "[gold]Transform[/gold] {0} card you choose in your [gold]{1}[/gold] into [gold]{2}[/gold]."
                    : "[gold]Transform[/gold] {0} cards you choose in your [gold]{1}[/gold] into [gold]{2}[/gold].",
                amount, pile, target);
    }

    public override object CaptureBeforeOnPlay(CardModel card, CardPlay cardPlay) => GetEntry(card);

    public override async Task AfterOnPlay(CardModel card, PlayerChoiceContext choiceContext,
        CardPlay cardPlay, object? capturedState)
    {
        var combat = card.CombatState;
        if (combat is null || CombatManager.Instance.IsOverOrEnding
            || capturedState is not LoadoutCardKeywordEntry entry
            || !LoadoutCardGrantKeywordModel.IsCombatPile(entry.Pile))
            return;
        if (!LoadoutCardKeywordState.TryResolveCard(entry.CardId, out CardModel canonical))
        {
            LoadoutCardKeywordState.WarnUnknownCard(entry.CardId);
            return;
        }

        bool IsCandidate(CardModel candidate) => !ReferenceEquals(candidate, card) && candidate.IsTransformable
            && (!TransformsAllOfType || candidate.Type == entry.SourceCardType);
        CardPile pile = entry.Pile.GetPile(card.Owner);
        List<CardModel> candidates = pile.Cards.Where(IsCandidate).ToList();
        int count = TransformsAllOfType ? candidates.Count : Math.Min(entry.Amount, candidates.Count);
        if (count <= 0)
            return;
        IEnumerable<CardModel> selected;
        if (TransformsAllOfType)
            selected = candidates;
        else
        {
            CardSelectorPrefs prefs = new(CardSelectorPrefs.TransformSelectionPrompt, count);
            selected = entry.Pile == PileType.Hand
                ? await CardSelectCmd.FromHand(choiceContext, card.Owner, prefs, IsCandidate, card)
                : await CardSelectCmd.FromCombatPile(choiceContext, pile, card.Owner, prefs, IsCandidate);
        }
        foreach (CardModel target in selected.ToArray())
        {
            if (CombatManager.Instance.IsOverOrEnding || !ReferenceEquals(card.CombatState, combat))
                return;
            if (!ReferenceEquals(target.Owner, card.Owner) || !ReferenceEquals(target.Pile, pile)
                || !ReferenceEquals(target.CombatState, combat) || !IsCandidate(target))
                continue;
            CardModel replacement = combat.CreateCard(canonical, card.Owner);
            if (entry.Upgraded && replacement.IsUpgradable)
                CardCmd.Upgrade(replacement);
            await CardCmd.Transform(target, replacement);
        }
    }

    public override IEnumerable<IHoverTip> GetAdditionalCardHoverTips(CardModel card)
    {
        yield return HoverTipFactory.Static(StaticHoverTip.Transform);
        LoadoutCardKeywordEntry entry = GetEntry(card);
        if (LoadoutCardKeywordState.TryResolveCard(entry.CardId, out CardModel canonical))
            yield return HoverTipFactory.FromCard(canonical, entry.Upgraded);
    }
}

public sealed class TransformAllOfTypeKeyword : LoadoutCardTransformKeywordModel
{
    public static TransformAllOfTypeKeyword Instance { get; } = new();
    public override CardKeyword Keyword => LoadoutKeywords.TransformAllOfType;
    public override string StorageKey => LoadoutKeywords.TransformAllOfTypeKey;
    public override string TitleLocKey => "LOADOUT-TRANSFORM_ALL_OF_TYPE.title";
    public override bool TransformsAllOfType => true;
}

public sealed class TransformChosenCardsKeyword : LoadoutCardTransformKeywordModel
{
    public static TransformChosenCardsKeyword Instance { get; } = new();
    public override CardKeyword Keyword => LoadoutKeywords.TransformChosenCards;
    public override string StorageKey => LoadoutKeywords.TransformChosenCardsKey;
    public override string TitleLocKey => "LOADOUT-TRANSFORM_CHOSEN_CARDS.title";
    public override bool TransformsAllOfType => false;
}
