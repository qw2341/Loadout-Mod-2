#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;

public sealed class AddCardKeyword : LoadoutCardKeywordModel
{
    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition> Variables =
        CreateDisplayVariables("LoadoutAddCardList", "DYNAMIC_VAR_LOADOUT_ADD_CARD_LIST", LoadoutKeywords.AddCardKey);

    public static AddCardKeyword Instance { get; } = new();

    private AddCardKeyword() { }

    public override CardKeyword Keyword => LoadoutKeywords.AddCard;
    public override string StorageKey => LoadoutKeywords.AddCardKey;
    public override string TitleLocKey => "LOADOUT-ADD_CARD.title";
    public override string? CardTextLocKey => "LOADOUT-ADD_CARD.cardText";
    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars => Variables;

    public override async Task AfterOnPlay(CardModel card, PlayerChoiceContext choiceContext,
        CardPlay cardPlay, object? capturedState)
    {
        foreach (var entry in LoadoutCardKeywordState.GetEffectiveEntries(card, StorageKey))
        {
            if (entry.Amount <= 0 || !LoadoutCardKeywordState.IsSupportedPile(entry.Pile))
                continue;
            if (!LoadoutCardKeywordState.TryResolveCard(entry.CardId, out CardModel canonical))
            {
                LoadoutCardKeywordState.WarnUnknownCard(entry.CardId);
                continue;
            }

            for (int i = 0; i < entry.Amount; i++)
            {
                if (entry.Pile != PileType.Deck
                    && (CombatManager.Instance.IsOverOrEnding || card.CombatState is null))
                    break;

                CardModel generated = entry.Pile == PileType.Deck
                    ? card.Owner.RunState.CreateCard(canonical, card.Owner)
                    : card.CombatState!.CreateCard(canonical, card.Owner);
                if (entry.Upgraded && generated.IsUpgradable)
                    CardCmd.Upgrade(generated);
                LoadoutCardKeywordState.ApplyGeneratedCardCost(generated, entry);
                if (entry.Pile == PileType.Hand)
                {
                    await CardPileCmd.AddGeneratedCardToCombat(generated, entry.Pile, card.Owner);
                }
                else
                {
                    CardCmd.PreviewCardPileAdd(entry.Pile == PileType.Deck
                                        ? await CardPileCmd.Add(generated, PileType.Deck)
                    : await CardPileCmd.AddGeneratedCardToCombat(generated, entry.Pile, card.Owner), 2.2f);
                }
                
            }
        }
    }

    public override IEnumerable<IHoverTip> GetAdditionalCardHoverTips(CardModel card)
    {
        HashSet<(string, bool)> shown = [];
        foreach (var entry in LoadoutCardKeywordState.GetEffectiveEntries(card, StorageKey))
        {
            if (shown.Add((entry.CardId, entry.Upgraded))
                && LoadoutCardKeywordState.TryResolveCard(entry.CardId, out CardModel canonical))
                yield return HoverTipFactory.FromCard(canonical, entry.Upgraded);
        }
    }
}
