#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.ValueProps;

public sealed class BasicOstyAttackKeyword : LoadoutBasicKeywordModel
{
    public const string AmountVar = "LoadoutBasicOstyAttack";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_BASIC_OSTY_ATTACK",
                (name, value) => new OstyDamageVar(name, value, ValueProp.Move))
        ];

    public static BasicOstyAttackKeyword Instance { get; } = new();

    private BasicOstyAttackKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.BasicOstyAttack;

    public override string StorageKey => LoadoutKeywords.BasicOstyAttackKey;

    public override string TitleLocKey => "LOADOUT-BASIC_OSTY_ATTACK.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_OSTY_ATTACK.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override bool ChangesTargeting => true;

    public override async Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        DynamicVar amount = GetAmount(card, AmountVar);
        if (amount.BaseValue <= 0m || card.CombatState is null)
            return;

        Creature? target = cardPlay.Target;
        if (target is null && card.TargetType == TargetType.AnyPlayer)
            target = card.Owner.Creature;
        else if (target is null && card.TargetType == TargetType.Osty)
            target = card.Owner.Osty;

        if (target is null || target.IsDead)
            return;

        if (Osty.CheckMissingWithAnim(card.Owner) || card.Owner.Osty is not { } osty)
            return;

        await DamageCmd.Attack(amount.BaseValue)
            .FromOsty(osty, card, cardPlay)
            .Targeting(target)
            .WithAttackerAnim("attack_poke", 0.3f)
            .WithHitFx("vfx/vfx_attack_blunt", null, "blunt_attack.mp3")
            .Execute(choiceContext);
    }
}
