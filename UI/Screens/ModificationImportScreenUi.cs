#nullable enable

namespace Loadout.UI.Screens;

using System;
using Godot;
using Loadout.PanelItems;
using Loadout.UI.Managers;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

public static class ModificationImportScreenUi
{
    public const float BottomBarHeight = 126f;

    public static void Install(
        NGenericSelectScreen screen,
        Action keepLocal,
        Action merge,
        Action acceptIncoming)
    {
        MarginContainer margin = new()
        {
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        margin.AddThemeConstantOverride("margin_left", 28);
        margin.AddThemeConstantOverride("margin_top", 20);
        margin.AddThemeConstantOverride("margin_right", 230);
        margin.AddThemeConstantOverride("margin_bottom", 18);
        HBoxContainer buttons = new()
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        buttons.AddThemeConstantOverride("separation", 18);
        buttons.AddChild(CreateButton(
            "KeepLocal",
            LocMan.Loc("MOD_IMPORT_KEEP_LOCAL", "Keep All Local Changes"),
            keepLocal));
        buttons.AddChild(CreateButton(
            "Merge",
            LocMan.Loc("MOD_IMPORT_MERGE", "Merge Non-Conflicting Changes"),
            merge));
        buttons.AddChild(CreateButton(
            "AcceptIncoming",
            LocMan.Loc("MOD_IMPORT_ACCEPT_INCOMING", "Accept All Incoming Changes"),
            acceptIncoming));
        margin.AddChild(buttons);
        screen.SetBottomActionControl(margin, BottomBarHeight);

        MegaLabel abortLabel = CreateLabel(
            LocMan.Loc("MOD_IMPORT_ABORT", "Abort Operation"),
            24,
            HorizontalAlignment.Left);
        abortLabel.Name = "AbortOperationLabel";
        abortLabel.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        abortLabel.OffsetLeft = 32f;
        abortLabel.OffsetTop = -72f;
        abortLabel.OffsetRight = 280f;
        abortLabel.OffsetBottom = -24f;
        screen.AddChild(abortLabel);

        if (screen.GetNodeOrNull<Control>("BackButton") is { } back)
        {
            back.OffsetLeft = -34f;
            back.OffsetTop = -154f;
            back.OffsetRight = 166f;
            back.OffsetBottom = -44f;
        }
        if (screen.GetNodeOrNull<Control>("ConfirmButton") is { } confirm)
        {
            confirm.OffsetLeft = -166f;
            confirm.OffsetTop = -154f;
            confirm.OffsetRight = 34f;
            confirm.OffsetBottom = -44f;
        }
    }

    public static MegaLabel CreateLabel(string text, int fontSize, HorizontalAlignment alignment)
    {
        MegaLabel label = new()
        {
            Text = text,
            AutoSizeEnabled = false,
            MinFontSize = Math.Max(14, fontSize - 5),
            MaxFontSize = fontSize,
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        label.AddThemeFontOverride("font", CommonHelpers.LoadGameFont("res://themes/kreon_bold_glyph_space_one.tres"));
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", StsColors.cream);
        label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.72f));
        label.AddThemeConstantOverride("outline_size", 10);
        return label;
    }

    public static void RefreshExactCardView(Control view, CardModel? model)
    {
        if (model is null || !GodotObject.IsInstanceValid(view))
            return;

        void Refresh()
        {
            if (!GodotObject.IsInstanceValid(view))
                return;
            if (CommonHelpers.TryFindDescendantOrSelf(view, out NCardHolder holder)
                && holder.CardNode is not null
                && GodotObject.IsInstanceValid(holder.CardNode))
            {
                holder.ReassignToCard(model, PileType.None, null, ModelVisibility.Visible);
                return;
            }
            if (CommonHelpers.TryFindDescendantOrSelf(view, out NCard card))
            {
                card.Model = model;
                card.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
            }
        }

        if (view.IsNodeReady())
            Refresh();
        else
            view.Connect(Node.SignalName.Ready, Callable.From(Refresh), (uint)GodotObject.ConnectFlags.OneShot);
    }

    private static NLoadoutSettingsActionButton CreateButton(string id, string label, Action action)
    {
        NLoadoutSettingsActionButton button = new()
        {
            CustomMinimumSize = new Vector2(300f, 72f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        button.Init(id, label);
        button.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => action()));
        return button;
    }
}
