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
        RuleLimitDefinition limit,
        CustomRunRuleCounterState counter,
        int priorChainExecutions,
        bool untilConditionMet)
    {
        int maximum = Math.Max(1, limit.Count);
        return limit.Kind switch
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
}
