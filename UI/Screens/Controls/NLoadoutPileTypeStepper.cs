#nullable enable

namespace Loadout.UI.Screens.Controls;

using System;
using System.Linq;
using Loadout.Keywords;
using MegaCrit.Sts2.Core.Entities.Cards;

public partial class NLoadoutPileTypeStepper : NLoadoutDropdownStepper
{
    public void Init(PileType pile, Action<PileType> changed)
    {
        PileType[] piles = [PileType.Hand, PileType.Draw, PileType.Discard, PileType.Exhaust, PileType.Deck];
        Init(piles.Select(value =>
            new LoadoutDropdownOption(value.ToString(), LoadoutCardKeywordState.GetPileLabel(value))), pile.ToString(), selected =>
        {
            if (Enum.TryParse(selected, out PileType value))
                changed(value);
        });
    }
}
