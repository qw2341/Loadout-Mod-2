#nullable enable

namespace Loadout.Services.ContentBans;

using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

internal static class ContentBanAncientSlotPoolService
{
    private static readonly ConditionalWeakTable<AncientEventModel, SlotPoolState> SlotPools = new();

    [ThreadStatic]
    private static CaptureContext? _active;

    internal static void Begin(AncientEventModel ancient)
        => _active = new CaptureContext(ancient);

    internal static void Cancel(AncientEventModel ancient)
    {
        if (_active is { } active && ReferenceEquals(active.Ancient, ancient))
            _active = null;
    }

    internal static void Complete(AncientEventModel ancient, IReadOnlyList<EventOption> generated)
    {
        if (_active is not { } active || !ReferenceEquals(active.Ancient, ancient))
            return;

        _active = null;
        try
        {
            List<EventOption> allPossible = ancient.AllPossibleOptions
                .Where(option => option.Relic is not null)
                .ToList();
            List<IReadOnlyList<EventOption>> pools = new(generated.Count);
            foreach (EventOption option in generated)
                pools.Add(ResolvePool(active, option, allPossible));

            SlotPools.Remove(ancient);
            SlotPools.Add(ancient, new SlotPoolState(pools));
        }
        catch
        {
            SlotPools.Remove(ancient);
        }
    }

    internal static IReadOnlyList<EventOption>? GetSlotCandidates(AncientEventModel ancient, int slotIndex)
    {
        if (!SlotPools.TryGetValue(ancient, out SlotPoolState? state)
            || slotIndex < 0 || slotIndex >= state.Pools.Count)
            return null;
        IReadOnlyList<EventOption> pool = state.Pools[slotIndex];
        return pool.Count == 0 ? null : pool;
    }

    internal static IReadOnlyList<EventOption>? GetAllCandidates(AncientEventModel ancient)
    {
        if (!SlotPools.TryGetValue(ancient, out SlotPoolState? state))
            return null;
        List<EventOption> candidates = state.Pools.SelectMany(pool => pool).ToList();
        return candidates.Count == 0 ? null : candidates;
    }

    internal static EventOption[]? PrepareNextEventOptions(
        Rng rng,
        ref IEnumerable<EventOption> candidates)
    {
        if (!IsCapturing(rng))
            return null;
        EventOption[] snapshot = candidates as EventOption[] ?? candidates.ToArray();
        candidates = snapshot;
        return snapshot;
    }

    internal static RelicModel[]? PrepareNextRelics(
        Rng rng,
        ref IEnumerable<RelicModel> candidates)
    {
        if (!IsCapturing(rng))
            return null;
        RelicModel[] snapshot = candidates as RelicModel[] ?? candidates.ToArray();
        candidates = snapshot;
        return snapshot;
    }

    internal static void RecordNext(
        Rng rng,
        IReadOnlyList<EventOption>? candidates,
        EventOption? selected)
    {
        if (candidates is null || selected?.Relic is not { } relic || !TryGetActive(rng, out CaptureContext active))
            return;
        active.Add(CapturedPool.FromOptions(candidates, relic.Id.ToString()));
    }

    internal static void RecordNext(
        Rng rng,
        IReadOnlyList<RelicModel>? candidates,
        RelicModel? selected)
    {
        if (candidates is null || selected is null || !TryGetActive(rng, out CaptureContext active))
            return;
        active.Add(CapturedPool.FromRelics(candidates, selected.Id.ToString()));
    }

    internal static void RecordShuffle(Rng rng, IEnumerable<EventOption> candidates)
    {
        if (!TryGetActive(rng, out CaptureContext active))
            return;
        IReadOnlyList<EventOption> snapshot = candidates as IReadOnlyList<EventOption> ?? candidates.ToArray();
        active.Add(CapturedPool.FromOptions(snapshot, selectedId: null));
    }

    internal static void RecordShuffle(Rng rng, IEnumerable<RelicModel> candidates)
    {
        if (!TryGetActive(rng, out CaptureContext active))
            return;
        IReadOnlyList<RelicModel> snapshot = candidates as IReadOnlyList<RelicModel> ?? candidates.ToArray();
        active.Add(CapturedPool.FromRelics(snapshot, selectedId: null));
    }

    private static bool IsCapturing(Rng rng) => TryGetActive(rng, out _);

    private static bool TryGetActive(Rng rng, out CaptureContext active)
    {
        active = _active!;
        return active is not null && ReferenceEquals(active.Ancient.Rng, rng);
    }

    private static IReadOnlyList<EventOption> ResolvePool(
        CaptureContext capture,
        EventOption generated,
        IReadOnlyList<EventOption> allPossible)
    {
        if (generated.Relic is not { } relic)
            return [];
        string id = relic.Id.ToString();
        CapturedPool? pool = capture.Pools
            .Where(candidate => string.Equals(candidate.SelectedId, id, StringComparison.Ordinal))
            .OrderBy(candidate => candidate.Ids.Count)
            .ThenBy(candidate => candidate.Order)
            .FirstOrDefault()
            ?? capture.Pools.Where(candidate => candidate.Ids.Contains(id))
                .OrderBy(candidate => candidate.Ids.Count)
                .ThenBy(candidate => candidate.Order)
                .FirstOrDefault();
        if (pool is null)
            return [];
        if (pool.Options is not null)
            return pool.Options;

        List<EventOption> options = [];
        foreach (string candidateId in pool.Ids)
        {
            EventOption? option = allPossible.FirstOrDefault(candidate => candidate.Relic is { } candidateRelic
                && string.Equals(candidateRelic.Id.ToString(), candidateId, StringComparison.Ordinal));
            if (option is not null)
                options.Add(option);
        }
        return options;
    }

    private sealed class CaptureContext(AncientEventModel ancient)
    {
        public AncientEventModel Ancient { get; } = ancient;
        public List<CapturedPool> Pools { get; } = [];

        public void Add(CapturedPool pool)
        {
            pool.Order = Pools.Count;
            Pools.Add(pool);
        }
    }

    private sealed class CapturedPool
    {
        public HashSet<string> Ids { get; private init; } = new(StringComparer.Ordinal);
        public IReadOnlyList<EventOption>? Options { get; private init; }
        public string? SelectedId { get; private init; }
        public int Order { get; set; }

        public static CapturedPool FromOptions(IReadOnlyList<EventOption> candidates, string? selectedId)
            => new()
            {
                Ids = candidates.Select(option => option.Relic?.Id.ToString())
                    .OfType<string>().ToHashSet(StringComparer.Ordinal),
                Options = candidates.Where(option => option.Relic is not null).ToArray(),
                SelectedId = selectedId
            };

        public static CapturedPool FromRelics(IReadOnlyList<RelicModel> candidates, string? selectedId)
            => new()
            {
                Ids = candidates.Select(relic => relic.Id.ToString()).ToHashSet(StringComparer.Ordinal),
                SelectedId = selectedId
            };
    }

    private sealed record SlotPoolState(IReadOnlyList<IReadOnlyList<EventOption>> Pools);
}
