#nullable enable

namespace Loadout.Patches.Cards;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Loadout.Keywords;
using Loadout.Services.Actions;
using Loadout.Services.RelicModification;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

[HarmonyPatch(typeof(AbstractModel), nameof(AbstractModel.MutableClone))]
public static class CardModelMutableCloneModificationPatch
{
    [HarmonyPostfix]
    public static void Postfix(AbstractModel __instance, AbstractModel __result)
    {
        if (__instance is not CardModel source || __result is not CardModel clone || clone.IsCanonical)
            return;

        CardModificationPatcher.CopyRuntimeStateToClone(source, clone);
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.ToSerializable))]
public static class CardModelToSerializableModificationPatch
{
    [HarmonyPostfix]
    public static void Postfix(CardModel __instance, SerializableCard __result)
    {
        CardModificationFields.ExportTemporaryToSave(__instance, __result);
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.FromSerializable), typeof(SerializableCard))]
public static class CardModelFromSerializableModificationPatch
{
    [HarmonyPostfix]
    public static void Postfix(SerializableCard save, CardModel __result)
    {
        CardModificationFields.ImportTemporaryFromSave(__result, save);
        CardModificationPatcher.ApplyLoadedStateToCard(__result);
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.Title), MethodType.Getter)]
public static class CardModelTitleModificationPatch
{
    [HarmonyPostfix]
    public static void Postfix(CardModel __instance, ref LocString __result)
    {
        if (CardModificationFields.TryGetCustomTitle(__instance, out string title))
            __result = CardModificationFields.GetOrCreateTitleLocString(__instance, __result, title);
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.GetDescriptionForPile), typeof(PileType), typeof(Creature))]
public static class CardModelDescriptionModificationPatch
{
    [HarmonyPrefix]
    public static bool Prefix(CardModel __instance, ref string __result)
    {
        if (!CardModificationFields.TryGetCustomDescription(__instance, out string description))
            return true;
        __result = description;
        return false;
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.GetDescriptionForUpgradePreview))]
public static class CardModelUpgradeDescriptionModificationPatch
{
    [HarmonyPrefix]
    public static bool Prefix(CardModel __instance, ref string __result)
    {
        if (!CardModificationFields.TryGetCustomDescription(__instance, out string description))
            return true;
        __result = description;
        return false;
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.PortraitPath), MethodType.Getter)]
public static class CardModelPortraitModificationPatch
{
    [HarmonyPostfix]
    public static void Postfix(CardModel __instance, ref string __result)
    {
        if (CardModificationFields.TryGetPortrait(__instance, beta: false, out string path))
            __result = path;
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.BetaPortraitPath), MethodType.Getter)]
public static class CardModelBetaPortraitModificationPatch
{
    [HarmonyPostfix]
    public static void Postfix(CardModel __instance, ref string __result)
    {
        if (CardModificationFields.TryGetPortrait(__instance, beta: true, out string path))
            __result = path;
    }
}

[HarmonyPatch(typeof(CardCmd), nameof(CardCmd.Upgrade), typeof(IEnumerable<CardModel>), typeof(CardPreviewStyle))]
public static class CardCmdUpgradeCardModificationPatch
{
    [HarmonyPrefix]
    public static void Prefix(ref IEnumerable<CardModel> cards, out List<CardModel> __state)
    {
        List<CardModel> materializedCards = cards?.Where(card => card is not null).ToList() ?? [];
        __state = materializedCards.Where(card => card.IsUpgradable).ToList();
        cards = materializedCards;
    }

    [HarmonyPostfix]
    public static void Postfix(List<CardModel> __state)
    {
        CardModificationPatcher.ReapplyEffectiveStateAfterUpgrade(__state);
        if (__state.Count == 0)
            return;

        HashSet<ulong> changedPlayers = [];
        List<LoadoutChangedCard> changedCards = [];
        foreach (IGrouping<Player, CardModel> group in __state
                     .Where(card => card.Owner is not null && card.Pile?.Type == PileType.Deck)
                     .GroupBy(card => card.Owner!))
        {
            Player owner = group.Key;
            Dictionary<CardModel, int> deckIndices = new(ReferenceEqualityComparer.Instance);
            IReadOnlyList<CardModel> deck = owner.Deck.Cards;
            for (int index = 0; index < deck.Count; index++)
                deckIndices[deck[index]] = index;

            foreach (CardModel card in group)
            {
                if (!deckIndices.TryGetValue(card, out int index))
                    continue;

                CardModificationState state = CardModificationPatcher.GetEffectiveStateForCard(card);
                LoadoutCardVisualRefreshKind refreshKind = CardModificationPatcher.GetVisualRefreshKind(
                    new CardModificationState(),
                    state);
                changedPlayers.Add(owner.NetId);
                changedCards.Add(new LoadoutChangedCard(owner.NetId, index, card.Id, refreshKind));
            }
        }

        if (changedCards.Count > 0)
        {
            LoadoutRunContentChangeService.Queue(
                LoadoutRunContentKind.Cards,
                changedPlayers,
                LoadoutRunContentChangeMode.Update,
                changedCards);
        }
    }
}

[HarmonyPatch(
    typeof(CardPileCmd),
    nameof(CardPileCmd.RemoveFromDeck),
    typeof(IReadOnlyList<CardModel>),
    typeof(bool))]
public static class CardPileRemoveCardModificationPatch
{
    public sealed record RemovedCardState(CardModel Card, LoadoutChangedCard Change);

    public sealed record RemovalState(
        IReadOnlyList<CardModel> InevitableCards,
        IReadOnlyList<RemovedCardState> RemovedCards);

    [HarmonyPrefix]
    public static void Prefix(
        IReadOnlyList<CardModel> cards,
        out RemovalState __state)
    {
        Dictionary<Player, Dictionary<CardModel, int>> indexMaps = new();
        foreach (Player owner in cards
                     .Where(card => card?.Owner is not null)
                     .Select(card => card.Owner)
                     .Distinct())
        {
            Dictionary<CardModel, int> map = new(ReferenceEqualityComparer.Instance);
            IReadOnlyList<CardModel> deck = owner.Deck.Cards;
            for (int index = 0; index < deck.Count; index++)
                map[deck[index]] = index;
            indexMaps[owner] = map;
        }

        List<RemovedCardState> removedCards = [];
        foreach (CardModel card in cards.Where(card => card?.Owner is not null))
        {
            Player owner = card.Owner;
            if (indexMaps.TryGetValue(owner, out Dictionary<CardModel, int>? map)
                && map.TryGetValue(card, out int index))
            {
                removedCards.Add(new RemovedCardState(
                    card,
                    new LoadoutChangedCard(owner.NetId, index, card.Id)));
            }
        }

        __state = new RemovalState(
            LoadoutImmediateMutationService.IsRemovingAllCards
                ? []
                : cards.Where(card => LoadoutKeywords.Has(card, LoadoutKeywords.Inevitable)).ToList(),
            removedCards);
    }

    [HarmonyPostfix]
    public static void Postfix(
        RemovalState __state,
        ref Task __result)
    {
        __result = FinishRemovalAsync(__result, __state);
    }

    private static async Task FinishRemovalAsync(Task original, RemovalState state)
    {
        await original;

        // The temporary modification is attached to the CardModel itself. Re-adding
        // an Inevitable card keeps the state without any deck snapshot or reindex pass.
        foreach (CardModel card in state.InevitableCards)
        {
            if (!card.HasBeenRemovedFromState)
                continue;

            Player owner = card.Owner;
            owner.RunState.AddCard(card, owner);
            CardPileAddResult result = await CardPileCmd.Add(card, owner.Deck);
            CardCmd.PreviewCardPileAdd(result);
        }

        List<LoadoutChangedCard> removed = state.RemovedCards
            .Where(entry => entry.Card.Owner is null
                            || entry.Card.Pile?.Type != PileType.Deck
                            || !entry.Card.Owner.Deck.Cards.Any(card => ReferenceEquals(card, entry.Card)))
            .Select(entry => entry.Change)
            .ToList();
        if (removed.Count > 0)
        {
            LoadoutRunContentChangeService.Queue(
                LoadoutRunContentKind.Cards,
                removed.Select(entry => entry.OwnerNetId),
                LoadoutRunContentChangeMode.Remove,
                removed);
        }
    }
}

[HarmonyPatch(typeof(StartRunLobby))]
public static class StartRunLobbyCardModificationConstructorPatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        return AccessTools.GetDeclaredConstructors(typeof(StartRunLobby));
    }

    [HarmonyPostfix]
    public static void Postfix(StartRunLobby __instance)
    {
        CardModificationNetworkPatch.RegisterLobby(__instance);
        RelicModificationMultiplayerSyncService.RegisterLobby(__instance);
    }
}

[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.CleanUp))]
public static class StartRunLobbyCardModificationCleanUpPatch
{
    [HarmonyPrefix]
    public static void Prefix(StartRunLobby __instance, bool disconnectSession)
    {
        CardModificationNetworkPatch.UnregisterLobby(__instance, disconnectSession);
        RelicModificationMultiplayerSyncService.UnregisterLobby(__instance, disconnectSession);
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.Launch))]
public static class RunManagerLaunchCardModificationPatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        LoadoutImmediateMutationService.OnRunLaunched();
        CardModificationNetworkPatch.OnRunLaunched();
        RelicModificationMultiplayerSyncService.OnRunLaunched();
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
public static class RunManagerCleanUpCardModificationPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        LoadoutImmediateMutationService.OnRunCleaningUp();
        CardModificationNetworkPatch.OnRunCleaningUp();
        RelicModificationMultiplayerSyncService.OnRunCleaningUp();
    }
}
