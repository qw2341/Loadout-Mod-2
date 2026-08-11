#nullable enable

namespace Loadout.Services.CustomRuns.Models;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

public sealed class CustomRunDefinition
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 3;

    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("name")]
    public string Name { get; set; } = "New Custom Run";

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public long CreatedAtUnixSeconds { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [JsonPropertyName("updatedAt")]
    public long UpdatedAtUnixSeconds { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [JsonPropertyName("setup")]
    public RunSetupDefinition Setup { get; set; } = new();

    [JsonPropertyName("roleAssignmentMode")]
    public RoleAssignmentMode RoleAssignmentMode { get; set; } = RoleAssignmentMode.PlayersChoose;

    [JsonPropertyName("defaultRoleName")]
    public string DefaultRoleName { get; set; } = "Default Role";

    [JsonPropertyName("roles")]
    public List<RoleDefinition> Roles { get; set; } = [];

    [JsonPropertyName("playerChoices")]
    public List<PlayerChoiceDefinition> PlayerChoices { get; set; } = [];

    [JsonPropertyName("variables")]
    public List<VariableDefinition> Variables { get; set; } = [];

    [JsonPropertyName("rules")]
    public List<RuleDefinition> Rules { get; set; } = [];

    [JsonPropertyName("requiredModIds")]
    public List<string> RequiredModIds { get; set; } = [];
}
