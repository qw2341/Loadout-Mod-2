#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Loadout.PanelItems;
using Loadout.Services.CardModification;
using Loadout.UI.Managers;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;

public sealed class AddRandomCardKeyword : LoadoutCardKeywordModel
{
    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition> Variables =
        CreateDisplayVariables("LoadoutAddRandomCardList", "DYNAMIC_VAR_LOADOUT_ADD_RANDOM_CARD_LIST", LoadoutKeywords.AddRandomCardKey);

    public static AddRandomCardKeyword Instance { get; } = new();

    private AddRandomCardKeyword() { }

    public override CardKeyword Keyword => LoadoutKeywords.AddRandomCard;
    public override string StorageKey => LoadoutKeywords.AddRandomCardKey;
    public override string TitleLocKey => "LOADOUT-ADD_RANDOM_CARD.title";
    public override string? CardTextLocKey => "LOADOUT-ADD_RANDOM_CARD.cardText";
    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars => Variables;

    public override LoadoutCardKeywordEntry CreateDefaultEntry() => new()
    {
        KeywordKey = StorageKey,
        PoolId = ModelDb.CardPool<ColorlessCardPool>().Id.ToString()
    };

    public static IEnumerable<CardModel> GetCandidates(Player player, LoadoutCardKeywordEntry entry) =>
        ModelDb.AllCardPools
            .Where(pool => string.IsNullOrEmpty(entry.PoolId)
                || string.Equals(pool.Id.ToString(), entry.PoolId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(pool => pool.Id.ToString(), StringComparer.Ordinal)
            .SelectMany(pool => pool.GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint))
            .Where(candidate => entry.Rarity == CardRarity.None || candidate.Rarity == entry.Rarity);

    public override async Task AfterOnPlay(CardModel card, PlayerChoiceContext choiceContext,
        CardPlay cardPlay, object? capturedState)
    {
        foreach (var entry in LoadoutCardKeywordState.GetEffectiveEntries(card, StorageKey))
        {
            if (entry.Amount <= 0 || !LoadoutCardKeywordState.IsSupportedPile(entry.Pile))
                continue;
            if (entry.Pile != PileType.Deck
                && (CombatManager.Instance.IsOverOrEnding || card.CombatState is null))
                continue;

            var candidates = GetCandidates(card.Owner, entry);
            var rng = card.Owner.RunState.Rng.CombatCardGeneration;
            var generatedCards = entry.Pile == PileType.Deck
                ? CardFactory.FilterForCombat(candidates).TakeRandom(entry.Amount, rng)
                    .Select(candidate => card.Owner.RunState.CreateCard(candidate, card.Owner))
                : CardFactory.GetDistinctForCombat(card.Owner, candidates, entry.Amount, rng);
            using var generated = generatedCards.GetEnumerator();
            while (entry.Pile == PileType.Deck
                || !(CombatManager.Instance.IsOverOrEnding || card.CombatState is null))
            {
                if (!generated.MoveNext())
                    break;
                LoadoutCardKeywordState.ApplyGeneratedCardCost(generated.Current, entry);
                if (entry.Pile == PileType.Hand)
                    await CardPileCmd.AddGeneratedCardToCombat(generated.Current, entry.Pile, card.Owner);
                else
                    CardCmd.PreviewCardPileAdd(entry.Pile == PileType.Deck
                        ? await CardPileCmd.Add(generated.Current, PileType.Deck)
                        : await CardPileCmd.AddGeneratedCardToCombat(generated.Current, entry.Pile, card.Owner), 2.2f);
            }
        }
    }

    public static string FormatEntry(LoadoutCardKeywordEntry entry, string amount)
    {
        List<string> descriptors = [];
        if (entry.Rarity != CardRarity.None)
            descriptors.Add(CardPrinter.GetCardRarityLabel(entry.Rarity));
        if (!string.IsNullOrEmpty(entry.PoolId))
        {
            var pool = ModelDb.AllCardPools.FirstOrDefault(candidate =>
                string.Equals(candidate.Id.ToString(), entry.PoolId, StringComparison.OrdinalIgnoreCase));
            string label = pool is null ? entry.PoolId : CommonHelpers.GetPoolLabel(pool);
            if (label.StartsWith("The ", StringComparison.Ordinal))
                label = label[4..];
            descriptors.Add(label);
        }
        bool singular = entry.Amount == 1;
        return descriptors.Count == 0
            ? LocMan.Loc(singular ? "CARD_MOD_RANDOM_CARD_PLAIN_SINGLE" : "CARD_MOD_RANDOM_CARD_PLAIN_PLURAL",
                singular ? "{0} random card" : "{0} random cards", amount)
            : LocMan.Loc(singular ? "CARD_MOD_RANDOM_CARD_SINGLE" : "CARD_MOD_RANDOM_CARD_PLURAL",
                singular ? "{0} random {1} card" : "{0} random {1} cards", amount,
                descriptors.Count == 1 ? descriptors[0]
                    : LocMan.Loc("CARD_MOD_RANDOM_CARD_DESCRIPTORS", "{0} {1}", descriptors[0], descriptors[1]));
    }
}
