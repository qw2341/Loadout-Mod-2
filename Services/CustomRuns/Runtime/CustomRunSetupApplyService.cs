#nullable enable

namespace Loadout.Services.CustomRuns.Runtime;

using System;
using System.Collections.Generic;
using System.Linq;
using Loadout.Services.CustomRuns.Catalog;
using Loadout.Keywords;
using Loadout.Services.CustomRuns.Compilation;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.Loadouts;
using Loadout.Patches.Cards.CardModification;
using Loadout.Services.RelicModification;
using Loadout.Services.Morphing;
using Loadout.Services.PowerGiver;
using Loadout.Services.Targets;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Runs;

public static class CustomRunSetupApplyService
{
    public static void ApplyInitialRuntimeSetup()
    {
        RunState? runState;
        try
        {
            runState = RunManager.Instance.DebugOnlyGetState();
        }
        catch (Exception exception)
        {
            MainFile.Logger.Error($"[Loadout] Could not resolve the launched Custom Run state: {exception}");
            return;
        }
        if (runState is null
            || !CustomRunRuntimeSnapshotService.TryConsumeInitialRuntimeSetup(runState, out ResolvedCustomRunSnapshot snapshot))
            return;

        bool appliedStartingMorph = false;
        foreach (ResolvedPlayerSetup setup in snapshot.Players)
        {
            try
            {
                Dictionary<string, int> powers = setup.StartingPowers
                    .GroupBy(power => power.ModelId, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Sum(power => power.Amount), StringComparer.Ordinal);
                PowerGiverStateService.ReplaceCustomRunPlayerCounters(setup.PlayerId, powers);

                if (!string.IsNullOrWhiteSpace(setup.StartingMorphModelId)
                    && CustomRunCatalogService.TryResolveMorph(setup.StartingMorphModelId, out AbstractModel morph))
                {
                    BottledMonsterMorphService.ApplySynchronizedMorph(
                        morph.Id,
                        LoadoutTargetSelection.ForPlayer(setup.PlayerId));
                    appliedStartingMorph = true;
                }
            }
            catch (Exception exception)
            {
                MainFile.Logger.Error($"[Loadout] Custom Run initial powers/morph failed for player {setup.PlayerId}: {exception}");
            }
        }

        if (appliedStartingMorph)
            BottledMonsterMorphService.SynchronizeAuthoritativeState();
    }

    public static void ApplyToNewPlayer(Player player, ResolvedPlayerSetup setup)
    {
        ApplyStats(player, setup);
        ApplyDeck(player, setup.DeckEntries, setup.DeckModelIds, setup.OverrideDeck);
        ApplyRelics(player, setup.RelicEntries, setup.RelicModelIds, setup.OverrideRelics);
    }

    public static void ApplyPostAscensionSetup(RunState runState)
    {
        if (!CustomRunRuntimeSnapshotService.TryGetSnapshot(runState, out ResolvedCustomRunSnapshot snapshot))
            return;

        foreach (ResolvedPlayerSetup setup in snapshot.Players)
        {
            Player? player = runState.GetPlayer(setup.PlayerId);
            if (player is null)
                continue;
            try
            {
                int retainedPotionCount = setup.PotionSlots ?? player.MaxPotionCount;
                IEnumerable<PotionModel> potionsToDiscard = setup.OverridePotions
                    ? player.Potions.ToList()
                    : player.Potions.Skip(Math.Max(0, retainedPotionCount)).ToList();
                foreach (PotionModel potion in potionsToDiscard)
                    player.DiscardPotionInternal(potion, silent: true);
                ApplyPotionCapacity(player, setup.PotionSlots);
                ApplyPotions(player, setup.PotionModelIds, setup.OverridePotions);
            }
            catch (Exception exception)
            {
                MainFile.Logger.Error($"[Loadout] Custom Run post-ascension potion setup failed for player {setup.PlayerId}: {exception}");
            }
        }
    }

    private static void ApplyStats(Player player, ResolvedPlayerSetup setup)
    {
        if (setup.StartingMaxHp.HasValue)
            player.Creature.SetMaxHpInternal(setup.StartingMaxHp.Value);

        if (setup.StartingCurrentHp.HasValue)
            player.Creature.SetCurrentHpInternal(Math.Min(setup.StartingCurrentHp.Value, player.Creature.MaxHp));
        else if (setup.StartingMaxHp.HasValue && player.Creature.CurrentHp > player.Creature.MaxHp)
            player.Creature.SetCurrentHpInternal(player.Creature.MaxHp);

        if (setup.StartingGold.HasValue)
            player.Gold = setup.StartingGold.Value;
        if (setup.BaseEnergyPerTurn.HasValue)
            player.MaxEnergy = setup.BaseEnergyPerTurn.Value;
    }

    private static void ApplyPotionCapacity(Player player, int? potionSlots)
    {
        if (!potionSlots.HasValue)
            return;
        int target = Math.Max(0, potionSlots.Value);
        int delta = target - player.MaxPotionCount;
        if (delta > 0)
            player.AddToMaxPotionCount(delta);
        else if (delta < 0)
            player.SubtractFromMaxPotionCount(-delta);
    }

    private static void ApplyDeck(
        Player player,
        System.Collections.Generic.IReadOnlyList<SavedCardLoadoutEntry> entries,
        System.Collections.Generic.IReadOnlyList<string> legacyModelIds,
        bool shouldOverride)
    {
        IReadOnlyList<SavedCardLoadoutEntry> effectiveEntries = entries.Count > 0
            ? entries
            : legacyModelIds.Select(id => new SavedCardLoadoutEntry { ModelId = id }).ToList();
        if (!shouldOverride)
            return;

        List<CardModel> cards = [];
        foreach (SavedCardLoadoutEntry entry in effectiveEntries)
        {
            CardModel? canonical = Resolve<CardModel>(SelectionModelKind.Card, entry.ModelId);
            if (canonical is null)
                break;

            for (int copy = 0; copy < Math.Max(1, entry.Count); copy++)
            {
                CardModel card = canonical.ToMutable();
                for (int upgrade = 0; upgrade < entry.UpgradeLevel && card.IsUpgradable; upgrade++)
                {
                    card.UpgradeInternal();
                    card.FinalizeUpgradeInternal();
                }
                CardModificationRuntime.ApplyCustomRunStartingState(card, entry.ModificationState);
                cards.Add(card);
            }
        }
        if (cards.Count != effectiveEntries.Sum(entry => Math.Max(1, entry.Count)))
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
        LoadoutKeywordRuntimePatches.Reconcile();
    }

    private static void ApplyRelics(
        Player player,
        System.Collections.Generic.IReadOnlyList<SavedRelicLoadoutEntry> entries,
        System.Collections.Generic.IReadOnlyList<string> legacyModelIds,
        bool shouldOverride)
    {
        IReadOnlyList<SavedRelicLoadoutEntry> effectiveEntries = entries.Count > 0
            ? entries
            : legacyModelIds.Select(id => new SavedRelicLoadoutEntry { ModelId = id }).ToList();
        if (!shouldOverride)
            return;

        List<(RelicModel Relic, SavedRelicLoadoutEntry Entry)> relics = [];
        foreach (SavedRelicLoadoutEntry entry in effectiveEntries)
        {
            RelicModel? canonical = Resolve<RelicModel>(SelectionModelKind.Relic, entry.ModelId);
            if (canonical is null)
                break;
            for (int copy = 0; copy < Math.Max(1, entry.Count); copy++)
                relics.Add((canonical.ToMutable(), entry));
        }
        if (relics.Count != effectiveEntries.Sum(entry => Math.Max(1, entry.Count)))
        {
            MainFile.Logger.Error($"[Loadout] Custom Run relic setup for player {player.NetId} contained an unresolved relic.");
            return;
        }

        foreach (RelicModel relic in player.Relics.ToList())
            player.RemoveRelicInternal(relic, silent: true);
        foreach ((RelicModel relic, SavedRelicLoadoutEntry entry) in relics)
        {
            relic.FloorAddedToDeck = 1;
            SaveManager.Instance.MarkRelicAsSeen(relic);
            RelicModificationStateService.ApplyPermanentToRelic(relic);
            if (entry.ModificationState is not null)
                RelicModificationStateService.ApplyLoadoutTemporaryState(relic, entry.ModificationState);
            player.AddRelicInternal(relic, -1, silent: true);
        }
    }

    private static void ApplyPotions(
        Player player,
        System.Collections.Generic.IReadOnlyList<string> modelIds,
        bool shouldOverride)
    {
        if (!shouldOverride)
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
