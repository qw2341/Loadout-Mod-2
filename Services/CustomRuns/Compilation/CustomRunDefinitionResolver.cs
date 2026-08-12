#nullable enable

namespace Loadout.Services.CustomRuns.Compilation;

using System;
using System.Collections.Generic;
using System.Linq;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.CustomRuns.PermanentRules;
using Loadout.Services.CustomRuns.Persistence;

public static class CustomRunDefinitionResolver
{
    public static CustomRunDefinition WithEnabledPermanentRules(CustomRunDefinition source)
    {
        IReadOnlyList<PermanentRuleBundle> permanentBundles = PermanentRuleStorageService.GetBundles()
            .Where(bundle => bundle.Rule.Enabled)
            .ToList();
        return MergeWithPermanentBundles(source, permanentBundles);
    }

    public static CustomRunDefinition MergeWithPermanentBundles(
        CustomRunDefinition source,
        IReadOnlyList<PermanentRuleBundle> permanentBundles)
    {
        CustomRunDefinition effective = CustomRunNormalizationService.Clone(source);
        HashSet<string> scenarioRuleIds = effective.Rules.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal);
        HashSet<string> scenarioVariableIds = effective.Variables.Select(variable => variable.Id).ToHashSet(StringComparer.Ordinal);
        List<RuleDefinition> rules = permanentBundles
            .Select(bundle => bundle.Rule)
            .Where(rule => !scenarioRuleIds.Contains(rule.Id))
            .Select(CustomRunNormalizationService.CloneRule)
            .ToList();
        rules.AddRange(effective.Rules);
        effective.Rules = rules;
        List<VariableDefinition> variables = permanentBundles
            .SelectMany(bundle => bundle.Variables)
            .Where(variable => !scenarioVariableIds.Contains(variable.Id))
            .GroupBy(variable => variable.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        variables.AddRange(effective.Variables);
        effective.Variables = variables;
        return effective;
    }
}
