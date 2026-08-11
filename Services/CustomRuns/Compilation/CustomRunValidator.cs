#nullable enable

namespace Loadout.Services.CustomRuns.Compilation;

using System;
using System.Collections.Generic;
using System.Linq;
using Loadout.Services.CustomRuns.Catalog;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.CustomRuns.Persistence;
using Loadout.Services.CustomRuns.Registry;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

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

        foreach (StartingPowerDefinition power in setup.StartingPowers)
        {
            if (!CustomRunCatalogService.TryResolve(SelectionModelKind.Power, power.ModelId, out _))
                Error(result, section, objectId, $"Unknown power '{power.ModelId}'.");
        }
        if (setup.StartingMorphModelId is not null
            && !CustomRunCatalogService.TryResolveMorph(setup.StartingMorphModelId, out _))
        {
            Error(result, section, objectId, $"Unknown morph '{setup.StartingMorphModelId}'.");
        }

        Range(setup.PotionSlots, 0, 20, "Potion slots", result, section, objectId);
        Range(setup.StartingGold, 0, 999999, "Starting gold", result, section, objectId);
        Range(setup.StartingCurrentHp, 1, 99999, "Starting current HP", result, section, objectId);
        Range(setup.StartingMaxHp, 1, 99999, "Starting max HP", result, section, objectId);
        Range(setup.BaseEnergyPerTurn, 0, 99, "Base energy", result, section, objectId);
        Range(setup.CardsDrawnPerTurn, 0, 99, "Cards drawn", result, section, objectId);
        Range(setup.StartingAscension, 0, 10, "Starting ascension", result, section, objectId);
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
            else if (!CustomRunRegistry.TryGetTrigger(rule.Trigger.TypeId, out RuleComponentDescriptor triggerDescriptor))
                Error(result, "Rules", rule.Id, $"Rule '{rule.Name}' uses unknown trigger '{rule.Trigger.TypeId}'.");
            else
                ValidateComponentParameters(definition, rule, rule.Trigger, triggerDescriptor, result);

            ValidateConditions(definition, rule.Conditions, rule, result);
            if (rule.Actions.Count == 0)
                Error(result, "Rules", rule.Id, $"Rule '{rule.Name}' has no actions.");
            foreach (RuleComponentSpec action in rule.Actions)
            {
                if (!CustomRunRegistry.TryGetAction(action.TypeId, out RuleComponentDescriptor actionDescriptor))
                    Error(result, "Rules", rule.Id, $"Rule '{rule.Name}' uses unknown action '{action.TypeId}'.");
                else
                    ValidateComponentParameters(definition, rule, action, actionDescriptor, result);
            }
            if (rule.Limit.Kind == RuleLimitKind.UntilCondition)
                ValidateConditions(definition, rule.Limit.UntilConditions, rule, result);
        }
    }

    private static void ValidateConditions(
        CustomRunDefinition definition,
        ConditionGroupDefinition group,
        RuleDefinition rule,
        CustomRunValidationResult result)
    {
        foreach (RuleComponentSpec condition in group.Conditions)
        {
            if (!CustomRunRegistry.TryGetCondition(condition.TypeId, out RuleComponentDescriptor descriptor))
                Error(result, "Rules", rule.Id, $"Rule '{rule.Name}' uses unknown condition '{condition.TypeId}'.");
            else if (!CustomRunRegistry.IsCompatibleWithTrigger(descriptor, rule.Trigger.TypeId))
                Error(result, "Rules", rule.Id, $"Rule '{rule.Name}' cannot use '{descriptor.DisplayName}' with its selected trigger.");
            else
                ValidateComponentParameters(definition, rule, condition, descriptor, result);
        }
        foreach (ConditionGroupDefinition child in group.Groups)
            ValidateConditions(definition, child, rule, result);
    }

    private static void ValidateComponentParameters(
        CustomRunDefinition definition,
        RuleDefinition rule,
        RuleComponentSpec component,
        RuleComponentDescriptor descriptor,
        CustomRunValidationResult result)
    {
        foreach (RuleParameterDescriptor parameter in descriptor.Parameters)
        {
            if (!component.Parameters.TryGetValue(parameter.Key, out System.Text.Json.JsonElement element)
                || element.ValueKind is System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined)
            {
                if (parameter.Required)
                    RuleParameterError(result, rule, descriptor, parameter, "is required");
                continue;
            }

            switch (parameter.Kind)
            {
                case RuleParameterKind.Integer:
                    if (!RuleComponentParameterService.TryGet(component, parameter.Key, out int integer))
                        RuleParameterError(result, rule, descriptor, parameter, "must be an integer");
                    else if (integer < parameter.Minimum || integer > parameter.Maximum)
                        RuleParameterError(result, rule, descriptor, parameter, $"must be between {parameter.Minimum} and {parameter.Maximum}");
                    break;
                case RuleParameterKind.Boolean:
                    if (!RuleComponentParameterService.TryGet(component, parameter.Key, out bool _))
                        RuleParameterError(result, rule, descriptor, parameter, "must be true or false");
                    break;
                case RuleParameterKind.Enum:
                    ValidateEnumParameter(component, rule, descriptor, parameter, result);
                    break;
                case RuleParameterKind.Text:
                    ValidateRequiredString(component, rule, descriptor, parameter, result);
                    break;
                case RuleParameterKind.Card:
                    ValidateModelParameter(definition, component, rule, descriptor, parameter, SelectionModelKind.Card, result);
                    break;
                case RuleParameterKind.Relic:
                    ValidateModelParameter(definition, component, rule, descriptor, parameter, SelectionModelKind.Relic, result);
                    break;
                case RuleParameterKind.Potion:
                    ValidateModelParameter(definition, component, rule, descriptor, parameter, SelectionModelKind.Potion, result);
                    break;
                case RuleParameterKind.Power:
                    ValidateModelParameter(definition, component, rule, descriptor, parameter, SelectionModelKind.Power, result);
                    break;
                case RuleParameterKind.Monster:
                    ValidateModelParameter(definition, component, rule, descriptor, parameter, SelectionModelKind.Monster, result);
                    break;
                case RuleParameterKind.Role:
                    ValidateReferenceParameter(
                        component,
                        rule,
                        descriptor,
                        parameter,
                        definition.Roles.Select(role => role.Id),
                        "role",
                        result);
                    break;
                case RuleParameterKind.Variable:
                    ValidateReferenceParameter(
                        component,
                        rule,
                        descriptor,
                        parameter,
                        definition.Variables.Select(variable => variable.Id),
                        "variable",
                        result);
                    break;
                case RuleParameterKind.PlayerTarget:
                    ValidateTargetParameter(definition, component, rule, descriptor, parameter, result);
                    break;
                case RuleParameterKind.NumericSource:
                    ValidateNumericSourceParameter(definition, component, rule, descriptor, parameter, result);
                    break;
                case RuleParameterKind.ModelFilter:
                    ValidateModelFilterParameter(component, rule, descriptor, parameter, result);
                    break;
            }
        }

        if (descriptor.Validate is null)
            return;
        try
        {
            foreach (string message in descriptor.Validate(component))
                Error(result, "Rules", rule.Id, $"Rule '{rule.Name}', {descriptor.DisplayName}: {message}");
        }
        catch (Exception exception)
        {
            Error(result, "Rules", rule.Id, $"Rule '{rule.Name}', {descriptor.DisplayName}: validation failed ({exception.Message}).");
        }
    }

    private static void ValidateEnumParameter(
        RuleComponentSpec component,
        RuleDefinition rule,
        RuleComponentDescriptor descriptor,
        RuleParameterDescriptor parameter,
        CustomRunValidationResult result)
    {
        string value = RuleComponentParameterService.GetString(component, parameter.Key);
        if (parameter.Required && string.IsNullOrWhiteSpace(value))
            RuleParameterError(result, rule, descriptor, parameter, "is required");
        else if (parameter.Options.Count > 0
                 && !parameter.Options.Any(option => string.Equals(option.Id, value, StringComparison.Ordinal)))
            RuleParameterError(result, rule, descriptor, parameter, $"has unknown value '{value}'");
    }

    private static void ValidateRequiredString(
        RuleComponentSpec component,
        RuleDefinition rule,
        RuleComponentDescriptor descriptor,
        RuleParameterDescriptor parameter,
        CustomRunValidationResult result)
    {
        if (parameter.Required && string.IsNullOrWhiteSpace(RuleComponentParameterService.GetString(component, parameter.Key)))
            RuleParameterError(result, rule, descriptor, parameter, "is required");
    }

    private static void ValidateModelParameter(
        CustomRunDefinition definition,
        RuleComponentSpec component,
        RuleDefinition rule,
        RuleComponentDescriptor descriptor,
        RuleParameterDescriptor parameter,
        SelectionModelKind kind,
        CustomRunValidationResult result)
    {
        _ = definition;
        string id = RuleComponentParameterService.GetString(component, parameter.Key);
        if (string.IsNullOrWhiteSpace(id))
        {
            if (parameter.Required)
                RuleParameterError(result, rule, descriptor, parameter, "is required");
            return;
        }
        if (!CustomRunCatalogService.TryResolve(kind, id, out _))
            RuleParameterError(result, rule, descriptor, parameter, $"references unknown {kind.ToString().ToLowerInvariant()} '{id}'");
    }

    private static void ValidateReferenceParameter(
        RuleComponentSpec component,
        RuleDefinition rule,
        RuleComponentDescriptor descriptor,
        RuleParameterDescriptor parameter,
        IEnumerable<string> validIds,
        string referenceKind,
        CustomRunValidationResult result)
    {
        string id = RuleComponentParameterService.GetString(component, parameter.Key);
        if (string.IsNullOrWhiteSpace(id))
        {
            if (parameter.Required)
                RuleParameterError(result, rule, descriptor, parameter, "is required");
            return;
        }
        if (!validIds.Contains(id, StringComparer.Ordinal))
            RuleParameterError(result, rule, descriptor, parameter, $"references a missing {referenceKind}");
    }

    private static void ValidateTargetParameter(
        CustomRunDefinition definition,
        RuleComponentSpec component,
        RuleDefinition rule,
        RuleComponentDescriptor descriptor,
        RuleParameterDescriptor parameter,
        CustomRunValidationResult result)
    {
        if (!RuleComponentParameterService.TryGet(component, parameter.Key, out RuleTargetSpec target)
            || string.IsNullOrWhiteSpace(target.TypeId))
        {
            RuleParameterError(result, rule, descriptor, parameter, "is required");
            return;
        }
        target.Parameters ??= new System.Collections.Generic.SortedDictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);
        if (!CustomRunRegistry.TryGetTarget(target.TypeId, out RuleComponentDescriptor targetDescriptor))
        {
            RuleParameterError(result, rule, descriptor, parameter, $"uses unknown target '{target.TypeId}'");
            return;
        }
        ValidateComponentParameters(
            definition,
            rule,
            new RuleComponentSpec { TypeId = target.TypeId, Parameters = target.Parameters },
            targetDescriptor,
            result);
    }

    private static void ValidateModelFilterParameter(
        RuleComponentSpec component,
        RuleDefinition rule,
        RuleComponentDescriptor descriptor,
        RuleParameterDescriptor parameter,
        CustomRunValidationResult result)
    {
        if (!RuleComponentParameterService.TryGet(component, parameter.Key, out ModelMatchSpec filter))
        {
            RuleParameterError(result, rule, descriptor, parameter, "is invalid");
            return;
        }

        filter.ModelIds ??= [];
        filter.ModelKind = parameter.ModelKind;
        string value = (filter.Value ?? string.Empty).Trim();
        bool valid = filter.Kind switch
        {
            ModelMatchKind.SpecificModels => filter.ModelIds.Count > 0
                                             && filter.ModelIds.All(id => CustomRunCatalogService.TryResolve(filter.ModelKind, id, out _)),
            ModelMatchKind.TextContains => !string.IsNullOrWhiteSpace(value),
            _ => !string.IsNullOrWhiteSpace(value) && RuleModelMatcher.Resolve(filter).Count > 0
        };
        if (!valid)
            RuleParameterError(result, rule, descriptor, parameter, $"has an invalid {FormatModelMatchKind(filter.Kind)} selection");
    }

    private static string FormatModelMatchKind(ModelMatchKind kind)
    {
        return kind switch
        {
            ModelMatchKind.SpecificModels => "specific-model",
            ModelMatchKind.EnergyCost => "energy-cost",
            ModelMatchKind.TextContains => "text",
            _ => kind.ToString().ToLowerInvariant()
        };
    }

    private static void ValidateNumericSourceParameter(
        CustomRunDefinition definition,
        RuleComponentSpec component,
        RuleDefinition rule,
        RuleComponentDescriptor descriptor,
        RuleParameterDescriptor parameter,
        CustomRunValidationResult result)
    {
        if (!RuleComponentParameterService.TryGet(component, parameter.Key, out NumericValueSpec value)
            || !Enum.IsDefined(value.Source)
            || !Enum.IsDefined(value.ConstantKind)
            || (!parameter.AllowDouble && value.ConstantKind == NumericConstantKind.Double))
        {
            RuleParameterError(result, rule, descriptor, parameter, "has an invalid numeric source");
            return;
        }

        if (value.Source == NumericValueSourceKind.Variable
            && (string.IsNullOrWhiteSpace(value.ReferenceId)
                || !definition.Variables.Any(variable => string.Equals(variable.Id, value.ReferenceId, StringComparison.Ordinal))))
        {
            RuleParameterError(result, rule, descriptor, parameter, "references a missing variable");
        }
        if (value.Source == NumericValueSourceKind.EventContext
            && value.ReferenceId is not ("CurrentHp" or "MaxHp" or "Gold" or "Energy" or "TurnNumber" or "PlayerCount"))
        {
            RuleParameterError(result, rule, descriptor, parameter, "has an unknown event value");
        }
    }

    private static void RuleParameterError(
        CustomRunValidationResult result,
        RuleDefinition rule,
        RuleComponentDescriptor descriptor,
        RuleParameterDescriptor parameter,
        string problem)
    {
        Error(
            result,
            "Rules",
            rule.Id,
            $"Rule '{rule.Name}', {descriptor.DisplayName}: {parameter.DisplayName} {problem}.");
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
