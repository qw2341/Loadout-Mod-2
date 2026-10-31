#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Loadout.Services.CardModification;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

public abstract class LoadoutCardGrantKeywordModel : LoadoutCardKeywordModel
{
    public abstract LoadoutKeywordCardSelection Selection { get; }
    public override string AmountLabelLocKey => "CARD_MOD_KEYWORD_TARGET_AMOUNT";

    public static bool IsCombatPile(PileType pile) =>
        pile is PileType.Hand or PileType.Draw or PileType.Discard or PileType.Exhaust;

    public abstract bool HasPayload(LoadoutCardKeywordEntry entry);
    public virtual bool CanTarget(CardModel target, LoadoutCardKeywordEntry entry) => true;
    public abstract void ApplyToTarget(CardModel target, LoadoutCardKeywordEntry entry);
    protected abstract LocString CreateSelectionPrompt(LoadoutCardKeywordEntry entry);

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
            if (!IsCombatPile(entry.Pile) || !HasPayload(entry))
                continue;
            bool IsCandidate(CardModel candidate) =>
                !ReferenceEquals(candidate, card) && CanTarget(candidate, entry);
            CardPile pile = entry.Pile.GetPile(card.Owner);
            List<CardModel> candidates = pile.Cards.Where(IsCandidate).ToList();
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
                CardSelectorPrefs prefs = new(CreateSelectionPrompt(entry), count);
                selected = entry.Pile == PileType.Hand
                    ? await CardSelectCmd.FromHand(choiceContext, card.Owner, prefs, IsCandidate, card)
                    : await CardSelectCmd.FromCombatPile(choiceContext, pile, card.Owner, prefs, IsCandidate);
            }
            foreach (CardModel target in selected)
            {
                if (CombatManager.Instance.IsOverOrEnding || !ReferenceEquals(card.CombatState, combat))
                    return;
                if (ReferenceEquals(target.Owner, card.Owner) && ReferenceEquals(target.Pile, pile)
                    && ReferenceEquals(target.CombatState, combat) && IsCandidate(target))
                    ApplyToTarget(target, entry);
            }
        }
    }
}
