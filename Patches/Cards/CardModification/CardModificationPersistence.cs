#nullable enable

namespace Loadout.Patches.Cards.CardModification;

using System;
using System.Collections.Generic;
using System.Linq;
using BaseLib.Utils;
using Loadout.Services.CardModification;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;

internal static class CardModificationPersistence
{
    public const string V3FieldName = "loadout_card_modification_v3";
    public const string LegacyV2FieldName = "loadout_card_modification_state_v2";

    [ThreadStatic]
    private static int _checksumDepth;

    // Register the custom property name with BaseLib/game packet serialization.
    // Values live in CardModificationFields and are exported manually so checksum
    // snapshots can carry only the fixed-size fingerprint.
    private static readonly SavedSpireField<CardModel, string> V3Registration =
        new(() => (string?)null, V3FieldName);

    public static void Initialize()
    {
        _ = V3Registration.Name;
    }

    public static IDisposable BeginChecksumSerialization()
    {
        _checksumDepth++;
        return new ChecksumScope();
    }

    public static void Export(CardModel card, SerializableCard save)
    {
        RemoveOwnedFields(save.Props);
        if (!CardModificationFields.TryGet(card, out CardModificationCardData data))
            return;

        save.Props ??= new SavedProperties();
        save.Props.strings ??= [];
        string value = _checksumDepth > 0 ? data.Fingerprint : data.Serialized;
        save.Props.strings.Add(new SavedProperties.SavedProperty<string>(V3FieldName, value));
    }

    public static CardModificationSpec? ReadSpec(SerializableCard save)
    {
        string? v3Payload = FindString(save.Props, V3FieldName);
        string? payload = v3Payload ?? FindString(save.Props, LegacyV2FieldName);
        if (string.IsNullOrWhiteSpace(payload)
            || !CardModificationCodec.TryDeserialize(payload, out CardModificationSpec spec)
            || spec.IsEmpty)
        {
            return null;
        }

        return spec;
    }

    public static void Import(
        SerializableCard save,
        CardModel card,
        CardModificationSpec? preloadedSpec)
    {
        string? v3Payload = FindString(save.Props, V3FieldName);
        // BaseLib registration exists only to teach packet serialization these names;
        // the compact CardModificationCardData holder remains the sole runtime state.
        if (v3Payload is not null)
            V3Registration.Set(card, null);
        if (preloadedSpec is null)
            return;

        CardModificationFields.Set(card, preloadedSpec);
        // FromSerializable has already replayed native upgrades. Applying once here
        // restores the exact per-copy result saved after those upgrades.
        CardModificationRuntime.ApplySpecToCard(card, preloadedSpec);
    }

    private static string? FindString(SavedProperties? props, string name)
    {
        if (props?.strings is null)
            return null;

        foreach (SavedProperties.SavedProperty<string> entry in props.strings)
        {
            if (string.Equals(entry.name, name, StringComparison.Ordinal))
                return entry.value;
        }
        return null;
    }

    private static void RemoveOwnedFields(SavedProperties? props)
    {
        if (props?.strings is null)
            return;

        props.strings.RemoveAll(entry =>
            string.Equals(entry.name, V3FieldName, StringComparison.Ordinal)
            || string.Equals(entry.name, LegacyV2FieldName, StringComparison.Ordinal));
        if (props.strings.Count == 0)
            props.strings = null;
    }

    private sealed class ChecksumScope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _checksumDepth = Math.Max(0, _checksumDepth - 1);
        }
    }
}
