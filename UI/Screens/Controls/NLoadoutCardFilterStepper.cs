#nullable enable

namespace Loadout.UI.Screens.Controls;

using System;
using System.Linq;
using Loadout.PanelItems;
using Loadout.UI.Managers;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;

public partial class NLoadoutCardFilterStepper : NLoadoutDropdownStepper
{
    public void InitUpgraded(bool selected, Action<bool> changed) =>
        Init([
            new(bool.FalseString, LocMan.Loc("CARD_MOD_RANDOM_CARD_UPGRADE_NO", "No")),
            new(bool.TrueString, LocMan.Loc("CARD_MOD_RANDOM_CARD_UPGRADE_YES", "Yes"))
        ], selected.ToString(), value =>
        {
            if (bool.TryParse(value, out bool upgraded))
                changed(upgraded);
        });

    public void InitPool(string selected, Action<string> changed) =>
        Init(CardPrinter.BuildOrderedCardPools().Select(pool => new LoadoutDropdownOption(
                pool.Id.ToString(), CommonHelpers.GetPoolLabel(pool),
                IconFactory: () => CommonHelpers.GetPoolClassIcon(pool)))
            .Prepend(new LoadoutDropdownOption(string.Empty, LocMan.Loc("ALL", "All"))), selected, changed);

    public void InitRarity(CardRarity selected, Action<CardRarity> changed) =>
        Init(CardFactory.FilterForCombat(ModelDb.AllCards).Select(card => card.Rarity)
            .Where(rarity => rarity != CardRarity.None).Distinct()
            .OrderBy(CardPrinter.GetCardRaritySortValue).ThenBy(rarity => rarity)
            .Select(rarity => new LoadoutDropdownOption(rarity.ToString(), CardPrinter.GetCardRarityLabel(rarity),
                TextColor: CommonHelpers.GetRarityCardTitleColor(rarity)))
            .Prepend(new LoadoutDropdownOption(CardRarity.None.ToString(), LocMan.Loc("ALL", "All"))),
            selected.ToString(), value =>
            {
                if (Enum.TryParse(value, out CardRarity rarity))
                    changed(rarity);
            });
}
