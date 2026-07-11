#nullable enable

namespace Loadout.Services.Actions;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MegaCrit.Sts2.Core.Models;

/// <summary>
/// One-time model lookup index used by the mutation wire protocol.
/// The old implementation scanned every card/relic/potion/power/event/monster
/// for every ModelId field in every packet.
/// </summary>
internal static class LoadoutModelRegistry
{
    private static readonly object Gate = new();
    private static Dictionary<string, ModelId>? _wireIds;
    private static Dictionary<string, ModelId>? _entryIds;
    private static Dictionary<string, CardModel>? _cards;
    private static Dictionary<string, RelicModel>? _relics;
    private static Dictionary<string, PotionModel>? _potions;

    public static void WarmUp()
    {
        EnsureBuilt();
    }

    public static bool TryResolveWireId(string rawId, out ModelId id)
    {
        if (string.IsNullOrWhiteSpace(rawId))
        {
            id = ModelId.none;
            return true;
        }

        EnsureBuilt();
        if (_wireIds!.TryGetValue(rawId, out id)
            || _entryIds!.TryGetValue(rawId, out id))
        {
            return true;
        }

        id = ModelId.none;
        return false;
    }

    public static CardModel? ResolveCard(ModelId id)
    {
        if (LoadoutModelIdSafety.IsNoneOrEmpty(id))
            return null;

        EnsureBuilt();
        return _cards!.GetValueOrDefault(id.ToString());
    }

    public static RelicModel? ResolveRelic(ModelId id)
    {
        if (LoadoutModelIdSafety.IsNoneOrEmpty(id))
            return null;

        EnsureBuilt();
        return _relics!.GetValueOrDefault(id.ToString());
    }

    public static PotionModel? ResolvePotion(ModelId id)
    {
        if (LoadoutModelIdSafety.IsNoneOrEmpty(id))
            return null;

        EnsureBuilt();
        return _potions!.GetValueOrDefault(id.ToString());
    }

    private static void EnsureBuilt()
    {
        if (Volatile.Read(ref _wireIds) is not null)
            return;

        lock (Gate)
        {
            if (_wireIds is not null)
                return;

            Dictionary<string, ModelId> wireIds = new(StringComparer.Ordinal);
            Dictionary<string, ModelId> entryIds = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, CardModel> cards = new(StringComparer.Ordinal);
            Dictionary<string, RelicModel> relics = new(StringComparer.Ordinal);
            Dictionary<string, PotionModel> potions = new(StringComparer.Ordinal);
            foreach (CardModel model in ModelDb.AllCards)
                cards.TryAdd(model.Id.ToString(), model);
            foreach (RelicModel model in ModelDb.AllRelics)
                relics.TryAdd(model.Id.ToString(), model);
            foreach (PotionModel model in ModelDb.AllPotions)
                potions.TryAdd(model.Id.ToString(), model);

            foreach (AbstractModel model in EnumerateKnownModels())
            {
                string wireId = model.Id.ToString();
                if (!string.IsNullOrWhiteSpace(wireId))
                    wireIds.TryAdd(wireId, model.Id);

                if (!string.IsNullOrWhiteSpace(model.Id.Entry))
                    entryIds.TryAdd(model.Id.Entry, model.Id);
            }

            _entryIds = entryIds;
            _cards = cards;
            _relics = relics;
            _potions = potions;
            Volatile.Write(ref _wireIds, wireIds);
        }
    }

    private static IEnumerable<AbstractModel> EnumerateKnownModels()
    {
        foreach (CardModel model in ModelDb.AllCards)
            yield return model;
        foreach (RelicModel model in ModelDb.AllRelics)
            yield return model;
        foreach (PotionModel model in ModelDb.AllPotions)
            yield return model;
        foreach (PowerModel model in ModelDb.AllPowers)
            yield return model;
        foreach (EventModel model in ModelDb.AllEvents)
            yield return model;
        foreach (AncientEventModel model in ModelDb.AllAncients)
            yield return model;
        foreach (MonsterModel model in ModelDb.Monsters)
            yield return model;
        foreach (CharacterModel model in ModelDb.AllCharacters.Where(character => character.IsPlayable))
            yield return model;
    }
}
