#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using Loadout.Services.Compatibility;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

public sealed class MultiHitKeyword : LoadoutKeywordModel
{
    public const string UnitDamageVarName = "LoadoutMultiHitUnitDamage";
    public static MultiHitKeyword Instance { get; } = new();
    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition> Variables =
    [
        new(UnitDamageVarName, 1m, 1, 1, "",
            (_, _) => new UnitDamageVar(), EditorVisible: false)
    ];

    private MultiHitKeyword() { }

    public override CardKeyword Keyword => LoadoutKeywords.MultiHit;
    public override string StorageKey => LoadoutKeywords.MultiHitKey;
    public override string TitleLocKey => "LOADOUT-MULTI_HIT.title";
    
    public override LoadoutKeywordEditorSection EditorSection => LoadoutKeywordEditorSection.Joke;
    public override LoadoutKeywordPresentation Presentation => LoadoutKeywordPresentation.Normal;
    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars => Variables;

    public static int GetHitCount(decimal damage) => (int)Math.Clamp(decimal.Floor(damage), 0m, int.MaxValue);

    public sealed class UnitDamageVar() : DamageVar(UnitDamageVarName, 1m, ValueProp.Move)
    {
        public override void UpdateCardPreview(
            CardModel card, CardPreviewMode previewMode, Creature? target, bool runGlobalHooks)
        {
            BaseValue = 1m;
            if (card.DynamicVars.TryGetValue(DamageVar.defaultName, out DynamicVar? original)
                && original is DamageVar damage)
                Props = damage.Props;
            base.UpdateCardPreview(card, previewMode, target, runGlobalHooks);
        }
    }
}

public static class MultiHitKeywordPatches
{
    private static readonly Harmony Harmony = new("Loadout.Keyword.MultiHit");
    private static readonly HashSet<Type> PreparedTypes = [];
    private static readonly ConditionalWeakTable<AttackCommand, HitCount> AttackHits = new();
    private static readonly AccessTools.FieldRef<DynamicVar, AbstractModel?> Owner =
        AccessTools.FieldRefAccess<DynamicVar, AbstractModel?>("_owner");
    private static readonly MethodInfo DamageGetter = AccessTools.PropertyGetter(typeof(DynamicVarSet), nameof(DynamicVarSet.Damage));
    private static readonly MethodInfo BaseValueGetter = AccessTools.PropertyGetter(typeof(DynamicVar), nameof(DynamicVar.BaseValue));
    private static readonly MethodInfo AttackMethod = AccessTools.Method(typeof(DamageCmd), nameof(DamageCmd.Attack), [typeof(decimal)]);
    private static readonly MethodInfo DamageMethod = Sts2Compatibility.SingleTargetDamageMethod;
    private static bool Installed;

    private sealed record HitCount(int Count);

    public static bool IsOriginalDamage(DynamicVar variable, out CardModel card)
    {
        card = null!;
        if (variable is not DamageVar || variable.Name != DamageVar.defaultName
            || Owner(variable) is not CardModel owner
            || !LoadoutKeywords.Has(owner, LoadoutKeywords.MultiHit))
            return false;
        card = owner;
        return true;
    }

    public static void Prepare(CardModel card)
    {
        if (!Installed)
        {
            Harmony.Patch(AccessTools.Method(typeof(DamageVar), nameof(DamageVar.UpdateCardPreview)),
                prefix: new HarmonyMethod(typeof(MultiHitKeywordPatches), nameof(PreviewPrefix)));
            Harmony.Patch(AccessTools.Method(typeof(DynamicVar), nameof(DynamicVar.ToHighlightedString)),
                postfix: new HarmonyMethod(typeof(MultiHitKeywordPatches), nameof(HighlightPostfix)));
            Harmony.Patch(AccessTools.Method(typeof(Hook), nameof(Hook.ModifyAttackHitCount)),
                postfix: new HarmonyMethod(typeof(MultiHitKeywordPatches), nameof(HitCountPostfix)));
            Harmony.Patch(Sts2Compatibility.SingleTargetDamageVarMethod,
                prefix: new HarmonyMethod(typeof(MultiHitKeywordPatches),
                    Sts2Compatibility.SingleTargetDamageVarMethod.GetParameters().Length == 5
                        ? nameof(DamagePrefix) : nameof(LegacyDamagePrefix)));
            Harmony.Patch(Sts2Compatibility.MultiTargetDamageVarMethod,
                prefix: new HarmonyMethod(typeof(MultiHitKeywordPatches),
                    Sts2Compatibility.MultiTargetDamageVarMethod.GetParameters().Length == 6
                        ? nameof(DamageTargetsPrefix) : nameof(LegacyDamageTargetsPrefix)));
            Installed = true;
        }

        for (Type? type = card.GetType(); type is not null && type != typeof(CardModel); type = type.BaseType)
            PrepareType(type);
    }

    private static void PrepareType(Type type)
    {
        if (PreparedTypes.Contains(type))
            return;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                   | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (MethodInfo method in type.GetMethods(flags))
        {
            if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() is null)
                continue;
            List<CodeInstruction> instructions = PatchProcessor.GetOriginalInstructions(method);
            if (!Rewrite(instructions))
                continue;
            Harmony.Patch(method, transpiler: new HarmonyMethod(typeof(MultiHitKeywordPatches), nameof(Transpiler)));
        }
        foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            PrepareType(nested);
        PreparedTypes.Add(type);
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> result = new(instructions);
        Rewrite(result);
        return result;
    }

    // Preserve the variable identity before the native decimal command loses it.
    public static bool Rewrite(List<CodeInstruction> instructions)
    {
        bool changed = false;
        for (int i = 1; i < instructions.Count - 1; i++)
        {
            if (!instructions[i - 1].Calls(DamageGetter) || !instructions[i].Calls(BaseValueGetter))
                continue;
            if (instructions[i + 1].Calls(AttackMethod))
            {
                instructions[i].opcode = OpCodes.Nop;
                instructions[i].operand = null;
                instructions[i + 1].operand = AccessTools.Method(typeof(MultiHitKeywordPatches), nameof(Attack));
                changed = true;
                continue;
            }

            // Omnislice passes the value directly to CreatureCmd.Damage.
            for (int j = i + 1; j < instructions.Count; j++)
            {
                CodeInstruction next = instructions[j];
                if (next.Calls(DamageMethod))
                {
                    instructions[i].opcode = OpCodes.Nop;
                    instructions[i].operand = null;
                    next.operand = AccessTools.Method(typeof(MultiHitKeywordPatches), nameof(Damage));
                    if (DamageMethod.GetParameters().Length == 5)
                    {
                        CodeInstruction cardPlay = new(OpCodes.Ldnull);
                        cardPlay.MoveLabelsFrom(next);
                        cardPlay.MoveBlocksFrom(next);
                        instructions.Insert(j, cardPlay);
                    }
                    changed = true;
                    break;
                }
                if (next.labels.Count > 0 || next.blocks.Count > 0
                    || (next.opcode.FlowControl != FlowControl.Next)
                    || (next.opcode.StackBehaviourPop != StackBehaviour.Pop0
                        && next.opcode != OpCodes.Ldfld))
                    break;
            }
        }
        return changed;
    }

    public static AttackCommand Attack(DamageVar damage)
    {
        if (!IsOriginalDamage(damage, out CardModel card))
            return DamageCmd.Attack(damage.BaseValue);
        AttackCommand command = DamageCmd.Attack(XValueKeywordRuntime.GetInstanceValue(card));
        AttackHits.Add(command, new HitCount(MultiHitKeyword.GetHitCount(damage.BaseValue)));
        return command;
    }

    public static void HitCountPostfix(AttackCommand __1, ref decimal __result)
    {
        if (!AttackHits.TryGetValue(__1, out HitCount? hits))
            return;
        __result = Math.Min(int.MaxValue, __result * hits.Count);
        if (__1.ModelSource is CardModel card)
            CardEffectAnimationScope.MarkAltHeavenlyResult(card, MultiHitKeyword.GetHitCount(__result));
    }

    public static bool PreviewPrefix(DamageVar __instance)
    {
        if (!IsOriginalDamage(__instance, out _))
            return true;
        __instance.ResetToBase();
        return false;
    }

    public static void HighlightPostfix(DynamicVar __instance, bool inverse, ref string __result)
    {
        if (IsOriginalDamage(__instance, out CardModel card)
            && card.DynamicVars.TryGetValue(MultiHitKeyword.UnitDamageVarName, out DynamicVar? unit))
        {
            __result = $"{unit.ToHighlightedString(inverse)} x {__result}";
            if (LocManager.Instance?.Language is "zhs" or "zht")
                __result = $" {__result} ";
        }
    }

    public static bool DamagePrefix(
        PlayerChoiceContext choiceContext, Creature target, DamageVar damageVar,
        CardModel cardSource, CardPlay? cardPlay, ref Task<IEnumerable<DamageResult>> __result)
    {
        if (!IsOriginalDamage(damageVar, out _))
            return true;
        __result = Damage(choiceContext, target, damageVar, damageVar.Props, cardSource, cardPlay);
        return false;
    }

    public static bool LegacyDamagePrefix(
        PlayerChoiceContext choiceContext, Creature target, DamageVar damageVar,
        CardModel cardSource, ref Task<IEnumerable<DamageResult>> __result) =>
        DamagePrefix(choiceContext, target, damageVar, cardSource, null, ref __result);

    public static Task<IEnumerable<DamageResult>> Damage(
        PlayerChoiceContext choiceContext, Creature target, DamageVar damage,
        ValueProp props, CardModel cardSource, CardPlay? cardPlay)
    {
        return IsOriginalDamage(damage, out _)
            ? DamageRepeated(choiceContext, [target], MultiHitKeyword.GetHitCount(damage.BaseValue), props, cardSource.Owner.Creature, cardSource, cardPlay)
            : Sts2Compatibility.Damage(choiceContext, [target], damage.BaseValue, props, cardSource.Owner.Creature, cardSource, cardPlay);
    }

    public static bool DamageTargetsPrefix(
        PlayerChoiceContext choiceContext, IEnumerable<Creature> targets, DamageVar damageVar,
        Creature? dealer, CardModel? cardSource, CardPlay? cardPlay, ref Task<IEnumerable<DamageResult>> __result)
    {
        if (cardSource is null || !IsOriginalDamage(damageVar, out _))
            return true;
        __result = DamageRepeated(choiceContext, targets, MultiHitKeyword.GetHitCount(damageVar.BaseValue),
            damageVar.Props, dealer, cardSource, cardPlay);
        return false;
    }

    public static bool LegacyDamageTargetsPrefix(
        PlayerChoiceContext choiceContext, IEnumerable<Creature> targets, DamageVar damageVar,
        Creature? dealer, CardModel? cardSource, ref Task<IEnumerable<DamageResult>> __result) =>
        DamageTargetsPrefix(choiceContext, targets, damageVar, dealer, cardSource, null, ref __result);

    private static async Task<IEnumerable<DamageResult>> DamageRepeated(
        PlayerChoiceContext choiceContext, IEnumerable<Creature> targets, int count,
        ValueProp props, Creature? dealer, CardModel cardSource, CardPlay? cardPlay)
    {
        CardEffectAnimationScope.MarkAltHeavenlyResult(cardSource, count);
        decimal amount = XValueKeywordRuntime.GetInstanceValue(cardSource);
        List<Creature> remainingTargets = targets.ToList();
        List<DamageResult> results = [];
        for (int i = 0; i < count && dealer?.IsDead != true; i++)
        {
            remainingTargets.RemoveAll(target => !target.IsAlive);
            if (remainingTargets.Count == 0)
                break;
            results.AddRange(await Sts2Compatibility.Damage(choiceContext, remainingTargets, amount, props, dealer, cardSource, cardPlay));
        }
        return results;
    }
}
