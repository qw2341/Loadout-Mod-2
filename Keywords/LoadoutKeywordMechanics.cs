#nullable enable

using MegaCrit.Sts2.Core.Combat;
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
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using LinqExpression = System.Linq.Expressions.Expression;

public static class LoadoutKeywordMechanics
{
    private static readonly FieldInfo? EnergyCostField = AccessTools.Field(typeof(CardModel), "_energyCost");

    public static void SynchronizeEnergyCost(CardModel card, IReadOnlyDictionary<string, bool> overrides, int? modifiedCost)
    {
        bool enabled = overrides.TryGetValue(LoadoutKeywords.XCostKey, out bool requested)
            ? requested
            : LoadoutKeywords.Has(card, LoadoutKeywords.XCost);

        CardModel? canonical = ModelDb.AllCards.FirstOrDefault(candidate => candidate.Id.Equals(card.Id));
        bool canonicalCostsX = canonical?.EnergyCost.CostsX ?? false;
        bool explicitlyDisabled = overrides.TryGetValue(LoadoutKeywords.XCostKey, out requested) && !requested;
        bool shouldCostX = enabled || (canonicalCostsX && !explicitlyDisabled);

        if (card.EnergyCost.CostsX == shouldCostX)
        {
            if (!shouldCostX && modifiedCost.HasValue)
                card.EnergyCost.SetCustomBaseCost(modifiedCost.Value);
            return;
        }

        if (EnergyCostField is null)
            throw new MissingFieldException(typeof(CardModel).FullName, "_energyCost");

        int normalCost = modifiedCost
                         ?? (canonicalCostsX ? 0 : canonical?.EnergyCost.Canonical)
                         ?? card.EnergyCost.Canonical;
        EnergyCostField.SetValue(card, new CardEnergyCost(card, shouldCostX ? 0 : normalCost, shouldCostX));
        card.InvokeEnergyCostChanged();
    }
}

internal static class LoadoutKeywordRuntimePatches
{
    private const string InfiniteHarmonyId = "Loadout.Keyword.InfiniteUpgrade";
    private const string XCostHarmonyId = "Loadout.Keyword.XCost";
    private const string StickyHarmonyId = "Loadout.Keyword.Sticky";
    private const string InevitableHarmonyId = "Loadout.Keyword.Inevitable";

    private static readonly Harmony InfiniteHarmony = new(InfiniteHarmonyId);
    private static readonly Harmony XCostHarmony = new(XCostHarmonyId);
    private static readonly Harmony StickyHarmony = new(StickyHarmonyId);
    private static readonly Harmony InevitableHarmony = new(InevitableHarmonyId);

    public static bool InfiniteUpgradeEnabled { get; private set; }
    public static bool XCostEnabled { get; private set; }
    public static bool StickyEnabled { get; private set; }
    public static bool InevitableEnabled { get; private set; }

    public static void EnableFromDelta(CardModificationDelta delta)
    {
        if (IsEnabled(delta, LoadoutKeywords.InfiniteUpgradeKey))
            SetInfiniteUpgradeEnabled(true);
        if (IsEnabled(delta, LoadoutKeywords.XCostKey))
            SetXCostEnabled(true);
        if (IsEnabled(delta, LoadoutKeywords.StickyKey))
            SetStickyEnabled(true);
        if (IsEnabled(delta, LoadoutKeywords.InevitableKey))
            SetInevitableEnabled(true);
    }

    public static void Reconcile()
    {
        KeywordFeatureState required = GetRequiredFeatures();
        SetInfiniteUpgradeEnabled(required.InfiniteUpgrade);
        SetXCostEnabled(required.XCost);
        SetStickyEnabled(required.Sticky);
        SetInevitableEnabled(required.Inevitable);
    }

    public static void ResetRunPatches()
    {
        SetInfiniteUpgradeEnabled(false);
        SetXCostEnabled(false);
        SetStickyEnabled(false);
        SetInevitableEnabled(false);
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
        state.Inevitable |= IsEnabled(delta, LoadoutKeywords.InevitableKey);
    }

    private static void AddCardFeatures(IEnumerable<CardModel> cards, ref KeywordFeatureState state)
    {
        foreach (CardModel card in cards)
        {
            state.InfiniteUpgrade |= LoadoutKeywords.Has(card, LoadoutKeywords.InfiniteUpgrade);
            state.XCost |= LoadoutKeywords.Has(card, LoadoutKeywords.XCost);
            state.Sticky |= LoadoutKeywords.Has(card, LoadoutKeywords.Sticky);
            state.Inevitable |= LoadoutKeywords.Has(card, LoadoutKeywords.Inevitable);
            if (state.All)
                return;
        }
    }

    private static bool IsEnabled(CardModificationDelta delta, string key) =>
        delta.KeywordOverrides.TryGetValue(key, out bool enabled) && enabled;

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
            PatchPrefixFinalizer(InfiniteHarmony,
                AccessTools.Method(typeof(CardModel), nameof(CardModel.FromSerializable), [typeof(SerializableCard)])!,
                typeof(InfiniteUpgradeDeserializationPatch),
                nameof(InfiniteUpgradeDeserializationPatch.Prefix),
                nameof(InfiniteUpgradeDeserializationPatch.Finalizer));
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
            return;
        }

        TryEnable(StickyHarmony, StickyHarmonyId, () =>
        {
            StickyHarmony.Patch(
                Sts2Compatibility.StickyCardPlayResultMethod,
                postfix: new HarmonyMethod(StickyPlayedCardResultPilePatch.GetPostfixMethod()));
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
        }, () => StickyEnabled = true);
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
            GD.PushWarning($"Loadout keywords: failed enabling Harmony group '{harmonyId}'. {exception.Message}");
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
        public bool Inevitable;
        public readonly bool All => InfiniteUpgrade && XCost && Sticky && Inevitable;
    }
}

public static class InfiniteUpgradeMaxLevelPatch
{
    [ThreadStatic]
    private static int _deserializingMaxLevel;

    public static void BeginDeserialization(int maxLevel)
    {
        _deserializingMaxLevel = Math.Max(_deserializingMaxLevel, maxLevel);
    }

    public static void EndDeserialization()
    {
        _deserializingMaxLevel = 0;
    }

    public static IEnumerable<MethodBase> TargetMethods()
    {
        return typeof(CardModel).Assembly
            .GetTypes()
            .Where(type => typeof(CardModel).IsAssignableFrom(type))
            .Select(type => type.GetProperty(
                nameof(CardModel.MaxUpgradeLevel),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)?.GetMethod)
            .Where(method => method is not null)
            .Distinct()!;
    }

    [HarmonyPostfix]
    public static void Postfix(CardModel __instance, ref int __result)
    {
        if (LoadoutKeywords.Has(__instance, LoadoutKeywords.InfiniteUpgrade))
        {
            __result = int.MaxValue;
            return;
        }

        __result = Math.Max(__result, Math.Max(__instance.CurrentUpgradeLevel, _deserializingMaxLevel));
    }
}

public readonly struct InfiniteUpgradeContextState
{
    public InfiniteUpgradeContextState(CardModel? activeCard, bool isApplyingNativeUpgrade)
    {
        ActiveCard = activeCard;
        IsApplyingNativeUpgrade = isApplyingNativeUpgrade;
    }

    public CardModel? ActiveCard { get; }
    public bool IsApplyingNativeUpgrade { get; }
}

public static class InfiniteUpgradeContextPatch
{
    [ThreadStatic]
    internal static CardModel? ActiveCard;

    [ThreadStatic]
    internal static bool IsApplyingNativeUpgrade;

    [HarmonyPrefix]
    public static void Prefix(CardModel __instance, out InfiniteUpgradeContextState __state)
    {
        __state = new InfiniteUpgradeContextState(ActiveCard, IsApplyingNativeUpgrade);
        ActiveCard = LoadoutKeywords.Has(__instance, LoadoutKeywords.InfiniteUpgrade)
            ? __instance
            : null;
        IsApplyingNativeUpgrade = ActiveCard is not null;
    }

    [HarmonyFinalizer]
    public static Exception? Finalizer(InfiniteUpgradeContextState __state, Exception? __exception)
    {
        ActiveCard = __state.ActiveCard;
        IsApplyingNativeUpgrade = __state.IsApplyingNativeUpgrade;
        return __exception;
    }
}

public static class InfiniteUpgradeRecalculationBoundaryPatch
{
    [HarmonyPrefix]
    public static void Prefix(DynamicVarSet __instance)
    {
        CardModel? activeCard = InfiniteUpgradeContextPatch.ActiveCard;
        if (InfiniteUpgradeContextPatch.IsApplyingNativeUpgrade
            && activeCard is not null
            && ReferenceEquals(activeCard.DynamicVars, __instance))
        {
            // UpgradeInternal has finished OnUpgrade at this point. Do not scale
            // recalculation, enchantment, or Upgraded-event mutations.
            InfiniteUpgradeContextPatch.IsApplyingNativeUpgrade = false;
        }
    }
}

public static class InfiniteUpgradeDynamicValuePatch
{
    [HarmonyPrefix]
    public static void Prefix(DynamicVar __instance, ref decimal addend)
    {
        if (!InfiniteUpgradeContextPatch.IsApplyingNativeUpgrade)
            return;

        CardModel? card = InfiniteUpgradeContextPatch.ActiveCard;
        if (card is null
            || !card.DynamicVars.Any(pair => ReferenceEquals(pair.Value, __instance)))
        {
            return;
        }

        // UpgradeInternal increments CurrentUpgradeLevel before OnUpgrade.
        // +1: native amount; +2: native + 1; +3: native + 2; etc.
        int extraValue = card.CurrentUpgradeLevel - 1;
        if (extraValue > 0)
            addend += extraValue;
    }
}

public static class InfiniteUpgradeDeserializationPatch
{
    [HarmonyPrefix]
    public static void Prefix(SerializableCard save)
    {
        InfiniteUpgradeMaxLevelPatch.BeginDeserialization(save.CurrentUpgradeLevel);
    }

    [HarmonyFinalizer]
    public static Exception? Finalizer(Exception? __exception)
    {
        InfiniteUpgradeMaxLevelPatch.EndDeserialization();
        return __exception;
    }
}

public static class XCostPlayCountPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(CardModel),
            "GeneratePlayCount",
            [typeof(MegaCrit.Sts2.Core.Combat.ICombatState), typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature)]);
    }

    [HarmonyPostfix]
    public static void Postfix(CardModel __instance, ref Task<int> __result)
    {
        if (LoadoutKeywords.Has(__instance, LoadoutKeywords.XCost))
            __result = MultiplyByXAsync(__instance, __result);
    }

    private static async Task<int> MultiplyByXAsync(CardModel card, Task<int> original)
    {
        int nativePlayCount = await original;
        int x = Math.Max(0, card.ResolveEnergyXValue());
        return checked(nativePlayCount * x);
    }
}

public static class StickyPlayedCardResultPilePatch
{
    internal static MethodInfo GetPostfixMethod()
    {
        if (!Sts2Compatibility.UsesNewCardLocation)
        {
            // 0.107-only compatibility fallback; remove or replace when 0.107 support is dropped.
            return AccessTools.Method(typeof(StickyPlayedCardResultPilePatch), nameof(LegacyPostfix))
                   ?? throw new MissingMethodException(
                       typeof(StickyPlayedCardResultPilePatch).FullName,
                       nameof(LegacyPostfix));
        }

        Type resultType = Sts2Compatibility.StickyCardPlayResultMethod.ReturnType;
        Type transformerType = typeof(StickyLocationResult<>).MakeGenericType(resultType);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(transformerType.TypeHandle);

        MethodInfo genericPostfix = AccessTools.Method(
                                        typeof(StickyPlayedCardResultPilePatch),
                                        nameof(NewPostfix))
                                    ?? throw new MissingMethodException(
                                        typeof(StickyPlayedCardResultPilePatch).FullName,
                                        nameof(NewPostfix));
        return genericPostfix.MakeGenericMethod(resultType);
    }

    // Maintained newer API path. TResult closes over CardLocation at runtime,
    // keeping that newer-only type out of the compiled assembly references.
    [HarmonyPostfix]
    public static void NewPostfix<TResult>(CardModel card, ref TResult __result)
        where TResult : struct
    {
        if (LoadoutKeywords.Has(card, LoadoutKeywords.Sticky))
            __result = StickyLocationResult<TResult>.MoveToHand(__result);
    }

    // 0.107-only compatibility fallback; remove or replace when 0.107 support is dropped.
    [HarmonyPostfix]
    public static void LegacyPostfix(CardModel card, ref ValueTuple<PileType, CardPilePosition> __result)
    {
        if (LoadoutKeywords.Has(card, LoadoutKeywords.Sticky))
            __result = (PileType.Hand, CardPilePosition.Bottom);
    }

    private static class StickyLocationResult<TResult>
        where TResult : struct
    {
        internal static readonly Func<TResult, TResult> MoveToHand = CreateMoveToHand();

        private static Func<TResult, TResult> CreateMoveToHand()
        {
            Type resultType = typeof(TResult);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            MemberInfo? playerMember = resultType.GetField("player", flags)
                                       ?? (MemberInfo?)resultType.GetProperty("player", flags)
                                       ?? resultType.GetProperty("Player", flags);
            if (playerMember is null)
                throw new MissingMemberException(resultType.FullName, "player");

            Type playerType = playerMember switch
            {
                FieldInfo field => field.FieldType,
                PropertyInfo property => property.PropertyType,
                _ => throw new InvalidOperationException($"Unsupported player member on {resultType.FullName}.")
            };

            ConstructorInfo constructor = resultType.GetConstructor(
                                              flags,
                                              binder: null,
                                              [playerType, typeof(PileType), typeof(CardPilePosition)],
                                              modifiers: null)
                                          ?? throw new MissingMethodException(
                                              resultType.FullName,
                                              ".ctor(Player, PileType, CardPilePosition)");

            ParameterExpression current = LinqExpression.Parameter(resultType, "current");
            System.Linq.Expressions.Expression player = playerMember switch
            {
                FieldInfo field => LinqExpression.Field(current, field),
                PropertyInfo property => LinqExpression.Property(current, property),
                _ => throw new InvalidOperationException()
            };
            NewExpression replacement = LinqExpression.New(
                constructor,
                player,
                LinqExpression.Constant(PileType.Hand),
                LinqExpression.Constant(CardPilePosition.Bottom));
            return LinqExpression.Lambda<Func<TResult, TResult>>(replacement, current).Compile();
        }
    }
}

public static class StickyDiscardPatch
{
    [HarmonyPrefix]
    public static void Prefix(
        ref IEnumerable<CardModel> cardsToDiscard,
        out List<CardModel>? __state)
    {
        // Do not remove Sticky cards from the native discard operation.
        // The game must see them so that discard history, hooks, and Sly work.
        IReadOnlyList<CardModel> cards;

        if (cardsToDiscard is IReadOnlyList<CardModel> readOnlyList)
        {
            cards = readOnlyList;
        }
        else
        {
            List<CardModel> materialized = cardsToDiscard.ToList();
            cardsToDiscard = materialized;
            cards = materialized;
        }

        __state = null;

        for (int i = 0; i < cards.Count; i++)
        {
            CardModel card = cards[i];

            if (!LoadoutKeywords.Has(card, LoadoutKeywords.Sticky))
                continue;

            (__state ??= new List<CardModel>(1)).Add(card);
        }
    }

    [HarmonyPostfix]
    public static void Postfix(
        ref Task __result,
        List<CardModel>? __state)
    {
        if (__state is not { Count: > 0 })
            return;

        __result = ReturnStickyCardsAfterDiscard(__result, __state);
    }

    private static async Task ReturnStickyCardsAfterDiscard(
        Task originalDiscard,
        IReadOnlyList<CardModel> stickyCards)
    {
        // Wait for the entire native discard sequence:
        await originalDiscard;

        List<CardModel>? cardsToReturn = null;

        for (int i = 0; i < stickyCards.Count; i++)
        {
            CardModel card = stickyCards[i];

            // A Sticky + Sly card will normally already be back in hand
            // because Sly auto-plays it and Sticky changes its result pile.
            //
            // Only return cards that remain in the discard pile.
            if (card.Pile?.Type != PileType.Discard)
                continue;

            (cardsToReturn ??= new List<CardModel>(stickyCards.Count))
                .Add(card);
        }

        if (cardsToReturn is null)
            return;

        await Sts2Compatibility.AddCards(
            cardsToReturn,
            PileType.Hand,
            CardPilePosition.Bottom);
    }
}


public static class StickyFlushPlayerHandPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
                   typeof(CombatManager),
                   "FlushPlayerHand",
                   [
                       typeof(Player),
                       typeof(HookPlayerChoiceContext)
                   ])
               ?? throw new MissingMethodException(
                   typeof(CombatManager).FullName,
                   "FlushPlayerHand(Player, HookPlayerChoiceContext)");
    }

    [HarmonyPrefix]
    public static void Prefix(
        Player player,
        out List<CardModel> __state)
    {
        __state = PileType.Hand
            .GetPile(player)
            .Cards
            .Where(card =>
                LoadoutKeywords.Has(card, LoadoutKeywords.Sticky))
            .ToList();
    }

    [HarmonyPostfix]
    public static void Postfix(
        ref Task __result,
        List<CardModel> __state)
    {
        if (__state.Count == 0)
            return;
        __result = ReturnStickyCardsAfterFlush(__result, __state);
    }

    private static async Task ReturnStickyCardsAfterFlush(
        Task originalFlush,
        IReadOnlyList<CardModel> stickyCards)
    {
        await originalFlush;

        List<CardModel> cardsToReturn = stickyCards
            .Where(card => card.Pile?.Type == PileType.Discard)
            .ToList();

        if (cardsToReturn.Count == 0)
            return;
        
        await Sts2Compatibility.AddCards(
            cardsToReturn,
            PileType.Hand,
            CardPilePosition.Bottom);
    }
}

public static class InevitableExhaustPatch
{
    [HarmonyPostfix]
    public static void Postfix(
        CardModel card,
        ref Task __result)
    {
        if (!LoadoutKeywords.Has(card, LoadoutKeywords.Inevitable))
            return;

        __result = AddCopyToHandAfterExhaust(__result, card);
    }

    private static async Task AddCopyToHandAfterExhaust(
        Task originalExhaust,
        CardModel exhaustedCard)
    {
        
        await originalExhaust;

        // Do not produce a copy if another exhaust hook already moved or
        // removed the original card.
        if (exhaustedCard.Pile?.Type != PileType.Exhaust)
            return;
        
        CardModel copy = exhaustedCard.CreateClone();

        await CardPileCmd.AddGeneratedCardToCombat(
            copy,
            PileType.Hand,
            exhaustedCard.Owner,
            CardPilePosition.Bottom);
    }
}

public static class InevitableTransformPatch
{
    [HarmonyPrefix]
    public static void Prefix(ref IEnumerable<CardTransformation> transformations)
    {
        List<CardTransformation> rewritten = [];
        foreach (CardTransformation transformation in transformations)
        {
            if (!LoadoutKeywords.Has(transformation.Original, LoadoutKeywords.Inevitable))
            {
                rewritten.Add(transformation);
                continue;
            }

            if (transformation.Replacement is { IsCanonical: false } discardedReplacement)
                discardedReplacement.CardScope!.RemoveCard(discardedReplacement);

            CardModel replacement = transformation.Original.CardScope!.CloneCard(transformation.Original);
            rewritten.Add(new CardTransformation(transformation.Original, replacement));
        }

        transformations = rewritten;
    }
}
