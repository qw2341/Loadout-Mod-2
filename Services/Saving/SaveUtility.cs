#nullable enable

using System.Runtime.Serialization;

namespace Loadout.Services.Saving;

using Godot;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

public static class SaveUtility
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public readonly record struct LoadResult<T>(T Value, string? SourceRelativePath, bool Loaded)
    {
        public bool LoadedFrom(string relativePath)
        {
            return Loaded && string.Equals(SourceRelativePath, relativePath, StringComparison.Ordinal);
        }
    }

    public static LoadResult<T> LoadProfileJson<T>(
        string relativePath,
        T fallback,
        IEnumerable<string>? fallbackRelativePaths = null)
    {
        if (TryLoadProfileJson(relativePath, out T? value))
            return new LoadResult<T>(value!, relativePath, Loaded: true);

        if (fallbackRelativePaths is not null)
        {
            foreach (string fallbackRelativePath in fallbackRelativePaths)
            {
                if (TryLoadProfileJson(fallbackRelativePath, out value))
                    return new LoadResult<T>(value!, fallbackRelativePath, Loaded: true);
            }
        }

        return new LoadResult<T>(fallback, null, Loaded: false);
    }

    public static LoadResult<T> LoadGlobalJson<T>(string relativePath, T fallback)
    {
        if (TryLoadJson(GetGlobalPath(relativePath), relativePath, out T? value))
            return new LoadResult<T>(value!, relativePath, Loaded: true);

        return new LoadResult<T>(fallback, null, Loaded: false);
    }

    public static void SaveProfileJson<T>(string relativePath, T data) where T:ISerializable
    {
        _ = TrySaveProfileJson(relativePath, data);
    }

    public static bool TrySaveProfileJson<T>(string relativePath, T data) where T : ISerializable
    {
        try
        {
            if (!TryGetProfileScopedPath(relativePath, out string? profilePath))
            {
                GD.PushWarning($"Loadout: profile is not initialized; skipped saving profile JSON '{relativePath}'.");
                return false;
            }

            string globalPath = ProjectSettings.GlobalizePath(profilePath);
            string? directory = Path.GetDirectoryName(globalPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string json = JsonSerializer.Serialize(data, JsonOptions);
            string tempPath = $"{globalPath}.tmp";
            try
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, globalPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            return true;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Loadout: failed to save profile JSON '{relativePath}'. {exception.Message}");
            return false;
        }
    }

    public static void SaveGlobalJson<T>(string relativePath, T data) where T : ISerializable
    {
        _ = TrySaveGlobalJson(relativePath, data);
    }

    public static bool TrySaveGlobalJson<T>(string relativePath, T data) where T : ISerializable
    {
        return SaveJson(GetGlobalPath(relativePath), relativePath, data);
    }

    public static bool TryGetProfileScopedPath(string relativePath, out string? profilePath)
    {
        try
        {
            if (SaveManager.Instance.IsProfileInitialized)
            {
                profilePath = SaveManager.Instance.GetProfileScopedPath(relativePath);
                return true;
            }
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Loadout: could not resolve profile data path for '{relativePath}'. {exception.Message}");
        }

        profilePath = null;
        return false;
    }

    public static string GetProfileScopedPath(string relativePath)
    {
        try
        {
            if (SaveManager.Instance.IsProfileInitialized)
                return SaveManager.Instance.GetProfileScopedPath(relativePath);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Loadout: falling back to user data path for '{relativePath}'. {exception.Message}");
        }

        return $"user://{relativePath}";
    }

    public static long? GetCurrentRunStartTime()
    {
        try
        {
            if (!RunManager.Instance.IsInProgress || RunManager.Instance.DebugOnlyGetState() is null)
                return null;

            return RunManager.Instance.ToSave(null).StartTime;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string GetRunSidecarPath(string directory, string filePrefix, long runStartTime)
    {
        return $"{directory}/{filePrefix}_{runStartTime}.json";
    }

    private static bool TryLoadProfileJson<T>(string relativePath, out T? value)
    {
        try
        {
            return TryLoadJson(
                ProjectSettings.GlobalizePath(GetProfileScopedPath(relativePath)),
                relativePath,
                out value);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Loadout: failed to load profile JSON '{relativePath}'. {exception.Message}");
            value = default;
            return false;
        }
    }

    private static string GetGlobalPath(string relativePath)
    {
        return ProjectSettings.GlobalizePath($"user://{relativePath}");
    }

    private static bool TryLoadJson<T>(string globalPath, string displayPath, out T? value)
    {
        try
        {
            if (!File.Exists(globalPath))
            {
                value = default;
                return false;
            }

            string json = File.ReadAllText(globalPath);
            value = JsonSerializer.Deserialize<T>(json, JsonOptions);
            return value is not null;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Loadout: failed to load JSON '{displayPath}'. {exception.Message}");
            value = default;
            return false;
        }
    }

    private static bool SaveJson<T>(string globalPath, string displayPath, T data) where T : ISerializable
    {
        try
        {
            string? directory = Path.GetDirectoryName(globalPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string json = JsonSerializer.Serialize(data, JsonOptions);
            string tempPath = $"{globalPath}.tmp";
            try
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, globalPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            return true;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Loadout: failed to save JSON '{displayPath}'. {exception.Message}");
            return false;
        }
    }
}
