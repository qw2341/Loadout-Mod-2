#nullable enable

namespace Loadout.Services.CustomRuns.Persistence;

public static class CustomRunMigrationService
{
    public static bool TryMigrate(int schemaVersion, string json, out string migratedJson, out string error)
    {
        migratedJson = json;
        error = string.Empty;

        if (schemaVersion == CustomRunStorageService.CurrentSchemaVersion)
            return true;

        error = schemaVersion > CustomRunStorageService.CurrentSchemaVersion
            ? $"Custom Run schema {schemaVersion} is newer than this version supports."
            : $"Custom Run schema {schemaVersion} is not supported.";
        return false;
    }
}
