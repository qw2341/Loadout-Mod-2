#nullable enable

namespace Loadout.UI.Screens.Controls;

using System;
using System.Linq;
using Godot;
using Loadout.Keywords;
using Loadout.PanelItems;
using Loadout.UI.Managers;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;

public partial class NLoadoutPowerSelector : NLoadoutVariableControl
{
    private string _powerId = string.Empty;

    public event Action? SelectRequested;

    public void Init(string powerId)
    {
        _powerId = powerId;
        if (IsNodeReady())
            Rebuild();
    }

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 6);
        Rebuild();
    }

    private void Rebuild()
    {
        foreach (Node child in GetChildren())
        {
            RemoveChild(child);
            child.QueueFree();
        }

        PowerModel? power = LoadoutPowerKeywordState.TryResolvePower(
            _powerId,
            out PowerModel resolved)
            ? resolved
            : null;
        TextureRect icon = new()
        {
            CustomMinimumSize = new Vector2(34f, 40f),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Texture = power is null ? null : PowerGiver.GetLivePowerIcon(power),
            MouseFilter = MouseFilterEnum.Pass
        };
        AddChild(icon);

        MegaLabel title = new()
        {
            Text = power is null
                ? GetReadableId(_powerId)
                : CommonHelpers.FormatPowerTitle(power),
            CustomMinimumSize = new Vector2(72f, 40f),
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
            Text = LocMan.Loc("CARD_MOD_SELECT_POWER", "Select Power"),
            CustomMinimumSize = new Vector2(112f, 40f),
            MouseFilter = MouseFilterEnum.Stop
        };
        select.AddThemeFontOverride("font", CommonHelpers.LoadGameFont());
        select.AddThemeFontSizeOverride("font_size", 17);
        select.AddThemeColorOverride("font_color", StsColors.gold);
        select.Pressed += () => SelectRequested?.Invoke();
        AddChild(select);

        if (power is not null)
        {
            CommonHelpers.AttachHoverTips(
                this,
                PowerGiver.CreateSafePowerHoverTips(power, null).ToList());
        }
    }

    private static string GetReadableId(string id)
    {
        string trimmed = (id ?? string.Empty).Trim();
        int separator = Math.Max(
            trimmed.LastIndexOf(':'),
            trimmed.LastIndexOf('/'));
        return separator >= 0 && separator + 1 < trimmed.Length
            ? trimmed[(separator + 1)..]
            : trimmed;
    }
}
