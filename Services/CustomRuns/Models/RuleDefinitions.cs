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
    public NumericValueSpec Count { get; set; } = NumericValueSpec.Integer(1);

    [JsonPropertyName("untilConditions")]
    public ConditionGroupDefinition UntilConditions { get; set; } = new();
}

[JsonConverter(typeof(NumericValueSpecJsonConverter))]
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

    public static NumericValueSpec Integer(int value) => new()
    {
        Source = NumericValueSourceKind.Constant,
        Constant = value,
        ConstantKind = NumericConstantKind.Integer
    };
}

public sealed class NumericValueSpecJsonConverter : JsonConverter<NumericValueSpec>
{
    public override NumericValueSpec Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            double number = reader.GetDouble();
            return new NumericValueSpec
            {
                Source = NumericValueSourceKind.Constant,
                Constant = number,
                ConstantKind = Math.Truncate(number) == number
                    ? NumericConstantKind.Integer
                    : NumericConstantKind.Double
            };
        }

        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;
        NumericValueSpec result = new();
        if (root.TryGetProperty("source", out JsonElement source))
        {
            if (source.ValueKind == JsonValueKind.String
                && Enum.TryParse(source.GetString(), ignoreCase: true, out NumericValueSourceKind parsedSource))
                result.Source = parsedSource;
            else if (source.TryGetInt32(out int sourceValue) && Enum.IsDefined(typeof(NumericValueSourceKind), sourceValue))
                result.Source = (NumericValueSourceKind)sourceValue;
        }
        if (root.TryGetProperty("constant", out JsonElement constant) && constant.TryGetDouble(out double value))
            result.Constant = value;
        if (root.TryGetProperty("constantKind", out JsonElement constantKind))
        {
            if (constantKind.ValueKind == JsonValueKind.String
                && Enum.TryParse(constantKind.GetString(), ignoreCase: true, out NumericConstantKind parsedKind))
                result.ConstantKind = parsedKind;
            else if (constantKind.TryGetInt32(out int kindValue) && Enum.IsDefined(typeof(NumericConstantKind), kindValue))
                result.ConstantKind = (NumericConstantKind)kindValue;
        }
        if (root.TryGetProperty("referenceId", out JsonElement referenceId)
            && referenceId.ValueKind == JsonValueKind.String)
            result.ReferenceId = referenceId.GetString();
        return result;
    }

    public override void Write(Utf8JsonWriter writer, NumericValueSpec value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("source", (int)value.Source);
        writer.WriteNumber("constant", value.Constant);
        writer.WriteNumber("constantKind", (int)value.ConstantKind);
        if (!string.IsNullOrWhiteSpace(value.ReferenceId))
            writer.WriteString("referenceId", value.ReferenceId);
        writer.WriteEndObject();
    }
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
