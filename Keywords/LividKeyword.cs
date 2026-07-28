#nullable enable

namespace Loadout.Keywords;

using System.Threading.Tasks;
using Loadout.Services.Compatibility;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

public sealed class LividKeyword : LoadoutKeywordModel
{
    public static LividKeyword Instance { get; } = new();

    private LividKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.Livid;

    public override string StorageKey => LoadoutKeywords.LividKey;

    public override string TitleLocKey => "LOADOUT-LIVID.title";

    public override bool HasOnPlayEffect => true;

    public override Task AfterOnPlay(
        CardModel card,
        CardPlay cardPlay,
        object? capturedState)
    {
        return Apply(card);
    }

    private static async Task Apply(CardModel source)
    {
        ICombatState? combatState = source.CombatState;
        if (combatState is null)
            return;

        // Mirrors OUTRAGE.OnPlay: every living player on the owner's team
        // receives a clone in discard through the native generated-card path.
        foreach (Creature teammate in combatState.GetTeammatesOf(source.Owner.Creature))
        {
            Player? player = teammate.Player;
            if (!teammate.IsAlive || !teammate.IsPlayer || player is null)
                continue;

            CardModel copy = Sts2Compatibility.CreateCloneForPlayer(source, player);
            CardPileAddResult result = await CardPileCmd.AddGeneratedCardToCombat(
                copy,
                PileType.Discard,
                source.Owner,
                CardPilePosition.Bottom);
            CardCmd.PreviewCardPileAdd(
                result,
                2.2f,
                CardPreviewStyle.HorizontalLayout);
        }
    }
}
