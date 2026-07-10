#nullable enable

namespace Loadout.Services.Loadouts;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using Loadout.Services.Actions;
using Loadout.Services.CardModification;
using Loadout.Services.Targets;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

public static class LoadoutApplyService
{
    private static readonly FieldInfo? CardPileCardsField = AccessTools.Field(typeof(CardPile), "_cards");

    public static event Action<string>? WarningRaised;

    public static bool RequestApply(SavedLoadout loadout, LoadoutTargetSelection target)
    {
        Player? localPlayer = GetLocalRunPlayer();
        if (localPlayer is null)
            return false;

        LoadoutTargetSelection effectiveTarget = ForceClientTargetToSelf(target, localPlayer);
        string encodedPayload = LoadoutSerializationService.Encode(loadout);
        return LoadoutImmediateMutationService.RequestApplyLoadout(loadout.Kind, encodedPayload, effectiveTarget);
    }

    internal static void ApplyImmediate(
        LoadoutKind kind,
        string encodedPayload,
        LoadoutTargetSelection target,
        ulong requesterNetId)
    {
        if (!LoadoutSerializationService.TryDecode(encodedPayload, out SavedLoadout loadout, out string error))
        {
            Warn($"Loadout: immediate loadout apply failed. {error}");
            return;
        }

        Player? requester = GetRunPlayer(requesterNetId) ?? GetLocalRunPlayer();
        if (requester is null)
            return;

        loadout.Kind = kind;
        LoadoutTargetSelection safeTarget = GetServerSafeTarget(target, requester);
        IReadOnlyList<Player> targets = ResolveTargetPlayers(safeTarget, requester);
        HashSet<ulong> changedCardPlayers = [];
        foreach (Player targetPlayer in targets)
        {
            if (!loadout.HasCards)
                continue;

            bool isStartingDeck = loadout.SpecialPreset == LoadoutSpecialPreset.StartingDeck;
            IReadOnlyList<SavedCardLoadoutEntry> cardEntries = isStartingDeck
                ? CreateStartingDeckEntries(targetPlayer)
                : loadout.Cards;
            if (ReplaceCardsDirect(targetPlayer, cardEntries, isStartingDeck))
                changedCardPlayers.Add(targetPlayer.NetId);
        }

        if (changedCardPlayers.Count > 0)
            LoadoutRunContentChangeService.Notify(LoadoutRunContentKind.Cards, changedCardPlayers, LoadoutRunContentChangeMode.Replace);

        if (loadout.HasRelics)
            _ = ReplaceRelicsAsync(targets, loadout.Relics);
    }

    internal static void Warn(string message)
    {
        GD.PushWarning(message);

        try
        {
            WarningRaised?.Invoke(message);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Loadout: warning handler failed. {exception.Message}");
        }
    }

    private static bool ReplaceCardsDirect(
        Player targetPlayer,
        IReadOnlyList<SavedCardLoadoutEntry> entries,
        bool isStartingDeck = false)
    {
        if (!TryGetDeckBackingList(targetPlayer, out List<CardModel> deckCards))
        {
            Warn($"Loadout: failed replacing deck for player {targetPlayer.NetId}. Could not access deck backing list.");
            return false;
        }

        List<CardModel> oldCards = deckCards.ToList();
        List<CardModel> newCards = [];
        Dictionary<CardModel, CardModificationState> stateByCard = new(ReferenceEqualityComparer.Instance);

        foreach (SavedCardLoadoutEntry entry in entries)
        {
            CardModel? canonical = LoadoutSerializationService.ResolveCard(entry.ModelId);
            if (canonical is null)
            {
                Warn($"Loadout: skipped unknown card '{entry.ModelId}'.");
                continue;
            }

            int count = Math.Max(1, entry.Count);
            for (int i = 0; i < count; i++)
            {
                CardModel? card = CreateDeckCardForLoadout(targetPlayer, canonical, entry.UpgradeLevel, isStartingDeck);
                if (card is null)
                    continue;

                newCards.Add(card);
                if (entry.ModificationState is not null && !entry.ModificationState.IsEmpty)
                    stateByCard[card] = entry.ModificationState.Clone();
            }
        }

        try
        {
            ReplacePlayerDeckDirect(targetPlayer, deckCards, oldCards, newCards);
            CardModificationStateService.ReplaceTemporaryStatesForPlayer(targetPlayer, stateByCard);
            return oldCards.Count > 0 || newCards.Count > 0;
        }
        catch (Exception exception)
        {
            Warn($"Loadout: failed replacing deck for player {targetPlayer.NetId}. {exception.Message}");
            CardModificationStateService.ReplaceTemporaryStatesForPlayer(
                targetPlayer,
                new Dictionary<CardModel, CardModificationState>(ReferenceEqualityComparer.Instance));
            return oldCards.Count > 0;
        }
    }

    private static IReadOnlyList<SavedCardLoadoutEntry> CreateStartingDeckEntries(Player targetPlayer)
    {
        return targetPlayer.Character.StartingDeck
            .Select(card => new SavedCardLoadoutEntry
            {
                ModelId = card.Id.ToString(),
                UpgradeLevel = 0,
                Count = 1
            })
            .ToList();
    }

    private static void ReplacePlayerDeckDirect(
        Player targetPlayer,
        List<CardModel> deckCards,
        IReadOnlyList<CardModel> oldCards,
        IReadOnlyList<CardModel> newCards)
    {
        foreach (CardModel oldCard in oldCards)
        {
            try
            {
                targetPlayer.RunState.RemoveCard(oldCard);
                oldCard.HasBeenRemovedFromState = true;
            }
            catch (Exception exception)
            {
                Warn($"Loadout: failed unregistering old card '{oldCard.Id}' from player {targetPlayer.NetId}. {exception.Message}");
            }
        }

        deckCards.Clear();
        deckCards.AddRange(newCards);
        targetPlayer.Deck.InvokeContentsChanged();
        targetPlayer.Deck.InvokeCardRemoveFinished();
        targetPlayer.Deck.InvokeCardAddFinished();
    }

    private static CardModel? CreateDeckCardForLoadout(
        Player targetPlayer,
        CardModel canonical,
        int upgradeLevel,
        bool isStartingDeck)
    {
        try
        {
            CardModel card = targetPlayer.RunState.CreateCard(canonical, targetPlayer);
            if (isStartingDeck)
                card.FloorAddedToDeck = 1;
            ApplyLoadoutUpgradeLevelDirect(card, upgradeLevel);
            CardModificationStateService.ApplyPermanentToCard(card);
            return card;
        }
        catch (Exception exception)
        {
            Warn($"Loadout: failed creating card '{canonical.Id}'. {exception.Message}");
            return null;
        }
    }

    private static void ApplyLoadoutUpgradeLevelDirect(CardModel card, int upgradeLevel)
    {
        int upgrades = Math.Min(Math.Max(0, upgradeLevel), Math.Max(0, card.MaxUpgradeLevel));
        for (int i = 0; i < upgrades && card.IsUpgradable; i++)
        {
            card.UpgradeInternal();
            card.FinalizeUpgradeInternal();
        }
    }

    private static bool TryGetDeckBackingList(Player targetPlayer, out List<CardModel> deckCards)
    {
        deckCards = null!;
        try
        {
            if (CardPileCardsField?.GetValue(targetPlayer.Deck) is List<CardModel> cards)
            {
                deckCards = cards;
                return true;
            }
        }
        catch (Exception exception)
        {
            Warn($"Loadout: failed reading deck backing list for player {targetPlayer.NetId}. {exception.Message}");
        }

        return false;
    }

    private static async Task ReplaceRelicsAsync(
        IReadOnlyList<Player> targets,
        IReadOnlyList<SavedRelicLoadoutEntry> entries)
    {
        HashSet<ulong> changedRelicPlayers = [];
        foreach (Player targetPlayer in targets)
        {
            if (await ReplaceRelics(targetPlayer, entries))
                changedRelicPlayers.Add(targetPlayer.NetId);
        }

        if (changedRelicPlayers.Count > 0)
            LoadoutRunContentChangeService.Notify(LoadoutRunContentKind.Relics, changedRelicPlayers);
    }

    private static async Task<bool> ReplaceRelics(Player targetPlayer, IReadOnlyList<SavedRelicLoadoutEntry> entries)
    {
        bool changed = targetPlayer.Relics.Count > 0;
        foreach (RelicModel relic in targetPlayer.Relics.ToList())
        {
            try
            {
                await RelicCmd.Remove(relic);
            }
            catch (Exception exception)
            {
                Warn($"Loadout: failed removing relic '{relic.Id}' from player {targetPlayer.NetId}. {exception.Message}");
            }
        }

        foreach (SavedRelicLoadoutEntry entry in entries)
        {
            RelicModel? canonical = LoadoutSerializationService.ResolveRelic(entry.ModelId);
            if (canonical is null)
            {
                Warn($"Loadout: skipped unknown relic '{entry.ModelId}'.");
                continue;
            }

            int count = Math.Max(1, entry.Count);
            for (int i = 0; i < count; i++)
            {
                try
                {
                    await RelicCmd.Obtain(canonical.ToMutable(), targetPlayer);
                    changed = true;
                }
                catch (Exception exception)
                {
                    Warn($"Loadout: stopped adding relic '{entry.ModelId}' to player {targetPlayer.NetId} after {i}/{count}. {exception.Message}");
                    break;
                }
            }
        }

        return changed;
    }

    private static IReadOnlyList<Player> ResolveTargetPlayers(LoadoutTargetSelection target, Player requester)
    {
        IReadOnlyList<Player> players = LoadoutTargetService.ResolvePlayers(target, requester.RunState);
        return players.Count > 0 ? players : [requester];
    }

    private static LoadoutTargetSelection GetServerSafeTarget(LoadoutTargetSelection target, Player requester)
    {
        try
        {
            if (RunManager.Instance.NetService.Type == NetGameType.Host
                && requester.NetId != RunManager.Instance.NetService.NetId)
            {
                return LoadoutTargetSelection.ForPlayer(requester.NetId);
            }
        }
        catch
        {
            // Singleplayer and replay services do not need target hardening here.
        }

        return target;
    }

    private static LoadoutTargetSelection ForceClientTargetToSelf(LoadoutTargetSelection target, Player localPlayer)
    {
        try
        {
            return RunManager.Instance.NetService.Type == NetGameType.Client
                ? LoadoutTargetSelection.ForPlayer(localPlayer.NetId)
                : target;
        }
        catch
        {
            return target;
        }
    }

    private static Player? GetRunPlayer(ulong netId)
    {
        try
        {
            if (!RunManager.Instance.IsInProgress || netId == 0)
                return null;

            return RunManager.Instance.DebugOnlyGetState()?.GetPlayer(netId);
        }
        catch
        {
            return null;
        }
    }

    private static Player? GetLocalRunPlayer()
    {
        try
        {
            return RunManager.Instance.IsInProgress
                ? LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState())
                : null;
        }
        catch (Exception exception)
        {
            Warn($"Loadout: could not resolve local player for loadout apply. {exception.Message}");
            return null;
        }
    }
}
