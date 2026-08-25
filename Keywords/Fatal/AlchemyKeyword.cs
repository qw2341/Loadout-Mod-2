#nullable enable

namespace Loadout.Keywords;

using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

public sealed class AlchemyKeyword : LoadoutFatalKeywordModel
{
    public static AlchemyKeyword Instance { get; } = new();

    private AlchemyKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.Alchemy;

    public override string StorageKey => LoadoutKeywords.AlchemyKey;

    public override string TitleLocKey => "LOADOUT-FATAL_ALCHEMY.title";

    public override string? CardTextLocKey => "LOADOUT-FATAL_ALCHEMY.cardText";

    public override async Task AfterFatal(
        CardModel card,
        PlayerChoiceContext choiceContext,
        int fatalCount)
    {
        for (int potionIndex = 0; potionIndex < fatalCount; potionIndex++)
        {
            PotionModel potion = PotionFactory.CreateRandomPotionInCombat(
                card.Owner,
                card.Owner.RunState.Rng.CombatPotionGeneration);
            await PotionCmd.TryToProcure(potion.ToMutable(), card.Owner);
        }
    }
}
