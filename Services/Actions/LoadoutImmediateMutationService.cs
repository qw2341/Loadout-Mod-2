#nullable enable

namespace Loadout.Services.Actions;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Loadout.Services.CardModification;
using Loadout.Services.Loadouts;
using Loadout.Services.Morphing;
using Loadout.Services.Networking;
using Loadout.Services.PowerGiver;
using Loadout.Services.RelicModification;
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
    MorphPlayer,
    AddDeckCardCopies,
    RemoveAllCards,
    RemoveAllRelics,
    RelicModification,
    AddOwnedRelicCopies,
    TildeRelicCounterSet
}

public static class LoadoutImmediateMutationService
{
    internal const int MaxSynchronizedCardCopies = 50;
    internal const int MaxSynchronizedCardUpgradeCount = 1000;
    private const int MaxRelicModificationStateJsonLength = 256 * 1024;
    private const string SynchronizedAddCardsCommandPrefix = "__loadout_add_cards_v1";
    private const string SynchronizedDeckCardCopiesCommandPrefix = "__loadout_clone_deck_card_v1";

    private static readonly object SequenceGate = new();
    private static readonly SortedDictionary<int, LoadoutImmediateMutationPayload> PendingHostApplies = new();
    private static INetGameService? _runNetService;
    private static int _nextRequestId;
    private static int _nextHostSequence;
    private static int _lastAppliedHostSequence;
    private static bool _clientApplyQueued;
    private static int _runGeneration;
    private static readonly AsyncLocal<int> RemoveAllCardsDepth = new();

    internal static bool IsRemovingAllCards => RemoveAllCardsDepth.Value > 0;

    public static void OnRunLaunched()
    {
        Interlocked.Increment(ref _runGeneration);
        LoadoutRunContentChangeService.ResetQueuedChanges();
        LoadoutMutationSerialExecutor.Reset();
        _nextRequestId = 0;
        _nextHostSequence = 0;
        lock (SequenceGate)
        {
            _lastAppliedHostSequence = 0;
            _clientApplyQueued = false;
            PendingHostApplies.Clear();
        }

        try
        {
            INetGameService netService = RunManager.Instance.NetService;
            if (netService is null)
                return;

            RegisterRunNetService(netService);
            LoadoutModelRegistry.WarmUp();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"LoadoutImmediateMutation: failed to initialize run net service. {exception.Message}");
        }
    }

    public static void OnRunCleaningUp()
    {
        Interlocked.Increment(ref _runGeneration);
        LoadoutMutationSerialExecutor.Reset();
        LoadoutRunContentChangeService.ResetQueuedChanges();
        _nextRequestId = 0;
        _nextHostSequence = 0;
        lock (SequenceGate)
        {
            _lastAppliedHostSequence = 0;
            _clientApplyQueued = false;
            PendingHostApplies.Clear();
        }

        try
        {
            UnregisterRunNetService();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"LoadoutImmediateMutation: failed unregistering run net service during cleanup. {exception.Message}");
            _runNetService = null;
        }
    }

    public static bool RequestAddCard(
        ModelId modelId,
        int amount,
        LoadoutTargetSelection target,
        int cardUpgradeCount = 0)
    {
        if (amount <= 0
            || amount > MaxSynchronizedCardCopies
            || cardUpgradeCount < 0
            || cardUpgradeCount > MaxSynchronizedCardUpgradeCount
            || LoadoutModelIdSafety.IsNoneOrEmpty(modelId))
        {
            return false;
        }

        return Request(new LoadoutImmediateMutationPayload
        {
            Kind = LoadoutImmediateMutationKind.AddCard,
            ModelId = modelId,
            Amount = amount,
            Target = target,
            CardUpgradeCount = Math.Max(0, cardUpgradeCount)
        });
    }

    public static bool RequestAddDeckCardCopies(
        LoadoutOwnedItem<CardModel> item,
        int amount)
    {
        if (amount <= 0 || amount > MaxSynchronizedCardCopies)
            return false;

        return Request(new LoadoutImmediateMutationPayload
        {
            Kind = LoadoutImmediateMutationKind.AddDeckCardCopies,
            ModelId = item.Model.Id,
            Amount = amount,
            Target = LoadoutTargetSelection.ForPlayer(item.OwnerNetId),
            OwnedItemIndex = item.Index,
            ExpectedModelId = item.Model.Id
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

    public static bool RequestRemoveAllCards(LoadoutTargetSelection target)
    {
        return Request(new LoadoutImmediateMutationPayload
        {
            Kind = LoadoutImmediateMutationKind.RemoveAllCards,
            ModelId = ModelId.none,
            Amount = 1,
            Target = target
        });
    }

    public static bool RequestRemoveAllRelics(LoadoutTargetSelection target)
    {
        return Request(new LoadoutImmediateMutationPayload
        {
            Kind = LoadoutImmediateMutationKind.RemoveAllRelics,
            ModelId = ModelId.none,
            Amount = 1,
            Target = target
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
        return LoadoutSummonMonsterService.RequestSummonMonster(monsterId);
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

    public static bool RequestTildeRelicCounterSet(RelicModel relic, int value, string counterMember)
    {
        return RequestTildeRelicCounterMutation(relic, LoadoutImmediateMutationKind.TildeRelicCounterSet, value, counterMember, enabled: false);
    }

    public static bool RequestRelicModification(
        LoadoutOwnedItem<RelicModel> item,
        RelicModificationOperation operation,
        RelicModificationState? state)
    {
        string stateJson = state is null ? string.Empty : JsonSerializer.Serialize(state);
        if (stateJson.Length > MaxRelicModificationStateJsonLength)
            return false;
        return Request(new LoadoutImmediateMutationPayload
        {
            Kind = LoadoutImmediateMutationKind.RelicModification,
            ModelId = item.Model.Id,
            Amount = 1,
            Target = LoadoutTargetSelection.ForPlayer(item.OwnerNetId),
            OwnedItemIndex = item.Index,
            ExpectedModelId = item.Model.Id,
            RelicModificationOperation = operation,
            RelicModificationStateJson = stateJson
        });
    }

    public static bool RequestOwnedRelicCopies(LoadoutOwnedItem<RelicModel> item, int amount)
    {
        return Request(new LoadoutImmediateMutationPayload
        {
            Kind = LoadoutImmediateMutationKind.AddOwnedRelicCopies,
            ModelId = item.Model.Id,
            Amount = Math.Clamp(amount, 1, MaxSynchronizedCardCopies),
            Target = LoadoutTargetSelection.ForPlayer(item.OwnerNetId),
            OwnedItemIndex = item.Index,
            ExpectedModelId = item.Model.Id
        });
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

            return PublishHostApply(payload, netService);
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
            if (!PublishHostApply(payload, netService))
                GD.PushWarning($"LoadoutImmediateMutation: rejected host apply for {payload.Kind} from peer {senderId}.");
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
            int runGeneration = _runGeneration;
            try
            {
                await ApplyAsync(payload);
            }
            finally
            {
                if (runGeneration == _runGeneration)
                {
                    lock (SequenceGate)
                    {
                        _lastAppliedHostSequence = sequence;
                        _clientApplyQueued = false;
                    }

                    TryScheduleNextClientApply();
                }
            }
        }, $"host mutation {payload.Kind} #{sequence}");
    }

    private static bool PublishHostApply(LoadoutImmediateMutationPayload payload, INetGameService? netService)
    {
        payload.NormalizeDefaults();

        // Fresh card instances must be created inside the game's synchronized
        // action queue. Broadcasting only an amount makes every peer create its
        // own cards outside that queue, which can produce different runtime card
        // identities and later state divergence.
        if (payload.Kind == LoadoutImmediateMutationKind.AddCard)
            return TryEnqueueSynchronizedAddCards(payload);
        if (payload.Kind == LoadoutImmediateMutationKind.AddDeckCardCopies)
            return TryEnqueueSynchronizedDeckCardCopies(payload);

        payload.Sequence = ++_nextHostSequence;
        int runGeneration = _runGeneration;
        LoadoutMutationSerialExecutor.Enqueue(async () =>
        {
            await ApplyAsync(payload);

            if (runGeneration != _runGeneration)
                return;

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
        return true;
    }

    private static bool TryEnqueueSynchronizedAddCards(LoadoutImmediateMutationPayload payload)
    {
        if (payload.Amount <= 0
            || payload.Amount > MaxSynchronizedCardCopies
            || payload.CardUpgradeCount < 0
            || payload.CardUpgradeCount > MaxSynchronizedCardUpgradeCount
            || ResolveCanonicalCard(payload.ModelId) is null)
        {
            return false;
        }

        Player? requester = GetRunPlayer(payload.RequesterNetId) ?? GetLocalRunPlayer();
        Player? actionOwner = GetLocalRunPlayer();
        if (requester is null || actionOwner is null)
            return false;

        ulong[] targetNetIds = ResolveTargetPlayers(payload.Target, requester)
            .Select(player => player.NetId)
            .Where(netId => netId != 0)
            .Distinct()
            .OrderBy(netId => netId)
            .ToArray();
        if (targetNetIds.Length == 0)
            return false;

        bool includeCombatHand = CombatManager.Instance.IsInProgress;
        string command = BuildSynchronizedAddCardsCommand(
            payload.ModelId,
            payload.Amount,
            payload.CardUpgradeCount,
            includeCombatHand,
            targetNetIds);
        try
        {
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                new ConsoleCmdGameAction(actionOwner, command, includeCombatHand));
            return true;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"LoadoutImmediateMutation: failed to enqueue synchronized card grant. {exception.Message}");
            return false;
        }
    }

    private static bool TryEnqueueSynchronizedDeckCardCopies(LoadoutImmediateMutationPayload payload)
    {
        if (payload.Amount <= 0 || payload.Amount > MaxSynchronizedCardCopies)
            return false;

        Player? requester = GetRunPlayer(payload.RequesterNetId) ?? GetLocalRunPlayer();
        Player? actionOwner = GetLocalRunPlayer();
        if (requester is null
            || actionOwner is null
            || TryGetOwnedDeckCard(payload, requester) is not { } sourceItem)
        {
            return false;
        }

        string command = BuildSynchronizedDeckCardCopiesCommand(
            sourceItem.OwnerNetId,
            sourceItem.Index,
            sourceItem.Model.Id,
            payload.Amount);
        try
        {
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                new ConsoleCmdGameAction(
                    actionOwner,
                    command,
                    CombatManager.Instance.IsInProgress));
            return true;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"LoadoutImmediateMutation: failed to enqueue synchronized deck-card copies. {exception.Message}");
            return false;
        }
    }

    internal static bool TryHandleSynchronizedConsoleAction(
        ConsoleCmdGameAction action,
        out Task result)
    {
        result = Task.CompletedTask;
        string command = action.Cmd ?? string.Empty;

        string addCardsPrefix = SynchronizedAddCardsCommandPrefix + " ";
        if (command.StartsWith(addCardsPrefix, StringComparison.Ordinal))
        {
            if (!IsAuthorizedSynchronizedActionOwner(action.Player))
            {
                GD.PushWarning($"LoadoutImmediateMutation: ignored unauthorized synchronized card action from player {action.Player?.NetId}.");
                return true;
            }

            string[] args = command[addCardsPrefix.Length..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (args.Length != 5
                || !TryDecodeModelIdToken(args[0], out ModelId modelId)
                || !int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount)
                || !int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int upgradeCount)
                || !TryParseCombatHandFlag(args[3], out bool includeCombatHand)
                || !TryParseTargetNetIds(args[4], out ulong[] targetNetIds))
            {
                GD.PushWarning("LoadoutImmediateMutation: ignored malformed synchronized card action.");
                return true;
            }

            result = ExecuteSynchronizedAddCardsAsync(
                modelId,
                amount,
                upgradeCount,
                includeCombatHand,
                targetNetIds);
            return true;
        }

        string deckCopiesPrefix = SynchronizedDeckCardCopiesCommandPrefix + " ";
        if (!command.StartsWith(deckCopiesPrefix, StringComparison.Ordinal))
            return false;

        if (!IsAuthorizedSynchronizedActionOwner(action.Player))
        {
            GD.PushWarning($"LoadoutImmediateMutation: ignored unauthorized synchronized deck-card-copy action from player {action.Player?.NetId}.");
            return true;
        }

        string[] deckCopyArgs = command[deckCopiesPrefix.Length..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (deckCopyArgs.Length != 4
            || !ulong.TryParse(deckCopyArgs[0], NumberStyles.None, CultureInfo.InvariantCulture, out ulong ownerNetId)
            || ownerNetId == 0
            || !int.TryParse(deckCopyArgs[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sourceIndex)
            || sourceIndex < 0
            || !TryDecodeModelIdToken(deckCopyArgs[2], out ModelId expectedModelId)
            || !int.TryParse(deckCopyArgs[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int copyAmount))
        {
            GD.PushWarning("LoadoutImmediateMutation: ignored malformed synchronized deck-card-copy action.");
            return true;
        }

        result = ExecuteSynchronizedDeckCardCopiesAsync(
            ownerNetId,
            sourceIndex,
            expectedModelId,
            copyAmount);
        return true;
    }

    private static bool IsAuthorizedSynchronizedActionOwner(Player? player)
    {
        if (player is null)
            return false;

        try
        {
            INetGameService netService = RunManager.Instance.NetService;
            return netService.Type switch
            {
                NetGameType.Singleplayer => IsLocalPlayer(player),
                NetGameType.Host => player.NetId == netService.NetId,
                NetGameType.Client => LoadoutNetworkBroadcast.IsExpectedHostSender(player.NetId, netService),
                NetGameType.Replay => true,
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    private static async Task ExecuteSynchronizedAddCardsAsync(
        ModelId modelId,
        int amount,
        int upgradeCount,
        bool includeCombatHand,
        IReadOnlyList<ulong> targetNetIds)
    {
        if (amount <= 0
            || amount > MaxSynchronizedCardCopies
            || upgradeCount < 0
            || upgradeCount > MaxSynchronizedCardUpgradeCount
            || ResolveCanonicalCard(modelId) is not { } canonicalCard)
        {
            return;
        }

        ulong[] orderedTargetIds = targetNetIds
            .Where(netId => netId != 0)
            .Distinct()
            .OrderBy(netId => netId)
            .ToArray();
        if (orderedTargetIds.Length == 0)
            return;

        List<Player> targetPlayers = new(orderedTargetIds.Length);
        foreach (ulong targetNetId in orderedTargetIds)
        {
            Player? targetPlayer = GetRunPlayer(targetNetId);
            if (targetPlayer is null)
            {
                GD.PushWarning($"LoadoutImmediateMutation: synchronized card grant could not resolve player {targetNetId}; no cards were created.");
                return;
            }

            targetPlayers.Add(targetPlayer);
        }

        foreach (Player targetPlayer in targetPlayers)
        {
            await AddDeckCardCopiesAsync(
                targetPlayer,
                canonicalCard,
                amount,
                upgradeCount);

            if (includeCombatHand && CombatManager.Instance.IsInProgress)
            {
                await AddCombatHandCardCopiesAsync(
                    targetPlayer,
                    canonicalCard,
                    amount,
                    upgradeCount);
            }
        }
    }

    private static async Task ExecuteSynchronizedDeckCardCopiesAsync(
        ulong ownerNetId,
        int sourceIndex,
        ModelId expectedModelId,
        int amount)
    {
        if (amount <= 0 || amount > MaxSynchronizedCardCopies)
            return;

        Player? owner = GetRunPlayer(ownerNetId);
        if (owner is null
            || sourceIndex < 0
            || sourceIndex >= owner.Deck.Cards.Count)
        {
            return;
        }

        CardModel sourceCard = owner.Deck.Cards[sourceIndex];
        if (!IdMatches(sourceCard, expectedModelId)
            || sourceCard.Pile?.Type != PileType.Deck)
        {
            return;
        }

        await AddExactDeckCardCopiesAsync(owner, sourceCard, amount);
    }

    private static string BuildSynchronizedAddCardsCommand(
        ModelId modelId,
        int amount,
        int upgradeCount,
        bool includeCombatHand,
        IReadOnlyList<ulong> targetNetIds)
    {
        string targets = string.Join(",", targetNetIds
            .Where(netId => netId != 0)
            .Distinct()
            .OrderBy(netId => netId)
            .Select(netId => netId.ToString(CultureInfo.InvariantCulture)));

        return string.Join(" ",
            SynchronizedAddCardsCommandPrefix,
            EncodeModelIdToken(LoadoutModelIdSafety.ToWireString(modelId)),
            amount.ToString(CultureInfo.InvariantCulture),
            upgradeCount.ToString(CultureInfo.InvariantCulture),
            includeCombatHand ? "1" : "0",
            targets);
    }

    private static string BuildSynchronizedDeckCardCopiesCommand(
        ulong ownerNetId,
        int sourceIndex,
        ModelId expectedModelId,
        int amount)
    {
        return string.Join(" ",
            SynchronizedDeckCardCopiesCommandPrefix,
            ownerNetId.ToString(CultureInfo.InvariantCulture),
            sourceIndex.ToString(CultureInfo.InvariantCulture),
            EncodeModelIdToken(LoadoutModelIdSafety.ToWireString(expectedModelId)),
            amount.ToString(CultureInfo.InvariantCulture));
    }

    private static string EncodeModelIdToken(string wireModelId)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(wireModelId))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool TryDecodeModelIdToken(string token, out ModelId modelId)
    {
        modelId = ModelId.none;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        string base64 = token
            .Replace('-', '+')
            .Replace('_', '/');
        int padding = (4 - base64.Length % 4) % 4;
        if (padding > 0)
            base64 = base64.PadRight(base64.Length + padding, '=');

        try
        {
            string wireModelId = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            return LoadoutModelRegistry.TryResolveWireId(wireModelId, out modelId)
                   && !LoadoutModelIdSafety.IsNoneOrEmpty(modelId);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryParseCombatHandFlag(string raw, out bool includeCombatHand)
    {
        includeCombatHand = raw == "1";
        return includeCombatHand || raw == "0";
    }

    private static bool TryParseTargetNetIds(string raw, out ulong[] targetNetIds)
    {
        targetNetIds = [];
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string[] tokens = raw.Split(',', StringSplitOptions.RemoveEmptyEntries);
        List<ulong> parsed = new(tokens.Length);
        foreach (string token in tokens)
        {
            if (!ulong.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out ulong netId)
                || netId == 0)
            {
                return false;
            }

            parsed.Add(netId);
        }

        targetNetIds = parsed
            .Distinct()
            .OrderBy(netId => netId)
            .ToArray();
        return targetNetIds.Length > 0;
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
            case LoadoutImmediateMutationKind.TildeRelicCounterSet:
                TildeKeyStateService.ApplySynchronizedRelicCounterSet(
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
            case LoadoutImmediateMutationKind.AddDeckCardCopies:
                await ApplyAddDeckCardCopiesAsync(payload, requester);
                break;
            case LoadoutImmediateMutationKind.RemoveAllCards:
                await ApplyRemoveAllCardsAsync(payload, requester);
                break;
            case LoadoutImmediateMutationKind.RemoveAllRelics:
                await ApplyRemoveAllRelicsAsync(payload, requester);
                break;
            case LoadoutImmediateMutationKind.RelicModification:
                ApplyRelicModification(payload, requester);
                break;
            case LoadoutImmediateMutationKind.AddOwnedRelicCopies:
                await ApplyAddOwnedRelicCopiesAsync(payload, requester);
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

    private static async Task ApplyAddDeckCardCopiesAsync(LoadoutImmediateMutationPayload payload, Player requester)
    {
        if (payload.Amount <= 0 || TryGetOwnedDeckCard(payload, requester) is not { } sourceItem)
            return;

        await AddExactDeckCardCopiesAsync(
            sourceItem.Owner,
            sourceItem.Model,
            payload.Amount);
    }

    private static async Task<IReadOnlyList<CardModel>> AddExactDeckCardCopiesAsync(
        Player targetPlayer,
        CardModel sourceCard,
        int amount)
    {
        List<CardModel> cards = new(amount);
        for (int i = 0; i < amount; i++)
        {
            try
            {
                // RunState.CloneCard duplicates the complete mutable card state without
                // assigning CardModel._cloneOf. BaseLib's SavedSpireField CopyOnClone
                // callback also carries the per-copy temporary modification snapshot.
                cards.Add(targetPlayer.RunState.CloneCard(sourceCard));
            }
            catch (Exception exception)
            {
                GD.PushWarning($"LoadoutImmediateMutation: stopped cloning deck card '{sourceCard.Id}' for player {targetPlayer.NetId} after {i}/{amount}. {exception.Message}");
                break;
            }
        }

        if (cards.Count == 0)
            return [];

        IReadOnlyList<CardPileAddResult> addedCards;
        try
        {
            addedCards = await CardPileCmd.Add(cards, targetPlayer.Deck);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"LoadoutImmediateMutation: failed adding {cards.Count} exact deck copies of '{sourceCard.Id}' to player {targetPlayer.NetId}. {exception.Message}");
            return [];
        }

        if (IsLocalPlayer(targetPlayer))
            PreviewAddedCards(addedCards);
        return addedCards
            .Where(result => result.success)
            .Select(result => result.cardAdded)
            .ToList();
    }

    private static async Task<IReadOnlyList<CardModel>> AddDeckCardCopiesAsync(
        Player targetPlayer,
        CardModel canonicalCard,
        int amount,
        int upgradeCount)
    {
        List<CardModel> cards = new(amount);
        for (int i = 0; i < amount; i++)
        {
            try
            {
                CardModel card = targetPlayer.RunState.CreateCard(canonicalCard, targetPlayer);
                if (upgradeCount > 0)
                    UpgradeCardWithCommand(card, upgradeCount);
                cards.Add(card);
            }
            catch (Exception exception)
            {
                GD.PushWarning($"LoadoutImmediateMutation: stopped creating deck card '{canonicalCard.Id}' for player {targetPlayer.NetId} after {i}/{amount}. {exception.Message}");
                break;
            }
        }

        if (cards.Count == 0)
            return [];

        IReadOnlyList<CardPileAddResult> addedCards;
        try
        {
            // The native batch path runs deck hooks for every fresh card while
            // producing one structural notification and one preview group.
            addedCards = await CardPileCmd.Add(cards, targetPlayer.Deck);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"LoadoutImmediateMutation: failed adding {cards.Count} deck copies of '{canonicalCard.Id}' to player {targetPlayer.NetId}. {exception.Message}");
            return [];
        }

        if (IsLocalPlayer(targetPlayer))
            PreviewAddedCards(addedCards);
        return addedCards
            .Where(result => result.success)
            .Select(result => result.cardAdded)
            .ToList();
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
        CardCmd.PreviewCardPileAdd(addedCards, 1.2f, style);
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
                    RelicModel relic = canonicalRelic.ToMutable();
                    Task<RelicModel> obtainTask = RelicCmd.Obtain(relic, targetPlayer);

                    // RelicCmd inserts the relic before it awaits AfterObtained.
                    // Some relics expose their screen as a pickup effect (Kifuda),
                    // while others await RewardsCmd without that flag (Small Capsule).
                    // Any incomplete native effect must continue independently so no
                    // choice/reward screen can block broadcasts or later mutations.
                    if (!obtainTask.IsCompleted)
                        _ = TaskHelper.RunSafely(obtainTask);
                    else
                        await obtainTask;
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
            for (int pass = 0; pass < upgrades; pass++)
            {
                List<CardModel> upgradableCards = targetPlayer.Deck.Cards
                    .Where(card => card.IsUpgradable)
                    .ToList();
                if (upgradableCards.Count == 0)
                    break;

                // One native command lets the card-modification postfix process the
                // entire batch with one materialization, one owner index map, and one
                // coalesced UI update instead of N separate command/event chains.
                using (CardModificationStateService.BeginTargetedUpgradeRefresh())
                    CardCmd.Upgrade(upgradableCards, CardPreviewStyle.None);
            }
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
                using (CardModificationStateService.BeginTargetedUpgradeRefresh())
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

    private static async Task ApplyRemoveAllCardsAsync(LoadoutImmediateMutationPayload payload, Player requester)
    {
        foreach (Player targetPlayer in ResolveTargetPlayers(payload.Target, requester))
        {
            List<CardModel> cards = targetPlayer.Deck.Cards.ToList();
            if (cards.Count == 0)
                continue;

            try
            {
                using (BeginRemoveAllCards())
                    await CardPileCmd.RemoveFromDeck(cards, showPreview: true);
            }
            catch (Exception exception)
            {
                GD.PushWarning($"LoadoutImmediateMutation: failed removing all cards from player {targetPlayer.NetId}. {exception.Message}");
            }
        }
    }

    private static async Task ApplyRemoveAllRelicsAsync(LoadoutImmediateMutationPayload payload, Player requester)
    {
        foreach (Player targetPlayer in ResolveTargetPlayers(payload.Target, requester))
        {
            List<RelicModel> relics = targetPlayer.Relics.ToList();
            foreach (RelicModel relic in relics)
            {
                try
                {
                    await RelicCmd.Remove(relic);
                }
                catch (Exception exception)
                {
                    GD.PushWarning($"LoadoutImmediateMutation: failed removing relic '{relic.Id}' while clearing player {targetPlayer.NetId}. {exception.Message}");
                }
            }
        }
    }

    private static IDisposable BeginRemoveAllCards()
    {
        RemoveAllCardsDepth.Value++;
        return new RemoveAllCardsScope();
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

    private static void ApplyRelicModification(LoadoutImmediateMutationPayload payload, Player requester)
    {
        if (TryGetOwnedRelic(payload, requester) is not { } item)
            return;

        RelicModificationState? state = null;
        if (payload.RelicModificationStateJson.Length > MaxRelicModificationStateJsonLength)
        {
            GD.PushWarning("LoadoutImmediateMutation: rejected oversized relic modification state.");
            return;
        }
        if (!string.IsNullOrWhiteSpace(payload.RelicModificationStateJson))
        {
            try { state = JsonSerializer.Deserialize<RelicModificationState>(payload.RelicModificationStateJson); }
            catch (Exception exception)
            {
                GD.PushWarning($"LoadoutImmediateMutation: failed to deserialize relic modification state. {exception.Message}");
                return;
            }
        }

        RelicModificationStateService.ApplyOperation(item, payload.RelicModificationOperation, state);
    }

    private static async Task ApplyAddOwnedRelicCopiesAsync(LoadoutImmediateMutationPayload payload, Player requester)
    {
        if (payload.Amount <= 0 || TryGetOwnedRelic(payload, requester) is not { } item)
            return;

        for (int i = 0; i < Math.Min(payload.Amount, MaxSynchronizedCardCopies); i++)
        {
            try
            {
                RelicModel clone = (RelicModel)item.Model.ClonePreservingMutability();
                Task<RelicModel> obtainTask = RelicCmd.Obtain(clone, item.Owner);
                if (!obtainTask.IsCompleted) _ = TaskHelper.RunSafely(obtainTask);
                else await obtainTask;
            }
            catch (Exception exception)
            {
                GD.PushWarning($"LoadoutImmediateMutation: stopped cloning relic '{item.Model.Id}' after {i}/{payload.Amount}. {exception.Message}");
                break;
            }
        }
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
            or LoadoutImmediateMutationKind.TildeRelicCounterSet
            or LoadoutImmediateMutationKind.MorphPlayer
            or LoadoutImmediateMutationKind.AddDeckCardCopies
            or LoadoutImmediateMutationKind.RemoveAllCards
            or LoadoutImmediateMutationKind.RemoveAllRelics
            or LoadoutImmediateMutationKind.RelicModification
            or LoadoutImmediateMutationKind.AddOwnedRelicCopies;
    }

    private sealed class RemoveAllCardsScope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            RemoveAllCardsDepth.Value = Math.Max(0, RemoveAllCardsDepth.Value - 1);
        }
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
    public RelicModificationOperation RelicModificationOperation;
    public string RelicModificationStateJson;

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
        writer.WriteInt((int)RelicModificationOperation, 8);
        writer.WriteString(RelicModificationStateJson ?? string.Empty);
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
        RelicModificationOperation = (RelicModificationOperation)reader.ReadInt(8);
        RelicModificationStateJson = reader.ReadString();
        NormalizeDefaults();
    }

    public void NormalizeDefaults()
    {
        ModelId = LoadoutModelIdSafety.OrNone(ModelId);
        ExpectedModelId = LoadoutModelIdSafety.OrNone(ExpectedModelId);
        CardModificationStateJson ??= string.Empty;
        LoadoutPayload ??= string.Empty;
        TildePayloadJson ??= string.Empty;
        RelicModificationStateJson ??= string.Empty;
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
