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
        HashSet<string> customRunRuleIds = effective.Rules
            .Select(rule => rule.Id)
            .ToHashSet(StringComparer.Ordinal);
        List<RuleDefinition> rules = PermanentRuleStorageService.GetRules()
            .Where(rule => rule.Enabled && !customRunRuleIds.Contains(rule.Id))
            .Select(CustomRunNormalizationService.CloneRule)
            .ToList();
        rules.AddRange(effective.Rules);
        effective.Rules = rules;
        return effective;
    }
}
