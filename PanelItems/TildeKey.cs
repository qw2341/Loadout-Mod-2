#nullable enable

namespace Loadout.PanelItems;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
using MegaCrit.Sts2.Core.Runs;

public static partial class TildeKey
{
    private const string TargetDropdownName = "TildeKeyTargetDropdown";
    private const string GodmodeToggleName = "TildeKeyGodmodeToggle";
    private const string GoToAnyRoomToggleName = "TildeKeyGoToAnyRoomToggle";
    private const string InfiniteEnergyToggleName = "TildeKeyInfiniteEnergyToggle";
    private const string DrawTillHandLimitToggleName = "TildeKeyDrawTillHandLimitToggle";
    private const string ScrollRelicCounterToggleName = "TildeKeyScrollRelicCounterToggle";
    private const string StaticHoverTipsTable = "static_hover_tips";
    private const string HeartIconPath = "res://images/atlases/ui_atlas.sprites/top_bar/top_bar_heart.tres";
    private const string GoldIconPath = "res://images/atlases/ui_atlas.sprites/top_bar/top_bar_gold.tres";
    private const string BlockIconPath = "res://images/ui/combat/block.png";
    private const string StarIconPath = "res://images/ui/combat/energy_star.png";
    private const string OrbSlotIconPath = "res://images/orbs/empty_orb.png";
    private const string PotionSlotIconPath = "res://images/packed/potions/potion_placeholder.png";
    private const string TurnIconPath = "res://images/ui/run_history/unknown_monster.png";
    private const string CardIconPath = "res://images/packed/statistics_screen/stats_cards.png";
    private const string CardIconBluePath = "res://images/packed/statistics_screen/stats_bluecards.png";
    private const string CardRemovalIconPath = "res://images/ui/reward_screen/reward_icon_card_removal.png";
    private const string WongoIconPath = "res://images/relics/wongo_customer_appreciation_badge.png";
    private const string DamageIconPath = "res://images/ui/game_over_screen/badge_damage_leader.png";
    private const string DamageIconElitePath = "res://images/ui/game_over_screen/badge_elite.png";
    private const string DebuffIconPath = "res://images/ui/game_over_screen/badge_debuffer.png";
    private static WeakReference<TildeStatRow>? ActiveStatRow;
    private static readonly Color HpAccent = new("F1373E");
    private static readonly Color BlockAccent = new("3B6FA3");
    private static readonly Vector2 StatRowSize = new(720f, 54f);
    private static readonly Dictionary<string, Texture2D?> TextureCache = new(StringComparer.Ordinal);

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
            GetName = GetStatLabel,
            GetSearchText = GetStatSearchText,
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
        screen.GuiInput += CommitActiveStatRowOnOutsideClick;
        screen.LocaleChanged += () =>
        {
            SelectScreenUiState state = screen.CaptureUiState();
            ConfigureScreen();
            screen.RestoreUiState(state);
        };
        screen.Cancelled += NLoadoutPanelRoot.CloseTopLoadoutScreen;
        screen.Confirmed += _ => NLoadoutPanelRoot.CloseTopLoadoutScreen();
        TildeKeyStateService.StateChanged += () =>
        {
            if (!GodotObject.IsInstanceValid(screen) || !screen.IsInsideTree() || !screen.IsVisibleInTree())
                return;

            screen.CallDeferred(nameof(NGenericSelectScreen.RefreshCurrentItemStates));
        };

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

    private static void CommitActiveStatRowOnOutsideClick(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mouseButton)
            return;

        if (ActiveStatRow is not { } activeReference
            || !activeReference.TryGetTarget(out TildeStatRow? row)
            || !GodotObject.IsInstanceValid(row))
        {
            return;
        }

        row.CommitIfClickOutside(mouseButton.GlobalPosition);
    }

    private static void CommitActiveStatRowBeforeTargetChange()
    {
        if (ActiveStatRow is not { } activeReference
            || !activeReference.TryGetTarget(out TildeStatRow? row)
            || !GodotObject.IsInstanceValid(row))
        {
            return;
        }

        row.CommitAndReleaseFocus();
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
            },
            CommitActiveStatRowBeforeTargetChange);

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

        UpsertToggle(
            screen,
            InfiniteEnergyToggleName,
            TildeKeyStateService.InfiniteEnergyToggleId,
            LocMan.Loc("TILDEKEY_INFINITE_ENERGY", "Infinite Energy"),
            () => TildeKeyStateService.GetToggle(
                TildeKeyStateService.InfiniteEnergyToggleId,
                GetSelectedTarget()),
            enabled => LoadoutImmediateMutationService.RequestTildeSetToggle(
                TildeKeyStateService.InfiniteEnergyToggleId,
                enabled,
                GetSelectedTarget()));

        UpsertToggle(
            screen,
            DrawTillHandLimitToggleName,
            TildeKeyStateService.DrawTillHandLimitToggleId,
            LocMan.Loc("TILDEKEY_DRAW_TILL_HAND_LIMIT", "Draw Till Hand Limit"),
            () => TildeKeyStateService.GetToggle(
                TildeKeyStateService.DrawTillHandLimitToggleId,
                GetSelectedTarget()),
            enabled => LoadoutImmediateMutationService.RequestTildeSetToggle(
                TildeKeyStateService.DrawTillHandLimitToggleId,
                enabled,
                GetSelectedTarget()));

        UpsertToggle(
            screen,
            ScrollRelicCounterToggleName,
            TildeKeyStateService.ScrollRelicCounterToggleId,
            LocMan.Loc("TILDEKEY_SCROLL_RELIC_COUNTER", "Scroll Relic Counters"),
            () => TildeKeyStateService.GetToggle(
                TildeKeyStateService.ScrollRelicCounterToggleId,
                GetSelectedTarget()),
            enabled => LoadoutImmediateMutationService.RequestTildeSetToggle(
                TildeKeyStateService.ScrollRelicCounterToggleId,
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
        screen.GetNodeOrNull<NLoadoutToggle>($"Sidebar/MarginContainer/TopVBox/CustomControls/{InfiniteEnergyToggleName}")
            ?.SetChecked(TildeKeyStateService.GetToggle(TildeKeyStateService.InfiniteEnergyToggleId, GetSelectedTarget()), emit: false);
        screen.GetNodeOrNull<NLoadoutToggle>($"Sidebar/MarginContainer/TopVBox/CustomControls/{DrawTillHandLimitToggleName}")
            ?.SetChecked(TildeKeyStateService.GetToggle(TildeKeyStateService.DrawTillHandLimitToggleId, GetSelectedTarget()), emit: false);
        screen.GetNodeOrNull<NLoadoutToggle>($"Sidebar/MarginContainer/TopVBox/CustomControls/{ScrollRelicCounterToggleName}")
            ?.SetChecked(TildeKeyStateService.GetToggle(TildeKeyStateService.ScrollRelicCounterToggleId, GetSelectedTarget()), emit: false);
    }

    private static LoadoutTargetSelection GetSelectedTarget()
    {
        return LoadoutTargetService.GetSelected(
            TildeKeyStateService.TargetKey,
            LoadoutTargetMode.AllPlayersAndPlayers);
    }

    private static string GetStatLabel(TildeKeyStatDefinition definition)
    {
        return GetStatPresentation(definition).Label;
    }

    private static string GetStatSearchText(TildeKeyStatDefinition definition)
    {
        string label = GetStatLabel(definition);
        return string.Equals(label, definition.Label, StringComparison.Ordinal)
            ? $"{definition.Id} {definition.Label}"
            : $"{definition.Id} {definition.Label} {label}";
    }

    private static TildeStatPresentation GetStatPresentation(TildeKeyStatDefinition definition)
    {
        return definition.Id switch
        {
            "current_hp" => new(
                LocMan.Loc("TILDEKEY_STAT_CURRENT_HP", $"Current {StaticHoverTitle("HIT_POINTS", "HP")}"),
                HeartIconPath,
                HpAccent),
            "max_hp" => new(
                LocMan.Loc("TILDEKEY_STAT_MAX_HP", $"Max {StaticHoverTitle("HIT_POINTS", "HP")}"),
                HeartIconPath,
                HpAccent),
            "block" => new(
                StaticHoverTitle("BLOCK", definition.Label),
                BlockIconPath,
                BlockAccent),
            "gold" => new(
                StaticHoverTitle("MONEY_POUCH", definition.Label),
                GoldIconPath,
                StsColors.gold),
            "max_energy" => new(
                LocMan.Loc("TILDEKEY_STAT_MAX_ENERGY", $"Max {StaticHoverTitle("ENERGY_COUNT", "Energy")}"),
                GetEnergyIconPath(),
                StsColors.energyBlue),
            "combat_energy" => new(
                LocMan.Loc("TILDEKEY_STAT_COMBAT_ENERGY", $"Combat {StaticHoverTitle("ENERGY_COUNT", "Energy")}"),
                GetEnergyIconPath(),
                StsColors.energyBlue),
            "stars" => new(
                StaticHoverTitle("STAR_COUNT", definition.Label),
                StarIconPath,
                StsColors.blue),
            "base_orb_slots" => new(
                LocMan.Loc("TILDEKEY_STAT_BASE_ORB_SLOTS", "Base Orb Slots"),
                OrbSlotIconPath,
                StsColors.lightGray),
            "max_potion_slots" => new(
                LocMan.Loc("TILDEKEY_STAT_MAX_POTION_SLOTS", $"Max {StaticHoverTitle("POTION_SLOT", "Potion Slot")}s"),
                PotionSlotIconPath,
                StsColors.lightGray),
            TildeKeyStateService.DrawPerTurnStatId => new(
                LocMan.Loc("TILDEKEY_STAT_DRAW_PER_TURN", "Draw per Turn"),
                CardIconPath,
                StsColors.cream),
            TildeKeyStateService.HandSizeStatId => new(
                LocMan.Loc("TILDEKEY_STAT_HAND_SIZE", "Hand Size"),
                CardIconBluePath,
                StsColors.cream),
            TildeKeyStateService.PlayerDamageMultiplierStatId => new(
                LocMan.Loc("TILDEKEY_STAT_PLAYER_DAMAGE_MULTIPLIER", "Player Damage Multiplier"),
                DamageIconPath,
                HpAccent),
            TildeKeyStateService.EnemyDamageMultiplierStatId => new(
                LocMan.Loc("TILDEKEY_STAT_ENEMY_DAMAGE_MULTIPLIER", "Enemy Damage Multiplier"),
                DamageIconElitePath,
                HpAccent),
            
            //LESS USED STUFF GOES HERE
            "turn_number" => new(
                LocMan.Loc("TILDEKEY_STAT_TURN_NUMBER", "Turn Number"),
                TurnIconPath,
                StsColors.gray),
            "extra_card_shop_removals" => new(
                LocMan.Loc("TILDEKEY_STAT_CARD_SHOP_REMOVALS_USED", "Card Shop Removals Used"),
                CardRemovalIconPath,
                StsColors.gray),
            "extra_wongo_points" => new(
                LocMan.Loc("TILDEKEY_STAT_WONGO_POINTS", "Wongo Points"),
                WongoIconPath,
                StsColors.gray),
            "extra_damage_dealt" => new(
                LocMan.Loc("TILDEKEY_STAT_DAMAGE_DEALT", "Damage Dealt"),
                DamageIconPath,
                StsColors.gray),
            "extra_debuffs_applied" => new(
                LocMan.Loc("TILDEKEY_STAT_DEBUFFS_APPLIED", "Debuffs Applied"),
                DebuffIconPath,
                StsColors.gray),
            _ => new(definition.Label, null, StsColors.gold)
        };
    }

    private static string StaticHoverTitle(string key, string fallback)
    {
        return LocMan.GameLoc(StaticHoverTipsTable, $"{key}.title", fallback);
    }

    private static string GetEnergyIconPath()
    {
        const string fallback = "res://images/atlases/ui_atlas.sprites/card/energy_ironclad.tres";
        try
        {
            if (!RunManager.Instance.IsInProgress)
                return fallback;

            var runState = RunManager.Instance.DebugOnlyGetState();
            if (runState is null)
                return fallback;

            string? path = LoadoutTargetService.ResolvePlayers(GetSelectedTarget(), runState)
                .FirstOrDefault()
                ?.Character
                .CardPool
                .EnergyIconPath;
            return string.IsNullOrWhiteSpace(path) ? fallback : path;
        }
        catch
        {
            return fallback;
        }
    }

    private static Texture2D? LoadTexture(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (TextureCache.TryGetValue(path, out Texture2D? cached))
        {
            if (cached is null || GodotObject.IsInstanceValid(cached))
                return cached;

            TextureCache.Remove(path);
        }

        Texture2D? texture = null;
        try
        {
            texture = ResourceLoader.Exists(path)
                ? ResourceLoader.Load<Texture2D>(path, null, ResourceLoader.CacheMode.Reuse)
                : null;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"TildeKey: failed to load icon '{path}'. {exception.Message}");
        }

        TextureCache[path] = texture;
        return texture;
    }

    private readonly record struct TildeStatPresentation(string Label, string? IconPath, Color Accent);

    private sealed partial class TildeStatRow : HBoxContainer
    {
        private readonly NGenericSelectScreen _screen;
        private TildeKeyStatDefinition _definition;
        private readonly TextureRect _icon;
        private readonly MegaLabel _nameLabel;
        private readonly LineEdit _valueEntry;
        private readonly NLoadoutToggle? _lockToggle;
        private bool _isRefreshing;
        private bool _suppressNextFocusCommit;
        private string _targetOptionId = string.Empty;

        public TildeStatRow(NGenericSelectScreen screen, TildeKeyStatDefinition definition)
        {
            _screen = screen;
            _definition = definition;
            CustomMinimumSize = StatRowSize;
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ShrinkBegin;
            AddThemeConstantOverride("separation", 12);
            MouseFilter = MouseFilterEnum.Pass;

            _icon = CreateIcon();
            AddChild(_icon);

            _nameLabel = CreateLabel("Name", definition.Label, new Vector2(254f, 52f), 22, HorizontalAlignment.Left, StsColors.gold);
            AddChild(_nameLabel);

            _valueEntry = CreateEntry();
            _valueEntry.TextSubmitted += OnTextSubmitted;
            _valueEntry.FocusExited += OnFocusExited;
            _valueEntry.FocusEntered += OnFocusEntered;
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
            TildeStatPresentation presentation = GetStatPresentation(definition);
            Texture2D? iconTexture = LoadTexture(presentation.IconPath);
            _icon.Texture = iconTexture;
            _icon.Visible = iconTexture is not null;
            _icon.Modulate = presentation.Accent;
            _nameLabel.Text = presentation.Label;
            _nameLabel.AddThemeColorOverride("font_color", presentation.Accent);

            string targetOptionId = GetSelectedTarget().ToOptionId();
            bool targetChanged = !string.Equals(_targetOptionId, targetOptionId, StringComparison.Ordinal);
            int value = TildeKeyStateService.GetDisplayValue(definition, GetSelectedTarget());
            if (targetChanged && _valueEntry.HasFocus())
            {
                _suppressNextFocusCommit = true;
                _valueEntry.ReleaseFocus();
            }

            if (!_valueEntry.HasFocus() || targetChanged)
                _valueEntry.Text = value.ToString(CultureInfo.InvariantCulture);

            _targetOptionId = targetOptionId;
            _lockToggle?.SetChecked(TildeKeyStateService.IsLocked(definition, GetSelectedTarget()), emit: false);
            _isRefreshing = false;
        }

        public override void _ExitTree()
        {
            _valueEntry.TextSubmitted -= OnTextSubmitted;
            _valueEntry.FocusExited -= OnFocusExited;
            _valueEntry.FocusEntered -= OnFocusEntered;
        }

        public override void _Input(InputEvent @event)
        {
            if (!_valueEntry.HasFocus())
                return;

            if (@event is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mouseButton)
                return;

            CommitIfClickOutside(mouseButton.GlobalPosition);
        }

        public void CommitIfClickOutside(Vector2 globalPosition)
        {
            if (!_valueEntry.HasFocus() || _valueEntry.GetGlobalRect().HasPoint(globalPosition))
                return;

            CommitAndReleaseFocus();
        }

        private void OnFocusEntered()
        {
            ActiveStatRow = new WeakReference<TildeStatRow>(this);
        }

        private void OnTextSubmitted(string _)
        {
            CommitAndReleaseFocus();
        }

        private void OnFocusExited()
        {
            if (_suppressNextFocusCommit)
            {
                _suppressNextFocusCommit = false;
                return;
            }

            CommitText();
            if (ActiveStatRow is { } activeReference
                && activeReference.TryGetTarget(out TildeStatRow? row)
                && ReferenceEquals(row, this))
            {
                ActiveStatRow = null;
            }
        }

        public void CommitAndReleaseFocus()
        {
            CommitText();
            _suppressNextFocusCommit = true;
            _valueEntry.ReleaseFocus();
        }

        private void CommitText()
        {
            if (_isRefreshing)
                return;

            if (!TryParseEntryValue(_valueEntry.Text, out int value))
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

            if (!TryParseEntryValue(_valueEntry.Text, out int value))
                value = TildeKeyStateService.GetDisplayValue(_definition, GetSelectedTarget());

            LoadoutImmediateMutationService.RequestTildeSetLock(
                _definition.Id,
                value,
                toggle.IsChecked,
                GetSelectedTarget());
            _screen.RefreshCurrentItemStates();
        }

        private static bool TryParseEntryValue(string? text, out int value)
        {
            value = 0;
            string trimmed = (text ?? string.Empty).Trim();
            if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            {
                value = (int)Math.Clamp(parsed, int.MinValue, int.MaxValue);
                return true;
            }

            if (LooksLikeIntegerOverflow(trimmed, out bool isNegative))
            {
                value = isNegative ? int.MinValue : int.MaxValue;
                return true;
            }

            return false;
        }

        private static bool LooksLikeIntegerOverflow(string text, out bool isNegative)
        {
            isNegative = false;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            int index = 0;
            if (text[0] is '+' or '-')
            {
                isNegative = text[0] == '-';
                index = 1;
            }

            return index < text.Length && text[index..].All(char.IsDigit);
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

        private static TextureRect CreateIcon()
        {
            return new TextureRect
            {
                Name = "Icon",
                CustomMinimumSize = new Vector2(44f, 44f),
                SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = MouseFilterEnum.Ignore
            };
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
