#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.ValueProps;

public sealed class BasicOstyAllAttackKeyword : LoadoutBasicKeywordModel
{
    public const string AmountVar = "LoadoutBasicOstyAllAttack";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_BASIC_OSTY_ALL_ATTACK",
                (name, value) => new OstyDamageVar(name, value, ValueProp.Move))
        ];

    public static BasicOstyAllAttackKeyword Instance { get; } = new();

    private BasicOstyAllAttackKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.BasicOstyAllAttack;

    public override string StorageKey => LoadoutKeywords.BasicOstyAllAttackKey;

    public override string TitleLocKey => "LOADOUT-BASIC_OSTY_ALL_ATTACK.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_OSTY_ALL_ATTACK.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override async Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        DynamicVar amount = GetAmount(card, AmountVar);
        if (amount.BaseValue <= 0m || card.CombatState is null)
            return;

        if (Osty.CheckMissingWithAnim(card.Owner) || card.Owner.Osty is not { } osty)
            return;

        await DamageCmd.Attack(amount.BaseValue)
            .FromOsty(osty, card, cardPlay)
            .TargetingAllOpponents(card.CombatState)
            .WithHitFx("vfx/vfx_attack_blunt", null, "blunt_attack.mp3")
            .Execute(choiceContext);
    }
}
