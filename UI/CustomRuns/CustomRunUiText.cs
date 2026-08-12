#nullable enable

namespace Loadout.UI.CustomRuns;

using System;
using Loadout.Services.CustomRuns.Models;
using Loadout.UI.Managers;

public static class CustomRunUiText
{
    public static string DefinitionName(CustomRunDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Name))
            return LocMan.Loc("CUSTOM_RUN_UNNAMED", "Unnamed Custom Run");
        return string.Equals(definition.Name, "New Custom Run", StringComparison.Ordinal)
            ? LocMan.Loc("CUSTOM_RUN_DEFAULT_NAME", "New Custom Run")
            : definition.Name;
    }

    public static string DefaultRoleName(string? name)
    {
        return string.IsNullOrWhiteSpace(name)
               || string.Equals(name, "Default Role", StringComparison.Ordinal)
            ? LocMan.Loc("CUSTOM_RUN_DEFAULT_ROLE_NAME", "Default Role")
            : name;
    }

    public static string RoleName(RoleDefinition role)
    {
        return string.IsNullOrWhiteSpace(role.Name)
               || string.Equals(role.Name, "New Role", StringComparison.Ordinal)
            ? LocMan.Loc("CUSTOM_RUN_NEW_ROLE_NAME", "New Role")
            : role.Name;
    }
}
