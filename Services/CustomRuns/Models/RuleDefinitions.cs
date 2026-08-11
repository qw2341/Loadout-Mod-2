#nullable enable

namespace Loadout.Services.CustomRuns.Models;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

public enum ConditionGroupOperator
{
    And,
    Or
}

public enum RuleLimitKind
{
    Unlimited = 0,
    OncePerEventChain = 1,
    OncePerTurn = 2,
    TimesPerTurn = 3,
    OncePerCombat = 4,
    TimesPerCombat = 5,
    OncePerRun = 6,
    TimesPerRun = 7,
    UntilCondition = 8
}

public enum NumericValueSourceKind
{
    Constant,
    Variable,
    EventContext
}

public enum NumericConstantKind
{
    Integer,
    Double
}

public enum ModelMatchKind
{
    SpecificModels,
    Pool,
    Type,
    Rarity,
    Keyword,
    Tag,
    EnergyCost,
    TextContains,
    Mod,
    Act,
    MonsterCategory
}

public sealed class RuleDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("name")]
    public string Name { get; set; } = "New Rule";

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("contentHash")]
    public string ContentHash { get; set; } = string.Empty;

    [JsonPropertyName("trigger")]
    public RuleComponentSpec Trigger { get; set; } = new();

    [JsonPropertyName("conditions")]
    public ConditionGroupDefinition Conditions { get; set; } = new();

    [JsonPropertyName("actions")]
    public List<RuleComponentSpec> Actions { get; set; } = [];

    [JsonPropertyName("limit")]
    public RuleLimitDefinition Limit { get; set; } = new();
}

public sealed class RuleComponentSpec
{
    [JsonPropertyName("typeId")]
    public string TypeId { get; set; } = string.Empty;

    [JsonPropertyName("parameters")]
    public SortedDictionary<string, JsonElement> Parameters { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("negated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Negated { get; set; }
}

public sealed class ConditionGroupDefinition
{
    [JsonPropertyName("operator")]
    public ConditionGroupOperator Operator { get; set; }

    [JsonPropertyName("conditions")]
    public List<RuleComponentSpec> Conditions { get; set; } = [];

    [JsonPropertyName("groups")]
    public List<ConditionGroupDefinition> Groups { get; set; } = [];
}

public sealed class RuleLimitDefinition
{
    [JsonPropertyName("kind")]
    public RuleLimitKind Kind { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; } = 1;

    [JsonPropertyName("untilConditions")]
    public ConditionGroupDefinition UntilConditions { get; set; } = new();
}

public sealed class NumericValueSpec
{
    [JsonPropertyName("source")]
    public NumericValueSourceKind Source { get; set; }

    [JsonPropertyName("constant")]
    public double Constant { get; set; }

    [JsonPropertyName("constantKind")]
    public NumericConstantKind ConstantKind { get; set; }

    [JsonPropertyName("referenceId")]
    public string? ReferenceId { get; set; }
}

public sealed class RuleTargetSpec
{
    [JsonPropertyName("typeId")]
    public string TypeId { get; set; } = "Loadout2:TriggeringPlayer";

    [JsonPropertyName("parameters")]
    public SortedDictionary<string, JsonElement> Parameters { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ModelMatchSpec
{
    [JsonPropertyName("kind")]
    public ModelMatchKind Kind { get; set; }

    [JsonPropertyName("modelKind")]
    public SelectionModelKind ModelKind { get; set; }

    [JsonPropertyName("modelIds")]
    public List<string> ModelIds { get; set; } = [];

    [JsonPropertyName("cardIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? LegacyCardIds
    {
        get => null;
        set
        {
            if (value is { Count: > 0 } && ModelIds.Count == 0)
                ModelIds = value;
        }
    }

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
}
