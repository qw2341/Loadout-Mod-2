#nullable enable

namespace Loadout.UI.Screens.Controls;

using System;
using System.Linq;
using Godot;
using Loadout.Keywords;
using MegaCrit.Sts2.Core.Entities.Cards;

public partial class NLoadoutPileTypeStepper : NLoadoutVariableControl
{
    public void Init(PileType pile, Action<PileType> changed)
    {
        NLoadoutDropdown dropdown = new()
        {
            CustomMinimumSize = new Vector2(218f, 40f),
            ButtonHeight = 40f,
            LabelMinFontSize = 15,
            LabelMaxFontSize = 20,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        PileType[] piles = [PileType.Hand, PileType.Draw, PileType.Discard, PileType.Exhaust, PileType.Deck];
        dropdown.SetItems(string.Empty, piles.Select(value =>
            new LoadoutDropdownOption(value.ToString(), LoadoutCardKeywordState.GetPileLabel(value))), pile.ToString());
        dropdown.SelectedItemChanged += selected =>
        {
            if (Enum.TryParse(selected, out PileType value))
                changed(value);
        };
        AddChild(dropdown);
    }
}
