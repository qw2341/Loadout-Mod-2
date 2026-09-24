#nullable enable

namespace Loadout.UI.Screens.Controls;

using System;
using Loadout.Services.CardModification;
using Loadout.UI.Managers;

public partial class NLoadoutCardCostStepper : NLoadoutDropdownStepper
{
    public void Init(LoadoutGeneratedCardCost selected, Action<LoadoutGeneratedCardCost> changed) =>
        Init([
            new(LoadoutGeneratedCardCost.None.ToString(), LocMan.Loc("CARD_MOD_CARD_FREE_NO", "No")),
            new(LoadoutGeneratedCardCost.FreeThisTurn.ToString(), LocMan.Loc("CARD_MOD_CARD_FREE_TURN", "Free This Turn")),
            new(LoadoutGeneratedCardCost.FreeThisCombat.ToString(), LocMan.Loc("CARD_MOD_CARD_FREE_COMBAT", "Free This Combat"))
        ], selected.ToString(), value =>
        {
            if (Enum.TryParse(value, out LoadoutGeneratedCardCost cost))
                changed(cost);
        });
}
