#nullable enable

namespace Loadout.Services.CustomRuns.Runtime;

using System;
using System.Collections.Generic;
using System.Linq;

public static class CustomRunRuntimeProtocolValidation
{
    public static bool IsValidChoiceResponse(
        CustomRunChoiceRequest request,
        CustomRunChoiceResponse response,
        ulong senderId)
    {
        HashSet<string> allowed = request.AllowedModelIds.ToHashSet(StringComparer.Ordinal);
        return senderId == request.TargetPlayerId
               && string.Equals(response.SnapshotHash, request.SnapshotHash, StringComparison.Ordinal)
               && response.RequestId == request.RequestId
               && response.Revision == request.Revision
               && (!response.Cancelled || request.CanSkip)
               && response.SelectedModelIds.Count >= (response.Cancelled ? 0 : request.Minimum)
               && response.SelectedModelIds.Count <= request.Maximum
               && response.SelectedModelIds.Distinct(StringComparer.Ordinal).Count() == response.SelectedModelIds.Count
               && response.SelectedModelIds.All(allowed.Contains);
    }

    public static bool IsValidDecisionBatch(
        CustomRunDecisionBatch batch,
        string snapshotHash,
        long eventId,
        long currentRevision)
    {
        return string.Equals(batch.SnapshotHash, snapshotHash, StringComparison.Ordinal)
               && batch.EventId == eventId
               && batch.BaseRevision == currentRevision
               && batch.ResultRevision == batch.BaseRevision + 1
               && batch.Decisions.Count <= CustomRunRuleRuntimeService.MaximumActionsPerChain;
    }
}
