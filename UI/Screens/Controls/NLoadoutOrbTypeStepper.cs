#nullable enable

namespace Loadout.UI.Screens.Controls;

using System;
using System.Linq;
using Loadout.Keywords;
using Loadout.UI.Managers;

public partial class NLoadoutOrbTypeStepper : NLoadoutDropdownStepper
{
    public void Init(string selected, Action<string> changed)
    {
        var options = LoadoutBasicOrbKeywordModel.GetAvailableOrbs()
            .OrderBy(orb => orb.Title.GetFormattedText(), StringComparer.OrdinalIgnoreCase)
            .Select(orb => new LoadoutDropdownOption(orb.Id.ToString(), orb.Title.GetFormattedText(),
                () => [orb.DumbHoverTip], orb.Icon)).ToList();
        if (!string.IsNullOrEmpty(selected) && options.All(option => option.Id != selected))
            options.Add(new(selected, LoadoutBasicOrbKeywordModel.GetOrbTitle(selected)));
        Init(options, selected, changed);
    }
}
