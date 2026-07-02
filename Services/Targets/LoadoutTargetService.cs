#nullable enable

namespace Loadout.Services.Targets;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Loadout.UI.Managers;
using Loadout.UI.Screens;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;

public enum LoadoutTargetScope
{
    Player,
    AllPlayers,
    AllMonsters
}

public enum LoadoutTargetMode
{
    AllPlayersAndPlayers,
    PlayersOnly,
    PowerGiver
}

public readonly record struct LoadoutTargetSelection(LoadoutTargetScope Scope, ulong? PlayerNetId = null)
{
    public static LoadoutTargetSelection ForPlayer(ulong netId)
    {
        return new LoadoutTargetSelection(LoadoutTargetScope.Player, netId);
    }

    public string ToOptionId()
    {
        return Scope switch
        {
            LoadoutTargetScope.AllPlayers => "all_players",
            LoadoutTargetScope.AllMonsters => "all_monsters",
            _ => PlayerNetId.HasValue ? $"player:{PlayerNetId.Value}" : "player:"
        };
    }

    public static bool TryParseOptionId(string optionId, out LoadoutTargetSelection selection)
    {
        if (string.Equals(optionId, "all_players", StringComparison.Ordinal))
        {
            selection = new LoadoutTargetSelection(LoadoutTargetScope.AllPlayers);
            return true;
        }

        if (string.Equals(optionId, "all_monsters", StringComparison.Ordinal))
        {
            selection = new LoadoutTargetSelection(LoadoutTargetScope.AllMonsters);
            return true;
        }

        const string playerPrefix = "player:";
        if (optionId.StartsWith(playerPrefix, StringComparison.Ordinal)
            && ulong.TryParse(optionId[playerPrefix.Length..], out ulong netId))
        {
            selection = ForPlayer(netId);
            return true;
        }

        selection = default;
        return false;
    }
}

public sealed class LoadoutOwnedItem<TModel>
    where TModel : AbstractModel
{
    public LoadoutOwnedItem(Player owner, int index, TModel model)
    {
        Owner = owner;
        Index = index;
        Model = model;
    }

    public Player Owner { get; }
    public ulong OwnerNetId => Owner.NetId;
    public int Index { get; }
    public TModel Model { get; }
    public ModelId ModelId => Model.Id;
}

public static class LoadoutTargetService
{
    private static readonly Dictionary<string, LoadoutTargetSelection> SelectedTargets = new(StringComparer.Ordinal);

    public static LoadoutTargetSelection GetSelected(string key, LoadoutTargetMode mode)
    {
        RunState? runState = GetRunState();
        if (SelectedTargets.TryGetValue(key, out LoadoutTargetSelection selected)
            && IsSelectionAllowed(selected, mode, runState))
        {
            return selected;
        }

        LoadoutTargetSelection fallback = GetDefaultSelection(runState);
        SelectedTargets[key] = fallback;
        return fallback;
    }

    public static void SetSelected(string key, LoadoutTargetSelection selection, LoadoutTargetMode mode)
    {
        RunState? runState = GetRunState();
        SelectedTargets[key] = IsSelectionAllowed(selection, mode, runState)
            ? selection
            : GetDefaultSelection(runState);
    }

    public static NLoadoutDropdown? UpsertTargetDropdown(
        NGenericSelectScreen screen,
        string name,
        string key,
        LoadoutTargetMode mode,
        Action? onChanged = null)
    {
        NLoadoutDropdown? dropdown = screen.GetNodeOrNull<NLoadoutDropdown>(
            $"Sidebar/MarginContainer/TopVBox/CustomControls/{name}");

        if (!ShouldShowDropdown(mode))
        {
            if (dropdown is not null)
                dropdown.Visible = false;

            SetSelected(key, GetDefaultSelection(GetRunState()), mode);
            return dropdown;
        }

        if (dropdown is null)
        {
            dropdown = new NLoadoutDropdown
            {
                Name = name,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(256f, 52f)
            };
            dropdown.SelectedItemChanged += selectedId =>
            {
                if (!LoadoutTargetSelection.TryParseOptionId(selectedId, out LoadoutTargetSelection selection))
                    return;

                SetSelected(key, selection, mode);
                onChanged?.Invoke();
            };
            screen.AddCustomSidebarControl(dropdown);
        }

        IReadOnlyList<LoadoutDropdownOption> options = GetDropdownOptions(mode);
        LoadoutTargetSelection selected = GetSelected(key, mode);
        dropdown.Visible = true;
        dropdown.SetItems(
            LocMan.Loc("LOADOUT_TARGET", "Target"),
            options,
            selected.ToOptionId());
        return dropdown;
    }

    public static bool ShouldShowDropdown(LoadoutTargetMode mode)
    {
        RunState? runState = GetRunState();
        return runState is not null
               && !RunManager.Instance.IsSingleplayerOrFakeMultiplayer
               && runState.Players.Count > 1
               && GetDropdownOptions(mode).Count > 1;
    }

    public static IReadOnlyList<Player> ResolvePlayers(LoadoutTargetSelection selection, IRunState runState)
    {
        return selection.Scope switch
        {
            LoadoutTargetScope.AllPlayers => runState.Players.ToList(),
            LoadoutTargetScope.Player when selection.PlayerNetId.HasValue =>
                runState.GetPlayer(selection.PlayerNetId.Value) is { } player ? [player] : [],
            _ => []
        };
    }

    public static IReadOnlyList<LoadoutOwnedItem<TModel>> BuildOwnedItems<TModel>(
        LoadoutTargetSelection selection,
        Func<Player, IReadOnlyList<TModel>> getItems)
        where TModel : AbstractModel
    {
        RunState? runState = GetRunState();
        if (runState is null)
            return [];

        return ResolvePlayers(selection, runState)
            .SelectMany(player => getItems(player).Select((model, index) => new LoadoutOwnedItem<TModel>(player, index, model)))
            .ToList();
    }

    public static string FormatPlayerName(Player player)
    {
        try
        {
            if (!RunManager.Instance.IsSingleplayerOrFakeMultiplayer)
            {
                string rawName = PlatformUtil.GetPlayerNameRaw(RunManager.Instance.NetService.Platform, player.NetId);
                if (!string.IsNullOrWhiteSpace(rawName))
                    return rawName;
            }
        }
        catch
        {
            // Fall back below.
        }

        try
        {
            string characterName = player.Character.Title.GetFormattedText();
            if (!string.IsNullOrWhiteSpace(characterName))
                return characterName;
        }
        catch
        {
            // Fall back below.
        }

        return LocMan.Loc("LOADOUT_TARGET_PLAYER_FALLBACK", "Player {0}", player.NetId);
    }

    private static IReadOnlyList<LoadoutDropdownOption> GetDropdownOptions(LoadoutTargetMode mode)
    {
        RunState? runState = GetRunState();
        if (runState is null)
            return [];

        List<LoadoutDropdownOption> options = new();
        if (mode is LoadoutTargetMode.AllPlayersAndPlayers or LoadoutTargetMode.PowerGiver)
        {
            options.Add(new LoadoutDropdownOption(
                new LoadoutTargetSelection(LoadoutTargetScope.AllPlayers).ToOptionId(),
                LocMan.Loc("LOADOUT_TARGET_ALL_PLAYERS", "All Players")));
        }

        foreach (Player player in runState.Players)
        {
            options.Add(new LoadoutDropdownOption(
                LoadoutTargetSelection.ForPlayer(player.NetId).ToOptionId(),
                FormatPlayerName(player)));
        }

        if (mode == LoadoutTargetMode.PowerGiver)
        {
            options.Add(new LoadoutDropdownOption(
                new LoadoutTargetSelection(LoadoutTargetScope.AllMonsters).ToOptionId(),
                LocMan.Loc("LOADOUT_TARGET_ALL_MONSTERS", "All Monsters")));
        }

        return options;
    }

    private static bool IsSelectionAllowed(LoadoutTargetSelection selection, LoadoutTargetMode mode, RunState? runState)
    {
        if (runState is null)
            return false;

        if (selection.Scope == LoadoutTargetScope.AllMonsters)
            return mode == LoadoutTargetMode.PowerGiver && ShouldShowDropdown(mode);

        if (selection.Scope == LoadoutTargetScope.AllPlayers)
            return mode != LoadoutTargetMode.PlayersOnly && ShouldShowDropdown(mode);

        return selection.Scope == LoadoutTargetScope.Player
               && selection.PlayerNetId.HasValue
               && runState.GetPlayer(selection.PlayerNetId.Value) is not null;
    }

    private static LoadoutTargetSelection GetDefaultSelection(RunState? runState)
    {
        Player? localPlayer = null;
        try
        {
            localPlayer = runState is null ? null : LocalContext.GetMe(runState);
        }
        catch
        {
            // Fall back below.
        }

        localPlayer ??= runState?.Players.FirstOrDefault();
        return localPlayer is null ? default : LoadoutTargetSelection.ForPlayer(localPlayer.NetId);
    }

    private static RunState? GetRunState()
    {
        try
        {
            return RunManager.Instance.IsInProgress
                ? RunManager.Instance.DebugOnlyGetState()
                : null;
        }
        catch
        {
            return null;
        }
    }
}
