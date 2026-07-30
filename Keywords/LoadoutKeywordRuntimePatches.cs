#nullable enable

using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using Loadout.Services.CardModification;
using Loadout.Services.Compatibility;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using LinqExpression = System.Linq.Expressions.Expression;

internal static class LoadoutKeywordRuntimePatches
{
    private const string InfiniteHarmonyId = "Loadout.Keyword.InfiniteUpgrade";
    private const string XCostHarmonyId = "Loadout.Keyword.XCost";
    private const string StickyHarmonyId = "Loadout.Keyword.Sticky";
    private const string CardResultHarmonyId = "Loadout.Keyword.CardResultLocation";
    private const string InevitableHarmonyId = "Loadout.Keyword.Inevitable";
    private const string DescriptionKeywordHarmonyId =
        "Loadout.Keyword.Description";
    private const string PostOnPlayHarmonyId = "Loadout.Keyword.PostOnPlay";

    private static readonly Harmony InfiniteHarmony = new(InfiniteHarmonyId);
    private static readonly Harmony XCostHarmony = new(XCostHarmonyId);
    private static readonly Harmony StickyHarmony = new(StickyHarmonyId);
    private static readonly Harmony CardResultHarmony = new(CardResultHarmonyId);
    private static readonly Harmony InevitableHarmony = new(InevitableHarmonyId);
    private static readonly Harmony DescriptionKeywordHarmony =
        new(DescriptionKeywordHarmonyId);
    private static readonly Harmony PostOnPlayHarmony = new(PostOnPlayHarmonyId);

    public static bool InfiniteUpgradeEnabled { get; private set; }
    public static bool XCostEnabled { get; private set; }
    public static bool StickyEnabled { get; private set; }
    public static bool PassingEnabled { get; private set; }
    public static bool InevitableEnabled { get; private set; }
    public static bool LividEnabled { get; private set; }
    public static bool DescriptionKeywordsEnabled { get; private set; }
    private static bool DescriptionKeywordPostOnPlayEnabled { get; set; }
    private static bool CardResultLocationEnabled { get; set; }
    private static bool PostOnPlayEnabled { get; set; }
    private static bool RunKeywordPatchesPrepared { get; set; }

    public static void EnableFromDelta(CardModificationDelta delta)
    {
        if (IsEnabled(delta, LoadoutKeywords.InfiniteUpgradeKey))
            SetInfiniteUpgradeEnabled(true);
        if (IsEnabled(delta, LoadoutKeywords.XCostKey))
            SetXCostEnabled(true);
        if (IsEnabled(delta, LoadoutKeywords.StickyKey))
            SetStickyEnabled(true);
        if (IsEnabled(delta, LoadoutKeywords.PassingKey))
            SetPassingEnabled(true);
        if (IsEnabled(delta, LoadoutKeywords.InevitableKey))
            SetInevitableEnabled(true);
        if (IsEnabled(delta, LoadoutKeywords.GetStorageKey(LoadoutKeywords.Livid)))
            SetLividEnabled(true);
        EnableDescriptionKeywordsFromOverrides(delta.KeywordOverrides);
        EnableFromOverrides(delta.UpgradeModification.KeywordOverrides);
    }

    public static void EnableFromOverrides(IReadOnlyDictionary<string, bool> overrides)
    {
        if (IsEnabled(overrides, LoadoutKeywords.InfiniteUpgradeKey))
            SetInfiniteUpgradeEnabled(true);
        if (IsEnabled(overrides, LoadoutKeywords.XCostKey))
            SetXCostEnabled(true);
        if (IsEnabled(overrides, LoadoutKeywords.StickyKey))
            SetStickyEnabled(true);
        if (IsEnabled(overrides, LoadoutKeywords.PassingKey))
            SetPassingEnabled(true);
        if (IsEnabled(overrides, LoadoutKeywords.InevitableKey))
            SetInevitableEnabled(true);
        if (IsEnabled(overrides, LoadoutKeywords.GetStorageKey(LoadoutKeywords.Livid)))
            SetLividEnabled(true);
        EnableDescriptionKeywordsFromOverrides(overrides);
    }

    public static bool HasEnabledInfiniteUpgrade(CardModificationSpec? state)
    {
        return GetInfiniteUpgradeOverride(state) == true;
    }

    public static bool HasEnabledInfiniteUpgrade(CardModificationDelta? delta)
    {
        return GetInfiniteUpgradeOverride(delta) == true;
    }

    public static bool? GetInfiniteUpgradeOverride(CardModificationSpec? state)
    {
        return state?.KeywordOverrides.TryGetValue(
            LoadoutKeywords.InfiniteUpgradeKey,
            out bool enabled) == true
            ? enabled
            : null;
    }

    public static bool? GetInfiniteUpgradeOverride(CardModificationDelta? delta)
    {
        return delta?.KeywordOverrides.TryGetValue(
            LoadoutKeywords.InfiniteUpgradeKey,
            out bool enabled) == true
            ? enabled
            : null;
    }

    public static bool ResolveEffectiveInfiniteUpgrade(
        CardModificationSpec? permanent,
        CardModificationDelta? temporary,
        CardModificationSpec? legacyTemporary = null)
    {
        return GetInfiniteUpgradeOverride(temporary)
               ?? GetInfiniteUpgradeOverride(legacyTemporary)
               ?? GetInfiniteUpgradeOverride(permanent)
               ?? false;
    }

    public static void EnsureInfiniteUpgradeEnabled()
    {
        SetInfiniteUpgradeEnabled(true);
    }

    public static void PrepareRunKeywordPatches()
    {
        if (RunKeywordPatchesPrepared)
            return;

        RunKeywordPatchesPrepared = true;
        SetDescriptionKeywordsEnabled(true);
    }

    public static void Reconcile()
    {
        KeywordFeatureState required = GetRequiredFeatures();
        SetInfiniteUpgradeEnabled(required.InfiniteUpgrade);
        SetXCostEnabled(required.XCost);
        SetStickyEnabled(required.Sticky);
        SetPassingEnabled(required.Passing);
        SetInevitableEnabled(required.Inevitable);
        SetLividEnabled(required.Livid);
        SetDescriptionKeywordsEnabled(
            RunKeywordPatchesPrepared || required.DescriptionKeywords);
        SetDescriptionKeywordPostOnPlayEnabled(
            required.DescriptionKeywordPostOnPlay);
    }

    public static void ResetRunPatches()
    {
        SetInfiniteUpgradeEnabled(false);
        SetXCostEnabled(false);
        SetStickyEnabled(false);
        SetPassingEnabled(false);
        SetInevitableEnabled(false);
        SetLividEnabled(false);
        RunKeywordPatchesPrepared = false;
        SetDescriptionKeywordsEnabled(false);
        SetDescriptionKeywordPostOnPlayEnabled(false);
    }

    private static KeywordFeatureState GetRequiredFeatures()
    {
        KeywordFeatureState state = default;
        foreach (CardModificationDelta delta in PermanentCardModificationStore.GetEffectiveDeltasSnapshot().Values)
            AddDeltaFeatures(delta, ref state);

        try
        {
            if (!RunManager.Instance.IsInProgress)
                return state;

            RunState? runState = RunManager.Instance.DebugOnlyGetState();
            if (runState is null)
                return state;
            foreach (Player player in runState.Players)
            {
                AddCardFeatures(player.Deck.Cards, ref state);
                if (player.PlayerCombatState is { } combatState)
                    AddCardFeatures(combatState.AllCards, ref state);

                if (state.All)
                    return state;
            }
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Loadout keywords: failed reconciling live feature patches. {exception.Message}");
        }

        return state;
    }

    private static void AddDeltaFeatures(CardModificationDelta delta, ref KeywordFeatureState state)
    {
        state.InfiniteUpgrade |= IsEnabled(delta, LoadoutKeywords.InfiniteUpgradeKey);
        state.XCost |= IsEnabled(delta, LoadoutKeywords.XCostKey);
        state.Sticky |= IsEnabled(delta, LoadoutKeywords.StickyKey);
        state.Passing |= IsEnabled(delta, LoadoutKeywords.PassingKey);
        state.Inevitable |= IsEnabled(delta, LoadoutKeywords.InevitableKey);
        state.Livid |= IsEnabled(delta, LoadoutKeywords.GetStorageKey(LoadoutKeywords.Livid));
        AddDescriptionKeywordFeatures(delta.KeywordOverrides, ref state);
        state.InfiniteUpgrade |= IsEnabled(
            delta.UpgradeModification.KeywordOverrides,
            LoadoutKeywords.InfiniteUpgradeKey);
        state.XCost |= IsEnabled(
            delta.UpgradeModification.KeywordOverrides,
            LoadoutKeywords.XCostKey);
        state.Sticky |= IsEnabled(
            delta.UpgradeModification.KeywordOverrides,
            LoadoutKeywords.StickyKey);
        state.Passing |= IsEnabled(
            delta.UpgradeModification.KeywordOverrides,
            LoadoutKeywords.PassingKey);
        state.Inevitable |= IsEnabled(
            delta.UpgradeModification.KeywordOverrides,
            LoadoutKeywords.InevitableKey);
        state.Livid |= IsEnabled(
            delta.UpgradeModification.KeywordOverrides,
            LoadoutKeywords.GetStorageKey(LoadoutKeywords.Livid));
        AddDescriptionKeywordFeatures(
            delta.UpgradeModification.KeywordOverrides,
            ref state);
    }

    private static void AddCardFeatures(IEnumerable<CardModel> cards, ref KeywordFeatureState state)
    {
        foreach (CardModel card in cards)
        {
            state.InfiniteUpgrade |= LoadoutKeywords.Has(card, LoadoutKeywords.InfiniteUpgrade);
            state.XCost |= LoadoutKeywords.Has(card, LoadoutKeywords.XCost);
            state.Sticky |= LoadoutKeywords.Has(card, LoadoutKeywords.Sticky);
            state.Passing |= LoadoutKeywords.Has(card, LoadoutKeywords.Passing);
            state.Inevitable |= LoadoutKeywords.Has(card, LoadoutKeywords.Inevitable);
            state.Livid |= LoadoutKeywords.Has(card, LoadoutKeywords.Livid);
            foreach (LoadoutKeywordModel model in LoadoutKeywordRegistry.DescriptionOnly)
            {
                if (!model.IsEnabled(card))
                    continue;

                state.DescriptionKeywords = true;
                state.DescriptionKeywordPostOnPlay |= model.HasOnPlayEffect;
            }
            if (state.All)
                return;
        }
    }

    private static void AddDescriptionKeywordFeatures(
        IReadOnlyDictionary<string, bool> overrides,
        ref KeywordFeatureState state)
    {
        foreach (LoadoutKeywordModel model in LoadoutKeywordRegistry.DescriptionOnly)
        {
            if (!IsEnabled(overrides, model.StorageKey))
                continue;

            state.DescriptionKeywords = true;
            state.DescriptionKeywordPostOnPlay |= model.HasOnPlayEffect;
        }
    }

    private static void EnableDescriptionKeywordsFromOverrides(
        IReadOnlyDictionary<string, bool> overrides)
    {
        bool anyEnabled = false;
        bool anyOnPlayEnabled = false;
        foreach (LoadoutKeywordModel model in LoadoutKeywordRegistry.DescriptionOnly)
        {
            if (!IsEnabled(overrides, model.StorageKey))
                continue;

            anyEnabled = true;
            anyOnPlayEnabled |= model.HasOnPlayEffect;
        }

        if (anyEnabled)
            SetDescriptionKeywordsEnabled(true);
        if (anyOnPlayEnabled)
            SetDescriptionKeywordPostOnPlayEnabled(true);
    }

    private static bool IsEnabled(CardModificationDelta delta, string key) =>
        IsEnabled(delta.KeywordOverrides, key);

    private static bool IsEnabled(IReadOnlyDictionary<string, bool> overrides, string key) =>
        overrides.TryGetValue(key, out bool enabled) && enabled;

    private static void SetInfiniteUpgradeEnabled(bool enabled)
    {
        if (enabled == InfiniteUpgradeEnabled)
            return;

        if (!enabled)
        {
            InfiniteHarmony.UnpatchAll(InfiniteHarmonyId);
            InfiniteUpgradeEnabled = false;
            return;
        }

        TryEnable(InfiniteHarmony, InfiniteHarmonyId, () =>
        {
            HarmonyMethod maxLevelPostfix = new(typeof(InfiniteUpgradeMaxLevelPatch), nameof(InfiniteUpgradeMaxLevelPatch.Postfix));
            foreach (MethodBase target in InfiniteUpgradeMaxLevelPatch.TargetMethods())
                InfiniteHarmony.Patch(target, postfix: maxLevelPostfix);

            PatchPrefixFinalizer(InfiniteHarmony,
                AccessTools.Method(typeof(CardModel), "UpgradeInternal")!,
                typeof(InfiniteUpgradeContextPatch),
                nameof(InfiniteUpgradeContextPatch.Prefix),
                nameof(InfiniteUpgradeContextPatch.Finalizer));
            PatchPrefix(InfiniteHarmony,
                AccessTools.Method(typeof(DynamicVarSet), nameof(DynamicVarSet.RecalculateForUpgradeOrEnchant))!,
                typeof(InfiniteUpgradeRecalculationBoundaryPatch),
                nameof(InfiniteUpgradeRecalculationBoundaryPatch.Prefix));
            PatchPrefix(InfiniteHarmony,
                AccessTools.Method(typeof(DynamicVar), nameof(DynamicVar.UpgradeValueBy))!,
                typeof(InfiniteUpgradeDynamicValuePatch),
                nameof(InfiniteUpgradeDynamicValuePatch.Prefix));
        }, () => InfiniteUpgradeEnabled = true);
    }

    private static void SetXCostEnabled(bool enabled)
    {
        if (enabled == XCostEnabled)
            return;
        if (!enabled)
        {
            XCostHarmony.UnpatchAll(XCostHarmonyId);
            XCostEnabled = false;
            return;
        }

        TryEnable(XCostHarmony, XCostHarmonyId, () =>
            XCostHarmony.Patch(
                XCostPlayCountPatch.TargetMethod(),
                postfix: new HarmonyMethod(typeof(XCostPlayCountPatch), nameof(XCostPlayCountPatch.Postfix))),
            () => XCostEnabled = true);
    }

    private static void SetStickyEnabled(bool enabled)
    {
        if (enabled == StickyEnabled)
            return;
        if (!enabled)
        {
            StickyHarmony.UnpatchAll(StickyHarmonyId);
            StickyEnabled = false;
            RefreshCardResultLocationPatch();
            return;
        }

        TryEnable(StickyHarmony, StickyHarmonyId, () =>
        {
            PatchPrefixPostfix(StickyHarmony,
                AccessTools.Method(typeof(CardCmd), nameof(CardCmd.DiscardAndDraw),
                    [typeof(PlayerChoiceContext), typeof(IEnumerable<CardModel>), typeof(int)])!,
                typeof(StickyDiscardPatch),
                nameof(StickyDiscardPatch.Prefix),
                nameof(StickyDiscardPatch.Postfix));
            PatchPrefixPostfix(StickyHarmony,
                StickyFlushPlayerHandPatch.TargetMethod(),
                typeof(StickyFlushPlayerHandPatch),
                nameof(StickyFlushPlayerHandPatch.Prefix),
                nameof(StickyFlushPlayerHandPatch.Postfix));
        }, () =>
        {
            StickyEnabled = true;
            RefreshCardResultLocationPatch();
        });
    }

    private static void SetPassingEnabled(bool enabled)
    {
        if (enabled == PassingEnabled)
            return;

        PassingEnabled = enabled;
        RefreshCardResultLocationPatch();
    }

    private static void RefreshCardResultLocationPatch()
    {
        bool enabled = StickyEnabled || PassingEnabled;
        if (enabled == CardResultLocationEnabled)
            return;

        if (!enabled)
        {
            CardResultHarmony.UnpatchAll(CardResultHarmonyId);
            CardResultLocationEnabled = false;
            return;
        }

        TryEnable(CardResultHarmony, CardResultHarmonyId, () =>
            CardResultHarmony.Patch(
                Sts2Compatibility.StickyCardPlayResultMethod,
                postfix: new HarmonyMethod(CardResultLocationKeywordPatch.GetPostfixMethod())),
            () => CardResultLocationEnabled = true);
    }

    private static void SetInevitableEnabled(bool enabled)
    {
        if (enabled == InevitableEnabled)
            return;
        if (!enabled)
        {
            InevitableHarmony.UnpatchAll(InevitableHarmonyId);
            InevitableEnabled = false;
            return;
        }

        TryEnable(InevitableHarmony, InevitableHarmonyId, () =>
        {
            InevitableHarmony.Patch(
                AccessTools.Method(typeof(CardCmd), nameof(CardCmd.Exhaust),
                    [typeof(PlayerChoiceContext), typeof(CardModel), typeof(bool), typeof(bool)])!,
                postfix: new HarmonyMethod(typeof(InevitableExhaustPatch), nameof(InevitableExhaustPatch.Postfix)));
            InevitableHarmony.Patch(
                AccessTools.Method(typeof(CardCmd), nameof(CardCmd.Transform),
                    [typeof(IEnumerable<CardTransformation>), typeof(Rng), typeof(CardPreviewStyle)])!,
                prefix: new HarmonyMethod(typeof(InevitableTransformPatch), nameof(InevitableTransformPatch.Prefix)));
        }, () => InevitableEnabled = true);
    }

    private static void SetLividEnabled(bool enabled)
    {
        if (enabled == LividEnabled)
            return;

        LividEnabled = enabled;
        RefreshPostOnPlayPatch();
    }

    private static void SetDescriptionKeywordsEnabled(bool enabled)
    {
        if (enabled == DescriptionKeywordsEnabled)
            return;
        if (!enabled)
        {
            DescriptionKeywordHarmony.UnpatchAll(
                DescriptionKeywordHarmonyId);
            DescriptionKeywordsEnabled = false;
            return;
        }

        TryEnable(
            DescriptionKeywordHarmony,
            DescriptionKeywordHarmonyId,
            () =>
        {
            DescriptionKeywordHarmony.Patch(
                LoadoutKeywordModel.GetDescriptionTarget(),
                postfix: new HarmonyMethod(
                    typeof(LoadoutDescriptionKeywordPatch),
                    nameof(LoadoutDescriptionKeywordPatch.Postfix)));
            DescriptionKeywordHarmony.Patch(
                AccessTools.PropertyGetter(
                    typeof(CardModel),
                    nameof(CardModel.HoverTips))
                ?? throw new MissingMethodException(
                    typeof(CardModel).FullName,
                    $"get_{nameof(CardModel.HoverTips)}"),
                postfix: new HarmonyMethod(
                    typeof(LoadoutDescriptionKeywordHoverTipsPatch),
                    nameof(LoadoutDescriptionKeywordHoverTipsPatch.Postfix)));
        }, () => DescriptionKeywordsEnabled = true);
    }

    private static void SetDescriptionKeywordPostOnPlayEnabled(bool enabled)
    {
        if (enabled == DescriptionKeywordPostOnPlayEnabled)
            return;

        DescriptionKeywordPostOnPlayEnabled = enabled;
        RefreshPostOnPlayPatch();
    }

    private static void RefreshPostOnPlayPatch()
    {
        bool enabled =
            LividEnabled || DescriptionKeywordPostOnPlayEnabled;
        if (enabled == PostOnPlayEnabled)
            return;

        if (!enabled)
        {
            PostOnPlayHarmony.UnpatchAll(PostOnPlayHarmonyId);
            PostOnPlayEnabled = false;
            return;
        }

        TryEnable(PostOnPlayHarmony, PostOnPlayHarmonyId, () =>
        {
            HarmonyMethod prefix = new(
                typeof(PostOnPlayKeywordDispatcher),
                nameof(PostOnPlayKeywordDispatcher.Prefix));
            HarmonyMethod postfix = new(
                typeof(PostOnPlayKeywordDispatcher),
                nameof(PostOnPlayKeywordDispatcher.Postfix));
            MethodBase[] targets = PostOnPlayKeywordDispatcher.TargetMethods().ToArray();
            if (targets.Length == 0)
            {
                throw new MissingMethodException(
                    typeof(CardModel).FullName,
                    "OnPlay(PlayerChoiceContext, CardPlay) implementations");
            }

            foreach (MethodBase target in targets)
                PostOnPlayHarmony.Patch(target, prefix: prefix, postfix: postfix);
        }, () => PostOnPlayEnabled = true);
    }

    private static void TryEnable(Harmony harmony, string harmonyId, Action patch, Action markEnabled)
    {
        try
        {
            patch();
            markEnabled();
        }
        catch (Exception exception)
        {
            harmony.UnpatchAll(harmonyId);
            GD.PushWarning($"Loadout keywords: failed enabling Harmony group '{harmonyId}'. {exception}");
        }
    }

    private static void PatchPrefix(Harmony harmony, MethodBase target, Type patchType, string prefix) =>
        harmony.Patch(target, prefix: new HarmonyMethod(patchType, prefix));

    private static void PatchPrefixFinalizer(
        Harmony harmony,
        MethodBase target,
        Type patchType,
        string prefix,
        string finalizer) =>
        harmony.Patch(target,
            prefix: new HarmonyMethod(patchType, prefix),
            finalizer: new HarmonyMethod(patchType, finalizer));

    private static void PatchPrefixPostfix(
        Harmony harmony,
        MethodBase target,
        Type patchType,
        string prefix,
        string postfix) =>
        harmony.Patch(target,
            prefix: new HarmonyMethod(patchType, prefix),
            postfix: new HarmonyMethod(patchType, postfix));

    private struct KeywordFeatureState
    {
        public bool InfiniteUpgrade;
        public bool XCost;
        public bool Sticky;
        public bool Passing;
        public bool Inevitable;
        public bool Livid;
        public bool DescriptionKeywords;
        public bool DescriptionKeywordPostOnPlay;
        public readonly bool All =>
            InfiniteUpgrade
            && XCost
            && Sticky
            && Passing
            && Inevitable
            && Livid
            && DescriptionKeywords
            && DescriptionKeywordPostOnPlay;
    }
}
