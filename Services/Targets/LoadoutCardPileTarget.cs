#nullable enable

namespace Loadout.Services.Targets;

using System;
using System.Collections.Generic;
using System.Linq;
using Loadout.UI.Managers;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

public enum LoadoutCardPileTarget : byte
{
    Unspecified = 0,
    HandAndDeck = 1,
    Deck = 2,
    Hand = 3,
    Draw = 4,
    Discard = 5,
    Exhaust = 6
}

public static class LoadoutCardPileTargets
{
    public static readonly IReadOnlyList<LoadoutCardPileTarget> PrinterOptions =
    [
        LoadoutCardPileTarget.HandAndDeck,
        LoadoutCardPileTarget.Deck,
        LoadoutCardPileTarget.Hand,
        LoadoutCardPileTarget.Draw,
        LoadoutCardPileTarget.Discard,
        LoadoutCardPileTarget.Exhaust
    ];

    public static readonly IReadOnlyList<LoadoutCardPileTarget> OwnedCardOptions =
    [
        LoadoutCardPileTarget.Deck,
        LoadoutCardPileTarget.Hand,
        LoadoutCardPileTarget.Draw,
        LoadoutCardPileTarget.Discard,
        LoadoutCardPileTarget.Exhaust
    ];

    public static LoadoutCardPileTarget NormalizeForCreation(this LoadoutCardPileTarget target)
    {
        return target == LoadoutCardPileTarget.Unspecified
            ? LoadoutCardPileTarget.HandAndDeck
            : target;
    }

    public static LoadoutCardPileTarget NormalizeForOwnedCard(this LoadoutCardPileTarget target)
    {
        return target == LoadoutCardPileTarget.Unspecified
            ? LoadoutCardPileTarget.Deck
            : target;
    }

    public static bool IsSupportedCreationTarget(LoadoutCardPileTarget target)
    {
        return target is LoadoutCardPileTarget.HandAndDeck
            or LoadoutCardPileTarget.Deck
            or LoadoutCardPileTarget.Hand
            or LoadoutCardPileTarget.Draw
            or LoadoutCardPileTarget.Discard
            or LoadoutCardPileTarget.Exhaust;
    }

    public static bool IsSupportedOwnedTarget(LoadoutCardPileTarget target)
    {
        return target is LoadoutCardPileTarget.Deck
            or LoadoutCardPileTarget.Hand
            or LoadoutCardPileTarget.Draw
            or LoadoutCardPileTarget.Discard
            or LoadoutCardPileTarget.Exhaust;
    }

    public static bool IsCombatPile(this LoadoutCardPileTarget target)
    {
        return target is LoadoutCardPileTarget.Hand
            or LoadoutCardPileTarget.Draw
            or LoadoutCardPileTarget.Discard
            or LoadoutCardPileTarget.Exhaust;
    }

    public static bool TryGetPileType(this LoadoutCardPileTarget target, out PileType pileType)
    {
        pileType = target switch
        {
            LoadoutCardPileTarget.Deck => PileType.Deck,
            LoadoutCardPileTarget.Hand => PileType.Hand,
            LoadoutCardPileTarget.Draw => PileType.Draw,
            LoadoutCardPileTarget.Discard => PileType.Discard,
            LoadoutCardPileTarget.Exhaust => PileType.Exhaust,
            _ => PileType.None
        };
        return pileType != PileType.None;
    }

    public static LoadoutCardPileTarget FromPileType(PileType pileType)
    {
        return pileType switch
        {
            PileType.Deck => LoadoutCardPileTarget.Deck,
            PileType.Hand => LoadoutCardPileTarget.Hand,
            PileType.Draw => LoadoutCardPileTarget.Draw,
            PileType.Discard => LoadoutCardPileTarget.Discard,
            PileType.Exhaust => LoadoutCardPileTarget.Exhaust,
            _ => LoadoutCardPileTarget.Unspecified
        };
    }

    public static string ToOptionId(this LoadoutCardPileTarget target)
    {
        return ((byte)target).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static bool TryParseOptionId(string value, out LoadoutCardPileTarget target)
    {
        if (byte.TryParse(value, out byte raw)
            && Enum.IsDefined(typeof(LoadoutCardPileTarget), raw))
        {
            target = (LoadoutCardPileTarget)raw;
            return true;
        }

        target = LoadoutCardPileTarget.Unspecified;
        return false;
    }

    public static LoadoutDropdownOption ToDropdownOption(this LoadoutCardPileTarget target)
    {
        return new LoadoutDropdownOption(target.ToOptionId(), target switch
        {
            LoadoutCardPileTarget.HandAndDeck => LocMan.Loc("CARD_PILE_HAND_AND_DECK", "Hand + Deck"),
            LoadoutCardPileTarget.Deck => LocMan.Loc("CARD_PILE_DECK", "Deck"),
            LoadoutCardPileTarget.Hand => LocMan.Loc("CARD_PILE_HAND", "Hand"),
            LoadoutCardPileTarget.Draw => LocMan.Loc("CARD_PILE_DRAW", "Draw Pile"),
            LoadoutCardPileTarget.Discard => LocMan.Loc("CARD_PILE_DISCARD", "Discard Pile"),
            LoadoutCardPileTarget.Exhaust => LocMan.Loc("CARD_PILE_EXHAUST", "Exhaust Pile"),
            _ => LocMan.Loc("CARD_PILE_DECK", "Deck")
        });
    }

    public static IReadOnlyList<LoadoutOwnedItem<CardModel>> BuildOwnedCards(
        LoadoutTargetSelection selection,
        LoadoutCardPileTarget target)
    {
        RunState? runState;
        try
        {
            runState = RunManager.Instance.IsInProgress
                ? RunManager.Instance.DebugOnlyGetState()
                : null;
        }
        catch
        {
            runState = null;
        }

        if (runState is null)
            return [];

        target = target.NormalizeForOwnedCard();
        if (!target.TryGetPileType(out PileType pileType))
            return [];

        List<LoadoutOwnedItem<CardModel>> items = [];
        foreach (Player player in LoadoutTargetService.ResolvePlayers(selection, runState))
        {
            CardPile pile = pileType.GetPile(player);
            for (int index = 0; index < pile.Cards.Count; index++)
            {
                CardModel card = pile.Cards[index];
                int nativeIndex = pileType == PileType.Deck
                    ? checked((int)NetDeckCard.FromModel(card).DeckIndex)
                    : index;
                uint? combatCardId = null;
                if (pileType != PileType.Deck
                    && NetCombatCardDb.Instance.TryGetCardId(card, out uint id))
                {
                    combatCardId = id;
                }

                items.Add(new LoadoutOwnedItem<CardModel>(
                    player,
                    nativeIndex,
                    card,
                    pileType,
                    combatCardId));
            }
        }

        return items;
    }

    public static IEnumerable<CardPile> ResolveObservedPiles(
        LoadoutTargetSelection selection,
        LoadoutCardPileTarget target)
    {
        if (!target.NormalizeForOwnedCard().TryGetPileType(out PileType pileType))
            return [];

        RunState? runState;
        try
        {
            runState = RunManager.Instance.IsInProgress
                ? RunManager.Instance.DebugOnlyGetState()
                : null;
        }
        catch
        {
            return [];
        }

        if (runState is null)
            return [];

        return LoadoutTargetService.ResolvePlayers(selection, runState)
            .Select(player => pileType.GetPile(player))
            .ToArray();
    }

    public static bool IsCombatInProgress()
    {
        try
        {
            return CombatManager.Instance.IsInProgress;
        }
        catch
        {
            return false;
        }
    }
}
