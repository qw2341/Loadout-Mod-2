#nullable enable

namespace Loadout.Services.Compatibility;

using System;
using System.Collections.Generic;
using BaseLib.Utils.ModInterop;
using MegaCrit.Sts2.Core.Models;

/// <summary>
/// Optional ARAM: Mayhem integration. BaseLib replaces these stubs only when
/// the HextechRunes mod is loaded, so Loadout never takes a hard dependency on it.
/// </summary>
[ModInterop("HextechRunes", "HextechRunes.HextechCatalog")]
public static class HextechRunesInterop
{
    public static bool IsHextechRelic(RelicModel relic)
    {
        return false;
    }

    public static string? GetPlayerRunePoolKey(RelicModel relic)
    {
        return null;
    }

    public static bool IsHextechForgeRelic(RelicModel relic)
    {
        return false;
    }

    public static bool IsHextechShopRelic(RelicModel relic)
    {
        return false;
    }

    public static int GetPlayerRuneRaritySortOrder(Type runeType)
    {
        return int.MaxValue;
    }

    public static IReadOnlyList<Type> GetAllRuneTypes()
    {
        return Array.Empty<Type>();
    }

    public static IReadOnlyList<Type> GetAllForgeTypes()
    {
        return Array.Empty<Type>();
    }
}

/// <summary>
/// Optional access to ARAM's own relic-collection header formatter.
/// </summary>
[ModInterop("HextechRunes", "HextechRunes.CollectionHooks")]
public static class HextechRunesCollectionInterop
{
    public static string FormatLikeStarterHeader(
        string starterHeader,
        string zhHeader,
        string zhBody,
        string enHeader,
        string enBody,
        string logKey)
    {
        return $"{enHeader} {enBody}";
    }
}
