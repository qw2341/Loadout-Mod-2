#nullable enable

namespace Loadout.PanelItems;

using System;
using System.Globalization;
using System.Threading.Tasks;
using Godot;
using Loadout.Services.Actions;
using Loadout.Services.Targets;
using Loadout.Services.TildeKey;
using Loadout.UI;
using Loadout.UI.Managers;
using Loadout.UI.Screens;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;

public static partial class TildeKey
{
    private const string TargetDropdownName = "TildeKeyTargetDropdown";
    private const string GodmodeToggleName = "TildeKeyGodmodeToggle";
    private const string GoToAnyRoomToggleName = "TildeKeyGoToAnyRoomToggle";
    private static readonly Vector2 StatRowSize = new(720f, 54f);

    public static void Initialize()
    {
        NLoadoutPanelItem item = new(
            "TildeKey.png",
            LocMan.Loc("TILDEKEY_TITLE", "Reality Manipulator"),
            LocMan.Loc("TILDEKEY_DESC", "Right-click this relic to select debug powers you want. Left Click fields to edit them. Ctrl + right click to kill all monsters. Shift + right click to spare all monsters."));

        PackedScene scene = GD.Load<PackedScene>("res://UI/Screens/GenericSelectScreen.tscn");
        NGenericSelectScreen screen = scene.Instantiate<NGenericSelectScreen>();

        SelectItemAdapter<TildeKeyStatDefinition> adapter = new()
        {
            GetId = definition => definition.Id,
            GetName = definition => definition.Label,
            GetSearchText = definition => $"{definition.Id} {definition.Label}",
            CreateView = (definition, _) => new TildeStatRow(screen, definition),
            UpdateView = (definition, view, _) =>
            {
                if (view is TildeStatRow row)
                    row.Refresh(definition);
            }
        };

        void ConfigureScreen()
        {
            TildeKeyStateService.EnsureLoaded();
            screen.Configure(TildeKeyStateService.StatDefinitions, adapter, builder =>
            {
                builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
                builder.Materialization(SelectMaterializationMode.Eager);
                builder.Layout(1, StatRowSize, 0, 8, fixedSlots: false, paddingTop: 84f, paddingBottom: 160f);
            });
            AddSidebarControls(screen);
        }

        ConfigureScreen();
        screen.LocaleChanged += () =>
        {
            SelectScreenUiState state = screen.CaptureUiState();
            ConfigureScreen();
            screen.RestoreUiState(state);
        };
        screen.Cancelled += NLoadoutPanelRoot.CloseTopLoadoutScreen;
        screen.Confirmed += _ => NLoadoutPanelRoot.CloseTopLoadoutScreen();
        TildeKeyStateService.StateChanged += () => screen.CallDeferred(nameof(NGenericSelectScreen.RefreshCurrentItemStates));

        item.BoundScreen = screen;
        item.BeforeOpen = target =>
        {
            TildeKeyStateService.EnsureLoaded();
        };
        item.AfterOpen = target =>
        {
            AddSidebarControls(target);
            target.RefreshNow(resetScroll: false);
            target.RefreshCurrentItemStates();
        };
        item.QuickAction = () =>
        {
            LoadoutImmediateMutationService.RequestTildeKillEnemies();
            return Task.CompletedTask;
        };
        item.ShiftQuickAction = () =>
        {
            LoadoutImmediateMutationService.RequestTildeSpareEnemies();
            return Task.CompletedTask;
        };

        NLoadoutPanel.ItemsContainer.AddChild(item);
    }

    private static void AddSidebarControls(NGenericSelectScreen screen)
    {
        LoadoutTargetService.UpsertTargetDropdown(
            screen,
            TargetDropdownName,
            TildeKeyStateService.TargetKey,
            LoadoutTargetMode.AllPlayersAndPlayers,
            () =>
            {
                RefreshSidebarToggles(screen);
                screen.RefreshCurrentItemStates();
            });

        UpsertToggle(
            screen,
            GodmodeToggleName,
            TildeKeyStateService.GodmodeToggleId,
            LocMan.Loc("TILDEKEY_GODMODE", "Godmode"),
            () => TildeKeyStateService.GetToggle(
                TildeKeyStateService.GodmodeToggleId,
                GetSelectedTarget()),
            enabled => LoadoutImmediateMutationService.RequestTildeSetToggle(
                TildeKeyStateService.GodmodeToggleId,
                enabled,
                GetSelectedTarget()));

        UpsertToggle(
            screen,
            GoToAnyRoomToggleName,
            TildeKeyStateService.GoToAnyRoomToggleId,
            LocMan.Loc("TILDEKEY_GO_TO_ANY_ROOM", "Go To Any Room"),
            () => TildeKeyStateService.GetToggle(
                TildeKeyStateService.GoToAnyRoomToggleId,
                GetSelectedTarget()),
            enabled => LoadoutImmediateMutationService.RequestTildeSetToggle(
                TildeKeyStateService.GoToAnyRoomToggleId,
                enabled,
                GetSelectedTarget()));

        RefreshSidebarToggles(screen);
    }

    private static void UpsertToggle(
        NGenericSelectScreen screen,
        string nodeName,
        string toggleId,
        string label,
        Func<bool> getChecked,
        Action<bool> onChanged)
    {
        NLoadoutToggle? toggle = screen.GetNodeOrNull<NLoadoutToggle>(
            $"Sidebar/MarginContainer/TopVBox/CustomControls/{nodeName}");

        if (toggle is null)
        {
            toggle = new NLoadoutToggle
            {
                Name = nodeName,
                CustomMinimumSize = new Vector2(256f, 48f),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            toggle.Init(toggleId, label, getChecked());
            toggle.Connect(NLoadoutToggle.SignalName.Toggled, Callable.From<NLoadoutToggle>(changed =>
            {
                onChanged(changed.IsChecked);
                screen.RefreshCurrentItemStates();
            }));
            screen.AddCustomSidebarControl(toggle);
            return;
        }

        toggle.Init(toggleId, label, getChecked());
        toggle.SetChecked(getChecked(), emit: false);
    }

    private static void RefreshSidebarToggles(NGenericSelectScreen screen)
    {
        screen.GetNodeOrNull<NLoadoutToggle>($"Sidebar/MarginContainer/TopVBox/CustomControls/{GodmodeToggleName}")
            ?.SetChecked(TildeKeyStateService.GetToggle(TildeKeyStateService.GodmodeToggleId, GetSelectedTarget()), emit: false);
        screen.GetNodeOrNull<NLoadoutToggle>($"Sidebar/MarginContainer/TopVBox/CustomControls/{GoToAnyRoomToggleName}")
            ?.SetChecked(TildeKeyStateService.GetToggle(TildeKeyStateService.GoToAnyRoomToggleId, GetSelectedTarget()), emit: false);
    }

    private static LoadoutTargetSelection GetSelectedTarget()
    {
        return LoadoutTargetService.GetSelected(
            TildeKeyStateService.TargetKey,
            LoadoutTargetMode.AllPlayersAndPlayers);
    }

    private sealed partial class TildeStatRow : HBoxContainer
    {
        private readonly NGenericSelectScreen _screen;
        private TildeKeyStatDefinition _definition;
        private readonly MegaLabel _nameLabel;
        private readonly LineEdit _valueEntry;
        private readonly NLoadoutToggle? _lockToggle;
        private bool _isRefreshing;

        public TildeStatRow(NGenericSelectScreen screen, TildeKeyStatDefinition definition)
        {
            _screen = screen;
            _definition = definition;
            CustomMinimumSize = StatRowSize;
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ShrinkBegin;
            AddThemeConstantOverride("separation", 12);
            MouseFilter = MouseFilterEnum.Pass;

            _nameLabel = CreateLabel("Name", definition.Label, new Vector2(300f, 52f), 22, HorizontalAlignment.Left, StsColors.gold);
            AddChild(_nameLabel);

            _valueEntry = CreateEntry();
            _valueEntry.TextSubmitted += OnTextSubmitted;
            _valueEntry.FocusExited += CommitText;
            AddChild(_valueEntry);

            if (definition.SupportsLock)
            {
                _lockToggle = new NLoadoutToggle
                {
                    Name = "LockToggle",
                    CustomMinimumSize = new Vector2(150f, 46f),
                    SizeFlagsHorizontal = SizeFlags.ShrinkEnd
                };
                _lockToggle.Init("lock", LocMan.Loc("TILDEKEY_LOCK", "Lock"), checkedByDefault: false);
                _lockToggle.Connect(NLoadoutToggle.SignalName.Toggled, Callable.From<NLoadoutToggle>(OnLockToggled));
                AddChild(_lockToggle);
            }
            else
            {
                Control spacer = new()
                {
                    CustomMinimumSize = new Vector2(150f, 46f),
                    MouseFilter = MouseFilterEnum.Ignore
                };
                AddChild(spacer);
            }

            Refresh(definition);
        }

        public void Refresh(TildeKeyStatDefinition definition)
        {
            _definition = definition;
            _isRefreshing = true;
            _nameLabel.Text = definition.Label;

            int value = TildeKeyStateService.GetDisplayValue(definition, GetSelectedTarget());
            if (!_valueEntry.HasFocus())
                _valueEntry.Text = value.ToString(CultureInfo.InvariantCulture);

            _lockToggle?.SetChecked(TildeKeyStateService.IsLocked(definition, GetSelectedTarget()), emit: false);
            _isRefreshing = false;
        }

        public override void _ExitTree()
        {
            _valueEntry.TextSubmitted -= OnTextSubmitted;
            _valueEntry.FocusExited -= CommitText;
        }

        private void OnTextSubmitted(string _)
        {
            CommitText();
            _valueEntry.ReleaseFocus();
        }

        private void CommitText()
        {
            if (_isRefreshing)
                return;

            if (!int.TryParse(_valueEntry.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                Refresh(_definition);
                return;
            }

            LoadoutImmediateMutationService.RequestTildeSetStat(_definition.Id, value, GetSelectedTarget());
            _screen.RefreshCurrentItemStates();
        }

        private void OnLockToggled(NLoadoutToggle toggle)
        {
            if (_isRefreshing)
                return;

            if (!int.TryParse(_valueEntry.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                value = TildeKeyStateService.GetDisplayValue(_definition, GetSelectedTarget());

            LoadoutImmediateMutationService.RequestTildeSetLock(
                _definition.Id,
                value,
                toggle.IsChecked,
                GetSelectedTarget());
            _screen.RefreshCurrentItemStates();
        }

        private static LineEdit CreateEntry()
        {
            LineEdit entry = new()
            {
                CustomMinimumSize = new Vector2(180f, 42f),
                SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
                Alignment = HorizontalAlignment.Center,
                MouseFilter = MouseFilterEnum.Stop
            };
            entry.AddThemeFontOverride("font", CommonHelpers.LoadGameFont("res://themes/kreon_bold_glyph_space_one.tres"));
            entry.AddThemeFontSizeOverride("font_size", 23);
            entry.AddThemeColorOverride("font_color", StsColors.cream);
            entry.AddThemeColorOverride("font_focus_color", StsColors.gold);
            return entry;
        }

        private static MegaLabel CreateLabel(
            string name,
            string text,
            Vector2 size,
            int fontSize,
            HorizontalAlignment alignment,
            Color color)
        {
            MegaLabel label = new()
            {
                Name = name,
                Text = text,
                AutoSizeEnabled = false,
                CustomMinimumSize = size,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                HorizontalAlignment = alignment,
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = MouseFilterEnum.Ignore
            };
            label.AddThemeFontOverride("font", CommonHelpers.LoadGameFont("res://themes/kreon_bold_glyph_space_one.tres"));
            label.AddThemeFontSizeOverride("font_size", fontSize);
            label.AddThemeColorOverride("font_color", color);
            label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.5f));
            label.AddThemeConstantOverride("shadow_offset_x", 3);
            label.AddThemeConstantOverride("shadow_offset_y", 2);
            return label;
        }
    }
}
