#nullable enable

namespace Loadout.Patches.MonsterPowers;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Loadout.Services.Morphing;
using Loadout.Services.PowerGiver;
using Loadout.Services.Targets;
using MegaCrit.Sts2.Core.Audio;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;

internal static class PlayerOnDeathMorphPowerRuntime
{
    private const string TestSubjectPhaseTwoSfx =
        "event:/sfx/enemy/enemy_attacks/test_subject/test_subject_revive_two_heads";
    private const string TestSubjectPhaseThreeSfx =
        "event:/sfx/enemy/enemy_attacks/test_subject/test_subject_revive_three_heads";

    private static readonly MethodInfo GetTestSubjectEnrageAmountMethod =
        AccessTools.PropertyGetter(typeof(TestSubject), "EnrageAmount")
        ?? throw new MissingMethodException(typeof(TestSubject).FullName, "get_EnrageAmount");

    public static PowerModel? SelectRevivalPower(Creature creature)
    {
        if (creature.GetPower<AdaptablePower>() is { Amount: > 0 } adaptable
            && BottledMonsterMorphService.GetTestSubjectMorphPhase(
                creature.Player) < 3)
        {
            return adaptable;
        }

        // Only one fallback revival may run per death.
        if (creature.GetPower<InfestedPower>() is { Amount: > 0 } infested)
            return infested;

        if (creature.GetPower<StockPower>() is { Amount: > 0 } stock)
            return stock;

        return creature.GetPower<SurprisePower>() is { Amount: > 0 } surprise
            ? surprise
            : null;
    }

    public static bool IsSupported(PowerModel power)
    {
        return power is AdaptablePower
            or InfestedPower
            or StockPower
            or SurprisePower;
    }

    public static async Task ReviveAfterNative(
        Task nativeTask,
        PowerModel power,
        Creature creature)
    {
        await nativeTask;

        if (!creature.IsPlayer
            || power.Owner != creature
            || creature.Player is null
            || creature.CombatState is not { } combatState)
        {
            return;
        }

        if (power is AdaptablePower adaptable)
        {
            await ReviveAsTestSubject(adaptable, creature, combatState);
            return;
        }

        MonsterModel form = ResolveSummonedForm(power, combatState);
        int maxHp = GetScaledMaxHp(form, combatState);

        await PowerCmd.Decrement(power);
        PowerGiverStateService.ConsumePositivePlayerCounter(
            power.Id.ToString(),
            creature.Player.NetId);

        await BottledMonsterMorphService.ApplySynchronizedMorphAsync(
            form.Id,
            LoadoutTargetSelection.ForPlayer(creature.Player.NetId));
        await CreatureCmd.SetMaxAndCurrentHp(creature, maxHp);
    }

    private static MonsterModel ResolveSummonedForm(
        PowerModel power,
        ICombatState combatState)
    {
        if (power is InfestedPower)
            return ModelDb.Monster<Wriggler>();

        if (power is StockPower)
            return ModelDb.Monster<Axebot>();

        return combatState.RunState.Rng.Niche.NextBool()
            ? ModelDb.Monster<FatGremlin>()
            : ModelDb.Monster<SneakyGremlin>();
    }

    private static async Task ReviveAsTestSubject(
        AdaptablePower adaptable,
        Creature creature,
        ICombatState combatState)
    {
        TestSubject testSubject = ModelDb.Monster<TestSubject>();
        int currentPhase = BottledMonsterMorphService
            .GetTestSubjectMorphPhase(creature.Player);
        int nextPhase = Math.Clamp(currentPhase + 1, 1, 3);
        int baseHp = nextPhase switch
        {
            1 => testSubject.FirstFormHp,
            2 => testSubject.SecondFormHp,
            _ => testSubject.ThirdFormHp
        };
        int maxHp = (int)Creature.ScaleHpForMultiplayer(
            baseHp,
            combatState.Encounter,
            combatState.Players.Count,
            combatState.RunState.CurrentActIndex);

        await BottledMonsterMorphService.ApplySynchronizedTestSubjectMorphAsync(
            nextPhase,
            LoadoutTargetSelection.ForPlayer(creature.Player!.NetId));

        if (nextPhase == 2)
            SfxCmd.Play(TestSubjectPhaseTwoSfx);
        else if (nextPhase == 3)
            SfxCmd.Play(TestSubjectPhaseThreeSfx);

        await CreatureCmd.SetMaxAndCurrentHp(creature, maxHp);
        if (nextPhase > 1)
            await Cmd.Wait(1.95f);

        bool adaptableWasFormPower = await ApplyTestSubjectPhasePowers(
            creature,
            testSubject,
            nextPhase);
        if (nextPhase < 3)
            return;

        if (!adaptableWasFormPower)
        {
            PowerGiverStateService.ConsumePositivePlayerCounter(
                adaptable.Id.ToString(),
                creature.Player.NetId);
        }

        if (creature.GetPower<AdaptablePower>() is { } currentAdaptable)
            await PowerCmd.Remove(currentAdaptable);
    }

    private static async Task<bool> ApplyTestSubjectPhasePowers(
        Creature creature,
        TestSubject testSubject,
        int phase)
    {
        ulong playerNetId = creature.Player!.NetId;
        IReadOnlyDictionary<string, int> previous =
            BottledMonsterMorphService.GetReplacementFormPowerContributions(
                playerNetId);
        string adaptableId = ModelDb.Power<AdaptablePower>().Id.ToString();
        int adaptableContribution = previous.GetValueOrDefault(adaptableId);

        Dictionary<string, int> next = new(StringComparer.Ordinal);
        if (phase < 3 && adaptableContribution > 0)
            next[adaptableId] = adaptableContribution;

        switch (phase)
        {
            case 1:
                next[ModelDb.Power<EnragePower>().Id.ToString()] =
                    (int)GetTestSubjectEnrageAmountMethod.Invoke(
                        testSubject,
                        null)!;
                break;
            case 2:
                next[ModelDb.Power<PainfulStabsPower>().Id.ToString()] = 1;
                break;
            case 3:
                next[ModelDb.Power<NemesisPower>().Id.ToString()] = 1;
                break;
        }

        foreach (string powerId in previous.Keys
                     .Concat(next.Keys)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(id => id, StringComparer.Ordinal))
        {
            int delta = next.GetValueOrDefault(powerId)
                        - previous.GetValueOrDefault(powerId);
            if (delta != 0)
            {
                await PowerGiverStateService.AdjustCounterForPlayerAsync(
                    powerId,
                    delta,
                    creature.Player);
            }
        }

        BottledMonsterMorphService.SetReplacementFormPowerContributions(
            playerNetId,
            next);
        return adaptableContribution > 0;
    }

    private static int GetScaledMaxHp(
        MonsterModel form,
        ICombatState combatState)
    {
        int baseHp = combatState.RunState.Rng.Niche.NextInt(
            form.MinInitialHp,
            form.MaxInitialHp + 1);
        decimal scaledHp = Creature.ScaleHpForMultiplayer(
            baseHp,
            combatState.Encounter,
            combatState.Players.Count,
            combatState.RunState.CurrentActIndex);
        return (int)scaledHp;
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.ShouldDie))]
public static class PlayerOnDeathMorphSelectionPatch
{
    [HarmonyPostfix]
    public static void Postfix(
        Creature creature,
        ref AbstractModel? preventer,
        ref bool __result)
    {
        if (!__result || !creature.IsPlayer)
            return;

        preventer = PlayerOnDeathMorphPowerRuntime.SelectRevivalPower(creature);
        if (preventer is not null)
            __result = false;
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPreventingDeath))]
public static class PlayerOnDeathMorphExecutionPatch
{
    [HarmonyPostfix]
    public static void Postfix(
        AbstractModel preventer,
        Creature creature,
        ref Task __result)
    {
        if (preventer is PowerModel power
            && PlayerOnDeathMorphPowerRuntime.IsSupported(power)
            && power.Owner == creature
            && creature.IsPlayer)
        {
            __result = PlayerOnDeathMorphPowerRuntime.ReviveAfterNative(
                __result,
                power,
                creature);
        }
    }
}

[HarmonyPatch]
public static class PlayerOnDeathNativeSummonSuppressionPatch
{
    [HarmonyTargetMethods]
    public static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.DeclaredMethod(
            typeof(InfestedPower),
            nameof(InfestedPower.AfterDeath));
        yield return AccessTools.DeclaredMethod(
            typeof(StockPower),
            nameof(StockPower.AfterDeath));
        yield return AccessTools.DeclaredMethod(
            typeof(SurprisePower),
            nameof(SurprisePower.AfterDeath));
        yield return AccessTools.DeclaredMethod(
            typeof(AdaptablePower),
            nameof(AdaptablePower.AfterDeath));
    }

    [HarmonyPrefix]
    public static bool Prefix(PowerModel __instance, ref Task __result)
    {
        if (!__instance.Owner.IsPlayer)
            return true;

        __result = Task.CompletedTask;
        return false;
    }
}
