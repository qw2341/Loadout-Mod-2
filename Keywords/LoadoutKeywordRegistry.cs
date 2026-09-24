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
using Loadout.Patches.Cards.CardModification;
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
        XValueKeyword.Instance,
        ArithmeticXValueKeyword.Instance,
        AltXValueKeyword.Instance,
        AltAltXValueKeyword.Instance,
        AltAltAltXValueKeyword.Instance,
        InfiniteUpgradeKeyword.Instance,
        JokeInfiniteUpgradeKeyword.Instance,
        KarmicKeyword.Instance,
        HeavenlyKeyword.Instance,
        AltHeavenlyKeyword.Instance,
        MultiHitKeyword.Instance,
        MultiBlockKeyword.Instance,
        LifestealKeyword.Instance,
        WallopKeyword.Instance,
        AutoplayKeyword.Instance,
        ReplayXKeyword.Instance,
        GatlingKeyword.Instance,
        MegaGatlingKeyword.Instance,
        BolasKeyword.Instance,
        ParticleKeyword.Instance,
        AngerKeyword.Instance,
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
        AddCardKeyword.Instance,
        AddRandomCardKeyword.Instance,
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

    private static readonly IReadOnlyList<LoadoutKeywordModel> UnblockedDamageModels =
        Models.Where(model => model.HasUnblockedDamageEffect).ToArray();

    private static readonly IReadOnlyList<LoadoutKeywordModel> FatalModels =
        Models.Where(model => model.HasFatalEffect).ToArray();

    private static readonly IReadOnlyList<LoadoutKeywordModel> TurnEndInHandModels =
        Models.Where(model => model.HasTurnEndInHandEffect).ToArray();

    public static IReadOnlyList<LoadoutKeywordModel> WithXCostOverride { get; } =
        Models.Where(model => (model.ModificationFlags & LoadoutCardModificationFlags.OverrideXCost) != 0).ToArray();

    private static readonly Lazy<LoadoutKeywordIndex> ModelIndex = new(() => new(Models));

    private static readonly Dictionary<string, (LoadoutKeywordModel Model, LoadoutKeywordDynamicVarDefinition Definition)[]>
        DynamicVarOwners = Models
            .SelectMany(model => model.DynamicVars.Select(definition => (Model: model, Definition: definition)))
            .GroupBy(entry => entry.Definition.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

    private static readonly HashSet<string> XValueExemptDynamicVars = Models
        .SelectMany(model => model.DynamicVars)
        .Where(definition => !definition.AffectedByXValue)
        .Select(definition => definition.Name)
        .ToHashSet(StringComparer.Ordinal);

    private sealed class DescriptionContext(CardModel card)
    {
        public CardModel Card { get; } = card;
        public IReadOnlyList<LoadoutKeywordModel>? Models { get; set; }
    }

    [ThreadStatic]
    private static Stack<DescriptionContext>? _baseDescriptionContext;

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
        return ModelIndex.Value.TryGet(keyword, out model);
    }

    public static bool TryGet(string storageKey, out LoadoutKeywordModel model) =>
        ModelIndex.Value.TryGet(storageKey, out model);

    public static int CompareRegistration(LoadoutKeywordModel left, LoadoutKeywordModel right) =>
        ModelIndex.Value.CompareRegistration(left, right);

    public static IReadOnlyList<LoadoutKeywordModel> ResolveActiveModels(
        CardModel card,
        Predicate<LoadoutKeywordModel>? predicate = null,
        IReadOnlyDictionary<string, bool>? overrides = null) =>
        ModelIndex.Value.Resolve(card, predicate, overrides);

    public static IReadOnlyList<LoadoutKeywordModel> ResolvePostOnPlayModels(
        IReadOnlyList<LoadoutKeywordModel> active)
    {
        List<LoadoutKeywordModel>? effects = null;
        foreach (LoadoutKeywordModel model in active)
        {
            if (model.HasOnPlayEffect)
                (effects ??= []).Add(model);
        }
        if (effects is null)
            return Array.Empty<LoadoutKeywordModel>();
        effects.Sort(static (left, right) => ModelIndex.Value.CompareOnPlay(left, right));
        return effects;
    }

    public static IEnumerable<LoadoutKeywordModel> EnumerateLiveModels(
        CardModel card, Predicate<LoadoutKeywordModel> predicate) =>
        ModelIndex.Value.EnumerateLive(card, predicate);

    public static IEnumerable<LoadoutKeywordModel> ResolveOverrideModels(
        IReadOnlyDictionary<string, bool> overrides) =>
        ModelIndex.Value.EnumerateOverrides(overrides);

    public static bool IsAffectedByXValue(string name) => !XValueExemptDynamicVars.Contains(name);

    public static bool TryGetDynamicVar(
        string name,
        out LoadoutKeywordDynamicVarDefinition definition)
    {
        if (DynamicVarOwners.TryGetValue(name, out var owners))
        {
            definition = owners[0].Definition;
            return true;
        }

        definition = null!;
        return false;
    }

    private static IReadOnlyList<LoadoutKeywordModel> GetPresentationModels(CardModel card)
    {
        if (_baseDescriptionContext is { Count: > 0 }
            && _baseDescriptionContext.Peek() is var context
            && ReferenceEquals(context.Card, card))
        {
            return context.Models ??= ResolveActiveModels(card);
        }

        return ResolveActiveModels(card);
    }

    private static bool AnyActive(CardModel card, Predicate<LoadoutKeywordModel> predicate)
    {
        if (_baseDescriptionContext is { Count: > 0 }
            && ReferenceEquals(_baseDescriptionContext.Peek().Card, card))
        {
            foreach (LoadoutKeywordModel model in GetPresentationModels(card))
            {
                if (predicate(model))
                    return true;
            }
            return false;
        }

        return ModelIndex.Value.Any(card, predicate);
    }

    public static bool IsDescriptionKeyword(CardKeyword keyword)
    {
        return TryGet(keyword, out LoadoutKeywordModel model)
               && model.Presentation == LoadoutKeywordPresentation.DescriptionOnly;
    }

    public static bool ChangesTargeting(CardModel card) =>
        AnyActive(card, model => model.ChangesTargeting);

    public static bool RequiresAnotherPlayerTarget(CardModel card) =>
        AnyActive(card, model => model.RequiresAnotherPlayerTarget);

    public static bool ReportsGainsBlock(CardModel card) =>
        AnyActive(card, model => model.ReportsGainsBlock);

    public static bool HasFatalEffect(CardModel card) =>
        AnyActive(card, model => model.HasFatalEffect);

    public static bool RequiresFatalTargetSnapshots(CardModel card) =>
        AnyActive(card, model => model.HasFatalEffect && model.RequiresFatalTargetSnapshots);

    public static bool HasTurnEndInHandEffect(CardModel card) =>
        AnyActive(card, model => model.HasTurnEndInHandEffect);

    public static bool SuppressesOriginalOnPlay(CardModel card) =>
        AnyActive(card, model => model.SuppressesOriginalOnPlay);

    public static bool SuppressesOriginalIsPlayable(CardModel card) =>
        AnyActive(card, model => model.SuppressesOriginalIsPlayable);

    public static bool SuppressesOriginalShouldGlowGold(CardModel card) =>
        AnyActive(card, model => model.SuppressesOriginalShouldGlowGold);

    public static bool SuppressesOriginalModelHooks(CardModel card)
    {
        return BlankSlateModelHookState.IsSuppressed(card);
    }

    public static void SynchronizeOriginalModelHookSuppression(CardModel card)
    {
        BlankSlateModelHookState.Set(card,
            ModelIndex.Value.Any(card, model => model.SuppressesOriginalModelHooks));
    }

    public static void PushBaseDescriptionContext(CardModel card)
    {
        _baseDescriptionContext ??= new Stack<DescriptionContext>();
        _baseDescriptionContext.Push(new DescriptionContext(card));
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

        CardModel card = _baseDescriptionContext.Peek().Card;
        if (!string.Equals(locString.LocTable, "cards", StringComparison.Ordinal)
            || !string.Equals(
                locString.LocEntryKey,
                $"{card.Id.Entry}.description",
                StringComparison.Ordinal))
        {
            return;
        }

        if (CardModificationRuntime.UsesCombinedCustomDescription(card))
            return;

        description = GetDescriptionText(card, description, rawText: false);
    }

    public static string GetDescriptionText(CardModel card, string description, bool rawText)
    {
        IReadOnlyList<LoadoutKeywordModel> active = GetPresentationModels(card);
        List<LoadoutKeywordModel>? transformers = null;
        foreach (LoadoutKeywordModel model in active)
        {
            if (model.TransformsBaseDescription)
                (transformers ??= []).Add(model);
        }
        if (transformers is not null)
        {
            transformers.Sort(static (left, right) => ModelIndex.Value.CompareBaseDescription(left, right));
            foreach (LoadoutKeywordModel model in transformers)
                description = model.TransformBaseDescription(card, description);
        }

        return AddDescriptionLines(card, description, active, rawText);
    }

    public static void AddCustomDescriptionVariables(CardModel card, LocString description)
    {
        foreach (LoadoutKeywordModel model in GetPresentationModels(card))
        {
            if (model.Presentation == LoadoutKeywordPresentation.DescriptionOnly
                && !string.IsNullOrWhiteSpace(model.CardTextLocKey))
                model.AddCustomDescriptionVariables(card, description);
        }
    }

    public static async Task ApplyFatalEffects(
        CardModel card,
        PlayerChoiceContext choiceContext,
        FatalKeywordContext fatalContext)
    {
        if (fatalContext.FatalCount <= 0)
            return;

        foreach (LoadoutKeywordModel model in EnumerateLiveModels(card, model => model.HasFatalEffect))
            await model.AfterFatalTargets(card, choiceContext, fatalContext);
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
        IReadOnlyList<LoadoutKeywordModel> active = ResolveActiveModels(card, overrides: overrides);
        if ((LoadoutCardModificationFlagState.GetFlags(active) & LoadoutCardModificationFlags.OverrideXValue) != 0)
            XValueKeywordRuntime.Prepare(card);
        if (active.Contains(MultiHitKeyword.Instance))
            MultiHitKeywordPatches.Prepare(card);
        if (active.Contains(KarmicKeyword.Instance))
            KarmicKeywordPatches.Prepare();
        if (active.Contains(MultiBlockKeyword.Instance))
            MultiBlockKeywordPatches.Prepare();

        Dictionary<string, DynamicVar> variables = GetMutableVariables(card.DynamicVars);
        HashSet<LoadoutKeywordModel>? owners = null;
        foreach (string name in variables.Keys)
        {
            if (DynamicVarOwners.TryGetValue(name, out var definitions))
            {
                foreach (var definition in definitions)
                    (owners ??= []).Add(definition.Model);
            }
        }
        foreach (LoadoutKeywordModel model in active)
        {
            foreach (LoadoutKeywordDynamicVarDefinition definition in model.DynamicVars)
            {
                foreach (var owner in DynamicVarOwners[definition.Name])
                    (owners ??= []).Add(owner.Model);
            }
        }
        if (owners is null)
            return;

        List<LoadoutKeywordModel> orderedOwners = owners.ToList();
        orderedOwners.Sort(CompareRegistration);
        foreach (LoadoutKeywordModel model in orderedOwners)
        {
            // Retain registration-order add/remove behavior for shared variable names.
            bool enabled = active.Contains(model);
            foreach (LoadoutKeywordDynamicVarDefinition definition in model.DynamicVars)
            {
                if (!enabled)
                {
                    variables.Remove(definition.Name);
                }
                else if (!variables.ContainsKey(definition.Name))
                {
                    DynamicVar value = definition.Create();
                    value.SetOwner(card);
                    variables.Add(definition.Name, value);
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

    public static string AddDescriptionLines(CardModel card, string description) =>
        AddDescriptionLines(card, description, GetPresentationModels(card));

    private static string AddDescriptionLines(
        CardModel card, string description, IReadOnlyList<LoadoutKeywordModel> active,
        bool rawText = false)
    {
        List<string>? before = null;
        List<string>? after = null;

        foreach (LoadoutKeywordModel model in active)
        {
            if (model.Presentation != LoadoutKeywordPresentation.DescriptionOnly)
            {
                continue;
            }

            string formatted = rawText ? model.GetCardTextForEditor(card) : model.GetCardText(card);
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
        IReadOnlyList<LoadoutKeywordModel> active = GetPresentationModels(card);
        HashSet<string>? excludedIds = null;
        List<IHoverTip>? additionalHoverTips = null;
        foreach (LoadoutKeywordModel model in active)
        {
            if (model.Presentation != LoadoutKeywordPresentation.DescriptionOnly)
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

        Dictionary<LoadoutKeywordModel, List<LoadoutPowerKeywordEntry>>? powerEntries = null;
        foreach (LoadoutPowerKeywordEntry entry in LoadoutPowerKeywordState.GetEffectiveEntries(card))
        {
            if (!TryGet(entry.KeywordKey, out LoadoutKeywordModel model)
                || model is not LoadoutPowerKeywordModel)
                continue;

            powerEntries ??= [];
            if (!powerEntries.TryGetValue(model, out List<LoadoutPowerKeywordEntry>? entries))
                powerEntries.Add(model, entries = []);
            entries.Add(entry);
        }

        if (powerEntries is null)
            return result;

        List<LoadoutKeywordModel> powerModels = powerEntries.Keys.ToList();
        powerModels.Sort(CompareRegistration);
        HashSet<string> addedPowerIds = new(StringComparer.Ordinal);
        foreach (LoadoutKeywordModel model in powerModels)
        {
            foreach (LoadoutPowerKeywordEntry entry in powerEntries[model])
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
