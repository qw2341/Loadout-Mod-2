#nullable enable

namespace Loadout.UI.Screens.Controls;

using System;
using Godot;
using Loadout.Keywords;
using Loadout.PanelItems;
using Loadout.UI.Managers;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;

public partial class NLoadoutCardSelector : NLoadoutVariableControl
{
    private string _cardId = string.Empty;
    private bool _upgraded;
    public event Action? SelectRequested;

    public void Init(string cardId, bool upgraded)
    {
        _cardId = cardId;
        _upgraded = upgraded;
        if (IsNodeReady())
            Rebuild();
    }

    public override void _Ready() => Rebuild();

    private void Rebuild()
    {
        foreach (Node child in GetChildren())
        {
            RemoveChild(child);
            child.QueueFree();
        }
        AddThemeConstantOverride("separation", 6);
        MegaLabel title = new()
        {
            Text = LoadoutCardKeywordState.GetCardTitle(_cardId, _upgraded),
            CustomMinimumSize = new Vector2(112f, 40f),
            AutoSizeEnabled = true,
            MinFontSize = 13,
            MaxFontSize = 19,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Pass
        };
        title.AddThemeFontOverride("font", CommonHelpers.LoadGameFont());
        title.AddThemeColorOverride("font_color", StsColors.cream);
        AddChild(title);
        Button select = new()
        {
            Text = LocMan.Loc("CARD_MOD_SELECT_CARD", "Select Card"),
            CustomMinimumSize = new Vector2(112f, 40f),
            MouseFilter = MouseFilterEnum.Stop
        };
        select.AddThemeFontOverride("font", CommonHelpers.LoadGameFont());
        select.AddThemeFontSizeOverride("font_size", 17);
        select.AddThemeColorOverride("font_color", StsColors.gold);
        select.Pressed += () => SelectRequested?.Invoke();
        AddChild(select);
    }
}
