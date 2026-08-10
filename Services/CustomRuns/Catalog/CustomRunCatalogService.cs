#nullable enable

namespace Loadout.Services.CustomRuns.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using Loadout.PanelItems;
using Loadout.Services.CustomRuns.Models;
using MegaCrit.Sts2.Core.Models;

public sealed record CustomRunCatalogEntry(
    SelectionModelKind Kind,
    string ModelId,
    AbstractModel Model,
    string ModId,
    IReadOnlySet<string> Categories,
    IReadOnlySet<string> Types);

public static class CustomRunCatalogService
{
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<SelectionModelKind, IReadOnlyList<CustomRunCatalogEntry>> Catalogs = [];
    private static readonly Dictionary<SelectionModelKind, IReadOnlyDictionary<string, CustomRunCatalogEntry>> EntriesById = [];

    public static IReadOnlyList<CustomRunCatalogEntry> GetCatalog(SelectionModelKind kind)
    {
        lock (SyncRoot)
        {
            EnsureBuilt(kind);
            return Catalogs.GetValueOrDefault(kind) ?? [];
        }
    }

    public static bool TryResolve(SelectionModelKind kind, string modelId, out CustomRunCatalogEntry entry)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            entry = null!;
            return false;
        }

        lock (SyncRoot)
        {
            EnsureBuilt(kind);
            return EntriesById[kind].TryGetValue(modelId, out entry!);
        }
    }

    public static string CanonicalizeModelId(SelectionModelKind kind, string modelId)
    {
        return TryResolve(kind, modelId, out CustomRunCatalogEntry entry)
            ? entry.ModelId
            : modelId.Trim();
    }

    public static bool IsModelKind(AbstractModel model, SelectionModelKind kind)
    {
        return kind switch
        {
            SelectionModelKind.Card => model is CardModel,
            SelectionModelKind.Relic => model is RelicModel,
            SelectionModelKind.Potion => model is PotionModel,
            SelectionModelKind.Character => model is CharacterModel,
            SelectionModelKind.Power => model is PowerModel,
            _ => false
        };
    }

    private static void EnsureBuilt(SelectionModelKind kind)
    {
        if (Catalogs.ContainsKey(kind))
            return;

        IReadOnlyList<CustomRunCatalogEntry> catalog = kind switch
        {
            SelectionModelKind.Card => Build(kind, ModelDb.AllCards),
            SelectionModelKind.Relic => Build(kind, ModelDb.AllRelics),
            SelectionModelKind.Potion => Build(kind, ModelDb.AllPotions),
            SelectionModelKind.Character => Build(kind, ModelDb.AllCharacters),
            SelectionModelKind.Power => Build(kind, ModelDb.AllPowers),
            _ => []
        };
        Dictionary<string, CustomRunCatalogEntry> lookup = new(StringComparer.OrdinalIgnoreCase);
        foreach (CustomRunCatalogEntry entry in catalog)
        {
            lookup.TryAdd(entry.ModelId, entry);
            lookup.TryAdd(entry.Model.Id.Entry, entry);
        }

        Catalogs[kind] = catalog;
        EntriesById[kind] = lookup;
    }

    private static IReadOnlyList<CustomRunCatalogEntry> Build<TModel>(
        SelectionModelKind kind,
        IEnumerable<TModel> models)
        where TModel : AbstractModel
    {
        return models
            .GroupBy(model => model.Id.ToString(), StringComparer.Ordinal)
            .Select(group => CreateEntry(kind, group.First()))
            .OrderBy(entry => entry.ModelId, StringComparer.Ordinal)
            .ToList();
    }

    private static CustomRunCatalogEntry CreateEntry(SelectionModelKind kind, AbstractModel model)
    {
        HashSet<string> categories = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> types = new(StringComparer.OrdinalIgnoreCase);
        switch (model)
        {
            case CardModel card:
                Add(categories, card.Rarity.ToString(), card.Pool.Id.ToString(), card.Pool.Id.Entry);
                Add(types, card.Type.ToString());
                break;
            case RelicModel relic:
                Add(categories, relic.Rarity.ToString());
                if (LoadoutBag.TryGetRelicPool(relic, out var relicPool))
                    Add(categories, relicPool.Id.ToString(), relicPool.Id.Entry);
                break;
            case PotionModel potion:
                Add(categories, potion.Rarity.ToString(), potion.Pool.Id.ToString(), potion.Pool.Id.Entry);
                break;
        }

        return new CustomRunCatalogEntry(
            kind,
            model.Id.ToString(),
            model,
            CommonHelpers.GetModelModId(model),
            categories,
            types);
    }

    private static void Add(ISet<string> values, params string?[] candidates)
    {
        foreach (string? candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
                values.Add(candidate);
        }
    }
}
