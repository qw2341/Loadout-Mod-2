#nullable enable

namespace Loadout.UI.Screens;

using System;
using System.Collections.Generic;
using Godot;
using Loadout.PanelItems;
using Loadout.Services.Configuration;
using Loadout.UI.Managers;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Relics;

public partial class NModificationConflictOverlay : Control
{
    private Action? _closed;

    public static void ShowCard(
        Control parent,
        CardModificationImportEntry entry,
        Action resolved)
    {
        NModificationConflictOverlay overlay = new() { Name = "CardModificationConflictChoice" };
        overlay.BuildCard(entry, resolved);
        parent.AddChild(overlay);
    }

    public static void ShowRelic(
        Control parent,
        RelicModificationImportEntry entry,
        bool allowChoice,
        Action resolved)
    {
        NModificationConflictOverlay overlay = new() { Name = "RelicModificationConflictChoice" };
        overlay.BuildRelic(entry, allowChoice, resolved);
        parent.AddChild(overlay);
    }

    private void Prepare(string title)
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 400;

        ColorRect background = new()
        {
            Color = new Color(0.015f, 0.02f, 0.025f, 0.97f),
            MouseFilter = MouseFilterEnum.Stop
        };
        background.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(background);

        var titleLabel = ModificationImportScreenUi.CreateLabel(title, 38, HorizontalAlignment.Center);
        titleLabel.SetAnchorsPreset(LayoutPreset.TopWide);
        titleLabel.OffsetTop = 22f;
        titleLabel.OffsetBottom = 84f;
        AddChild(titleLabel);

        NBackButton back = NLoadoutBackButtonFactory.Create();
        back.Name = "BackButton";
        back.OffsetTop = -154f;
        back.OffsetBottom = -44f;
        back.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => Close()));
        AddChild(back);

        var backLabel = ModificationImportScreenUi.CreateLabel(
            LocMan.Loc("BACK", "Back"),
            24,
            HorizontalAlignment.Left);
        backLabel.SetAnchorsPreset(LayoutPreset.BottomLeft);
        backLabel.OffsetLeft = 32f;
        backLabel.OffsetTop = -72f;
        backLabel.OffsetRight = 210f;
        backLabel.OffsetBottom = -24f;
        AddChild(backLabel);
    }

    private void BuildCard(CardModificationImportEntry entry, Action resolved)
    {
        Prepare(LocMan.Loc("MOD_IMPORT_CARD_CONFLICT", "Choose Card Version"));
        _closed = resolved;
        HBoxContainer row = CreateChoiceRow();
        AddChild(row);
        row.AddChild(CreateCardChoice(
            entry.GetLocalPreview(),
            LocMan.Loc("MOD_IMPORT_LOCAL_VERSION", "Local Version"),
            () =>
            {
                entry.SetDecision(ModificationImportDecision.KeepLocal);
                ResolveAndClose();
            }));
        row.AddChild(CreateCardChoice(
            entry.GetIncomingPreview(),
            LocMan.Loc("MOD_IMPORT_THEIR_VERSION", "Their Version"),
            () =>
            {
                entry.SetDecision(ModificationImportDecision.UseIncoming);
                ResolveAndClose();
            }));
    }

    private void BuildRelic(
        RelicModificationImportEntry entry,
        bool allowChoice,
        Action resolved)
    {
        Prepare(allowChoice
            ? LocMan.Loc("MOD_IMPORT_RELIC_CONFLICT", "Choose Relic Version")
            : LocMan.Loc("MOD_IMPORT_RELIC_DETAILS", "Relic Modification"));
        _closed = resolved;
        HBoxContainer row = CreateChoiceRow();
        AddChild(row);
        if (allowChoice)
        {
            row.AddChild(CreateRelicChoice(
                entry.GetLocalPreview(),
                LocMan.Loc("MOD_IMPORT_LOCAL_VERSION", "Local Version"),
                () =>
                {
                    entry.SetDecision(ModificationImportDecision.KeepLocal);
                    ResolveAndClose();
                }));
        }
        row.AddChild(CreateRelicChoice(
            entry.GetIncomingPreview(),
            LocMan.Loc("MOD_IMPORT_THEIR_VERSION", "Their Version"),
            allowChoice
                ? () =>
                {
                    entry.SetDecision(ModificationImportDecision.UseIncoming);
                    ResolveAndClose();
                }
                : null));
    }

    private static HBoxContainer CreateChoiceRow()
    {
        HBoxContainer row = new()
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        row.SetAnchorsPreset(LayoutPreset.FullRect);
        row.OffsetLeft = 180f;
        row.OffsetTop = 100f;
        row.OffsetRight = -180f;
        row.OffsetBottom = -130f;
        row.AddThemeConstantOverride("separation", 150);
        return row;
    }

    private static Control CreateCardChoice(CardModel? model, string label, Action choose)
    {
        VBoxContainer column = CreateColumn();
        Control mount = new()
        {
            CustomMinimumSize = new Vector2(420f, 600f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        column.AddChild(mount);
        if (model is not null && NCard.Create(model) is { } card)
        {
            NPreviewCardHolder? holder = NPreviewCardHolder.Create(card, showHoverTips: true, scaleOnHover: true);
            if (holder is not null)
            {
                holder.Position = new Vector2(210f, 300f);
                holder.Scale = Vector2.One * 0.9f;
                holder.Connect(NCardHolder.SignalName.Pressed, Callable.From<NCardHolder>(_ => choose()));
                mount.AddChild(holder);
                card.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
            }
        }
        column.AddChild(CreateVersionLabel(label));
        return column;
    }

    private static Control CreateRelicChoice(RelicModel model, string label, Action? choose)
    {
        VBoxContainer column = CreateColumn();
        Control mount = new()
        {
            CustomMinimumSize = new Vector2(430f, 320f),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        column.AddChild(mount);
        NRelicBasicHolder? holder = NRelicBasicHolder.Create(model);
        if (holder is not null)
        {
            holder.Position = new Vector2(175f, 90f);
            holder.Scale = Vector2.One * 2.25f;
            if (choose is not null)
                holder.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => choose()));
            mount.AddChild(holder);
        }
        column.AddChild(CreateVersionLabel(label));
        column.AddChild(CreateRelicDescription(model));
        return column;
    }

    private static VBoxContainer CreateColumn()
    {
        VBoxContainer column = new()
        {
            CustomMinimumSize = new Vector2(460f, 0f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Alignment = BoxContainer.AlignmentMode.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        column.AddThemeConstantOverride("separation", 10);
        return column;
    }

    private static Control CreateVersionLabel(string text)
    {
        var label = ModificationImportScreenUi.CreateLabel(text, 28, HorizontalAlignment.Center);
        label.CustomMinimumSize = new Vector2(420f, 54f);
        return label;
    }

    private static Control CreateRelicDescription(RelicModel relic)
    {
        string text;
        try { text = $"{CommonHelpers.FormatRelicTitle(relic)}\n\n{relic.DynamicDescription.GetFormattedText()}"; }
        catch { text = relic.Id.Entry; }
        var label = ModificationImportScreenUi.CreateLabel(text, 22, HorizontalAlignment.Center);
        label.CustomMinimumSize = new Vector2(430f, 190f);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        return label;
    }

    private void ResolveAndClose()
    {
        Action? closed = _closed;
        _closed = null;
        QueueFree();
        closed?.Invoke();
    }

    private void Close()
    {
        _closed = null;
        QueueFree();
    }
}
