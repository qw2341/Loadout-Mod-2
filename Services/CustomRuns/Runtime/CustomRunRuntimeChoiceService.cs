#nullable enable

namespace Loadout.Services.CustomRuns.Runtime;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.CustomRuns.Persistence;
using Loadout.Services.Networking;
using Loadout.UI.CustomRuns;
using MegaCrit.Sts2.Core.Context;
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
        _netService = null;
        _runLobby = null;
        _requestSequence = 0;
    }

    public static async Task<IReadOnlyList<string>> RequestChoiceAsync(
        ulong targetPlayerId,
        SelectionModelKind kind,
        IReadOnlyList<string> allowedModelIds,
        int minimum,
        int maximum,
        bool canSkip,
        long revision)
    {
        if (_netService is null || _netService.Type == NetGameType.Client)
            return [];
        long requestId = ++_requestSequence;
        CustomRunChoiceRequest request = new()
        {
            SnapshotHash = CustomRunRuleRuntimeService.Snapshot.SnapshotHash,
            RequestId = requestId,
            Revision = revision,
            TargetPlayerId = targetPlayerId,
            ModelKind = kind,
            AllowedModelIds = allowedModelIds.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList(),
            Minimum = canSkip ? 0 : minimum,
            Maximum = maximum
        };
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
            GD.PushWarning($"Loadout Custom Run: choice {requestId} timed out for player {targetPlayerId}.");
            return [];
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
                || request.TargetPlayerId != _netService.NetId
                || !string.Equals(request.SnapshotHash, CustomRunRuleRuntimeService.Snapshot.SnapshotHash, StringComparison.Ordinal)
                || request.Revision != CustomRunRuleRuntimeService.Revision
                || request.AllowedModelIds.Count > 2048
                || request.Minimum < 0
                || request.Maximum < request.Minimum
                || request.Maximum > 50)
            {
                return;
            }
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
            pending.Completion.TrySetResult([]);
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
        bool opened = CustomRunCatalogSelector.TryOpenCatalogChoice(
            request.ModelKind,
            request.AllowedModelIds,
            request.Minimum,
            request.Maximum,
            selected => completed(CreateResponse(request, selected, cancelled: false)),
            () => completed(CreateResponse(request, [], cancelled: true)),
            out _localChoiceSession,
            out string error);
        if (!opened)
        {
            GD.PushWarning($"Loadout Custom Run: could not open choice {request.RequestId}. {error}");
            completed(CreateResponse(request, [], cancelled: true));
        }
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
            pending.Completion.TrySetResult([]);
    }

    private static void OnLocalPlayerDisconnected()
    {
        lock (Gate)
        {
            foreach (PendingHostChoice pending in PendingHostChoices.Values)
                pending.Completion.TrySetResult([]);
        }
        _localChoiceSession?.Dispose();
        _localChoiceSession = null;
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

    private sealed record PendingHostChoice(
        CustomRunChoiceRequest Request,
        TaskCompletionSource<IReadOnlyList<string>> Completion);
}
