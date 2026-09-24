#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

[Flags]
public enum LoadoutCardModificationFlags
{
    None = 0,
    FastAnimation = 1 << 0,
    OverrideXCost = 1 << 1,
    OverrideXValue = 1 << 2
}

public static class LoadoutCardModificationFlagState
{
    private sealed record CachedFlags(LoadoutCardModificationFlags Value);

    // Native clones and full rebuilds replace the local keyword set.
    private static readonly ConditionalWeakTable<IReadOnlySet<CardKeyword>, CachedFlags> Cache = new();

    public static LoadoutCardModificationFlags GetFlags(CardModel card)
    {
        LoadoutCardModificationFlags flags = GetLocalFlags(card);
        if ((flags & MultiHitKeyword.Instance.ModificationFlags) != MultiHitKeyword.Instance.ModificationFlags
            && Powers.MultiHitPower.GrantsKeyword(card))
            flags |= MultiHitKeyword.Instance.ModificationFlags;
        return flags;
    }

    public static bool HasFlag(CardModel card, LoadoutCardModificationFlags flag)
    {
        LoadoutCardModificationFlags flags = GetLocalFlags(card);
        if ((flags & flag) == flag)
            return true;
        return ((flags | MultiHitKeyword.Instance.ModificationFlags) & flag) == flag
               && Powers.MultiHitPower.GrantsKeyword(card);
    }

    private static LoadoutCardModificationFlags GetLocalFlags(CardModel card) =>
        Cache.GetValue(card.GetKeywordsWithSources(KeywordSources.Local),
            static local => new CachedFlags(GetFlags(local))).Value;

    public static LoadoutCardModificationFlags GetFlags(
        IReadOnlySet<CardKeyword> keywords,
        IReadOnlyDictionary<string, bool>? overrides = null)
    {
        LoadoutCardModificationFlags flags = LoadoutCardModificationFlags.None;
        foreach (CardKeyword keyword in keywords)
        {
            if (keyword != CardKeyword.None
                && LoadoutKeywordRegistry.TryGet(keyword, out LoadoutKeywordModel model)
                && (overrides?.TryGetValue(model.StorageKey, out bool enabled) != true || enabled))
                flags |= model.ModificationFlags;
        }

        if (overrides is not null)
        {
            foreach ((string key, bool enabled) in overrides)
            {
                if (enabled && LoadoutKeywordRegistry.TryGet(key, out LoadoutKeywordModel model)
                    && overrides.TryGetValue(model.StorageKey, out bool requested) && requested)
                    flags |= model.ModificationFlags;
            }
        }
        return flags;
    }

    public static LoadoutCardModificationFlags GetFlags(IReadOnlyList<LoadoutKeywordModel> models)
    {
        LoadoutCardModificationFlags flags = LoadoutCardModificationFlags.None;
        foreach (LoadoutKeywordModel model in models)
            flags |= model.ModificationFlags;
        return flags;
    }

    // Changed keys include explicit removals, which also require cost reconstruction.
    public static LoadoutCardModificationFlags GetOverrideFlags(IEnumerable<string> keys)
    {
        LoadoutCardModificationFlags flags = LoadoutCardModificationFlags.None;
        foreach (string key in keys)
        {
            if (LoadoutKeywordRegistry.TryGet(key, out LoadoutKeywordModel model))
                flags |= model.ModificationFlags;
        }
        return flags;
    }

    public static void Invalidate(CardModel card) =>
        Cache.Remove(card.GetKeywordsWithSources(KeywordSources.Local));
}

[HarmonyPatch]
public static class LoadoutCardModificationFlagsChangedPatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(CardModel), nameof(CardModel.AddKeyword));
        yield return AccessTools.Method(typeof(CardModel), nameof(CardModel.RemoveKeyword));
    }

    // Invalidate before native KeywordsChanged listeners read the new state.
    [HarmonyPrefix]
    public static void Prefix(CardModel __instance) =>
        LoadoutCardModificationFlagState.Invalidate(__instance);
}
