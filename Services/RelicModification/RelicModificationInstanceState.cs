#nullable enable

namespace Loadout.Services.RelicModification;

using System;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using BaseLib.Utils;
using Godot;
using MegaCrit.Sts2.Core.Models;

internal sealed class RelicModificationAttachment
{
    [JsonPropertyName("s")]
    public RelicModificationState State { get; set; } = new();

    [JsonPropertyName("b")]
    public RelicModificationState Baseline { get; set; } = new();

    public RelicModificationAttachment Clone() => new()
    {
        State = State.Clone(),
        Baseline = Baseline.Clone()
    };
}

internal static class RelicModificationInstanceState
{
    private const string FieldName = "loadout_relic_modification_state_v1";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly SavedSpireField<RelicModel, string> SerializedState = CreateField();
    private static readonly ConditionalWeakTable<RelicModel, Cache> Caches = new();

    public static void Initialize() => _ = SerializedState.Name;

    public static RelicModificationAttachment Get(RelicModel relic)
    {
        Cache cache = Caches.GetValue(relic, _ => new Cache());
        lock (cache)
        {
            cache.Value ??= Deserialize(SerializedState.Get(relic));
            return cache.Value.Clone();
        }
    }

    public static RelicModificationState GetStateReadOnly(RelicModel relic)
    {
        Cache cache = Caches.GetValue(relic, _ => new Cache());
        lock (cache)
        {
            cache.Value ??= Deserialize(SerializedState.Get(relic));
            return cache.Value.State;
        }
    }

    public static bool Set(RelicModel relic, RelicModificationAttachment attachment)
    {
        RelicModificationAttachment normalized = attachment.Clone();
        normalized.State.Normalize();
        normalized.Baseline.Normalize();
        string? serialized = normalized.State.IsEmpty && normalized.Baseline.IsEmpty
            ? null
            : JsonSerializer.Serialize(normalized, JsonOptions);

        Cache cache = Caches.GetValue(relic, _ => new Cache());
        lock (cache)
        {
            string? previous = SerializedState.Get(relic);
            if (string.Equals(previous, serialized, StringComparison.Ordinal))
                return false;

            SerializedState.Set(relic, serialized);
            cache.Value = normalized;
            return true;
        }
    }

    public static bool Clear(RelicModel relic)
    {
        return Set(relic, new RelicModificationAttachment());
    }

    private static SavedSpireField<RelicModel, string> CreateField()
    {
        SavedSpireField<RelicModel, string> field = new(() => (string?)null, FieldName);
        field.CopyOnClone((source, destination, value) =>
        {
            if (!string.IsNullOrWhiteSpace(value))
                field.Set(destination, value);

            Cache sourceCache = Caches.GetValue(source, _ => new Cache());
            Cache destinationCache = Caches.GetValue(destination, _ => new Cache());
            lock (sourceCache)
            lock (destinationCache)
            {
                sourceCache.Value ??= Deserialize(value);
                destinationCache.Value = sourceCache.Value.Clone();
            }
        });
        return field;
    }

    private static RelicModificationAttachment Deserialize(string? serialized)
    {
        if (string.IsNullOrWhiteSpace(serialized))
            return new RelicModificationAttachment();

        try
        {
            RelicModificationAttachment result = JsonSerializer.Deserialize<RelicModificationAttachment>(serialized, JsonOptions)
                                                ?? new RelicModificationAttachment();
            result.State.Normalize();
            result.Baseline.Normalize();
            return result;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"RelicModifier: ignored invalid attached state. {exception.Message}");
            return new RelicModificationAttachment();
        }
    }

    private sealed class Cache
    {
        public RelicModificationAttachment? Value;
    }
}
