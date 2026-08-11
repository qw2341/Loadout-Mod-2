#nullable enable

namespace Loadout.Services.CustomRuns.PermanentRules;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.CustomRuns.Compilation;
using Loadout.Services.CustomRuns.Persistence;
using Loadout.Services.Saving;
using MegaCrit.Sts2.Core.Saves;

public static class PermanentRuleStorageService
{
    public const int CurrentSchemaVersion = 1;

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
            return _profile.Rules.Select(CustomRunNormalizationService.CloneRule).ToList();
    }

    public static RuleDefinition Upsert(RuleDefinition rule)
    {
        EnsureLoaded();
        RuleDefinition normalized = CustomRunNormalizationService.NormalizeRule(
            CustomRunNormalizationService.CloneRule(rule));
        lock (SyncRoot)
        {
            int index = _profile.Rules.FindIndex(existing =>
                string.Equals(existing.Id, normalized.Id, StringComparison.Ordinal));
            if (index < 0)
            {
                string hash = RuleBehaviorHashService.Compute(normalized);
                index = _profile.Rules.FindIndex(existing =>
                    string.Equals(RuleBehaviorHashService.Compute(existing), hash, StringComparison.Ordinal));
            }
            if (index >= 0)
            {
                normalized.Id = _profile.Rules[index].Id;
                _profile.Rules[index] = normalized;
            }
            else
                _profile.Rules.Add(normalized);
            string normalizedHash = RuleBehaviorHashService.Compute(normalized);
            _profile.Rules.RemoveAll(existing =>
                !string.Equals(existing.Id, normalized.Id, StringComparison.Ordinal)
                && string.Equals(RuleBehaviorHashService.Compute(existing), normalizedHash, StringComparison.Ordinal));
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
            RuleDefinition? rule = _profile.Rules.FirstOrDefault(candidate =>
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
            changed = _profile.Rules.RemoveAll(rule =>
                string.Equals(rule.Id, id, StringComparison.Ordinal)) > 0;
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
            int sourceIndex = _profile.Rules.FindIndex(rule =>
                string.Equals(rule.Id, sourceId, StringComparison.Ordinal));
            if (sourceIndex < 0)
                return false;

            RuleDefinition source = _profile.Rules[sourceIndex];
            _profile.Rules.RemoveAt(sourceIndex);
            int targetIndex = string.IsNullOrEmpty(targetId)
                ? _profile.Rules.Count
                : _profile.Rules.FindIndex(rule => string.Equals(rule.Id, targetId, StringComparison.Ordinal));
            if (targetIndex < 0)
                targetIndex = _profile.Rules.Count;
            else if (placeAfter)
                targetIndex++;
            targetIndex = Math.Clamp(targetIndex, 0, _profile.Rules.Count);
            _profile.Rules.Insert(targetIndex, source);
            changed = targetIndex != sourceIndex;
            if (changed)
                SaveLocked();
        }
        if (changed)
            Changed?.Invoke();
        return changed;
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
        profile.SchemaVersion = CurrentSchemaVersion;
        profile.Rules = (profile.Rules ?? [])
            .Where(rule => rule is not null)
            .Select(CustomRunNormalizationService.NormalizeRule)
            .GroupBy(rule => rule.Id, StringComparer.Ordinal)
            .Select(group => group.Last())
            .GroupBy(RuleBehaviorHashService.Compute, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToList();
        return profile;
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

    public void GetObjectData(SerializationInfo info, StreamingContext context)
    {
        info.AddValue(nameof(SchemaVersion), SchemaVersion);
        info.AddValue(nameof(Rules), Rules);
    }
}
