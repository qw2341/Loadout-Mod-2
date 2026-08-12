#nullable enable

namespace Loadout.Services.CustomRuns.Runtime;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Loadout.Services.Compatibility;
using Loadout.Services.CustomRuns.Compilation;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.CustomRuns.Persistence;
using Loadout.Services.Networking;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

public static class CustomRunRuleRuntimeService
{
    public const int MaximumActionsPerChain = 512;
    public const int MaximumRulesPerChain = 256;
    public const int MaximumDepth = 32;
    public const int MaximumExecutionsPerRule = 64;
    public const int MaximumDebugEntries = 256;

    private static readonly object Gate = new();
    private static readonly AsyncLocal<CustomRunRuntimeEvent?> CurrentEvent = new();
    private static readonly Dictionary<long, TaskCompletionSource<CustomRunDecisionBatch>> BatchWaiters = [];
    private static readonly Dictionary<long, CustomRunDecisionBatch> PendingBatches = [];
    private static readonly Dictionary<long, ChainState> Chains = [];
    private static readonly Queue<string> DebugEntries = new();
    private static readonly HashSet<ulong> PendingRejoinRecipients = [];

    private static RunState? _runState;
    private static ResolvedCustomRunSnapshot? _snapshot;
    private static CustomRunVariableStore? _variables;
    private static readonly Dictionary<string, List<CompiledRuleDefinition>> RulesByTrigger = new(StringComparer.Ordinal);
    private static readonly SortedDictionary<string, CustomRunRuleCounterState> RuleCounters = new(StringComparer.Ordinal);
    private static CustomRunRuntimeState _state = new();
    private static INetGameService? _netService;
    private static RunLobby? _runLobby;
    private static Delegate? _playerRejoinedHandler;
    private static bool _initialized;
    private static bool _captureEnabled;
    private static int _activeActions;

    public static bool IsActive => _initialized && _captureEnabled && _snapshot is { SchemaVersion: 2 };
    internal static bool IsForRun(RunState runState) => _initialized && ReferenceEquals(_runState, runState);
    internal static RunState RunState => _runState ?? throw new InvalidOperationException("Custom Run runtime is not initialized.");
    internal static ResolvedCustomRunSnapshot Snapshot => _snapshot ?? throw new InvalidOperationException("Custom Run runtime has no snapshot.");
    internal static CustomRunVariableStore Variables => _variables ?? throw new InvalidOperationException("Custom Run variable store is not initialized.");
    internal static long Revision => _state.Revision;

    public static void PrepareRunLaunch()
    {
        try
        {
            RunState? runState = RunManager.Instance.DebugOnlyGetState();
            if (runState is null || !CustomRunRuntimeSnapshotService.TryGetSnapshot(runState, out ResolvedCustomRunSnapshot snapshot))
                return;
            if (snapshot.SchemaVersion != 2 || snapshot.Rules.Count == 0 && snapshot.Variables.Count == 0)
                return;
            Initialize(runState, snapshot, CustomRunRuntimeSnapshotService.GetRestoredRuntimeState(runState));
        }
        catch (Exception exception)
        {
            LogError($"Could not prepare rule runtime: {exception}");
        }
    }

    public static void OnRunLaunched()
    {
        if (!_initialized)
            PrepareRunLaunch();
        if (!_initialized || _snapshot is not { SchemaVersion: 2 })
            return;
        _captureEnabled = true;
        if (_state.RunStartEmitted)
            return;
        _state.SetupApplied = true;
        _state.RunStartEmitted = true;
        EnqueueEvent("Loadout2:RunStart", Snapshot.HostPlayerId);
    }

    public static void OnRunCleaningUp()
    {
        if (_initialized)
        {
            CombatManager.Instance.CombatEnded -= OnCombatEnded;
            CombatManager.Instance.TurnStarted -= OnTurnStarted;
            CombatManager.Instance.TurnEnded -= OnTurnEnded;
        }
        if (_runLobby is not null && _playerRejoinedHandler is not null)
            Sts2Compatibility.UnsubscribeRunLobbyPlayerRejoined(_runLobby, _playerRejoinedHandler);
        if (_netService is not null)
        {
            _netService.UnregisterMessageHandler<CustomRunDecisionBatchMessage>(HandleDecisionBatch);
            _netService.UnregisterMessageHandler<CustomRunRuntimeStateMessage>(HandleRuntimeState);
        }
        CustomRunRuntimeChoiceService.Unregister();
        lock (Gate)
        {
            foreach (TaskCompletionSource<CustomRunDecisionBatch> waiter in BatchWaiters.Values)
                waiter.TrySetCanceled();
            BatchWaiters.Clear();
            PendingBatches.Clear();
            Chains.Clear();
            PendingRejoinRecipients.Clear();
            RulesByTrigger.Clear();
            RuleCounters.Clear();
            DebugEntries.Clear();
        }
        _runState = null;
        _snapshot = null;
        _variables = null;
        _state = new CustomRunRuntimeState();
        _netService = null;
        _runLobby = null;
        _playerRejoinedHandler = null;
        _activeActions = 0;
        _captureEnabled = false;
        _initialized = false;
    }

    public static void Capture(
        string triggerId,
        ulong triggeringPlayerId,
        SelectionModelKind? modelKind = null,
        string? modelId = null,
        double amount = 0d)
    {
        if (!IsActive)
            return;
        EnqueueEvent(triggerId, triggeringPlayerId, modelKind, modelId, amount);
    }

    public static async Task ExecuteSynchronizedEventAsync(CustomRunRuntimeEvent runtimeEvent)
    {
        if (!IsActive
            || runtimeEvent.EventId <= 0
            || runtimeEvent.Depth > MaximumDepth
            || !string.Equals(runtimeEvent.SnapshotHash, Snapshot.SnapshotHash, StringComparison.Ordinal)
            || runtimeEvent.EnqueuedRevision > _state.Revision)
            return;
        Interlocked.Increment(ref _activeActions);
        CustomRunRuntimeEvent? previous = CurrentEvent.Value;
        CurrentEvent.Value = runtimeEvent;
        try
        {
            if (_netService?.Type == NetGameType.Client)
            {
                CustomRunDecisionBatch batch;
                try
                {
                    batch = await WaitForBatchAsync(runtimeEvent.EventId);
                }
                catch (Exception exception)
                {
                    LogWarning($"Could not receive decisions for event {runtimeEvent.EventId}: {exception.Message}");
                    return;
                }
                if (!ValidateBatch(batch, runtimeEvent, out string error))
                {
                    LogWarning($"Rejected decision batch for event {runtimeEvent.EventId}: {error}");
                    return;
                }
                foreach (CustomRunResolvedDecision decision in batch.Decisions)
                {
                    try
                    {
                        await CustomRunRuleEvaluator.ApplyDecisionAsync(runtimeEvent, decision);
                    }
                    catch (Exception exception)
                    {
                        LogWarning($"Event {runtimeEvent.EventId}, rule '{decision.RuleId}', action '{decision.ActionTypeId}' failed on client: {exception}");
                    }
                }
                _state.Revision = batch.ResultRevision;
                return;
            }

            long baseRevision = _state.Revision;
            List<CustomRunResolvedDecision> decisions = [];
            try
            {
                if (RulesByTrigger.TryGetValue(runtimeEvent.TriggerId, out List<CompiledRuleDefinition>? rules))
                {
                    foreach (CompiledRuleDefinition rule in rules)
                    {
                        if (!TryBeginRule(runtimeEvent, rule))
                            continue;
                        try
                        {
                            if (!CustomRunRuleEvaluator.EvaluateConditions(rule.Conditions, runtimeEvent, rule.Id))
                                continue;
                            if (!CommitRuleExecution(runtimeEvent, rule))
                                continue;
                            foreach (RuleComponentSpec action in rule.Actions)
                            {
                                if (!TryCountAction(runtimeEvent.ChainId))
                                    break;
                                CustomRunResolvedDecision? decision = await CustomRunRuleEvaluator.ResolveDecisionAsync(
                                    runtimeEvent,
                                    rule.Id,
                                    action);
                                if (decision is null)
                                    continue;
                                try
                                {
                                    await CustomRunRuleEvaluator.ApplyDecisionAsync(runtimeEvent, decision);
                                    decisions.Add(decision);
                                }
                                catch (Exception exception)
                                {
                                    LogWarning($"Event {runtimeEvent.EventId}, rule '{rule.Name}', action '{action.TypeId}' failed: {exception}");
                                }
                            }
                        }
                        catch (Exception exception)
                        {
                            LogWarning($"Event {runtimeEvent.EventId}, rule '{rule.Name}' failed: {exception}");
                        }
                    }
                }
            }
            finally
            {
                _state.Revision++;
                CustomRunDecisionBatch batch = new()
                {
                    SnapshotHash = Snapshot.SnapshotHash,
                    EventId = runtimeEvent.EventId,
                    BaseRevision = baseRevision,
                    ResultRevision = _state.Revision,
                    Decisions = decisions
                };
                BroadcastBatch(batch);
            }
        }
        finally
        {
            CurrentEvent.Value = previous;
            FinishChainEvent(runtimeEvent.ChainId);
            if (Interlocked.Decrement(ref _activeActions) == 0)
                FlushPendingRejoinSnapshots();
        }
    }

    public static CustomRunRuntimeState ExportState()
    {
        lock (Gate)
        {
            return new CustomRunRuntimeState
            {
                Values = Variables.Export(),
                RuleCounters = new SortedDictionary<string, CustomRunRuleCounterState>(
                    RuleCounters.ToDictionary(
                        pair => pair.Key,
                        pair => new CustomRunRuleCounterState
                        {
                            Run = pair.Value.Run,
                            Combat = pair.Value.Combat,
                            Turn = pair.Value.Turn
                        },
                        StringComparer.Ordinal),
                    StringComparer.Ordinal),
                RngSequence = _state.RngSequence,
                EventSequence = _state.EventSequence,
                Revision = _state.Revision,
                SetupApplied = _state.SetupApplied,
                RunStartEmitted = _state.RunStartEmitted,
                CombatActive = _state.CombatActive,
                PlayerTurnActive = _state.PlayerTurnActive
            };
        }
    }

    public static IReadOnlyList<string> GetDebugLog()
    {
        lock (Gate)
            return DebugEntries.ToList();
    }

    internal static int NextIndex(int count, string context)
    {
        if (count <= 0)
            return -1;
        long sequence = _state.RngSequence++;
        return CustomRunDeterministicRng.NextIndex(Snapshot.RunSeed, sequence, context, count);
    }

    internal static CustomRunRuleCounterState GetCounter(string ruleId)
    {
        lock (Gate)
        {
            if (!RuleCounters.TryGetValue(ruleId, out CustomRunRuleCounterState? counter))
                RuleCounters[ruleId] = counter = new CustomRunRuleCounterState();
            return counter;
        }
    }

    private static void Initialize(
        RunState runState,
        ResolvedCustomRunSnapshot snapshot,
        CustomRunRuntimeState? restored)
    {
        OnRunCleaningUp();
        _runState = runState;
        _snapshot = snapshot;
        _state = restored ?? new CustomRunRuntimeState();
        _variables = new CustomRunVariableStore(snapshot, restored);
        foreach ((string triggerId, IReadOnlyList<CompiledRuleDefinition> rules) in CustomRunRulePlan.Build(snapshot.Rules))
            RulesByTrigger[triggerId] = rules.ToList();
        foreach (CompiledRuleDefinition rule in snapshot.Rules)
        {
            if (restored?.RuleCounters.TryGetValue(rule.Id, out CustomRunRuleCounterState? counter) == true)
                RuleCounters[rule.Id] = counter;
            else
                RuleCounters[rule.Id] = new CustomRunRuleCounterState();
        }

        _netService = RunManager.Instance.NetService;
        _netService.RegisterMessageHandler<CustomRunDecisionBatchMessage>(HandleDecisionBatch);
        _netService.RegisterMessageHandler<CustomRunRuntimeStateMessage>(HandleRuntimeState);
        _runLobby = RunManager.Instance.RunLobby;
        CustomRunRuntimeChoiceService.Register(_netService, _runLobby);
        if (_runLobby is not null)
        {
            _playerRejoinedHandler = Sts2Compatibility.SubscribeRunLobbyPlayerRejoined(
                _runLobby,
                OnPlayerRejoined);
        }
        CombatManager.Instance.CombatEnded += OnCombatEnded;
        CombatManager.Instance.TurnStarted += OnTurnStarted;
        CombatManager.Instance.TurnEnded += OnTurnEnded;
        _initialized = true;
        _captureEnabled = false;
    }

    private static void EnqueueEvent(
        string triggerId,
        ulong triggeringPlayerId,
        SelectionModelKind? modelKind = null,
        string? modelId = null,
        double amount = 0d)
    {
        if (_netService?.Type == NetGameType.Client || !RulesByTrigger.ContainsKey(triggerId))
            return;
        CustomRunRuntimeEvent? parent = CurrentEvent.Value;
        long eventId = ++_state.EventSequence;
        long chainId = parent?.ChainId ?? eventId;
        int depth = parent is null ? 0 : parent.Depth + 1;
        if (depth > MaximumDepth)
        {
            HaltChain(chainId, $"depth exceeded {MaximumDepth}");
            return;
        }

        ChainState chain;
        lock (Gate)
        {
            if (!Chains.TryGetValue(chainId, out chain!))
                Chains[chainId] = chain = new ChainState();
            if (chain.Halted)
                return;
            chain.Pending++;
        }
        CustomRunRuntimeEvent runtimeEvent = new()
        {
            SnapshotHash = Snapshot.SnapshotHash,
            EnqueuedRevision = _state.Revision,
            EventId = eventId,
            ChainId = chainId,
            Depth = depth,
            TriggerId = triggerId,
            TriggeringPlayerId = triggeringPlayerId == 0 ? Snapshot.HostPlayerId : triggeringPlayerId,
            ModelKind = modelKind,
            ModelId = modelId ?? string.Empty,
            Amount = amount
        };
        Player? owner = RunState.GetPlayer(Snapshot.HostPlayerId)
                        ?? RunState.Players.OrderBy(player => player.NetId).FirstOrDefault();
        if (owner is null)
        {
            FinishChainEvent(chainId);
            return;
        }
        try
        {
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new CustomRunRuleEventAction(owner, runtimeEvent));
        }
        catch (Exception exception)
        {
            FinishChainEvent(chainId);
            LogWarning($"Could not enqueue Custom Run event {eventId} ({triggerId}): {exception.Message}");
        }
    }

    private static bool TryBeginRule(CustomRunRuntimeEvent runtimeEvent, CompiledRuleDefinition rule)
    {
        lock (Gate)
        {
            if (!Chains.TryGetValue(runtimeEvent.ChainId, out ChainState? chain) || chain.Halted)
                return false;
            return true;
        }
    }

    private static bool CommitRuleExecution(CustomRunRuntimeEvent runtimeEvent, CompiledRuleDefinition rule)
    {
        lock (Gate)
        {
            if (!Chains.TryGetValue(runtimeEvent.ChainId, out ChainState? chain) || chain.Halted)
                return false;
            int priorChainExecutions = chain.ExecutionsByRule.GetValueOrDefault(rule.Id);
            if (!CustomRunRuleEvaluator.AllowsByLimit(runtimeEvent, rule, priorChainExecutions))
                return false;
            if (++chain.RuleExecutions > MaximumRulesPerChain)
            {
                HaltChainLocked(runtimeEvent.ChainId, chain, $"rule executions exceeded {MaximumRulesPerChain}");
                return false;
            }
            int perRule = priorChainExecutions + 1;
            chain.ExecutionsByRule[rule.Id] = perRule;
            if (perRule > MaximumExecutionsPerRule)
            {
                HaltChainLocked(runtimeEvent.ChainId, chain,
                    $"rule '{rule.Name}' exceeded {MaximumExecutionsPerRule} executions");
                return false;
            }
            CustomRunRuleCounterState counter = GetCounter(rule.Id);
            counter.Run++;
            counter.Combat++;
            counter.Turn++;
            return true;
        }
    }

    private static bool TryCountAction(long chainId)
    {
        lock (Gate)
        {
            if (!Chains.TryGetValue(chainId, out ChainState? chain) || chain.Halted)
                return false;
            if (++chain.Actions <= MaximumActionsPerChain)
                return true;
            HaltChainLocked(chainId, chain, $"actions exceeded {MaximumActionsPerChain}");
            return false;
        }
    }

    private static void FinishChainEvent(long chainId)
    {
        lock (Gate)
        {
            if (!Chains.TryGetValue(chainId, out ChainState? chain))
                return;
            chain.Pending = Math.Max(0, chain.Pending - 1);
            if (chain.Pending == 0)
                Chains.Remove(chainId);
        }
    }

    private static void HaltChain(long chainId, string reason)
    {
        lock (Gate)
        {
            if (!Chains.TryGetValue(chainId, out ChainState? chain))
                Chains[chainId] = chain = new ChainState();
            HaltChainLocked(chainId, chain, reason);
        }
    }

    private static void HaltChainLocked(long chainId, ChainState chain, string reason)
    {
        if (chain.Halted)
            return;
        chain.Halted = true;
        LogWarning($"Halted event chain {chainId}: {reason}.");
    }

    internal static void OnNativeCombatStarted(ICombatState state)
    {
        if (!IsActive || _state.CombatActive)
            return;
        _state.CombatActive = true;
        Variables.Reset(VariableScope.Combat);
        ResetCounterScope(combat: true, turn: true);
        Capture("Loadout2:CombatStart", Snapshot.HostPlayerId);
    }


    private static void OnCombatEnded(CombatRoom _)
    {
        if (!IsActive || !_state.CombatActive)
            return;
        Capture("Loadout2:CombatEnd", Snapshot.HostPlayerId);
        _state.CombatActive = false;
        _state.PlayerTurnActive = false;
    }

    private static void OnTurnStarted(CombatState state)
    {
        if (!IsActive || state.CurrentSide != CombatSide.Player || _state.PlayerTurnActive)
            return;
        _state.PlayerTurnActive = true;
        Variables.Reset(VariableScope.Turn);
        ResetCounterScope(combat: false, turn: true);
        foreach (Player player in state.Players.OrderBy(player => player.NetId))
            Capture("Loadout2:TurnStart", player.NetId);
    }

    private static void OnTurnEnded(CombatState state)
    {
        if (!IsActive || state.CurrentSide != CombatSide.Player || !_state.PlayerTurnActive)
            return;
        foreach (Player player in state.Players.OrderBy(player => player.NetId))
            Capture("Loadout2:TurnEnd", player.NetId);
        _state.PlayerTurnActive = false;
    }

    private static void ResetCounterScope(bool combat, bool turn)
    {
        foreach (CustomRunRuleCounterState counter in RuleCounters.Values)
        {
            if (combat)
                counter.Combat = 0;
            if (turn)
                counter.Turn = 0;
        }
    }

    private static async Task<CustomRunDecisionBatch> WaitForBatchAsync(long eventId)
    {
        TaskCompletionSource<CustomRunDecisionBatch> waiter;
        lock (Gate)
        {
            if (PendingBatches.Remove(eventId, out CustomRunDecisionBatch? pending))
                return pending;
            waiter = new TaskCompletionSource<CustomRunDecisionBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
            BatchWaiters[eventId] = waiter;
        }
        Task completed = await Task.WhenAny(waiter.Task, Task.Delay(TimeSpan.FromMinutes(5)));
        lock (Gate)
            BatchWaiters.Remove(eventId);
        if (completed != waiter.Task)
            throw new TimeoutException($"Timed out waiting for Custom Run decision batch {eventId}.");
        return await waiter.Task;
    }

    private static void HandleDecisionBatch(CustomRunDecisionBatchMessage message, ulong senderId)
    {
        if (_netService?.Type != NetGameType.Client
            || !LoadoutNetworkBroadcast.IsExpectedHostSender(senderId, _netService)
            || message.payload.Length > 1024 * 1024)
        {
            return;
        }
        try
        {
            CustomRunDecisionBatch? batch = JsonSerializer.Deserialize<CustomRunDecisionBatch>(
                message.payload,
                CustomRunSerializationService.SharedJsonOptions);
            if (batch is null || batch.Decisions.Count > MaximumActionsPerChain)
                return;
            lock (Gate)
            {
                if (BatchWaiters.Remove(batch.EventId, out TaskCompletionSource<CustomRunDecisionBatch>? waiter))
                    waiter.TrySetResult(batch);
                else
                    PendingBatches[batch.EventId] = batch;
            }
        }
        catch (Exception exception)
        {
            LogWarning($"Ignored invalid decision batch: {exception.Message}");
        }
    }

    private static bool ValidateBatch(
        CustomRunDecisionBatch batch,
        CustomRunRuntimeEvent runtimeEvent,
        out string error)
    {
        if (!CustomRunRuntimeProtocolValidation.IsValidDecisionBatch(
                batch,
                Snapshot.SnapshotHash,
                runtimeEvent.EventId,
                _state.Revision))
        {
            if (!string.Equals(batch.SnapshotHash, Snapshot.SnapshotHash, StringComparison.Ordinal))
            error = "snapshot hash did not match";
            else if (batch.EventId != runtimeEvent.EventId)
            error = "event ID did not match";
            else
            error = $"stale revision {batch.BaseRevision}; expected {_state.Revision}";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private static void BroadcastBatch(CustomRunDecisionBatch batch)
    {
        if (_netService?.Type != NetGameType.Host)
            return;
        string payload = JsonSerializer.Serialize(batch, CustomRunSerializationService.SharedJsonOptions);
        CustomRunDecisionBatchMessage message = new() { payload = payload };
        LoadoutNetworkBroadcast.SendToRunClients(
            _netService,
            recipient => _netService.SendMessage(message, recipient),
            $"Custom Run decision batch {batch.EventId}");
    }

    private static void OnPlayerRejoined(ulong playerId)
    {
        if (_netService?.Type != NetGameType.Host || playerId == _netService.NetId)
            return;
        lock (Gate)
        {
            if (_activeActions > 0)
            {
                PendingRejoinRecipients.Add(playerId);
                return;
            }
        }
        SendRuntimeState(playerId);
    }

    private static void FlushPendingRejoinSnapshots()
    {
        ulong[] recipients;
        lock (Gate)
        {
            recipients = PendingRejoinRecipients.ToArray();
            PendingRejoinRecipients.Clear();
        }
        foreach (ulong recipient in recipients)
            SendRuntimeState(recipient);
    }

    private static void SendRuntimeState(ulong playerId)
    {
        if (_netService?.Type != NetGameType.Host)
            return;
        string payload = JsonSerializer.Serialize(ExportState(), CustomRunSerializationService.SharedJsonOptions);
        _netService.SendMessage(new CustomRunRuntimeStateMessage
        {
            snapshotHash = Snapshot.SnapshotHash,
            payload = payload
        }, playerId);
    }

    private static void HandleRuntimeState(CustomRunRuntimeStateMessage message, ulong senderId)
    {
        if (_netService?.Type != NetGameType.Client
            || !LoadoutNetworkBroadcast.IsExpectedHostSender(senderId, _netService)
            || !string.Equals(message.snapshotHash, Snapshot.SnapshotHash, StringComparison.Ordinal)
            || message.payload.Length > 1024 * 1024)
        {
            return;
        }
        try
        {
            CustomRunRuntimeState? incoming = JsonSerializer.Deserialize<CustomRunRuntimeState>(
                message.payload,
                CustomRunSerializationService.SharedJsonOptions);
            if (incoming is null || incoming.Revision < _state.Revision)
                return;
            _state = incoming;
            _variables = new CustomRunVariableStore(Snapshot, incoming);
            RuleCounters.Clear();
            foreach ((string ruleId, CustomRunRuleCounterState counter) in incoming.RuleCounters)
                RuleCounters[ruleId] = counter;
        }
        catch (Exception exception)
        {
            LogWarning($"Ignored invalid runtime recovery state: {exception.Message}");
        }
    }

    private static void LogWarning(string message)
    {
        AddDebug($"WARN {message}");
        GD.PushWarning($"Loadout Custom Run: {message}");
    }

    private static void LogError(string message)
    {
        AddDebug($"ERROR {message}");
        GD.PushError($"Loadout Custom Run: {message}");
    }

    private static void AddDebug(string message)
    {
        lock (Gate)
        {
            DebugEntries.Enqueue($"{DateTimeOffset.UtcNow:O} {message}");
            while (DebugEntries.Count > MaximumDebugEntries)
                DebugEntries.Dequeue();
        }
    }

    private sealed class ChainState
    {
        public int Pending;
        public int Actions;
        public int RuleExecutions;
        public bool Halted;
        public Dictionary<string, int> ExecutionsByRule { get; } = new(StringComparer.Ordinal);
    }
}
