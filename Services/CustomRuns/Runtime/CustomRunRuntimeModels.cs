#nullable enable

namespace Loadout.Services.CustomRuns.Runtime;

using System.Collections.Generic;
using System.Text.Json.Serialization;
using Loadout.Services.CustomRuns.Models;

public sealed class CustomRunRuntimeEvent
{
    [JsonPropertyName("snapshotHash")]
    public string SnapshotHash { get; set; } = string.Empty;

    [JsonPropertyName("enqueuedRevision")]
    public long EnqueuedRevision { get; set; }

    [JsonPropertyName("eventId")]
    public long EventId { get; set; }

    [JsonPropertyName("chainId")]
    public long ChainId { get; set; }

    [JsonPropertyName("depth")]
    public int Depth { get; set; }

    [JsonPropertyName("triggerId")]
    public string TriggerId { get; set; } = string.Empty;

    [JsonPropertyName("triggeringPlayerId")]
    public ulong TriggeringPlayerId { get; set; }

    [JsonPropertyName("modelKind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SelectionModelKind? ModelKind { get; set; }

    [JsonPropertyName("modelId")]
    public string ModelId { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public double Amount { get; set; }
}

public sealed class CustomRunResolvedDecision
{
    [JsonPropertyName("ruleId")]
    public string RuleId { get; set; } = string.Empty;

    [JsonPropertyName("actionTypeId")]
    public string ActionTypeId { get; set; } = string.Empty;

    [JsonPropertyName("targetPlayerIds")]
    public List<ulong> TargetPlayerIds { get; set; } = [];

    [JsonPropertyName("modelIds")]
    public List<string> ModelIds { get; set; } = [];

    [JsonPropertyName("modelIdsByPlayer")]
    public SortedDictionary<ulong, List<string>> ModelIdsByPlayer { get; set; } = [];

    [JsonPropertyName("amount")]
    public double Amount { get; set; }

    [JsonPropertyName("numericOperation")]
    public NumericModificationKind NumericOperation { get; set; }

    [JsonPropertyName("variableId")]
    public string VariableId { get; set; } = string.Empty;

    [JsonPropertyName("booleanValue")]
    public bool BooleanValue { get; set; }

    [JsonPropertyName("isBoolean")]
    public bool IsBoolean { get; set; }

    [JsonPropertyName("pile")]
    public string Pile { get; set; } = string.Empty;
}

public sealed class CustomRunRuleCounterState
{
    [JsonPropertyName("run")]
    public int Run { get; set; }

    [JsonPropertyName("combat")]
    public int Combat { get; set; }

    [JsonPropertyName("turn")]
    public int Turn { get; set; }
}

public sealed class CustomRunVariableValue
{
    [JsonPropertyName("number")]
    public double Number { get; set; }

    [JsonPropertyName("boolean")]
    public bool Boolean { get; set; }
}

public sealed class CustomRunRuntimeState
{
    [JsonPropertyName("values")]
    public SortedDictionary<string, CustomRunVariableValue> Values { get; set; } = new(System.StringComparer.Ordinal);

    [JsonPropertyName("ruleCounters")]
    public SortedDictionary<string, CustomRunRuleCounterState> RuleCounters { get; set; } = new(System.StringComparer.Ordinal);

    [JsonPropertyName("rngSequence")]
    public long RngSequence { get; set; }

    [JsonPropertyName("eventSequence")]
    public long EventSequence { get; set; }

    [JsonPropertyName("revision")]
    public long Revision { get; set; }

    [JsonPropertyName("setupApplied")]
    public bool SetupApplied { get; set; }

    [JsonPropertyName("runStartEmitted")]
    public bool RunStartEmitted { get; set; }

    [JsonPropertyName("combatActive")]
    public bool CombatActive { get; set; }

    [JsonPropertyName("playerTurnActive")]
    public bool PlayerTurnActive { get; set; }

    [JsonPropertyName("pendingEventModelId")]
    public string PendingEventModelId { get; set; } = string.Empty;

    [JsonPropertyName("lastCompletedRoomToken")]
    public string LastCompletedRoomToken { get; set; } = string.Empty;
}

public sealed class CustomRunRuntimeSaveEnvelope
{
    [JsonPropertyName("envelopeVersion")]
    public int EnvelopeVersion { get; set; } = 1;

    [JsonPropertyName("snapshot")]
    public Loadout.Services.CustomRuns.Compilation.ResolvedCustomRunSnapshot Snapshot { get; set; } = new();

    [JsonPropertyName("runtime")]
    public CustomRunRuntimeState Runtime { get; set; } = new();
}

public sealed class CustomRunDecisionBatch
{
    [JsonPropertyName("snapshotHash")]
    public string SnapshotHash { get; set; } = string.Empty;

    [JsonPropertyName("eventId")]
    public long EventId { get; set; }

    [JsonPropertyName("baseRevision")]
    public long BaseRevision { get; set; }

    [JsonPropertyName("resultRevision")]
    public long ResultRevision { get; set; }

    [JsonPropertyName("decisions")]
    public List<CustomRunResolvedDecision> Decisions { get; set; } = [];
}

public sealed class CustomRunChoiceRequest
{
    [JsonPropertyName("snapshotHash")]
    public string SnapshotHash { get; set; } = string.Empty;

    [JsonPropertyName("requestId")]
    public long RequestId { get; set; }

    [JsonPropertyName("eventId")]
    public long EventId { get; set; }

    [JsonPropertyName("revision")]
    public long Revision { get; set; }

    [JsonPropertyName("targetPlayerId")]
    public ulong TargetPlayerId { get; set; }

    [JsonPropertyName("modelKind")]
    public SelectionModelKind ModelKind { get; set; }

    [JsonPropertyName("allowedModelIds")]
    public List<string> AllowedModelIds { get; set; } = [];

    [JsonPropertyName("minimum")]
    public int Minimum { get; set; }

    [JsonPropertyName("maximum")]
    public int Maximum { get; set; }

    [JsonPropertyName("canSkip")]
    public bool CanSkip { get; set; }
}

public sealed class CustomRunChoiceResponse
{
    [JsonPropertyName("snapshotHash")]
    public string SnapshotHash { get; set; } = string.Empty;

    [JsonPropertyName("requestId")]
    public long RequestId { get; set; }

    [JsonPropertyName("revision")]
    public long Revision { get; set; }

    [JsonPropertyName("cancelled")]
    public bool Cancelled { get; set; }

    [JsonPropertyName("selectedModelIds")]
    public List<string> SelectedModelIds { get; set; } = [];
}
