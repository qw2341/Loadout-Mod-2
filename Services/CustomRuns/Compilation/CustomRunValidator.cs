#nullable enable

namespace Loadout.Services.CustomRuns.Compilation;

using System;
using System.Collections.Generic;
using System.Linq;
using Loadout.Services.CustomRuns.Catalog;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.CustomRuns.Persistence;
using Loadout.Services.CustomRuns.Registry;

public enum CustomRunValidationSeverity
{
    Warning,
    Error
}

public sealed record CustomRunValidationIssue(
    CustomRunValidationSeverity Severity,
    string Section,
    string ObjectId,
    string Message);

public sealed class CustomRunValidationResult
{
    public List<CustomRunValidationIssue> Issues { get; } = [];
    public bool IsValid => Issues.All(issue => issue.Severity != CustomRunValidationSeverity.Error);
}

public static class CustomRunValidator
{
    public static CustomRunValidationResult Validate(CustomRunDefinition source)
    {
        CustomRunDefinition definition = CustomRunNormalizationService.Normalize(
            CustomRunNormalizationService.Clone(source));
        CustomRunRegistry.EnsureBuiltInsRegistered();
        CustomRunValidationResult result = new();

        ValidateDefinition(definition, result);
        ValidateSetup(definition.Setup, result, "Run Setup", definition.Id);
        ValidateRoles(definition, result);
        ValidateChoices(definition, result);
        ValidateVariables(definition, result);
        ValidateRules(definition, result);
        return result;
    }

    private static void ValidateDefinition(CustomRunDefinition definition, CustomRunValidationResult result)
    {
        if (definition.SchemaVersion != CustomRunStorageService.CurrentSchemaVersion)
            Error(result, "Overview", definition.Id, $"Unsupported schema version {definition.SchemaVersion}.");
        if (!IsGuidStyleId(definition.Id))
            Error(result, "Overview", definition.Id, "Definition ID is not a GUID-style ID.");
        if (string.IsNullOrWhiteSpace(definition.Name))
            Error(result, "Overview", definition.Id, "Name is required.");
    }

    private static void ValidateSetup(
        RunSetupDefinition setup,
        CustomRunValidationResult result,
        string section,
        string objectId)
    {
        ValidateSelection(setup.Character, result, section, objectId);
        ValidateSelection(setup.StartingDeck, result, section, objectId);
        ValidateSelection(setup.StartingRelics, result, section, objectId);
        ValidateSelection(setup.StartingPotions, result, section, objectId);

        Range(setup.PotionSlots, 0, 20, "Potion slots", result, section, objectId);
        Range(setup.StartingGold, 0, 999999, "Starting gold", result, section, objectId);
        Range(setup.StartingCurrentHp, 1, 99999, "Starting current HP", result, section, objectId);
        Range(setup.StartingMaxHp, 1, 99999, "Starting max HP", result, section, objectId);
        Range(setup.BaseEnergyPerTurn, 0, 99, "Base energy", result, section, objectId);
        Range(setup.CardsDrawnPerTurn, 0, 99, "Cards drawn", result, section, objectId);
        if (setup.StartingCurrentHp.HasValue && setup.StartingMaxHp.HasValue
            && setup.StartingCurrentHp > setup.StartingMaxHp)
        {
            Error(result, section, objectId, "Starting current HP cannot exceed starting max HP.");
        }
    }

    private static void ValidateSelection(
        SelectionSpec selection,
        CustomRunValidationResult result,
        string section,
        string objectId)
    {
        if (selection.Mode == SelectionMode.Fixed && selection.FixedModelIds.Count == 0)
            Error(result, section, objectId, $"Fixed {selection.Kind} selection has no model.");
        if (selection.Mode == SelectionMode.PlayerChoice && string.IsNullOrWhiteSpace(selection.PlayerChoiceId))
            Error(result, section, objectId, $"{selection.Kind} player choice has no choice ID.");
        if (selection.Pool.MaximumCopiesPerItem < 1)
            Error(result, section, objectId, $"{selection.Kind} pool copy limit must be at least 1.");

        if (selection.Mode != SelectionMode.Fixed)
            return;

        foreach (string id in selection.FixedModelIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!CustomRunCatalogService.TryResolve(selection.Kind, id, out _))
                Error(result, section, objectId, $"Unknown {selection.Kind.ToString().ToLowerInvariant()} '{id}'.");
        }
    }

    private static void ValidateRoles(CustomRunDefinition definition, CustomRunValidationResult result)
    {
        ValidateUniqueIds(definition.Roles.Select(role => role.Id), "Roles", result);
        foreach (RoleDefinition role in definition.Roles)
        {
            if (!IsGuidStyleId(role.Id))
                Error(result, "Roles", role.Id, $"Role '{role.Name}' has an invalid ID.");
            if (role.MaximumPlayers < role.MinimumPlayers)
                Error(result, "Roles", role.Id, $"Role '{role.Name}' maximum is below its minimum.");
            ValidateSetup(role.Setup, result, "Roles", role.Id);
        }
    }

    private static void ValidateChoices(CustomRunDefinition definition, CustomRunValidationResult result)
    {
        ValidateUniqueIds(definition.PlayerChoices.Select(choice => choice.Id), "Player Choices", result);
        HashSet<string> roleIds = definition.Roles.Select(role => role.Id).ToHashSet(StringComparer.Ordinal);
        foreach (PlayerChoiceDefinition choice in definition.PlayerChoices)
        {
            if (!IsGuidStyleId(choice.Id))
                Error(result, "Player Choices", choice.Id, $"Choice '{choice.Name}' has an invalid ID.");
            if (choice.MaximumSelections < choice.MinimumSelections)
                Error(result, "Player Choices", choice.Id, $"Choice '{choice.Name}' maximum is below its minimum.");
            if (choice.Audience == PlayerChoiceAudience.Role
                && (string.IsNullOrWhiteSpace(choice.RoleId) || !roleIds.Contains(choice.RoleId)))
            {
                Error(result, "Player Choices", choice.Id, $"Choice '{choice.Name}' references a missing role.");
            }
        }
    }

    private static void ValidateVariables(CustomRunDefinition definition, CustomRunValidationResult result)
    {
        ValidateUniqueIds(definition.Variables.Select(variable => variable.Id), "Variables", result);
        foreach (VariableDefinition variable in definition.Variables.Where(variable => !IsGuidStyleId(variable.Id)))
            Error(result, "Variables", variable.Id, $"Variable '{variable.Name}' has an invalid ID.");
    }

    private static void ValidateRules(CustomRunDefinition definition, CustomRunValidationResult result)
    {
        ValidateUniqueIds(definition.Rules.Select(rule => rule.Id), "Rules", result);
        foreach (RuleDefinition rule in definition.Rules)
        {
            if (!IsGuidStyleId(rule.Id))
                Error(result, "Rules", rule.Id, $"Rule '{rule.Name}' has an invalid ID.");
            if (string.IsNullOrWhiteSpace(rule.Trigger.TypeId))
                Error(result, "Rules", rule.Id, $"Rule '{rule.Name}' has no trigger.");
            else if (!CustomRunRegistry.TryGetTrigger(rule.Trigger.TypeId, out _))
                Error(result, "Rules", rule.Id, $"Rule '{rule.Name}' uses unknown trigger '{rule.Trigger.TypeId}'.");

            ValidateConditions(rule.Conditions, rule, result);
            if (rule.Actions.Count == 0)
                Error(result, "Rules", rule.Id, $"Rule '{rule.Name}' has no actions.");
            foreach (RuleComponentSpec action in rule.Actions)
            {
                if (!CustomRunRegistry.TryGetAction(action.TypeId, out _))
                    Error(result, "Rules", rule.Id, $"Rule '{rule.Name}' uses unknown action '{action.TypeId}'.");
            }
        }
    }

    private static void ValidateConditions(
        ConditionGroupDefinition group,
        RuleDefinition rule,
        CustomRunValidationResult result)
    {
        foreach (RuleComponentSpec condition in group.Conditions)
        {
            if (!CustomRunRegistry.TryGetCondition(condition.TypeId, out _))
                Error(result, "Rules", rule.Id, $"Rule '{rule.Name}' uses unknown condition '{condition.TypeId}'.");
        }
        foreach (ConditionGroupDefinition child in group.Groups)
            ValidateConditions(child, rule, result);
    }

    private static void ValidateUniqueIds(IEnumerable<string> ids, string section, CustomRunValidationResult result)
    {
        foreach (IGrouping<string, string> duplicate in ids.GroupBy(id => id, StringComparer.Ordinal).Where(group => group.Count() > 1))
            Error(result, section, duplicate.Key, $"Duplicate ID '{duplicate.Key}'.");
    }

    private static void Range(
        int? value,
        int minimum,
        int maximum,
        string label,
        CustomRunValidationResult result,
        string section,
        string objectId)
    {
        if (value.HasValue && (value < minimum || value > maximum))
            Error(result, section, objectId, $"{label} must be between {minimum} and {maximum}.");
    }

    private static bool IsGuidStyleId(string id)
    {
        return Guid.TryParseExact(id, "N", out _) || Guid.TryParse(id, out _);
    }

    private static void Error(CustomRunValidationResult result, string section, string objectId, string message)
    {
        result.Issues.Add(new CustomRunValidationIssue(CustomRunValidationSeverity.Error, section, objectId, message));
    }
}
