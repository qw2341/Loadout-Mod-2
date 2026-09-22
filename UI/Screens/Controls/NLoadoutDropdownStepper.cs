#nullable enable

namespace Loadout.UI.Screens.Controls;

using System;
using System.Collections.Generic;
using Godot;

public partial class NLoadoutDropdownStepper : NLoadoutVariableControl
{
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
