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
using Loadout.Patches;
using Loadout.Patches.Cards.CardModification;
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
    private const string TurnEndInHandHarmonyId =
        "Loadout.Keyword.TurnEndInHand";
    private const string PlayRestrictionHarmonyId =
        "Loadout.Keyword.PlayRestriction";
    private const string BlankSlateHooksHarmonyId =
        "Loadout.Keyword.BlankSlateHooks";

    private static readonly Harmony InfiniteHarmony = new(InfiniteHarmonyId);
    private static readonly Harmony XCostHarmony = new(XCostHarmonyId);
    private static readonly Harmony StickyHarmony = new(StickyHarmonyId);
    private static readonly Harmony CardResultHarmony = new(CardResultHarmonyId);
    private static readonly Harmony InevitableHarmony = new(InevitableHarmonyId);
    private static readonly Harmony DescriptionKeywordHarmony =
        new(DescriptionKeywordHarmonyId);
    private static readonly Harmony PostOnPlayHarmony = new(PostOnPlayHarmonyId);
    private static readonly Harmony TurnEndInHandHarmony =
        new(TurnEndInHandHarmonyId);
    private static readonly Harmony PlayRestrictionHarmony =
        new(PlayRestrictionHarmonyId);
    private static readonly Harmony BlankSlateHooksHarmony =
        new(BlankSlateHooksHarmonyId);

    public static bool InfiniteUpgradeEnabled { get; private set; }
    public static bool XCostEnabled { get; private set; }
    public static bool StickyEnabled { get; private set; }
    public static bool PassingEnabled { get; private set; }
    public static bool ParticleEnabled { get; private set; }
    public static bool InevitableEnabled { get; private set; }
    public static bool LividEnabled { get; private set; }
    public static bool DescriptionKeywordsEnabled { get; private set; }
    private static bool DescriptionKeywordOnPlayEnabled { get; set; }
    private static bool CardResultLocationEnabled { get; set; }
    private static bool PostOnPlayEnabled { get; set; }
    private static bool TurnEndInHandEnabled { get; set; }
    private static bool PlayRestrictionEnabled { get; set; }
    private static bool BlankSlateHooksEnabled { get; set; }
    private static bool RunKeywordPatchesPrepared { get; set; }

    public static void EnableFromDelta(CardModificationDelta delta)
    {
        if (IsEnabled(delta, LoadoutKeywords.InfiniteUpgradeKey)
            || IsEnabled(delta, LoadoutKeywords.JokeInfiniteUpgradeKey))
            SetInfiniteUpgradeEnabled(true);
        if (IsEnabled(delta, LoadoutKeywords.XCostKey))
            SetXCostEnabled(true);
        if (IsEnabled(delta, LoadoutKeywords.StickyKey))
            SetStickyEnabled(true);
        if (IsEnabled(delta, LoadoutKeywords.PassingKey))
            SetPassingEnabled(true);
        if (IsEnabled(delta, LoadoutKeywords.ParticleKey))
            SetParticleEnabled(true);
        if (IsEnabled(delta, LoadoutKeywords.InevitableKey))
            SetInevitableEnabled(true);
        if (IsEnabled(delta, LoadoutKeywords.GetStorageKey(LoadoutKeywords.Livid)))
            SetLividEnabled(true);
        EnableDescriptionKeywordsFromOverrides(delta.KeywordOverrides);
        EnableFromOverrides(delta.UpgradeModification.KeywordOverrides);
        EnableFromPowerKeywordEntries(
            delta.PowerKeywordEntries,
            delta.UpgradeModification.PowerKeywordEntryUpgrades,
            delta.UpgradeModification.AddedPowerKeywordEntries);
        EnableFromCardKeywordEntries(
            delta.CardKeywordEntries,
            delta.UpgradeModification.CardKeywordEntryUpgrades,
            delta.UpgradeModification.AddedCardKeywordEntries);
    }

    public static void EnableFromPowerKeywordEntries(
        IReadOnlyList<LoadoutPowerKeywordEntry>? baseEntries,
        IReadOnlyList<LoadoutPowerKeywordEntryUpgrade>? entryUpgrades,
        IReadOnlyList<LoadoutPowerKeywordEntry>? addedEntries)
    {
        KeywordFeatureState state = default;
        AddPowerKeywordFeatures(baseEntries, ref state);
        AddPowerKeywordUpgradeFeatures(entryUpgrades, ref state);
        AddPowerKeywordFeatures(addedEntries, ref state);
        if (state.RepeatableKeywords)
            CardUpgradeModificationRuntimePatches.Enable();
        if (state.DescriptionKeywords)
            SetDescriptionKeywordsEnabled(true);
        if (state.DescriptionKeywordOnPlay)
            SetDescriptionKeywordOnPlayEnabled(true);
        if (state.TurnEndInHand)
            SetTurnEndInHandEnabled(true);
        if (state.PlayRestriction)
            SetPlayRestrictionEnabled(true);
        if (state.BlankSlateHooks)
            SetBlankSlateHooksEnabled(true);
    }

    public static void EnableFromCardKeywordEntries(
        IReadOnlyList<LoadoutCardKeywordEntry>? baseEntries,
        IReadOnlyList<LoadoutCardKeywordEntryUpgrade>? entryUpgrades,
        IReadOnlyList<LoadoutCardKeywordEntry>? addedEntries)
    {
        KeywordFeatureState state = default;
        AddCardKeywordFeatures(baseEntries, ref state);
        AddCardKeywordUpgradeFeatures(entryUpgrades, ref state);
        AddCardKeywordFeatures(addedEntries, ref state);
        if (state.RepeatableKeywords)
            CardUpgradeModificationRuntimePatches.Enable();
        if (state.DescriptionKeywords)
            SetDescriptionKeywordsEnabled(true);
        if (state.DescriptionKeywordOnPlay)
            SetDescriptionKeywordOnPlayEnabled(true);
        if (state.TurnEndInHand)
            SetTurnEndInHandEnabled(true);
        if (state.PlayRestriction)
            SetPlayRestrictionEnabled(true);
        if (state.BlankSlateHooks)
            SetBlankSlateHooksEnabled(true);
    }

    public static void EnableFromOverrides(IReadOnlyDictionary<string, bool> overrides)
    {
        if (IsEnabled(overrides, LoadoutKeywords.InfiniteUpgradeKey)
            || IsEnabled(overrides, LoadoutKeywords.JokeInfiniteUpgradeKey))
            SetInfiniteUpgradeEnabled(true);
        if (IsEnabled(overrides, LoadoutKeywords.XCostKey))
            SetXCostEnabled(true);
        if (IsEnabled(overrides, LoadoutKeywords.StickyKey))
            SetStickyEnabled(true);
        if (IsEnabled(overrides, LoadoutKeywords.PassingKey))
            SetPassingEnabled(true);
        if (IsEnabled(overrides, LoadoutKeywords.ParticleKey))
            SetParticleEnabled(true);
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

    public static bool? GetInfiniteUpgradeOverride(
        CardUpgradeModificationSpec? modification)
    {
        return modification?.KeywordOverrides.TryGetValue(
            LoadoutKeywords.InfiniteUpgradeKey,
            out bool enabled) == true
            ? enabled
            : null;
    }

    public static bool? GetJokeInfiniteUpgradeOverride(
        CardModificationSpec? state)
    {
        return state?.KeywordOverrides.TryGetValue(
            LoadoutKeywords.JokeInfiniteUpgradeKey,
            out bool enabled) == true
            ? enabled
            : null;
    }

    public static bool? GetJokeInfiniteUpgradeOverride(
        CardModificationDelta? delta)
    {
        return delta?.KeywordOverrides.TryGetValue(
            LoadoutKeywords.JokeInfiniteUpgradeKey,
            out bool enabled) == true
            ? enabled
            : null;
    }

    public static bool? GetJokeInfiniteUpgradeOverride(
        CardUpgradeModificationSpec? modification)
    {
        return modification?.KeywordOverrides.TryGetValue(
            LoadoutKeywords.JokeInfiniteUpgradeKey,
            out bool enabled) == true
            ? enabled
            : null;
    }

    public static bool? ResolveEffectiveInfiniteUpgrade(
        CardModificationSpec? permanent,
        CardModificationDelta? temporary,
        CardModificationSpec? legacyTemporary = null)
    {
        return GetInfiniteUpgradeOverride(temporary)
               ?? GetInfiniteUpgradeOverride(legacyTemporary)
               ?? GetInfiniteUpgradeOverride(permanent);
    }

    public static bool? ResolveEffectiveJokeInfiniteUpgrade(
        CardModificationSpec? permanent,
        CardModificationDelta? temporary,
        CardModificationSpec? legacyTemporary = null)
    {
        return GetJokeInfiniteUpgradeOverride(temporary)
               ?? GetJokeInfiniteUpgradeOverride(legacyTemporary)
               ?? GetJokeInfiniteUpgradeOverride(permanent);
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
        if (required.RepeatableKeywords)
            CardUpgradeModificationRuntimePatches.Enable();
        SetInfiniteUpgradeEnabled(required.InfiniteUpgrade);
        SetXCostEnabled(required.XCost);
        SetStickyEnabled(required.Sticky);
        SetPassingEnabled(required.Passing);
        SetParticleEnabled(required.Particle);
        SetInevitableEnabled(required.Inevitable);
        SetLividEnabled(required.Livid);
        SetDescriptionKeywordsEnabled(
            RunKeywordPatchesPrepared || required.DescriptionKeywords);
        SetDescriptionKeywordOnPlayEnabled(
            required.DescriptionKeywordOnPlay);
        SetTurnEndInHandEnabled(required.TurnEndInHand);
        SetPlayRestrictionEnabled(required.PlayRestriction);
        SetBlankSlateHooksEnabled(required.BlankSlateHooks);
    }

    public static void ResetRunPatches()
    {
        SetInfiniteUpgradeEnabled(false);
        SetXCostEnabled(false);
        SetStickyEnabled(false);
        SetPassingEnabled(false);
        SetParticleEnabled(false);
        SetInevitableEnabled(false);
        SetLividEnabled(false);
        RunKeywordPatchesPrepared = false;
        SetDescriptionKeywordsEnabled(false);
        SetDescriptionKeywordOnPlayEnabled(false);
        SetTurnEndInHandEnabled(false);
        SetPlayRestrictionEnabled(false);
        SetBlankSlateHooksEnabled(false);
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
        state.InfiniteUpgrade |= IsEnabled(
            delta,
            LoadoutKeywords.JokeInfiniteUpgradeKey);
        state.XCost |= IsEnabled(delta, LoadoutKeywords.XCostKey);
        state.Sticky |= IsEnabled(delta, LoadoutKeywords.StickyKey);
        state.Passing |= IsEnabled(delta, LoadoutKeywords.PassingKey);
        state.Particle |= IsEnabled(delta, LoadoutKeywords.ParticleKey);
        state.Inevitable |= IsEnabled(delta, LoadoutKeywords.InevitableKey);
        state.Livid |= IsEnabled(delta, LoadoutKeywords.GetStorageKey(LoadoutKeywords.Livid));
        AddDescriptionKeywordFeatures(delta.KeywordOverrides, ref state);
        state.InfiniteUpgrade |= IsEnabled(
            delta.UpgradeModification.KeywordOverrides,
            LoadoutKeywords.InfiniteUpgradeKey);
        state.InfiniteUpgrade |= IsEnabled(
            delta.UpgradeModification.KeywordOverrides,
            LoadoutKeywords.JokeInfiniteUpgradeKey);
        state.XCost |= IsEnabled(
            delta.UpgradeModification.KeywordOverrides,
            LoadoutKeywords.XCostKey);
        state.Sticky |= IsEnabled(
            delta.UpgradeModification.KeywordOverrides,
            LoadoutKeywords.StickyKey);
        state.Passing |= IsEnabled(
            delta.UpgradeModification.KeywordOverrides,
            LoadoutKeywords.PassingKey);
        state.Particle |= IsEnabled(
            delta.UpgradeModification.KeywordOverrides,
            LoadoutKeywords.ParticleKey);
        state.Inevitable |= IsEnabled(
            delta.UpgradeModification.KeywordOverrides,
            LoadoutKeywords.InevitableKey);
        state.Livid |= IsEnabled(
            delta.UpgradeModification.KeywordOverrides,
            LoadoutKeywords.GetStorageKey(LoadoutKeywords.Livid));
        AddDescriptionKeywordFeatures(
            delta.UpgradeModification.KeywordOverrides,
            ref state);
        AddPowerKeywordFeatures(delta.PowerKeywordEntries, ref state);
        AddCardKeywordFeatures(delta.CardKeywordEntries, ref state);
        AddPowerKeywordUpgradeFeatures(
            delta.UpgradeModification.PowerKeywordEntryUpgrades,
            ref state);
        AddPowerKeywordFeatures(
            delta.UpgradeModification.AddedPowerKeywordEntries,
            ref state);
        AddCardKeywordUpgradeFeatures(
            delta.UpgradeModification.CardKeywordEntryUpgrades,
            ref state);
        AddCardKeywordFeatures(
            delta.UpgradeModification.AddedCardKeywordEntries,
            ref state);
    }

    private static void AddPowerKeywordFeatures(
        IReadOnlyList<LoadoutPowerKeywordEntry>? entries,
        ref KeywordFeatureState state)
    {
        if (entries is null)
            return;

        foreach (LoadoutPowerKeywordEntry entry in entries)
        {
            if (!LoadoutKeywords.TryResolve(entry.KeywordKey, out CardKeyword keyword)
                || !LoadoutKeywordRegistry.TryGet(keyword, out LoadoutKeywordModel model)
                || model is not LoadoutPowerKeywordModel)
            {
                continue;
            }

            state.DescriptionKeywords = true;
            state.RepeatableKeywords = true;
            state.DescriptionKeywordOnPlay |=
                model.HasOnPlayEffect || model.SuppressesOriginalOnPlay;
            state.TurnEndInHand |= model.HasTurnEndInHandEffect;
            state.PlayRestriction |= RequiresCardLogicPatch(model);
            state.BlankSlateHooks |= model.SuppressesOriginalModelHooks;
        }
    }

    private static void AddCardKeywordFeatures(
        IReadOnlyList<LoadoutCardKeywordEntry>? entries,
        ref KeywordFeatureState state)
    {
        if (entries is null)
            return;

        foreach (LoadoutCardKeywordEntry entry in entries)
        {
            if (!LoadoutKeywords.TryResolve(entry.KeywordKey, out CardKeyword keyword)
                || !LoadoutKeywordRegistry.TryGet(keyword, out LoadoutKeywordModel model)
                || model is not LoadoutCardKeywordModel)
            {
                continue;
            }

            state.DescriptionKeywords = true;
            state.RepeatableKeywords = true;
            state.DescriptionKeywordOnPlay |=
                model.HasOnPlayEffect || model.SuppressesOriginalOnPlay;
            state.TurnEndInHand |= model.HasTurnEndInHandEffect;
            state.PlayRestriction |= RequiresCardLogicPatch(model);
            state.BlankSlateHooks |= model.SuppressesOriginalModelHooks;
        }
    }

    private static void AddPowerKeywordUpgradeFeatures(
        IReadOnlyList<LoadoutPowerKeywordEntryUpgrade>? entries,
        ref KeywordFeatureState state)
    {
        if (entries is null)
            return;

        foreach (LoadoutPowerKeywordEntryUpgrade entry in entries)
            AddPowerKeywordFeature(entry.KeywordKey, ref state);
    }

    private static void AddCardKeywordUpgradeFeatures(
        IReadOnlyList<LoadoutCardKeywordEntryUpgrade>? entries,
        ref KeywordFeatureState state)
    {
        if (entries is null)
            return;

        foreach (LoadoutCardKeywordEntryUpgrade entry in entries)
            AddCardKeywordFeature(entry.KeywordKey, ref state);
    }

    private static void AddPowerKeywordFeature(
        string keywordKey,
        ref KeywordFeatureState state)
    {
        if (!LoadoutKeywords.TryResolve(keywordKey, out CardKeyword keyword)
            || !LoadoutKeywordRegistry.TryGet(keyword, out LoadoutKeywordModel model)
            || model is not LoadoutPowerKeywordModel powerModel)
        {
            return;
        }

        state.DescriptionKeywords = true;
        state.RepeatableKeywords = true;
        state.DescriptionKeywordOnPlay |=
            powerModel.HasOnPlayEffect || powerModel.SuppressesOriginalOnPlay;
        state.TurnEndInHand |= powerModel.HasTurnEndInHandEffect;
        state.PlayRestriction |= RequiresCardLogicPatch(powerModel);
        state.BlankSlateHooks |= powerModel.SuppressesOriginalModelHooks;
    }

    private static void AddCardKeywordFeature(
        string keywordKey,
        ref KeywordFeatureState state)
    {
        if (!LoadoutKeywords.TryResolve(keywordKey, out CardKeyword keyword)
            || !LoadoutKeywordRegistry.TryGet(keyword, out LoadoutKeywordModel model)
            || model is not LoadoutCardKeywordModel cardModel)
        {
            return;
        }

        state.DescriptionKeywords = true;
        state.RepeatableKeywords = true;
        state.DescriptionKeywordOnPlay |=
            cardModel.HasOnPlayEffect || cardModel.SuppressesOriginalOnPlay;
        state.TurnEndInHand |= cardModel.HasTurnEndInHandEffect;
        state.PlayRestriction |= RequiresCardLogicPatch(cardModel);
        state.BlankSlateHooks |= cardModel.SuppressesOriginalModelHooks;
    }

    private static void AddCardFeatures(IEnumerable<CardModel> cards, ref KeywordFeatureState state)
    {
        foreach (CardModel card in cards)
        {
            LoadoutKeywordRegistry.SynchronizeOriginalModelHookSuppression(card);
            state.InfiniteUpgrade |=
                LoadoutKeywords.Has(card, LoadoutKeywords.InfiniteUpgrade)
                || LoadoutKeywords.Has(
                    card,
                    LoadoutKeywords.JokeInfiniteUpgrade);
            state.XCost |= LoadoutKeywords.Has(card, LoadoutKeywords.XCost);
            state.Sticky |= LoadoutKeywords.Has(card, LoadoutKeywords.Sticky);
            state.Passing |= LoadoutKeywords.Has(card, LoadoutKeywords.Passing);
            state.Particle |= LoadoutKeywords.Has(card, LoadoutKeywords.Particle);
            state.Inevitable |= LoadoutKeywords.Has(card, LoadoutKeywords.Inevitable);
            state.Livid |= LoadoutKeywords.Has(card, LoadoutKeywords.Livid);
            foreach (LoadoutKeywordModel model in LoadoutKeywordRegistry.DescriptionOnly)
            {
                if (!model.IsEnabled(card))
                    continue;

                state.DescriptionKeywords = true;
                state.RepeatableKeywords |= model is LoadoutPowerKeywordModel or LoadoutCardKeywordModel;
                state.DescriptionKeywordOnPlay |=
                    model.HasOnPlayEffect || model.SuppressesOriginalOnPlay;
                state.TurnEndInHand |= model.HasTurnEndInHandEffect;
                state.PlayRestriction |= RequiresCardLogicPatch(model);
                state.BlankSlateHooks |= model.SuppressesOriginalModelHooks;
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
            state.DescriptionKeywordOnPlay |=
                model.HasOnPlayEffect || model.SuppressesOriginalOnPlay;
            state.TurnEndInHand |= model.HasTurnEndInHandEffect;
            state.PlayRestriction |= RequiresCardLogicPatch(model);
            state.BlankSlateHooks |= model.SuppressesOriginalModelHooks;
        }
    }

    private static void EnableDescriptionKeywordsFromOverrides(
        IReadOnlyDictionary<string, bool> overrides)
    {
        bool anyEnabled = false;
        bool anyOnPlayEnabled = false;
        bool anyTurnEndInHandEnabled = false;
        bool anyPlayRestrictionEnabled = false;
        bool anyBlankSlateHooksEnabled = false;
        foreach (LoadoutKeywordModel model in LoadoutKeywordRegistry.DescriptionOnly)
        {
            if (!IsEnabled(overrides, model.StorageKey))
                continue;

            anyEnabled = true;
            anyOnPlayEnabled |=
                model.HasOnPlayEffect || model.SuppressesOriginalOnPlay;
            anyTurnEndInHandEnabled |= model.HasTurnEndInHandEffect;
            anyPlayRestrictionEnabled |= RequiresCardLogicPatch(model);
            anyBlankSlateHooksEnabled |= model.SuppressesOriginalModelHooks;
        }

        if (anyEnabled)
            SetDescriptionKeywordsEnabled(true);
        if (anyOnPlayEnabled)
            SetDescriptionKeywordOnPlayEnabled(true);
        if (anyTurnEndInHandEnabled)
            SetTurnEndInHandEnabled(true);
        if (anyPlayRestrictionEnabled)
            SetPlayRestrictionEnabled(true);
        if (anyBlankSlateHooksEnabled)
            SetBlankSlateHooksEnabled(true);
    }

    private static bool IsEnabled(CardModificationDelta delta, string key) =>
        IsEnabled(delta.KeywordOverrides, key);

    private static bool RequiresCardLogicPatch(LoadoutKeywordModel model) =>
        model.HasPlayRestriction
        || model.SuppressesOriginalIsPlayable
        || model.SuppressesOriginalShouldGlowGold;

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
        {
            HarmonyMethod prefix = new(
                typeof(XCostOnPlayPatch),
                nameof(XCostOnPlayPatch.Prefix));
            HarmonyMethod postfix = new(
                typeof(XCostOnPlayPatch),
                nameof(XCostOnPlayPatch.Postfix))
            {
                before = [PostOnPlayHarmonyId]
            };
            MethodBase[] targets = XCostOnPlayPatch.TargetMethods().ToArray();
            if (targets.Length == 0)
            {
                throw new MissingMethodException(
                    typeof(CardModel).FullName,
                    "OnPlay(PlayerChoiceContext, CardPlay) implementations");
            }

            foreach (MethodBase target in targets)
                XCostHarmony.Patch(target, prefix: prefix, postfix: postfix);
        }, () => XCostEnabled = true);
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

    private static void SetParticleEnabled(bool enabled)
    {
        if (enabled == ParticleEnabled)
            return;

        ParticleEnabled = enabled;
        RefreshCardResultLocationPatch();
    }

    private static void RefreshCardResultLocationPatch()
    {
        bool enabled = StickyEnabled || PassingEnabled || ParticleEnabled;
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
            LocStringModificationDispatcher.EnsureInstalled();
            MethodBase descriptionTarget =
                LoadoutKeywordModel.GetDescriptionTarget();
            DescriptionKeywordHarmony.Patch(
                descriptionTarget,
                prefix: new HarmonyMethod(
                    typeof(LoadoutDescriptionKeywordPatch),
                    nameof(LoadoutDescriptionKeywordPatch.Prefix)),
                postfix: new HarmonyMethod(
                    typeof(LoadoutDescriptionKeywordPatch),
                    nameof(LoadoutDescriptionKeywordPatch.Postfix)),
                finalizer: new HarmonyMethod(
                    typeof(LoadoutDescriptionKeywordPatch),
                    nameof(LoadoutDescriptionKeywordPatch.Finalizer)));
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
            HarmonyMethod targetTypePostfix = new(
                typeof(LoadoutBasicKeywordTargetTypePatch),
                nameof(LoadoutBasicKeywordTargetTypePatch.Postfix));
            foreach (MethodBase target in
                     LoadoutBasicKeywordTargetTypePatch.TargetMethods())
            {
                DescriptionKeywordHarmony.Patch(
                    target,
                    postfix: targetTypePostfix);
            }

            HarmonyMethod gainsBlockPostfix = new(
                typeof(LoadoutBasicKeywordGainsBlockPatch),
                nameof(LoadoutBasicKeywordGainsBlockPatch.Postfix));
            foreach (MethodBase target in
                     LoadoutBasicKeywordGainsBlockPatch.TargetMethods())
            {
                DescriptionKeywordHarmony.Patch(
                    target,
                    postfix: gainsBlockPostfix);
            }
        }, () => DescriptionKeywordsEnabled = true);
    }

    private static void SetDescriptionKeywordOnPlayEnabled(bool enabled)
    {
        if (enabled == DescriptionKeywordOnPlayEnabled)
            return;

        DescriptionKeywordOnPlayEnabled = enabled;
        RefreshPostOnPlayPatch();
    }

    private static void SetTurnEndInHandEnabled(bool enabled)
    {
        if (enabled == TurnEndInHandEnabled)
            return;

        if (!enabled)
        {
            TurnEndInHandHarmony.UnpatchAll(TurnEndInHandHarmonyId);
            TurnEndInHandEnabled = false;
            return;
        }

        TryEnable(TurnEndInHandHarmony, TurnEndInHandHarmonyId, () =>
        {
            HarmonyMethod hasEffectPostfix = new(
                typeof(RestrictiveHasTurnEndInHandEffectPatch),
                nameof(RestrictiveHasTurnEndInHandEffectPatch.Postfix));
            foreach (MethodBase target in
                     RestrictiveHasTurnEndInHandEffectPatch.TargetMethods())
            {
                TurnEndInHandHarmony.Patch(
                    target,
                    postfix: hasEffectPostfix);
            }

            HarmonyMethod effectPostfix = new(
                typeof(TurnEndInHandKeywordDispatcher),
                nameof(TurnEndInHandKeywordDispatcher.Postfix));
            MethodBase[] targets =
                TurnEndInHandKeywordDispatcher.TargetMethods().ToArray();
            if (targets.Length == 0)
            {
                throw new MissingMethodException(
                    typeof(CardModel).FullName,
                    "OnTurnEndInHand(PlayerChoiceContext) implementations");
            }

            foreach (MethodBase target in targets)
                TurnEndInHandHarmony.Patch(target, postfix: effectPostfix);
        }, () => TurnEndInHandEnabled = true);
    }

    private static void SetPlayRestrictionEnabled(bool enabled)
    {
        if (enabled == PlayRestrictionEnabled)
            return;

        if (!enabled)
        {
            PlayRestrictionHarmony.UnpatchAll(PlayRestrictionHarmonyId);
            PlayRestrictionEnabled = false;
            return;
        }

        TryEnable(PlayRestrictionHarmony, PlayRestrictionHarmonyId, () =>
        {
            HarmonyMethod isPlayablePrefix = new(
                typeof(BlankSlateCardLogicPatch),
                nameof(BlankSlateCardLogicPatch.IsPlayablePrefix));
            HarmonyMethod isPlayablePostfix = new(
                typeof(RestrictiveIsPlayablePatch),
                nameof(RestrictiveIsPlayablePatch.Postfix));
            foreach (MethodBase target in
                     RestrictiveIsPlayablePatch.TargetMethods())
            {
                PlayRestrictionHarmony.Patch(
                    target,
                    prefix: isPlayablePrefix,
                    postfix: isPlayablePostfix);
            }

            HarmonyMethod shouldGlowGoldPrefix = new(
                typeof(BlankSlateCardLogicPatch),
                nameof(BlankSlateCardLogicPatch.ShouldGlowGoldPrefix));
            foreach (MethodBase target in
                     BlankSlateCardLogicPatch.ShouldGlowGoldTargets())
            {
                PlayRestrictionHarmony.Patch(
                    target,
                    prefix: shouldGlowGoldPrefix);
            }

            HarmonyMethod enthralledPostfix = new(
                typeof(RestrictiveEnthralledShouldPlayPatch),
                nameof(RestrictiveEnthralledShouldPlayPatch.Postfix));
            foreach (MethodBase target in
                     RestrictiveEnthralledShouldPlayPatch.TargetMethods())
            {
                PlayRestrictionHarmony.Patch(
                    target,
                    postfix: enthralledPostfix);
            }

            PlayRestrictionHarmony.Patch(
                AccessTools.PropertyGetter(
                    typeof(CardModel),
                    nameof(CardModel.ShouldGlowGold))
                ?? throw new MissingMethodException(
                    typeof(CardModel).FullName,
                    $"get_{nameof(CardModel.ShouldGlowGold)}"),
                postfix: new HarmonyMethod(
                    typeof(RestrictiveKeywordGlowPatch),
                    nameof(RestrictiveKeywordGlowPatch.ShouldGlowGoldPostfix)));
            PlayRestrictionHarmony.Patch(
                AccessTools.PropertyGetter(
                    typeof(CardModel),
                    nameof(CardModel.ShouldGlowRed))
                ?? throw new MissingMethodException(
                    typeof(CardModel).FullName,
                    $"get_{nameof(CardModel.ShouldGlowRed)}"),
                postfix: new HarmonyMethod(
                    typeof(RestrictiveKeywordGlowPatch),
                    nameof(RestrictiveKeywordGlowPatch.ShouldGlowRedPostfix)));
        }, () => PlayRestrictionEnabled = true);
    }

    private static void SetBlankSlateHooksEnabled(bool enabled)
    {
        if (enabled == BlankSlateHooksEnabled)
            return;

        if (!enabled)
        {
            BlankSlateHooksHarmony.UnpatchAll(BlankSlateHooksHarmonyId);
            BlankSlateModelHookPatch.ClearPrefixes();
            BlankSlateModelHookState.Reset();
            BlankSlateHooksEnabled = false;
            return;
        }

        TryEnable(BlankSlateHooksHarmony, BlankSlateHooksHarmonyId, () =>
        {
            BlankSlateHooksHarmony.Patch(
                AccessTools.Method(
                    typeof(AbstractModel),
                    nameof(AbstractModel.MutableClone))
                ?? throw new MissingMethodException(
                    typeof(AbstractModel).FullName,
                    nameof(AbstractModel.MutableClone)),
                postfix: new HarmonyMethod(
                    typeof(BlankSlateModelHookState),
                    nameof(BlankSlateModelHookState.MutableClonePostfix)));

            BlankSlateModelHookPatch.ClearPrefixes();
            MethodBase[] targets = BlankSlateModelHookPatch
                .TargetMethods()
                .ToArray();
            if (targets.Length == 0)
            {
                throw new MissingMethodException(
                    typeof(AbstractModel).FullName,
                    "concrete card hook overrides");
            }

            HarmonyMethod prefixFactory = new(
                typeof(BlankSlateModelHookPatch),
                nameof(BlankSlateModelHookPatch.PrefixFactory))
            {
                priority = Priority.Last
            };
            foreach (MethodBase target in targets)
                BlankSlateHooksHarmony.Patch(target, prefix: prefixFactory);
        }, () => BlankSlateHooksEnabled = true);
    }

    private static void RefreshPostOnPlayPatch()
    {
        bool enabled =
            LividEnabled || DescriptionKeywordOnPlayEnabled;
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
        public bool Particle;
        public bool Inevitable;
        public bool Livid;
        public bool RepeatableKeywords;
        public bool DescriptionKeywords;
        public bool DescriptionKeywordOnPlay;
        public bool TurnEndInHand;
        public bool PlayRestriction;
        public bool BlankSlateHooks;
        public readonly bool All =>
            InfiniteUpgrade
            && XCost
            && Sticky
            && Passing
            && Particle
            && Inevitable
            && Livid
            && RepeatableKeywords
            && DescriptionKeywords
            && DescriptionKeywordOnPlay
            && TurnEndInHand
            && PlayRestriction
            && BlankSlateHooks;
    }
}
