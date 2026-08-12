#nullable enable

namespace Loadout.Services.CustomRuns.Persistence;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.CustomRuns.Compilation;
using Loadout.Services.CustomRuns.Registry;
using Loadout.Services.Loadouts;

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
        definition.DefaultRoleName = string.IsNullOrWhiteSpace(definition.DefaultRoleName)
            ? "Default Role"
            : definition.DefaultRoleName.Trim();
        definition.CreatedAtUnixSeconds = definition.CreatedAtUnixSeconds > 0
            ? definition.CreatedAtUnixSeconds
            : now;
        definition.UpdatedAtUnixSeconds = definition.UpdatedAtUnixSeconds > 0
            ? definition.UpdatedAtUnixSeconds
            : definition.CreatedAtUnixSeconds;
        definition.Setup ??= new RunSetupDefinition();
        definition.Setup = NormalizeSetup(definition.Setup);
        if (!Enum.IsDefined(definition.RoleAssignmentMode))
            definition.RoleAssignmentMode = RoleAssignmentMode.PlayersChoose;
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
        if (!Enum.IsDefined(setup.StartingLoadoutMode))
            setup.StartingLoadoutMode = StartingLoadoutMode.PerCharacter;
        NormalizeStartingLoadout(setup);
        setup.CharacterStartingLoadouts = (setup.CharacterStartingLoadouts ?? [])
            .Where(loadout => loadout is not null && !string.IsNullOrWhiteSpace(loadout.CharacterModelId))
            .Select(loadout =>
            {
                loadout.CharacterModelId = loadout.CharacterModelId.Trim();
                NormalizeStartingLoadout(loadout);
                return loadout;
            })
            .GroupBy(loadout => loadout.CharacterModelId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(loadout => loadout.CharacterModelId, StringComparer.Ordinal)
            .ToList();
        setup.StartingAscension = setup.StartingAscension.HasValue
            ? Math.Clamp(setup.StartingAscension.Value, 0, 10)
            : null;
        setup.Modifiers = (setup.Modifiers ?? [])
            .Where(modifier => modifier is not null && !string.IsNullOrWhiteSpace(modifier.ModelId))
            .Select(modifier => new RunModifierDefinition
            {
                ModelId = modifier.ModelId.Trim(),
                CharacterModelId = string.IsNullOrWhiteSpace(modifier.CharacterModelId)
                    ? null
                    : modifier.CharacterModelId.Trim()
            })
            .DistinctBy(
                modifier => $"{modifier.ModelId}\n{modifier.CharacterModelId}",
                StringComparer.OrdinalIgnoreCase)
            .ToList();
        setup.RunSeed = string.IsNullOrWhiteSpace(setup.RunSeed) ? null : setup.RunSeed.Trim();
        return setup;
    }

    public static T NormalizeStartingLoadout<T>(T loadout)
        where T : IStartingLoadoutDefinition
    {
        loadout.StartingDeck = NormalizeSelection(loadout.StartingDeck, SelectionModelKind.Card);
        loadout.StartingRelics = NormalizeSelection(loadout.StartingRelics, SelectionModelKind.Relic);
        loadout.StartingPotions = NormalizeSelection(loadout.StartingPotions, SelectionModelKind.Potion);
        loadout.StartingCardEntries = NormalizeCardEntries(loadout.StartingCardEntries, loadout.StartingDeck);
        loadout.StartingRelicEntries = NormalizeRelicEntries(loadout.StartingRelicEntries, loadout.StartingRelics);
        loadout.StartingPowers = (loadout.StartingPowers ?? [])
            .Where(power => power is not null && !string.IsNullOrWhiteSpace(power.ModelId) && power.Amount != 0)
            .Select(power => new StartingPowerDefinition
            {
                ModelId = power.ModelId.Trim(),
                Amount = power.Amount
            })
            .GroupBy(power => power.ModelId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new StartingPowerDefinition
            {
                ModelId = group.First().ModelId,
                Amount = group.Sum(power => power.Amount)
            })
            .Where(power => power.Amount != 0)
            .OrderBy(power => power.ModelId, StringComparer.Ordinal)
            .ToList();
        loadout.StartingMorphModelId = string.IsNullOrWhiteSpace(loadout.StartingMorphModelId)
            ? null
            : loadout.StartingMorphModelId.Trim();
        return loadout;
    }

    private static List<SavedCardLoadoutEntry> NormalizeCardEntries(
        List<SavedCardLoadoutEntry>? entries,
        SelectionSpec selection)
    {
        if (selection.Mode != SelectionMode.Fixed)
            return [];

        List<SavedCardLoadoutEntry> normalized = (entries ?? [])
            .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.ModelId))
            .SelectMany(entry => Enumerable.Range(0, Math.Max(1, entry.Count)).Select(_ =>
            {
                SavedCardLoadoutEntry clone = entry.Clone();
                clone.ModelId = clone.ModelId.Trim();
                clone.Count = 1;
                clone.UpgradeLevel = Math.Max(0, clone.UpgradeLevel);
                clone.ModificationState?.Normalize();
                if (clone.ModificationState?.IsEmpty == true)
                    clone.ModificationState = null;
                return clone;
            }))
            .ToList();

        if (normalized.Count == 0)
        {
            normalized.AddRange(selection.FixedModelIds.Select(id => new SavedCardLoadoutEntry
            {
                ModelId = id,
                Count = 1
            }));
        }

        selection.FixedModelIds = normalized.Select(entry => entry.ModelId).ToList();
        return normalized;
    }

    private static List<SavedRelicLoadoutEntry> NormalizeRelicEntries(
        List<SavedRelicLoadoutEntry>? entries,
        SelectionSpec selection)
    {
        if (selection.Mode != SelectionMode.Fixed)
            return [];

        List<SavedRelicLoadoutEntry> normalized = (entries ?? [])
            .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.ModelId))
            .SelectMany(entry => Enumerable.Range(0, Math.Max(1, entry.Count)).Select(_ =>
            {
                SavedRelicLoadoutEntry clone = entry.Clone();
                clone.ModelId = clone.ModelId.Trim();
                clone.Count = 1;
                clone.ModificationState?.Normalize();
                if (clone.ModificationState?.IsEmpty == true)
                    clone.ModificationState = null;
                return clone;
            }))
            .ToList();

        if (normalized.Count == 0)
        {
            normalized.AddRange(selection.FixedModelIds.Select(id => new SavedRelicLoadoutEntry
            {
                ModelId = id,
                Count = 1
            }));
        }

        selection.FixedModelIds = normalized.Select(entry => entry.ModelId).ToList();
        return normalized;
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
        role.MaximumPlayers = Math.Clamp(role.MaximumPlayers, 0, 4);
        role.MinimumPlayers = Math.Clamp(
            role.MinimumPlayers,
            0,
            role.MaximumPlayers == 0 ? 4 : role.MaximumPlayers);
        role.LegacyAssignmentMode = null;
        role.Setup ??= new RunSetupDefinition();
        role.Setup = NormalizeSetup(role.Setup);
        role.Setup.RunSeed = null;
        role.Setup.StartingAscension = null;
        role.Setup.ModifiersEnabled = false;
        role.Setup.Modifiers.Clear();
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
        NormalizeKnownParameters(rule.Trigger, RuleComponentKind.Trigger);
        NormalizeKnownConditionParameters(rule.Conditions);
        foreach (RuleComponentSpec action in rule.Actions)
            NormalizeKnownParameters(action, RuleComponentKind.Action);
        rule.Limit ??= new RuleLimitDefinition();
        rule.Limit.Count ??= NumericValueSpec.Integer(1);
        rule.Limit.Count.ReferenceId = string.IsNullOrWhiteSpace(rule.Limit.Count.ReferenceId)
            ? null
            : rule.Limit.Count.ReferenceId.Trim();
        if (rule.Limit.Count.Source == NumericValueSourceKind.Constant)
        {
            rule.Limit.Count.ConstantKind = NumericConstantKind.Integer;
            rule.Limit.Count.Constant = Math.Max(1d, Math.Truncate(Math.Clamp(
                rule.Limit.Count.Constant,
                int.MinValue,
                int.MaxValue)));
        }
        if (!Enum.IsDefined(rule.Limit.Kind))
            rule.Limit.Kind = RuleLimitKind.Unlimited;
        rule.Limit.Kind = rule.Limit.Kind switch
        {
            RuleLimitKind.OncePerTurn => RuleLimitKind.TimesPerTurn,
            RuleLimitKind.OncePerCombat => RuleLimitKind.TimesPerCombat,
            RuleLimitKind.OncePerRun => RuleLimitKind.TimesPerRun,
            _ => rule.Limit.Kind
        };
        rule.Limit.UntilConditions = NormalizeConditionGroup(
            rule.Limit.UntilConditions ?? new ConditionGroupDefinition());
        NormalizeKnownConditionParameters(rule.Limit.UntilConditions);
        rule.ContentHash = RuleBehaviorHashService.Compute(rule);
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

    private static void NormalizeKnownConditionParameters(ConditionGroupDefinition group)
    {
        foreach (RuleComponentSpec condition in group.Conditions)
            NormalizeKnownParameters(condition, RuleComponentKind.Condition);
        foreach (ConditionGroupDefinition child in group.Groups)
            NormalizeKnownConditionParameters(child);
    }

    private static void NormalizeKnownParameters(RuleComponentSpec component, RuleComponentKind kind)
    {
        RuleComponentDescriptor? descriptor = CustomRunRegistry.GetDescriptors(kind)
            .FirstOrDefault(candidate => string.Equals(candidate.StableId, component.TypeId, StringComparison.Ordinal));
        if (descriptor is null)
            return;
        RuleComponentParameterService.ApplyDefaults(component, descriptor);
        foreach (RuleParameterDescriptor parameter in descriptor.Parameters)
        {
            if (parameter.Kind == RuleParameterKind.ModelFilter
                && RuleComponentParameterService.TryGet(component, parameter.Key, out ModelMatchSpec matcher))
            {
                matcher.ModelKind = parameter.ModelKind;
                matcher.Value = (matcher.Value ?? string.Empty).Trim();
                matcher.ModelIds = NormalizeStrings(matcher.ModelIds);
                if (matcher.Kind == ModelMatchKind.SpecificModels)
                    matcher.Value = string.Empty;
                else
                    matcher.ModelIds.Clear();
                RuleComponentParameterService.Set(component, parameter.Key, matcher);
            }
            else if (parameter.Kind == RuleParameterKind.NumericSource
                     && RuleComponentParameterService.TryGet(component, parameter.Key, out NumericValueSpec numeric))
            {
                if (!parameter.AllowDouble)
                {
                    numeric.ConstantKind = NumericConstantKind.Integer;
                    numeric.Constant = Math.Truncate(Math.Clamp(numeric.Constant, parameter.Minimum, parameter.Maximum));
                }
                RuleComponentParameterService.Set(component, parameter.Key, numeric);
            }
        }
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
