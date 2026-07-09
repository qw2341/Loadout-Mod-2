#nullable enable

namespace Loadout.Services.LastActions;

using Godot;
using Loadout.Services.PowerGiver;
using Loadout.Services.Saving;
using Loadout.Services.Targets;
using MegaCrit.Sts2.Core.Saves;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

public static class LastActionService
{
    public const string LoadoutBagKey = "loadout_bag";
    public const string CardPrinterKey = "card_printer";
    public const string EventfulCompassKey = "eventful_compass";
    public const string PowerGiverKey = "power_giver";
    public const string BottleMonsterKey = "bottle_monster";

    public const string AddCardKind = "add_card";
    public const string AddRelicKind = "add_relic";
    public const string EnterEventKind = "enter_event";
    public const string AdjustPowerKind = "adjust_power";
    public const string SummonMonsterKind = "summon_monster";

    private const int CurrentSchemaVersion = 1;
    private const string SavePath = "loadout/services/last_actions.json";

    private static readonly object SyncRoot = new();
    private static SaveData _save = new();
    private static bool _loaded;
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
            return;

        _registered = true;
        SaveManager.Instance.ProfileIdChanged += OnProfileIdChanged;
        EnsureLoaded();
    }

    public static void Unregister()
    {
        if (!_registered)
            return;

        SaveManager.Instance.ProfileIdChanged -= OnProfileIdChanged;
        _registered = false;
    }

    public static IReadOnlyList<LastActionEntry> GetAction(string itemKey)
    {
        EnsureLoaded();
        lock (SyncRoot)
        {
            return _save.ActionsByItemKey.TryGetValue(itemKey, out List<LastActionEntry>? entries)
                ? entries.Select(CloneEntry).ToList()
                : [];
        }
    }

    public static void SaveAction(string itemKey, IReadOnlyList<LastActionEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(itemKey))
            return;

        List<LastActionEntry> normalized = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Kind)
                            && !string.IsNullOrWhiteSpace(entry.ContentId)
                            && entry.Amount != 0)
            .Select(CloneEntry)
            .ToList();

        if (normalized.Count == 0)
            return;

        EnsureLoaded();
        lock (SyncRoot)
        {
            _save.ActionsByItemKey[itemKey] = normalized;
            Save();
        }
    }

    private static void OnProfileIdChanged(int _)
    {
        lock (SyncRoot)
        {
            _loaded = false;
            _save = new SaveData();
        }
    }

    private static void EnsureLoaded()
    {
        lock (SyncRoot)
        {
            if (_loaded)
                return;

            SaveUtility.LoadResult<SaveData> loaded = SaveUtility.LoadProfileJson(SavePath, new SaveData());
            _save = NormalizeSave(loaded.Value);
            _loaded = true;

            if (loaded.Loaded && loaded.Value.SchemaVersion != CurrentSchemaVersion)
                Save();
        }
    }

    private static void Save()
    {
        _save.SchemaVersion = CurrentSchemaVersion;
        SaveUtility.SaveProfileJson(SavePath, _save);
    }

    private static SaveData NormalizeSave(SaveData save)
    {
        save.SchemaVersion = CurrentSchemaVersion;
        save.ActionsByItemKey ??= new Dictionary<string, List<LastActionEntry>>(StringComparer.Ordinal);

        Dictionary<string, List<LastActionEntry>> normalized = new(StringComparer.Ordinal);
        foreach ((string itemKey, List<LastActionEntry>? entries) in save.ActionsByItemKey)
        {
            if (string.IsNullOrWhiteSpace(itemKey) || entries is null)
                continue;

            List<LastActionEntry> normalizedEntries = entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Kind)
                                && !string.IsNullOrWhiteSpace(entry.ContentId)
                                && entry.Amount != 0)
                .Select(CloneEntry)
                .ToList();

            if (normalizedEntries.Count > 0)
                normalized[itemKey] = normalizedEntries;
        }

        save.ActionsByItemKey = normalized;
        return save;
    }

    private static LastActionEntry CloneEntry(LastActionEntry entry)
    {
        return new LastActionEntry
        {
            Kind = entry.Kind,
            ContentId = entry.ContentId,
            Amount = entry.Amount,
            Target = entry.Target,
            TargetScope = entry.TargetScope,
            TargetPlayerNetId = entry.TargetPlayerNetId
        };
    }

    private struct SaveData : ISerializable
    {
        public SaveData()
        {
        }

        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [JsonPropertyName("actionsByItemKey")]
        public Dictionary<string, List<LastActionEntry>> ActionsByItemKey { get; set; } = new(StringComparer.Ordinal);

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue(nameof(SchemaVersion), SchemaVersion);
            info.AddValue(nameof(ActionsByItemKey), ActionsByItemKey);
        }
    }
}

public sealed class LastActionEntry
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("contentId")]
    public string ContentId { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public int Amount { get; set; }

    [JsonPropertyName("target")]
    public PowerGiverTarget? Target { get; set; }

    [JsonPropertyName("targetScope")]
    public LoadoutTargetScope? TargetScope { get; set; }

    [JsonPropertyName("targetPlayerNetId")]
    public ulong? TargetPlayerNetId { get; set; }

    public void SetTargetSelection(LoadoutTargetSelection selection)
    {
        Target = null;
        TargetScope = selection.Scope;
        TargetPlayerNetId = selection.PlayerNetId;
    }

    public LoadoutTargetSelection GetTargetSelection(LoadoutTargetSelection fallback)
    {
        if (TargetScope.HasValue)
            return new LoadoutTargetSelection(TargetScope.Value, TargetPlayerNetId);

        return Target switch
        {
            PowerGiverTarget.Monsters => new LoadoutTargetSelection(LoadoutTargetScope.AllMonsters),
            PowerGiverTarget.Player => fallback,
            _ => fallback
        };
    }
}
