#nullable enable

namespace Loadout.Keywords;

using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

public sealed class LifestealKeyword : LoadoutKeywordModel
{
    public static LifestealKeyword Instance { get; } = new();

    private LifestealKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.Lifesteal;

    public override string StorageKey => LoadoutKeywords.LifestealKey;

    public override string TitleLocKey => "LOADOUT-LIFESTEAL.title";

    public override LoadoutKeywordPresentation Presentation =>
        LoadoutKeywordPresentation.DescriptionOnly;

    public override string? CardTextLocKey =>
        "LOADOUT-LIFESTEAL.cardText";

    public override bool HasUnblockedDamageEffect => true;

    public override Task AfterUnblockedDamageDealt(
        CardModel card,
        decimal unblockedDamage)
    {
        return CreatureCmd.Heal(card.Owner.Creature, unblockedDamage);
    }
}
