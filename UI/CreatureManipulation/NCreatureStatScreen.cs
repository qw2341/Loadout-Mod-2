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

public partial class NCreatureStatScreen : Control
{
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

        PanelContainer panel = new()
        {
            CustomMinimumSize = new Vector2(760f, 430f),
            MouseFilter = MouseFilterEnum.Stop
        };
        panel.SetAnchorsPreset(LayoutPreset.Center);
        panel.Position = new Vector2(-380f, -215f);
        StyleBoxFlat style = new()
        {
            BgColor = new Color(0.08f, 0.035f, 0.025f, 0.97f),
            BorderColor = new Color(0.55f, 0.28f, 0.13f),
            BorderWidthLeft = 4,
            BorderWidthTop = 4,
            BorderWidthRight = 4,
            BorderWidthBottom = 4,
            CornerRadiusTopLeft = 10,
            CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10,
            CornerRadiusBottomRight = 10,
            ContentMarginLeft = 34,
            ContentMarginTop = 24,
            ContentMarginRight = 34,
            ContentMarginBottom = 24
        };
        panel.AddThemeStyleboxOverride("panel", style);
        AddChild(panel);

        VBoxContainer content = new();
        content.AddThemeConstantOverride("separation", 18);
        panel.AddChild(content);

        MegaLabel title = CommonHelpers.CreateButtonLabel(
            "Title",
            LocMan.Loc("CREATURE_MANIP_STATS_TITLE", "Creature Stats"),
            Vector2.Zero,
            new Vector2(692f, 58f),
            34,
            HorizontalAlignment.Center,
            StsColors.gold);
        content.AddChild(title);

        AddRow(content, CreatureManipulationStat.CurrentHp,
            LocMan.Loc("CREATURE_MANIP_CURRENT_HP", "Current HP"));
        AddRow(content, CreatureManipulationStat.MaxHp,
            LocMan.Loc("CREATURE_MANIP_MAX_HP", "Max HP"));
        AddRow(content, CreatureManipulationStat.Block,
            LocMan.Loc("CREATURE_MANIP_BLOCK", "Block"));

        Button close = CommonHelpers.CreateModelButton(new Vector2(220f, 58f));
        close.Text = LocMan.Loc("CANCEL", "Cancel");
        close.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        close.AddThemeFontOverride(
            "font",
            CommonHelpers.LoadGameFont("res://themes/kreon_bold_glyph_space_one.tres"));
        close.AddThemeFontSizeOverride("font_size", 24);
        close.Pressed += NLoadoutPanelRoot.CloseTopLoadoutScreen;
        content.AddChild(close);
    }

    private void AddRow(
        VBoxContainer content,
        CreatureManipulationStat stat,
        string labelText)
    {
        StatRow row = new(_target, stat, labelText);
        _rows[stat] = row;
        content.AddChild(row);
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

    private sealed partial class StatRow : HBoxContainer
    {
        private readonly Creature _target;
        private readonly CreatureManipulationStat _stat;
        private readonly LineEdit _entry;
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
            CustomMinimumSize = new Vector2(692f, 62f);
            AddThemeConstantOverride("separation", 14);

            MegaLabel label = CommonHelpers.CreateButtonLabel(
                "Label",
                labelText,
                Vector2.Zero,
                new Vector2(260f, 52f),
                24,
                HorizontalAlignment.Left,
                StsColors.gold);
            AddChild(label);

            _entry = new LineEdit
            {
                CustomMinimumSize = new Vector2(190f, 46f),
                Alignment = HorizontalAlignment.Center,
                MouseFilter = MouseFilterEnum.Stop
            };
            _entry.AddThemeFontOverride(
                "font",
                CommonHelpers.LoadGameFont("res://themes/kreon_bold_glyph_space_one.tres"));
            _entry.AddThemeFontSizeOverride("font_size", 23);
            _entry.AddThemeColorOverride("font_color", StsColors.cream);
            _entry.AddThemeColorOverride("font_focus_color", StsColors.gold);
            _entry.TextSubmitted += OnSubmitted;
            _entry.FocusExited += OnFocusExited;
            AddChild(_entry);

            _lock = new NLoadoutToggle
            {
                Name = "Lock",
                CustomMinimumSize = new Vector2(190f, 52f),
                SizeFlagsHorizontal = SizeFlags.ShrinkEnd
            };
            _lock.Init(
                "lock",
                LocMan.Loc("TILDEKEY_LOCK", "Lock"),
                checkedByDefault: false);
            _lock.Connect(
                NLoadoutToggle.SignalName.Toggled,
                Callable.From<NLoadoutToggle>(OnLockToggled));
            AddChild(_lock);
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
            _entry.FocusExited -= OnFocusExited;
        }

        private void OnSubmitted(string _)
        {
            _submitted = true;
            Commit();
            _entry.ReleaseFocus();
        }

        private void OnFocusExited()
        {
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
