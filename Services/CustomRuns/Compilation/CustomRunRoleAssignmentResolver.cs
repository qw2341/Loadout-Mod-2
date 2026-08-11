#nullable enable

namespace Loadout.Services.CustomRuns.Compilation;

using System;
using System.Collections.Generic;
using System.Linq;
using Loadout.Services.CustomRuns.Models;

public sealed record CustomRunRoleResolutionResult(
    IReadOnlyDictionary<ulong, string?> Assignments,
    string Error)
{
    public bool IsValid => string.IsNullOrEmpty(Error);
}

public static class CustomRunRoleAssignmentResolver
{
    public static CustomRunRoleResolutionResult Resolve(
        CustomRunDefinition definition,
        IEnumerable<ulong> rosterPlayerIds,
        IReadOnlyDictionary<ulong, string?> authoredAssignments,
        string seed)
    {
        ulong[] roster = rosterPlayerIds.Distinct().OrderBy(id => id).ToArray();
        Dictionary<string, RoleDefinition> roles = definition.Roles
            .ToDictionary(role => role.Id, StringComparer.Ordinal);
        if (roles.Count == 0)
        {
            return new CustomRunRoleResolutionResult(
                roster.ToDictionary(id => id, _ => (string?)null),
                string.Empty);
        }

        return definition.RoleAssignmentMode == RoleAssignmentMode.Random
            ? ResolveRandom(roles.Values, roster, seed)
            : ResolveManual(roles, roster, authoredAssignments);
    }

    private static CustomRunRoleResolutionResult ResolveManual(
        IReadOnlyDictionary<string, RoleDefinition> roles,
        IReadOnlyList<ulong> roster,
        IReadOnlyDictionary<ulong, string?> authoredAssignments)
    {
        HashSet<ulong> rosterIds = roster.ToHashSet();
        if (authoredAssignments.Keys.Any(playerId => !rosterIds.Contains(playerId)))
            return Invalid("Role assignments contain a player who is not in the lobby.");

        Dictionary<ulong, string?> resolved = roster.ToDictionary(playerId => playerId, _ => (string?)null);
        foreach ((ulong playerId, string? roleId) in authoredAssignments)
        {
            if (string.IsNullOrWhiteSpace(roleId))
                continue;
            if (!roles.ContainsKey(roleId))
                return Invalid($"Player {playerId} references a missing role.");
            resolved[playerId] = roleId;
        }

        foreach (RoleDefinition role in roles.Values)
        {
            int count = resolved.Values.Count(roleId => string.Equals(roleId, role.Id, StringComparison.Ordinal));
            if (count > role.MaximumPlayers)
                return Invalid($"Role '{role.Name}' exceeds its maximum of {role.MaximumPlayers}.");
            if (count < role.MinimumPlayers)
                return Invalid($"Role '{role.Name}' requires at least {role.MinimumPlayers} player(s).");
        }

        return new CustomRunRoleResolutionResult(resolved, string.Empty);
    }

    private static CustomRunRoleResolutionResult ResolveRandom(
        IEnumerable<RoleDefinition> roleValues,
        IReadOnlyList<ulong> roster,
        string seed)
    {
        RoleDefinition[] roles = roleValues.OrderBy(role => role.Id, StringComparer.Ordinal).ToArray();
        int minimumSlots = roles.Sum(role => role.MinimumPlayers);
        int maximumSlots = roles.Sum(role => role.MaximumPlayers);
        if (minimumSlots > roster.Count)
            return Invalid($"Role minimums require {minimumSlots} players, but the lobby has {roster.Count}.");
        if (maximumSlots < roster.Count)
            return Invalid($"Role capacity is {maximumSlots}, but the lobby has {roster.Count} players.");

        IReadOnlyList<ulong> players = CustomRunRngService.OrderDeterministically(
            roster,
            seed,
            "role_players",
            id => id.ToString());
        List<(string RoleId, int Slot)> requiredSlots = roles
            .SelectMany(role => Enumerable.Range(0, role.MinimumPlayers).Select(slot => (role.Id, slot)))
            .ToList();
        IReadOnlyList<(string RoleId, int Slot)> orderedRequired = CustomRunRngService.OrderDeterministically(
            requiredSlots,
            seed,
            "role_required_slots",
            slot => $"{slot.RoleId}:{slot.Slot}");

        Dictionary<ulong, string?> resolved = [];
        int playerIndex = 0;
        foreach ((string roleId, _) in orderedRequired)
            resolved[players[playerIndex++]] = roleId;

        List<(string RoleId, int Slot)> optionalSlots = roles
            .SelectMany(role => Enumerable.Range(
                    role.MinimumPlayers,
                    role.MaximumPlayers - role.MinimumPlayers)
                .Select(slot => (role.Id, slot)))
            .ToList();
        IReadOnlyList<(string RoleId, int Slot)> orderedOptional = CustomRunRngService.OrderDeterministically(
            optionalSlots,
            seed,
            "role_optional_slots",
            slot => $"{slot.RoleId}:{slot.Slot}");
        int slotIndex = 0;
        while (playerIndex < players.Count)
            resolved[players[playerIndex++]] = orderedOptional[slotIndex++].RoleId;

        return new CustomRunRoleResolutionResult(resolved, string.Empty);
    }

    private static CustomRunRoleResolutionResult Invalid(string error)
    {
        return new CustomRunRoleResolutionResult(new Dictionary<ulong, string?>(), error);
    }
}
