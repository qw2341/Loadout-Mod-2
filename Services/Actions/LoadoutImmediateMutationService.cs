#nullable enable

namespace Loadout.Services.Actions;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Loadout.Services.CardModification;
using Loadout.Services.Loadouts;
using Loadout.Services.PowerGiver;
using Loadout.Services.Targets;
using Loadout.UI;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

public enum LoadoutImmediateMutationKind
{
    AddCard,
    AddRelic,
    AddPotion,
    RemoveCard,
    UpgradeCard,
    UpgradeAllDeckCards,
    RemoveRelic,
    CardModification,
    ApplyLoadout,
    AdjustPower,
    EnterEvent,
    GoToRoom
}

public static class LoadoutImmediateMutationService
{
    private static INetGameService? _runNetService;
    private static int _nextRequestId;
    private static int _nextHostSequence;

    public static void OnRunLaunched()
    {
        try
        {
            INetGameService netService = RunManager.Instance.NetService;
            if (netService is null)
                return;

            RegisterRunNetService(netService);
            _nextRequestId = 0;
            _nextHostSequence = 0;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"LoadoutImmediateMutation: failed to initialize run net service. {exception.Message}");
        }
    }

    public static void OnRunCleaningUp()
    {
        UnregisterRunNetService();
    }

    public static bool RequestAddCard(ModelId modelId, int amount, LoadoutTargetSelection target)
    {
        return Request(new LoadoutImmediateMutationPayload
        {
            Kind = LoadoutImmediateMutationKind.AddCard,
            ModelId = modelId,
            Amount = amount,
            Target = target
        });
    }

    public static bool RequestAddRelic(ModelId modelId, int amount, LoadoutTargetSelection target)
    {
        return Request(new LoadoutImmediateMutationPayload
        {
            Kind = LoadoutImmediateMutationKind.AddRelic,
            ModelId = modelId,
            Amount = amount,
            Target = target
        });
    }

    public static bool RequestAddPotion(ModelId modelId, int amount, LoadoutTargetSelection target)
    {
        return Request(new LoadoutImmediateMutationPayload
        {
            Kind = LoadoutImmediateMutationKind.AddPotion,
            ModelId = modelId,
            Amount = amount,
            Target = target
        });
    }

    public static bool RequestRemoveCard(LoadoutOwnedItem<CardModel> item)
    {
        return Request(new LoadoutImmediateMutationPayload
        {
            Kind = LoadoutImmediateMutationKind.RemoveCard,
            ModelId = item.Model.Id,
            Amount = 1,
            Target = LoadoutTargetSelection.ForPlayer(item.OwnerNetId),
            OwnedItemIndex = item.Index,
            ExpectedModelId = item.Model.Id
        });
    }

    public static bool RequestUpgradeCard(LoadoutOwnedItem<CardModel> item, int amount)
    {
        return Request(new LoadoutImmediateMutationPayload
        {
            Kind = LoadoutImmediateMutationKind.UpgradeCard,
            ModelId = item.Model.Id,
            Amount = amount,
            Target = LoadoutTargetSelection.ForPlayer(item.OwnerNetId),
            OwnedItemIndex = item.Index,
            ExpectedModelId = item.Model.Id
        });
    }

    public static bool RequestUpgradeAllDeckCards(LoadoutTargetSelection target)
    {
        return Request(new LoadoutImmediateMutationPayload
        {
            Kind = LoadoutImmediateMutationKind.UpgradeAllDeckCards,
            ModelId = ModelId.none,
            Amount = 1,
            Target = target
        });
    }

    public static bool RequestRemoveRelic(LoadoutOwnedItem<RelicModel> item)
    {
        return Request(new LoadoutImmediateMutationPayload
        {
            Kind = LoadoutImmediateMutationKind.RemoveRelic,
            ModelId = item.Model.Id,
            Amount = 1,
            Target = LoadoutTargetSelection.ForPlayer(item.OwnerNetId),
            OwnedItemIndex = item.Index,
            ExpectedModelId = item.Model.Id
        });
    }

    public static bool RequestCardModification(
        CardModificationOperation operation,
        LoadoutOwnedItem<CardModel> item,
        CardModificationState? state = null)
    {
        return Request(new LoadoutImmediateMutationPayload
        {
            Kind = LoadoutImmediateMutationKind.CardModification,
            ModelId = item.Model.Id,
            Amount = 1,
            Target = LoadoutTargetSelection.ForPlayer(item.OwnerNetId),
            OwnedItemIndex = item.Index,
            ExpectedModelId = item.Model.Id,
            CardModificationOperation = operation,
            CardModificationStateJson = state is null ? string.Empty : JsonSerializer.Serialize(state)
        });
    }

    public static bool RequestApplyLoadout(LoadoutKind kind, string encodedPayload, LoadoutTargetSelection target)
    {
        return Request(new LoadoutImmediateMutationPayload
        {
            Kind = LoadoutImmediateMutationKind.ApplyLoadout,
            ModelId = ModelId.none,
            Amount = 1,
            Target = target,
            LoadoutKind = kind,
            LoadoutPayload = encodedPayload ?? string.Empty
        });
    }

    public static bool RequestAdjustPower(ModelId powerId, int delta, LoadoutTargetSelection target)
    {
        return Request(new LoadoutImmediateMutationPayload
        {
            Kind = LoadoutImmediateMutationKind.AdjustPower,
            ModelId = powerId,
            Amount = delta,
            Target = target
        });
    }

    public static bool RequestEnterEvent(ModelId eventId)
    {
        return Request(new LoadoutImmediateMutationPayload
        {
            Kind = LoadoutImmediateMutationKind.EnterEvent,
            ModelId = eventId,
            Amount = 1
        });
    }

    public static bool RequestGoToRoom(RoomType roomType)
    {
        return Request(new LoadoutImmediateMutationPayload
        {
            Kind = LoadoutImmediateMutationKind.GoToRoom,
            ModelId = ModelId.none,
            Amount = (int)roomType
        });
    }

    public static bool Request(
        LoadoutImmediateMutationKind kind,
        ModelId modelId,
        int amount,
        LoadoutTargetSelection target,
        int ownedItemIndex = -1,
        ModelId? expectedModelId = null,
        CardModificationOperation cardModificationOperation = CardModificationOperation.None,
        string? cardModificationStateJson = null,
        LoadoutKind loadoutKind = LoadoutKind.CardsAndRelics,
        string? loadoutPayload = null)
    {
        return Request(new LoadoutImmediateMutationPayload
        {
            Kind = kind,
            ModelId = modelId,
            Amount = amount,
            Target = target,
            OwnedItemIndex = ownedItemIndex,
            ExpectedModelId = expectedModelId ?? ModelId.none,
            CardModificationOperation = cardModificationOperation,
            CardModificationStateJson = cardModificationStateJson ?? string.Empty,
            LoadoutKind = loadoutKind,
            LoadoutPayload = loadoutPayload ?? string.Empty
        });
    }

    private static bool Request(LoadoutImmediateMutationPayload payload)
    {
        Player? localPlayer = GetLocalRunPlayer();
        if (localPlayer is null)
            return false;

        payload.RequesterNetId = localPlayer.NetId;
        payload.Target = ForceClientTargetToSelf(payload.Target, localPlayer);

        try
        {
            INetGameService netService = RunManager.Instance.NetService;
            if (netService.Type == NetGameType.Client)
            {
                netService.SendMessage(new LoadoutImmediateMutationRequestMessage
                {
                    requestId = ++_nextRequestId,
                    payload = payload
                });
                return true;
            }

            PublishHostApply(payload, netService);
            return true;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"LoadoutImmediateMutation: failed to request {payload.Kind}. {exception.Message}");
            return false;
        }
    }

    private static void RegisterRunNetService(INetGameService netService)
    {
        if (_runNetService == netService)
            return;

        UnregisterRunNetService();
        _runNetService = netService;
        _runNetService.RegisterMessageHandler<LoadoutImmediateMutationRequestMessage>(HandleRequest);
        _runNetService.RegisterMessageHandler<LoadoutImmediateMutationApplyMessage>(HandleApply);
    }

    private static void UnregisterRunNetService()
    {
        if (_runNetService is null)
            return;

        _runNetService.UnregisterMessageHandler<LoadoutImmediateMutationRequestMessage>(HandleRequest);
        _runNetService.UnregisterMessageHandler<LoadoutImmediateMutationApplyMessage>(HandleApply);
        _runNetService = null;
    }

    private static void HandleRequest(LoadoutImmediateMutationRequestMessage message, ulong senderId)
    {
        try
        {
            INetGameService? netService = _runNetService ?? RunManager.Instance.NetService;
            if (netService?.Type != NetGameType.Host)
                return;

            LoadoutImmediateMutationPayload payload = message.payload;
            payload.RequesterNetId = senderId;
            payload = HardenClientPayload(payload, netService.NetId);
            PublishHostApply(payload, netService);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"LoadoutImmediateMutation: failed to handle request. {exception.Message}");
        }
    }

    private static void HandleApply(LoadoutImmediateMutationApplyMessage message, ulong senderId)
    {
        try
        {
            if (IsHostSession())
                return;

            Apply(message.payload);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"LoadoutImmediateMutation: failed to apply host mutation {message.sequence}. {exception.Message}");
        }
    }

    private static void PublishHostApply(LoadoutImmediateMutationPayload payload, INetGameService? netService)
    {
        payload.Sequence = ++_nextHostSequence;
        Apply(payload);

        if (netService is not null && netService.Type == NetGameType.Host)
        {
            netService.SendMessage(new LoadoutImmediateMutationApplyMessage
            {
                sequence = payload.Sequence,
                payload = payload
            });
        }
    }

    private static void Apply(LoadoutImmediateMutationPayload payload)
    {
        Player? requester = GetRunPlayer(payload.RequesterNetId) ?? GetLocalRunPlayer();
        if (requester is null)
            return;

        switch (payload.Kind)
        {
            case LoadoutImmediateMutationKind.AddCard:
                TaskHelper.RunSafely(ApplyAddCardAsync(payload, requester));
                break;
            case LoadoutImmediateMutationKind.AddRelic:
                TaskHelper.RunSafely(ApplyAddRelicAsync(payload, requester));
                break;
            case LoadoutImmediateMutationKind.AddPotion:
                TaskHelper.RunSafely(ApplyAddPotionAsync(payload, requester));
                break;
            case LoadoutImmediateMutationKind.RemoveCard:
                TaskHelper.RunSafely(ApplyRemoveCardAsync(payload, requester));
                break;
            case LoadoutImmediateMutationKind.UpgradeCard:
                ApplyUpgradeCard(payload, requester);
                break;
            case LoadoutImmediateMutationKind.UpgradeAllDeckCards:
                ApplyUpgradeAllDeckCards(payload, requester);
                break;
            case LoadoutImmediateMutationKind.RemoveRelic:
                ApplyRemoveRelic(payload, requester);
                break;
            case LoadoutImmediateMutationKind.CardModification:
                ApplyCardModification(payload, requester);
                break;
            case LoadoutImmediateMutationKind.ApplyLoadout:
                TaskHelper.RunSafely(LoadoutApplyService.ApplyImmediateAsync(payload.LoadoutKind, payload.LoadoutPayload, payload.Target, requester.NetId));
                break;
            case LoadoutImmediateMutationKind.AdjustPower:
                ApplyAdjustPower(payload, requester);
                break;
            case LoadoutImmediateMutationKind.EnterEvent:
                ApplyEnterEvent(payload, requester);
                break;
            case LoadoutImmediateMutationKind.GoToRoom:
                ApplyGoToRoom(payload);
                break;
        }
    }

    private static async Task ApplyAddCardAsync(LoadoutImmediateMutationPayload payload, Player requester)
    {
        if (payload.Amount <= 0 || ResolveCanonicalCard(payload.ModelId) is not { } canonicalCard)
            return;

        foreach (Player targetPlayer in ResolveTargetPlayers(payload.Target, requester))
        {
            bool deckChanged = await AddDeckCardCopiesAsync(targetPlayer, canonicalCard, payload.Amount);
            bool combatChanged = CombatManager.Instance.IsInProgress
                                  && await AddCombatHandCardCopiesAsync(targetPlayer, canonicalCard, payload.Amount);

            if (deckChanged || combatChanged)
                LoadoutRunContentChangeService.Notify(LoadoutRunContentKind.Cards, targetPlayer.NetId, LoadoutRunContentChangeMode.Add);
        }
    }

    private static async Task<bool> AddDeckCardCopiesAsync(Player targetPlayer, CardModel canonicalCard, int amount)
    {
        bool changed = false;
        List<CardPileAddResult> addedCards = [];
        for (int i = 0; i < amount; i++)
        {
            try
            {
                CardModel card = targetPlayer.RunState.CreateCard(canonicalCard, targetPlayer);
                CardPileAddResult result = await CardPileCmd.Add(card, targetPlayer.Deck);
                if (result.success)
                {
                    addedCards.Add(result);
                    changed = true;
                }
            }
            catch (Exception exception)
            {
                GD.PushWarning($"LoadoutImmediateMutation: stopped adding deck card '{canonicalCard.Id}' to player {targetPlayer.NetId} after {i}/{amount}. {exception.Message}");
                break;
            }
        }

        PreviewAddedCards(addedCards);
        return changed;
    }

    private static async Task<bool> AddCombatHandCardCopiesAsync(Player targetPlayer, CardModel canonicalCard, int amount)
    {
        ICombatState? combatState = targetPlayer.Creature.CombatState;
        if (combatState is null)
            return false;

        bool changed = false;
        List<CardPileAddResult> addedCards = [];
        for (int i = 0; i < amount; i++)
        {
            try
            {
                CardModel card = combatState.CreateCard(canonicalCard, targetPlayer);
                CardPileAddResult result;
                using (LoadoutCardAddRules.IgnoreHandLimit())
                    result = await CardPileCmd.AddGeneratedCardToCombat(card, PileType.Hand, targetPlayer);

                if (result.success)
                {
                    addedCards.Add(result);
                    changed = true;
                }
            }
            catch (Exception exception)
            {
                GD.PushWarning($"LoadoutImmediateMutation: stopped adding combat hand card '{canonicalCard.Id}' to player {targetPlayer.NetId} after {i}/{amount}. {exception.Message}");
                break;
            }
        }

        PreviewAddedCards(addedCards);
        return changed;
    }

    private static void PreviewAddedCards(IReadOnlyList<CardPileAddResult> addedCards)
    {
        if (addedCards.Count == 0)
            return;

        CardPreviewStyle style = addedCards.Count > 5
            ? CardPreviewStyle.GridLayout
            : CardPreviewStyle.HorizontalLayout;
        CardCmd.PreviewCardPileAdd(addedCards, 1.2f, style);
    }

    private static async Task ApplyAddRelicAsync(LoadoutImmediateMutationPayload payload, Player requester)
    {
        if (payload.Amount <= 0 || ResolveCanonicalRelic(payload.ModelId) is not { } canonicalRelic)
            return;

        foreach (Player targetPlayer in ResolveTargetPlayers(payload.Target, requester))
        {
            bool changed = false;
            for (int i = 0; i < payload.Amount; i++)
            {
                try
                {
                    await RelicCmd.Obtain(canonicalRelic.ToMutable(), targetPlayer);
                    changed = true;
                }
                catch (Exception exception)
                {
                    GD.PushWarning($"LoadoutImmediateMutation: stopped adding relic '{payload.ModelId}' to player {targetPlayer.NetId} after {i}/{payload.Amount}. {exception.Message}");
                    break;
                }
            }

            if (changed)
                LoadoutRunContentChangeService.Notify(LoadoutRunContentKind.Relics, targetPlayer.NetId);
        }
    }

    private static async Task ApplyAddPotionAsync(LoadoutImmediateMutationPayload payload, Player requester)
    {
        if (payload.Amount <= 0 || ResolveCanonicalPotion(payload.ModelId) is not { } canonicalPotion)
            return;

        foreach (Player targetPlayer in ResolveTargetPlayers(payload.Target, requester))
        {
            for (int i = 0; i < payload.Amount; i++)
            {
                try
                {
                    PotionProcureResult result = await PotionCmd.TryToProcure(canonicalPotion.ToMutable(), targetPlayer);
                    if (!result.success)
                        break;
                }
                catch (Exception exception)
                {
                    GD.PushWarning($"LoadoutImmediateMutation: stopped adding potion '{payload.ModelId}' to player {targetPlayer.NetId} after {i}/{payload.Amount}. {exception.Message}");
                    break;
                }
            }
        }
    }

    private static async Task ApplyRemoveCardAsync(LoadoutImmediateMutationPayload payload, Player requester)
    {
        if (TryGetOwnedDeckCard(payload, requester) is not { } item)
            return;

        try
        {
            await CardPileCmd.RemoveFromDeck(item.Model, showPreview: false);
            LoadoutRunContentChangeService.Notify(LoadoutRunContentKind.Cards, item.OwnerNetId, LoadoutRunContentChangeMode.Remove);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"LoadoutImmediateMutation: failed removing card '{item.Model.Id}' at index {item.Index}. {exception.Message}");
        }
    }

    private static void ApplyUpgradeCard(LoadoutImmediateMutationPayload payload, Player requester)
    {
        if (TryGetOwnedDeckCard(payload, requester) is not { } item)
            return;

        if (UpgradeCardWithCommand(item.Model, Math.Max(1, payload.Amount)))
        {
            LoadoutRunContentChangeService.Notify(LoadoutRunContentKind.Cards, item.OwnerNetId, LoadoutRunContentChangeMode.Update);
        }
    }

    private static void ApplyUpgradeAllDeckCards(LoadoutImmediateMutationPayload payload, Player requester)
    {
        HashSet<ulong> changedPlayers = [];
        int upgrades = Math.Max(1, payload.Amount);
        foreach (Player targetPlayer in ResolveTargetPlayers(payload.Target, requester))
        {
            List<CardModel> upgradedCards = [];
            foreach (CardModel card in targetPlayer.Deck.Cards.ToList())
            {
                if (UpgradeCardWithCommand(card, upgrades))
                    upgradedCards.Add(card);
            }

            if (upgradedCards.Count == 0)
                continue;

            changedPlayers.Add(targetPlayer.NetId);
        }

        if (changedPlayers.Count > 0)
            LoadoutRunContentChangeService.Notify(LoadoutRunContentKind.Cards, changedPlayers, LoadoutRunContentChangeMode.Update);
    }

    private static bool UpgradeCardWithCommand(CardModel card, int upgrades)
    {
        bool changed = false;
        for (int i = 0; i < upgrades && card.IsUpgradable; i++)
        {
            try
            {
                CardCmd.Upgrade(card, CardPreviewStyle.None);
                changed = true;
            }
            catch (Exception exception)
            {
                GD.PushWarning($"LoadoutImmediateMutation: failed upgrading card '{card.Id}'. {exception.Message}");
                break;
            }
        }

        return changed;
    }

    private static void ApplyRemoveRelic(LoadoutImmediateMutationPayload payload, Player requester)
    {
        if (TryGetOwnedRelic(payload, requester) is not { } item)
            return;

        try
        {
            item.Owner.RemoveRelicInternal(item.Model, silent: false);
            LoadoutRunContentChangeService.Notify(LoadoutRunContentKind.Relics, item.OwnerNetId);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"LoadoutImmediateMutation: failed removing relic '{item.Model.Id}' at index {item.Index}. {exception.Message}");
        }
    }

    private static void ApplyCardModification(LoadoutImmediateMutationPayload payload, Player requester)
    {
        CardModificationState? state = null;
        if (!string.IsNullOrWhiteSpace(payload.CardModificationStateJson))
        {
            try
            {
                state = JsonSerializer.Deserialize<CardModificationState>(payload.CardModificationStateJson);
            }
            catch (Exception exception)
            {
                GD.PushWarning($"LoadoutImmediateMutation: failed to deserialize card modification state. {exception.Message}");
            }
        }

        CardModificationStateService.ApplySynchronizedOperation(
            payload.CardModificationOperation,
            payload.ModelId,
            payload.Target,
            payload.OwnedItemIndex,
            payload.ExpectedModelId,
            state,
            requester);
    }

    private static void ApplyAdjustPower(LoadoutImmediateMutationPayload payload, Player requester)
    {
        PowerGiverStateService.AdjustCounterFromAction(
            payload.ModelId.ToString(),
            payload.Amount,
            payload.Target,
            requester);
    }

    private static void ApplyEnterEvent(LoadoutImmediateMutationPayload payload, Player requester)
    {
        EventModel? eventModel = ResolveEvent(payload.ModelId);
        if (eventModel is null)
        {
            GD.PushWarning($"LoadoutImmediateMutation: unknown event '{payload.ModelId}'.");
            return;
        }

        TaskHelper.RunSafely(EnterEventAsync(eventModel, requester));
    }

    private static async Task EnterEventAsync(EventModel eventModel, Player requester)
    {
        try
        {
            CloseRunNavigationScreens();
            MapPointType mapPointType = eventModel is AncientEventModel
                ? MapPointType.Ancient
                : MapPointType.Unknown;
            requester.RunState.AppendToMapPointHistory(mapPointType, RoomType.Event, eventModel.Id);
            await RunManager.Instance.EnterRoom(new EventRoom(eventModel));
        }
        catch (Exception exception)
        {
            GD.PushError($"LoadoutImmediateMutation: failed to enter event '{eventModel.Id}'. {exception}");
        }
    }

    private static void ApplyGoToRoom(LoadoutImmediateMutationPayload payload)
    {
        RoomType roomType = (RoomType)payload.Amount;
        if (roomType == RoomType.Unassigned)
            return;

        TaskHelper.RunSafely(GoToRoomAsync(roomType));
    }

    private static async Task GoToRoomAsync(RoomType roomType)
    {
        try
        {
            CloseRunNavigationScreens();
            await RunManager.Instance.EnterRoomDebug(roomType, MapPointType.Unknown, null, false);
        }
        catch (Exception exception)
        {
            GD.PushError($"LoadoutImmediateMutation: failed to go to room '{roomType}'. {exception}");
        }
    }

    private static void CloseRunNavigationScreens()
    {
        NLoadoutPanelRoot.CloseBlockingRunScreens();
        NLoadoutPanelRoot.Instance?.CloseAllScreens();
    }

    private static LoadoutOwnedItem<CardModel>? TryGetOwnedDeckCard(LoadoutImmediateMutationPayload payload, Player requester)
    {
        Player? targetPlayer = ResolveSingleTargetPlayer(payload.Target, requester);
        if (targetPlayer is null || payload.OwnedItemIndex < 0 || payload.OwnedItemIndex >= targetPlayer.Deck.Cards.Count)
            return null;

        CardModel card = targetPlayer.Deck.Cards[payload.OwnedItemIndex];
        return IdMatches(card, payload.ExpectedModelId) && card.Pile?.Type == PileType.Deck
            ? new LoadoutOwnedItem<CardModel>(targetPlayer, payload.OwnedItemIndex, card)
            : null;
    }

    private static LoadoutOwnedItem<RelicModel>? TryGetOwnedRelic(LoadoutImmediateMutationPayload payload, Player requester)
    {
        Player? targetPlayer = ResolveSingleTargetPlayer(payload.Target, requester);
        if (targetPlayer is null || payload.OwnedItemIndex < 0 || payload.OwnedItemIndex >= targetPlayer.Relics.Count)
            return null;

        RelicModel relic = targetPlayer.Relics[payload.OwnedItemIndex];
        return IdMatches(relic, payload.ExpectedModelId)
            ? new LoadoutOwnedItem<RelicModel>(targetPlayer, payload.OwnedItemIndex, relic)
            : null;
    }

    private static IReadOnlyList<Player> ResolveTargetPlayers(LoadoutTargetSelection target, Player requester)
    {
        IReadOnlyList<Player> players = LoadoutTargetService.ResolvePlayers(target, requester.RunState);
        return players.Count > 0 ? players : [requester];
    }

    private static Player? ResolveSingleTargetPlayer(LoadoutTargetSelection target, Player requester)
    {
        return target.Scope == LoadoutTargetScope.Player && target.PlayerNetId.HasValue
            ? requester.RunState.GetPlayer(target.PlayerNetId.Value)
            : requester;
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
            GD.PushWarning($"LoadoutImmediateMutation: could not resolve local player. {exception.Message}");
            return null;
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

    private static LoadoutImmediateMutationPayload HardenClientPayload(LoadoutImmediateMutationPayload payload, ulong hostNetId)
    {
        if (payload.RequesterNetId == hostNetId)
            return payload;

        payload.Target = LoadoutTargetSelection.ForPlayer(payload.RequesterNetId);
        return payload;
    }

    private static bool IsHostSession()
    {
        try
        {
            return (_runNetService ?? RunManager.Instance.NetService)?.Type == NetGameType.Host;
        }
        catch
        {
            return false;
        }
    }

    private static bool IdMatches(AbstractModel model, ModelId id)
    {
        return id == ModelId.none
               || model.Id == id
               || string.Equals(model.Id.ToString(), id.ToString(), StringComparison.Ordinal)
               || string.Equals(model.Id.Entry, id.Entry, StringComparison.OrdinalIgnoreCase);
    }

    private static CardModel? ResolveCanonicalCard(ModelId id)
    {
        return ModelDb.AllCards.FirstOrDefault(card => IdMatches(card, id));
    }

    private static RelicModel? ResolveCanonicalRelic(ModelId id)
    {
        return ModelDb.AllRelics.FirstOrDefault(relic => IdMatches(relic, id));
    }

    private static PotionModel? ResolveCanonicalPotion(ModelId id)
    {
        return ModelDb.AllPotions.FirstOrDefault(potion => IdMatches(potion, id));
    }

    private static EventModel? ResolveEvent(ModelId id)
    {
        return ModelDb.AllEvents
            .Concat(ModelDb.AllAncients)
            .Distinct()
            .FirstOrDefault(eventModel => IdMatches(eventModel, id));
    }
}

public struct LoadoutImmediateMutationPayload
{
    public LoadoutImmediateMutationKind Kind;
    public int Sequence;
    public ulong RequesterNetId;
    public ModelId ModelId;
    public int Amount;
    public LoadoutTargetSelection Target;
    public int OwnedItemIndex;
    public ModelId ExpectedModelId;
    public CardModificationOperation CardModificationOperation;
    public string CardModificationStateJson;
    public LoadoutKind LoadoutKind;
    public string LoadoutPayload;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt((int)Kind, 8);
        writer.WriteInt(Sequence);
        writer.WriteULong(RequesterNetId);
        writer.WriteFullModelId(ModelId);
        writer.WriteInt(Amount);
        writer.WriteInt((int)Target.Scope, 4);
        writer.WriteBool(Target.PlayerNetId.HasValue);
        if (Target.PlayerNetId.HasValue)
            writer.WriteULong(Target.PlayerNetId.Value);
        writer.WriteInt(OwnedItemIndex);
        writer.WriteFullModelId(ExpectedModelId);
        writer.WriteInt((int)CardModificationOperation, 8);
        writer.WriteString(CardModificationStateJson ?? string.Empty);
        writer.WriteInt((int)LoadoutKind, 4);
        writer.WriteString(LoadoutPayload ?? string.Empty);
    }

    public void Deserialize(PacketReader reader)
    {
        Kind = (LoadoutImmediateMutationKind)reader.ReadInt(8);
        Sequence = reader.ReadInt();
        RequesterNetId = reader.ReadULong();
        ModelId = reader.ReadFullModelId();
        Amount = reader.ReadInt();
        LoadoutTargetScope scope = (LoadoutTargetScope)reader.ReadInt(4);
        ulong? playerNetId = reader.ReadBool() ? reader.ReadULong() : null;
        Target = new LoadoutTargetSelection(scope, playerNetId);
        OwnedItemIndex = reader.ReadInt();
        ExpectedModelId = reader.ReadFullModelId();
        CardModificationOperation = (CardModificationOperation)reader.ReadInt(8);
        CardModificationStateJson = reader.ReadString();
        LoadoutKind = (LoadoutKind)reader.ReadInt(4);
        LoadoutPayload = reader.ReadString();
    }

    public override readonly string ToString()
    {
        return $"LoadoutImmediateMutationPayload {Kind} seq {Sequence} requester {RequesterNetId} model {ModelId} amount {Amount} target {Target}";
    }
}

public struct LoadoutImmediateMutationRequestMessage : INetMessage, IPacketSerializable
{
    public int requestId;
    public LoadoutImmediateMutationPayload payload;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt(requestId);
        payload.Serialize(writer);
    }

    public void Deserialize(PacketReader reader)
    {
        requestId = reader.ReadInt();
        payload = new LoadoutImmediateMutationPayload();
        payload.Deserialize(reader);
    }

    public override readonly string ToString()
    {
        return $"LoadoutImmediateMutationRequest {requestId} {payload}";
    }
}

public struct LoadoutImmediateMutationApplyMessage : INetMessage, IPacketSerializable
{
    public int sequence;
    public LoadoutImmediateMutationPayload payload;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt(sequence);
        payload.Serialize(writer);
    }

    public void Deserialize(PacketReader reader)
    {
        sequence = reader.ReadInt();
        payload = new LoadoutImmediateMutationPayload();
        payload.Deserialize(reader);
        payload.Sequence = sequence;
    }

    public override readonly string ToString()
    {
        return $"LoadoutImmediateMutationApply {sequence} {payload}";
    }
}
