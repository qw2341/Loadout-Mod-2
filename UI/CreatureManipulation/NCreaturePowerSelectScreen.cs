#nullable enable

namespace Loadout.UI.CreatureManipulation;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Loadout.PanelItems;
using Loadout.Services.CreatureManipulation;
using Loadout.UI.Screens;
using Loadout.UI.Managers;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Models;

public static class NCreaturePowerSelectScreen
{
    private static readonly Vector2 PowerButtonSize = new(220f, 104f);

    public static void Open(Creature target)
    {
        if (target.CombatId is not uint combatId
            || NLoadoutPanelRoot.Instance is not { } root)
        {
            return;
        }

        PackedScene? scene = GD.Load<PackedScene>("res://UI/Screens/GenericSelectScreen.tscn");
        NGenericSelectScreen screen = scene.Instantiate<NGenericSelectScreen>();
        screen.Name = $"CreaturePowerSelect_{combatId}";

        SelectItemAdapter<PowerModel> adapter = new()
        {
            GetId = power => power.Id.ToString(),
            GetName = CommonHelpers.FormatPowerTitle,
            GetSearchText = GetSearchText,
            CreateView = (power, _) =>
                PowerGiver.CreatePowerGridItem(power, GetLiveAmount(target, power.Id)),
            UpdateView = (power, view, _) => UpdateLiveAmount(view, target, power.Id),
            BindActivationWithCleanup = (power, view, _) =>
                BindActivation(screen, target, power, view)
        };

        IReadOnlyList<PowerModel> allPowers = ModelDb.AllPowers.ToList();
        screen.Configure(allPowers, adapter, builder =>
        {
            builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
            builder.Materialization(SelectMaterializationMode.Eager);
            builder.Layout(5, PowerButtonSize, 24, 24, fixedSlots: false);
            builder.FilterGroup("type", LocMan.Loc("FILTER_GROUP_TYPE", "Type"));
            builder.Filter(
                "buff",
                LocMan.Loc("POWER_TYPE_BUFF", "Buff"),
                power => power.Type == PowerType.Buff,
                "type");
            builder.Filter(
                "debuff",
                LocMan.Loc("POWER_TYPE_DEBUFF", "Debuff"),
                power => power.Type == PowerType.Debuff,
                "type");
            builder.Filter(
                "none",
                LocMan.Loc("NONE", "None"),
                power => power.Type == PowerType.None,
                "type");
            builder.FilterGroup("stack", LocMan.Loc("FILTER_GROUP_STACK", "Stack"));
            builder.Filter(
                "stack_none",
                LocMan.Loc("NONE", "None"),
                power => power.StackType == PowerStackType.None,
                "stack");
            builder.Filter(
                "counter",
                LocMan.Loc("POWER_STACK_COUNTER", "Counter"),
                power => power.StackType == PowerStackType.Counter,
                "stack");
            builder.Filter(
                "single",
                LocMan.Loc("POWER_STACK_SINGLE", "Single"),
                power => power.StackType == PowerStackType.Single,
                "stack");
            CommonHelpers.AddModFilters(builder, allPowers);
            builder.Sorter(
                "name",
                LocMan.Loc("SORT_NAME", "Name"),
                (a, b) => string.Compare(
                    CommonHelpers.FormatPowerTitle(a),
                    CommonHelpers.FormatPowerTitle(b),
                    StringComparison.Ordinal),
                activeByDefault: true);
            builder.Sorter(
                "id",
                LocMan.Loc("SORT_ID", "ID"),
                (a, b) => string.Compare(a.Id.Entry, b.Id.Entry, StringComparison.Ordinal));
            builder.Sorter(
                "type",
                LocMan.Loc("SORT_TYPE", "Type"),
                (a, b) => a.Type.CompareTo(b.Type));
        });

        void RefreshPower(PowerModel _) => screen.RefreshCurrentItemStates();
        void RefreshPowerAmount(PowerModel _, int __, bool ___) => screen.RefreshCurrentItemStates();
        void RefreshPowerDecrease(PowerModel _, bool __) => screen.RefreshCurrentItemStates();
        void CloseForDeath(Creature _) => NLoadoutPanelRoot.CloseTopLoadoutScreen();

        target.PowerApplied += RefreshPower;
        target.PowerIncreased += RefreshPowerAmount;
        target.PowerDecreased += RefreshPowerDecrease;
        target.PowerRemoved += RefreshPower;
        target.Died += CloseForDeath;

        bool cleaned = false;
        void Cleanup()
        {
            if (cleaned)
                return;
            cleaned = true;
            target.PowerApplied -= RefreshPower;
            target.PowerIncreased -= RefreshPowerAmount;
            target.PowerDecreased -= RefreshPowerDecrease;
            target.PowerRemoved -= RefreshPower;
            target.Died -= CloseForDeath;
            screen.QueueFree();
        }

        screen.Cancelled += NLoadoutPanelRoot.CloseTopLoadoutScreen;
        screen.Confirmed += _ => NLoadoutPanelRoot.CloseTopLoadoutScreen();
        screen.ScreenClosed += Cleanup;
        root.OpenScreen(screen);
        screen.RefreshCurrentItemStates();
    }

    private static Action? BindActivation(
        NGenericSelectScreen screen,
        Creature target,
        PowerModel power,
        Control view)
    {
        void OnInput(InputEvent input)
        {
            if (input is not InputEventMouseButton mouse
                || mouse.Pressed
                || mouse.ButtonIndex is not (MouseButton.Left or MouseButton.Right))
            {
                return;
            }

            int multiplier = screen.GetCurrentActivationMultiplier();
            int delta = mouse.ButtonIndex == MouseButton.Right ? -multiplier : multiplier;
            CreatureManipulationStateService.RequestAdjustPower(target, power.Id, delta);
            view.AcceptEvent();
        }

        view.GuiInput += OnInput;
        return () =>
        {
            if (GodotObject.IsInstanceValid(view))
                view.GuiInput -= OnInput;
        };
    }

    private static void UpdateLiveAmount(Control view, Creature target, ModelId powerId)
    {
        int amount = GetLiveAmount(target, powerId);
        if (view.GetNodeOrNull<MegaLabel>("PowerAmount") is not { } label)
            return;
        label.Text = amount == 0 ? string.Empty : amount.ToString();
        label.Visible = amount != 0;
    }

    private static int GetLiveAmount(Creature target, ModelId powerId) =>
        target.Powers
            .Where(power => power.Id == powerId
                            || string.Equals(
                                power.Id.ToString(),
                                powerId.ToString(),
                                StringComparison.Ordinal))
            .Sum(power => power.DisplayAmount);

    private static string GetSearchText(PowerModel power)
    {
        try
        {
            return $"{power.Id} {CommonHelpers.FormatPowerTitle(power)} {power.Description.GetFormattedText()}";
        }
        catch
        {
            return $"{power.Id} {CommonHelpers.FormatPowerTitle(power)}";
        }
    }
}
