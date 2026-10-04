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

[Flags]
internal enum LoadoutKeywordCapabilities
{
    None = 0,
    ChangesTargeting = 1 << 0,
    RequiresAnotherPlayerTarget = 1 << 1,
    ReportsGainsBlock = 1 << 2,
    HasFatalEffect = 1 << 3,
    RequiresFatalTargetSnapshots = 1 << 4,
    HasTurnEndInHandEffect = 1 << 5,
    SuppressesOriginalOnPlay = 1 << 6,
    SuppressesOriginalIsPlayable = 1 << 7,
    SuppressesOriginalShouldGlowGold = 1 << 8,
    SuppressesOriginalModelHooks = 1 << 9
}

public static class LoadoutCardModificationFlagState
{
    private sealed record CachedFlags(
        LoadoutCardModificationFlags Value,
        LoadoutKeywordCapabilities Capabilities);

    private static readonly LoadoutKeywordCapabilities MultiHitCapabilities =
        GetCapabilities(MultiHitKeyword.Instance);

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

    private static CachedFlags GetLocalState(CardModel card) =>
        Cache.GetValue(card.GetKeywordsWithSources(KeywordSources.Local),
            CreateLocalState);

    private static LoadoutCardModificationFlags GetLocalFlags(CardModel card) =>
        GetLocalState(card).Value;

    internal static bool HasCapability(CardModel card, LoadoutKeywordCapabilities capability)
    {
        LoadoutKeywordCapabilities capabilities = GetLocalState(card).Capabilities;
        if ((capabilities & capability) == capability)
            return true;
        return ((capabilities | MultiHitCapabilities) & capability) == capability
               && Powers.MultiHitPower.GrantsKeyword(card);
    }

    private static CachedFlags CreateLocalState(IReadOnlySet<CardKeyword> keywords)
    {
        LoadoutCardModificationFlags flags = LoadoutCardModificationFlags.None;
        LoadoutKeywordCapabilities capabilities = LoadoutKeywordCapabilities.None;
        foreach (CardKeyword keyword in keywords)
        {
            if (keyword == CardKeyword.None
                || !LoadoutKeywordRegistry.TryGet(keyword, out LoadoutKeywordModel model))
                continue;

            flags |= model.ModificationFlags;
            capabilities |= GetCapabilities(model);
        }
        return new CachedFlags(flags, capabilities);
    }

    private static LoadoutKeywordCapabilities GetCapabilities(LoadoutKeywordModel model)
    {
        LoadoutKeywordCapabilities capabilities = LoadoutKeywordCapabilities.None;
        if (model.ChangesTargeting) capabilities |= LoadoutKeywordCapabilities.ChangesTargeting;
        if (model.RequiresAnotherPlayerTarget) capabilities |= LoadoutKeywordCapabilities.RequiresAnotherPlayerTarget;
        if (model.ReportsGainsBlock) capabilities |= LoadoutKeywordCapabilities.ReportsGainsBlock;
        if (model.HasFatalEffect) capabilities |= LoadoutKeywordCapabilities.HasFatalEffect;
        if (model.HasFatalEffect && model.RequiresFatalTargetSnapshots) capabilities |= LoadoutKeywordCapabilities.RequiresFatalTargetSnapshots;
        if (model.HasTurnEndInHandEffect) capabilities |= LoadoutKeywordCapabilities.HasTurnEndInHandEffect;
        if (model.SuppressesOriginalOnPlay) capabilities |= LoadoutKeywordCapabilities.SuppressesOriginalOnPlay;
        if (model.SuppressesOriginalIsPlayable) capabilities |= LoadoutKeywordCapabilities.SuppressesOriginalIsPlayable;
        if (model.SuppressesOriginalShouldGlowGold) capabilities |= LoadoutKeywordCapabilities.SuppressesOriginalShouldGlowGold;
        if (model.SuppressesOriginalModelHooks) capabilities |= LoadoutKeywordCapabilities.SuppressesOriginalModelHooks;
        return capabilities;
    }

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
