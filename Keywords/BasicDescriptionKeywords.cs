#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BaseLib.Patches.Features;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

public abstract class LoadoutBasicKeywordModel : LoadoutKeywordModel
{
    public override LoadoutKeywordPresentation Presentation =>
        LoadoutKeywordPresentation.DescriptionOnly;

    public override LoadoutKeywordEditorSection EditorSection =>
        LoadoutKeywordEditorSection.Basic;

    public override bool HasOnPlayEffect => true;

    protected static DynamicVar GetAmount(CardModel card, string name)
    {
        return LoadoutKeywordRegistry.TryGetValue(card, name, out DynamicVar value)
            ? value
            : new DynamicVar(name, 0m);
    }
}

public sealed class BasicDamageKeyword : LoadoutBasicKeywordModel
{
    public const string AmountVar = "LoadoutBasicDamage";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                0,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_BASIC_DAMAGE",
                (name, value) => new DamageVar(name, value, ValueProp.Move))
        ];

    public static BasicDamageKeyword Instance { get; } = new();

    private BasicDamageKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.BasicDamage;

    public override string StorageKey => LoadoutKeywords.BasicDamageKey;

    public override string TitleLocKey => "LOADOUT-BASIC_DAMAGE.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_DAMAGE.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override async Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        DynamicVar amount = GetAmount(card, AmountVar);
        if (amount.BaseValue <= 0m)
            return;

        Creature? target = cardPlay.Target;
        if (target is null && card.TargetType == TargetType.AnyPlayer)
            target = card.Owner.Creature;
        else if (target is null && card.TargetType == TargetType.Osty)
            target = card.Owner.Osty;

        if (target is null || target.IsDead)
            return;

        await DamageCmd.Attack(amount.BaseValue)
            .FromCard(card, cardPlay)
            .Targeting(target)
            .WithHitFx("vfx/vfx_attack_slash")
            .Execute(choiceContext);
    }
}

public sealed class BasicDamageAoeKeyword : LoadoutBasicKeywordModel
{
    public const string AmountVar = "LoadoutBasicDamageAoe";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                0,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_BASIC_DAMAGE_AOE",
                (name, value) => new DamageVar(name, value, ValueProp.Move))
        ];

    public static BasicDamageAoeKeyword Instance { get; } = new();

    private BasicDamageAoeKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.BasicDamageAoe;

    public override string StorageKey => LoadoutKeywords.BasicDamageAoeKey;

    public override string TitleLocKey => "LOADOUT-BASIC_DAMAGE_AOE.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_DAMAGE_AOE.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override async Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        DynamicVar amount = GetAmount(card, AmountVar);
        if (amount.BaseValue <= 0m || card.CombatState is null)
            return;

        await DamageCmd.Attack(amount.BaseValue)
            .FromCard(card, cardPlay)
            .TargetingAllOpponents(card.CombatState)
            .WithHitFx("vfx/vfx_attack_slash")
            .Execute(choiceContext);
    }
}

public sealed class BasicBlockKeyword : LoadoutBasicKeywordModel
{
    public const string AmountVar = "LoadoutBasicBlock";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                0,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_BASIC_BLOCK",
                (name, value) => new BlockVar(name, value, ValueProp.Move))
        ];

    public static BasicBlockKeyword Instance { get; } = new();

    private BasicBlockKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.BasicBlock;

    public override string StorageKey => LoadoutKeywords.BasicBlockKey;

    public override string TitleLocKey => "LOADOUT-BASIC_BLOCK.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_BLOCK.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override async Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        if (GetAmount(card, AmountVar) is not BlockVar amount
            || amount.BaseValue <= 0m)
        {
            return;
        }

        await CreatureCmd.GainBlock(card.Owner.Creature, amount, cardPlay);
    }
}

public sealed class BasicDrawKeyword : LoadoutBasicKeywordModel
{
    public const string AmountVar = "LoadoutBasicDraw";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                0,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_BASIC_DRAW",
                (name, value) => new CardsVar(name, decimal.ToInt32(value)))
        ];

    public static BasicDrawKeyword Instance { get; } = new();

    private BasicDrawKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.BasicDraw;

    public override string StorageKey => LoadoutKeywords.BasicDrawKey;

    public override string TitleLocKey => "LOADOUT-BASIC_DRAW.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_DRAW.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        int amount = Math.Max(0, GetAmount(card, AmountVar).IntValue);
        return amount == 0
            ? Task.CompletedTask
            : CardPileCmd.Draw(choiceContext, amount, card.Owner);
    }
}

public sealed class BasicDiscardKeyword : LoadoutBasicKeywordModel
{
    public const string AmountVar = "LoadoutBasicDiscard";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                0,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_BASIC_DISCARD",
                (name, value) => new CardsVar(name, decimal.ToInt32(value)))
        ];

    public static BasicDiscardKeyword Instance { get; } = new();

    private BasicDiscardKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.BasicDiscard;

    public override string StorageKey => LoadoutKeywords.BasicDiscardKey;

    public override string TitleLocKey => "LOADOUT-BASIC_DISCARD.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_DISCARD.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override async Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        int amount = GetClampedHandCount(card, AmountVar);
        if (amount == 0)
            return;

        IEnumerable<CardModel> selection = await CardSelectCmd.FromHandForDiscard(
            choiceContext,
            card.Owner,
            new CardSelectorPrefs(CardSelectorPrefs.DiscardSelectionPrompt, amount),
            null,
            card);
        await CardCmd.Discard(choiceContext, selection);
    }

    internal static int GetClampedHandCount(CardModel card, string variableName)
    {
        int requested = Math.Max(0, GetAmount(card, variableName).IntValue);
        int available = PileType.Hand.GetPile(card.Owner).Cards.Count;
        return Math.Min(requested, available);
    }
}

public sealed class BasicExhaustKeyword : LoadoutBasicKeywordModel
{
    public const string AmountVar = "LoadoutBasicExhaust";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                0,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_BASIC_EXHAUST",
                (name, value) => new CardsVar(name, decimal.ToInt32(value)))
        ];

    public static BasicExhaustKeyword Instance { get; } = new();

    private BasicExhaustKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.BasicExhaust;

    public override string StorageKey => LoadoutKeywords.BasicExhaustKey;

    public override string TitleLocKey => "LOADOUT-BASIC_EXHAUST.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_EXHAUST.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override async Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        int amount = BasicDiscardKeyword.GetClampedHandCount(card, AmountVar);
        if (amount == 0)
            return;

        IEnumerable<CardModel> selection = await CardSelectCmd.FromHand(
            choiceContext,
            card.Owner,
            new CardSelectorPrefs(CardSelectorPrefs.ExhaustSelectionPrompt, amount),
            null,
            card);
        foreach (CardModel selectedCard in selection.ToList())
            await CardCmd.Exhaust(choiceContext, selectedCard);
    }
}

public sealed class BasicHealKeyword : LoadoutBasicKeywordModel
{
    public const string AmountVar = "LoadoutBasicHeal";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                0,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_BASIC_HEAL",
                (name, value) => new HealVar(name, value))
        ];

    public static BasicHealKeyword Instance { get; } = new();

    private BasicHealKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.BasicHeal;

    public override string StorageKey => LoadoutKeywords.BasicHealKey;

    public override string TitleLocKey => "LOADOUT-BASIC_HEAL.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_HEAL.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        decimal amount = Math.Max(0m, GetAmount(card, AmountVar).BaseValue);
        return amount <= 0m
            ? Task.CompletedTask
            : CreatureCmd.Heal(card.Owner.Creature, amount);
    }
}

public sealed class BasicEnergyKeyword : LoadoutBasicKeywordModel
{
    public const string AmountVar = "LoadoutBasicEnergy";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                0,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_BASIC_ENERGY",
                (name, value) => new EnergyVar(name, decimal.ToInt32(value)))
        ];

    public static BasicEnergyKeyword Instance { get; } = new();

    private BasicEnergyKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.BasicEnergy;

    public override string StorageKey => LoadoutKeywords.BasicEnergyKey;

    public override string TitleLocKey => "LOADOUT-BASIC_ENERGY.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_ENERGY.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        int amount = Math.Max(0, GetAmount(card, AmountVar).IntValue);
        return amount == 0
            ? Task.CompletedTask
            : PlayerCmd.GainEnergy(amount, card.Owner);
    }
}

internal static class LoadoutBasicKeywordTargetTypePatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        return GetPropertyGetters(nameof(CardModel.TargetType));
    }

    [HarmonyPostfix]
    public static void Postfix(CardModel __instance, ref TargetType __result)
    {
        if (!LoadoutKeywords.Has(__instance, LoadoutKeywords.BasicDamage))
            return;

        if (__result == TargetType.Self)
        {
            __result = CustomTargetType.Anyone;
            return;
        }

        if (__result is TargetType.AnyEnemy
            or TargetType.AnyPlayer
            or TargetType.AnyAlly
            or TargetType.Osty
            || CustomTargetType.IsCustomSingleTargetType(__result))
        {
            return;
        }

        if (__result is TargetType.None
            or TargetType.AllEnemies
            or TargetType.RandomEnemy
            or TargetType.AllAllies
            or TargetType.TargetedNoCreature
            || CustomTargetType.IsCustomMultiTargetType(__result))
        {
            __result = TargetType.AnyEnemy;
        }
    }

    internal static IEnumerable<MethodBase> GetPropertyGetters(
        string propertyName)
    {
        HashSet<MethodBase> targets = [];
        const BindingFlags flags = BindingFlags.Instance
                                   | BindingFlags.Public
                                   | BindingFlags.NonPublic
                                   | BindingFlags.DeclaredOnly;
        foreach (Type type in ModelDb.AllCards
                     .Select(card => card.GetType())
                     .Append(typeof(CardModel))
                     .Distinct())
        {
            MethodInfo? getter = type.GetProperty(propertyName, flags)?.GetMethod;
            if (getter is not null && !getter.IsStatic)
                targets.Add(getter);
        }

        return targets;
    }
}

internal static class LoadoutBasicKeywordGainsBlockPatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        return LoadoutBasicKeywordTargetTypePatch.GetPropertyGetters(
            nameof(CardModel.GainsBlock));
    }

    [HarmonyPostfix]
    public static void Postfix(CardModel __instance, ref bool __result)
    {
        __result |= LoadoutKeywords.Has(__instance, LoadoutKeywords.BasicBlock);
    }
}
