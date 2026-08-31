#nullable enable

namespace Loadout.Services.ContentBans;

using System;
using System.Collections.Generic;
using System.Linq;
using Loadout.Services.Compatibility;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Timeline.Epochs;

internal static class ContentBanEventRerollService
{
    internal static EventModel Resolve(IRunState runState, EventModel original)
    {
        if (!ContentBanService.IsBanned(original))
            return original;

        ActModel currentAct = runState.Act;
        int currentIndex = currentAct.Index;
        List<EventTier> tiers = original is AncientEventModel
            ? BuildAncientTiers(runState, currentAct, currentIndex)
            : BuildRegularTiers(runState, currentAct, currentIndex);

        for (int i = 0; i < tiers.Count; i++)
        {
            if (TrySelect(runState, original, tiers[i], i, out EventModel replacement))
                return replacement;
        }

        return original;
    }

    private static List<EventTier> BuildRegularTiers(
        IRunState runState,
        ActModel currentAct,
        int currentIndex)
    {
        IReadOnlyList<ActModel> allActs = CatalogActs();
        IEnumerable<ActModel> sameIndexActs = allActs
            .Where(act => act.Index == currentIndex && act.Id != currentAct.Id);
        return
        [
            new EventTier(GeneratedRegularEvents(currentAct), true),
            new EventTier(RegularEventsForActs(sameIndexActs), false),
            new EventTier(ModelDb.AllEvents, false),
            new EventTier(AncientsForActs(runState, allActs.Where(act => act.Index == currentIndex)), false),
            new EventTier(AncientsForActs(runState, allActs).Concat(runState.UnlockState.SharedAncients), false)
        ];
    }

    private static List<EventTier> BuildAncientTiers(
        IRunState runState,
        ActModel currentAct,
        int currentIndex)
    {
        IReadOnlyList<ActModel> allActs = CatalogActs();
        IEnumerable<ActModel> sameIndexActs = allActs
            .Where(act => act.Index == currentIndex && act.Id != currentAct.Id);
        return
        [
            new EventTier(AncientsForActs(runState, [currentAct]).Concat(runState.UnlockState.SharedAncients), false),
            new EventTier(AncientsForActs(runState, sameIndexActs), false),
            new EventTier(AncientsForActs(runState, allActs).Concat(runState.UnlockState.SharedAncients), false),
            new EventTier(GeneratedRegularEvents(currentAct), true),
            new EventTier(RegularEventsForActs(sameIndexActs), false),
            new EventTier(ModelDb.AllEvents, false)
        ];
    }

    private static IReadOnlyList<ActModel> CatalogActs()
    {
        return OrderedActs(ModelDb.Acts.Where(act => act.Index >= 0))
            .GroupBy(act => act.Id.ToString(), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    private static IEnumerable<ActModel> OrderedActs(IEnumerable<ActModel> acts)
    {
        return acts.OrderBy(act => act.Index)
            .ThenBy(act => act.IsDefault ? 0 : 1)
            .ThenBy(act => act.Id.ToString(), StringComparer.Ordinal);
    }

    private static IEnumerable<EventModel> RegularEventsForActs(IEnumerable<ActModel> acts)
    {
        return acts.SelectMany(act => act.AllEvents);
    }

    private static IEnumerable<EventModel> GeneratedRegularEvents(ActModel act)
    {
        SerializableRoomSet rooms;
        try
        {
            rooms = act.ToSave().SerializableRooms;
        }
        catch
        {
            ActModel? canonical = ModelDb.Acts.FirstOrDefault(candidate => candidate.Id == act.Id);
            return canonical is null
                ? []
                : canonical.AllEvents.Concat(ModelDb.AllSharedEvents);
        }

        if (rooms.EventIds is null || rooms.EventIds.Count == 0)
        {
            ActModel canonical = ModelDb.Acts.FirstOrDefault(candidate => candidate.Id == act.Id) ?? act;
            return canonical.AllEvents.Concat(ModelDb.AllSharedEvents);
        }

        List<EventModel> ordered = [];
        int start = Math.Abs(rooms.EventsVisited % rooms.EventIds.Count);
        for (int offset = 0; offset < rooms.EventIds.Count; offset++)
        {
            ModelId id = rooms.EventIds[(start + offset) % rooms.EventIds.Count];
            EventModel? eventModel = ModelDb.GetByIdOrNull<EventModel>(id);
            if (eventModel is not null)
                ordered.Add(eventModel);
        }
        return ordered;
    }

    private static IEnumerable<EventModel> AncientsForActs(IRunState runState, IEnumerable<ActModel> acts)
    {
        foreach (ActModel act in acts)
        {
            ActModel canonical = ModelDb.Acts.FirstOrDefault(candidate => candidate.Id == act.Id) ?? act;
            IEnumerable<AncientEventModel> ancients;
            try
            {
                ancients = canonical.GetUnlockedAncients(runState.UnlockState);
            }
            catch
            {
                ancients = canonical.AllAncients;
            }
            foreach (AncientEventModel ancient in ancients)
                yield return ancient;
        }
    }

    private static bool TrySelect(
        IRunState runState,
        EventModel original,
        EventTier tier,
        int tierIndex,
        out EventModel replacement)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<EventModel> eligible = [];
        List<EventModel> unvisited = [];
        IReadOnlySet<ModelId>? visited = (runState as RunState)?.VisitedEventIds;
        foreach (EventModel candidate in tier.Candidates)
        {
            string id = candidate.Id.ToString();
            if (!seen.Add(id)
                || candidate.Id == original.Id
                || candidate is DeprecatedEvent
                || ContentBanService.IsBanned(candidate)
                || !IsUnlocked(candidate, runState)
                || !IsAllowed(candidate, runState))
            {
                continue;
            }

            eligible.Add(candidate);
            if (visited?.Contains(candidate.Id) != true)
                unvisited.Add(candidate);
        }

        List<EventModel> candidates = unvisited.Count > 0 ? unvisited : eligible;
        if (candidates.Count == 0)
        {
            replacement = null!;
            return false;
        }

        if (tier.PreserveOrder || runState.Players.FirstOrDefault() is not { } seedPlayer)
        {
            replacement = candidates[0];
            return true;
        }

        candidates.Sort((left, right) => string.Compare(
            left.Id.ToString(),
            right.Id.ToString(),
            StringComparison.Ordinal));
        ulong mixin = BuildMixin(runState, tierIndex);
        replacement = Sts2Compatibility.CreateEventRng(seedPlayer, original.Id, mixin)
            .NextItem(candidates)!;
        return true;
    }

    private static bool IsAllowed(EventModel eventModel, IRunState runState)
    {
        try
        {
            return eventModel.IsAllowed(runState);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUnlocked(EventModel eventModel, IRunState runState)
    {
        if (eventModel is AncientEventModel)
            return true;

        return (runState.UnlockState.IsEpochRevealed<Event1Epoch>()
                || !Event1Epoch.Events.Any(candidate => candidate.Id == eventModel.Id))
               && (runState.UnlockState.IsEpochRevealed<Event2Epoch>()
                   || !Event2Epoch.Events.Any(candidate => candidate.Id == eventModel.Id))
               && (runState.UnlockState.IsEpochRevealed<Event3Epoch>()
                   || !Event3Epoch.Events.Any(candidate => candidate.Id == eventModel.Id));
    }

    private static ulong BuildMixin(IRunState runState, int tierIndex)
    {
        ulong act = unchecked((uint)runState.CurrentActIndex);
        ulong floor = unchecked((uint)runState.ActFloor);
        ulong history = unchecked((uint)runState.MapPointHistory.Count);
        return (act << 48) ^ (floor << 24) ^ history ^ unchecked((ulong)(tierIndex + 1) * 0x9E3779B9UL);
    }

    private sealed record EventTier(IEnumerable<EventModel> Candidates, bool PreserveOrder);
}
