#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Loadout.Services.Compatibility;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

[HarmonyPatch]
internal static class UnblockedDamageKeywordDispatcher
{
    private static MethodBase TargetMethod() =>
        Sts2Compatibility.MultiTargetDamageMethod;

    [HarmonyPostfix]
    private static void Postfix(
        CardModel? __5,
        ref Task<IEnumerable<DamageResult>> __result)
    {
        CardModel? source = __5;
        if (source is null)
            return;

        IReadOnlyList<LoadoutKeywordModel> effects = LoadoutKeywordRegistry.ResolveActiveModels(
            source, model => model.HasUnblockedDamageEffect);
        if (effects.Count > 0)
            __result = Apply(__result, source, effects);
    }

    private static async Task<IEnumerable<DamageResult>> Apply(
        Task<IEnumerable<DamageResult>> original,
        CardModel source,
        IReadOnlyList<LoadoutKeywordModel> effects)
    {
        IEnumerable<DamageResult> results = await original;
        decimal unblockedDamage = 0m;
        foreach (DamageResult result in results)
            unblockedDamage += result.UnblockedDamage;

        if (unblockedDamage <= 0 || source.Owner.Creature.IsDead)
            return results;

        foreach (LoadoutKeywordModel effect in effects)
        {
            await effect.AfterUnblockedDamageDealt(
                source,
                unblockedDamage);
        }

        return results;
    }
}
