#nullable enable

namespace Loadout.Cards;

using BaseLib.Abstracts;
using Loadout.Patches.Cards.CardModification;
using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class CreatedCard : CustomCardModel
{
    public const string BlankPortraitPath = "res://Loadout/images/cards/created_card_blank.tres";

    public CreatedCard() : base(0, CardType.Skill, CardRarity.Common, TargetType.Self, autoAdd: false)
    {
    }

    public override string PortraitPath => ResolvePortrait(beta: false);
    public override string BetaPortraitPath => ResolvePortrait(beta: true);

    private string ResolvePortrait(bool beta) =>
        CardModificationRuntime.TryGetPortraitPath(this, beta, BlankPortraitPath, out string path)
            ? path
            : BlankPortraitPath;
}
