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

public sealed class BasicOrbSlotsKeyword : LoadoutBasicKeywordModel
{
    public const string AmountVar = "OrbSlots";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition> VariableDefinitions =
    [
        new(AmountVar, 1m, int.MinValue, int.MaxValue, "DYNAMIC_VAR_ORB_SLOTS")
    ];

    public static BasicOrbSlotsKeyword Instance { get; } = new();

    private BasicOrbSlotsKeyword() { }

    public override CardKeyword Keyword => LoadoutKeywords.BasicOrbSlots;
    public override string StorageKey => LoadoutKeywords.BasicOrbSlotsKey;
    public override string TitleLocKey => "LOADOUT-BASIC_ORB_SLOTS.title";
    public override string? CardTextLocKey => "LOADOUT-BASIC_ORB_SLOTS.cardText";
    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars => VariableDefinitions;

    public override async Task AfterOnPlay(CardModel card, PlayerChoiceContext choiceContext,
        CardPlay cardPlay, object? capturedState)
    {
        int amount = GetAmount(card, AmountVar).IntValue;
        var combat = card.CombatState;
        var player = card.Owner;
        if (amount == 0 || combat is null || CombatManager.Instance.IsOverOrEnding
            || player.Creature.IsDead || player.PlayerCombatState is null)
            return;

        await CreatureCmd.TriggerAnim(player.Creature, "PowerUp", player.Character.PowerUpAnimDelay);
        if (CombatManager.Instance.IsOverOrEnding || !ReferenceEquals(card.CombatState, combat)
            || player.Creature.IsDead || player.PlayerCombatState is null)
            return;

        if (amount > 0)
            await OrbCmd.AddSlots(player, amount);
        else
            OrbCmd.RemoveSlots(player, (int)Math.Min(-(long)amount, int.MaxValue));
    }
}
