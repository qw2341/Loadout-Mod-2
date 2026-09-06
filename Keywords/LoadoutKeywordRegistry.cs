#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using Loadout.PanelItems;
using Loadout.Services.CardModification;

public static class LoadoutKeywordRegistry
{
    private static readonly FieldInfo DynamicVarDictionaryField =
        AccessTools.Field(typeof(DynamicVarSet), "_vars")
        ?? throw new MissingFieldException(typeof(DynamicVarSet).FullName, "_vars");

    private static readonly IReadOnlyList<LoadoutKeywordModel> Models =
    [
        InevitableKeyword.Instance,
        StickyKeyword.Instance,
        PassingKeyword.Instance,
        LividKeyword.Instance,
        XCostKeyword.Instance,
        InfiniteUpgradeKeyword.Instance,
        JokeInfiniteUpgradeKeyword.Instance,
        HeavenlyKeyword.Instance,
        AltHeavenlyKeyword.Instance,
        LifestealKeyword.Instance,
        WallopKeyword.Instance,
        AutoplayKeyword.Instance,
        ReplayXKeyword.Instance,
        GatlingKeyword.Instance,
        MegaGatlingKeyword.Instance,
        BlankSlateKeyword.Instance,
        BasicDamageKeyword.Instance,
        BasicDamageAoeKeyword.Instance,
        BasicMultiHitKeyword.Instance,
        BasicMultiHitAoeKeyword.Instance,
        BasicBlockKeyword.Instance,
        BasicDrawKeyword.Instance,
        BasicDiscardKeyword.Instance,
        BasicExhaustKeyword.Instance,
        BasicTransformKeyword.Instance,
        BasicHealKeyword.Instance,
        BasicGainMaxHpKeyword.Instance,
        BasicLoseHealthKeyword.Instance,
        BasicEnergyKeyword.Instance,
        BasicStarsKeyword.Instance,
        BasicForgeKeyword.Instance,
        AnotherPlayerBlockKeyword.Instance,
        AllOtherPlayersBlockKeyword.Instance,
        AllPlayersBlockKeyword.Instance,
        AnotherPlayerDrawKeyword.Instance,
        AllOtherPlayersDrawKeyword.Instance,
        AllPlayersDrawKeyword.Instance,
        AnotherPlayerDiscardKeyword.Instance,
        AllOtherPlayersDiscardKeyword.Instance,
        AllPlayersDiscardKeyword.Instance,
        AnotherPlayerExhaustKeyword.Instance,
        AllOtherPlayersExhaustKeyword.Instance,
        AllPlayersExhaustKeyword.Instance,
        AnotherPlayerTransformKeyword.Instance,
        AllOtherPlayersTransformKeyword.Instance,
        AllPlayersTransformKeyword.Instance,
        AnotherPlayerHealKeyword.Instance,
        AllOtherPlayersHealKeyword.Instance,
        AllPlayersHealKeyword.Instance,
        AnotherPlayerGainMaxHpKeyword.Instance,
        AllOtherPlayersGainMaxHpKeyword.Instance,
        AllPlayersGainMaxHpKeyword.Instance,
        AnotherPlayerLoseHealthKeyword.Instance,
        AllOtherPlayersLoseHealthKeyword.Instance,
        AllPlayersLoseHealthKeyword.Instance,
        AnotherPlayerEnergyKeyword.Instance,
        AllOtherPlayersEnergyKeyword.Instance,
        AllPlayersEnergyKeyword.Instance,
        AnotherPlayerStarsKeyword.Instance,
        AllOtherPlayersStarsKeyword.Instance,
        AllPlayersStarsKeyword.Instance,
        AnotherPlayerForgeKeyword.Instance,
        AllOtherPlayersForgeKeyword.Instance,
        AllPlayersForgeKeyword.Instance,
        IncreaseDamageDealtThisTurnKeyword.Instance,
        IncreaseDamageDealtThisCombatKeyword.Instance,
        IncreaseDamageDealtPermanentlyKeyword.Instance,
        IncreaseMonsterDamageThisTurnKeyword.Instance,
        IncreaseMonsterDamageThisCombatKeyword.Instance,
        IncreaseMonsterDamagePermanentlyKeyword.Instance,
        ApplyPowerKeyword.Instance,
        ApplySelfKeyword.Instance,
        ApplyToAllEnemiesKeyword.Instance,
        ApplyToAllPlayersKeyword.Instance,
        ApplyToAnotherPlayerKeyword.Instance,
        GainPowerPermanentlyKeyword.Instance,
        GainPowerPermanentlyOnFatalKeyword.Instance,
        DiscardHandKeyword.Instance,
        NoDrawKeyword.Instance,
        InHandLoseHealthKeyword.Instance,
        InHandTakeDamageKeyword.Instance,
        InHandLoseGoldKeyword.Instance,
        EnthralledKeyword.Instance,
        ClashKeyword.Instance,
        EndTurnKeyword.Instance,
        GrandKeyword.Instance,
        BorrowedKeyword.Instance,
        LoseStrengthKeyword.Instance,
        LoseDexterityKeyword.Instance,
        LoseMaxHealthKeyword.Instance,
        LessonLearnedKeyword.Instance,
        FeedKeyword.Instance,
        GreedKeyword.Instance,
        HuntKeyword.Instance,
        SunderKeyword.Instance,
        AlchemyKeyword.Instance,
        VintageKeyword.Instance,
        MaxHpStealKeyword.Instance,
        BuffStealKeyword.Instance,
        PermanentBuffStealKeyword.Instance,
        EffectStealKeyword.Instance,
        PermanentEffectStealKeyword.Instance,
        PermanentFormStealKeyword.Instance,
        PermanentFormStealStackingKeyword.Instance,
        DamageOnPlayKeyword.Instance,
        DoubleDamageOnPlayKeyword.Instance,
        AllDamageOnPlayKeyword.Instance,
        PermanentDamageOnPlayKeyword.Instance,
        PermanentDamageOnFatalKeyword.Instance,
        BlockOnPlayKeyword.Instance,
        AllBlockOnPlayKeyword.Instance,
        PermanentBlockOnPlayKeyword.Instance,
        DoubleBlockOnPlayKeyword.Instance,
        VariablesOnPlayKeyword.Instance,
        AllVariablesOnPlayKeyword.Instance,
        PermanentVariablesOnPlayKeyword.Instance,
        PermanentVariablesOnFatalKeyword.Instance,
        DoubleVariablesOnPlayKeyword.Instance
    ];

    private static readonly IReadOnlyList<LoadoutKeywordModel>
        DescriptionModels =
        Models
            .Where(model =>
                model.Presentation
                == LoadoutKeywordPresentation.DescriptionOnly)
            .ToArray();

    private static readonly IReadOnlyList<LoadoutKeywordModel>
        PostOnPlayModels =
        Models
            .Where(model => model.HasOnPlayEffect)
            .OrderBy(model => model.OnPlayPriority)
            .ToArray();

    private static readonly IReadOnlyList<LoadoutKeywordModel>
        BaseDescriptionModels =
        Models
            .Where(model => model.TransformsBaseDescription)
            .OrderBy(model => model.BaseDescriptionPriority)
            .ToArray();

    private static readonly IReadOnlyList<LoadoutKeywordModel>
        OriginalOnPlaySuppressorModels =
        Models
            .Where(model => model.SuppressesOriginalOnPlay)
            .ToArray();

    private static readonly IReadOnlyList<LoadoutKeywordModel>
        OriginalIsPlayableSuppressorModels =
        Models
            .Where(model => model.SuppressesOriginalIsPlayable)
            .ToArray();

    private static readonly IReadOnlyList<LoadoutKeywordModel>
        OriginalShouldGlowGoldSuppressorModels =
        Models
            .Where(model => model.SuppressesOriginalShouldGlowGold)
            .ToArray();

    private static readonly IReadOnlyList<LoadoutKeywordModel>
        OriginalModelHookSuppressorModels =
        Models
            .Where(model => model.SuppressesOriginalModelHooks)
            .ToArray();

    private static readonly IReadOnlyList<LoadoutKeywordModel>
        UnblockedDamageModels =
        Models
            .Where(model => model.HasUnblockedDamageEffect)
            .ToArray();

    private static readonly IReadOnlyList<LoadoutKeywordModel>
        FatalModels =
        Models
            .Where(model => model.HasFatalEffect)
            .ToArray();

    private static readonly IReadOnlyList<LoadoutKeywordModel>
        FatalTargetSnapshotModels =
        FatalModels
            .Where(model => model.RequiresFatalTargetSnapshots)
            .ToArray();

    private static readonly IReadOnlyList<LoadoutKeywordModel>
        TurnEndInHandModels =
        Models
            .Where(model => model.HasTurnEndInHandEffect)
            .ToArray();

    private static readonly IReadOnlyList<LoadoutKeywordModel>
        TargetChangingModels =
        Models
            .Where(model => model.ChangesTargeting)
            .ToArray();

    private static readonly IReadOnlyList<LoadoutKeywordModel>
        AnotherPlayerTargetModels =
        Models
            .Where(model => model.RequiresAnotherPlayerTarget)
            .ToArray();

    private static readonly IReadOnlyList<LoadoutKeywordModel>
        BlockGainModels =
        Models
            .Where(model => model.ReportsGainsBlock)
            .ToArray();

    [ThreadStatic]
    private static Stack<CardModel>? _baseDescriptionContext;

    public static IReadOnlyList<LoadoutKeywordModel> All => Models;

    public static IReadOnlyList<LoadoutKeywordModel> DescriptionOnly =>
        DescriptionModels;

    public static IReadOnlyList<LoadoutKeywordModel> WithPostOnPlayEffect =>
        PostOnPlayModels;

    public static IReadOnlyList<LoadoutKeywordModel> WithUnblockedDamageEffect =>
        UnblockedDamageModels;

    public static IReadOnlyList<LoadoutKeywordModel> WithFatalEffect =>
        FatalModels;

    public static IReadOnlyList<LoadoutKeywordModel> WithTurnEndInHandEffect =>
        TurnEndInHandModels;

    public static bool TryGet(
        CardKeyword keyword,
        out LoadoutKeywordModel model)
    {
        foreach (LoadoutKeywordModel candidate in Models)
        {
            if (candidate.Keyword.Equals(keyword))
            {
                model = candidate;
                return true;
            }
        }

        model = null!;
        return false;
    }

    public static bool TryGetDynamicVar(
        string name,
        out LoadoutKeywordDynamicVarDefinition definition)
    {
        foreach (LoadoutKeywordModel keyword in Models)
        {
            foreach (LoadoutKeywordDynamicVarDefinition candidate in keyword.DynamicVars)
            {
                if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
                {
                    definition = candidate;
                    return true;
                }
            }
        }

        definition = null!;
        return false;
    }

    public static bool IsDescriptionKeyword(CardKeyword keyword)
    {
        return TryGet(keyword, out LoadoutKeywordModel model)
               && model.Presentation == LoadoutKeywordPresentation.DescriptionOnly;
    }

    public static bool ChangesTargeting(CardModel card)
    {
        foreach (LoadoutKeywordModel model in TargetChangingModels)
        {
            if (model.IsEnabled(card))
                return true;
        }

        return false;
    }

    public static bool RequiresAnotherPlayerTarget(CardModel card)
    {
        foreach (LoadoutKeywordModel model in AnotherPlayerTargetModels)
        {
            if (model.IsEnabled(card))
                return true;
        }

        return false;
    }

    public static bool ReportsGainsBlock(CardModel card)
    {
        foreach (LoadoutKeywordModel model in BlockGainModels)
        {
            if (model.IsEnabled(card))
                return true;
        }

        return false;
    }

    public static bool HasFatalEffect(CardModel card)
    {
        foreach (LoadoutKeywordModel model in FatalModels)
        {
            if (model.IsEnabled(card))
                return true;
        }

        return false;
    }

    public static bool RequiresFatalTargetSnapshots(CardModel card)
    {
        foreach (LoadoutKeywordModel model in FatalTargetSnapshotModels)
        {
            if (model.IsEnabled(card))
                return true;
        }

        return false;
    }

    public static bool HasTurnEndInHandEffect(CardModel card)
    {
        foreach (LoadoutKeywordModel model in TurnEndInHandModels)
        {
            if (model.IsEnabled(card))
                return true;
        }

        return false;
    }

    public static bool SuppressesOriginalOnPlay(CardModel card)
    {
        foreach (LoadoutKeywordModel model in OriginalOnPlaySuppressorModels)
        {
            if (model.IsEnabled(card))
                return true;
        }

        return false;
    }

    public static bool SuppressesOriginalIsPlayable(CardModel card)
    {
        foreach (LoadoutKeywordModel model in
                 OriginalIsPlayableSuppressorModels)
        {
            if (model.IsEnabled(card))
                return true;
        }

        return false;
    }

    public static bool SuppressesOriginalShouldGlowGold(CardModel card)
    {
        foreach (LoadoutKeywordModel model in
                 OriginalShouldGlowGoldSuppressorModels)
        {
            if (model.IsEnabled(card))
                return true;
        }

        return false;
    }

    public static bool SuppressesOriginalModelHooks(CardModel card)
    {
        return BlankSlateModelHookState.IsSuppressed(card);
    }

    public static void SynchronizeOriginalModelHookSuppression(CardModel card)
    {
        foreach (LoadoutKeywordModel model in
                 OriginalModelHookSuppressorModels)
        {
            if (model.IsEnabled(card))
            {
                BlankSlateModelHookState.Set(card, suppresses: true);
                return;
            }
        }

        BlankSlateModelHookState.Set(card, suppresses: false);
    }

    public static void PushBaseDescriptionContext(CardModel card)
    {
        _baseDescriptionContext ??= new Stack<CardModel>();
        _baseDescriptionContext.Push(card);
    }

    public static void PopBaseDescriptionContext()
    {
        if (_baseDescriptionContext is { Count: > 0 })
            _baseDescriptionContext.Pop();
    }

    public static void TransformBaseDescription(
        LocString locString,
        ref string description)
    {
        if (_baseDescriptionContext is not { Count: > 0 })
            return;

        CardModel card = _baseDescriptionContext.Peek();
        if (!string.Equals(locString.LocTable, "cards", StringComparison.Ordinal)
            || !string.Equals(
                locString.LocEntryKey,
                $"{card.Id.Entry}.description",
                StringComparison.Ordinal))
        {
            return;
        }

        foreach (LoadoutKeywordModel model in BaseDescriptionModels)
        {
            if (model.IsEnabled(card))
                description = model.TransformBaseDescription(card, description);
        }
    }

    public static async Task ApplyFatalEffects(
        CardModel card,
        PlayerChoiceContext choiceContext,
        FatalKeywordContext fatalContext)
    {
        if (fatalContext.FatalCount <= 0)
            return;

        foreach (LoadoutKeywordModel model in FatalModels)
        {
            if (model.IsEnabled(card))
            {
                await model.AfterFatalTargets(
                    card,
                    choiceContext,
                    fatalContext);
            }
        }
    }

    public static string GetTitle(LoadoutKeywordModel model)
    {
        return model.GetTitle();
    }

    public static bool IsEnabled(
        CardModel card,
        LoadoutKeywordModel model,
        IReadOnlyDictionary<string, bool>? overrides = null)
    {
        return model.IsEnabled(card, overrides);
    }

    public static void SynchronizeDynamicVars(
        CardModel card,
        IReadOnlyDictionary<string, bool>? overrides = null)
    {
        Dictionary<string, DynamicVar> variables = GetMutableVariables(card.DynamicVars);
        foreach (LoadoutKeywordModel model in Models)
        {
            bool enabled = model.IsEnabled(card, overrides);
            foreach (LoadoutKeywordDynamicVarDefinition dynamicVar in model.DynamicVars)
            {
                if (enabled)
                {
                    if (variables.ContainsKey(dynamicVar.Name))
                        continue;

                    DynamicVar value = dynamicVar.Create();
                    value.SetOwner(card);
                    variables.Add(dynamicVar.Name, value);
                }
                else
                {
                    variables.Remove(dynamicVar.Name);
                }
            }
        }
    }

    public static bool TryGetValue(
        CardModel card,
        string name,
        out DynamicVar dynamicVar)
    {
        if (card.DynamicVars.TryGetValue(name, out DynamicVar? value)
            && value is not null)
        {
            dynamicVar = value;
            return true;
        }

        dynamicVar = null!;
        return false;
    }

    public static string AddDescriptionLines(CardModel card, string description)
    {
        List<string>? before = null;
        List<string>? after = null;

        foreach (LoadoutKeywordModel model in DescriptionModels)
        {
            if (!model.IsEnabled(card))
            {
                continue;
            }

            string formatted = model.GetCardText(card);
            if (string.IsNullOrWhiteSpace(formatted))
                continue;

            if (model.TextPosition == LoadoutKeywordTextPosition.Before)
                (before ??= []).Add(formatted);
            else
                (after ??= []).Add(formatted);
        }

        if (before is null && after is null)
            return description;

        IEnumerable<string> lines = (before ?? [])
            .Append(description)
            .Concat(after ?? [])
            .Where(line => !string.IsNullOrWhiteSpace(line));
        return string.Join('\n', lines);
    }

    public static IEnumerable<IHoverTip> AdjustDescriptionKeywordHoverTips(
        CardModel card,
        IEnumerable<IHoverTip> hoverTips)
    {
        HashSet<string>? excludedIds = null;
        List<IHoverTip>? additionalHoverTips = null;
        foreach (LoadoutKeywordModel model in DescriptionModels)
        {
            if (!model.IsEnabled(card))
                continue;

            if (!model.ShowKeywordHoverTip)
            {
                excludedIds ??= [];
                excludedIds.Add(HoverTipFactory.FromKeyword(model.Keyword).Id);
            }

            foreach (IHoverTip tip in model.GetAdditionalCardHoverTips(card))
                (additionalHoverTips ??= []).Add(tip);
        }

        List<IHoverTip> result = (excludedIds is null
            ? hoverTips
            : hoverTips.Where(tip => !excludedIds.Contains(tip.Id)))
            .ToList();

        if (additionalHoverTips is not null)
        {
            HashSet<string> addedHoverTipIds = result
                .Select(tip => tip.Id)
                .ToHashSet(StringComparer.Ordinal);
            foreach (IHoverTip tip in additionalHoverTips)
            {
                if (addedHoverTipIds.Add(tip.Id))
                    result.Add(tip);
            }
        }

        HashSet<string> addedPowerIds = new(StringComparer.Ordinal);
        foreach (LoadoutPowerKeywordModel model in
                 Models.OfType<LoadoutPowerKeywordModel>())
        {
            foreach (LoadoutPowerKeywordEntry entry in
                     LoadoutPowerKeywordState.GetEffectiveEntries(
                         card,
                         model.StorageKey))
            {
                if (!addedPowerIds.Add(entry.PowerId)
                    || !LoadoutPowerKeywordState.TryResolvePower(
                        entry.PowerId,
                        out PowerModel power))
                {
                    continue;
                }

                result.AddRange(
                    PowerGiver.CreateSafePowerHoverTips(power, null));
            }
        }

        return result;
    }

    private static Dictionary<string, DynamicVar> GetMutableVariables(DynamicVarSet dynamicVars)
    {
        return DynamicVarDictionaryField.GetValue(dynamicVars) as Dictionary<string, DynamicVar>
               ?? throw new InvalidOperationException(
                   $"{typeof(DynamicVarSet).FullName}._vars was not a DynamicVar dictionary.");
    }
}

public static class LoadoutDescriptionKeywordPatch
{
    [HarmonyPrefix]
    public static void Prefix(CardModel __instance)
    {
        LoadoutKeywordRegistry.PushBaseDescriptionContext(__instance);
    }

    [HarmonyPostfix]
    public static void Postfix(CardModel __instance, ref string __result)
    {
        __result = LoadoutKeywordRegistry.AddDescriptionLines(__instance, __result);
    }

    [HarmonyFinalizer]
    public static Exception? Finalizer(Exception? __exception)
    {
        LoadoutKeywordRegistry.PopBaseDescriptionContext();

        return __exception;
    }
}

public static class LoadoutBaseDescriptionKeywordLocStringPatch
{
    [HarmonyPostfix]
    public static void Postfix(LocString __instance, ref string __result)
    {
        LoadoutKeywordRegistry.TransformBaseDescription(__instance, ref __result);
    }
}

public static class LoadoutDescriptionKeywordHoverTipsPatch
{
    [HarmonyPostfix]
    public static void Postfix(
        CardModel __instance,
        ref IEnumerable<IHoverTip> __result)
    {
        __result = LoadoutKeywordRegistry.AdjustDescriptionKeywordHoverTips(
            __instance,
            __result);
    }
}
