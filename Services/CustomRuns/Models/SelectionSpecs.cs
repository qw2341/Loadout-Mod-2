#nullable enable

namespace Loadout.Services.CustomRuns.Models;

using System.Collections.Generic;
using System.Text.Json.Serialization;

public enum SelectionMode
{
    Default,
    Fixed,
    Random,
    PlayerChoice
}

public enum SelectionModelKind
{
    Card,
    Relic,
    Potion,
    Character,
    Power,
    Monster
}

public sealed class SelectionSpec
{
    [JsonPropertyName("kind")]
    public SelectionModelKind Kind { get; set; }

    [JsonPropertyName("mode")]
    public SelectionMode Mode { get; set; }

    [JsonPropertyName("fixedModelIds")]
    public List<string> FixedModelIds { get; set; } = [];

    [JsonPropertyName("pool")]
    public SelectionPoolDefinition Pool { get; set; } = new();

    [JsonPropertyName("playerChoiceId")]
    public string? PlayerChoiceId { get; set; }

    public static SelectionSpec Default(SelectionModelKind kind)
    {
        return new SelectionSpec
        {
            Kind = kind,
            Pool = new SelectionPoolDefinition { Kind = kind }
        };
    }
}

public sealed class SelectionPoolDefinition
{
    [JsonPropertyName("kind")]
    public SelectionModelKind Kind { get; set; }

    [JsonPropertyName("includedModelIds")]
    public List<string> IncludedModelIds { get; set; } = [];

    [JsonPropertyName("excludedModelIds")]
    public List<string> ExcludedModelIds { get; set; } = [];

    [JsonPropertyName("allowedModIds")]
    public List<string> AllowedModIds { get; set; } = [];

    [JsonPropertyName("excludedModIds")]
    public List<string> ExcludedModIds { get; set; } = [];

    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = [];

    [JsonPropertyName("types")]
    public List<string> Types { get; set; } = [];

    [JsonPropertyName("allowDuplicates")]
    public bool AllowDuplicates { get; set; }

    [JsonPropertyName("maximumCopiesPerItem")]
    public int MaximumCopiesPerItem { get; set; } = 1;
}
