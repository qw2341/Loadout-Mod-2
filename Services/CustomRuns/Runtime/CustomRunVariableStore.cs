#nullable enable

namespace Loadout.Services.CustomRuns.Runtime;

using System;
using System.Collections.Generic;
using System.Linq;
using Loadout.Services.CustomRuns.Compilation;
using Loadout.Services.CustomRuns.Models;

public sealed class CustomRunVariableStore
{
    public const string DefaultRoleKey = "__default_role__";

    private readonly IReadOnlyDictionary<string, ResolvedVariableDefinition> _definitions;
    private readonly IReadOnlyDictionary<ulong, string> _rolesByPlayer;
    private readonly SortedDictionary<string, CustomRunVariableValue> _values = new(StringComparer.Ordinal);

    public CustomRunVariableStore(ResolvedCustomRunSnapshot snapshot, CustomRunRuntimeState? restored = null)
    {
        _definitions = snapshot.Variables.ToDictionary(variable => variable.Id, StringComparer.Ordinal);
        _rolesByPlayer = snapshot.Players.ToDictionary(
            player => player.PlayerId,
            player => string.IsNullOrWhiteSpace(player.RoleId) ? DefaultRoleKey : player.RoleId!,
            EqualityComparer<ulong>.Default);
        if (restored is not null)
        {
            foreach ((string key, CustomRunVariableValue value) in restored.Values)
                _values[key] = new CustomRunVariableValue { Number = value.Number, Boolean = value.Boolean };
        }
        EnsureGlobalDefaults();
    }

    public bool TryGetDefinition(string id, out ResolvedVariableDefinition definition)
    {
        return _definitions.TryGetValue(id, out definition!);
    }

    public CustomRunVariableValue Read(string variableId, ulong playerId, string ruleId)
    {
        if (!_definitions.TryGetValue(variableId, out ResolvedVariableDefinition? definition))
            return new CustomRunVariableValue();
        string key = BuildKey(definition, playerId, ruleId);
        if (!_values.TryGetValue(key, out CustomRunVariableValue? value))
            _values[key] = value = CreateDefault(definition);
        return new CustomRunVariableValue { Number = value.Number, Boolean = value.Boolean };
    }

    public void Set(
        string variableId,
        IEnumerable<ulong> targetPlayerIds,
        string ruleId,
        CustomRunVariableValue value)
    {
        Mutate(variableId, targetPlayerIds, ruleId, _ => value);
    }

    public void Add(string variableId, IEnumerable<ulong> targetPlayerIds, string ruleId, double amount)
    {
        Mutate(variableId, targetPlayerIds, ruleId, current => new CustomRunVariableValue
        {
            Number = current.Number + amount
        });
    }

    public void Modify(
        string variableId,
        IEnumerable<ulong> targetPlayerIds,
        string ruleId,
        NumericModificationKind operation,
        double operand)
    {
        Mutate(variableId, targetPlayerIds, ruleId, current => new CustomRunVariableValue
        {
            Number = NumericModification.Apply(current.Number, operand, operation)
        });
    }

    public void Reset(VariableScope scope)
    {
        string prefix = $"{scope}:";
        foreach (string key in _values.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            _values.Remove(key);
        EnsureGlobalDefaults();
    }

    public SortedDictionary<string, CustomRunVariableValue> Export()
    {
        return new SortedDictionary<string, CustomRunVariableValue>(
            _values.ToDictionary(
                pair => pair.Key,
                pair => new CustomRunVariableValue
                {
                    Number = pair.Value.Number,
                    Boolean = pair.Value.Boolean
                },
                StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    private void Mutate(
        string variableId,
        IEnumerable<ulong> targetPlayerIds,
        string ruleId,
        Func<CustomRunVariableValue, CustomRunVariableValue> mutation)
    {
        if (!_definitions.TryGetValue(variableId, out ResolvedVariableDefinition? definition))
            return;
        IEnumerable<ulong> targets = targetPlayerIds.Distinct();
        if (definition.Scope is VariableScope.Run or VariableScope.Combat or VariableScope.Turn or VariableScope.Rule)
            targets = [targets.FirstOrDefault()];
        else if (definition.Scope == VariableScope.Role)
            targets = targets.GroupBy(GetRoleKey).Select(group => group.First());

        foreach (ulong playerId in targets)
        {
            string key = BuildKey(definition, playerId, ruleId);
            if (!_values.TryGetValue(key, out CustomRunVariableValue? current))
                current = CreateDefault(definition);
            CustomRunVariableValue next = mutation(current);
            _values[key] = definition.ValueType == VariableValueType.Boolean
                ? new CustomRunVariableValue { Boolean = next.Boolean }
                : new CustomRunVariableValue { Number = next.Number };
        }
    }

    private void EnsureGlobalDefaults()
    {
        foreach (ResolvedVariableDefinition definition in _definitions.Values.Where(variable =>
                     variable.Scope is VariableScope.Run or VariableScope.Combat or VariableScope.Turn))
        {
            _values.TryAdd(BuildKey(definition, 0, string.Empty), CreateDefault(definition));
        }
    }

    private string BuildKey(ResolvedVariableDefinition definition, ulong playerId, string ruleId)
    {
        string discriminator = definition.Scope switch
        {
            VariableScope.Player => playerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            VariableScope.Role => GetRoleKey(playerId),
            VariableScope.Rule => ruleId,
            _ => "global"
        };
        return $"{definition.Scope}:{definition.Id}:{discriminator}";
    }

    private string GetRoleKey(ulong playerId)
    {
        return _rolesByPlayer.GetValueOrDefault(playerId) ?? DefaultRoleKey;
    }

    private static CustomRunVariableValue CreateDefault(ResolvedVariableDefinition definition)
    {
        return definition.ValueType == VariableValueType.Boolean
            ? new CustomRunVariableValue { Boolean = definition.DefaultBoolean }
            : new CustomRunVariableValue { Number = definition.DefaultNumber };
    }
}

internal static class NumericModification
{
    public static double Apply(double current, double operand, NumericModificationKind operation)
    {
        double result = operation switch
        {
            NumericModificationKind.Set => operand,
            NumericModificationKind.Add => current + operand,
            NumericModificationKind.Subtract => current - operand,
            NumericModificationKind.Multiply => current * operand,
            NumericModificationKind.Divide when operand != 0d => current / operand,
            NumericModificationKind.Divide => current,
            _ => current
        };
        return double.IsFinite(result) ? result : current;
    }
}
