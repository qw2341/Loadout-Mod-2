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
using Loadout.Services.CardModification;
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
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

[HarmonyPatch(typeof(RunState), nameof(RunState.CreateCard), typeof(CardModel), typeof(Player))]
public static class RunStateCreateCardPatch
{
    [HarmonyPostfix]
    public static void Postfix(CardModel __result)
    {
        CardModificationStateService.ApplyPermanentToCard(__result);
    }
}

[HarmonyPatch(typeof(CombatState), nameof(CombatState.CreateCard), typeof(CardModel), typeof(Player))]
public static class CombatStateCreateCardPatch
{
    [HarmonyPostfix]
    public static void Postfix(CardModel __result)
    {
        CardModificationStateService.ApplyPermanentToCard(__result);
    }
}

[HarmonyPatch(typeof(AbstractModel), nameof(AbstractModel.MutableClone))]
public static class CardModelMutableCloneModificationStatePatch
{
    [HarmonyPostfix]
    public static void Postfix(AbstractModel __instance, AbstractModel __result)
    {
        if (NInspectCardScreenCloneStatePatch.IsActive
            && __instance is CardModel source
            && __result is CardModel clone)
            CardModificationStateService.CarryEffectiveStateToClone(source, clone);
    }
}

[HarmonyPatch(typeof(NInspectCardScreen), "UpdateCardDisplay")]
public static class NInspectCardScreenCloneStatePatch
{
    [ThreadStatic]
    private static int _depth;

    internal static bool IsActive => _depth > 0;

    [HarmonyPrefix]
    public static void Prefix()
    {
        _depth++;
    }

    [HarmonyFinalizer]
    public static Exception? Finalizer(Exception? __exception)
    {
        _depth = Math.Max(0, _depth - 1);
        return __exception;
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
        CardModificationStateService.ReapplyEffectiveStateAfterUpgrade(__state);
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

                CardModificationState state = CardModificationStateService.GetEffectiveStateForCard(card);
                LoadoutCardVisualRefreshKind refreshKind = CardModificationStateService.GetVisualRefreshKind(
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

[HarmonyPatch(typeof(RunState), nameof(RunState.LoadCard), typeof(SerializableCard), typeof(Player))]
public static class RunStateLoadCardPatch
{
    [HarmonyPostfix]
    public static void Postfix(CardModel __result)
    {
        CardModificationStateService.ApplyPermanentToCard(__result);
    }
}

[HarmonyPatch]
public static class PlayerPopulateDeckCardModificationPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(Player),
            "PopulateDeck",
            [typeof(IEnumerable<CardModel>), typeof(bool)]);
    }

    [HarmonyPostfix]
    public static void Postfix(Player __instance)
    {
        CardModificationStateService.ApplySavedRunStateToPlayerDeck(__instance);
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.Title), MethodType.Getter)]
public static class CardModelTitleCardModificationPatch
{
    [HarmonyPrefix]
    public static void Prefix(CardModel __instance, out bool __state)
    {
        __state = CardModificationStateService.HasCustomTextOverrides(__instance);
        if (__state)
            CardModificationStateService.PushLocStringContext(__instance);
    }

    [HarmonyFinalizer]
    public static Exception? Finalizer(bool __state, Exception? __exception)
    {
        if (__state)
            CardModificationStateService.PopLocStringContext();

        return __exception;
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.GetDescriptionForPile), typeof(PileType), typeof(Creature))]
public static class CardModelDescriptionCardModificationPatch
{
    [HarmonyPrefix]
    public static void Prefix(CardModel __instance, out bool __state)
    {
        __state = CardModificationStateService.HasCustomTextOverrides(__instance);
        if (__state)
            CardModificationStateService.PushLocStringContext(__instance);
    }

    [HarmonyFinalizer]
    public static Exception? Finalizer(bool __state, Exception? __exception)
    {
        if (__state)
            CardModificationStateService.PopLocStringContext();

        return __exception;
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.GetDescriptionForUpgradePreview))]
public static class CardModelUpgradeDescriptionCardModificationPatch
{
    [HarmonyPrefix]
    public static void Prefix(CardModel __instance, out bool __state)
    {
        __state = CardModificationStateService.HasCustomTextOverrides(__instance);
        if (__state)
            CardModificationStateService.PushLocStringContext(__instance);
    }

    [HarmonyFinalizer]
    public static Exception? Finalizer(bool __state, Exception? __exception)
    {
        if (__state)
            CardModificationStateService.PopLocStringContext();

        return __exception;
    }
}

[HarmonyPatch(typeof(LocString), nameof(LocString.GetRawText))]
public static class LocStringRawTextCardModificationPatch
{
    [HarmonyPostfix]
    public static void Postfix(LocString __instance, ref string __result)
    {
        if (!CardModificationStateService.HasActiveLocStringContext)
            return;

        if (CardModificationStateService.TryGetCustomRawLocString(__instance, out string customRawText))
            __result = customRawText;
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.PortraitPath), MethodType.Getter)]
public static class CardModelPortraitPathCardModificationPatch
{
    [HarmonyPostfix]
    public static void Postfix(CardModel __instance, ref string __result)
    {
        if (!CardModificationStateService.HasPortraitOverrides(__instance))
            return;

        if (CardModificationStateService.TryGetPortraitPath(__instance, beta: false, currentPath: __result, out string portraitPath))
            __result = portraitPath;
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.BetaPortraitPath), MethodType.Getter)]
public static class CardModelBetaPortraitPathCardModificationPatch
{
    [HarmonyPostfix]
    public static void Postfix(CardModel __instance, ref string __result)
    {
        if (!CardModificationStateService.HasPortraitOverrides(__instance))
            return;

        if (CardModificationStateService.TryGetPortraitPath(__instance, beta: true, currentPath: __result, out string portraitPath))
            __result = portraitPath;
    }
}

[HarmonyPatch(typeof(NCard), nameof(NCard.Create), typeof(CardModel), typeof(ModelVisibility))]
public static class NCardCreateCardModificationPatch
{
    [HarmonyPrefix]
    public static void Prefix(ref CardModel card)
    {
        card = CardModificationStateService.CreatePermanentPreviewCard(card);
    }
}

[HarmonyPatch(typeof(NCardHolder), nameof(NCardHolder.ReassignToCard))]
public static class NCardHolderReassignCardModificationPatch
{
    [HarmonyPrefix]
    public static void Prefix(ref CardModel cardModel)
    {
        cardModel = CardModificationStateService.GetEffectivePermanentCardForDisplay(cardModel);
    }
}

[HarmonyPatch(typeof(NCardLibraryGrid), nameof(NCardLibraryGrid.FilterCards), typeof(Func<CardModel, bool>), typeof(List<SortingOrders>))]
public static class NCardLibraryGridFilterCardModificationPatch
{
    private static readonly FieldInfo? AllCardsField = AccessTools.Field(typeof(NCardLibraryGrid), "_allCards");

    [HarmonyPrefix]
    public static bool Prefix(NCardLibraryGrid __instance, Func<CardModel, bool> filter, List<SortingOrders> sortingPriority)
    {
        if (AllCardsField?.GetValue(__instance) is not List<CardModel> allCards)
            return true;

        List<CardModel> cards = allCards
            .Select(CardModificationStateService.GetEffectivePermanentCardForDisplay)
            .Where(filter)
            .ToList();
        __instance.SetCards(cards, PileType.None, sortingPriority, Task.CompletedTask);
        return false;
    }
}

[HarmonyPatch(typeof(NCardLibraryGrid), "GetCardVisibility")]
public static class NCardLibraryGridVisibilityCardModificationPatch
{
    private static readonly FieldInfo? SeenCardsField = AccessTools.Field(typeof(NCardLibraryGrid), "_seenCards");
    private static readonly FieldInfo? UnlockedCardsField = AccessTools.Field(typeof(NCardLibraryGrid), "_unlockedCards");

    [HarmonyPrefix]
    public static bool Prefix(NCardLibraryGrid __instance, CardModel card, ref ModelVisibility __result)
    {
        if (SeenCardsField?.GetValue(__instance) is not HashSet<ModelId> seenCards
            || UnlockedCardsField?.GetValue(__instance) is not HashSet<CardModel> unlockedCards)
        {
            return true;
        }

        if (!unlockedCards.Any(unlocked => unlocked.Id.Equals(card.Id)))
        {
            __result = ModelVisibility.Locked;
            return false;
        }

        if (!seenCards.Contains(card.Id))
        {
            __result = ModelVisibility.NotSeen;
            return false;
        }

        __result = ModelVisibility.Visible;
        return false;
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
        CardModificationMultiplayerSyncService.RegisterLobby(__instance);
        RelicModificationMultiplayerSyncService.RegisterLobby(__instance);
    }
}

[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.CleanUp))]
public static class StartRunLobbyCardModificationCleanUpPatch
{
    [HarmonyPrefix]
    public static void Prefix(StartRunLobby __instance, bool disconnectSession)
    {
        CardModificationMultiplayerSyncService.UnregisterLobby(__instance, disconnectSession);
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
        CardModificationMultiplayerSyncService.OnRunLaunched();
        RelicModificationMultiplayerSyncService.OnRunLaunched();
        CardModificationStateService.ApplySavedRunStateToLiveDecks();
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
public static class RunManagerCleanUpCardModificationPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        LoadoutImmediateMutationService.OnRunCleaningUp();
        CardModificationMultiplayerSyncService.OnRunCleaningUp();
        RelicModificationMultiplayerSyncService.OnRunCleaningUp();
    }
}
