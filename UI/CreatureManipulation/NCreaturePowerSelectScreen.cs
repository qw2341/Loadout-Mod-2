#nullable enable

namespace Loadout.UI.CreatureManipulation;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Loadout.PanelItems;
using Loadout.Services.CreatureManipulation;
using Loadout.Services.PowerGiver;
using Loadout.UI.Screens;
using Loadout.UI.Managers;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Models;

public static class NCreaturePowerSelectScreen
{
    private const string CurrentPowersToggleId = "current_powers";
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
        PowerGiverStateService.EnsureLoaded();

        bool favoritesOnly = false;
        HashSet<string> currentPowerIds = target.Powers
            .Select(power => PowerKey(power.Id))
            .ToHashSet(StringComparer.Ordinal);
        Dictionary<string, string> powerTitles = new(StringComparer.Ordinal);
        Dictionary<string, PowerType> powerTypes = new(StringComparer.Ordinal);
        Dictionary<string, PowerStackType> powerStackTypes = new(StringComparer.Ordinal);
        bool opened = false;
        bool cleaned = false;
        bool membershipRefreshQueued = false;

        string GetPowerTitle(PowerModel power)
        {
            string powerId = PowerKey(power.Id);
            if (!powerTitles.TryGetValue(powerId, out string? title))
            {
                title = CommonHelpers.FormatPowerTitle(power);
                powerTitles[powerId] = title;
            }

            return title;
        }

        PowerType GetPowerType(PowerModel power)
        {
            string powerId = PowerKey(power.Id);
            if (!powerTypes.TryGetValue(powerId, out PowerType type))
            {
                type = PowerGiver.GetSafePowerType(power);
                powerTypes[powerId] = type;
            }

            return type;
        }

        PowerStackType GetPowerStackType(PowerModel power)
        {
            string powerId = PowerKey(power.Id);
            if (!powerStackTypes.TryGetValue(powerId, out PowerStackType stackType))
            {
                stackType = PowerGiver.GetSafePowerStackType(power);
                powerStackTypes[powerId] = stackType;
            }

            return stackType;
        }

        SelectItemAdapter<PowerModel> adapter = new()
        {
            GetId = power => PowerKey(power.Id),
            GetName = GetPowerTitle,
            GetSearchTextFromName = PowerGiver.CreateSafePowerSearchText,
            CreateView = (power, _) =>
                PowerGiver.CreatePowerGridItem(
                    power,
                    GetLiveAmount(target, power.Id),
                    PowerGiverStateService.IsFavorite(PowerKey(power.Id)) && !favoritesOnly,
                    GetPowerTitle(power)),
            UpdateView = (power, view, _) =>
                UpdatePowerGridItem(view, target, power, favoritesOnly),
            BindActivationWithCleanup = (power, view, _) =>
                BindActivation(screen, target, power, view, () => favoritesOnly)
        };

        IReadOnlyList<PowerModel> allPowers = ModelDb.AllPowers.ToList();
        screen.Configure(allPowers, adapter, builder =>
        {
            builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
            builder.Materialization(SelectMaterializationMode.Lazy);
            builder.Layout(5, PowerButtonSize, 24, 24, fixedSlots: false);
            builder.ActionButton(
                "clear_current_buffs",
                LocMan.Loc("POWER_GIVER_CLEAR_CURRENT_BUFFS", "Clear Current Buffs"),
                _ => CreatureManipulationStateService.RequestClearPowers(target, PowerType.Buff),
                section: SelectSidebarSection.Bottom);
            builder.ActionButton(
                "clear_current_debuffs",
                LocMan.Loc("POWER_GIVER_CLEAR_CURRENT_DEBUFFS", "Clear Current Debuffs"),
                _ => CreatureManipulationStateService.RequestClearPowers(target, PowerType.Debuff),
                section: SelectSidebarSection.Bottom);
            builder.Toggle(
                CurrentPowersToggleId,
                LocMan.Loc("CREATURE_MANIP_CURRENT_POWERS", "Current Powers"),
                checkedByDefault: true,
                section: SelectSidebarSection.Bottom,
                affectsVisibility: true);
            builder.CustomVisibilityPredicate(power =>
                (!favoritesOnly || PowerGiverStateService.IsFavorite(PowerKey(power.Id)))
                && (!screen.IsToggleEnabled(CurrentPowersToggleId)
                    || currentPowerIds.Contains(PowerKey(power.Id))));
            builder.FilterGroup("type", LocMan.Loc("FILTER_GROUP_TYPE", "Type"));
            builder.Filter(
                "buff",
                LocMan.Loc("POWER_TYPE_BUFF", "Buff"),
                power => GetPowerType(power) == PowerType.Buff,
                "type");
            builder.Filter(
                "debuff",
                LocMan.Loc("POWER_TYPE_DEBUFF", "Debuff"),
                power => GetPowerType(power) == PowerType.Debuff,
                "type");
            builder.Filter(
                "none",
                LocMan.Loc("NONE", "None"),
                power => GetPowerType(power) == PowerType.None,
                "type");
            builder.FilterGroup("stack", LocMan.Loc("FILTER_GROUP_STACK", "Stack"));
            builder.Filter(
                "stack_none",
                LocMan.Loc("NONE", "None"),
                power => GetPowerStackType(power) == PowerStackType.None,
                "stack");
            builder.Filter(
                "counter",
                LocMan.Loc("POWER_STACK_COUNTER", "Counter"),
                power => GetPowerStackType(power) == PowerStackType.Counter,
                "stack");
            builder.Filter(
                "single",
                LocMan.Loc("POWER_STACK_SINGLE", "Single"),
                power => GetPowerStackType(power) == PowerStackType.Single,
                "stack");
            CommonHelpers.AddModFilters(builder, allPowers);
            builder.Sorter(
                "name",
                LocMan.Loc("SORT_NAME", "Name"),
                (a, b) => string.Compare(
                    GetPowerTitle(a),
                    GetPowerTitle(b),
                    StringComparison.Ordinal),
                activeByDefault: true);
            builder.Sorter(
                "id",
                LocMan.Loc("SORT_ID", "ID"),
                (a, b) => string.Compare(a.Id.Entry, b.Id.Entry, StringComparison.Ordinal));
            builder.Sorter(
                "type",
                LocMan.Loc("SORT_TYPE", "Type"),
                (a, b) => GetPowerType(a).CompareTo(GetPowerType(b)));
        });

        CommonHelpers.AddFavoritesModeDropdown(
            screen,
            "CreaturePowerFavoritesDropdown",
            () => favoritesOnly,
            value => favoritesOnly = value);

        void QueueMembershipRefresh()
        {
            if (membershipRefreshQueued || cleaned)
                return;

            membershipRefreshQueued = true;
            Callable.From(() =>
            {
                membershipRefreshQueued = false;
                if (cleaned
                    || !GodotObject.IsInstanceValid(screen)
                    || !screen.IsInsideTree())
                {
                    return;
                }

                screen.RefreshLayout(resetScroll: false, updateExistingViews: false);
            }).CallDeferred();
        }

        void RefreshPowerView(PowerModel power, bool membershipChanged)
        {
            screen.RefreshItemView(PowerKey(power.Id));
            if (membershipChanged && screen.IsToggleEnabled(CurrentPowersToggleId))
                QueueMembershipRefresh();
        }

        void RefreshPowerApplied(PowerModel power)
        {
            bool membershipChanged = currentPowerIds.Add(PowerKey(power.Id));
            RefreshPowerView(power, membershipChanged);
        }

        void RefreshPowerAmount(PowerModel power, int _, bool __) =>
            screen.RefreshItemView(PowerKey(power.Id));

        void RefreshPowerDecrease(PowerModel power, bool _) =>
            screen.RefreshItemView(PowerKey(power.Id));

        void RefreshPowerRemoved(PowerModel power)
        {
            bool stillPresent = target.Powers.Any(candidate => SamePowerId(candidate.Id, power.Id));
            bool membershipChanged = !stillPresent && currentPowerIds.Remove(PowerKey(power.Id));
            RefreshPowerView(power, membershipChanged);
        }

        void CloseForDeath(Creature _) => NLoadoutPanelRoot.CloseTopLoadoutScreen();

        target.PowerApplied += RefreshPowerApplied;
        target.PowerIncreased += RefreshPowerAmount;
        target.PowerDecreased += RefreshPowerDecrease;
        target.PowerRemoved += RefreshPowerRemoved;
        target.Died += CloseForDeath;

        void Cleanup()
        {
            // RegisterScreen deliberately hides a newly attached screen before
            // PushScreen opens it. NGenericSelectScreen reports that initial
            // hidden state through ScreenClosed, so ignore it until this
            // particular screen has actually been opened once.
            if (!opened || cleaned)
                return;
            cleaned = true;
            target.PowerApplied -= RefreshPowerApplied;
            target.PowerIncreased -= RefreshPowerAmount;
            target.PowerDecreased -= RefreshPowerDecrease;
            target.PowerRemoved -= RefreshPowerRemoved;
            target.Died -= CloseForDeath;
            screen.QueueFree();
        }

        screen.Cancelled += NLoadoutPanelRoot.CloseTopLoadoutScreen;
        screen.Confirmed += _ => NLoadoutPanelRoot.CloseTopLoadoutScreen();
        screen.ScreenClosed += Cleanup;
        root.OpenScreen(screen);
        opened = screen.IsVisibleInTree();
        screen.RefreshCurrentItemStates();
    }

    private static Action? BindActivation(
        NGenericSelectScreen screen,
        Creature target,
        PowerModel power,
        Control view,
        Func<bool> getFavoritesOnly)
    {
        void OnInput(InputEvent input)
        {
            if (input is not InputEventMouseButton mouse
                || mouse.Pressed
                || mouse.ButtonIndex is not (MouseButton.Left or MouseButton.Right))
            {
                return;
            }

            if (mouse.AltPressed || Input.IsKeyPressed(Key.Alt))
            {
                PowerGiverStateService.ToggleFavorite(PowerKey(power.Id));
                if (getFavoritesOnly())
                    screen.RefreshLayout(resetScroll: false, updateExistingViews: false);
                else
                    screen.RefreshItemView(PowerKey(power.Id));
                view.AcceptEvent();
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

    private static void UpdatePowerGridItem(
        Control view,
        Creature target,
        PowerModel power,
        bool favoritesOnly)
    {
        int amount = GetLiveAmount(target, power.Id);
        if (view.GetNodeOrNull<MegaLabel>("PowerAmount") is { } label)
        {
            label.Text = amount == 0 ? string.Empty : amount.ToString();
            label.Visible = amount != 0;
        }

        if (view.GetNodeOrNull<CanvasItem>("FavoriteGlow") is { } favoriteGlow)
        {
            favoriteGlow.Visible = !favoritesOnly
                                   && PowerGiverStateService.IsFavorite(PowerKey(power.Id));
        }
    }

    private static int GetLiveAmount(Creature target, ModelId powerId) =>
        target.Powers
            .Where(power => power.Id == powerId
                            || string.Equals(
                                power.Id.ToString(),
                                powerId.ToString(),
                                StringComparison.Ordinal))
            .Sum(power => power.DisplayAmount);

    private static string PowerKey(ModelId powerId) => powerId.ToString();

    private static bool SamePowerId(ModelId left, ModelId right) =>
        left == right
        || string.Equals(PowerKey(left), PowerKey(right), StringComparison.Ordinal);

}
