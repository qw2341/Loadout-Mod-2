#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using System.Text.Json;
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
                LoadoutCardKeywordState.ApplyGeneratedCardKeywords(generated, entry);
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
        foreach (IHoverTip tip in base.GetAdditionalCardHoverTips(card))
            yield return tip;
        HashSet<(string, bool, string?)> shown = [];
        foreach (var entry in LoadoutCardKeywordState.GetEffectiveEntries(card, StorageKey))
        {
            string? keywordState = entry.GrantedKeyword is { IsEmpty: false } extra
                ? JsonSerializer.Serialize(extra) : null;
            if (!shown.Add((entry.CardId, entry.Upgraded, keywordState))
                || !LoadoutCardKeywordState.TryResolveCard(entry.CardId, out CardModel canonical))
                continue;

            CardHoverTip preview = (CardHoverTip)HoverTipFactory.FromCard(canonical, entry.Upgraded);
            if (keywordState is not null)
            {
                LoadoutCardKeywordState.ApplyGeneratedCardKeywords(preview.Card, entry);
                preview = new GeneratedCardHoverTip(preview.Card, keywordState);
            }
            yield return preview;
        }
    }

    public sealed class GeneratedCardHoverTip : CardHoverTip, IHoverTip
    {
        public new string Id { get; }

        public GeneratedCardHoverTip(CardModel card, string keywordState) : base(card)
        {
            Id = $"LOADOUT_ADD_CARD:{base.Id}:{keywordState}";
        }
    }
}
