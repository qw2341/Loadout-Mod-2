#nullable enable

namespace Loadout.Services.CustomRuns.Compilation;

using System.Collections.Generic;
using System.Text.Json.Serialization;
using Loadout.Services.Loadouts;
using Loadout.Services.CustomRuns.Models;

public sealed class ResolvedCustomRunSnapshot
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 3;

    [JsonPropertyName("hostPlayerId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ulong HostPlayerId { get; init; }

    [JsonPropertyName("sourceDefinitionId")]
    public string SourceDefinitionId { get; init; } = string.Empty;

    [JsonPropertyName("runSeed")]
    public string RunSeed { get; init; } = string.Empty;

    [JsonPropertyName("ascensionLevel")]
    public int? AscensionLevel { get; init; }

    [JsonPropertyName("modifiersEnabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ModifiersEnabled { get; init; }

    [JsonPropertyName("modifiers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RunModifierDefinition> Modifiers { get; init; } = [];

    [JsonPropertyName("players")]
    public IReadOnlyList<ResolvedPlayerSetup> Players { get; init; } = [];

    [JsonPropertyName("rules")]
    public IReadOnlyList<CompiledRuleDefinition> Rules { get; init; } = [];

    [JsonPropertyName("variables")]
    public IReadOnlyList<ResolvedVariableDefinition> Variables { get; init; } = [];

    [JsonPropertyName("requiredModIds")]
    public IReadOnlyList<string> RequiredModIds { get; init; } = [];

    [JsonPropertyName("snapshotHash")]
    public string SnapshotHash { get; init; } = string.Empty;
}

public sealed class ResolvedPlayerSetup
{
    [JsonPropertyName("playerId")]
    public ulong PlayerId { get; init; }

    [JsonPropertyName("lobbySlot")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int LobbySlot { get; init; }

    [JsonPropertyName("characterModelId")]
    public string CharacterModelId { get; init; } = string.Empty;

    [JsonPropertyName("roleId")]
    public string? RoleId { get; init; }

    [JsonPropertyName("deckModelIds")]
    public IReadOnlyList<string> DeckModelIds { get; init; } = [];

    [JsonPropertyName("overrideDeck")]
    public bool OverrideDeck { get; init; }

    [JsonPropertyName("deckEntries")]
    public IReadOnlyList<SavedCardLoadoutEntry> DeckEntries { get; init; } = [];

    [JsonPropertyName("relicModelIds")]
    public IReadOnlyList<string> RelicModelIds { get; init; } = [];

    [JsonPropertyName("overrideRelics")]
    public bool OverrideRelics { get; init; }

    [JsonPropertyName("relicEntries")]
    public IReadOnlyList<SavedRelicLoadoutEntry> RelicEntries { get; init; } = [];

    [JsonPropertyName("potionModelIds")]
    public IReadOnlyList<string> PotionModelIds { get; init; } = [];

    [JsonPropertyName("overridePotions")]
    public bool OverridePotions { get; init; }

    [JsonPropertyName("startingPowers")]
    public IReadOnlyList<StartingPowerDefinition> StartingPowers { get; init; } = [];

    [JsonPropertyName("startingMorphModelId")]
    public string? StartingMorphModelId { get; init; }

    [JsonPropertyName("potionSlots")]
    public int? PotionSlots { get; init; }

    [JsonPropertyName("startingGold")]
    public int? StartingGold { get; init; }

    [JsonPropertyName("startingCurrentHp")]
    public int? StartingCurrentHp { get; init; }

    [JsonPropertyName("startingMaxHp")]
    public int? StartingMaxHp { get; init; }

    [JsonPropertyName("baseEnergyPerTurn")]
    public int? BaseEnergyPerTurn { get; init; }

    [JsonPropertyName("cardsDrawnPerTurn")]
    public int? CardsDrawnPerTurn { get; init; }
}

public sealed class CompiledRuleDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("trigger")]
    public RuleComponentSpec Trigger { get; init; } = new();

    [JsonPropertyName("conditions")]
    public ConditionGroupDefinition Conditions { get; init; } = new();

    [JsonPropertyName("actions")]
    public IReadOnlyList<RuleComponentSpec> Actions { get; init; } = [];

    [JsonPropertyName("limit")]
    public RuleLimitDefinition Limit { get; init; } = new();
}

public sealed class ResolvedVariableDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("valueType")]
    public VariableValueType ValueType { get; init; }

    [JsonPropertyName("scope")]
    public VariableScope Scope { get; init; }

    [JsonPropertyName("defaultNumber")]
    public double DefaultNumber { get; init; }

    [JsonPropertyName("defaultBoolean")]
    public bool DefaultBoolean { get; init; }
}
