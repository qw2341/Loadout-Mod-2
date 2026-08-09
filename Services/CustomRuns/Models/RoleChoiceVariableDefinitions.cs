#nullable enable

namespace Loadout.Services.CustomRuns.Models;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

public enum RoleAssignmentMode
{
    HostAssigns,
    PlayersChoose,
    Random
}

public enum PlayerChoiceAudience
{
    Host,
    AllPlayers,
    Role,
    PlayerSlot
}

public enum VariableValueType
{
    Number,
    Boolean
}

public enum VariableScope
{
    Run,
    Player,
    Role,
    Combat,
    Turn,
    Rule
}

public sealed class RoleDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("name")]
    public string Name { get; set; } = "New Role";

    [JsonPropertyName("minimumPlayers")]
    public int MinimumPlayers { get; set; }

    [JsonPropertyName("maximumPlayers")]
    public int MaximumPlayers { get; set; } = 1;

    [JsonPropertyName("assignmentMode")]
    public RoleAssignmentMode AssignmentMode { get; set; }

    [JsonPropertyName("setup")]
    public RunSetupDefinition Setup { get; set; } = new();
}

public sealed class PlayerChoiceDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("name")]
    public string Name { get; set; } = "New Choice";

    [JsonPropertyName("audience")]
    public PlayerChoiceAudience Audience { get; set; } = PlayerChoiceAudience.AllPlayers;

    [JsonPropertyName("roleId")]
    public string? RoleId { get; set; }

    [JsonPropertyName("playerSlot")]
    public int? PlayerSlot { get; set; }

    [JsonPropertyName("minimumSelections")]
    public int MinimumSelections { get; set; } = 1;

    [JsonPropertyName("maximumSelections")]
    public int MaximumSelections { get; set; } = 1;

    [JsonPropertyName("pool")]
    public SelectionPoolDefinition Pool { get; set; } = new();

    [JsonPropertyName("options")]
    public List<PlayerChoiceOptionDefinition> Options { get; set; } = [];
}

public sealed class PlayerChoiceOptionDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("selection")]
    public SelectionSpec Selection { get; set; } = new();

    [JsonPropertyName("mystery")]
    public bool IsMystery { get; set; }

    [JsonPropertyName("weight")]
    public int Weight { get; set; } = 1;

    [JsonPropertyName("revealImmediately")]
    public bool RevealImmediately { get; set; } = true;
}

public sealed class VariableDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("name")]
    public string Name { get; set; } = "New Variable";

    [JsonPropertyName("valueType")]
    public VariableValueType ValueType { get; set; }

    [JsonPropertyName("scope")]
    public VariableScope Scope { get; set; }

    [JsonPropertyName("defaultNumber")]
    public decimal DefaultNumber { get; set; }

    [JsonPropertyName("defaultBoolean")]
    public bool DefaultBoolean { get; set; }
}
