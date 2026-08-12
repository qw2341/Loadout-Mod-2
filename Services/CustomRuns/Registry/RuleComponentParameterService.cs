#nullable enable

namespace Loadout.Services.CustomRuns.Registry;

using System;
using System.Text.Json;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.CustomRuns.Persistence;

public static class RuleComponentParameterService
{
    public static void ApplyDefaults(RuleComponentSpec component, RuleComponentDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(descriptor);

        foreach (RuleParameterDescriptor parameter in descriptor.Parameters)
        {
            if (component.Parameters.ContainsKey(parameter.Key))
                continue;

            switch (parameter.Kind)
            {
                case RuleParameterKind.Integer:
                    Set(component, parameter.Key, parameter.DefaultInteger);
                    break;
                case RuleParameterKind.Boolean:
                    Set(component, parameter.Key, false);
                    break;
                case RuleParameterKind.Enum:
                    Set(component, parameter.Key, parameter.Options.Count > 0 ? parameter.Options[0].Id : string.Empty);
                    break;
                case RuleParameterKind.PlayerTarget:
                    Set(component, parameter.Key, new RuleTargetSpec());
                    break;
                case RuleParameterKind.NumericSource:
                    Set(component, parameter.Key, new NumericValueSpec
                    {
                        Source = NumericValueSourceKind.Constant,
                        Constant = parameter.DefaultNumeric,
                        ConstantKind = parameter.DefaultConstantKind
                    });
                    break;
                case RuleParameterKind.ModelFilter:
                    if (component.Parameters.TryGetValue("filter", out JsonElement legacyFilter))
                    {
                        component.Parameters.Remove("filter");
                        component.Parameters[parameter.Key] = legacyFilter;
                        break;
                    }
                    string legacyCardId = GetString(component, "cardId");
                    Set(component, parameter.Key, new ModelMatchSpec
                    {
                        ModelKind = parameter.ModelKind,
                        ModelIds = string.IsNullOrWhiteSpace(legacyCardId) ? [] : [legacyCardId]
                    });
                    component.Parameters.Remove("cardId");
                    break;
                default:
                    Set(component, parameter.Key, string.Empty);
                    break;
            }
        }
    }

    public static void Set<T>(RuleComponentSpec component, string key, T value)
    {
        ArgumentNullException.ThrowIfNull(component);
        component.Parameters[key] = JsonSerializer.SerializeToElement(
            value,
            CustomRunSerializationService.SharedJsonOptions);
    }

    public static bool TryGet<T>(RuleComponentSpec component, string key, out T value)
    {
        value = default!;
        if (!component.Parameters.TryGetValue(key, out JsonElement element)
            || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        try
        {
            T? decoded = element.Deserialize<T>(CustomRunSerializationService.SharedJsonOptions);
            if (decoded is null)
                return false;
            value = decoded;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string GetString(RuleComponentSpec component, string key)
    {
        return TryGet(component, key, out string value) ? value : string.Empty;
    }

    public static int GetInt32(RuleComponentSpec component, string key, int fallback = 0)
    {
        return TryGet(component, key, out int value) ? value : fallback;
    }

    public static bool GetBoolean(RuleComponentSpec component, string key, bool fallback = false)
    {
        return TryGet(component, key, out bool value) ? value : fallback;
    }
}
