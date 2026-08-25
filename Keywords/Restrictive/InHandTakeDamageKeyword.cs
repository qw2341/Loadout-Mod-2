#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

public sealed class InHandTakeDamageKeyword : LoadoutRestrictiveKeywordModel
{
    public const string AmountVar = "LoadoutRestrictiveDamage";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                AmountVar,
                1m,
                0,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_RESTRICTIVE_DAMAGE",
                (name, value) => new DamageVar(
                    name,
                    value,
                    ValueProp.Unpowered | ValueProp.Move))
        ];

    public static InHandTakeDamageKeyword Instance { get; } = new();

    private InHandTakeDamageKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.InHandTakeDamage;

    public override string StorageKey => LoadoutKeywords.InHandTakeDamageKey;

    public override string TitleLocKey =>
        "LOADOUT-RESTRICTIVE_IN_HAND_TAKE_DAMAGE.title";

    public override string? CardTextLocKey =>
        "LOADOUT-RESTRICTIVE_IN_HAND_TAKE_DAMAGE.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override bool HasTurnEndInHandEffect => true;

    public override async Task AfterTurnEndInHand(
        CardModel card,
        PlayerChoiceContext choiceContext)
    {
        if (GetAmount(card, AmountVar) is not DamageVar damage
            || damage.BaseValue <= 0m)
        {
            return;
        }

        await CreatureCmd.Damage(
            choiceContext,
            card.Owner.Creature,
            damage,
            card,
            null);
    }
}
