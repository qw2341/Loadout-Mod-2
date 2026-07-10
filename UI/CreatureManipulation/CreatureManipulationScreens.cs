#nullable enable

namespace Loadout.UI.CreatureManipulation;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Loadout.PanelItems;
using Loadout.Services.CreatureManipulation;
using Loadout.UI.Screens;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;

internal sealed class CreatureMorphOption
{
    public required AbstractModel Model { get; init; }
    public required string Name { get; init; }
    public ModelId Id => Model.Id;
    public bool IsMonster => Model is MonsterModel;
}

public static class CreatureManipulationScreens
{
    private static readonly Vector2 PowerButtonSize = new(224f, 92f);
    private static readonly Vector2 MorphButtonSize = new(224f, 86f);

    public static void OpenPowerScreen(Creature target)
    {
        if (!IsTargetUsable(target) || NLoadoutPanelRoot.Instance is null)
            return;

        PackedScene scene = GD.Load<PackedScene>("res://UI/Screens/GenericSelectScreen.tscn");
        NGenericSelectScreen screen = scene.Instantiate<NGenericSelectScreen>();
        screen.Visible = false;
        screen.Name = $"CreaturePowerManipulation_{target.CombatId}_{Time.GetTicksMsec()}";

        SelectItemAdapter<PowerModel> adapter = new()
        {
            GetId = power => power.Id.ToString(),
            GetName = FormatPowerTitle,
            GetSearchText = power => $"{power.Id} {FormatPowerTitle(power)} {power.Description}",
            CreateView = (power, _) => CreatePowerView(target, power),
            UpdateView = (power, view, _) => UpdatePowerView(target, power, view),
            BindActivation = (power, view, _) => BindPowerActivation(target, power, view, screen)
        };

        IReadOnlyList<PowerModel> powers = ModelDb.AllPowers.ToList();
        screen.Configure(powers, adapter, builder =>
        {
            builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
            builder.Materialization(SelectMaterializationMode.Eager);
            builder.Layout(5, PowerButtonSize, 22, 22, fixedSlots: false);
            builder.ActionButton("clear_buffs", "Clear Current Buffs", _ => CreatureManipulationStateService.RequestClearPowers(target, PowerType.Buff));
            builder.ActionButton("clear_debuffs", "Clear Current Debuffs", _ => CreatureManipulationStateService.RequestClearPowers(target, PowerType.Debuff));
            builder.FilterGroup("type", "Type");
            builder.Filter("buff", "Buff", power => power.Type == PowerType.Buff, "type");
            builder.Filter("debuff", "Debuff", power => power.Type == PowerType.Debuff, "type");
            builder.Filter("none", "None", power => power.Type == PowerType.None, "type");
            builder.Sorter("name", "Name", (a, b) => string.Compare(FormatPowerTitle(a), FormatPowerTitle(b), StringComparison.Ordinal), activeByDefault: true);
            builder.Sorter("id", "ID", (a, b) => string.Compare(a.Id.Entry, b.Id.Entry, StringComparison.Ordinal));
            CommonHelpers.AddModFilters(builder, powers);
        });

        void RefreshChangedCreature(uint combatId)
        {
            if (target.CombatId != combatId || !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree())
                return;

            screen.CallDeferred(nameof(NGenericSelectScreen.RefreshCurrentItemStates));
        }

        CreatureManipulationStateService.CreatureChanged += RefreshChangedCreature;
        screen.ScreenClosed += () =>
        {
            CreatureManipulationStateService.CreatureChanged -= RefreshChangedCreature;
            screen.QueueFree();
        };
        screen.Cancelled += NLoadoutPanelRoot.CloseTopLoadoutScreen;
        screen.Confirmed += _ => NLoadoutPanelRoot.CloseTopLoadoutScreen();
        NLoadoutPanelRoot.Instance.OpenScreen(screen);
    }

    public static void OpenMorphScreen(Creature target)
    {
        if (!IsTargetUsable(target) || NLoadoutPanelRoot.Instance is null)
            return;

        PackedScene scene = GD.Load<PackedScene>("res://UI/Screens/GenericSelectScreen.tscn");
        NGenericSelectScreen screen = scene.Instantiate<NGenericSelectScreen>();
        screen.Visible = false;
        screen.Name = $"CreatureMorphManipulation_{target.CombatId}_{Time.GetTicksMsec()}";

        IReadOnlyList<CreatureMorphOption> options = ModelDb.Monsters
            .Cast<AbstractModel>()
            .Concat(ModelDb.AllCharacters)
            .GroupBy(model => model.Id.ToString(), StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(model => new CreatureMorphOption
            {
                Model = model,
                Name = FormatMorphTitle(model)
            })
            .OrderBy(option => option.Name, StringComparer.Ordinal)
            .ToList();

        SelectItemAdapter<CreatureMorphOption> adapter = new()
        {
            GetId = option => option.Id.ToString(),
            GetName = option => option.Name,
            GetSearchText = option => $"{option.Id} {option.Name} {(option.IsMonster ? "monster" : "character")}",
            CreateView = (option, _) => CreateMorphView(option),
            BindActivation = (_, view, activate) => CommonHelpers.BindGuiReleaseActivation(view, activate)
        };

        screen.Configure(options, adapter, builder =>
        {
            builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
            builder.Materialization(SelectMaterializationMode.Lazy);
            builder.Layout(5, MorphButtonSize, 22, 22, fixedSlots: false);
            builder.ActionButton("restore_original", "Restore Original Appearance", _ =>
            {
                CreatureManipulationStateService.RequestRestoreOriginalAppearance(target);
                NLoadoutPanelRoot.CloseTopLoadoutScreen();
            });
            builder.FilterGroup("kind", "Kind");
            builder.Filter("monster", "Monsters", option => option.IsMonster, "kind");
            builder.Filter("character", "Characters", option => !option.IsMonster, "kind");
            builder.Sorter("name", "Name", (a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal), activeByDefault: true);
            builder.Sorter("id", "ID", (a, b) => string.Compare(a.Id.Entry, b.Id.Entry, StringComparison.Ordinal));
        });

        screen.ItemActivated += (item, _) =>
        {
            if (item.UntypedModel is CreatureMorphOption option)
                CreatureManipulationStateService.RequestMorph(target, option.Id);
            NLoadoutPanelRoot.CloseTopLoadoutScreen();
        };
        screen.ScreenClosed += screen.QueueFree;
        screen.Cancelled += NLoadoutPanelRoot.CloseTopLoadoutScreen;
        screen.Confirmed += _ => NLoadoutPanelRoot.CloseTopLoadoutScreen();
        NLoadoutPanelRoot.Instance.OpenScreen(screen);
    }

    public static void OpenStatScreen(Creature target)
    {
        if (!IsTargetUsable(target) || NLoadoutPanelRoot.Instance is null)
            return;

        NCreatureStatEditScreen screen = new(target)
        {
            Name = $"CreatureStatManipulation_{target.CombatId}_{Time.GetTicksMsec()}"
        };
        NLoadoutPanelRoot.Instance.OpenScreen(screen);
    }

    private static Control CreatePowerView(Creature target, PowerModel power)
    {
        Button button = CommonHelpers.CreateModelButton(PowerButtonSize);
        button.Text = BuildPowerText(target, power);
        button.AddThemeFontSizeOverride("font_size", 18);
        button.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        return button;
    }

    private static void UpdatePowerView(Creature target, PowerModel power, Control view)
    {
        if (view is Button button)
            button.Text = BuildPowerText(target, power);
    }

    private static bool BindPowerActivation(Creature target, PowerModel power, Control view, NGenericSelectScreen screen)
    {
        view.GuiInput += input =>
        {
            if (input is not InputEventMouseButton mouseButton || mouseButton.Pressed)
                return;

            if (mouseButton.ButtonIndex is not (MouseButton.Left or MouseButton.Right))
                return;

            int multiplier = screen.GetCurrentActivationMultiplier();
            int delta = mouseButton.ButtonIndex == MouseButton.Right ? -multiplier : multiplier;
            CreatureManipulationStateService.RequestAdjustPower(target, power, delta);
            view.AcceptEvent();
        };
        return true;
    }

    private static string BuildPowerText(Creature target, PowerModel power)
    {
        int amount = CreatureManipulationStateService.GetPowerAmount(target, power.Id);
        return $"{FormatPowerTitle(power)}\n{amount:+#;-#;0}";
    }

    private static Control CreateMorphView(CreatureMorphOption option)
    {
        Button button = CommonHelpers.CreateModelButton(MorphButtonSize);
        button.Text = $"{option.Name}\n{(option.IsMonster ? "Monster" : "Character")}";
        button.AddThemeFontSizeOverride("font_size", 18);
        button.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        return button;
    }

    private static string FormatPowerTitle(PowerModel power)
    {
        try
        {
            string text = power.Title.GetFormattedText();
            return string.IsNullOrWhiteSpace(text) ? power.Id.Entry : text;
        }
        catch
        {
            return power.Id.Entry;
        }
    }

    private static string FormatMorphTitle(AbstractModel model)
    {
        try
        {
            string text = model switch
            {
                MonsterModel monster => monster.Title.GetFormattedText(),
                CharacterModel character => character.Title.GetFormattedText(),
                _ => model.Id.Entry
            };
            return string.IsNullOrWhiteSpace(text) ? model.Id.Entry : text;
        }
        catch
        {
            return model.Id.Entry;
        }
    }

    private static bool IsTargetUsable(Creature target)
    {
        return target.CombatId.HasValue && target.CombatId.Value != 0;
    }
}

public partial class NCreatureStatEditScreen : Control
{
    private readonly Creature _target;
    private readonly Dictionary<string, NLoadoutNumberStepper> _steppers = [];
    private readonly Dictionary<string, NLoadoutToggle> _locks = [];

    public NCreatureStatEditScreen(Creature target)
    {
        _target = target;
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        ProcessMode = ProcessModeEnum.Always;
    }

    public override void _Ready()
    {
        BuildUi();
        CreatureManipulationStateService.CreatureChanged += OnCreatureChanged;
    }

    private bool _wasVisible;

    public override void _Notification(int what)
    {
        if (what != NotificationVisibilityChanged)
            return;

        if (Visible)
        {
            _wasVisible = true;
            return;
        }

        if (_wasVisible)
            Callable.From(QueueFree).CallDeferred();
    }

    public override void _ExitTree()
    {
        CreatureManipulationStateService.CreatureChanged -= OnCreatureChanged;
    }

    private void BuildUi()
    {
        ColorRect shade = new()
        {
            Color = new Color(0f, 0f, 0f, 0.7f),
            MouseFilter = MouseFilterEnum.Stop
        };
        shade.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(shade);

        PanelContainer panel = new()
        {
            CustomMinimumSize = new Vector2(620f, 330f),
            MouseFilter = MouseFilterEnum.Stop
        };
        panel.SetAnchorsPreset(LayoutPreset.Center);
        panel.Position = new Vector2(-310f, -165f);
        AddChild(panel);

        MarginContainer margin = new();
        margin.AddThemeConstantOverride("margin_left", 28);
        margin.AddThemeConstantOverride("margin_right", 28);
        margin.AddThemeConstantOverride("margin_top", 24);
        margin.AddThemeConstantOverride("margin_bottom", 24);
        panel.AddChild(margin);

        VBoxContainer rows = new();
        rows.AddThemeConstantOverride("separation", 14);
        margin.AddChild(rows);

        Label title = new()
        {
            Text = $"Edit {_target.Name}",
            HorizontalAlignment = HorizontalAlignment.Center,
            CustomMinimumSize = new Vector2(0f, 42f)
        };
        title.AddThemeFontSizeOverride("font_size", 28);
        rows.AddChild(title);

        AddStatRow(rows, CreatureManipulationStateService.CurrentHpStatId, "Current HP");
        AddStatRow(rows, CreatureManipulationStateService.MaxHpStatId, "Max HP");
        AddStatRow(rows, CreatureManipulationStateService.BlockStatId, "Block");

        Button close = new()
        {
            Text = "Done",
            CustomMinimumSize = new Vector2(180f, 46f),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter
        };
        close.Pressed += NLoadoutPanelRoot.CloseTopLoadoutScreen;
        rows.AddChild(close);
    }

    private void AddStatRow(VBoxContainer parent, string statId, string labelText)
    {
        HBoxContainer row = new()
        {
            CustomMinimumSize = new Vector2(0f, 56f)
        };
        row.AddThemeConstantOverride("separation", 12);
        parent.AddChild(row);

        Label label = new()
        {
            Text = labelText,
            CustomMinimumSize = new Vector2(170f, 48f),
            VerticalAlignment = VerticalAlignment.Center
        };
        label.AddThemeFontSizeOverride("font_size", 22);
        row.AddChild(label);

        NLoadoutNumberStepper stepper = new()
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        stepper.Init(CreatureManipulationStateService.GetStat(_target, statId), int.MinValue, int.MaxValue);
        stepper.ValueChanged += value =>
        {
            CreatureManipulationStateService.RequestSetStat(_target, statId, value);
            if (_locks[statId].IsChecked)
                CreatureManipulationStateService.RequestSetStatLock(_target, statId, value, true);
        };
        row.AddChild(stepper);
        _steppers[statId] = stepper;

        NLoadoutToggle toggle = new()
        {
            CustomMinimumSize = new Vector2(130f, 48f)
        };
        toggle.Init($"creature_{statId}_lock", "Lock", CreatureManipulationStateService.IsStatLocked(_target, statId));
        toggle.Connect(NLoadoutToggle.SignalName.Toggled, Callable.From<NLoadoutToggle>(changed =>
        {
            CreatureManipulationStateService.RequestSetStatLock(_target, statId, stepper.Value, changed.IsChecked);
        }));
        row.AddChild(toggle);
        _locks[statId] = toggle;
    }

    private void OnCreatureChanged(uint combatId)
    {
        if (_target.CombatId != combatId)
            return;

        Callable.From(RefreshValues).CallDeferred();
    }

    private void RefreshValues()
    {
        if (!GodotObject.IsInstanceValid(this))
            return;

        foreach ((string statId, NLoadoutNumberStepper stepper) in _steppers)
            stepper.SetValue(CreatureManipulationStateService.GetStat(_target, statId), emit: false);

        foreach ((string statId, NLoadoutToggle toggle) in _locks)
            toggle.SetChecked(CreatureManipulationStateService.IsStatLocked(_target, statId));
    }
}
