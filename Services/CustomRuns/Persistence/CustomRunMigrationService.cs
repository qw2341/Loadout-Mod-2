#nullable enable

namespace Loadout.Services.CustomRuns.Persistence;

using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

public static class CustomRunMigrationService
{
    public const int MinimumSupportedSchemaVersion = 2;

    public static bool TryMigrate(int schemaVersion, string json, out string migratedJson, out string error)
    {
        migratedJson = json;
        error = string.Empty;

        if (schemaVersion == CustomRunStorageService.CurrentSchemaVersion)
            return true;

        if (schemaVersion < MinimumSupportedSchemaVersion)
        {
            error = $"Custom Run schema {schemaVersion} is not supported.";
            return false;
        }

        try
        {
            JsonNode? root = JsonNode.Parse(json);
            if (root is not JsonObject definition)
            {
                error = "Custom Run payload is not an object.";
                return false;
            }
            if (schemaVersion < 4 && definition["rules"] is JsonArray rules)
            {
                foreach (JsonObject rule in rules.OfType<JsonObject>())
                {
                    RewriteComponentQuantity(rule["trigger"] as JsonObject);
                    RewriteConditionGroupQuantities(rule["conditions"] as JsonObject);
                    if (rule["actions"] is JsonArray actions)
                    {
                        foreach (JsonObject action in actions.OfType<JsonObject>())
                            RewriteComponentQuantity(action);
                    }
                    if (rule["limit"] is JsonObject limit)
                    {
                        RewriteNumericProperty(limit, "count");
                        RewriteConditionGroupQuantities(limit["untilConditions"] as JsonObject);
                    }
                }
            }
            definition["schemaVersion"] = CustomRunStorageService.CurrentSchemaVersion;
            migratedJson = definition.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            return true;
        }
        catch (JsonException exception)
        {
            error = $"Could not migrate Custom Run schema {schemaVersion}. {exception.Message}";
            return false;
        }
    }

    private static void RewriteConditionGroupQuantities(JsonObject? group)
    {
        if (group is null)
            return;
        if (group["conditions"] is JsonArray conditions)
        {
            foreach (JsonObject condition in conditions.OfType<JsonObject>())
                RewriteComponentQuantity(condition);
        }
        if (group["groups"] is JsonArray groups)
        {
            foreach (JsonObject child in groups.OfType<JsonObject>())
                RewriteConditionGroupQuantities(child);
        }
    }

    private static void RewriteComponentQuantity(JsonObject? component)
    {
        if (component?["parameters"] is not JsonObject parameters)
            return;
        RewriteNumericProperty(parameters, "count");
        RewriteNumericProperty(parameters, "minimumMatches");
    }

    private static void RewriteNumericProperty(JsonObject owner, string key)
    {
        if (owner[key] is not JsonValue value || !value.TryGetValue(out double number))
            return;
        owner[key] = new JsonObject
        {
            ["source"] = 0,
            ["constant"] = number,
            ["constantKind"] = 0
        };
    }
}
