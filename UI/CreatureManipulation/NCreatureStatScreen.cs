#nullable enable

namespace Loadout.UI.CreatureManipulation;

using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Loadout.PanelItems;
using Loadout.Services.CreatureManipulation;
using Loadout.UI.Screens.Controls;
using Loadout.UI.Managers;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

public partial class NCreatureStatScreen : Control
{
    private static readonly Vector2 PanelSize = new(820f, 590f);
    private static readonly Dictionary<string, Texture2D?> TextureCache = new(StringComparer.Ordinal);

    private readonly Creature _target;
    private readonly Dictionary<CreatureManipulationStat, StatRow> _rows = [];
    private bool _wasVisible;
    private bool _cleaned;

    private NCreatureStatScreen(Creature target)
    {
        _target = target;
        Name = $"CreatureStatScreen_{target.CombatId}";
        MouseFilter = MouseFilterEnum.Stop;
    }

    public static void Open(Creature target)
    {
        if (target.CombatId is null || NLoadoutPanelRoot.Instance is not { } root)
            return;
        root.OpenScreen(new NCreatureStatScreen(target));
    }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        BuildUi();

        _target.CurrentHpChanged += OnStatChanged;
        _target.MaxHpChanged += OnStatChanged;
        _target.BlockChanged += OnStatChanged;
        _target.Died += OnTargetDied;
        CreatureManipulationStateService.StateChanged += RefreshRows;
        VisibilityChanged += OnVisibilityChanged;
        _wasVisible = Visible;
        RefreshRows();
    }

    public override void _ExitTree() => Cleanup();

    private void BuildUi()
    {
        ColorRect dim = new()
        {
            Color = new Color(0f, 0f, 0f, 0.72f),
            MouseFilter = MouseFilterEnum.Stop
        };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(dim);

        Control panel = new()
        {
            CustomMinimumSize = PanelSize,
            MouseFilter = MouseFilterEnum.Stop
        };
        panel.SetAnchorsPreset(LayoutPreset.Center);
        panel.OffsetLeft = -PanelSize.X * 0.5f;
        panel.OffsetTop = -PanelSize.Y * 0.5f;
        panel.OffsetRight = PanelSize.X * 0.5f;
        panel.OffsetBottom = PanelSize.Y * 0.5f;

        ColorRect panelFallback = new()
        {
            Color = new Color(0.035f, 0.085f, 0.11f, 0.98f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        panelFallback.SetAnchorsPreset(LayoutPreset.FullRect);
        panel.AddChild(panelFallback);

        NinePatchRect panelBackground = new()
        {
            Name = "RewardPanelBackground",
            Texture = LoadGameTexture("res://images/ui/reward_screen/reward_panel.png"),
            DrawCenter = true,
            PatchMarginLeft = 92,
            PatchMarginTop = 102,
            PatchMarginRight = 92,
            PatchMarginBottom = 102,
            AxisStretchHorizontal = NinePatchRect.AxisStretchMode.Stretch,
            AxisStretchVertical = NinePatchRect.AxisStretchMode.Stretch,
            MouseFilter = MouseFilterEnum.Ignore
        };
        panelBackground.SetAnchorsPreset(LayoutPreset.FullRect);
        panel.AddChild(panelBackground);
        AddChild(panel);

        MarginContainer margin = new();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 56);
        margin.AddThemeConstantOverride("margin_top", 54);
        margin.AddThemeConstantOverride("margin_right", 56);
        margin.AddThemeConstantOverride("margin_bottom", 54);
        panel.AddChild(margin);

        CenterContainer contentCenter = new()
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        margin.AddChild(contentCenter);

        VBoxContainer content = new()
        {
            CustomMinimumSize = new Vector2(700f, 0f),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            Alignment = BoxContainer.AlignmentMode.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        content.AddThemeConstantOverride("separation", 12);
        contentCenter.AddChild(content);

        MegaLabel title = CommonHelpers.CreateButtonLabel(
            "Title",
            LocMan.Loc("CREATURE_MANIP_STATS_TITLE", "Creature Stats"),
            Vector2.Zero,
            new Vector2(700f, 64f),
            38,
            HorizontalAlignment.Center,
            StsColors.gold);
        title.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.7f));
        title.AddThemeConstantOverride("outline_size", 10);
        content.AddChild(title);

        AddRow(content, CreatureManipulationStat.CurrentHp,
            LocMan.Loc("CREATURE_MANIP_CURRENT_HP", "Current HP"));
        AddRow(content, CreatureManipulationStat.MaxHp,
            LocMan.Loc("CREATURE_MANIP_MAX_HP", "Max HP"));
        AddRow(content, CreatureManipulationStat.Block,
            LocMan.Loc("CREATURE_MANIP_BLOCK", "Block"));

        CenterContainer closeMount = new()
        {
            CustomMinimumSize = new Vector2(700f, 58f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        NLoadoutActionButton close = new()
        {
            Name = "CloseButton",
            CustomMinimumSize = new Vector2(230f, 48f)
        };
        close.Init("close", LocMan.Loc("CANCEL", "Cancel"));
        close.Connect(
            NClickableControl.SignalName.Released,
            Callable.From<NClickableControl>(_ => NLoadoutPanelRoot.CloseTopLoadoutScreen()));
        closeMount.AddChild(close);
        content.AddChild(closeMount);
    }

    private void AddRow(
        VBoxContainer content,
        CreatureManipulationStat stat,
        string labelText)
    {
        StatRow row = new(_target, stat, labelText);
        _rows[stat] = row;
        CenterContainer rowMount = new()
        {
            CustomMinimumSize = new Vector2(700f, 86f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        rowMount.AddChild(row);
        content.AddChild(rowMount);
    }

    private void OnStatChanged(int _, int __) => RefreshRows();
    private void OnTargetDied(Creature _) => NLoadoutPanelRoot.CloseTopLoadoutScreen();

    private void RefreshRows()
    {
        foreach (StatRow row in _rows.Values)
            row.Refresh();
    }

    private void OnVisibilityChanged()
    {
        if (Visible)
        {
            _wasVisible = true;
            return;
        }

        if (_wasVisible)
        {
            Cleanup();
            QueueFree();
        }
    }

    private void Cleanup()
    {
        if (_cleaned)
            return;
        _cleaned = true;
        _target.CurrentHpChanged -= OnStatChanged;
        _target.MaxHpChanged -= OnStatChanged;
        _target.BlockChanged -= OnStatChanged;
        _target.Died -= OnTargetDied;
        CreatureManipulationStateService.StateChanged -= RefreshRows;
        VisibilityChanged -= OnVisibilityChanged;
    }

    private static Texture2D? LoadGameTexture(string path)
    {
        if (TextureCache.TryGetValue(path, out Texture2D? cached))
            return cached;

        string localPath = path.Replace("res://images/", "res://Loadout/images/");
        string resolvedPath = ResourceLoader.Exists(localPath) ? localPath : path;
        Texture2D? texture = ResourceLoader.Exists(resolvedPath)
            ? ResourceLoader.Load<Texture2D>(resolvedPath, null, ResourceLoader.CacheMode.Reuse)
            : null;
        TextureCache[path] = texture;
        return texture;
    }

    private sealed partial class StatRow : Control
    {
        private readonly Creature _target;
        private readonly CreatureManipulationStat _stat;
        private readonly LineEdit _entry;
        private readonly TextureRect _entryBackground;
        private readonly NLoadoutToggle _lock;
        private bool _refreshing;
        private bool _submitted;

        public StatRow(
            Creature target,
            CreatureManipulationStat stat,
            string labelText)
        {
            _target = target;
            _stat = stat;
            CustomMinimumSize = new Vector2(660f, 78f);
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
            SizeFlagsVertical = SizeFlags.ShrinkCenter;
            MouseFilter = MouseFilterEnum.Pass;

            TextureRect rowBackground = new()
            {
                Name = "RowBackground",
                Texture = LoadGameTexture("res://images/ui/reward_screen/reward_item_button.png"),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                Modulate = new Color(0.72f, 0.79f, 0.84f, 0.92f),
                MouseFilter = MouseFilterEnum.Ignore
            };
            rowBackground.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(rowBackground);

            HBoxContainer rowContent = new()
            {
                Name = "RowContent",
                Alignment = BoxContainer.AlignmentMode.Center,
                MouseFilter = MouseFilterEnum.Pass
            };
            rowContent.SetAnchorsPreset(LayoutPreset.FullRect);
            rowContent.OffsetLeft = 24f;
            rowContent.OffsetTop = 10f;
            rowContent.OffsetRight = -24f;
            rowContent.OffsetBottom = -10f;
            rowContent.AddThemeConstantOverride("separation", 14);
            AddChild(rowContent);

            (string iconPath, Color accent) = stat switch
            {
                CreatureManipulationStat.CurrentHp => (
                    "res://images/atlases/ui_atlas.sprites/top_bar/top_bar_heart.tres",
                    new Color(0.92f, 0.32f, 0.28f)),
                CreatureManipulationStat.MaxHp => (
                    "res://images/atlases/ui_atlas.sprites/top_bar/top_bar_heart.tres",
                    StsColors.gold),
                CreatureManipulationStat.Block => (
                    "res://images/ui/combat/block.png",
                    new Color(0.38f, 0.72f, 1f)),
                _ => (string.Empty, StsColors.cream)
            };

            TextureRect icon = new()
            {
                Name = "StatIcon",
                Texture = string.IsNullOrEmpty(iconPath) ? null : LoadGameTexture(iconPath),
                CustomMinimumSize = new Vector2(42f, 42f),
                SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                Modulate = accent,
                MouseFilter = MouseFilterEnum.Ignore
            };
            rowContent.AddChild(icon);

            MegaLabel label = CommonHelpers.CreateButtonLabel(
                "Label",
                labelText,
                Vector2.Zero,
                new Vector2(0f, 54f),
                25,
                HorizontalAlignment.Left,
                accent);
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            label.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.6f));
            label.AddThemeConstantOverride("shadow_offset_x", 3);
            label.AddThemeConstantOverride("shadow_offset_y", 2);
            rowContent.AddChild(label);

            HBoxContainer actions = new()
            {
                Name = "Actions",
                CustomMinimumSize = new Vector2(380f, 56f),
                SizeFlagsHorizontal = SizeFlags.ShrinkEnd,
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                Alignment = BoxContainer.AlignmentMode.End,
                MouseFilter = MouseFilterEnum.Pass
            };
            actions.AddThemeConstantOverride("separation", 12);
            rowContent.AddChild(actions);

            Control entryFrame = new()
            {
                Name = "EntryFrame",
                CustomMinimumSize = new Vector2(190f, 50f),
                SizeFlagsHorizontal = SizeFlags.ShrinkEnd,
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                MouseFilter = MouseFilterEnum.Pass
            };
            _entryBackground = new TextureRect
            {
                Name = "Background",
                Texture = LoadGameTexture("res://images/ui/reward_screen/reward_item_button.png"),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                Modulate = new Color(0.62f, 0.68f, 0.72f, 0.95f),
                MouseFilter = MouseFilterEnum.Ignore
            };
            _entryBackground.SetAnchorsPreset(LayoutPreset.FullRect);
            entryFrame.AddChild(_entryBackground);

            _entry = new LineEdit
            {
                Alignment = HorizontalAlignment.Center,
                MouseFilter = MouseFilterEnum.Stop
            };
            _entry.SetAnchorsPreset(LayoutPreset.FullRect);
            _entry.OffsetLeft = 12f;
            _entry.OffsetTop = 2f;
            _entry.OffsetRight = -12f;
            _entry.OffsetBottom = -2f;
            _entry.AddThemeFontOverride(
                "font",
                CommonHelpers.LoadGameFont("res://themes/kreon_bold_glyph_space_one.tres"));
            _entry.AddThemeFontSizeOverride("font_size", 25);
            _entry.AddThemeColorOverride("font_color", StsColors.cream);
            _entry.AddThemeColorOverride("font_focus_color", StsColors.gold);
            _entry.AddThemeColorOverride("caret_color", StsColors.gold);
            _entry.AddThemeColorOverride("selection_color", new Color(0.55f, 0.36f, 0.12f, 0.75f));
            StyleBoxEmpty emptyStyle = new();
            _entry.AddThemeStyleboxOverride("normal", emptyStyle);
            _entry.AddThemeStyleboxOverride("focus", emptyStyle);
            _entry.AddThemeStyleboxOverride("read_only", emptyStyle);
            _entry.TextSubmitted += OnSubmitted;
            _entry.FocusEntered += OnFocusEntered;
            _entry.FocusExited += OnFocusExited;
            entryFrame.AddChild(_entry);
            actions.AddChild(entryFrame);

            _lock = new NLoadoutToggle
            {
                Name = "Lock",
                CustomMinimumSize = new Vector2(170f, 52f),
                SizeFlagsHorizontal = SizeFlags.ShrinkEnd,
                SizeFlagsVertical = SizeFlags.ShrinkCenter
            };
            _lock.Init(
                "lock",
                LocMan.Loc("TILDEKEY_LOCK", "Lock"),
                checkedByDefault: false);
            _lock.Connect(
                NLoadoutToggle.SignalName.Toggled,
                Callable.From<NLoadoutToggle>(OnLockToggled));
            actions.AddChild(_lock);
        }

        public void Refresh()
        {
            _refreshing = true;
            int value = GetValue();
            if (!_entry.HasFocus())
                _entry.Text = value.ToString(CultureInfo.InvariantCulture);
            bool isLocked = _target.CombatId is uint combatId
                            && CreatureManipulationStateService.TryGetLock(
                                combatId,
                                _stat,
                                out _);
            _lock.SetChecked(isLocked, emit: false);
            _refreshing = false;
        }

        public override void _ExitTree()
        {
            _entry.TextSubmitted -= OnSubmitted;
            _entry.FocusEntered -= OnFocusEntered;
            _entry.FocusExited -= OnFocusExited;
        }

        private void OnSubmitted(string _)
        {
            _submitted = true;
            Commit();
            _entry.ReleaseFocus();
        }

        private void OnFocusEntered()
        {
            _entryBackground.Modulate = new Color(0.92f, 0.94f, 1f, 1f);
        }

        private void OnFocusExited()
        {
            _entryBackground.Modulate = new Color(0.62f, 0.68f, 0.72f, 0.95f);
            if (_submitted)
            {
                _submitted = false;
                return;
            }
            Commit();
        }

        private void Commit()
        {
            if (_refreshing || !TryParse(_entry.Text, out int value))
            {
                Refresh();
                return;
            }

            CreatureManipulationStateService.RequestSetStat(_target, _stat, value);
        }

        private void OnLockToggled(NLoadoutToggle toggle)
        {
            if (_refreshing)
                return;

            int value = TryParse(_entry.Text, out int parsed) ? parsed : GetValue();
            CreatureManipulationStateService.RequestSetLock(
                _target,
                _stat,
                toggle.IsChecked,
                value);
        }

        private int GetValue() => _stat switch
        {
            CreatureManipulationStat.CurrentHp => _target.CurrentHp,
            CreatureManipulationStat.MaxHp => _target.MaxHp,
            CreatureManipulationStat.Block => _target.Block,
            _ => 0
        };

        private static bool TryParse(string? text, out int value)
        {
            if (!long.TryParse(
                    text?.Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out long parsed))
            {
                value = 0;
                return false;
            }

            value = (int)Math.Clamp(parsed, 0L, int.MaxValue);
            return true;
        }
    }
}
