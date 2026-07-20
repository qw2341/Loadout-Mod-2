#nullable enable

namespace Loadout.PanelItems;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using Godot;
using Loadout.Services.RelicModification;
using Loadout.Services.Targets;
using Loadout.UI;
using Loadout.UI.Managers;
using Loadout.UI.Screens;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Runs;

public static class RelicModifier
{
    public const string TargetKey = "relic_modifier";
    private const string TargetDropdownName = "RelicModifierTargetDropdown";
    private const string CounterLabelName = "LoadoutRelicModifierCounterLabel";
    private static readonly ConditionalWeakTable<NRelic, RelicCounterViewState> CounterViewStates = new();

    private sealed class RelicCounterViewState
    {
        public MegaLabel? Label;
        public bool HasSnapshot;
        public bool ShowCounter;
        public int DisplayAmount;
    }

    public static void Initialize()
    {
        NGenericSelectScreen? modifierScreen = null;
        SelectItemAdapter<LoadoutOwnedItem<RelicModel>> adapter = new()
        {
            GetId = CommonHelpers.OwnedItemId,
            GetName = item => CommonHelpers.FormatRelicTitle(item.Model),
            GetSearchTextFromName = (item, name) => $"{item.Model.Id} {name} {item.Model.DynamicDescription.GetFormattedText()}",
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
                builder.HiddenPrewarm(true);
                builder.Layout(10, new Vector2(68f, 68f), 32, 32);
                if (IsMultiplayerClient())
                {
                    builder.ActionButton(
                        "host_relic_permamods",
                        LocMan.Loc("HOST_PERMAMODS_DOWNLOAD_TITLE", "Download Host Permamods"),
                        _ => OpenHostPermamodConflictScreen(),
                        CommonHelpers.LoadActionButtonIcon("RelicModifier.png"));
                }
            },
            (_, _) => System.Threading.Tasks.Task.FromResult<IReadOnlyList<Services.LastActions.LastActionEntry>>([]),
            "RelicModifier.png",
            LocMan.Loc("RELICMODIFIER_TITLE", "Relic Modifier"),
            LocMan.Loc("RELICMODIFIER_DESC", "Right-click this relic to modify owned relics; right-click a relic to inspect and edit it."),
            (screen, refresh) =>
            {
                modifierScreen = screen;
                LoadoutTargetService.UpsertTargetDropdown(screen, TargetDropdownName, TargetKey, LoadoutTargetMode.PlayersOnly, refresh);
            },
            selectScreenScenePath: CommonHelpers.RelicSelectScreenScenePath,
            reconcileModelsOnEveryOpen: false,
            refreshModelsAfterActivation: false,
            syncChangesWhileHidden: true);
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
        string itemId = CommonHelpers.OwnedItemId(item);
        bool refreshed = screen switch
        {
            NRelicSelectScreen relicScreen => relicScreen.RefreshItemById(itemId),
            not null => screen.RefreshItemView(itemId),
            _ => false
        };
        if (refreshed)
            return;

        if (SameOwnedItem(item, fallbackItem) && GodotObject.IsInstanceValid(fallbackView))
            RefreshView(fallbackView, item.Model);
    }

    private static void RefreshView(Control view, RelicModel model)
    {
        if (!CommonHelpers.TryFindDescendantOrSelf(view, out NRelicBasicHolder holder)
            || holder.Relic is not { } relicView)
        {
            return;
        }

        if (!ReferenceEquals(relicView.Model, model))
            relicView.Model = model;

        RefreshCounterLabel(relicView, model);
    }

    private static void RefreshCounterLabel(NRelic relicView, RelicModel model)
    {
        RelicCounterViewState state = CounterViewStates.GetValue(relicView, static _ => new RelicCounterViewState());

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

        bool snapshotMatches = state.HasSnapshot
                               && state.ShowCounter == showCounter
                               && (!showCounter || state.DisplayAmount == displayAmount);
        if (snapshotMatches)
        {
            if (!showCounter)
                return;

            MegaLabel? current = ResolveCounterLabel(relicView, state);
            if (current is { Visible: true })
                return;
        }

        state.HasSnapshot = true;
        state.ShowCounter = showCounter;
        state.DisplayAmount = showCounter ? displayAmount : 0;

        MegaLabel? label = ResolveCounterLabel(relicView, state);
        if (!showCounter)
        {
            if (label is { Visible: true })
                label.Visible = false;
            return;
        }

        if (label is null)
        {
            label = CreateCounterLabel();
            relicView.AddChild(label);
            state.Label = label;
        }

        label.Text = displayAmount.ToString(CultureInfo.InvariantCulture);
        if (!label.Visible)
            label.Visible = true;
    }

    private static MegaLabel? ResolveCounterLabel(NRelic relicView, RelicCounterViewState state)
    {
        if (state.Label is { } cached
            && GodotObject.IsInstanceValid(cached)
            && cached.GetParent() == relicView)
        {
            return cached;
        }

        MegaLabel? existing = relicView.GetNodeOrNull<MegaLabel>(CounterLabelName);
        state.Label = existing;
        return existing;
    }

    private static MegaLabel CreateCounterLabel()
    {
        MegaLabel label = new()
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
        return label;
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

    private static bool IsMultiplayerClient()
    {
        try
        {
            return RunManager.Instance.NetService.Type == NetGameType.Client;
        }
        catch
        {
            return false;
        }
    }

    private static bool SameOwnedItem(LoadoutOwnedItem<RelicModel> left, LoadoutOwnedItem<RelicModel> right)
        => left.OwnerNetId == right.OwnerNetId && left.Index == right.Index && left.Model.Id.Equals(right.Model.Id);
}
