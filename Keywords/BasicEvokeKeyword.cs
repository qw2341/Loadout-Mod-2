#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

public sealed class BasicEvokeKeyword : LoadoutBasicKeywordModel
{
    public const string AmountVar = "LoadoutBasicEvoke";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition> VariableDefinitions =
    [
        new(AmountVar, 1m, int.MinValue, int.MaxValue, "DYNAMIC_VAR_LOADOUT_BASIC_EVOKE",
            (name, value) => new RepeatVar(name, decimal.ToInt32(value)))
    ];

    public static BasicEvokeKeyword Instance { get; } = new();

    private BasicEvokeKeyword() { }

    public override CardKeyword Keyword => LoadoutKeywords.BasicEvoke;
    public override string StorageKey => LoadoutKeywords.BasicEvokeKey;
    public override string TitleLocKey => "LOADOUT-BASIC_EVOKE.title";
    public override string? CardTextLocKey => "LOADOUT-BASIC_EVOKE.cardText";
    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars => VariableDefinitions;

    public override async Task AfterOnPlay(CardModel card, PlayerChoiceContext choiceContext,
        CardPlay cardPlay, object? capturedState)
    {
        int amount = Math.Max(0, GetAmount(card, AmountVar).IntValue);
        var combat = card.CombatState;
        var player = card.Owner;
        var queue = player.PlayerCombatState?.OrbQueue;
        if (amount == 0 || combat is null || CombatManager.Instance.IsOverOrEnding
            || player.Creature.IsDead || queue is null || queue.Orbs.Count == 0)
            return;

        await CreatureCmd.TriggerAnim(player.Creature, "Cast", player.Character.CastAnimDelay);
        for (int i = 0; i < amount; i++)
        {
            if (CombatManager.Instance.IsOverOrEnding || !ReferenceEquals(card.CombatState, combat)
                || player.Creature.IsDead || queue.Orbs.Count == 0)
                return;
            bool last = i == amount - 1;
            await OrbCmd.EvokeNext(choiceContext, player, dequeue: last);
            if (!last)
                await Cmd.CustomScaledWait(0.1f, 0.25f);
        }
    }

    public static void OrbEvokeTypePostfix(CardModel __instance, ref OrbEvokeType __result)
    {
        if (__result == OrbEvokeType.None && LoadoutKeywords.Has(__instance, LoadoutKeywords.BasicEvoke))
            __result = OrbEvokeType.Front;
    }
}
