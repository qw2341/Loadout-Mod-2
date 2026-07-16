#nullable enable

namespace Loadout.Services.CardModification;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using Loadout.Services.Saving;
using Loadout.Patches.Cards.CardModification;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;

/// <summary>
/// Durable profile state only: one immutable modification spec per card ModelId.
/// Published dictionaries are never mutated, so rendering/creation hooks read without
/// locks and without creating per-card cache entries.
/// </summary>
public static class PermanentCardModificationStore
{
    private const int CurrentSchemaVersion = 1;
    private const string PermanentPath = "loadout/services/card_modifications/permanent.json";

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static Dictionary<string, CardModificationSpec> _profileCards = new(StringComparer.Ordinal);
    private static Dictionary<string, CardModificationSpec> _hostCards = new(StringComparer.Ordinal);
    private static Dictionary<ModelId, CardModificationSpec> _profileLookup = new();
    private static Dictionary<ModelId, CardModificationSpec> _hostLookup = new();
    private static bool _useHostCards;
    private static bool _loaded;
    private static bool _registered;
    private static bool _saveQueued;

    public static event Action<ModelId>? CardChanged;
    public static event Action? Reloaded;

    public static void Register()
    {
        if (_registered)
            return;

        _registered = true;
        SaveManager.Instance.ProfileIdChanged += OnProfileIdChanged;
        Reload();
    }

    public static void Unregister()
    {
        if (!_registered)
            return;

        FlushPendingSave();
        SaveManager.Instance.ProfileIdChanged -= OnProfileIdChanged;
        _registered = false;
    }

    public static void EnsureLoaded()
    {
        if (_loaded)
            return;

        lock (Gate)
        {
            if (!_loaded)
                LoadProfileLocked();
        }
    }

    public static bool TryGet(ModelId cardId, out CardModificationSpec spec)
    {
        EnsureLoaded();
        Dictionary<ModelId, CardModificationSpec> snapshot = _useHostCards ? _hostLookup : _profileLookup;
        return snapshot.TryGetValue(cardId, out spec!);
    }

    public static CardModificationSpec Get(ModelId cardId)
    {
        return TryGet(cardId, out CardModificationSpec? spec)
            ? spec.Clone()
            : new CardModificationSpec();
    }

    public static int Count
    {
        get
        {
            EnsureLoaded();
            return _profileCards.Count;
        }
    }

    public static bool SetProfile(ModelId cardId, CardModificationSpec? value)
    {
        EnsureLoaded();
        CardModificationSpec normalized = Normalize(value);
        string key = cardId.ToString();
        bool changed;
        lock (Gate)
        {
            Dictionary<string, CardModificationSpec> next = CloneDictionary(_profileCards);
            changed = SetInDictionary(next, key, normalized);
            if (!changed)
                return false;

            _profileCards = next;
            // The ModelId index references the same immutable specs as the durable
            // string-keyed snapshot; it is an index, not a second state store.
            _profileLookup = BuildLookup(next);
            QueueSaveLocked();
        }

        CardChanged?.Invoke(cardId);
        return true;
    }

    public static IReadOnlyList<ModelId> ResetAllProfile()
    {
        EnsureLoaded();
        List<ModelId> changed = ResolveIds(_profileCards.Keys);
        if (changed.Count == 0)
            return changed;

        lock (Gate)
        {
            _profileCards = new Dictionary<string, CardModificationSpec>(StringComparer.Ordinal);
            _profileLookup = new Dictionary<ModelId, CardModificationSpec>();
            QueueSaveLocked();
        }

        foreach (ModelId id in changed)
            CardChanged?.Invoke(id);
        return changed;
    }

    public static string ExportEffectiveSnapshotJson()
    {
        EnsureLoaded();
        Dictionary<string, CardModificationSpec> source = _useHostCards ? _hostCards : _profileCards;
        return JsonSerializer.Serialize(new PermanentSaveData
        {
            SchemaVersion = CurrentSchemaVersion,
            Cards = CloneDictionary(source)
        }, JsonOptions);
    }

    public static IReadOnlyList<ModelId> ApplyHostSnapshot(string? json)
    {
        if (!TryDeserializeSnapshot(json, out Dictionary<string, CardModificationSpec> next))
            return [];

        Dictionary<string, CardModificationSpec> previous = _useHostCards ? _hostCards : _profileCards;
        List<ModelId> changed = GetChangedIds(previous, next);
        _hostCards = next;
        _hostLookup = BuildLookup(next);
        _useHostCards = true;
        foreach (ModelId id in changed)
            CardChanged?.Invoke(id);
        return changed;
    }

    public static bool ApplyHostDelta(ModelId cardId, CardModificationSpec? value)
    {
        EnsureLoaded();
        Dictionary<string, CardModificationSpec> next = CloneDictionary(_useHostCards ? _hostCards : _profileCards);
        bool changed = SetInDictionary(next, cardId.ToString(), Normalize(value));
        _hostCards = next;
        _hostLookup = BuildLookup(next);
        _useHostCards = true;
        if (changed)
            CardChanged?.Invoke(cardId);
        return changed;
    }

    public static IReadOnlyList<ModelId> ClearHostOverlay()
    {
        if (!_useHostCards)
            return [];

        List<ModelId> changed = GetChangedIds(_hostCards, _profileCards);
        _hostCards = new Dictionary<string, CardModificationSpec>(StringComparer.Ordinal);
        _hostLookup = new Dictionary<ModelId, CardModificationSpec>();
        _useHostCards = false;
        foreach (ModelId id in changed)
            CardChanged?.Invoke(id);
        return changed;
    }

    public static IReadOnlyList<ModelId> ImportSnapshotToProfile(
        string? json,
        CardModificationPermanentImportMode mode)
    {
        EnsureLoaded();
        if (!TryDeserializeSnapshot(json, out Dictionary<string, CardModificationSpec> incoming))
            return [];

        if (mode == CardModificationPermanentImportMode.KeepMine)
            return [];

        Dictionary<string, CardModificationSpec> next = mode == CardModificationPermanentImportMode.UseHost
            ? CloneDictionary(incoming)
            : CloneDictionary(_profileCards);
        if (mode == CardModificationPermanentImportMode.MergeNonConflicting)
        {
            foreach ((string key, CardModificationSpec spec) in incoming)
            {
                if (!next.ContainsKey(key))
                    next[key] = spec.Clone();
            }
        }

        List<ModelId> changed = GetChangedIds(_profileCards, next);
        if (changed.Count == 0)
            return changed;

        lock (Gate)
        {
            _profileCards = next;
            _profileLookup = BuildLookup(next);
            QueueSaveLocked();
        }

        foreach (ModelId id in changed)
            CardChanged?.Invoke(id);
        return changed;
    }

    public static void FlushPendingSave()
    {
        PermanentSaveData? snapshot = null;
        lock (Gate)
        {
            if (!_saveQueued)
                return;

            _saveQueued = false;
            snapshot = new PermanentSaveData
            {
                SchemaVersion = CurrentSchemaVersion,
                Cards = CloneDictionary(_profileCards)
            };
        }

        SaveUtility.SaveProfileJson(PermanentPath, snapshot.Value);
    }

    private static void Reload()
    {
        lock (Gate)
        {
            _loaded = false;
            _useHostCards = false;
            _hostCards = new Dictionary<string, CardModificationSpec>(StringComparer.Ordinal);
            _hostLookup = new Dictionary<ModelId, CardModificationSpec>();
            LoadProfileLocked();
        }

        Reloaded?.Invoke();
    }

    private static void LoadProfileLocked()
    {
        SaveUtility.LoadResult<PermanentSaveData> loaded =
            SaveUtility.LoadProfileJson(PermanentPath, new PermanentSaveData());
        _profileCards = NormalizeDictionary(loaded.Value.Cards);
        _profileLookup = BuildLookup(_profileCards);
        _loaded = true;
        if (loaded.Loaded && loaded.Value.SchemaVersion != CurrentSchemaVersion)
            QueueSaveLocked();
    }

    private static void QueueSaveLocked()
    {
        if (_saveQueued)
            return;

        _saveQueued = true;
        Callable.From(FlushPendingSave).CallDeferred();
    }

    private static void OnProfileIdChanged(int _)
    {
        FlushPendingSave();
        Reload();
    }

    private static CardModificationSpec Normalize(CardModificationSpec? value)
    {
        CardModificationSpec normalized = value?.Clone() ?? new CardModificationSpec();
        normalized.Normalize();
        return normalized;
    }

    private static bool SetInDictionary(
        Dictionary<string, CardModificationSpec> dictionary,
        string key,
        CardModificationSpec value)
    {
        if (value.IsEmpty)
            return dictionary.Remove(key);

        if (dictionary.TryGetValue(key, out CardModificationSpec? current)
            && CardModificationCodec.Serialize(current) == CardModificationCodec.Serialize(value))
        {
            return false;
        }

        dictionary[key] = value.Clone();
        return true;
    }

    private static bool TryDeserializeSnapshot(
        string? json,
        out Dictionary<string, CardModificationSpec> snapshot)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            snapshot = new Dictionary<string, CardModificationSpec>(StringComparer.Ordinal);
            return true;
        }

        try
        {
            PermanentSaveData save = JsonSerializer.Deserialize<PermanentSaveData>(json, JsonOptions);
            snapshot = NormalizeDictionary(save.Cards);
            return true;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: ignored invalid permanent snapshot. {exception.Message}");
            snapshot = new Dictionary<string, CardModificationSpec>(StringComparer.Ordinal);
            return false;
        }
    }

    private static Dictionary<string, CardModificationSpec> NormalizeDictionary(
        Dictionary<string, CardModificationSpec>? source)
    {
        Dictionary<string, CardModificationSpec> result = new(StringComparer.Ordinal);
        if (source is null)
            return result;

        foreach ((string key, CardModificationSpec? spec) in source)
        {
            if (string.IsNullOrWhiteSpace(key) || spec is null)
                continue;

            CardModificationSpec normalized = Normalize(spec);
            if (!normalized.IsEmpty)
                result[key] = normalized;
        }

        return result;
    }

    private static Dictionary<string, CardModificationSpec> CloneDictionary(
        IReadOnlyDictionary<string, CardModificationSpec> source)
    {
        return source.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
    }

    private static List<ModelId> GetChangedIds(
        IReadOnlyDictionary<string, CardModificationSpec> previous,
        IReadOnlyDictionary<string, CardModificationSpec> next)
    {
        HashSet<string> keys = new(previous.Keys, StringComparer.Ordinal);
        keys.UnionWith(next.Keys);
        return ResolveIds(keys.Where(key =>
        {
            previous.TryGetValue(key, out CardModificationSpec? left);
            next.TryGetValue(key, out CardModificationSpec? right);
            return !string.Equals(
                left is null ? string.Empty : CardModificationCodec.Serialize(left),
                right is null ? string.Empty : CardModificationCodec.Serialize(right),
                StringComparison.Ordinal);
        }));
    }

    private static List<ModelId> ResolveIds(IEnumerable<string> keys)
    {
        HashSet<string> wanted = new(keys, StringComparer.Ordinal);
        return ModelDb.AllCards
            .Where(card => wanted.Contains(card.Id.ToString()))
            .Select(card => card.Id)
            .ToList();
    }

    private static Dictionary<ModelId, CardModificationSpec> BuildLookup(
        IReadOnlyDictionary<string, CardModificationSpec> source)
    {
        Dictionary<ModelId, CardModificationSpec> result = new();
        if (source.Count == 0)
            return result;

        foreach (CardModel card in ModelDb.AllCards)
        {
            if (source.TryGetValue(card.Id.ToString(), out CardModificationSpec? spec))
                result[card.Id] = spec;
        }
        return result;
    }

    private struct PermanentSaveData : ISerializable
    {
        public PermanentSaveData()
        {
        }

        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [JsonPropertyName("cards")]
        public Dictionary<string, CardModificationSpec> Cards { get; set; } = new(StringComparer.Ordinal);

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue(nameof(SchemaVersion), SchemaVersion);
            info.AddValue(nameof(Cards), Cards);
        }
    }
}
