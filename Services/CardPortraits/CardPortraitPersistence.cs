#nullable enable

namespace Loadout.Services.CardPortraits;

using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;

internal static class CardPortraitPersistence
{
    public const string FieldName = "loadout_card_portrait_ref_v1";

    [ThreadStatic]
    private static int _checksumDepth;

    public static IDisposable BeginChecksumSerialization()
    {
        _checksumDepth++;
        return new ChecksumScope();
    }

    public static void Export(CardModel card, SerializableCard save)
    {
        if (_checksumDepth > 0 || !CardPortraitFields.TryGet(card, out CardPortraitReference? reference))
            return;

        save.Props ??= new SavedProperties();
        save.Props.strings ??= [];
        save.Props.strings.RemoveAll(entry => string.Equals(entry.name, FieldName, StringComparison.Ordinal));
        save.Props.strings.Add(new SavedProperties.SavedProperty<string>(
            FieldName,
            $"1:{reference.ProfileId.ToString(CultureInfo.InvariantCulture)}:{reference.RunStartTime.ToString(CultureInfo.InvariantCulture)}:{reference.PortraitId}"));
    }

    public static bool TryRead(SerializableCard save, out CardPortraitReference reference)
    {
        reference = null!;
        if (save.Props?.strings is not { } strings)
            return false;

        foreach (SavedProperties.SavedProperty<string> entry in strings)
        {
            if (!string.Equals(entry.name, FieldName, StringComparison.Ordinal))
                continue;

            string[] parts = entry.value.Split(':', 4, StringSplitOptions.None);
            if (parts.Length == 4
                && parts[0] == "1"
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int profileId)
                && long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long runStartTime)
                && Guid.TryParseExact(parts[3], "N", out _))
            {
                reference = new CardPortraitReference(parts[3], runStartTime, profileId);
                return true;
            }
            return false;
        }
        return false;
    }

    public static CardPortraitPacketState RemoveForPacket(SerializableCard card)
    {
        SavedProperties? props = card.Props;
        List<SavedProperties.SavedProperty<string>>? strings = props?.strings;
        if (strings is null)
            return default;

        List<SavedProperties.SavedProperty<string>> filtered = strings
            .FindAll(entry => !string.Equals(entry.name, FieldName, StringComparison.Ordinal));
        if (filtered.Count == strings.Count)
            return default;

        props!.strings = filtered.Count == 0 ? null : filtered;

        bool propsWereCleared = IsEmpty(props!);
        if (propsWereCleared)
            card.Props = null;

        return new CardPortraitPacketState(props, strings, propsWereCleared, true);
    }

    public static void RestoreAfterPacket(SerializableCard card, CardPortraitPacketState state)
    {
        if (!state.Removed || state.Props is null || state.OriginalStrings is null)
            return;

        if (state.PropsWereCleared)
            card.Props = state.Props;
        state.Props.strings = state.OriginalStrings;
    }

    private static bool IsEmpty(SavedProperties props) =>
        props.ints is not { Count: > 0 }
        && props.bools is not { Count: > 0 }
        && props.strings is not { Count: > 0 }
        && props.intArrays is not { Count: > 0 }
        && props.modelIds is not { Count: > 0 }
        && props.cards is not { Count: > 0 }
        && props.cardArrays is not { Count: > 0 };

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

internal readonly record struct CardPortraitPacketState(
    SavedProperties? Props,
    List<SavedProperties.SavedProperty<string>>? OriginalStrings,
    bool PropsWereCleared,
    bool Removed);

[HarmonyPatch(typeof(CardModel), nameof(CardModel.FromSerializable), typeof(SerializableCard))]
internal static class CardPortraitFromSerializablePatch
{
    [HarmonyPostfix]
    public static void Postfix(SerializableCard save, CardModel __result)
    {
        if (!CardPortraitPersistence.TryRead(save, out CardPortraitReference reference))
            return;

        CardPortraitFields.Set(__result, reference);
        CardPortraitDynamicPatches.EnsureInstalled();
    }
}
