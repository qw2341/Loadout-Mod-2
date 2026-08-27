#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

public sealed class BasicBlockKeyword : LoadoutBasicKeywordModel
{
    public const string AmountVar = "LoadoutBasicBlock";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                int.MinValue,
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

    public override bool ReportsGainsBlock => true;

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

internal static class LoadoutBasicKeywordGainsBlockPatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        return LoadoutKeywordModel.GetCardPropertyGetters(
            nameof(CardModel.GainsBlock));
    }

    [HarmonyPostfix]
    public static void Postfix(CardModel __instance, ref bool __result)
    {
        __result |= LoadoutKeywordRegistry.ReportsGainsBlock(__instance);
    }
}
