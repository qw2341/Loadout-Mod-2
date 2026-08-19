#nullable enable

namespace Loadout.Services.Actions;

using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;

public sealed record LoadoutMonsterSpawnRule
{
    public Action<MonsterModel, ICombatState>? BeforeSpawn { get; init; }
    public Func<PowerModel, bool>? PreserveSpawnedPowerWhenDuplicating { get; init; }
    public Func<PowerModel, bool>? CopySourcePowerWhenDuplicating { get; init; }
}

public static class LoadoutMonsterSpawnRules
{
    private static readonly IReadOnlyDictionary<Type, LoadoutMonsterSpawnRule> Rules =
        new Dictionary<Type, LoadoutMonsterSpawnRule>
        {
            [typeof(Aeonglass)] = new LoadoutMonsterSpawnRule
            {
                PreserveSpawnedPowerWhenDuplicating = power => power is WitheringPresencePower,
                CopySourcePowerWhenDuplicating = power => power is not WitheringPresencePower
            },
            [typeof(Chomper)] = new LoadoutMonsterSpawnRule
            {
                BeforeSpawn = static (monster, combatState) =>
                    ((Chomper)monster).ScreamFirst = combatState.RunState.Rng.MonsterAi.NextBool()
            }
        };

    public static void ApplyBeforeSpawn(MonsterModel monster, ICombatState combatState)
    {
        if (Rules.TryGetValue(monster.GetType(), out LoadoutMonsterSpawnRule? rule))
            rule.BeforeSpawn?.Invoke(monster, combatState);
    }

    public static bool PreserveSpawnedPowerWhenDuplicating(MonsterModel monster, PowerModel power)
    {
        return Rules.TryGetValue(monster.GetType(), out LoadoutMonsterSpawnRule? rule)
               && rule.PreserveSpawnedPowerWhenDuplicating?.Invoke(power) == true;
    }

    public static bool CopySourcePowerWhenDuplicating(MonsterModel monster, PowerModel power)
    {
        return !Rules.TryGetValue(monster.GetType(), out LoadoutMonsterSpawnRule? rule)
               || rule.CopySourcePowerWhenDuplicating?.Invoke(power) != false;
    }
}
