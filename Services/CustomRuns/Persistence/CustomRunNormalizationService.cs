#nullable enable

namespace Loadout.Services.CustomRuns.Persistence;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Loadout.Services.CustomRuns.Models;

public static class CustomRunNormalizationService
{
    public static CustomRunDefinition Normalize(CustomRunDefinition definition)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        definition.SchemaVersion = CustomRunStorageService.CurrentSchemaVersion;
        definition.Id = NormalizeObjectId(definition.Id);
        definition.Name = string.IsNullOrWhiteSpace(definition.Name)
            ? "New Custom Run"
            : definition.Name.Trim();
        definition.Description = (definition.Description ?? string.Empty).Trim();
        definition.CreatedAtUnixSeconds = definition.CreatedAtUnixSeconds > 0
            ? definition.CreatedAtUnixSeconds
            : now;
        definition.UpdatedAtUnixSeconds = definition.UpdatedAtUnixSeconds > 0
            ? definition.UpdatedAtUnixSeconds
            : definition.CreatedAtUnixSeconds;
        definition.Setup = NormalizeSetup(definition.Setup ?? new RunSetupDefinition());
        definition.Roles = (definition.Roles ?? [])
            .Where(role => role is not null)
            .Select(NormalizeRole)
            .ToList();
        definition.PlayerChoices = (definition.PlayerChoices ?? [])
            .Where(choice => choice is not null)
            .Select(NormalizeChoice)
            .ToList();
        definition.Variables = (definition.Variables ?? [])
            .Where(variable => variable is not null)
            .Select(NormalizeVariable)
            .ToList();
        definition.Rules = (definition.Rules ?? [])
            .Where(rule => rule is not null)
            .Select(NormalizeRule)
            .ToList();
        definition.RequiredModIds = NormalizeStrings(definition.RequiredModIds);
        return definition;
    }

    public static RunSetupDefinition NormalizeSetup(RunSetupDefinition setup)
    {
        setup.Character = NormalizeSelection(setup.Character, SelectionModelKind.Character);
        setup.StartingDeck = NormalizeSelection(setup.StartingDeck, SelectionModelKind.Card);
        setup.StartingRelics = NormalizeSelection(setup.StartingRelics, SelectionModelKind.Relic);
        setup.StartingPotions = NormalizeSelection(setup.StartingPotions, SelectionModelKind.Potion);
        setup.RunSeed = string.IsNullOrWhiteSpace(setup.RunSeed) ? null : setup.RunSeed.Trim();
        return setup;
    }

    public static SelectionSpec NormalizeSelection(SelectionSpec? selection, SelectionModelKind fallbackKind)
    {
        selection ??= SelectionSpec.Default(fallbackKind);
        if (!Enum.IsDefined(selection.Kind))
            selection.Kind = fallbackKind;
        if (!Enum.IsDefined(selection.Mode))
            selection.Mode = SelectionMode.Default;

        selection.FixedModelIds = NormalizeOrderedStrings(selection.FixedModelIds);
        selection.PlayerChoiceId = string.IsNullOrWhiteSpace(selection.PlayerChoiceId)
            ? null
            : selection.PlayerChoiceId.Trim();
        selection.Pool = NormalizePool(selection.Pool ?? new SelectionPoolDefinition(), selection.Kind);

        if (selection.Mode == SelectionMode.Default)
        {
            selection.FixedModelIds.Clear();
            selection.PlayerChoiceId = null;
        }

        return selection;
    }

    public static CustomRunDefinition Clone(CustomRunDefinition definition)
    {
        string json = JsonSerializer.Serialize(definition, CustomRunSerializationService.SharedJsonOptions);
        return JsonSerializer.Deserialize<CustomRunDefinition>(json, CustomRunSerializationService.SharedJsonOptions)
               ?? new CustomRunDefinition();
    }

    private static SelectionPoolDefinition NormalizePool(SelectionPoolDefinition pool, SelectionModelKind kind)
    {
        pool.Kind = Enum.IsDefined(pool.Kind) ? pool.Kind : kind;
        pool.IncludedModelIds = NormalizeStrings(pool.IncludedModelIds);
        pool.ExcludedModelIds = NormalizeStrings(pool.ExcludedModelIds);
        pool.AllowedModIds = NormalizeStrings(pool.AllowedModIds);
        pool.ExcludedModIds = NormalizeStrings(pool.ExcludedModIds);
        pool.Categories = NormalizeStrings(pool.Categories);
        pool.Types = NormalizeStrings(pool.Types);
        pool.MaximumCopiesPerItem = Math.Max(1, pool.MaximumCopiesPerItem);
        if (!pool.AllowDuplicates)
            pool.MaximumCopiesPerItem = 1;
        return pool;
    }

    private static RoleDefinition NormalizeRole(RoleDefinition role)
    {
        role.Id = NormalizeObjectId(role.Id);
        role.Name = string.IsNullOrWhiteSpace(role.Name) ? "New Role" : role.Name.Trim();
        role.MinimumPlayers = Math.Max(0, role.MinimumPlayers);
        role.MaximumPlayers = Math.Max(role.MinimumPlayers, role.MaximumPlayers);
        if (!Enum.IsDefined(role.AssignmentMode))
            role.AssignmentMode = RoleAssignmentMode.HostAssigns;
        role.Setup = NormalizeSetup(role.Setup ?? new RunSetupDefinition());
        return role;
    }

    private static PlayerChoiceDefinition NormalizeChoice(PlayerChoiceDefinition choice)
    {
        choice.Id = NormalizeObjectId(choice.Id);
        choice.Name = string.IsNullOrWhiteSpace(choice.Name) ? "New Choice" : choice.Name.Trim();
        if (!Enum.IsDefined(choice.Audience))
            choice.Audience = PlayerChoiceAudience.AllPlayers;
        choice.RoleId = string.IsNullOrWhiteSpace(choice.RoleId) ? null : choice.RoleId.Trim();
        choice.MinimumSelections = Math.Max(0, choice.MinimumSelections);
        choice.MaximumSelections = Math.Max(choice.MinimumSelections, choice.MaximumSelections);
        choice.Pool = NormalizePool(choice.Pool ?? new SelectionPoolDefinition(), choice.Pool?.Kind ?? SelectionModelKind.Card);
        choice.Options = (choice.Options ?? [])
            .Where(option => option is not null)
            .Select(option =>
            {
                option.Id = NormalizeObjectId(option.Id);
                option.Label = (option.Label ?? string.Empty).Trim();
                option.Selection = NormalizeSelection(option.Selection, option.Selection?.Kind ?? SelectionModelKind.Card);
                option.Weight = Math.Max(1, option.Weight);
                return option;
            })
            .ToList();
        return choice;
    }

    private static VariableDefinition NormalizeVariable(VariableDefinition variable)
    {
        variable.Id = NormalizeObjectId(variable.Id);
        variable.Name = string.IsNullOrWhiteSpace(variable.Name) ? "New Variable" : variable.Name.Trim();
        if (!Enum.IsDefined(variable.ValueType))
            variable.ValueType = VariableValueType.Number;
        if (!Enum.IsDefined(variable.Scope))
            variable.Scope = VariableScope.Run;
        return variable;
    }

    public static RuleDefinition NormalizeRule(RuleDefinition rule)
    {
        rule.Id = NormalizeObjectId(rule.Id);
        rule.Name = string.IsNullOrWhiteSpace(rule.Name) ? "New Rule" : rule.Name.Trim();
        rule.Description = (rule.Description ?? string.Empty).Trim();
        rule.Trigger = NormalizeComponent(rule.Trigger);
        rule.Conditions = NormalizeConditionGroup(rule.Conditions ?? new ConditionGroupDefinition());
        rule.Actions = (rule.Actions ?? [])
            .Where(action => action is not null)
            .Select(NormalizeComponent)
            .ToList();
        rule.Limit ??= new RuleLimitDefinition();
        if (!Enum.IsDefined(rule.Limit.Kind))
            rule.Limit.Kind = RuleLimitKind.Unlimited;
        rule.Limit.Count = Math.Max(1, rule.Limit.Count);
        return rule;
    }

    public static RuleDefinition CloneRule(RuleDefinition rule)
    {
        string json = JsonSerializer.Serialize(rule, CustomRunSerializationService.SharedJsonOptions);
        return JsonSerializer.Deserialize<RuleDefinition>(json, CustomRunSerializationService.SharedJsonOptions)
               ?? new RuleDefinition();
    }

    private static RuleComponentSpec NormalizeComponent(RuleComponentSpec? component)
    {
        component ??= new RuleComponentSpec();
        component.TypeId = (component.TypeId ?? string.Empty).Trim();
        SortedDictionary<string, JsonElement> parameters = new(StringComparer.Ordinal);
        foreach ((string key, JsonElement value) in component.Parameters ?? new SortedDictionary<string, JsonElement>())
        {
            string normalizedKey = (key ?? string.Empty).Trim();
            if (normalizedKey.Length > 0)
                parameters[normalizedKey] = value.Clone();
        }
        component.Parameters = parameters;
        return component;
    }

    private static ConditionGroupDefinition NormalizeConditionGroup(ConditionGroupDefinition group)
    {
        if (!Enum.IsDefined(group.Operator))
            group.Operator = ConditionGroupOperator.And;
        group.Conditions = (group.Conditions ?? [])
            .Where(condition => condition is not null)
            .Select(NormalizeComponent)
            .ToList();
        group.Groups = (group.Groups ?? [])
            .Where(child => child is not null)
            .Select(NormalizeConditionGroup)
            .ToList();
        return group;
    }

    private static string NormalizeObjectId(string? id)
    {
        return string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id.Trim();
    }

    private static List<string> NormalizeStrings(IEnumerable<string>? values)
    {
        return (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
    }

    private static List<string> NormalizeOrderedStrings(IEnumerable<string>? values)
    {
        return (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToList();
    }
}
