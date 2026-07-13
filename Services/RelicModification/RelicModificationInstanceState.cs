#nullable enable

namespace Loadout.Services.RelicModification;

using System;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
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
    private static readonly ConditionalWeakTable<RelicModel, ParsedAttachmentCache> ParsedAttachments = new();

    public static void Initialize() => _ = SerializedState.Name;

    public static RelicModificationAttachment Get(RelicModel relic)
    {
        return GetSnapshot(relic).Attachment.Clone();
    }

    public static RelicModificationState GetStateReadOnly(RelicModel relic)
    {
        return GetSnapshot(relic).Attachment.State;
    }

    public static RelicModificationInstanceSnapshot GetSnapshot(RelicModel relic)
    {
        ParsedAttachmentCache cache = ParsedAttachments.GetValue(relic, _ => new ParsedAttachmentCache());
        ParsedAttachmentEntry entry = GetOrInitialize(relic, cache);
        return new RelicModificationInstanceSnapshot(entry.Attachment, entry.Revision);
    }

    public static bool Set(RelicModel relic, RelicModificationAttachment attachment)
    {
        RelicModificationAttachment normalized = attachment.Clone();
        normalized.State.Normalize();
        normalized.Baseline.Normalize();
        string? serialized = normalized.State.IsEmpty && normalized.Baseline.IsEmpty
            ? null
            : JsonSerializer.Serialize(normalized, JsonOptions);

        ParsedAttachmentCache cache = ParsedAttachments.GetValue(relic, _ => new ParsedAttachmentCache());
        lock (cache)
        {
            ParsedAttachmentEntry current = GetOrInitializeLocked(relic, cache);
            if (string.Equals(current.Serialized, serialized, StringComparison.Ordinal))
                return false;

            SerializedState.Set(relic, serialized);
            Volatile.Write(
                ref cache.Entry,
                new ParsedAttachmentEntry(
                    unchecked(current.Revision + 1),
                    serialized,
                    normalized));
            return true;
        }
    }

    public static bool Clear(RelicModel relic)
    {
        return Set(relic, new RelicModificationAttachment());
    }

    /// <summary>
    /// Drops only the parsed runtime view. BaseLib remains the persistence source of
    /// truth and will be read again on the next access. This is required after the
    /// game's save-property importer fills a freshly deserialized relic.
    /// </summary>
    public static void Invalidate(RelicModel relic)
    {
        ParsedAttachments.Remove(relic);
    }

    private static SavedSpireField<RelicModel, string> CreateField()
    {
        SavedSpireField<RelicModel, string> field = new(() => (string?)null, FieldName);
        field.CopyOnClone((source, destination, value) =>
        {
            string? serialized = NormalizeSerialized(value);
            ParsedAttachmentCache sourceCache = ParsedAttachments.GetValue(source, _ => new ParsedAttachmentCache());
            ParsedAttachmentEntry sourceEntry = GetOrInitialize(sourceCache, serialized);
            if (!string.Equals(serialized, sourceEntry.Serialized, StringComparison.Ordinal))
                field.Set(source, sourceEntry.Serialized);
            if (sourceEntry.Serialized is not null)
                field.Set(destination, sourceEntry.Serialized);

            ParsedAttachmentCache destinationCache = ParsedAttachments.GetValue(destination, _ => new ParsedAttachmentCache());
            Volatile.Write(
                ref destinationCache.Entry,
                new ParsedAttachmentEntry(
                    sourceEntry.Revision,
                    sourceEntry.Serialized,
                    sourceEntry.Attachment));
        });
        return field;
    }

    private static ParsedAttachmentEntry GetOrInitialize(RelicModel relic, ParsedAttachmentCache cache)
    {
        ParsedAttachmentEntry? entry = Volatile.Read(ref cache.Entry);
        if (entry is not null)
            return entry;

        lock (cache)
            return GetOrInitializeLocked(relic, cache);
    }

    private static ParsedAttachmentEntry GetOrInitialize(ParsedAttachmentCache cache, string? serialized)
    {
        ParsedAttachmentEntry? entry = Volatile.Read(ref cache.Entry);
        if (entry is not null)
            return entry;

        lock (cache)
        {
            entry = Volatile.Read(ref cache.Entry);
            if (entry is not null)
                return entry;

            entry = CreateInitialEntry(serialized);
            Volatile.Write(ref cache.Entry, entry);
            return entry;
        }
    }

    private static ParsedAttachmentEntry GetOrInitializeLocked(RelicModel relic, ParsedAttachmentCache cache)
    {
        ParsedAttachmentEntry? entry = Volatile.Read(ref cache.Entry);
        if (entry is not null)
            return entry;

        string? serialized = NormalizeSerialized(SerializedState.Get(relic));
        entry = CreateInitialEntry(serialized);
        if (!string.Equals(serialized, entry.Serialized, StringComparison.Ordinal))
            SerializedState.Set(relic, entry.Serialized);
        Volatile.Write(ref cache.Entry, entry);
        return entry;
    }

    private static ParsedAttachmentEntry CreateInitialEntry(string? serialized)
    {
        RelicModificationAttachment attachment = Deserialize(serialized);
        string? compactSerialized = attachment.State.IsEmpty && attachment.Baseline.IsEmpty
            ? null
            : JsonSerializer.Serialize(attachment, JsonOptions);
        return new ParsedAttachmentEntry(0, compactSerialized, attachment);
    }

    private static string? NormalizeSerialized(string? serialized)
    {
        return string.IsNullOrWhiteSpace(serialized) ? null : serialized;
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

    private sealed class ParsedAttachmentCache
    {
        public ParsedAttachmentEntry? Entry;
    }

    private sealed record ParsedAttachmentEntry(
        int Revision,
        string? Serialized,
        RelicModificationAttachment Attachment);
}

internal readonly record struct RelicModificationInstanceSnapshot(
    RelicModificationAttachment Attachment,
    int Revision);
