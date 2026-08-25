#nullable enable

namespace Loadout.Keywords;

using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

public sealed class DoubleDamageOnPlayKeyword : LoadoutImprovementKeywordModel
{
    public static DoubleDamageOnPlayKeyword Instance { get; } = new();

    private DoubleDamageOnPlayKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.DoubleDamageOnPlay;

    public override string StorageKey => LoadoutKeywords.DoubleDamageOnPlayKey;

    public override string TitleLocKey =>
        "LOADOUT-IMPROVEMENT_DOUBLE_DAMAGE_ON_PLAY.title";

    public override string? CardTextLocKey =>
        "LOADOUT-IMPROVEMENT_DOUBLE_DAMAGE_ON_PLAY.cardText";

    public override bool HasOnPlayEffect => true;

    public override Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        MultiplyDamage(card, 2m);
        return Task.CompletedTask;
    }
}
