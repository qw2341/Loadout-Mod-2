#nullable enable

namespace Loadout.PanelItems;

using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Loadout.Services.RelicModification;
using Loadout.Services.Targets;
using Loadout.UI;
using Loadout.UI.Managers;
using Loadout.UI.Screens;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Relics;

public static class RelicModifier
{
    public const string TargetKey = "relic_modifier";
    private const string TargetDropdownName = "RelicModifierTargetDropdown";
    private const string CounterLabelName = "LoadoutRelicModifierCounterLabel";

    public static void Initialize()
    {
        NGenericSelectScreen? modifierScreen = null;
        SelectItemAdapter<LoadoutOwnedItem<RelicModel>> adapter = new()
        {
            GetId = CommonHelpers.OwnedItemId,
            GetName = item => CommonHelpers.FormatRelicTitle(item.Model),
            GetSearchText = item => $"{item.Model.Id} {CommonHelpers.FormatRelicTitle(item.Model)}",
            CreateView = (item, _) => NLoadoutPanel.CreateOwnedRelicGridItem(item.Model),
            ViewReady = (item, view) => RefreshView(view, item.Model),
            UpdateView = (item, view, _) => RefreshView(view, item.Model),
            BindActivationWithCleanup = (item, view, _) => BindRightClickWithCleanup(
                view,
                () => OpenModificationScreen(modifierScreen, item, view))
        };

        CommonHelpers.CreateAndAddDynamicLoadoutItem(
            GetSelectedTargetRelics,
            adapter,
            builder =>
            {
                builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
                builder.Materialization(SelectMaterializationMode.Lazy);
                builder.HiddenPrewarm(false);
                builder.Layout(10, new Vector2(68f, 68f), 32, 32);
                builder.ActionButton(
                    "host_relic_permamods",
                    LocMan.Loc("HOST_PERMAMODS_DOWNLOAD_TITLE", "Download Host Permamods"),
                    _ => OpenHostPermamodConflictScreen(),
                    CommonHelpers.LoadActionButtonIcon("CardModifier.png"));
            },
            (_, _) => System.Threading.Tasks.Task.FromResult<IReadOnlyList<Services.LastActions.LastActionEntry>>([]),
            "CardModifier.png",
            LocMan.Loc("RELICMODIFIER_TITLE", "Relic Modifier"),
            LocMan.Loc("RELICMODIFIER_DESC", "Right-click this relic to modify owned relics; right-click a relic to inspect and edit it."),
            (screen, refresh) =>
            {
                modifierScreen = screen;
                LoadoutTargetService.UpsertTargetDropdown(screen, TargetDropdownName, TargetKey, LoadoutTargetMode.PlayersOnly, refresh);
            });
    }

    internal static IReadOnlyList<LoadoutOwnedItem<RelicModel>> GetSelectedTargetRelics()
    {
        LoadoutTargetSelection target = LoadoutTargetService.GetSelected(TargetKey, LoadoutTargetMode.PlayersOnly);
        return LoadoutTargetService.BuildOwnedItems(target, player => player.Relics);
    }

    private static void OpenModificationScreen(NGenericSelectScreen? selectScreen, LoadoutOwnedItem<RelicModel> fallback, Control sourceView)
    {
        LoadoutOwnedItem<RelicModel> item = fallback;
        if (selectScreen is not null && selectScreen.TryGetItemForView(sourceView, out IGenericSelectItem current)
            && current.UntypedModel is LoadoutOwnedItem<RelicModel> currentRelic)
            item = currentRelic;

        if (NLoadoutPanelRoot.Instance is not { } root) return;
        NRelicModificationScreen screen = NRelicModificationScreen.Create();
        screen.Name = $"RelicModification_{CommonHelpers.MakeSafeNodeName(CommonHelpers.OwnedItemId(item))}";
        screen.Init(item, GetSelectedTargetRelics(), current => RefreshOwnedItem(selectScreen, current, sourceView, fallback));
        root.OpenScreen(screen);
    }

    private static Action? BindRightClickWithCleanup(Control view, Action activate)
    {
        if (!CommonHelpers.TryFindDescendantOrSelf(view, out NRelicBasicHolder holder)) return null;
        void OnGuiInput(InputEvent input)
        {
            if (input is not InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: false }) return;
            activate();
            holder.AcceptEvent();
        }
        holder.GuiInput += OnGuiInput;
        return () =>
        {
            if (GodotObject.IsInstanceValid(holder)) holder.GuiInput -= OnGuiInput;
        };
    }

    private static void RefreshOwnedItem(NGenericSelectScreen? screen, LoadoutOwnedItem<RelicModel> item, Control fallbackView, LoadoutOwnedItem<RelicModel> fallbackItem)
    {
        bool refreshed = false;
        screen?.ForEachVisibleItemView((selectItem, view) =>
        {
            if (refreshed || selectItem.UntypedModel is not LoadoutOwnedItem<RelicModel> visible || !SameOwnedItem(visible, item)) return;
            RefreshView(view, item.Model);
            refreshed = true;
        });
        if (!refreshed && SameOwnedItem(item, fallbackItem) && GodotObject.IsInstanceValid(fallbackView)) RefreshView(fallbackView, item.Model);
    }

    private static void RefreshView(Control view, RelicModel model)
    {
        if (CommonHelpers.TryFindDescendantOrSelf(view, out NRelicBasicHolder holder) && holder.Relic is { } relicView)
        {
            relicView.Model = model;
            RelicModificationStateService.RefreshRelic(model);
            RefreshCounterLabel(relicView, model);
        }
    }

    private static void RefreshCounterLabel(NRelic relicView, RelicModel model)
    {
        MegaLabel? label = relicView.GetNodeOrNull<MegaLabel>(CounterLabelName);
        bool showCounter;
        int displayAmount;
        try
        {
            showCounter = model.ShowCounter;
            displayAmount = model.DisplayAmount;
        }
        catch
        {
            showCounter = false;
            displayAmount = 0;
        }

        if (!showCounter)
        {
            if (label is not null)
                label.Visible = false;
            return;
        }

        if (label is null)
        {
            label = new MegaLabel
            {
                Name = CounterLabelName,
                AutoSizeEnabled = false,
                MinFontSize = 12,
                MaxFontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                FocusMode = Control.FocusModeEnum.None
            };
            label.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
            label.OffsetLeft = -36f;
            label.OffsetTop = -33f;
            label.OffsetRight = -4f;
            label.OffsetBottom = -1f;
            label.AddThemeFontOverride("font", CommonHelpers.LoadGameFont());
            label.AddThemeFontSizeOverride("font_size", 18);
            label.AddThemeColorOverride("font_color", new Color(1f, 0.965f, 0.886f));
            label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.19f));
            label.AddThemeColorOverride("font_outline_color", new Color(0.15f, 0.141f, 0.111f));
            label.AddThemeConstantOverride("shadow_offset_x", 2);
            label.AddThemeConstantOverride("shadow_offset_y", 2);
            label.AddThemeConstantOverride("outline_size", 10);
            label.AddThemeConstantOverride("shadow_outline_size", 10);
            relicView.AddChild(label);
        }

        label.Text = displayAmount.ToString(CultureInfo.InvariantCulture);
        label.Visible = true;
        relicView.MoveChild(label, relicView.GetChildCount() - 1);
    }

    private static void OpenHostPermamodConflictScreen()
    {
        if (!RelicModificationMultiplayerSyncService.HasPendingHostPermanentSnapshot)
        {
            GD.PushWarning("RelicModifier: no host relic permamod snapshot is available to download.");
            return;
        }
        if (NLoadoutPanelRoot.Instance is not { } root) return;
        NHostPermamodConflictScreen screen = new() { Name = "HostRelicPermamodConflict" };
        screen.InitForRelics();
        root.OpenScreen(screen);
    }

    private static bool SameOwnedItem(LoadoutOwnedItem<RelicModel> left, LoadoutOwnedItem<RelicModel> right)
        => left.OwnerNetId == right.OwnerNetId && left.Index == right.Index && left.Model.Id.Equals(right.Model.Id);
}
