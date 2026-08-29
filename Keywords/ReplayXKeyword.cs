#nullable enable

namespace Loadout.Keywords;

using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

public sealed class ReplayXKeyword : LoadoutKeywordModel
{
    public static ReplayXKeyword Instance { get; } = new();

    private ReplayXKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.ReplayX;

    public override string StorageKey => LoadoutKeywords.ReplayXKey;

    public override string TitleLocKey => "LOADOUT-REPLAY_X.title";

    public static int ResolveReplayCount(CardModel card)
    {
        if (card.EnergyCost.CostsX)
            return card.ResolveEnergyXValue();

        ICombatState? combatState = card.CombatState;
        if (combatState is null)
            return 0;

        return Hook.ModifyXValue(
                combatState,
                card,
                card.Owner.PlayerCombatState?.Energy ?? 0);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyCardPlayCount))]
internal static class ReplayXModifyCardPlayCountPatch
{
    [HarmonyPrefix]
    private static void Prefix(CardModel card, ref int playCount)
    {
        if (!LoadoutKeywords.Has(card, LoadoutKeywords.ReplayX))
            return;

        playCount += ReplayXKeyword.ResolveReplayCount(card);
    }
}
