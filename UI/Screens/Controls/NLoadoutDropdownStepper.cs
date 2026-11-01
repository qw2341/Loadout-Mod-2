#nullable enable

namespace Loadout.UI.Screens.Controls;

using System;
using System.Collections.Generic;
using Godot;
using Loadout.UI.Managers;

public partial class NLoadoutDropdownStepper : NLoadoutVariableControl
{
    public void Init(bool selected, Action<bool> changed) =>
        Init([
            new("false", LocMan.Loc("DROPDOWN_NO", "No")),
            new("true", LocMan.Loc("DROPDOWN_YES", "Yes"))
        ], selected ? "true" : "false", value => changed(value == "true"));

    public void Init(IEnumerable<LoadoutDropdownOption> options, string selected, Action<string> changed)
    {
        NLoadoutDropdown dropdown = new()
        {
            CustomMinimumSize = new Vector2(218f, 40f),
            ButtonHeight = 40f,
            LabelMinFontSize = 15,
            LabelMaxFontSize = 20,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        dropdown.SetItems(string.Empty, options, selected);
        dropdown.SelectedItemChanged += changed;
        AddChild(dropdown);
    }
}
