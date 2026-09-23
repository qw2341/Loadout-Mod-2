#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

public sealed class LoadoutKeywordIndex
{
    private readonly Dictionary<CardKeyword, LoadoutKeywordModel[]> _byKeyword;
    private readonly Dictionary<string, LoadoutKeywordModel[]> _byStorageKey;
    private readonly Dictionary<LoadoutKeywordModel, int> _order;
    private readonly Comparison<LoadoutKeywordModel> _compareRegistration;

    public LoadoutKeywordIndex(IReadOnlyList<LoadoutKeywordModel> models)
    {
        _byKeyword = models.GroupBy(model => model.Keyword)
            .ToDictionary(group => group.Key, group => group.ToArray());
        _byStorageKey = models.GroupBy(model => model.StorageKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        _order = models.Select((model, order) => (model, order))
            .ToDictionary(entry => entry.model, entry => entry.order);
        _compareRegistration = CompareRegistration;
    }

    public bool TryGet(CardKeyword keyword, out LoadoutKeywordModel model)
    {
        if (_byKeyword.TryGetValue(keyword, out LoadoutKeywordModel[]? models))
        {
            model = models[0];
            return true;
        }

        model = null!;
        return false;
    }

    public bool TryGet(string key, out LoadoutKeywordModel model)
    {
        if (_byStorageKey.TryGetValue(key, out LoadoutKeywordModel[]? models))
        {
            model = models[0];
            return true;
        }

        model = null!;
        return false;
    }

    public int CompareRegistration(LoadoutKeywordModel left, LoadoutKeywordModel right) =>
        _order[left].CompareTo(_order[right]);

    public int CompareOnPlay(LoadoutKeywordModel left, LoadoutKeywordModel right)
    {
        int priority = left.OnPlayPriority.CompareTo(right.OnPlayPriority);
        return priority != 0 ? priority : CompareRegistration(left, right);
    }

    public int CompareBaseDescription(LoadoutKeywordModel left, LoadoutKeywordModel right)
    {
        int priority = left.BaseDescriptionPriority.CompareTo(right.BaseDescriptionPriority);
        return priority != 0 ? priority : CompareRegistration(left, right);
    }

    public IReadOnlyList<LoadoutKeywordModel> Resolve(
        CardModel card,
        Predicate<LoadoutKeywordModel>? predicate = null,
        IReadOnlyDictionary<string, bool>? overrides = null,
        Comparison<LoadoutKeywordModel>? comparison = null)
    {
        List<LoadoutKeywordModel>? result = null;
        foreach (LoadoutKeywordModel model in EnumerateEnabled(card, overrides))
        {
            if (predicate is null || predicate(model))
                (result ??= []).Add(model);
        }

        if (result is null)
            return Array.Empty<LoadoutKeywordModel>();

        result.Sort(comparison ?? _compareRegistration);
        return result;
    }

    public bool Any(CardModel card, Predicate<LoadoutKeywordModel> predicate)
    {
        foreach (LoadoutKeywordModel model in EnumerateEnabled(card))
        {
            if (predicate(model))
                return true;
        }

        return false;
    }

    public IEnumerable<LoadoutKeywordModel> EnumerateOverrides(
        IReadOnlyDictionary<string, bool> overrides)
    {
        HashSet<LoadoutKeywordModel>? yielded = null;
        foreach (KeyValuePair<string, bool> entry in overrides)
        {
            if (!entry.Value || !_byStorageKey.TryGetValue(entry.Key, out LoadoutKeywordModel[]? models))
                continue;

            foreach (LoadoutKeywordModel model in models)
            {
                // Honor the caller's dictionary comparer, including case-sensitive overrides.
                if (overrides.TryGetValue(model.StorageKey, out bool enabled) && enabled
                    && (yielded ??= []).Add(model))
                    yield return model;
            }
        }
    }

    public IEnumerable<LoadoutKeywordModel> EnumerateLive(
        CardModel card,
        Predicate<LoadoutKeywordModel> predicate)
    {
        int previous = -1;
        while (true)
        {
            LoadoutKeywordModel? next = null;
            int nextOrder = int.MaxValue;
            // Re-resolve after each handler: awaited effects may change later keywords.
            foreach (LoadoutKeywordModel model in EnumerateEnabled(card))
            {
                int order = _order[model];
                if (order > previous && order < nextOrder && predicate(model))
                {
                    next = model;
                    nextOrder = order;
                }
            }

            if (next is null)
                yield break;

            previous = nextOrder;
            yield return next;
        }
    }

    private IEnumerable<LoadoutKeywordModel> EnumerateEnabled(
        CardModel card,
        IReadOnlyDictionary<string, bool>? overrides = null)
    {
        IReadOnlySet<CardKeyword> local = card.GetKeywordsWithSources(KeywordSources.Local);
        bool grantedMultiHit = Powers.MultiHitPower.GrantsKeyword(card);
        foreach (CardKeyword keyword in local)
        {
            if (keyword == CardKeyword.None || !_byKeyword.TryGetValue(keyword, out LoadoutKeywordModel[]? models))
                continue;

            foreach (LoadoutKeywordModel model in models)
            {
                if (overrides?.TryGetValue(model.StorageKey, out bool enabled) != true
                    || enabled || (keyword == LoadoutKeywords.MultiHit && grantedMultiHit))
                {
                    yield return model;
                }
            }
        }

        if (grantedMultiHit && !local.Contains(LoadoutKeywords.MultiHit)
            && _byKeyword.TryGetValue(LoadoutKeywords.MultiHit, out LoadoutKeywordModel[]? grantedModels))
        {
            foreach (LoadoutKeywordModel model in grantedModels)
                yield return model;
        }

        if (overrides is null)
            yield break;

        foreach (LoadoutKeywordModel model in EnumerateOverrides(overrides))
        {
            if ((model.Keyword == CardKeyword.None || !local.Contains(model.Keyword))
                && !(model.Keyword == LoadoutKeywords.MultiHit && grantedMultiHit))
            {
                yield return model;
            }
        }
    }
}
