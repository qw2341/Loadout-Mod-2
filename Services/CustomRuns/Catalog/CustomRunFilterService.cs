#nullable enable

namespace Loadout.Services.CustomRuns.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using Loadout.Services.CustomRuns.Models;

public static class CustomRunFilterService
{
    public static IReadOnlyList<CustomRunCatalogEntry> Resolve(SelectionPoolDefinition pool)
    {
        HashSet<string> included = pool.IncludedModelIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> excluded = pool.ExcludedModelIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> allowedMods = pool.AllowedModIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> excludedMods = pool.ExcludedModIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> categories = pool.Categories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> types = pool.Types.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return CustomRunCatalogService.GetCatalog(pool.Kind)
            .Where(entry => included.Count == 0 || MatchesId(entry, included))
            .Where(entry => !MatchesId(entry, excluded))
            .Where(entry => allowedMods.Count == 0 || allowedMods.Contains(entry.ModId))
            .Where(entry => !excludedMods.Contains(entry.ModId))
            .Where(entry => categories.Count == 0 || entry.Categories.Overlaps(categories))
            .Where(entry => types.Count == 0 || entry.Types.Overlaps(types))
            .ToList();
    }

    private static bool MatchesId(CustomRunCatalogEntry entry, IReadOnlySet<string> ids)
    {
        return ids.Contains(entry.ModelId) || ids.Contains(entry.Model.Id.Entry);
    }
}
