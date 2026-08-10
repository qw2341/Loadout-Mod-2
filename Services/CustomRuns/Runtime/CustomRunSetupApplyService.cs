#nullable enable

namespace Loadout.Services.CustomRuns.Runtime;

using System;
using System.Linq;
using Loadout.Services.CustomRuns.Catalog;
using Loadout.Services.CustomRuns.Compilation;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.RelicModification;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;

public static class CustomRunSetupApplyService
{
    public static void ApplyToNewPlayer(Player player, ResolvedPlayerSetup setup)
    {
        ApplyStats(player, setup);
        ApplyDeck(player, setup.DeckModelIds);
        ApplyRelics(player, setup.RelicModelIds);
        ApplyPotions(player, setup.PotionModelIds);
    }

    private static void ApplyStats(Player player, ResolvedPlayerSetup setup)
    {
        if (setup.StartingMaxHp.HasValue)
            player.Creature.SetMaxHpInternal(setup.StartingMaxHp.Value);

        if (setup.StartingCurrentHp.HasValue)
            player.Creature.SetCurrentHpInternal(Math.Min(setup.StartingCurrentHp.Value, player.Creature.MaxHp));
        else if (setup.StartingMaxHp.HasValue && player.Creature.CurrentHp > player.Creature.MaxHp)
            player.Creature.SetCurrentHpInternal(player.Creature.MaxHp);

        if (setup.PotionSlots.HasValue)
        {
            int target = Math.Max(0, setup.PotionSlots.Value);
            int delta = target - player.MaxPotionCount;
            if (delta > 0)
                player.AddToMaxPotionCount(delta);
            else if (delta < 0)
                player.SubtractFromMaxPotionCount(-delta);
        }

        if (setup.StartingGold.HasValue)
            player.Gold = setup.StartingGold.Value;
        if (setup.BaseEnergyPerTurn.HasValue)
            player.MaxEnergy = setup.BaseEnergyPerTurn.Value;
    }

    private static void ApplyDeck(Player player, System.Collections.Generic.IReadOnlyList<string> modelIds)
    {
        if (modelIds.Count == 0)
            return;

        CardModel[] cards = modelIds
            .Select(id => Resolve<CardModel>(SelectionModelKind.Card, id))
            .Where(model => model is not null)
            .Select(model => model!.ToMutable())
            .ToArray();
        if (cards.Length != modelIds.Count)
        {
            MainFile.Logger.Error($"[Loadout] Custom Run deck setup for player {player.NetId} contained an unresolved card.");
            return;
        }

        player.Deck.Clear(silent: true);
        foreach (CardModel card in cards)
        {
            card.FloorAddedToDeck = 1;
            player.Deck.AddInternal(card, -1, silent: true);
        }
    }

    private static void ApplyRelics(Player player, System.Collections.Generic.IReadOnlyList<string> modelIds)
    {
        if (modelIds.Count == 0)
            return;

        RelicModel[] relics = modelIds
            .Select(id => Resolve<RelicModel>(SelectionModelKind.Relic, id))
            .Where(model => model is not null)
            .Select(model => model!.ToMutable())
            .ToArray();
        if (relics.Length != modelIds.Count)
        {
            MainFile.Logger.Error($"[Loadout] Custom Run relic setup for player {player.NetId} contained an unresolved relic.");
            return;
        }

        foreach (RelicModel relic in player.Relics.ToList())
            player.RemoveRelicInternal(relic, silent: true);
        foreach (RelicModel relic in relics)
        {
            relic.FloorAddedToDeck = 1;
            SaveManager.Instance.MarkRelicAsSeen(relic);
            RelicModificationStateService.ApplyPermanentToRelic(relic);
            player.AddRelicInternal(relic, -1, silent: true);
        }
    }

    private static void ApplyPotions(Player player, System.Collections.Generic.IReadOnlyList<string> modelIds)
    {
        if (modelIds.Count == 0)
            return;

        PotionModel[] potions = modelIds
            .Select(id => Resolve<PotionModel>(SelectionModelKind.Potion, id))
            .Where(model => model is not null)
            .Select(model => model!.ToMutable())
            .ToArray();
        if (potions.Length != modelIds.Count)
        {
            MainFile.Logger.Error($"[Loadout] Custom Run potion setup for player {player.NetId} contained an unresolved potion.");
            return;
        }

        foreach (PotionModel potion in player.Potions.ToList())
            player.DiscardPotionInternal(potion, silent: true);
        foreach (PotionModel potion in potions)
        {
            if (!player.AddPotionInternal(potion, -1, silent: true).success)
            {
                MainFile.Logger.Error($"[Loadout] Custom Run could not place starting potion '{potion.Id}' for player {player.NetId}.");
                return;
            }
        }
    }

    private static TModel? Resolve<TModel>(SelectionModelKind kind, string modelId)
        where TModel : AbstractModel
    {
        return CustomRunCatalogService.TryResolve(kind, modelId, out CustomRunCatalogEntry entry)
            ? entry.Model as TModel
            : null;
    }
}
