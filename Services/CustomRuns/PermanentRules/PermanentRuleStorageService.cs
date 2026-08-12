#nullable enable

namespace Loadout.Services.CustomRuns.PermanentRules;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using System.Text.Json;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.CustomRuns.Compilation;
using Loadout.Services.CustomRuns.Persistence;
using Loadout.Services.CustomRuns.Registry;
using Loadout.Services.Saving;
using MegaCrit.Sts2.Core.Saves;

public static class PermanentRuleStorageService
{
    public const int CurrentSchemaVersion = 2;

    private const string ProfilePath = "loadout/services/custom_runs/profile_permanent_rules.json";
    private static readonly object SyncRoot = new();
    private static PermanentRuleProfileSaveData _profile = new();
    private static bool _loaded;
    private static bool _registered;

    public static event Action? Changed;

    public static void Register()
    {
        if (_registered)
            return;
        _registered = true;
        SaveManager.Instance.ProfileIdChanged += OnProfileIdChanged;
        EnsureLoaded();
    }

    public static IReadOnlyList<RuleDefinition> GetRules()
    {
        EnsureLoaded();
        lock (SyncRoot)
            return _profile.Bundles.Select(bundle => CustomRunNormalizationService.CloneRule(bundle.Rule)).ToList();
    }

    public static IReadOnlyList<PermanentRuleBundle> GetBundles()
    {
        EnsureLoaded();
        lock (SyncRoot)
            return _profile.Bundles.Select(CloneBundle).ToList();
    }

    public static RuleDefinition Upsert(RuleDefinition rule)
    {
        PermanentRuleBundle? existing = GetBundles().FirstOrDefault(bundle =>
            string.Equals(bundle.Rule.Id, rule.Id, StringComparison.Ordinal));
        return Upsert(rule, existing?.Variables ?? []);
    }

    public static RuleDefinition Upsert(
        RuleDefinition rule,
        IEnumerable<VariableDefinition> availableVariables)
    {
        EnsureLoaded();
        RuleDefinition normalized = CustomRunNormalizationService.NormalizeRule(
            CustomRunNormalizationService.CloneRule(rule));
        lock (SyncRoot)
        {
            int index = _profile.Bundles.FindIndex(existing =>
                string.Equals(existing.Rule.Id, normalized.Id, StringComparison.Ordinal));
            List<VariableDefinition> variables = SelectReferencedVariables(normalized, availableVariables);
            PermanentRuleBundle bundle = new() { Rule = normalized, Variables = variables };
            if (index >= 0)
            {
                normalized.Id = _profile.Bundles[index].Rule.Id;
                _profile.Bundles[index] = bundle;
            }
            else
                _profile.Bundles.Add(bundle);
            SaveLocked();
        }
        Changed?.Invoke();
        return CustomRunNormalizationService.CloneRule(normalized);
    }

    public static bool SetEnabled(string id, bool enabled)
    {
        EnsureLoaded();
        bool changed = false;
        lock (SyncRoot)
        {
            RuleDefinition? rule = _profile.Bundles.Select(bundle => bundle.Rule).FirstOrDefault(candidate =>
                string.Equals(candidate.Id, id, StringComparison.Ordinal));
            if (rule is not null && rule.Enabled != enabled)
            {
                rule.Enabled = enabled;
                changed = true;
                SaveLocked();
            }
        }
        if (changed)
            Changed?.Invoke();
        return changed;
    }

    public static bool Delete(string id)
    {
        EnsureLoaded();
        bool changed;
        lock (SyncRoot)
        {
            changed = _profile.Bundles.RemoveAll(bundle =>
                string.Equals(bundle.Rule.Id, id, StringComparison.Ordinal)) > 0;
            if (changed)
                SaveLocked();
        }
        if (changed)
            Changed?.Invoke();
        return changed;
    }

    public static bool Move(string sourceId, string? targetId, bool placeAfter)
    {
        EnsureLoaded();
        bool changed = false;
        lock (SyncRoot)
        {
            int sourceIndex = _profile.Bundles.FindIndex(bundle =>
                string.Equals(bundle.Rule.Id, sourceId, StringComparison.Ordinal));
            if (sourceIndex < 0)
                return false;

            PermanentRuleBundle source = _profile.Bundles[sourceIndex];
            _profile.Bundles.RemoveAt(sourceIndex);
            int targetIndex = string.IsNullOrEmpty(targetId)
                ? _profile.Bundles.Count
                : _profile.Bundles.FindIndex(bundle => string.Equals(bundle.Rule.Id, targetId, StringComparison.Ordinal));
            if (targetIndex < 0)
                targetIndex = _profile.Bundles.Count;
            else if (placeAfter)
                targetIndex++;
            targetIndex = Math.Clamp(targetIndex, 0, _profile.Bundles.Count);
            _profile.Bundles.Insert(targetIndex, source);
            changed = targetIndex != sourceIndex;
            if (changed)
                SaveLocked();
        }
        if (changed)
            Changed?.Invoke();
        return changed;
    }

    public static PermanentRuleBundle? Duplicate(string id)
    {
        PermanentRuleBundle? source = GetBundles().FirstOrDefault(bundle =>
            string.Equals(bundle.Rule.Id, id, StringComparison.Ordinal));
        if (source is null)
            return null;

        PermanentRuleBundle copy = CreateDuplicateBundle(source);
        RuleDefinition stored = Upsert(copy.Rule, copy.Variables);
        return GetBundles().FirstOrDefault(bundle => string.Equals(bundle.Rule.Id, stored.Id, StringComparison.Ordinal));
    }

    public static PermanentRuleBundle CreateDuplicateBundle(PermanentRuleBundle source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Dictionary<string, string> variableIds = source.Variables
            .Select(variable => variable.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(
                id => id,
                _ => Guid.NewGuid().ToString("N"),
                StringComparer.Ordinal);
        PermanentRuleBundle copy = CloneBundle(source);
        copy.Rule.Id = Guid.NewGuid().ToString("N");
        copy.Rule.Name = $"{source.Rule.Name} Copy";
        foreach (VariableDefinition variable in copy.Variables)
        {
            if (variableIds.TryGetValue(variable.Id, out string? replacement))
                variable.Id = replacement;
        }
        RemapVariableIds(copy.Rule, variableIds);
        return copy;
    }

    private static void EnsureLoaded()
    {
        lock (SyncRoot)
        {
            if (_loaded)
                return;
            SaveUtility.LoadResult<PermanentRuleProfileSaveData> loaded =
                SaveUtility.LoadProfileJson(ProfilePath, new PermanentRuleProfileSaveData());
            _profile = NormalizeProfile(loaded.Value);
            _loaded = true;
            if (loaded.Loaded && loaded.Value.SchemaVersion != CurrentSchemaVersion)
                SaveLocked();
        }
    }

    private static PermanentRuleProfileSaveData NormalizeProfile(PermanentRuleProfileSaveData profile)
    {
        if ((profile.Bundles is null || profile.Bundles.Count == 0) && profile.Rules is { Count: > 0 })
        {
            profile.Bundles = profile.Rules.Select(rule => new PermanentRuleBundle
            {
                Rule = rule,
                Variables = []
            }).ToList();
        }
        profile.SchemaVersion = CurrentSchemaVersion;
        profile.Bundles = (profile.Bundles ?? [])
            .Where(bundle => bundle?.Rule is not null)
            .Select(bundle => NormalizeBundle(bundle!))
            .GroupBy(bundle => bundle.Rule.Id, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToList();
        profile.Rules = [];
        return profile;
    }

    private static PermanentRuleBundle NormalizeBundle(PermanentRuleBundle bundle)
    {
        CustomRunDefinition context = CustomRunNormalizationService.Normalize(new CustomRunDefinition
        {
            Rules = [bundle.Rule],
            Variables = bundle.Variables ?? []
        });
        return new PermanentRuleBundle
        {
            Rule = context.Rules[0],
            Variables = SelectReferencedVariables(context.Rules[0], context.Variables)
        };
    }

    private static PermanentRuleBundle CloneBundle(PermanentRuleBundle bundle)
    {
        string json = JsonSerializer.Serialize(bundle, CustomRunSerializationService.SharedJsonOptions);
        return JsonSerializer.Deserialize<PermanentRuleBundle>(json, CustomRunSerializationService.SharedJsonOptions)
               ?? new PermanentRuleBundle();
    }

    private static List<VariableDefinition> SelectReferencedVariables(
        RuleDefinition rule,
        IEnumerable<VariableDefinition> availableVariables)
    {
        HashSet<string> ids = [];
        CollectVariableIds(rule.Trigger, ids);
        CollectVariableIds(rule.Conditions, ids);
        CollectVariableIds(rule.Limit.UntilConditions, ids);
        foreach (RuleComponentSpec action in rule.Actions)
            CollectVariableIds(action, ids);
        return availableVariables
            .Where(variable => ids.Contains(variable.Id))
            .Select(variable => CustomRunNormalizationService.Normalize(new CustomRunDefinition
            {
                Variables = [variable]
            }).Variables[0])
            .ToList();
    }

    private static void CollectVariableIds(ConditionGroupDefinition group, ISet<string> ids)
    {
        foreach (RuleComponentSpec condition in group.Conditions)
            CollectVariableIds(condition, ids);
        foreach (ConditionGroupDefinition child in group.Groups)
            CollectVariableIds(child, ids);
    }

    private static void CollectVariableIds(RuleComponentSpec component, ISet<string> ids)
    {
        string direct = RuleComponentParameterService.GetString(component, "variableId");
        if (!string.IsNullOrWhiteSpace(direct))
            ids.Add(direct);
        foreach (JsonElement element in component.Parameters.Values)
        {
            try
            {
                NumericValueSpec? numeric = element.Deserialize<NumericValueSpec>(CustomRunSerializationService.SharedJsonOptions);
                if (numeric?.Source == NumericValueSourceKind.Variable && !string.IsNullOrWhiteSpace(numeric.ReferenceId))
                    ids.Add(numeric.ReferenceId);
            }
            catch (JsonException)
            {
            }
        }
    }

    private static void RemapVariableIds(RuleDefinition rule, IReadOnlyDictionary<string, string> ids)
    {
        RemapVariableIds(rule.Trigger, ids);
        RemapVariableIds(rule.Conditions, ids);
        RemapVariableIds(rule.Limit.UntilConditions, ids);
        foreach (RuleComponentSpec action in rule.Actions)
            RemapVariableIds(action, ids);
    }

    private static void RemapVariableIds(ConditionGroupDefinition group, IReadOnlyDictionary<string, string> ids)
    {
        foreach (RuleComponentSpec condition in group.Conditions)
            RemapVariableIds(condition, ids);
        foreach (ConditionGroupDefinition child in group.Groups)
            RemapVariableIds(child, ids);
    }

    private static void RemapVariableIds(RuleComponentSpec component, IReadOnlyDictionary<string, string> ids)
    {
        string direct = RuleComponentParameterService.GetString(component, "variableId");
        if (ids.TryGetValue(direct, out string? replacement))
            RuleComponentParameterService.Set(component, "variableId", replacement);
        foreach ((string key, JsonElement element) in component.Parameters.ToList())
        {
            try
            {
                NumericValueSpec? numeric = element.Deserialize<NumericValueSpec>(CustomRunSerializationService.SharedJsonOptions);
                if (numeric?.Source == NumericValueSourceKind.Variable
                    && numeric.ReferenceId is not null
                    && ids.TryGetValue(numeric.ReferenceId, out replacement))
                {
                    numeric.ReferenceId = replacement;
                    RuleComponentParameterService.Set(component, key, numeric);
                }
            }
            catch (JsonException)
            {
            }
        }
    }

    private static void SaveLocked()
    {
        _profile = NormalizeProfile(_profile);
        SaveUtility.SaveProfileJson(ProfilePath, _profile);
    }

    private static void OnProfileIdChanged(int _)
    {
        lock (SyncRoot)
        {
            _profile = new PermanentRuleProfileSaveData();
            _loaded = false;
        }
        EnsureLoaded();
        Changed?.Invoke();
    }
}

public sealed class PermanentRuleProfileSaveData : ISerializable
{
    public PermanentRuleProfileSaveData()
    {
    }

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = PermanentRuleStorageService.CurrentSchemaVersion;

    [JsonPropertyName("rules")]
    public List<RuleDefinition> Rules { get; set; } = [];

    [JsonPropertyName("bundles")]
    public List<PermanentRuleBundle> Bundles { get; set; } = [];

    public void GetObjectData(SerializationInfo info, StreamingContext context)
    {
        info.AddValue(nameof(SchemaVersion), SchemaVersion);
        info.AddValue(nameof(Rules), Rules);
        info.AddValue(nameof(Bundles), Bundles);
    }
}

public sealed class PermanentRuleBundle
{
    [JsonPropertyName("rule")]
    public RuleDefinition Rule { get; set; } = new();

    [JsonPropertyName("variables")]
    public List<VariableDefinition> Variables { get; set; } = [];
}
