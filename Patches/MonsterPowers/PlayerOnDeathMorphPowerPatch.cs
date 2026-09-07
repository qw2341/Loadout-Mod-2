#nullable enable

namespace Loadout.Patches.MonsterPowers;

using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Loadout.Services.Morphing;
using Loadout.Services.PowerGiver;
using Loadout.Services.Targets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;

internal static class PlayerOnDeathMorphPowerRuntime
{
    public static PowerModel? SelectRevivalPower(Creature creature)
    {
        // Explicit priority prevents multiple summon powers from reviving the
        // player during the same death: Infested, then Stock, then Surprise.
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
        return power is InfestedPower or StockPower or SurprisePower;
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

        MonsterModel form = ResolveForm(power, combatState);
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

    private static MonsterModel ResolveForm(
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
