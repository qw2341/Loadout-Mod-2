#nullable enable

namespace Loadout.Services.CustomRuns.Runtime;

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Loadout.Patches.CustomRuns;
using Loadout.Services.CustomRuns.Compilation;
using Loadout.Services.CustomRuns.Persistence;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

public static class CustomRunRuntimeSnapshotService
{
    private static readonly ConditionalWeakTable<RunState, SnapshotAttachment> SnapshotsByRun = new();
    private static ResolvedCustomRunSnapshot? _pendingSnapshot;
    private static RunState? _needsInitialRuntimeApply;

    public static ResolvedCustomRunSnapshot? PendingSnapshot => _pendingSnapshot;

    public static void SetPending(ResolvedCustomRunSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        CustomRunRuntimePatchManager.ActivateForSnapshot(snapshot, pendingLaunch: true);
        _pendingSnapshot = snapshot;
    }

    public static void ClearPending()
    {
        bool shouldDeactivate = _pendingSnapshot is not null && _needsInitialRuntimeApply is null;
        _pendingSnapshot = null;
        _needsInitialRuntimeApply = null;
        if (shouldDeactivate)
            CustomRunRuntimePatchManager.Deactivate();
    }

    public static bool TryGetPendingPlayerSetup(ulong playerId, out ResolvedPlayerSetup setup)
    {
        setup = _pendingSnapshot?.Players.FirstOrDefault(player => player.PlayerId == playerId)
                ?? new ResolvedPlayerSetup();
        return _pendingSnapshot is not null
               && _pendingSnapshot.Players.Any(player => player.PlayerId == playerId);
    }

    public static void AttachPending(RunState runState)
    {
        if (_pendingSnapshot is null)
            return;

        Attach(runState, _pendingSnapshot);
        _needsInitialRuntimeApply = runState;
        _pendingSnapshot = null;
    }

    public static bool TryConsumeInitialRuntimeSetup(
        RunState runState,
        out ResolvedCustomRunSnapshot snapshot)
    {
        snapshot = new ResolvedCustomRunSnapshot();
        if (!ReferenceEquals(_needsInitialRuntimeApply, runState)
            || !SnapshotsByRun.TryGetValue(runState, out SnapshotAttachment? attachment)
            || attachment.Snapshot is null)
        {
            return false;
        }

        _needsInitialRuntimeApply = null;
        snapshot = attachment.Snapshot;
        return true;
    }

    public static void Attach(RunState runState, ResolvedCustomRunSnapshot snapshot)
    {
        SnapshotAttachment attachment = SnapshotsByRun.GetValue(
            runState,
            static _ => new SnapshotAttachment());
        attachment.Snapshot = snapshot;
    }

    public static bool TryGetSnapshot(RunState runState, out ResolvedCustomRunSnapshot snapshot)
    {
        if (SnapshotsByRun.TryGetValue(runState, out SnapshotAttachment? attachment)
            && attachment.Snapshot is not null)
        {
            snapshot = attachment.Snapshot;
            return true;
        }
        snapshot = new ResolvedCustomRunSnapshot();
        return false;
    }

    public static CustomRunRuntimeState? GetRestoredRuntimeState(RunState runState)
    {
        return SnapshotsByRun.TryGetValue(runState, out SnapshotAttachment? attachment)
            ? attachment.RestoredRuntime
            : null;
    }

    public static bool TryGetPlayerSetup(Player player, out ResolvedPlayerSetup setup)
    {
        if (player.RunState is RunState runState
            && SnapshotsByRun.TryGetValue(runState, out SnapshotAttachment? attachment)
            && attachment.Snapshot is not null)
        {
            ResolvedPlayerSetup? resolved = attachment.Snapshot.Players
                .FirstOrDefault(candidate => candidate.PlayerId == player.NetId);
            if (resolved is not null)
            {
                setup = resolved;
                return true;
            }
        }

        return TryGetPendingPlayerSetup(player.NetId, out setup);
    }

    public static string GetSerializedSnapshotForSave(RunState runState)
    {
        if (!SnapshotsByRun.TryGetValue(runState, out SnapshotAttachment? attachment)
            || attachment.Snapshot is null)
        {
            return string.Empty;
        }
        CustomRunRuntimeState runtime = CustomRunRuleRuntimeService.IsForRun(runState)
            ? CustomRunRuleRuntimeService.ExportState()
            : attachment.RestoredRuntime ?? new CustomRunRuntimeState
            {
                SetupApplied = true,
                RunStartEmitted = true
            };
        return JsonSerializer.Serialize(new CustomRunRuntimeSaveEnvelope
        {
            Snapshot = attachment.Snapshot,
            Runtime = runtime
        }, CustomRunSerializationService.SharedJsonOptions);
    }

    public static void LoadSerializedSnapshot(RunState runState, string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return;

        if (TryLoadEnvelope(payload, out ResolvedCustomRunSnapshot envelopeSnapshot, out CustomRunRuntimeState runtime))
        {
            CustomRunRuntimePatchManager.ActivateForSnapshot(envelopeSnapshot, pendingLaunch: false);
            Attach(runState, envelopeSnapshot);
            SnapshotsByRun.GetValue(runState, static _ => new SnapshotAttachment()).RestoredRuntime = runtime;
            return;
        }

        if (!CustomRunSnapshotSerializationService.TryDeserialize(payload, out ResolvedCustomRunSnapshot snapshot, out string error))
        {
            MainFile.Logger.Warn($"[Loadout] Ignored invalid saved Custom Run snapshot: {error}");
            return;
        }

        CustomRunRuntimePatchManager.ActivateForSnapshot(snapshot, pendingLaunch: false);
        Attach(runState, snapshot);
    }

    private static bool TryLoadEnvelope(
        string payload,
        out ResolvedCustomRunSnapshot snapshot,
        out CustomRunRuntimeState runtime)
    {
        snapshot = new ResolvedCustomRunSnapshot();
        runtime = new CustomRunRuntimeState();
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("envelopeVersion", out _))
                return false;
            CustomRunRuntimeSaveEnvelope? envelope = JsonSerializer.Deserialize<CustomRunRuntimeSaveEnvelope>(
                payload,
                CustomRunSerializationService.SharedJsonOptions);
            if (envelope is null || envelope.EnvelopeVersion != 1)
                return false;
            string snapshotPayload = CustomRunSnapshotSerializationService.Serialize(envelope.Snapshot);
            if (!CustomRunSnapshotSerializationService.TryDeserialize(snapshotPayload, out snapshot, out _))
                return false;
            runtime = envelope.Runtime ?? new CustomRunRuntimeState();
            return runtime.Revision >= 0 && runtime.RngSequence >= 0 && runtime.EventSequence >= 0;
        }
        catch
        {
            return false;
        }
    }

    private sealed class SnapshotAttachment
    {
        public ResolvedCustomRunSnapshot? Snapshot;
        public CustomRunRuntimeState? RestoredRuntime;
    }
}
