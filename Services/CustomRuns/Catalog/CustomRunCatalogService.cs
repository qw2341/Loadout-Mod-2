#nullable enable

namespace Loadout.Services.CustomRuns.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using Loadout.PanelItems;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.Morphing;
using Loadout.Services.Compatibility;
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

    public static bool TryResolveMorph(string modelId, out AbstractModel model)
    {
        model = ModelDb.AllCharacters
            .Where(character => character.IsPlayable)
            .Cast<AbstractModel>()
            .Concat(BottledMonsterMorphService.GetMonsterModels())
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Id.ToString(), modelId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Id.Entry, modelId, StringComparison.OrdinalIgnoreCase))!;
        return model is not null;
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
            SelectionModelKind.Monster => model is MonsterModel,
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
            SelectionModelKind.Monster => BuildMonsters(),
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

    private static IReadOnlyList<CustomRunCatalogEntry> BuildMonsters()
    {
        Dictionary<string, HashSet<string>> actsByMonster = new(StringComparer.Ordinal);
        Dictionary<string, HashSet<string>> typesByMonster = new(StringComparer.Ordinal);
        foreach (ActModel act in ModelDb.Acts.Where(act => act.Index >= 0))
        {
            foreach (EncounterModel encounter in act.AllEncounters)
            {
                foreach (MonsterModel monster in encounter.AllPossibleMonsters)
                {
                    string id = monster.Id.ToString();
                    if (!actsByMonster.TryGetValue(id, out HashSet<string>? acts))
                        actsByMonster[id] = acts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    Add(acts, act.Id.ToString(), act.Id.Entry, $"Act {act.Index + 1}");
                    if (!typesByMonster.TryGetValue(id, out HashSet<string>? types))
                        typesByMonster[id] = types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    Add(types, encounter.RoomType.ToString());
                }
            }
        }
        foreach (EncounterModel encounter in Sts2Compatibility.EnumerateEncounters())
        {
            foreach (MonsterModel monster in encounter.AllPossibleMonsters)
            {
                string id = monster.Id.ToString();
                if (!typesByMonster.TryGetValue(id, out HashSet<string>? types))
                    typesByMonster[id] = types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Add(types, encounter.RoomType.ToString());
            }
        }

        return BottledMonsterMorphService.GetMonsterModels()
            .GroupBy(monster => monster.Id.ToString(), StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(monster => new CustomRunCatalogEntry(
                SelectionModelKind.Monster,
                monster.Id.ToString(),
                monster,
                CommonHelpers.GetModelModId(monster),
                actsByMonster.GetValueOrDefault(monster.Id.ToString())
                    ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                typesByMonster.GetValueOrDefault(monster.Id.ToString())
                    ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
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
