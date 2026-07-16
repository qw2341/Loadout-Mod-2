#nullable enable

namespace Loadout.Patches.Cards.CardModification;

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Loadout.Services.CardModification;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

internal static class CardModificationPersistence
{
    public const string V4FieldName = "loadout_card_modification_delta_v4";
    public const string V3FieldName = "loadout_card_modification_v3";
    public const string LegacyV2FieldName = "loadout_card_modification_state_v2";

    [ThreadStatic]
    private static int _checksumDepth;

    internal static void RegisterPacketPropertyName()
    {
        Type? patchType = AccessTools.TypeByName("BaseLib.Patches.Utils.SavedSpireFieldPatch");
        if (patchType is null) return;
        AccessTools.Method(patchType, "InjectNameIntoBaseGameCache")?.Invoke(null, [V4FieldName]);
    }

    public static IDisposable BeginChecksumSerialization()
    {
        _checksumDepth++;
        return new ChecksumScope();
    }

    public static void Export(CardModel card, SerializableCard save)
    {
        if (!CardModificationFields.TryGet(card, out CardModificationCardData data))
            return;

        save.Props ??= new SavedProperties();
        save.Props.strings ??= [];
        string value = _checksumDepth > 0 ? $"h:{data.Fingerprint}" : data.Serialized;
        save.Props.strings.Add(new SavedProperties.SavedProperty<string>(V4FieldName, value));
    }

    public static CardModificationLoadData? Read(SerializableCard save)
    {
        string? v4Payload = FindString(save.Props, V4FieldName);
        if (!string.IsNullOrWhiteSpace(v4Payload))
        {
            if (v4Payload.StartsWith("h:", StringComparison.Ordinal)) return null;
            if (CardModificationCodec.TryDeserializeDelta(v4Payload, out CardModificationDelta delta)
                && !delta.IsEmpty)
            {
                return new CardModificationLoadData(delta, null);
            }
            return null;
        }

        string? v3Payload = FindString(save.Props, V3FieldName);
        string? payload = v3Payload ?? FindString(save.Props, LegacyV2FieldName);
        if (string.IsNullOrWhiteSpace(payload)
            || !CardModificationCodec.TryDeserialize(payload, out CardModificationSpec spec)
            || spec.IsEmpty)
        {
            return null;
        }

        return new CardModificationLoadData(null, spec);
    }

    public static void Import(
        SerializableCard save,
        CardModel card,
        CardModificationLoadData? loaded)
    {
        if (loaded is null) return;

        CardModificationDelta delta = loaded.Value.Delta
                                      ?? CardModificationRuntime.CreateTemporaryDelta(card, loaded.Value.LegacyAbsolute);
        CardModificationFields.SetDelta(card, delta);
        CardModificationRuntime.ApplyDeltaToCard(card, delta);
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

public readonly record struct CardModificationLoadData(
    CardModificationDelta? Delta,
    CardModificationSpec? LegacyAbsolute);

/// <summary>
/// Registers one packet property name after BaseLib's sorted registration pass,
/// without creating a SavedSpireField exporter or any per-card state.
/// </summary>
[HarmonyPatch]
internal static class CardModificationSavedFieldRegistrationPatch
{
    public static System.Reflection.MethodBase TargetMethod()
    {
        Type type = AccessTools.TypeByName("BaseLib.Patches.Utils.SavedSpireFieldPatch")
                    ?? throw new TypeLoadException("BaseLib.Patches.Utils.SavedSpireFieldPatch");
        return AccessTools.Method(type, "AddFieldsSorted")
               ?? throw new MissingMethodException(type.FullName, "AddFieldsSorted");
    }

    [HarmonyPostfix]
    public static void Postfix() => CardModificationPersistence.RegisterPacketPropertyName();
}
