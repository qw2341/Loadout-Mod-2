#nullable enable

namespace Loadout.Services.CustomRuns.Runtime;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Loadout.Services.CustomRuns.Catalog;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.CustomRuns.Persistence;
using Loadout.Services.Networking;
using Loadout.UI;
using Loadout.UI.CustomRuns;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;

internal static class CustomRunRuntimeChoiceService
{
    private static readonly object Gate = new();
    private static readonly Dictionary<long, PendingHostChoice> PendingHostChoices = [];
    private static INetGameService? _netService;
    private static RunLobby? _runLobby;
    private static long _requestSequence;
    private static IDisposable? _localChoiceSession;
    private static long _localChoiceRequestId;
    private static readonly Dictionary<long, CustomRunChoiceRequest> PendingNativeCardChoices = [];
    private static readonly HashSet<long> RunningNativeCardChoices = [];
    private static PlayerChoiceContext? _activeContext;
    private static long _activeContextEventId;

    public static void Register(INetGameService netService, RunLobby? runLobby)
    {
        Unregister();
        _netService = netService;
        _runLobby = runLobby;
        netService.RegisterMessageHandler<CustomRunChoiceRequestMessage>(HandleRequest);
        netService.RegisterMessageHandler<CustomRunChoiceResponseMessage>(HandleResponse);
        if (runLobby is not null)
        {
            runLobby.RemotePlayerDisconnected += OnRemotePlayerDisconnected;
            runLobby.LocalPlayerDisconnected += OnLocalPlayerDisconnected;
        }
    }

    public static void Unregister()
    {
        if (_netService is not null)
        {
            _netService.UnregisterMessageHandler<CustomRunChoiceRequestMessage>(HandleRequest);
            _netService.UnregisterMessageHandler<CustomRunChoiceResponseMessage>(HandleResponse);
        }
        if (_runLobby is not null)
        {
            _runLobby.RemotePlayerDisconnected -= OnRemotePlayerDisconnected;
            _runLobby.LocalPlayerDisconnected -= OnLocalPlayerDisconnected;
        }
        lock (Gate)
        {
            foreach (PendingHostChoice pending in PendingHostChoices.Values)
                pending.Completion.TrySetResult([]);
            PendingHostChoices.Clear();
        }
        _localChoiceSession?.Dispose();
        _localChoiceSession = null;
        _localChoiceRequestId = 0;
        _netService = null;
        _runLobby = null;
        _requestSequence = 0;
        lock (Gate)
        {
            PendingNativeCardChoices.Clear();
            RunningNativeCardChoices.Clear();
            _activeContext = null;
            _activeContextEventId = 0;
        }
    }

    public static void SetActiveContext(long eventId, PlayerChoiceContext context)
    {
        List<CustomRunChoiceRequest> pending;
        lock (Gate)
        {
            _activeContextEventId = eventId;
            _activeContext = context;
            pending = PendingNativeCardChoices.Values
                .Where(request => request.EventId == eventId)
                .OrderBy(request => request.RequestId)
                .ToList();
            foreach (CustomRunChoiceRequest request in pending)
                PendingNativeCardChoices.Remove(request.RequestId);
        }
        foreach (CustomRunChoiceRequest request in pending)
            StartReceivedNativeCardChoice(request, context);
    }

    public static void ClearActiveContext(long eventId, PlayerChoiceContext context)
    {
        lock (Gate)
        {
            if (_activeContextEventId != eventId || !ReferenceEquals(_activeContext, context))
                return;
            _activeContextEventId = 0;
            _activeContext = null;
        }
    }

    public static async Task<IReadOnlyList<string>> RequestChoiceAsync(
        ulong targetPlayerId,
        SelectionModelKind kind,
        IReadOnlyList<string> allowedModelIds,
        int minimum,
        int maximum,
        bool canSkip,
        long revision,
        long eventId,
        PlayerChoiceContext playerChoiceContext)
    {
        if (_netService is null || _netService.Type == NetGameType.Client)
            return [];
        long requestId = ++_requestSequence;
        CustomRunChoiceRequest request = new()
        {
            SnapshotHash = CustomRunRuleRuntimeService.Snapshot.SnapshotHash,
            RequestId = requestId,
            EventId = eventId,
            Revision = revision,
            TargetPlayerId = targetPlayerId,
            ModelKind = kind,
            AllowedModelIds = allowedModelIds.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList(),
            Minimum = minimum,
            Maximum = maximum,
            CanSkip = canSkip
        };
        if (kind == SelectionModelKind.Card)
            return await RequestNativeCardChoiceAsync(request, playerChoiceContext);

        TaskCompletionSource<IReadOnlyList<string>> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Gate)
            PendingHostChoices[requestId] = new PendingHostChoice(request, completion);

        if (targetPlayerId == _netService.NetId || _netService.Type is NetGameType.Singleplayer or NetGameType.Replay)
            OpenLocalChoice(request, response => AcceptResponse(response, targetPlayerId));
        else
        {
            _netService.SendMessage(new CustomRunChoiceRequestMessage
            {
                payload = JsonSerializer.Serialize(request, CustomRunSerializationService.SharedJsonOptions)
            }, targetPlayerId);
        }

        Task completed = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(60)));
        lock (Gate)
            PendingHostChoices.Remove(requestId);
        if (completed != completion.Task)
        {
            GD.PushWarning(Loadout.UI.Managers.LocMan.Loc(
                "CUSTOM_RUN_CHOICE_TIMED_OUT",
                "Loadout Custom Run: choice {0} timed out for player {1}.",
                requestId,
                targetPlayerId));
            CloseLocalChoice(requestId);
            return GetFallback(request);
        }
        return await completion.Task;
    }

    private static void HandleRequest(CustomRunChoiceRequestMessage message, ulong senderId)
    {
        if (_netService?.Type != NetGameType.Client
            || !LoadoutNetworkBroadcast.IsExpectedHostSender(senderId, _netService)
            || message.payload.Length > 512 * 1024)
        {
            return;
        }
        try
        {
            CustomRunChoiceRequest? request = JsonSerializer.Deserialize<CustomRunChoiceRequest>(
                message.payload,
                CustomRunSerializationService.SharedJsonOptions);
            if (request is null
                || !string.Equals(request.SnapshotHash, CustomRunRuleRuntimeService.Snapshot.SnapshotHash, StringComparison.Ordinal)
                || request.Revision != CustomRunRuleRuntimeService.Revision
                || request.EventId <= 0
                || request.AllowedModelIds.Count > 2048
                || request.Minimum < 0
                || request.Maximum < request.Minimum
                || request.Maximum > 50)
            {
                return;
            }
            if (request.ModelKind == SelectionModelKind.Card)
            {
                QueueOrStartReceivedNativeCardChoice(request);
                return;
            }
            if (request.TargetPlayerId != _netService.NetId)
                return;
            OpenLocalChoice(request, response =>
            {
                if (_netService is null)
                    return;
                _netService.SendMessage(new CustomRunChoiceResponseMessage
                {
                    payload = JsonSerializer.Serialize(response, CustomRunSerializationService.SharedJsonOptions)
                });
            });
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Loadout Custom Run: ignored invalid choice request. {exception.Message}");
        }
    }

    private static async Task<IReadOnlyList<string>> RequestNativeCardChoiceAsync(
        CustomRunChoiceRequest request,
        PlayerChoiceContext playerChoiceContext)
    {
        if (_netService?.Type == NetGameType.Host)
        {
            _netService.SendMessage(new CustomRunChoiceRequestMessage
            {
                payload = JsonSerializer.Serialize(request, CustomRunSerializationService.SharedJsonOptions)
            });
        }
        return await SelectCardsWithNativeCommandAsync(request, playerChoiceContext);
    }

    private static void QueueOrStartReceivedNativeCardChoice(CustomRunChoiceRequest request)
    {
        PlayerChoiceContext? context;
        lock (Gate)
        {
            if (_activeContextEventId != request.EventId || _activeContext is null)
            {
                PendingNativeCardChoices[request.RequestId] = request;
                return;
            }
            context = _activeContext;
        }
        StartReceivedNativeCardChoice(request, context);
    }

    private static void StartReceivedNativeCardChoice(
        CustomRunChoiceRequest request,
        PlayerChoiceContext playerChoiceContext)
    {
        lock (Gate)
        {
            if (!RunningNativeCardChoices.Add(request.RequestId))
                return;
        }
        _ = RunReceivedNativeCardChoiceAsync(request, playerChoiceContext);
    }

    private static async Task RunReceivedNativeCardChoiceAsync(
        CustomRunChoiceRequest request,
        PlayerChoiceContext playerChoiceContext)
    {
        try
        {
            await SelectCardsWithNativeCommandAsync(request, playerChoiceContext);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Loadout Custom Run: native card choice {request.RequestId} failed. {exception}");
        }
        finally
        {
            lock (Gate)
                RunningNativeCardChoices.Remove(request.RequestId);
        }
    }

    private static async Task<IReadOnlyList<string>> SelectCardsWithNativeCommandAsync(
        CustomRunChoiceRequest request,
        PlayerChoiceContext playerChoiceContext)
    {
        Player? player = CustomRunRuleRuntimeService.RunState.GetPlayer(request.TargetPlayerId);
        if (player is null)
            return [];
        List<CardModel> cards = request.AllowedModelIds
            .Select(id => CustomRunCatalogService.TryResolve(SelectionModelKind.Card, id, out CustomRunCatalogEntry entry)
                ? entry.Model as CardModel
                : null)
            .Where(card => card is not null)
            .Cast<CardModel>()
            .ToList();
        CardSelectorPrefs prefs = new(
            new LocString("settings_ui", "LOADOUT-CUSTOM_RUN-CHOOSE-CARDS.title"),
            Math.Min(request.Minimum, cards.Count),
            Math.Min(request.Maximum, cards.Count))
        {
            Cancelable = request.CanSkip
        };
        IEnumerable<CardModel> selected = await CardSelectCmd.FromSimpleGrid(
            playerChoiceContext,
            cards,
            player,
            prefs);
        return selected
            .Select(card => card.Id.ToString())
            .ToList();
    }

    private static void HandleResponse(CustomRunChoiceResponseMessage message, ulong senderId)
    {
        if (_netService?.Type != NetGameType.Host || message.payload.Length > 512 * 1024)
            return;
        try
        {
            CustomRunChoiceResponse? response = JsonSerializer.Deserialize<CustomRunChoiceResponse>(
                message.payload,
                CustomRunSerializationService.SharedJsonOptions);
            if (response is not null)
                AcceptResponse(response, senderId);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Loadout Custom Run: ignored invalid choice response. {exception.Message}");
        }
    }

    private static void AcceptResponse(CustomRunChoiceResponse response, ulong senderId)
    {
        PendingHostChoice? pending;
        lock (Gate)
            PendingHostChoices.TryGetValue(response.RequestId, out pending);
        if (pending is null)
            return;
        CustomRunChoiceRequest request = pending.Request;
        if (!CustomRunRuntimeProtocolValidation.IsValidChoiceResponse(request, response, senderId))
        {
            GD.PushWarning($"Loadout Custom Run: rejected forged or stale choice response {response.RequestId} from {senderId}.");
            pending.Completion.TrySetResult(GetFallback(request));
            return;
        }
        pending.Completion.TrySetResult(response.Cancelled ? [] : response.SelectedModelIds);
    }

    private static void OpenLocalChoice(
        CustomRunChoiceRequest request,
        Action<CustomRunChoiceResponse> completed)
    {
        _localChoiceSession?.Dispose();
        _localChoiceSession = null;
        _localChoiceRequestId = request.RequestId;
        bool opened = CustomRunCatalogSelector.TryOpenCatalogChoice(
            request.ModelKind,
            request.AllowedModelIds,
            request.Minimum,
            request.Maximum,
            request.CanSkip,
            selected =>
            {
                MarkLocalChoiceCompleted(request.RequestId);
                completed(CreateResponse(request, selected, cancelled: false));
            },
            () =>
            {
                MarkLocalChoiceCompleted(request.RequestId);
                completed(CreateResponse(request, [], cancelled: true));
            },
            out _localChoiceSession,
            out string error);
        if (!opened)
        {
            GD.PushWarning($"Loadout Custom Run: could not open choice {request.RequestId}. {error}");
            completed(CreateResponse(request, [], cancelled: true));
            return;
        }
        _ = ExpireLocalChoiceAsync(request, completed);
    }

    private static void OnRemotePlayerDisconnected(ulong playerId)
    {
        PendingHostChoice[] cancelled;
        lock (Gate)
        {
            cancelled = PendingHostChoices.Values
                .Where(pending => pending.Request.TargetPlayerId == playerId)
                .ToArray();
        }
        foreach (PendingHostChoice pending in cancelled)
            pending.Completion.TrySetResult(GetFallback(pending.Request));
    }

    private static void OnLocalPlayerDisconnected()
    {
        lock (Gate)
        {
            foreach (PendingHostChoice pending in PendingHostChoices.Values)
                pending.Completion.TrySetResult(GetFallback(pending.Request));
        }
        _localChoiceSession?.Dispose();
        _localChoiceSession = null;
        _localChoiceRequestId = 0;
    }

    private static CustomRunChoiceResponse CreateResponse(
        CustomRunChoiceRequest request,
        IReadOnlyList<string> selected,
        bool cancelled)
    {
        return new CustomRunChoiceResponse
        {
            SnapshotHash = request.SnapshotHash,
            RequestId = request.RequestId,
            Revision = request.Revision,
            Cancelled = cancelled,
            SelectedModelIds = selected.ToList()
        };
    }

    private static IReadOnlyList<string> GetFallback(CustomRunChoiceRequest request)
    {
        if (request.CanSkip)
            return [];
        return request.AllowedModelIds
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .Take(Math.Clamp(request.Minimum, 0, request.AllowedModelIds.Count))
            .ToList();
    }

    private static void CloseLocalChoice(long requestId)
    {
        if (_localChoiceRequestId != requestId)
            return;
        _localChoiceSession?.Dispose();
        _localChoiceSession = null;
        _localChoiceRequestId = 0;
        NLoadoutPanelRoot.Instance?.CloseTopScreen();
    }

    private static void MarkLocalChoiceCompleted(long requestId)
    {
        if (_localChoiceRequestId != requestId)
            return;
        _localChoiceSession = null;
        _localChoiceRequestId = 0;
    }

    private static async Task ExpireLocalChoiceAsync(
        CustomRunChoiceRequest request,
        Action<CustomRunChoiceResponse> completed)
    {
        await Task.Delay(TimeSpan.FromSeconds(59));
        if (_localChoiceRequestId != request.RequestId)
            return;
        IReadOnlyList<string> fallback = GetFallback(request);
        CloseLocalChoice(request.RequestId);
        completed(CreateResponse(request, fallback, cancelled: request.CanSkip));
    }

    private sealed record PendingHostChoice(
        CustomRunChoiceRequest Request,
        TaskCompletionSource<IReadOnlyList<string>> Completion);
}
