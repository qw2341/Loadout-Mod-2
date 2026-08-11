#nullable enable

namespace Loadout.Services.CustomRuns.Persistence;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Loadout.Services.CustomRuns.Models;

public static class CustomRunMigrationService
{
    public static bool TryMigrate(int schemaVersion, string json, out string migratedJson, out string error)
    {
        migratedJson = json;
        error = string.Empty;

        if (schemaVersion == CustomRunStorageService.CurrentSchemaVersion)
            return true;

        if (schemaVersion == 1)
        {
            try
            {
                JsonObject root = JsonNode.Parse(json)?.AsObject()
                                  ?? throw new JsonException("Custom Run root is missing.");
                List<RoleAssignmentMode> legacyModes = [];
                if (root["roles"] is JsonArray roles)
                {
                    foreach (JsonNode? node in roles)
                    {
                        if (node is not JsonObject role)
                            continue;
                        if (role["assignmentMode"]?.GetValue<int>() is int value
                            && Enum.IsDefined((RoleAssignmentMode)value))
                        {
                            legacyModes.Add((RoleAssignmentMode)value);
                        }
                        role.Remove("assignmentMode");
                    }
                }

                RoleAssignmentMode mode = legacyModes.Distinct().Count() == 1
                    ? legacyModes[0]
                    : RoleAssignmentMode.PlayersChoose;
                root["roleAssignmentMode"] = (int)mode;
                root["schemaVersion"] = CustomRunStorageService.CurrentSchemaVersion;
                migratedJson = root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
                return true;
            }
            catch (Exception exception)
            {
                error = $"Could not migrate Custom Run schema 1. {exception.Message}";
                return false;
            }
        }

        error = schemaVersion > CustomRunStorageService.CurrentSchemaVersion
            ? $"Custom Run schema {schemaVersion} is newer than this version supports."
            : $"Custom Run schema {schemaVersion} is not supported.";
        return false;
    }
}
