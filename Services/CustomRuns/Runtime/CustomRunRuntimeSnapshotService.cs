#nullable enable

namespace Loadout.Services.CustomRuns.Runtime;

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Loadout.Services.CustomRuns.Compilation;
using Loadout.Services.CustomRuns.Persistence;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

public static class CustomRunRuntimeSnapshotService
{
    private static readonly ConditionalWeakTable<RunState, SnapshotAttachment> SnapshotsByRun = new();
    private static ResolvedCustomRunSnapshot? _pendingSnapshot;

    public static ResolvedCustomRunSnapshot? PendingSnapshot => _pendingSnapshot;

    public static void SetPending(ResolvedCustomRunSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _pendingSnapshot = snapshot;
    }

    public static void ClearPending()
    {
        _pendingSnapshot = null;
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
        _pendingSnapshot = null;
    }

    public static void Attach(RunState runState, ResolvedCustomRunSnapshot snapshot)
    {
        SnapshotAttachment attachment = SnapshotsByRun.GetValue(
            runState,
            static _ => new SnapshotAttachment());
        attachment.Snapshot = snapshot;
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
        return SnapshotsByRun.TryGetValue(runState, out SnapshotAttachment? attachment)
               && attachment.Snapshot is not null
            ? CustomRunSnapshotSerializationService.Serialize(attachment.Snapshot)
            : string.Empty;
    }

    public static void LoadSerializedSnapshot(RunState runState, string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return;

        if (!CustomRunSnapshotSerializationService.TryDeserialize(payload, out ResolvedCustomRunSnapshot snapshot, out string error))
        {
            MainFile.Logger.Warn($"[Loadout] Ignored invalid saved Custom Run snapshot: {error}");
            return;
        }

        Attach(runState, snapshot);
    }

    private sealed class SnapshotAttachment
    {
        public ResolvedCustomRunSnapshot? Snapshot;
    }
}
