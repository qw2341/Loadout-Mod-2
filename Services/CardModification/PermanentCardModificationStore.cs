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
    private const int CurrentSchemaVersion = 2;
    private const string PermanentPath = "loadout/services/card_modifications/permanent.json";

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static Dictionary<string, CardModificationDelta> _profileCards = new(StringComparer.Ordinal);
    private static Dictionary<string, CardModificationDelta> _hostCards = new(StringComparer.Ordinal);
    private static Dictionary<ModelId, CardModificationDelta> _profileLookup = new();
    private static Dictionary<ModelId, CardModificationDelta> _hostLookup = new();
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
        if (TryGetDelta(cardId, out CardModificationDelta delta))
        {
            spec = CardModificationRuntime.MaterializePermanentSpec(cardId, delta);
            return true;
        }

        spec = null!;
        return false;
    }

    public static bool TryGetDelta(ModelId cardId, out CardModificationDelta delta)
    {
        EnsureLoaded();
        Dictionary<ModelId, CardModificationDelta> snapshot = _useHostCards ? _hostLookup : _profileLookup;
        return snapshot.TryGetValue(cardId, out delta!);
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

    public static bool HasAnyCustomText
    {
        get
        {
            EnsureLoaded();
            return (_useHostCards ? _hostCards : _profileCards).Values.Any(delta => delta.HasCustomText);
        }
    }

    public static bool HasAnyPortraitOverrides
    {
        get
        {
            EnsureLoaded();
            return (_useHostCards ? _hostCards : _profileCards).Values.Any(delta => delta.HasPortraitOverride);
        }
    }

    public static bool HasAnyCreationResidual
    {
        get
        {
            EnsureLoaded();
            return (_useHostCards ? _hostCards : _profileCards).Values.Any(delta =>
                delta.Enchantment is not null || delta.Affliction is not null);
        }
    }

    internal static IReadOnlyDictionary<ModelId, CardModificationDelta> GetEffectiveDeltasSnapshot()
    {
        EnsureLoaded();
        Dictionary<ModelId, CardModificationDelta> source = _useHostCards ? _hostLookup : _profileLookup;
        return source.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
    }

    public static bool SetProfile(ModelId cardId, CardModificationSpec? value)
    {
        return SetProfileDelta(cardId, CardModificationRuntime.CreatePermanentDelta(cardId, value));
    }

    public static bool SetProfileDelta(ModelId cardId, CardModificationDelta? value)
    {
        EnsureLoaded();
        CardModificationDelta normalized = value?.Clone() ?? new CardModificationDelta();
        normalized.Normalize();
        string key = cardId.ToString();
        bool changed;
        lock (Gate)
        {
            Dictionary<string, CardModificationDelta> next = CloneDictionary(_profileCards);
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
            _profileCards = new Dictionary<string, CardModificationDelta>(StringComparer.Ordinal);
            _profileLookup = new Dictionary<ModelId, CardModificationDelta>();
            QueueSaveLocked();
        }

        foreach (ModelId id in changed)
            CardChanged?.Invoke(id);
        return changed;
    }

    public static string ExportEffectiveSnapshotJson()
    {
        EnsureLoaded();
        Dictionary<string, CardModificationDelta> source = _useHostCards ? _hostCards : _profileCards;
        return JsonSerializer.Serialize(new PermanentSaveData
        {
            SchemaVersion = CurrentSchemaVersion,
            Cards = CloneDictionary(source)
        }, JsonOptions);
    }

    public static IReadOnlyList<ModelId> ApplyHostSnapshot(string? json)
    {
        if (!TryDeserializeSnapshot(json, out Dictionary<string, CardModificationDelta> next))
            return [];

        Dictionary<string, CardModificationDelta> previous = _useHostCards ? _hostCards : _profileCards;
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
        return ApplyHostDelta(cardId, CardModificationRuntime.CreatePermanentDelta(cardId, value));
    }

    public static bool ApplyHostDelta(ModelId cardId, CardModificationDelta? value)
    {
        EnsureLoaded();
        CardModificationDelta normalized = value?.Clone() ?? new CardModificationDelta();
        normalized.Normalize();
        Dictionary<string, CardModificationDelta> next = CloneDictionary(_useHostCards ? _hostCards : _profileCards);
        bool changed = SetInDictionary(next, cardId.ToString(), normalized);
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
        _hostCards = new Dictionary<string, CardModificationDelta>(StringComparer.Ordinal);
        _hostLookup = new Dictionary<ModelId, CardModificationDelta>();
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
        if (!TryDeserializeSnapshot(json, out Dictionary<string, CardModificationDelta> incoming))
            return [];

        if (mode == CardModificationPermanentImportMode.KeepMine)
            return [];

        Dictionary<string, CardModificationDelta> next = mode == CardModificationPermanentImportMode.UseHost
            ? CloneDictionary(incoming)
            : CloneDictionary(_profileCards);
        if (mode == CardModificationPermanentImportMode.MergeNonConflicting)
        {
            foreach ((string key, CardModificationDelta spec) in incoming)
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
            _hostCards = new Dictionary<string, CardModificationDelta>(StringComparer.Ordinal);
            _hostLookup = new Dictionary<ModelId, CardModificationDelta>();
            LoadProfileLocked();
        }

        Reloaded?.Invoke();
    }

    private static void LoadProfileLocked()
    {
        SaveUtility.LoadResult<PermanentRawData> loaded =
            SaveUtility.LoadProfileJson(PermanentPath, new PermanentRawData());
        _profileCards = DeserializeCards(loaded.Value);
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

    private static bool SetInDictionary(
        Dictionary<string, CardModificationDelta> dictionary,
        string key,
        CardModificationDelta value)
    {
        if (value.IsEmpty)
            return dictionary.Remove(key);

        if (dictionary.TryGetValue(key, out CardModificationDelta? current)
            && CardModificationCodec.SerializeDelta(current) == CardModificationCodec.SerializeDelta(value))
        {
            return false;
        }

        dictionary[key] = value.Clone();
        return true;
    }

    private static bool TryDeserializeSnapshot(
        string? json,
        out Dictionary<string, CardModificationDelta> snapshot)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            snapshot = new Dictionary<string, CardModificationDelta>(StringComparer.Ordinal);
            return true;
        }

        try
        {
            PermanentRawData save = JsonSerializer.Deserialize<PermanentRawData>(json, JsonOptions);
            snapshot = DeserializeCards(save);
            return true;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: ignored invalid permanent snapshot. {exception.Message}");
            snapshot = new Dictionary<string, CardModificationDelta>(StringComparer.Ordinal);
            return false;
        }
    }

    private static Dictionary<string, CardModificationDelta> DeserializeCards(PermanentRawData save)
    {
        Dictionary<string, CardModificationDelta> result = new(StringComparer.Ordinal);
        if (save.Cards is null)
            return result;

        foreach ((string key, JsonElement element) in save.Cards)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            CardModificationDelta? delta;
            if (save.SchemaVersion >= CurrentSchemaVersion)
            {
                delta = element.Deserialize<CardModificationDelta>(JsonOptions);
            }
            else
            {
                CardModificationSpec? legacy = element.Deserialize<CardModificationSpec>(JsonOptions);
                delta = TryResolveId(key, out ModelId id)
                    ? CardModificationRuntime.CreatePermanentDelta(id, legacy)
                    : null;
            }

            delta?.Normalize();
            if (delta is { IsEmpty: false }) result[key] = delta;
        }

        return result;
    }

    private static Dictionary<string, CardModificationDelta> CloneDictionary(
        IReadOnlyDictionary<string, CardModificationDelta> source)
    {
        return source.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
    }

    private static List<ModelId> GetChangedIds(
        IReadOnlyDictionary<string, CardModificationDelta> previous,
        IReadOnlyDictionary<string, CardModificationDelta> next)
    {
        HashSet<string> keys = new(previous.Keys, StringComparer.Ordinal);
        keys.UnionWith(next.Keys);
        return ResolveIds(keys.Where(key =>
        {
            previous.TryGetValue(key, out CardModificationDelta? left);
            next.TryGetValue(key, out CardModificationDelta? right);
            return !string.Equals(
                left is null ? string.Empty : CardModificationCodec.SerializeDelta(left),
                right is null ? string.Empty : CardModificationCodec.SerializeDelta(right),
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

    private static Dictionary<ModelId, CardModificationDelta> BuildLookup(
        IReadOnlyDictionary<string, CardModificationDelta> source)
    {
        Dictionary<ModelId, CardModificationDelta> result = new();
        if (source.Count == 0)
            return result;

        foreach (CardModel card in ModelDb.AllCards)
        {
            if (source.TryGetValue(card.Id.ToString(), out CardModificationDelta? spec))
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
        public Dictionary<string, CardModificationDelta> Cards { get; set; } = new(StringComparer.Ordinal);

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue(nameof(SchemaVersion), SchemaVersion);
            info.AddValue(nameof(Cards), Cards);
        }
    }

    private struct PermanentRawData
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("cards")]
        public Dictionary<string, JsonElement> Cards { get; set; }
    }

    private static bool TryResolveId(string key, out ModelId id)
    {
        foreach (CardModel card in ModelDb.AllCards)
        {
            if (string.Equals(card.Id.ToString(), key, StringComparison.Ordinal)
                || string.Equals(card.Id.Entry, key, StringComparison.OrdinalIgnoreCase))
            {
                id = card.Id;
                return true;
            }
        }
        id = ModelId.none;
        return false;
    }
}
