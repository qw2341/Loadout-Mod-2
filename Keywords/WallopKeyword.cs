#nullable enable

namespace Loadout.Keywords;

using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

public sealed class WallopKeyword : LoadoutKeywordModel
{
    public static WallopKeyword Instance { get; } = new();

    private WallopKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.Wallop;

    public override string StorageKey => LoadoutKeywords.WallopKey;

    public override string TitleLocKey => "LOADOUT-WALLOP.title";

    public override LoadoutKeywordPresentation Presentation =>
        LoadoutKeywordPresentation.DescriptionOnly;

    public override string? CardTextLocKey => "LOADOUT-WALLOP.cardText";

    public override bool HasUnblockedDamageEffect => true;

    public override Task AfterUnblockedDamageDealt(
        CardModel card,
        decimal unblockedDamage)
    {
        return CreatureCmd.GainBlock(
            card.Owner.Creature,
            unblockedDamage,
            ValueProp.Move,
            null);
    }
}
