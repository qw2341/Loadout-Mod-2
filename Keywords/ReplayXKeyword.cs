#nullable enable

namespace Loadout.Keywords;

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

    public override LoadoutCardModificationFlags ModificationFlags =>
        LoadoutCardModificationFlags.FastAnimation;

    public override string StorageKey => LoadoutKeywords.ReplayXKey;

    public override string TitleLocKey => "LOADOUT-REPLAY_X.title";

    public static int ResolveReplayCount(CardModel card)
    {
        ICombatState? combatState = card.CombatState;
        if (combatState is null)
            return 0;
        card.EnergyCost.CapturedXValue = card.Owner.PlayerCombatState?.Energy ?? 0;
        return card.ResolveEnergyXValue();
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

        int additionalPlayCount = ReplayXKeyword.ResolveReplayCount(card);
        CardEffectAnimationScope.MarkRepeatedCardPlays(
            card,
            additionalPlayCount);
        playCount += additionalPlayCount;
    }
}
