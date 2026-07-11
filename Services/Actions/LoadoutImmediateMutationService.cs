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
using Loadout.Services.Morphing;
using Loadout.Services.Networking;
using Loadout.Services.PowerGiver;
using Loadout.Services.Targets;
using Loadout.Services.TildeKey;
using Loadout.UI;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rooms;
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
    ClearCurrentPowers,
    EnterEvent,
    GoToRoom,
    TildeStatSet,
    TildeStatLock,
    TildeToggleSet,
    TildeKillEnemies,
    TildeSpareEnemies,
    TildeRelicCounterDelta,
    TildeRelicCounterLock,
    SummonMonster,
    MorphPlayer
}

public static class LoadoutImmediateMutationService
{
    private static readonly object SequenceGate = new();
    private static readonly SortedDictionary<int, LoadoutImmediateMutationPayload> PendingHostApplies = new();
    private static INetGameService? _runNetService;
    private static int _nextRequestId;
    private static int _nextHostSequence;
    private static int _lastAppliedHostSequence;
    private static bool _clientApplyQueued;

    public static void OnRunLaunched()
    {
        try
        {
            INetGameService netService = RunManager.Instance.NetService;
            if (netService is null)
                return;

            RegisterRunNetService(netService);
            LoadoutRunContentChangeService.ResetQueuedChanges();
            LoadoutMutationSerialExecutor.Reset();
            LoadoutModelRegistry.WarmUp();
            _nextRequestId = 0;
            _nextHostSequence = 0;
            lock (SequenceGate)
            {
                _lastAppliedHostSequence = 0;
                _clientApplyQueued = false;
                PendingHostApplies.Clear();
            }
        }
        catch (Exception exception)
        {
            GD.PushWarning($"LoadoutImmediateMutation: failed to initialize run net service. {exception.Message}");
        }
    }

    public static void OnRunCleaningUp()
    {
        UnregisterRunNetService();
        LoadoutRunContentChangeService.ResetQueuedChanges();
        LoadoutMutationSerialExecutor.Reset();
        lock (SequenceGate)
        {
            _lastAppliedHostSequence = 0;
            _clientApplyQueued = false;
            PendingHostApplies.Clear();
        }
    }

    public static bool RequestAddCard(
        ModelId modelId,
        int amount,
        LoadoutTargetSelection target,
        int cardUpgradeCount = 0)
    {
        return Request(new LoadoutImmediateMutationPayload
        {
            Kind = LoadoutImmediateMutationKind.AddCard,
            ModelId = modelId,
            Amount = amount,
            Target = target,
            CardUpgradeCount = Math.Max(0, cardUpgradeCount)
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
        // Card modifications have their own compact delta protocol. Keeping them out
        // of the generic mutation payload avoids serializing unrelated fields and
        // prevents the old global refresh/snapshot cascade.
        return CardModificationMultiplayerSyncService.RequestOperation(operation, item, state);
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

    public static bool RequestClearCurrentPowers(PowerType type, LoadoutTargetSelection target)
    {
        if (type is not (PowerType.Buff or PowerType.Debuff))
            return false;

        return Request(new LoadoutImmediateMutationPayload
        {
            Kind = LoadoutImmediateMutationKind.ClearCurrentPowers,
            ModelId = ModelId.none,
            Amount = (int)type,
            Target = target
        });
    }

    public static bool RequestSummonMonster(ModelId monsterId)
    {
        return Request(new LoadoutImmediateMutationPayload
        {
            Kind = LoadoutImmediateMutationKind.SummonMonster,
            ModelId = monsterId,
            Amount = 1
        });
    }

    public static bool RequestMorphPlayer(ModelId modelId, LoadoutTargetSelection target)
    {
        return Request(new LoadoutImmediateMutationPayload
        {
            Kind = LoadoutImmediateMutationKind.MorphPlayer,
            ModelId = modelId,
            Target = target
        });
    }

    public static bool RequestEnterEvent(ModelId eventId)
    {
        if (LoadoutModelIdSafety.IsNoneOrEmpty(eventId))
            return false;

        return RequestNetworkedConsoleCommand($"event {eventId.Entry}");
    }

    public static bool RequestGoToRoom(RoomType roomType)
    {
        return roomType != RoomType.Unassigned
               && RequestNetworkedConsoleCommand($"room {roomType}");
    }

    private static bool RequestNetworkedConsoleCommand(string command)
    {
        Player? localPlayer = GetLocalRunPlayer();
        if (localPlayer is null || !LoadoutPanelAccessService.CanLocalPlayerUsePanel())
            return false;

        try
        {
            CloseRunNavigationScreens();
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                new ConsoleCmdGameAction(localPlayer, command, CombatManager.Instance.IsInProgress));
            return true;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"LoadoutImmediateMutation: failed requesting networked command '{command}'. {exception.Message}");
            return false;
        }
    }

    public static bool RequestTildeSetStat(string statId, int value, LoadoutTargetSelection target)
    {
        return Request(new LoadoutImmediateMutationPayload
        {
            Kind = LoadoutImmediateMutationKind.TildeStatSet,
            ModelId = ModelId.none,
            Amount = value,
            Target = target,
            TildePayloadJson = JsonSerializer.Serialize(new TildeKeyMutationPayload { StatId = statId })
        });
    }

    public static bool RequestTildeSetLock(string statId, int value, bool locked, LoadoutTargetSelection target)
    {
        return Request(new LoadoutImmediateMutationPayload
        {
            Kind = LoadoutImmediateMutationKind.TildeStatLock,
            ModelId = ModelId.none,
            Amount = value,
            Target = target,
            TildePayloadJson = JsonSerializer.Serialize(new TildeKeyMutationPayload { StatId = statId, Enabled = locked })
        });
    }

    public static bool RequestTildeSetToggle(string toggleId, bool enabled, LoadoutTargetSelection target)
    {
        return Request(new LoadoutImmediateMutationPayload
        {
            Kind = LoadoutImmediateMutationKind.TildeToggleSet,
            ModelId = ModelId.none,
            Amount = enabled ? 1 : 0,
            Target = target,
            TildePayloadJson = JsonSerializer.Serialize(new TildeKeyMutationPayload { ToggleId = toggleId, Enabled = enabled })
        });
    }

    public static bool RequestTildeKillEnemies()
    {
        return Request(new LoadoutImmediateMutationPayload
        {
            Kind = LoadoutImmediateMutationKind.TildeKillEnemies,
            ModelId = ModelId.none,
            Amount = 1
        });
    }

    public static bool RequestTildeSpareEnemies()
    {
        return Request(new LoadoutImmediateMutationPayload
        {
            Kind = LoadoutImmediateMutationKind.TildeSpareEnemies,
            ModelId = ModelId.none,
            Amount = 1
        });
    }

    public static bool RequestTildeRelicCounterDelta(RelicModel relic, int delta, string counterMember)
    {
        return RequestTildeRelicCounterMutation(relic, LoadoutImmediateMutationKind.TildeRelicCounterDelta, delta, counterMember, enabled: false);
    }

    public static bool RequestTildeRelicCounterLock(RelicModel relic, string counterMember, int value, bool locked)
    {
        return RequestTildeRelicCounterMutation(relic, LoadoutImmediateMutationKind.TildeRelicCounterLock, value, counterMember, locked);
    }

    private static bool RequestTildeRelicCounterMutation(
        RelicModel relic,
        LoadoutImmediateMutationKind kind,
        int amount,
        string counterMember,
        bool enabled)
    {
        Player owner;
        try
        {
            owner = relic.Owner;
        }
        catch
        {
            return false;
        }

        int relicIndex = FindRelicIndex(owner.Relics, relic);
        if (relicIndex < 0 || string.IsNullOrWhiteSpace(counterMember))
            return false;

        return Request(new LoadoutImmediateMutationPayload
        {
            Kind = kind,
            ModelId = ModelId.none,
            Amount = amount,
            Target = LoadoutTargetSelection.ForPlayer(owner.NetId),
            OwnedItemIndex = relicIndex,
            ExpectedModelId = relic.Id,
            TildePayloadJson = JsonSerializer.Serialize(new TildeKeyMutationPayload
            {
                CounterMember = counterMember,
                Enabled = enabled
            })
        });
    }

    private static int FindRelicIndex(IReadOnlyList<RelicModel> relics, RelicModel relic)
    {
        for (int i = 0; i < relics.Count; i++)
        {
            if (ReferenceEquals(relics[i], relic))
                return i;
        }

        return -1;
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
        payload.NormalizeDefaults();

        Player? localPlayer = GetLocalRunPlayer();
        if (localPlayer is null)
            return false;

        if (!LoadoutPanelAccessService.CanLocalPlayerUsePanel())
            return false;

        payload.RequesterNetId = localPlayer.NetId;
        payload.Target = SanitizeOutgoingTarget(payload.Target, localPlayer, payload.Kind);

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

            if (!LoadoutPanelAccessService.CanRequesterUsePanel(senderId))
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

            if (!LoadoutNetworkBroadcast.IsExpectedHostSender(
                    senderId,
                    _runNetService ?? RunManager.Instance.NetService))
            {
                GD.PushWarning($"LoadoutImmediateMutation: ignored mutation {message.sequence} from non-host peer {senderId}.");
                return;
            }

            lock (SequenceGate)
            {
                if (message.sequence <= _lastAppliedHostSequence)
                    return;

                PendingHostApplies[message.sequence] = message.payload;
            }

            TryScheduleNextClientApply();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"LoadoutImmediateMutation: failed to queue host mutation {message.sequence}. {exception.Message}");
        }
    }

    private static void TryScheduleNextClientApply()
    {
        int sequence;
        LoadoutImmediateMutationPayload payload;
        lock (SequenceGate)
        {
            if (_clientApplyQueued)
                return;

            if (PendingHostApplies.Count == 0)
                return;

            KeyValuePair<int, LoadoutImmediateMutationPayload> next = PendingHostApplies.First();
            sequence = next.Key;
            payload = next.Value;
            PendingHostApplies.Remove(sequence);
            _clientApplyQueued = true;
        }

        LoadoutMutationSerialExecutor.Enqueue(async () =>
        {
            await ApplyAsync(payload);

            lock (SequenceGate)
            {
                _lastAppliedHostSequence = sequence;
                _clientApplyQueued = false;
            }

            TryScheduleNextClientApply();
        }, $"host mutation {payload.Kind} #{sequence}");
    }

    private static void PublishHostApply(LoadoutImmediateMutationPayload payload, INetGameService? netService)
    {
        payload.NormalizeDefaults();
        payload.Sequence = ++_nextHostSequence;

        LoadoutMutationSerialExecutor.Enqueue(async () =>
        {
            await ApplyAsync(payload);

            if (netService is not null && netService.Type == NetGameType.Host)
            {
                LoadoutImmediateMutationApplyMessage message = new()
                {
                    sequence = payload.Sequence,
                    payload = payload
                };

                LoadoutNetworkBroadcast.SendToRunClients(
                    netService,
                    recipient => netService.SendMessage(message, recipient),
                    $"immediate mutation {payload.Kind} #{payload.Sequence}");
            }
        }, $"local mutation {payload.Kind} #{payload.Sequence}");
    }

    private static async Task ApplyAsync(LoadoutImmediateMutationPayload payload)
    {
        Player? requester = GetRunPlayer(payload.RequesterNetId) ?? GetLocalRunPlayer();
        if (requester is null)
            return;

        switch (payload.Kind)
        {
            case LoadoutImmediateMutationKind.AddCard:
                await ApplyAddCardAsync(payload, requester);
                break;
            case LoadoutImmediateMutationKind.AddRelic:
                await ApplyAddRelicAsync(payload, requester);
                break;
            case LoadoutImmediateMutationKind.AddPotion:
                await ApplyAddPotionAsync(payload, requester);
                break;
            case LoadoutImmediateMutationKind.RemoveCard:
                await ApplyRemoveCardAsync(payload, requester);
                break;
            case LoadoutImmediateMutationKind.UpgradeCard:
                ApplyUpgradeCard(payload, requester);
                break;
            case LoadoutImmediateMutationKind.UpgradeAllDeckCards:
                ApplyUpgradeAllDeckCards(payload, requester);
                break;
            case LoadoutImmediateMutationKind.RemoveRelic:
                await ApplyRemoveRelicAsync(payload, requester);
                break;
            case LoadoutImmediateMutationKind.CardModification:
                // Compatibility for buffered messages created by older builds.
                ApplyCardModification(payload, requester);
                break;
            case LoadoutImmediateMutationKind.ApplyLoadout:
                LoadoutApplyService.ApplyImmediate(payload.LoadoutKind, payload.LoadoutPayload, payload.Target, requester.NetId);
                break;
            case LoadoutImmediateMutationKind.AdjustPower:
                ApplyAdjustPower(payload, requester);
                break;
            case LoadoutImmediateMutationKind.ClearCurrentPowers:
                await ApplyClearCurrentPowersAsync(payload);
                break;
            case LoadoutImmediateMutationKind.EnterEvent:
            case LoadoutImmediateMutationKind.GoToRoom:
                GD.PushWarning($"LoadoutImmediateMutation: ignored obsolete navigation mutation '{payload.Kind}'.");
                break;
            case LoadoutImmediateMutationKind.TildeStatSet:
                TildeKeyStateService.ApplySynchronizedStatSet(payload.TildePayloadJson, payload.Amount, payload.Target, requester);
                break;
            case LoadoutImmediateMutationKind.TildeStatLock:
                TildeKeyStateService.ApplySynchronizedStatLock(payload.TildePayloadJson, payload.Amount, payload.Target, requester);
                break;
            case LoadoutImmediateMutationKind.TildeToggleSet:
                TildeKeyStateService.ApplySynchronizedToggle(payload.TildePayloadJson, payload.Target, requester);
                break;
            case LoadoutImmediateMutationKind.TildeKillEnemies:
                await TildeKeyStateService.KillCurrentEnemiesAsync();
                break;
            case LoadoutImmediateMutationKind.TildeSpareEnemies:
                await TildeKeyStateService.SpareCurrentEnemiesAsync();
                break;
            case LoadoutImmediateMutationKind.TildeRelicCounterDelta:
                TildeKeyStateService.ApplySynchronizedRelicCounterDelta(
                    payload.TildePayloadJson,
                    payload.Amount,
                    payload.Target,
                    payload.OwnedItemIndex,
                    payload.ExpectedModelId,
                    requester);
                break;
            case LoadoutImmediateMutationKind.TildeRelicCounterLock:
                TildeKeyStateService.ApplySynchronizedRelicCounterLock(
                    payload.TildePayloadJson,
                    payload.Amount,
                    payload.Target,
                    payload.OwnedItemIndex,
                    payload.ExpectedModelId,
                    requester);
                break;
            case LoadoutImmediateMutationKind.SummonMonster:
                await LoadoutSummonMonsterService.SummonMonsterNowAsync(payload.ModelId);
                break;
            case LoadoutImmediateMutationKind.MorphPlayer:
                BottledMonsterMorphService.ApplySynchronizedMorph(payload.ModelId, payload.Target);
                break;
        }
    }

    private static async Task ApplyAddCardAsync(LoadoutImmediateMutationPayload payload, Player requester)
    {
        if (payload.Amount <= 0 || ResolveCanonicalCard(payload.ModelId) is not { } canonicalCard)
            return;

        foreach (Player targetPlayer in ResolveTargetPlayers(payload.Target, requester))
        {
            await AddDeckCardCopiesAsync(
                targetPlayer,
                canonicalCard,
                payload.Amount,
                payload.CardUpgradeCount);

            if (CombatManager.Instance.IsInProgress)
            {
                await AddCombatHandCardCopiesAsync(
                    targetPlayer,
                    canonicalCard,
                    payload.Amount,
                    payload.CardUpgradeCount);
            }
        }
    }

    private static async Task<IReadOnlyList<CardModel>> AddDeckCardCopiesAsync(
        Player targetPlayer,
        CardModel canonicalCard,
        int amount,
        int upgradeCount)
    {
        List<CardPileAddResult> addedCards = [];
        List<CardModel> changedCards = [];
        for (int i = 0; i < amount; i++)
        {
            try
            {
                CardModel card = targetPlayer.RunState.CreateCard(canonicalCard, targetPlayer);
                if (upgradeCount > 0)
                    UpgradeCardWithCommand(card, upgradeCount);

                CardPileAddResult result = await CardPileCmd.Add(card, targetPlayer.Deck);
                if (result.success)
                {
                    addedCards.Add(result);
                    changedCards.Add(result.cardAdded);
                }
            }
            catch (Exception exception)
            {
                GD.PushWarning($"LoadoutImmediateMutation: stopped adding deck card '{canonicalCard.Id}' to player {targetPlayer.NetId} after {i}/{amount}. {exception.Message}");
                break;
            }
        }

        if (IsLocalPlayer(targetPlayer))
            PreviewAddedCards(addedCards);
        return changedCards;
    }

    private static async Task<bool> AddCombatHandCardCopiesAsync(
        Player targetPlayer,
        CardModel canonicalCard,
        int amount,
        int upgradeCount)
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
                if (upgradeCount > 0)
                    UpgradeCardWithCommand(card, upgradeCount);

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

        if (IsLocalPlayer(targetPlayer))
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

        try
        {
            CardCmd.PreviewCardPileAdd(addedCards, 1.2f, style);
        }
        catch (Exception exception)
        {
            // PreviewCardPileAdd is local-only presentation. A large batch that
            // deliberately overfills the hand can make its preview nodes reject
            // their state after every card has already been added successfully.
            // Do not abort ApplyAddCardAsync here: the host must still broadcast
            // the mutation so every peer registers the generated combat-card IDs.
            // GD.PushWarning(
            //     $"LoadoutImmediateMutation: card-add preview failed after adding {addedCards.Count} card(s). " +
            //     $"The synchronized mutation will continue. {exception.Message}");
        }
    }

    private static async Task ApplyAddRelicAsync(LoadoutImmediateMutationPayload payload, Player requester)
    {
        if (payload.Amount <= 0 || ResolveCanonicalRelic(payload.ModelId) is not { } canonicalRelic)
            return;

        foreach (Player targetPlayer in ResolveTargetPlayers(payload.Target, requester))
        {
            for (int i = 0; i < payload.Amount; i++)
            {
                try
                {
                    await RelicCmd.Obtain(canonicalRelic.ToMutable(), targetPlayer);
                }
                catch (Exception exception)
                {
                    GD.PushWarning($"LoadoutImmediateMutation: stopped adding relic '{payload.ModelId}' to player {targetPlayer.NetId} after {i}/{payload.Amount}. {exception.Message}");
                    break;
                }
            }
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
            // The CardPileCmd postfix publishes the precise structural delta.
            await CardPileCmd.RemoveFromDeck(item.Model, showPreview: true);
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

        UpgradeCardWithCommand(item.Model, Math.Max(1, payload.Amount));
    }

    private static void ApplyUpgradeAllDeckCards(LoadoutImmediateMutationPayload payload, Player requester)
    {
        int upgrades = Math.Max(1, payload.Amount);
        foreach (Player targetPlayer in ResolveTargetPlayers(payload.Target, requester))
        {
            foreach (CardModel card in targetPlayer.Deck.Cards.ToList())
                UpgradeCardWithCommand(card, upgrades);
        }
    }

    private static bool UpgradeCardWithCommand(CardModel card, int upgrades)
    {
        bool changed = false;
        for (int i = 0; i < upgrades && card.IsUpgradable; i++)
        {
            try
            {
                int previousUpgradeLevel = card.CurrentUpgradeLevel;
                CardCmd.Upgrade(card, CardPreviewStyle.None);
                if (card.CurrentUpgradeLevel <= previousUpgradeLevel && card.IsUpgradable && !CombatManager.Instance.IsEnding)
                {
                    card.UpgradeInternal();
                    card.FinalizeUpgradeInternal();
                }

                changed |= card.CurrentUpgradeLevel > previousUpgradeLevel;
            }
            catch (Exception exception)
            {
                GD.PushWarning($"LoadoutImmediateMutation: failed upgrading card '{card.Id}'. {exception.Message}");
                break;
            }
        }

        return changed;
    }

    private static async Task ApplyRemoveRelicAsync(LoadoutImmediateMutationPayload payload, Player requester)
    {
        if (TryGetOwnedRelic(payload, requester) is not { } item)
            return;

        try
        {
            await RelicCmd.Remove(item.Model);
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

    private static async Task ApplyClearCurrentPowersAsync(LoadoutImmediateMutationPayload payload)
    {
        PowerType type = (PowerType)payload.Amount;
        if (type is not (PowerType.Buff or PowerType.Debuff) || !CombatManager.Instance.IsInProgress)
            return;

        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        if (combatState is null)
            return;

        foreach (Creature creature in ResolveCurrentCombatTargets(combatState, payload.Target))
        {
            List<PowerModel> powers = creature.Powers
                .Where(power => power.Type == type)
                .ToList();

            foreach (PowerModel power in powers)
            {
                try
                {
                    await PowerCmd.Remove(power);
                }
                catch (Exception exception)
                {
                    GD.PushWarning($"LoadoutImmediateMutation: failed clearing {type} power '{power.Id}' from '{creature.Name}'. {exception.Message}");
                }
            }
        }
    }

    private static IReadOnlyList<Creature> ResolveCurrentCombatTargets(CombatState combatState, LoadoutTargetSelection target)
    {
        return target.Scope switch
        {
            LoadoutTargetScope.AllMonsters => combatState.Enemies.ToList(),
            LoadoutTargetScope.AllPlayers => combatState.Players.Select(player => player.Creature).ToList(),
            LoadoutTargetScope.Player when target.PlayerNetId.HasValue && combatState.GetPlayer(target.PlayerNetId.Value) is { } player => [player.Creature],
            _ => []
        };
    }

    private static void CloseRunNavigationScreens()
    {
        NLoadoutPanelRoot.CloseBlockingRunScreens();
        NLoadoutPanelRoot.Instance?.CloseAllScreens();
    }

    private static LoadoutOwnedItem<CardModel>? TryGetOwnedDeckCard(LoadoutImmediateMutationPayload payload, Player requester)
    {
        Player? targetPlayer = ResolveExactTargetPlayer(payload.Target, requester);
        if (targetPlayer is null || payload.OwnedItemIndex < 0 || payload.OwnedItemIndex >= targetPlayer.Deck.Cards.Count)
            return null;

        CardModel card = targetPlayer.Deck.Cards[payload.OwnedItemIndex];
        return IdMatches(card, payload.ExpectedModelId) && card.Pile?.Type == PileType.Deck
            ? new LoadoutOwnedItem<CardModel>(targetPlayer, payload.OwnedItemIndex, card)
            : null;
    }

    private static LoadoutOwnedItem<RelicModel>? TryGetOwnedRelic(LoadoutImmediateMutationPayload payload, Player requester)
    {
        Player? targetPlayer = ResolveExactTargetPlayer(payload.Target, requester);
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

    private static Player? ResolveExactTargetPlayer(LoadoutTargetSelection target, Player requester)
    {
        return target.Scope == LoadoutTargetScope.Player && target.PlayerNetId.HasValue
            ? requester.RunState.GetPlayer(target.PlayerNetId.Value)
            : null;
    }

    private static bool IsLocalPlayer(Player player)
    {
        try
        {
            return LocalContext.GetMe(player.RunState)?.NetId == player.NetId;
        }
        catch
        {
            return false;
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

    private static LoadoutTargetSelection SanitizeOutgoingTarget(
        LoadoutTargetSelection target,
        Player localPlayer,
        LoadoutImmediateMutationKind kind)
    {
        try
        {
            return RunManager.Instance.NetService.Type == NetGameType.Client
                   && !AllowsGuestSelectedTarget(kind)
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

        if (!AllowsGuestSelectedTarget(payload.Kind))
            payload.Target = LoadoutTargetSelection.ForPlayer(payload.RequesterNetId);
        return payload;
    }

    private static bool AllowsGuestSelectedTarget(LoadoutImmediateMutationKind kind)
    {
        return kind is LoadoutImmediateMutationKind.AddCard
            or LoadoutImmediateMutationKind.AddRelic
            or LoadoutImmediateMutationKind.AddPotion
            or LoadoutImmediateMutationKind.RemoveCard
            or LoadoutImmediateMutationKind.UpgradeCard
            or LoadoutImmediateMutationKind.UpgradeAllDeckCards
            or LoadoutImmediateMutationKind.RemoveRelic
            or LoadoutImmediateMutationKind.CardModification
            or LoadoutImmediateMutationKind.ApplyLoadout
            or LoadoutImmediateMutationKind.AdjustPower
            or LoadoutImmediateMutationKind.ClearCurrentPowers
            or LoadoutImmediateMutationKind.TildeStatSet
            or LoadoutImmediateMutationKind.TildeStatLock
            or LoadoutImmediateMutationKind.TildeToggleSet
            or LoadoutImmediateMutationKind.TildeRelicCounterDelta
            or LoadoutImmediateMutationKind.TildeRelicCounterLock
            or LoadoutImmediateMutationKind.MorphPlayer;
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
        return LoadoutModelIdSafety.Matches(model, id);
    }

    private static CardModel? ResolveCanonicalCard(ModelId id)
    {
        if (LoadoutModelIdSafety.IsNoneOrEmpty(id))
            return null;

        return LoadoutModelRegistry.ResolveCard(id);
    }

    private static RelicModel? ResolveCanonicalRelic(ModelId id)
    {
        if (LoadoutModelIdSafety.IsNoneOrEmpty(id))
            return null;

        return LoadoutModelRegistry.ResolveRelic(id);
    }

    private static PotionModel? ResolveCanonicalPotion(ModelId id)
    {
        if (LoadoutModelIdSafety.IsNoneOrEmpty(id))
            return null;

        return LoadoutModelRegistry.ResolvePotion(id);
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
    public string TildePayloadJson;
    public int CardUpgradeCount;

    public void Serialize(PacketWriter writer)
    {
        NormalizeDefaults();

        writer.WriteInt((int)Kind, 8);
        writer.WriteInt(Sequence);
        writer.WriteULong(RequesterNetId);
        WriteModelIdString(writer, ModelId);
        writer.WriteInt(Amount);
        writer.WriteInt((int)Target.Scope, 4);
        writer.WriteBool(Target.PlayerNetId.HasValue);
        if (Target.PlayerNetId.HasValue)
            writer.WriteULong(Target.PlayerNetId.Value);
        writer.WriteInt(OwnedItemIndex);
        WriteModelIdString(writer, ExpectedModelId);
        writer.WriteInt((int)CardModificationOperation, 8);
        writer.WriteString(CardModificationStateJson ?? string.Empty);
        writer.WriteInt((int)LoadoutKind, 4);
        writer.WriteString(LoadoutPayload ?? string.Empty);
        writer.WriteString(TildePayloadJson ?? string.Empty);
        writer.WriteInt(CardUpgradeCount);
    }

    public void Deserialize(PacketReader reader)
    {
        Kind = (LoadoutImmediateMutationKind)reader.ReadInt(8);
        Sequence = reader.ReadInt();
        RequesterNetId = reader.ReadULong();
        ModelId = ReadModelIdString(reader);
        Amount = reader.ReadInt();
        LoadoutTargetScope scope = (LoadoutTargetScope)reader.ReadInt(4);
        ulong? playerNetId = reader.ReadBool() ? reader.ReadULong() : null;
        Target = new LoadoutTargetSelection(scope, playerNetId);
        OwnedItemIndex = reader.ReadInt();
        ExpectedModelId = ReadModelIdString(reader);
        CardModificationOperation = (CardModificationOperation)reader.ReadInt(8);
        CardModificationStateJson = reader.ReadString();
        LoadoutKind = (LoadoutKind)reader.ReadInt(4);
        LoadoutPayload = reader.ReadString();
        TildePayloadJson = reader.ReadString();
        CardUpgradeCount = reader.ReadInt();
        NormalizeDefaults();
    }

    public void NormalizeDefaults()
    {
        ModelId = LoadoutModelIdSafety.OrNone(ModelId);
        ExpectedModelId = LoadoutModelIdSafety.OrNone(ExpectedModelId);
        CardModificationStateJson ??= string.Empty;
        LoadoutPayload ??= string.Empty;
        TildePayloadJson ??= string.Empty;
        CardUpgradeCount = Math.Max(0, CardUpgradeCount);
    }

    public override readonly string ToString()
    {
        return $"LoadoutImmediateMutationPayload {Kind} seq {Sequence} requester {RequesterNetId} model {LoadoutModelIdSafety.ToLogString(ModelId)} amount {Amount} upgrades {CardUpgradeCount} target {Target}";
    }

    private static void WriteModelIdString(PacketWriter writer, ModelId id)
    {
        writer.WriteString(LoadoutModelIdSafety.ToWireString(id));
    }

    private static ModelId ReadModelIdString(PacketReader reader)
    {
        string rawId = reader.ReadString();
        if (LoadoutModelRegistry.TryResolveWireId(rawId, out ModelId id))
            return id;

        GD.PushWarning($"LoadoutImmediateMutation: received unknown model id '{rawId}'.");
        return ModelId.none;
    }

}

internal static class LoadoutModelIdSafety
{
    public static ModelId OrNone(ModelId? id)
    {
        return IsNoneOrEmpty(id) ? ModelId.none : id!;
    }

    public static string ToWireString(ModelId? id)
    {
        return IsNoneOrEmpty(id) ? string.Empty : id!.ToString();
    }

    public static string ToLogString(ModelId? id)
    {
        return IsNoneOrEmpty(id) ? ModelId.none.ToString() : id!.ToString();
    }

    public static bool Matches(AbstractModel model, ModelId? id)
    {
        if (IsNoneOrEmpty(id))
            return true;

        ModelId safeId = id!;
        return model.Id == safeId
               || string.Equals(model.Id.ToString(), safeId.ToString(), StringComparison.Ordinal)
               || string.Equals(model.Id.Entry, safeId.Entry, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNoneOrEmpty(ModelId? id)
    {
        return id is null
               || id == ModelId.none
               || string.IsNullOrWhiteSpace(id.Category)
               || string.IsNullOrWhiteSpace(id.Entry);
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
