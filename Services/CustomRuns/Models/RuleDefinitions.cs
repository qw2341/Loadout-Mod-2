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
    Unlimited,
    OncePerEventChain,
    OncePerTurn,
    TimesPerTurn,
    OncePerCombat,
    TimesPerCombat,
    OncePerRun,
    TimesPerRun
}

public enum NumericValueSourceKind
{
    Constant,
    Variable,
    EventContext
}

public sealed class RuleDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("name")]
    public string Name { get; set; } = "New Rule";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

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
}

public sealed class NumericValueSpec
{
    [JsonPropertyName("source")]
    public NumericValueSourceKind Source { get; set; }

    [JsonPropertyName("constant")]
    public decimal Constant { get; set; }

    [JsonPropertyName("referenceId")]
    public string? ReferenceId { get; set; }
}
