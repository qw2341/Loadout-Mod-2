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
        CustomRunDefinition effective = CustomRunNormalizationService.Clone(source);
        List<RuleDefinition> permanentRules = PermanentRuleStorageService.GetRules()
            .Where(rule => rule.Enabled)
            .GroupBy(RuleBehaviorHashService.Compute, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(CustomRunNormalizationService.CloneRule)
            .ToList();
        HashSet<string> permanentHashes = permanentRules
            .Select(RuleBehaviorHashService.Compute)
            .ToHashSet(StringComparer.Ordinal);
        List<RuleDefinition> rules = permanentRules;
        rules.AddRange(effective.Rules.Where(rule => !permanentHashes.Contains(RuleBehaviorHashService.Compute(rule))));
        effective.Rules = rules;
        return effective;
    }
}
