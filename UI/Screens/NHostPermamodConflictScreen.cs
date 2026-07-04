#nullable enable

namespace Loadout.UI.Screens;

using System;
using Godot;
using Loadout.PanelItems;
using Loadout.Services.CardModification;
using Loadout.UI.Managers;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

public partial class NHostPermamodConflictScreen : Control
{
    private const float DialogWidth = 520f;
    private const float ButtonHeight = 42f;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 180;
        BuildDialog();
    }

    private void BuildDialog()
    {
        ColorRect backstop = new()
        {
            Name = "Backstop",
            Color = new Color(0f, 0f, 0f, 0.62f),
            MouseFilter = MouseFilterEnum.Stop
        };
        backstop.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(backstop);

        PanelContainer panel = new()
        {
            Name = "Dialog",
            CustomMinimumSize = new Vector2(DialogWidth, 0f),
            MouseFilter = MouseFilterEnum.Stop
        };
        panel.AnchorLeft = 0.5f;
        panel.AnchorTop = 0.5f;
        panel.AnchorRight = 0.5f;
        panel.AnchorBottom = 0.5f;
        panel.OffsetLeft = -DialogWidth / 2f;
        panel.OffsetRight = DialogWidth / 2f;
        panel.OffsetTop = -190f;
        panel.OffsetBottom = 190f;

        StyleBoxFlat style = new()
        {
            BgColor = new Color(0.035f, 0.028f, 0.024f, 0.94f),
            BorderColor = StsColors.gold,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4
        };
        style.SetBorderWidthAll(2);
        style.SetContentMarginAll(14f);
        panel.AddThemeStyleboxOverride("panel", style);
        AddChild(panel);

        MarginContainer margin = new();
        margin.AddThemeConstantOverride("margin_left", 14);
        margin.AddThemeConstantOverride("margin_top", 14);
        margin.AddThemeConstantOverride("margin_right", 14);
        margin.AddThemeConstantOverride("margin_bottom", 14);
        panel.AddChild(margin);

        VBoxContainer content = new()
        {
            Name = "Content"
        };
        content.AddThemeConstantOverride("separation", 10);
        margin.AddChild(content);

        content.AddChild(CreateLabel(LocMan.Loc("HOST_PERMAMODS_TITLE", "Host Permamods"), 30, StsColors.gold));
        content.AddChild(CreateLabel(
            LocMan.Loc("HOST_PERMAMODS_BODY", "Choose how to import the host's permanent card modifications."),
            20,
            StsColors.cream));
        content.AddChild(CreateButton("keep_mine", LocMan.Loc("PERMAMODS_KEEP_MINE", "Keep Mine"), () => Apply(CardModificationPermanentImportMode.KeepMine)));
        content.AddChild(CreateButton("use_host", LocMan.Loc("PERMAMODS_USE_HOST", "Use Host"), () => Apply(CardModificationPermanentImportMode.UseHost)));
        content.AddChild(CreateButton("merge", LocMan.Loc("PERMAMODS_MERGE", "Merge Non-Conflicting"), () => Apply(CardModificationPermanentImportMode.MergeNonConflicting)));
        content.AddChild(CreateButton("cancel", LocMan.Loc("CANCEL", "Cancel"), Close));
    }

    private void Apply(CardModificationPermanentImportMode mode)
    {
        CardModificationMultiplayerSyncService.ApplyPendingHostPermanentSnapshot(mode);
        Close();
    }

    private void Close()
    {
        NLoadoutPanelRoot.Instance?.CloseTopScreen();
    }

    private static NLoadoutActionButton CreateButton(string id, string label, Action action)
    {
        NLoadoutActionButton button = new()
        {
            Name = CommonHelpers.MakeSafeNodeName($"{id}_button"),
            CustomMinimumSize = new Vector2(DialogWidth - 64f, ButtonHeight),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        button.Init(id, label);
        button.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => action()));
        return button;
    }

    private static MegaLabel CreateLabel(string text, int fontSize, Color color)
    {
        MegaLabel label = new()
        {
            AutoSizeEnabled = true,
            MinFontSize = Math.Max(12, fontSize - 5),
            MaxFontSize = fontSize,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = MouseFilterEnum.Ignore
        };
        label.AddThemeFontOverride("font", CommonHelpers.LoadGameFont("res://themes/kreon_bold_glyph_space_one.tres"));
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.5f));
        label.AddThemeConstantOverride("outline_size", 10);
        label.SetTextAutoSize(text);
        return label;
    }
}
