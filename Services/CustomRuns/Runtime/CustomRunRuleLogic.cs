#nullable enable

namespace Loadout.Services.CustomRuns.Runtime;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Loadout.Services.CustomRuns.Compilation;
using Loadout.Services.CustomRuns.Models;

public static class CustomRunConditionGroupLogic
{
    public static bool Evaluate(
        ConditionGroupDefinition group,
        Func<RuleComponentSpec, bool> evaluateCondition)
    {
        var results = group.Conditions
            .Select(condition => condition.Negated
                ? !evaluateCondition(condition)
                : evaluateCondition(condition))
            .Concat(group.Groups.Select(child => Evaluate(child, evaluateCondition)));
        return group.Operator == ConditionGroupOperator.And
            ? results.All(result => result)
            : results.Any(result => result);
    }
}

public static class CustomRunRuleLimitLogic
{
    public static bool Allows(
        RuleLimitKind kind,
        int maximum,
        CustomRunRuleCounterState counter,
        int priorChainExecutions,
        bool untilConditionMet)
    {
        maximum = Math.Max(1, maximum);
        return kind switch
        {
            RuleLimitKind.Unlimited => true,
            RuleLimitKind.OncePerEventChain => priorChainExecutions == 0,
            RuleLimitKind.OncePerTurn or RuleLimitKind.TimesPerTurn => counter.Turn < maximum,
            RuleLimitKind.OncePerCombat or RuleLimitKind.TimesPerCombat => counter.Combat < maximum,
            RuleLimitKind.OncePerRun or RuleLimitKind.TimesPerRun => counter.Run < maximum,
            RuleLimitKind.UntilCondition => !untilConditionMet,
            _ => false
        };
    }
}

public static class CustomRunDeterministicRng
{
    public static int NextIndex(string runSeed, long sequence, string context, int count)
    {
        if (count <= 0)
            return -1;
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{runSeed}\n{sequence}\n{context}"));
        return (int)(BitConverter.ToUInt64(digest, 0) % (ulong)count);
    }
}

public static class CustomRunRulePlan
{
    private static readonly HashSet<string> CombatLifecycleTriggers = new(StringComparer.Ordinal)
    {
        "Loadout2:CombatStart",
        "Loadout2:CombatEnd",
        "Loadout2:TurnStart",
        "Loadout2:TurnEnd"
    };

    private static readonly HashSet<string> TurnLifecycleTriggers = new(StringComparer.Ordinal)
    {
        "Loadout2:TurnStart",
        "Loadout2:TurnEnd"
    };

    public static IReadOnlyDictionary<string, IReadOnlyList<CompiledRuleDefinition>> Build(
        IEnumerable<CompiledRuleDefinition> rules)
    {
        Dictionary<string, List<CompiledRuleDefinition>> plan = new(StringComparer.Ordinal);
        foreach (CompiledRuleDefinition rule in rules)
        {
            if (!plan.TryGetValue(rule.Trigger.TypeId, out List<CompiledRuleDefinition>? triggerRules))
                plan[rule.Trigger.TypeId] = triggerRules = [];
            triggerRules.Add(rule);
        }
        return plan.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<CompiledRuleDefinition>)pair.Value,
            StringComparer.Ordinal);
    }

    public static IReadOnlySet<string> GetTriggerIds(IEnumerable<CompiledRuleDefinition> rules)
    {
        return rules.Select(rule => rule.Trigger.TypeId).ToHashSet(StringComparer.Ordinal);
    }

    public static bool NeedsCombatLifecycle(ResolvedCustomRunSnapshot snapshot)
    {
        return snapshot.Rules.Any(rule =>
                   CombatLifecycleTriggers.Contains(rule.Trigger.TypeId)
                   || rule.Limit.Kind is RuleLimitKind.OncePerCombat
                       or RuleLimitKind.TimesPerCombat
                       or RuleLimitKind.OncePerTurn
                       or RuleLimitKind.TimesPerTurn)
               || snapshot.Variables.Any(variable =>
                   variable.Scope is VariableScope.Combat or VariableScope.Turn);
    }

    public static bool NeedsTurnLifecycle(ResolvedCustomRunSnapshot snapshot)
    {
        return snapshot.Rules.Any(rule =>
                   TurnLifecycleTriggers.Contains(rule.Trigger.TypeId)
                   || rule.Limit.Kind is RuleLimitKind.OncePerTurn or RuleLimitKind.TimesPerTurn)
               || snapshot.Variables.Any(variable => variable.Scope == VariableScope.Turn);
    }

    public static bool NeedsPlayerChoices(ResolvedCustomRunSnapshot snapshot)
    {
        return snapshot.Rules
            .SelectMany(rule => rule.Actions)
            .Any(action => action.Parameters.TryGetValue("selectionMode", out System.Text.Json.JsonElement value)
                           && value.ValueKind == System.Text.Json.JsonValueKind.String
                           && string.Equals(value.GetString(), "Choose", StringComparison.Ordinal));
    }
}
