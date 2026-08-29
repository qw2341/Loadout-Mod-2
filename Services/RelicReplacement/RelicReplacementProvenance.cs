#nullable enable

namespace Loadout.Services.RelicReplacement;

using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Models;

internal enum RelicReplacementSource
{
    CustomRun,
    OneRelic
}

internal static class RelicReplacementProvenance
{
    private static readonly ConditionalWeakTable<RelicModel, Marker> CustomRunOccurrences = new();
    private static readonly ConditionalWeakTable<RelicModel, Marker> OneRelicOccurrences = new();

    internal static RelicModel CreateOccurrence(
        RelicReplacementSource source,
        RelicModel destination,
        RelicModel? original = null)
    {
        RelicModel occurrence = destination.CanonicalInstance.ToMutable();
        ConditionalWeakTable<RelicModel, Marker> table = GetTable(source);
        table.Remove(occurrence);
        table.Add(occurrence, new Marker(original));
        return occurrence;
    }

    internal static bool TryReuseOccurrence(
        RelicReplacementSource source,
        RelicModel relic,
        out RelicModel occurrence)
    {
        if (GetTable(source).TryGetValue(relic, out _))
        {
            occurrence = relic;
            return true;
        }

        occurrence = null!;
        return false;
    }

    internal static bool IsForced(RelicModel relic)
        => CustomRunOccurrences.TryGetValue(relic, out _)
           || OneRelicOccurrences.TryGetValue(relic, out _);

    internal static bool IsForced(RelicReplacementSource source, RelicModel relic)
        => GetTable(source).TryGetValue(relic, out _);

    internal static bool TryConsumeAuthorization(RelicModel relic)
    {
        if (OneRelicOccurrences.Remove(relic))
            return true;
        return CustomRunOccurrences.Remove(relic);
    }

    internal static bool TryGetOriginal(
        RelicReplacementSource source,
        RelicModel relic,
        out RelicModel original)
    {
        if (GetTable(source).TryGetValue(relic, out Marker? marker)
            && marker.Original is { } value)
        {
            original = value;
            return true;
        }

        original = null!;
        return false;
    }

    internal static void Clear(RelicReplacementSource source) => GetTable(source).Clear();

    private static ConditionalWeakTable<RelicModel, Marker> GetTable(RelicReplacementSource source)
        => source == RelicReplacementSource.OneRelic
            ? OneRelicOccurrences
            : CustomRunOccurrences;

    private sealed record Marker(RelicModel? Original);
}
