#nullable enable

namespace Loadout.Companions;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using Loadout.UI.ImageEditing;

public static class CustomCompanionStore
{
    public const int CurrentVersion = 1;
    public const string DirectoryPath = "user://loadout/custom_companions";
    public const string ManifestFileName = "manifest.json";
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static Dictionary<string, CustomCompanionRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    public static string GlobalDirectoryPath => Path.GetFullPath(ProjectSettings.GlobalizePath(DirectoryPath));

    public static IReadOnlyList<CustomLoadoutCompanion> LoadCompanions()
    {
        EnsureLoaded();
        List<CustomLoadoutCompanion> companions = [];
        foreach (CustomCompanionRecord record in _records.Values
            .OrderBy(record => record.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.Id, StringComparer.Ordinal))
        {
            try
            {
                companions.Add(CreateCompanion(record));
            }
            catch (Exception exception)
            {
                GD.PushWarning($"Loadout: skipped custom companion '{record.Id}' because its runtime model could not be created. {exception.Message}");
            }
        }

        return companions;
    }

    public static bool TryAdd(
        string companionId,
        string displayName,
        string imageFileName,
        out CustomLoadoutCompanion? companion,
        out string? error)
    {
        EnsureLoaded();
        companion = null;
        error = ValidateRecord(companionId, displayName, imageFileName, requireImage: true);
        if (error is not null)
            return false;
        if (_records.ContainsKey(companionId))
        {
            error = "A custom companion with this id already exists.";
            return false;
        }

        CustomCompanionRecord record = new(companionId.Trim(), displayName.Trim(), Path.GetFileName(imageFileName));
        Dictionary<string, CustomCompanionRecord> next = new(_records, StringComparer.OrdinalIgnoreCase)
        {
            [record.Id] = record
        };

        try
        {
            CustomLoadoutCompanion createdCompanion = CreateCompanion(record);
            SaveManifest(next.Values);
            _records = next;
            companion = createdCompanion;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            GD.PushError($"Loadout: failed to save custom companion '{companionId}'. {exception}");
            return false;
        }
    }

    public static bool TryRemove(string companionId, out string? warningOrError)
    {
        EnsureLoaded();
        warningOrError = null;
        if (!_records.TryGetValue(companionId, out CustomCompanionRecord? record))
        {
            warningOrError = "The selected custom companion no longer exists.";
            return false;
        }

        Dictionary<string, CustomCompanionRecord> next = new(_records, StringComparer.OrdinalIgnoreCase);
        next.Remove(companionId);
        try
        {
            SaveManifest(next.Values);
            _records = next;
        }
        catch (Exception exception)
        {
            warningOrError = exception.Message;
            GD.PushError($"Loadout: failed to remove custom companion '{companionId}' from the manifest. {exception}");
            return false;
        }

        string imagePath = GetGlobalImagePath(record.File);
        try
        {
            if (File.Exists(imagePath))
                File.Delete(imagePath);
        }
        catch (Exception exception)
        {
            warningOrError = exception.Message;
            GD.PushWarning($"Loadout: removed custom companion '{companionId}', but its image could not be deleted. {exception.Message}");
        }

        return true;
    }

    public static bool TryUpdate(
        string companionId,
        string displayName,
        string imageFileName,
        out CustomLoadoutCompanion? companion,
        out string? error)
    {
        EnsureLoaded();
        companion = null;
        error = ValidateRecord(companionId, displayName, imageFileName, requireImage: true);
        if (error is not null)
            return false;
        if (!_records.TryGetValue(companionId, out CustomCompanionRecord? existing))
        {
            error = "The selected custom companion no longer exists.";
            return false;
        }

        CustomCompanionRecord updated = new(
            companionId.Trim(),
            displayName.Trim(),
            Path.GetFileName(imageFileName));
        Dictionary<string, CustomCompanionRecord> next = new(_records, StringComparer.OrdinalIgnoreCase)
        {
            [updated.Id] = updated
        };
        try
        {
            CustomLoadoutCompanion updatedCompanion = CreateCompanion(updated);
            SaveManifest(next.Values);
            _records = next;
            companion = updatedCompanion;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            GD.PushError($"Loadout: failed to update custom companion '{companionId}'. {exception}");
            return false;
        }

        if (!string.Equals(existing.File, updated.File, PathComparison))
        {
            try
            {
                string oldPath = GetGlobalImagePath(existing.File);
                if (File.Exists(oldPath))
                    File.Delete(oldPath);
            }
            catch (Exception exception)
            {
                GD.PushWarning($"Loadout: updated custom companion '{companionId}', but its previous image could not be deleted. {exception.Message}");
            }
        }
        return true;
    }

    public static string GetImageResourcePath(string imageFileName)
    {
        return $"{DirectoryPath}/{Path.GetFileName(imageFileName)}";
    }

    private static void EnsureLoaded()
    {
        if (_loaded)
            return;

        _loaded = true;
        _records = new Dictionary<string, CustomCompanionRecord>(StringComparer.OrdinalIgnoreCase);
        string manifestPath = Path.Combine(GlobalDirectoryPath, ManifestFileName);
        if (!File.Exists(manifestPath))
            return;

        try
        {
            string json = File.ReadAllText(manifestPath);
            CustomCompanionManifest? manifest = JsonSerializer.Deserialize<CustomCompanionManifest>(json, JsonOptions);
            if (manifest is null || manifest.Version != CurrentVersion)
            {
                GD.PushWarning($"Loadout: unsupported custom companion manifest version in '{manifestPath}'.");
                return;
            }

            foreach (CustomCompanionRecord? record in manifest.Companions ?? [])
            {
                if (record is null)
                {
                    GD.PushWarning("Loadout: skipped a null custom companion manifest entry.");
                    continue;
                }

                try
                {
                    string? error = ValidateRecord(record.Id, record.Name, record.File, requireImage: true);
                    if (error is not null)
                    {
                        GD.PushWarning($"Loadout: skipped invalid custom companion '{record.Id}'. {error}");
                        continue;
                    }

                    if (!_records.TryAdd(record.Id, new CustomCompanionRecord(record.Id.Trim(), record.Name.Trim(), Path.GetFileName(record.File))))
                        GD.PushWarning($"Loadout: skipped duplicate custom companion id '{record.Id}'.");
                }
                catch (Exception exception)
                {
                    GD.PushWarning($"Loadout: skipped invalid custom companion '{record.Id}'. {exception.Message}");
                }
            }
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Loadout: failed to load custom companions from '{manifestPath}'. {exception.Message}");
        }
    }

    private static string? ValidateRecord(string? id, string? name, string? file, bool requireImage)
    {
        if (string.IsNullOrWhiteSpace(id) || !id.StartsWith("custom-", StringComparison.OrdinalIgnoreCase))
            return "The companion id is invalid.";
        if (string.IsNullOrWhiteSpace(name))
            return "The companion name is empty.";
        if (name.Trim().Length > 80)
            return "The companion name is too long.";
        if (string.IsNullOrWhiteSpace(file) || !string.Equals(file, Path.GetFileName(file), StringComparison.Ordinal))
            return "The companion image filename is invalid.";
        bool isPng = file.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
        bool isAnimation = file.EndsWith(ImageAnimationPackage.Extension, StringComparison.OrdinalIgnoreCase);
        if (!isPng && !isAnimation)
            return "The companion image must be a PNG or Loadout animation package.";
        string fileStem = Path.GetFileNameWithoutExtension(file);
        if (!Guid.TryParseExact(fileStem, "N", out _)
            || !string.Equals(id.Trim(), $"custom-{fileStem}", StringComparison.OrdinalIgnoreCase))
        {
            return "The companion id and image filename must use the same GUID.";
        }
        if (requireImage)
        {
            string imagePath = GetGlobalImagePath(file);
            if (!File.Exists(imagePath))
                return "The companion image file is missing.";

            try
            {
                ImageMediaMetadata metadata = ImageMediaLoader.ReadMetadata(imagePath);
                if (metadata.FrameCount <= 0)
                    return "The companion image file is corrupt or unsupported.";
                if (metadata.Width != 192 || metadata.Height != 224)
                    return "Every companion animation frame must be 192 by 224 pixels.";
            }
            catch (Exception exception)
            {
                return $"The companion image file could not be read. {exception.Message}";
            }
        }
        return null;
    }

    private static CustomLoadoutCompanion CreateCompanion(CustomCompanionRecord record)
    {
        return CustomLoadoutCompanion.Create(record.Id, record.Name, GetImageResourcePath(record.File));
    }

    private static string GetGlobalImagePath(string file)
    {
        string root = GlobalDirectoryPath
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, Path.GetFileName(file)));
        if (!path.StartsWith(root, PathComparison))
            throw new InvalidOperationException("The companion image path escapes its storage directory.");
        return path;
    }

    private static void SaveManifest(IEnumerable<CustomCompanionRecord> records)
    {
        Directory.CreateDirectory(GlobalDirectoryPath);
        string manifestPath = Path.Combine(GlobalDirectoryPath, ManifestFileName);
        string temporaryPath = Path.Combine(GlobalDirectoryPath, $".{Guid.NewGuid():N}.tmp.json");
        CustomCompanionManifest manifest = new()
        {
            Version = CurrentVersion,
            Companions = records
                .OrderBy(record => record.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(record => record.Id, StringComparer.Ordinal)
                .ToList()
        };

        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(manifest, JsonOptions));
            File.Move(temporaryPath, manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private sealed class CustomCompanionManifest
    {
        public int Version { get; set; }
        public List<CustomCompanionRecord>? Companions { get; set; }
    }

    private sealed record CustomCompanionRecord(string Id, string Name, string File);
}
