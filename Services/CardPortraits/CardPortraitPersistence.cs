#nullable enable

namespace Loadout.Services.CardPortraits;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using Loadout.UI.ImageEditing;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;

internal static class CardPortraitPersistence
{
    public const string FieldName = "loadout_card_portrait_ref_v2";
    private const string LegacyFieldName = "loadout_card_portrait_ref_v1";

    [ThreadStatic]
    private static int _checksumDepth;

    public static IDisposable BeginChecksumSerialization()
    {
        _checksumDepth++;
        return new ChecksumScope();
    }

    public static void Export(CardModel card, SerializableCard save)
    {
        if (_checksumDepth > 0)
            return;

        bool hasReference = CardPortraitFields.TryGet(card, out CardPortraitReference? reference);
        if (save.Props?.strings is { } existingStrings)
        {
            existingStrings.RemoveAll(entry => IsPortraitField(entry.name));
            if (existingStrings.Count == 0)
                save.Props.strings = null;
            if (!hasReference && IsEmpty(save.Props))
                save.Props = null;
        }
        if (!hasReference)
            return;

        save.Props ??= new SavedProperties();
        save.Props.strings ??= [];
        save.Props.strings.Add(new SavedProperties.SavedProperty<string>(
            FieldName,
            $"2:{reference!.RunStartTime.ToString(CultureInfo.InvariantCulture)}:{reference.PortraitId}:{reference.RelativeFile}"));
    }

    public static bool TryRead(SerializableCard save, out CardPortraitReference reference)
    {
        reference = null!;
        if (save.Props?.strings is not { } strings)
            return false;

        foreach (SavedProperties.SavedProperty<string> entry in strings)
        {
            if (!IsPortraitField(entry.name))
                continue;

            string[] parts = entry.value.Split(':', 4, StringSplitOptions.None);
            if (parts.Length == 4
                && parts[0] == "2"
                && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long runStartTimeV2)
                && Guid.TryParseExact(parts[2], "N", out _)
                && IsSafeRelativeFile(parts[3]))
            {
                reference = new CardPortraitReference(parts[2], runStartTimeV2, parts[3]);
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

        bool containsPortraitField = false;
        foreach (SavedProperties.SavedProperty<string> entry in strings)
        {
            if (!IsPortraitField(entry.name))
                continue;
            containsPortraitField = true;
            break;
        }
        if (!containsPortraitField)
            return default;

        List<SavedProperties.SavedProperty<string>> filtered = strings
            .FindAll(entry => !IsPortraitField(entry.name));

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

    private static bool IsPortraitField(string name) =>
        string.Equals(name, FieldName, StringComparison.Ordinal)
        || string.Equals(name, LegacyFieldName, StringComparison.Ordinal);

    private static bool IsSafeRelativeFile(string file)
    {
        if (string.IsNullOrWhiteSpace(file) || Path.IsPathFullyQualified(file))
            return false;

        string normalized = file.Replace('\\', '/');
        return !normalized.StartsWith("/", StringComparison.Ordinal)
            && !normalized.Contains("://", StringComparison.Ordinal)
            && !normalized.Split('/').Any(part => part is "" or "." or "..")
            && (normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith(ImageAnimationPackage.Extension, StringComparison.OrdinalIgnoreCase));
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
        CardPortraitDynamicPatches.EnsureTemporaryInstalled();
    }
}
