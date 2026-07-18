#nullable enable

namespace Loadout.Services.RelicModification;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using HarmonyLib;
using Loadout.Services.Actions;
using Loadout.Services.Saving;
using Loadout.Services.Targets;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using Loadout.Services.TildeKey;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

public enum RelicModificationOperation
{
    None,
    SaveTemporary,
    ResetTemporary,
    ResetTemporaryToBasic,
    ApplyPermanent,
    ResetPermanentToBasic
}

public enum RelicPrimitiveKind
{
    Boolean,
    Integer,
    Decimal,
    String,
    Enum
}

public sealed class RelicPrimitiveValue
{
    [JsonPropertyName("k")]
    public RelicPrimitiveKind Kind { get; set; }

    [JsonPropertyName("v")]
    public string Value { get; set; } = string.Empty;

    public RelicPrimitiveValue Clone() => new() { Kind = Kind, Value = Value };

    public static RelicPrimitiveValue FromObject(object? value, Type type)
    {
        Type actual = Nullable.GetUnderlyingType(type) ?? type;
        if (actual == typeof(bool))
            return new RelicPrimitiveValue { Kind = RelicPrimitiveKind.Boolean, Value = ((bool?)value ?? false) ? "1" : "0" };
        if (actual == typeof(string))
            return new RelicPrimitiveValue { Kind = RelicPrimitiveKind.String, Value = value as string ?? string.Empty };
        if (actual.IsEnum)
            return new RelicPrimitiveValue { Kind = RelicPrimitiveKind.Enum, Value = value?.ToString() ?? Enum.GetNames(actual).FirstOrDefault() ?? string.Empty };
        if (actual == typeof(float) || actual == typeof(double) || actual == typeof(decimal))
            return new RelicPrimitiveValue { Kind = RelicPrimitiveKind.Decimal, Value = Convert.ToDecimal(value ?? 0, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) };
        return new RelicPrimitiveValue { Kind = RelicPrimitiveKind.Integer, Value = Convert.ToString(value ?? 0, CultureInfo.InvariantCulture) ?? "0" };
    }

    public bool TryConvert(Type targetType, out object? value)
    {
        Type actual = Nullable.GetUnderlyingType(targetType) ?? targetType;
        try
        {
            if (actual == typeof(string))
                value = Value;
            else if (actual == typeof(bool))
                value = Value == "1" || bool.Parse(Value);
            else if (actual.IsEnum)
                value = Enum.Parse(actual, Value, ignoreCase: true);
            else if (actual == typeof(decimal))
                value = decimal.Parse(Value, CultureInfo.InvariantCulture);
            else if (actual == typeof(double))
                value = double.Parse(Value, CultureInfo.InvariantCulture);
            else if (actual == typeof(float))
                value = float.Parse(Value, CultureInfo.InvariantCulture);
            else
                value = Convert.ChangeType(Value, actual, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            value = null;
            return false;
        }
    }
}

public sealed class RelicModificationState
{
    [JsonPropertyName("r")]
    public string? Rarity { get; set; }
    [JsonPropertyName("d")]
    public Dictionary<string, decimal> DynamicVars { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("t")]
    public string? CustomTitle { get; set; }
    [JsonPropertyName("x")]
    public string? CustomDescription { get; set; }
    [JsonPropertyName("f")]
    public string? CustomFlavor { get; set; }
    [JsonPropertyName("w")]
    public bool? IsWax { get; set; }
    [JsonPropertyName("m")]
    public bool? IsMelted { get; set; }
    [JsonPropertyName("s")]
    public string? Status { get; set; }
    [JsonPropertyName("nm")]
    public bool? NeverMelt { get; set; }
    [JsonPropertyName("nu")]
    public bool? NeverUsed { get; set; }
    [JsonPropertyName("p")]
    public Dictionary<string, RelicPrimitiveValue> PrimitiveValues { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("cm")]
    public string? CounterMember { get; set; }
    [JsonPropertyName("cv")]
    public int? CounterValue { get; set; }

    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Rarity)
                           && DynamicVars.Count == 0
                           && string.IsNullOrWhiteSpace(CustomTitle)
                           && string.IsNullOrWhiteSpace(CustomDescription)
                           && string.IsNullOrWhiteSpace(CustomFlavor)
                           && IsWax is null && IsMelted is null && string.IsNullOrWhiteSpace(Status)
                           && NeverMelt is null && NeverUsed is null && PrimitiveValues.Count == 0
                           && string.IsNullOrWhiteSpace(CounterMember) && CounterValue is null;

    public RelicModificationState Clone() => new()
    {
        Rarity = Rarity,
        DynamicVars = new Dictionary<string, decimal>(DynamicVars, StringComparer.Ordinal),
        CustomTitle = CustomTitle,
        CustomDescription = CustomDescription,
        CustomFlavor = CustomFlavor,
        IsWax = IsWax,
        IsMelted = IsMelted,
        Status = Status,
        NeverMelt = NeverMelt,
        NeverUsed = NeverUsed,
        PrimitiveValues = PrimitiveValues.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal),
        CounterMember = CounterMember,
        CounterValue = CounterValue
    };

    public void MergeFrom(RelicModificationState other)
    {
        if (other.Rarity is not null) Rarity = other.Rarity;
        foreach ((string key, decimal value) in other.DynamicVars) DynamicVars[key] = value;
        if (other.CustomTitle is not null) CustomTitle = other.CustomTitle;
        if (other.CustomDescription is not null) CustomDescription = other.CustomDescription;
        if (other.CustomFlavor is not null) CustomFlavor = other.CustomFlavor;
        if (other.IsWax.HasValue) IsWax = other.IsWax;
        if (other.IsMelted.HasValue) IsMelted = other.IsMelted;
        if (other.Status is not null) Status = other.Status;
        if (other.NeverMelt.HasValue) NeverMelt = other.NeverMelt;
        if (other.NeverUsed.HasValue) NeverUsed = other.NeverUsed;
        foreach ((string key, RelicPrimitiveValue value) in other.PrimitiveValues) PrimitiveValues[key] = value.Clone();
        if (other.CounterMember is not null) CounterMember = other.CounterMember;
        if (other.CounterValue.HasValue) CounterValue = other.CounterValue;
    }

    public void Normalize()
    {
        Rarity = NormalizeText(Rarity);
        CustomTitle = NormalizeText(CustomTitle);
        CustomDescription = NormalizeText(CustomDescription);
        CustomFlavor = NormalizeText(CustomFlavor);
        Status = NormalizeText(Status);
        CounterMember = NormalizeText(CounterMember);
        if (CounterMember is null) CounterValue = null;
        DynamicVars ??= new Dictionary<string, decimal>(StringComparer.Ordinal);
        PrimitiveValues ??= new Dictionary<string, RelicPrimitiveValue>(StringComparer.Ordinal);
        DynamicVars = new Dictionary<string, decimal>(DynamicVars, StringComparer.Ordinal);
        PrimitiveValues = PrimitiveValues.Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (NeverMelt == true) IsMelted = false;
        if (NeverUsed == true && string.Equals(Status, "Disabled", StringComparison.OrdinalIgnoreCase)) Status = "Normal";
    }

    private static string? NormalizeText(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

public sealed class RelicSavedPropertyDescriptor
{
    internal RelicSavedPropertyDescriptor(string key, string name, Type valueType, Func<RelicModel, object?> get, Action<RelicModel, object?> set)
    {
        Key = key;
        Name = name;
        ValueType = valueType;
        GetValue = get;
        SetValue = set;
    }

    public string Key { get; }
    public string Name { get; }
    public Type ValueType { get; }
    internal Func<RelicModel, object?> GetValue { get; }
    internal Action<RelicModel, object?> SetValue { get; }
}

public static class RelicModificationStateService
{
    private const string PermanentPath = "loadout/services/relic_modifications/permanent.json";
    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<RelicSavedPropertyDescriptor>> DescriptorCache = new();
    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, RelicSavedPropertyDescriptor>> DescriptorMapCache = new();
    private static readonly MethodInfo? DisplayAmountChanged = AccessTools.Method(typeof(RelicModel), "InvokeDisplayAmountChanged");
    private static readonly MethodInfo? StatusChanged = AccessTools.Method(typeof(RelicModel), "InvokeStatusChanged");
    private static readonly MethodInfo? IconChanged = AccessTools.Method(typeof(RelicModel), "RelicIconChanged");
    private static PermanentSaveData _permanent = new();
    private static Dictionary<string, RelicModificationState> _hostOverlay = new(StringComparer.Ordinal);
    private static bool _hasHostOverlay;
    private static bool _loaded;
    private static bool _registered;
    private static bool _savePending;
    private static int _stateRevision;
    private static int _knownFeatureFlags;
    private static readonly ConditionalWeakTable<RelicModel, EffectiveStateCache> EffectiveStates = new();
    private static readonly ConditionalWeakTable<LocString, LocStringOwner> LocStringOwners = new();
    [ThreadStatic] private static Stack<(RelicModel Relic, string Kind)>? _locContext;

    public static event Action? StateChanged;
    public static event Action<ModelId>? PermanentRelicDisplayChanged;

    // These flags keep optional Harmony patches on a near-zero-cost path until
    // the corresponding relic-modifier feature is actually present. Flags are
    // sticky for the current profile session so preview/temporary instances
    // cannot accidentally lose their behavior due to lifetime bookkeeping.
    public static bool HasRarityOverrides => HasKnownFeature(RelicModificationFeature.Rarity);
    public static bool HasCustomTextOverrides => HasKnownFeature(RelicModificationFeature.CustomText);
    public static bool HasNeverMeltOverrides => HasKnownFeature(RelicModificationFeature.NeverMelt);
    public static bool HasNeverUsedOverrides => HasKnownFeature(RelicModificationFeature.NeverUsed);

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        RelicModificationInstanceState.Initialize();
        SaveManager.Instance.ProfileIdChanged += OnProfileChanged;
        EnsureLoaded();
        RelicModificationMultiplayerSyncService.Register();
    }

    public static void Unregister()
    {
        if (!_registered) return;
        FlushSave();
        SaveManager.Instance.ProfileIdChanged -= OnProfileChanged;
        RelicModificationMultiplayerSyncService.Unregister();
        _registered = false;
    }

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (Gate)
        {
            if (_loaded) return;
            _permanent = SaveUtility.LoadProfileJson(PermanentPath, new PermanentSaveData()).Value;
            _permanent.Relics ??= new Dictionary<string, RelicModificationState>(StringComparer.Ordinal);
            _permanent.Relics = new Dictionary<string, RelicModificationState>(_permanent.Relics, StringComparer.Ordinal);
            foreach (RelicModificationState state in _permanent.Relics.Values) state.Normalize();
            MarkFeaturePresence(_permanent.Relics.Values);
            _loaded = true;
        }
    }

    public static IReadOnlyList<RelicSavedPropertyDescriptor> GetSavedPropertyDescriptors(RelicModel relic)
        => DescriptorCache.GetOrAdd(relic.GetType(), BuildDescriptors);

    public static RelicModificationState GetTemporaryState(RelicModel relic) => RelicModificationInstanceState.Get(relic).State;

    public static RelicModificationState GetPermanentState(ModelId id)
    {
        EnsureLoaded();
        lock (Gate) return GetPermanentLocked(id).Clone();
    }

    public static int GetPermanentModificationCount()
    {
        EnsureLoaded();
        lock (Gate) return _permanent.Relics.Count;
    }

    public static int ResetAllPermanent()
    {
        EnsureLoaded();
        string[] changedKeys;
        lock (Gate)
        {
            changedKeys = _permanent.Relics.Keys.ToArray();
            if (changedKeys.Length == 0)
                return 0;
        }

        HashSet<string> changed = changedKeys.ToHashSet(StringComparer.Ordinal);
        RestorePermanentBaselines(changed);
        lock (Gate)
        {
            _permanent.Relics.Clear();
            QueueSave();
            Interlocked.Increment(ref _stateRevision);
        }

        ReapplyRelicsAfterPermanentReset(changed);
        NotifyAllMatchingRelicsUpdated(changed);
        RaisePermanentRelicDisplayChanged(changedKeys);
        StateChanged?.Invoke();
        if (IsMultiplayerHost())
            RelicModificationMultiplayerSyncService.BroadcastSnapshot();
        return changedKeys.Length;
    }

    public static RelicModificationState GetEffectiveState(RelicModel relic)
        => GetEffectiveStateReadOnly(relic).Clone();

    private static RelicModificationState GetEffectiveStateReadOnly(RelicModel relic)
    {
        EnsureLoaded();
        RelicModificationInstanceSnapshot attached = RelicModificationInstanceState.GetSnapshot(relic);
        int revision = Volatile.Read(ref _stateRevision);
        EffectiveStateCache cache = EffectiveStates.GetValue(relic, _ => new EffectiveStateCache());
        EffectiveStateEntry? current = Volatile.Read(ref cache.Entry);
        if (current is not null && current.Revision == revision && current.AttachedRevision == attached.Revision)
            return current.State;
        lock (Gate)
        {
            attached = RelicModificationInstanceState.GetSnapshot(relic);
            revision = Volatile.Read(ref _stateRevision);
            current = Volatile.Read(ref cache.Entry);
            if (current is not null && current.Revision == revision && current.AttachedRevision == attached.Revision)
                return current.State;
            RelicModificationState state = GetPermanentLocked(relic.Id).Clone();
            state.MergeFrom(attached.Attachment.State);
            state.Normalize();
            Volatile.Write(ref cache.Entry, new EffectiveStateEntry(revision, attached.Revision, state));
            return state;
        }
    }

    public static void ApplyOperation(LoadoutOwnedItem<RelicModel> item, RelicModificationOperation operation, RelicModificationState? requested)
    {
        EnsureLoaded();
        RelicModel relic = item.Model;
        RelicModificationState state = requested?.Clone() ?? new RelicModificationState();
        state.Normalize();
        MarkFeaturePresence(state);

        switch (operation)
        {
            case RelicModificationOperation.SaveTemporary:
            {
                RelicModificationAttachment attachment = RelicModificationInstanceState.Get(relic);
                if (attachment.Baseline.IsEmpty)
                    attachment.Baseline = CaptureCurrentState(relic);
                attachment.State = state;
                RelicModificationInstanceState.Set(relic, attachment);
                EffectiveStates.Remove(relic);
                ApplyEffectiveState(relic);
                break;
            }
            case RelicModificationOperation.ResetTemporary:
            case RelicModificationOperation.ResetTemporaryToBasic:
            {
                RelicModificationAttachment attachment = RelicModificationInstanceState.Get(relic);
                RestoreState(relic, attachment.Baseline);
                attachment.State = new RelicModificationState();
                lock (Gate)
                {
                    if (GetPermanentLocked(relic.Id).IsEmpty) RelicModificationInstanceState.Clear(relic);
                    else RelicModificationInstanceState.Set(relic, attachment);
                }
                EffectiveStates.Remove(relic);
                ApplyEffectiveState(relic);
                break;
            }
            case RelicModificationOperation.ApplyPermanent:
                EnsureBaseline(relic);
                RelicModificationAttachment permanentAttachment = RelicModificationInstanceState.Get(relic);
                permanentAttachment.State = new RelicModificationState();
                RelicModificationInstanceState.Set(relic, permanentAttachment);
                lock (Gate)
                {
                    if (_hasHostOverlay || IsMultiplayerClient()) { _hasHostOverlay = true; _hostOverlay[relic.Id.ToString()] = state; }
                    else
                    {
                        _permanent.Relics[relic.Id.ToString()] = state;
                        QueueSave();
                    }
                }
                Interlocked.Increment(ref _stateRevision);
                ApplyPermanentToLoadedRelics(relic.Id);
                break;
            case RelicModificationOperation.ResetPermanentToBasic:
                RestorePermanentBaselines(relic.Id);
                lock (Gate)
                {
                    if (_hasHostOverlay || IsMultiplayerClient()) { _hasHostOverlay = true; _hostOverlay.Remove(relic.Id.ToString()); }
                    else
                    {
                        _permanent.Relics.Remove(relic.Id.ToString());
                        QueueSave();
                    }
                }
                Interlocked.Increment(ref _stateRevision);
                ApplyPermanentToLoadedRelics(relic.Id);
                ClearUnusedBaselines(relic.Id);
                break;
        }

        if (operation is RelicModificationOperation.ApplyPermanent or RelicModificationOperation.ResetPermanentToBasic)
        {
            NotifyAllMatchingRelicsUpdated(relic.Id);
            RaisePermanentRelicDisplayChanged(relic.Id);
        }
        else
            LoadoutRunContentChangeService.NotifyRelicUpdated(item);
        StateChanged?.Invoke();
        if (operation is RelicModificationOperation.ApplyPermanent or RelicModificationOperation.ResetPermanentToBasic
            && IsMultiplayerHost())
            RelicModificationMultiplayerSyncService.BroadcastSnapshot();
    }

    public static void ApplyPermanentToRelic(RelicModel relic)
    {
        if (relic.IsCanonical) return;
        if (GetEffectiveStateReadOnly(relic).IsEmpty) return;
        EnsureBaseline(relic);
        ApplyEffectiveState(relic);
    }

    public static void ApplyLoadoutTemporaryState(RelicModel relic, RelicModificationState state)
    {
        if (relic.IsCanonical) return;

        RelicModificationState normalized = state.Clone();
        normalized.Normalize();
        MarkFeaturePresence(normalized);
        if (normalized.IsEmpty) return;

        RelicModificationAttachment attachment = RelicModificationInstanceState.Get(relic);
        if (attachment.Baseline.IsEmpty)
            attachment.Baseline = CaptureCurrentState(relic);
        attachment.State = normalized;
        RelicModificationInstanceState.Set(relic, attachment);
        EffectiveStates.Remove(relic);
        ApplyEffectiveState(relic);
    }

    public static void CarryStateToClone(RelicModel source, RelicModel clone)
    {
        if (clone.IsCanonical) return;
        // Canonical-to-mutable is also the first half of RelicModel.FromSerializable.
        // Avoid touching an unmodified clone before BaseLib imports its saved fields.
        if (source.IsCanonical && GetEffectiveStateReadOnly(source).IsEmpty) return;
        EffectiveStates.Remove(clone);
        ApplyPermanentToRelic(clone);
    }

    public static void ApplyDeserializedState(RelicModel relic)
    {
        if (relic.IsCanonical) return;
        // RelicModel.FromSerializable fills SavedProperties after ToMutable. Any cache
        // created by clone propagation must be discarded before reading attached state.
        RelicModificationInstanceState.Invalidate(relic);
        MarkFeaturePresence(RelicModificationInstanceState.GetStateReadOnly(relic));
        EffectiveStates.Remove(relic);
        ApplyPermanentToRelic(relic);
    }

    public static RelicModel CreatePreviewRelic(RelicModel source, RelicModificationState state)
    {
        MarkFeaturePresence(state);
        RelicModel preview = (RelicModel)source.ClonePreservingMutability();
        RelicModificationAttachment attachment = RelicModificationInstanceState.Get(preview);
        attachment.State = state.Clone();
        RelicModificationInstanceState.Set(preview, attachment);
        EffectiveStates.Remove(preview);
        ApplyEffectiveState(preview);
        return preview;
    }

    public static RelicModel GetEffectivePermanentRelicForDisplay(RelicModel relic)
    {
        EnsureLoaded();
        lock (Gate)
        {
            if (GetPermanentLocked(relic.Id).IsEmpty)
                return relic;
        }

        RelicModel canonical = relic.IsCanonical ? relic : relic.CanonicalInstance;
        return canonical.ToMutable();
    }

    public static bool ShouldNeverMelt(RelicModel relic) => GetEffectiveStateReadOnly(relic).NeverMelt == true;
    public static bool ShouldNeverUse(RelicModel relic) => GetEffectiveStateReadOnly(relic).NeverUsed == true;

    public static bool TryGetCustomDescription(RelicModel relic, out string description)
    {
        description = GetEffectiveStateReadOnly(relic).CustomDescription ?? string.Empty;
        return description.Length > 0;
    }

    public static bool TryGetRarity(RelicModel relic, out RelicRarity rarity)
    {
        RelicModificationState state = GetEffectiveStateReadOnly(relic);
        return Enum.TryParse(state.Rarity, true, out rarity);
    }

    public static void PushLocStringContext(RelicModel relic, string kind) => (_locContext ??= new Stack<(RelicModel, string)>()).Push((relic, kind));
    public static void PopLocStringContext()
    {
        if (_locContext?.Count > 0) _locContext.Pop();
    }

    public static void AssociateLocString(RelicModel relic, LocString locString, string kind)
    {
        LocStringOwner owner = LocStringOwners.GetValue(locString, _ => new LocStringOwner());
        owner.Relic = relic;
        owner.Kind = kind;
    }

    public static bool TryGetCustomRawLocString(LocString locString, out string text)
    {
        text = string.Empty;
        RelicModel relic;
        string kind;
        if (_locContext?.Count is > 0)
            (relic, kind) = _locContext.Peek();
        else if (LocStringOwners.TryGetValue(locString, out LocStringOwner? owner) && owner.Relic is not null)
            (relic, kind) = (owner.Relic, owner.Kind);
        else
            return false;
        RelicModificationState state = GetEffectiveStateReadOnly(relic);
        if (kind == "title" && state.CustomTitle is not null) text = state.CustomTitle;
        else if (kind == "flavor" && state.CustomFlavor is not null) text = state.CustomFlavor;
        else if (kind == "description" && state.CustomDescription is not null) text = state.CustomDescription;
        return text.Length > 0;
    }

    public static void RecordRuntimeCounterValue(RelicModel relic, string memberKey, int value)
    {
        RelicSavedPropertyDescriptor? descriptor = GetSavedPropertyDescriptors(relic)
            .FirstOrDefault(candidate => candidate.Key == memberKey
                                         || candidate.Name == memberKey
                                         || memberKey.EndsWith($":{candidate.Name}", StringComparison.Ordinal));
        RelicModificationAttachment attachment = RelicModificationInstanceState.Get(relic);
        if (attachment.Baseline.IsEmpty) attachment.Baseline = CaptureCurrentState(relic);
        attachment.State.CounterMember = memberKey;
        attachment.State.CounterValue = value;
        if (descriptor is not null)
            attachment.State.PrimitiveValues[descriptor.Key] = RelicPrimitiveValue.FromObject(value, descriptor.ValueType);
        RelicModificationInstanceState.Set(relic, attachment);
        EffectiveStates.Remove(relic);
    }

    public static void PrepareRuntimeCounterMutation(RelicModel relic)
    {
        EnsureBaseline(relic);
    }

    public static string ExportPermanentSnapshot()
    {
        EnsureLoaded();
        lock (Gate) return JsonSerializer.Serialize(_permanent.Relics);
    }

    public static void SetHostPermanentOverlay(string json)
    {
        Dictionary<string, RelicModificationState>? states = JsonSerializer.Deserialize<Dictionary<string, RelicModificationState>>(json);
        if (states is not null) MarkFeaturePresence(states.Values);
        string[] changedKeys;
        RestoreAllOverlayBaselines();
        lock (Gate)
        {
            changedKeys = _hostOverlay.Keys
                .Concat(states is null ? Enumerable.Empty<string>() : states.Keys)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            _hostOverlay = states is null ? new(StringComparer.Ordinal) : new(states, StringComparer.Ordinal);
            _hasHostOverlay = true;
            Interlocked.Increment(ref _stateRevision);
        }
        ApplyAllLoadedRelics();
        RaisePermanentRelicDisplayChanged(changedKeys);
    }

    public static void ClearHostPermanentOverlay()
    {
        string[] changedKeys;
        RestoreAllOverlayBaselines();
        lock (Gate)
        {
            changedKeys = _hostOverlay.Keys.ToArray();
            _hostOverlay.Clear();
            _hasHostOverlay = false;
            Interlocked.Increment(ref _stateRevision);
        }
        ApplyAllLoadedRelics();
        RaisePermanentRelicDisplayChanged(changedKeys);
    }

    public static void ImportHostPermanentSnapshot(string json, bool merge)
    {
        Dictionary<string, RelicModificationState>? states = JsonSerializer.Deserialize<Dictionary<string, RelicModificationState>>(json);
        if (states is null) return;
        MarkFeaturePresence(states.Values);
        lock (Gate)
        {
            if (!merge) _permanent.Relics.Clear();
            foreach ((string key, RelicModificationState state) in states)
            {
                if (!merge || !_permanent.Relics.ContainsKey(key)) _permanent.Relics[key] = state.Clone();
            }
            QueueSave();
            Interlocked.Increment(ref _stateRevision);
        }
        RaisePermanentRelicDisplayChanged(states.Keys);
    }

    public static void RefreshRelic(RelicModel relic)
    {
        try { DisplayAmountChanged?.Invoke(relic, null); } catch { }
        try { StatusChanged?.Invoke(relic, null); } catch { }
        try { IconChanged?.Invoke(relic, null); } catch { }
    }

    public static void RestoreFlashSubscriptionAfterUnmelt(RelicModel relic)
    {
        try
        {
            Player owner = relic.Owner;
            MethodInfo? handler = AccessTools.Method(typeof(Player), "OnRelicFlashed");
            FieldInfo? flashed = AccessTools.Field(typeof(RelicModel), "Flashed");
            if (handler is null || flashed is null) return;
            Delegate callback = Delegate.CreateDelegate(flashed.FieldType, owner, handler);
            Delegate? current = flashed.GetValue(relic) as Delegate;
            flashed.SetValue(relic, Delegate.Combine(Delegate.Remove(current, callback), callback));
        }
        catch { }
    }

    private static void ApplyEffectiveState(RelicModel relic)
    {
        if (relic.IsCanonical) return;
        RelicModificationState state = GetEffectiveState(relic);
        foreach ((string name, decimal value) in state.DynamicVars)
            if (relic.DynamicVars.TryGetValue(name, out var dynamicVar)) dynamicVar.BaseValue = value;

        if (state.IsWax.HasValue) TrySetProperty(relic, nameof(RelicModel.IsWax), state.IsWax.Value);
        bool wasMelted = relic.IsMelted;
        bool melted = state.NeverMelt == true ? false : state.IsMelted ?? relic.IsMelted;
        TrySetProperty(relic, nameof(RelicModel.IsMelted), melted);
        if ((wasMelted && !melted) || state.NeverMelt == true) RestoreFlashSubscriptionAfterUnmelt(relic);
        string? status = state.NeverUsed == true && string.Equals(state.Status, "Disabled", StringComparison.OrdinalIgnoreCase) ? "Normal" : state.Status;
        if ((state.NeverUsed == true || state.NeverMelt == true) && status is null && relic.Status == RelicStatus.Disabled)
            status = RelicStatus.Normal.ToString();
        if (status is not null && Enum.TryParse(typeof(RelicStatus), status, true, out object? parsedStatus))
            TrySetProperty(relic, nameof(RelicModel.Status), parsedStatus);

        IReadOnlyDictionary<string, RelicSavedPropertyDescriptor> descriptors = GetSavedPropertyDescriptorMap(relic);
        foreach ((string key, RelicPrimitiveValue primitive) in state.PrimitiveValues)
            if (descriptors.TryGetValue(key, out RelicSavedPropertyDescriptor? descriptor) && primitive.TryConvert(descriptor.ValueType, out object? value))
                TrySetDescriptor(relic, descriptor, value);
        if (state.CounterMember is not null && state.CounterValue.HasValue)
            TildeKeyStateService.ReconcileRelicCounterValue(relic, state.CounterMember, state.CounterValue.Value);
        RefreshRelic(relic);
    }

    private static RelicModificationState CaptureCurrentState(RelicModel relic)
    {
        RelicModificationState state = new()
        {
            Rarity = relic.Rarity.ToString(),
            IsWax = relic.IsWax,
            IsMelted = relic.IsMelted,
            Status = relic.Status.ToString()
        };
        foreach ((string key, var dynamicVar) in relic.DynamicVars) state.DynamicVars[key] = dynamicVar.BaseValue;
        foreach (RelicSavedPropertyDescriptor descriptor in GetSavedPropertyDescriptors(relic))
        {
            try { state.PrimitiveValues[descriptor.Key] = RelicPrimitiveValue.FromObject(descriptor.GetValue(relic), descriptor.ValueType); }
            catch { }
        }
        if (TildeKeyStateService.TryGetRelicCounterMember(relic, out string counterMember)
            && TildeKeyStateService.TryGetRelicCounterValue(relic, counterMember, out int counterValue))
        {
            state.CounterMember = counterMember;
            state.CounterValue = counterValue;
        }
        return state;
    }

    private static void RestoreState(RelicModel relic, RelicModificationState baseline)
    {
        foreach ((string name, decimal value) in baseline.DynamicVars)
            if (relic.DynamicVars.TryGetValue(name, out var dynamicVar)) dynamicVar.BaseValue = value;
        if (baseline.IsWax.HasValue) TrySetProperty(relic, nameof(RelicModel.IsWax), baseline.IsWax.Value);
        if (baseline.IsMelted.HasValue) TrySetProperty(relic, nameof(RelicModel.IsMelted), baseline.IsMelted.Value);
        if (baseline.Status is not null && Enum.TryParse(typeof(RelicStatus), baseline.Status, true, out object? status)) TrySetProperty(relic, nameof(RelicModel.Status), status);
        IReadOnlyDictionary<string, RelicSavedPropertyDescriptor> descriptors = GetSavedPropertyDescriptorMap(relic);
        foreach ((string key, RelicPrimitiveValue primitive) in baseline.PrimitiveValues)
            if (descriptors.TryGetValue(key, out RelicSavedPropertyDescriptor? descriptor) && primitive.TryConvert(descriptor.ValueType, out object? value)) TrySetDescriptor(relic, descriptor, value);
        if (baseline.CounterMember is not null && baseline.CounterValue.HasValue)
            TildeKeyStateService.ReconcileRelicCounterValue(relic, baseline.CounterMember, baseline.CounterValue.Value);
    }

    private static IReadOnlyList<RelicSavedPropertyDescriptor> BuildDescriptors(Type type)
    {
        List<RelicSavedPropertyDescriptor> result = [];
        HashSet<string> names = new(StringComparer.Ordinal);
        BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (PropertyInfo property in type.GetProperties(flags))
        {
            if (!HasSavedProperty(property) || property.GetIndexParameters().Length != 0 || property.GetMethod is null || property.SetMethod is null || !IsSupported(property.PropertyType)) continue;
            if (IsCoreMember(property.Name) || !names.Add(property.Name)) continue;
            string key = $"P:{property.DeclaringType?.FullName}:{property.Name}";
            result.Add(new RelicSavedPropertyDescriptor(key, property.Name, property.PropertyType, relic => property.GetValue(relic), (relic, value) => property.SetValue(relic, value)));
        }
        foreach (FieldInfo field in type.GetFields(flags))
        {
            if (!HasSavedProperty(field) || field.IsStatic || field.IsInitOnly || !IsSupported(field.FieldType)) continue;
            if (IsCoreMember(field.Name) || !names.Add(field.Name)) continue;
            string key = $"F:{field.DeclaringType?.FullName}:{field.Name}";
            result.Add(new RelicSavedPropertyDescriptor(key, field.Name, field.FieldType, relic => field.GetValue(relic), (relic, value) => field.SetValue(relic, value)));
        }
        return result.OrderBy(descriptor => descriptor.Name, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyDictionary<string, RelicSavedPropertyDescriptor> GetSavedPropertyDescriptorMap(RelicModel relic)
    {
        return DescriptorMapCache.GetOrAdd(
            relic.GetType(),
            _ => GetSavedPropertyDescriptors(relic).ToDictionary(descriptor => descriptor.Key, StringComparer.Ordinal));
    }

    private static bool HasSavedProperty(MemberInfo member) => member.GetCustomAttributes(true).Any(a => a.GetType().Name == "SavedPropertyAttribute");
    private static bool IsSupported(Type type)
    {
        Type actual = Nullable.GetUnderlyingType(type) ?? type;
        return actual == typeof(bool) || actual == typeof(string) || actual.IsEnum || actual == typeof(decimal) || actual == typeof(float) || actual == typeof(double)
               || actual == typeof(byte) || actual == typeof(sbyte) || actual == typeof(short) || actual == typeof(ushort) || actual == typeof(int) || actual == typeof(uint) || actual == typeof(long) || actual == typeof(ulong);
    }
    private static bool IsCoreMember(string name) => name is nameof(RelicModel.IsWax) or nameof(RelicModel.IsMelted) or nameof(RelicModel.Status);
    private static void TrySetProperty(RelicModel relic, string name, object? value)
    {
        try { AccessTools.Property(relic.GetType(), name)?.SetValue(relic, value); } catch (Exception e) { GD.PushWarning($"RelicModifier: could not set {name} on {relic.Id}. {e.Message}"); }
    }
    private static void TrySetDescriptor(RelicModel relic, RelicSavedPropertyDescriptor descriptor, object? value)
    {
        try { descriptor.SetValue(relic, value); } catch (Exception e) { GD.PushWarning($"RelicModifier: could not set {descriptor.Name} on {relic.Id}. {e.Message}"); }
    }

    private static RelicModificationState GetPermanentLocked(ModelId id)
    {
        string key = id.ToString();
        if (_hasHostOverlay && _hostOverlay.TryGetValue(key, out RelicModificationState? host)) return host;
        return _permanent.Relics.TryGetValue(key, out RelicModificationState? local) ? local : new RelicModificationState();
    }
    private static bool IsMultiplayerClient()
    {
        try { return RunManager.Instance.NetService.Type == NetGameType.Client; }
        catch { return false; }
    }
    private static bool IsMultiplayerHost()
    {
        try { return RunManager.Instance.NetService.Type == NetGameType.Host; }
        catch { return false; }
    }

    private static void ApplyPermanentToLoadedRelics(ModelId id)
    {
        if (!RunManager.Instance.IsInProgress || RunManager.Instance.DebugOnlyGetState() is not { } run) return;
        foreach (RelicModel relic in run.Players.SelectMany(player => player.Relics).Where(relic => relic.Id.Equals(id)))
        {
            if (GetEffectiveStateReadOnly(relic).IsEmpty) continue;
            EnsureBaseline(relic);
            ApplyEffectiveState(relic);
        }
    }
    private static void NotifyAllMatchingRelicsUpdated(ModelId id)
    {
        if (!RunManager.Instance.IsInProgress || RunManager.Instance.DebugOnlyGetState() is not { } run) return;
        List<LoadoutChangedRelic> changes = [];
        HashSet<ulong> owners = [];
        foreach (Player player in run.Players)
        {
            for (int index = 0; index < player.Relics.Count; index++)
            {
                RelicModel relic = player.Relics[index];
                if (!relic.Id.Equals(id)) continue;
                owners.Add(player.NetId);
                changes.Add(new LoadoutChangedRelic(player.NetId, index, relic.Id));
            }
        }
        if (changes.Count > 0)
            LoadoutRunContentChangeService.Notify(LoadoutRunContentKind.Relics, owners, LoadoutRunContentChangeMode.Update, changedRelics: changes);
    }

    private static void NotifyAllMatchingRelicsUpdated(IReadOnlySet<string> ids)
    {
        if (!RunManager.Instance.IsInProgress || RunManager.Instance.DebugOnlyGetState() is not { } run) return;
        List<LoadoutChangedRelic> changes = [];
        HashSet<ulong> owners = [];
        foreach (Player player in run.Players)
        {
            for (int index = 0; index < player.Relics.Count; index++)
            {
                RelicModel relic = player.Relics[index];
                if (!ids.Contains(relic.Id.ToString())) continue;
                owners.Add(player.NetId);
                changes.Add(new LoadoutChangedRelic(player.NetId, index, relic.Id));
            }
        }
        if (changes.Count > 0)
            LoadoutRunContentChangeService.Notify(LoadoutRunContentKind.Relics, owners, LoadoutRunContentChangeMode.Update, changedRelics: changes);
    }
    private static void EnsureBaseline(RelicModel relic)
    {
        RelicModificationAttachment attachment = RelicModificationInstanceState.Get(relic);
        if (!attachment.Baseline.IsEmpty) return;
        attachment.Baseline = CaptureCurrentState(relic);
        RelicModificationInstanceState.Set(relic, attachment);
    }
    private static void RestorePermanentBaselines(ModelId id)
    {
        if (!RunManager.Instance.IsInProgress || RunManager.Instance.DebugOnlyGetState() is not { } run) return;
        foreach (RelicModel relic in run.Players.SelectMany(player => player.Relics).Where(relic => relic.Id.Equals(id)))
        {
            RelicModificationAttachment attachment = RelicModificationInstanceState.Get(relic);
            RestoreState(relic, attachment.Baseline);
        }
    }

    private static void RestorePermanentBaselines(IReadOnlySet<string> ids)
    {
        if (!RunManager.Instance.IsInProgress || RunManager.Instance.DebugOnlyGetState() is not { } run) return;
        foreach (RelicModel relic in run.Players.SelectMany(player => player.Relics).Where(relic => ids.Contains(relic.Id.ToString())))
        {
            RelicModificationAttachment attachment = RelicModificationInstanceState.Get(relic);
            RestoreState(relic, attachment.Baseline);
        }
    }

    private static void ReapplyRelicsAfterPermanentReset(IReadOnlySet<string> ids)
    {
        if (!RunManager.Instance.IsInProgress || RunManager.Instance.DebugOnlyGetState() is not { } run) return;
        foreach (RelicModel relic in run.Players.SelectMany(player => player.Relics).Where(relic => ids.Contains(relic.Id.ToString())))
        {
            EffectiveStates.Remove(relic);
            if (GetEffectiveStateReadOnly(relic).IsEmpty)
                RelicModificationInstanceState.Clear(relic);
            else
                ApplyEffectiveState(relic);
        }
    }
    private static void RestoreAllOverlayBaselines()
    {
        string[] keys;
        lock (Gate) keys = _hostOverlay.Keys.ToArray();
        if (keys.Length == 0 || !RunManager.Instance.IsInProgress || RunManager.Instance.DebugOnlyGetState() is not { } run) return;
        foreach (RelicModel relic in run.Players.SelectMany(player => player.Relics).Where(relic => keys.Contains(relic.Id.ToString(), StringComparer.Ordinal)))
        {
            RelicModificationAttachment attachment = RelicModificationInstanceState.Get(relic);
            RestoreState(relic, attachment.Baseline);
        }
    }
    private static void ClearUnusedBaselines(ModelId id)
    {
        if (!RunManager.Instance.IsInProgress || RunManager.Instance.DebugOnlyGetState() is not { } run) return;
        foreach (RelicModel relic in run.Players.SelectMany(player => player.Relics).Where(relic => relic.Id.Equals(id)))
        {
            if (RelicModificationInstanceState.GetStateReadOnly(relic).IsEmpty)
                RelicModificationInstanceState.Clear(relic);
        }
    }
    private static void ApplyAllLoadedRelics()
    {
        if (!RunManager.Instance.IsInProgress || RunManager.Instance.DebugOnlyGetState() is not { } run) return;
        foreach (RelicModel relic in run.Players.SelectMany(player => player.Relics))
        {
            if (GetEffectiveStateReadOnly(relic).IsEmpty) continue;
            EnsureBaseline(relic);
            ApplyEffectiveState(relic);
        }
    }
    private static void OnProfileChanged(int _)
    {
        FlushSave();
        Volatile.Write(ref _knownFeatureFlags, 0);
        lock (Gate) { _loaded = false; _permanent = new PermanentSaveData(); _hostOverlay.Clear(); _hasHostOverlay = false; }
        EnsureLoaded();
    }
    private static void QueueSave()
    {
        _savePending = true;
        Callable.From(FlushSave).CallDeferred();
    }
    private static void FlushSave()
    {
        PermanentSaveData snapshot;
        lock (Gate)
        {
            if (!_savePending) return;
            _savePending = false;
            snapshot = _permanent.Clone();
        }
        SaveUtility.SaveProfileJson(PermanentPath, snapshot);
    }

    private static void RaisePermanentRelicDisplayChanged(ModelId relicId)
    {
        Action<ModelId>? handlers = PermanentRelicDisplayChanged;
        if (handlers is null)
            return;

        foreach (Action<ModelId> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(relicId);
            }
            catch (Exception exception)
            {
                GD.PushWarning($"RelicModifier: permanent-relic display handler failed. {exception.Message}");
            }
        }
    }

    private static void RaisePermanentRelicDisplayChanged(IEnumerable<string> relicKeys)
    {
        HashSet<string> keys = relicKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.Ordinal);
        if (keys.Count == 0)
            return;

        foreach (RelicModel relic in ModelDb.AllRelics)
        {
            if (keys.Contains(relic.Id.ToString()))
                RaisePermanentRelicDisplayChanged(relic.Id);
        }
    }

    private struct PermanentSaveData : ISerializable
    {
        public PermanentSaveData() { }
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
        [JsonPropertyName("relics")] public Dictionary<string, RelicModificationState> Relics { get; set; } = new(StringComparer.Ordinal);
        public readonly PermanentSaveData Clone() => new() { SchemaVersion = SchemaVersion, Relics = Relics.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal) };
        public readonly void GetObjectData(SerializationInfo info, StreamingContext context) { info.AddValue(nameof(SchemaVersion), SchemaVersion); info.AddValue(nameof(Relics), Relics); }
    }

    [Flags]
    private enum RelicModificationFeature
    {
        None = 0,
        Rarity = 1 << 0,
        CustomText = 1 << 1,
        NeverMelt = 1 << 2,
        NeverUsed = 1 << 3
    }

    private static bool HasKnownFeature(RelicModificationFeature feature)
        => (Volatile.Read(ref _knownFeatureFlags) & (int)feature) != 0;

    private static RelicModificationFeature GetFeatures(RelicModificationState state)
    {
        RelicModificationFeature features = RelicModificationFeature.None;
        if (!string.IsNullOrWhiteSpace(state.Rarity)) features |= RelicModificationFeature.Rarity;
        if (!string.IsNullOrWhiteSpace(state.CustomTitle)
            || !string.IsNullOrWhiteSpace(state.CustomDescription)
            || !string.IsNullOrWhiteSpace(state.CustomFlavor))
            features |= RelicModificationFeature.CustomText;
        if (state.NeverMelt == true) features |= RelicModificationFeature.NeverMelt;
        if (state.NeverUsed == true) features |= RelicModificationFeature.NeverUsed;
        return features;
    }

    private static void MarkFeaturePresence(RelicModificationState state)
        => MarkFeaturePresence(GetFeatures(state));

    private static void MarkFeaturePresence(IEnumerable<RelicModificationState> states)
    {
        RelicModificationFeature features = RelicModificationFeature.None;
        foreach (RelicModificationState state in states)
            features |= GetFeatures(state);
        MarkFeaturePresence(features);
    }

    private static void MarkFeaturePresence(RelicModificationFeature features)
    {
        int added = (int)features;
        if (added == 0) return;

        int current;
        int updated;
        do
        {
            current = Volatile.Read(ref _knownFeatureFlags);
            updated = current | added;
            if (updated == current) return;
        }
        while (Interlocked.CompareExchange(ref _knownFeatureFlags, updated, current) != current);
    }

    private sealed class EffectiveStateCache { public EffectiveStateEntry? Entry; }
    private sealed record EffectiveStateEntry(int Revision, int AttachedRevision, RelicModificationState State);
    private sealed class LocStringOwner { public RelicModel? Relic; public string Kind = string.Empty; }
}
