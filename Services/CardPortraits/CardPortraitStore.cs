#nullable enable

namespace Loadout.Services.CardPortraits;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Godot;
using Loadout.Services.CardModification;
using Loadout.Services.Saving;
using Loadout.UI.ImageEditing;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;

internal sealed record CardPortraitRecord(
    string PortraitId,
    string CardModelId,
    string File,
    string FrameId,
    int Width,
    int Height,
    bool Animated,
    long? RunStartTime,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record CardPortraitReference(
    string CardInstanceId,
    string PortraitId,
    long RunStartTime,
    string RelativeFile);

internal readonly record struct CardPortraitSaveTarget(
    string PortraitId,
    string Directory,
    string FileName,
    long? RunStartTime,
    int ProfileId,
    PermanentCardCustomizationScope? PermanentScope);

internal readonly record struct CardPortraitAsset(
    CardPortraitRecord Record,
    string GlobalPath);

internal static class CardPortraitStore
{
    private const int CurrentSchemaVersion = 2;
    private const string PermanentDirectory = "loadout/card_portraits/permanent";
    private const string TemporaryDirectory = "loadout/card_portraits/temporary";
    private const string IndexFileName = "index.json";

    private static readonly object Gate = new();
    private static PortraitIndex _permanent = NewIndex();
    private static readonly Dictionary<long, PortraitIndex> TemporaryByRun = new();
    private static readonly Dictionary<ModelId, CardPortraitAsset> PermanentAssets = [];
    private static readonly Dictionary<long, Dictionary<string, CardPortraitAsset>> TemporaryAssetsByRun = new();
    private static bool _registered;
    private static bool _permanentLoaded;
    private static PermanentCardCustomizationScope _loadedPermanentScope;

    public static event Action<ModelId>? PermanentChanged;
    public static event Action<IReadOnlyList<ModelId>>? PermanentReloaded;

    public static bool HasAnyPermanent
    {
        get
        {
            EnsurePermanentLoaded();
            return PermanentAssets.Count > 0;
        }
    }

    public static bool HasPermanent(ModelId cardId)
    {
        return PermanentAssets.ContainsKey(cardId);
    }

    public static CardPortraitSaveTarget CreatePermanentSaveTarget(ModelId cardId)
    {
        EnsurePermanentLoaded();
        string portraitId = Guid.NewGuid().ToString("N");
        return new CardPortraitSaveTarget(
            portraitId,
            GetPermanentImagesDirectory(_loadedPermanentScope),
            $"{SanitizeCardId(cardId)}--{portraitId}.png",
            null,
            GetCurrentProfileId(),
            _loadedPermanentScope);
    }

    public static CardPortraitSaveTarget? CreateTemporarySaveTarget(long? runStartTime)
    {
        if (!runStartTime.HasValue)
            return null;

        string portraitId = Guid.NewGuid().ToString("N");
        return new CardPortraitSaveTarget(
            portraitId,
            GetTemporaryImagesDirectory(runStartTime.Value),
            $"{portraitId}.png",
            runStartTime,
            0,
            null);
    }

    public static bool RegisterPermanent(
        ModelId cardId,
        CardPortraitSaveTarget target,
        ImageEditFrameDefinition frame,
        ImageMediaDocument document,
        string savedPath)
    {
        EnsurePermanentLoaded();
        if (target.PermanentScope != _loadedPermanentScope
            || (_loadedPermanentScope == PermanentCardCustomizationScope.Profile
                && !SaveManager.Instance.IsProfileInitialized)
            || (_loadedPermanentScope == PermanentCardCustomizationScope.Profile
                && target.ProfileId != GetCurrentProfileId())
            || !TryValidateSavedFile(target.Directory, savedPath, out string fileName))
            return false;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        CardPortraitRecord record = new(
            target.PortraitId,
            cardId.ToString(),
            fileName,
            frame.Id,
            document.Width,
            document.Height,
            document.IsAnimated,
            null,
            now,
            now);
        string? previousFile = null;
        lock (Gate)
        {
            _permanent.Assignments ??= new Dictionary<string, CardPortraitRecord>(StringComparer.Ordinal);
            bool hadPrevious = _permanent.Assignments.TryGetValue(
                cardId.ToString(),
                out CardPortraitRecord? previous);
            if (hadPrevious)
                previousFile = previous!.File;
            _permanent.Assignments[cardId.ToString()] = record;
            if (!SavePermanentLocked())
            {
                if (hadPrevious)
                    _permanent.Assignments[cardId.ToString()] = previous!;
                else
                    _permanent.Assignments.Remove(cardId.ToString());
                return false;
            }

            if (TryResolveImagePath(
                    GetPermanentImagesDirectory(_loadedPermanentScope),
                    record.File,
                    out string path))
            {
                PermanentAssets[cardId] = new CardPortraitAsset(record, path);
            }
        }

        if (!string.IsNullOrWhiteSpace(previousFile)
            && !string.Equals(previousFile, fileName, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteImage(GetPermanentImagesDirectory(_loadedPermanentScope), previousFile);
        }
        return true;
    }

    public static bool RegisterTemporary(
        ModelId cardId,
        string cardInstanceId,
        CardPortraitSaveTarget target,
        ImageEditFrameDefinition frame,
        ImageMediaDocument document,
        string savedPath,
        out CardPortraitReference reference)
    {
        reference = null!;
        if (!Guid.TryParseExact(cardInstanceId, "N", out _)
            || !target.RunStartTime.HasValue
            || SaveUtility.GetCurrentRunStartTime() != target.RunStartTime
            || !TryValidateSavedFile(target.Directory, savedPath, out string fileName))
        {
            return false;
        }

        long runStartTime = target.RunStartTime.Value;
        string relativeFile = $"images/{fileName}";
        PortraitIndex index = GetTemporaryIndex(runStartTime);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CardPortraitRecord record = new(
            target.PortraitId,
            cardId.ToString(),
            relativeFile,
            frame.Id,
            document.Width,
            document.Height,
            document.IsAnimated,
            runStartTime,
            now,
            now);
        lock (Gate)
        {
            index.Assignments ??= new Dictionary<string, CardPortraitRecord>(StringComparer.Ordinal);
            index.Assignments[target.PortraitId] = record;
            if (!SaveUtility.TrySaveGlobalJson(GetTemporaryIndexPath(runStartTime), index))
            {
                index.Assignments.Remove(target.PortraitId);
                return false;
            }

            if (TryResolveImagePath(
                    $"user://{GetTemporaryRunDirectory(runStartTime)}",
                    record.File,
                    out string path))
            {
                if (!TemporaryAssetsByRun.TryGetValue(runStartTime, out Dictionary<string, CardPortraitAsset>? assets))
                {
                    assets = new Dictionary<string, CardPortraitAsset>(StringComparer.Ordinal);
                    TemporaryAssetsByRun[runStartTime] = assets;
                }
                assets[record.PortraitId] = new CardPortraitAsset(record, path);
            }
        }

        reference = new CardPortraitReference(cardInstanceId, target.PortraitId, runStartTime, relativeFile);
        return true;
    }

    public static bool TryGetPermanent(ModelId cardId, out CardPortraitAsset asset)
    {
        return PermanentAssets.TryGetValue(cardId, out asset);
    }

    public static bool TryGetTemporary(CardPortraitReference reference, out CardPortraitAsset asset)
    {
        if (TryGetTemporaryCached(reference, out asset))
            return true;

        _ = GetTemporaryIndex(reference.RunStartTime);
        return TryGetTemporaryCached(reference, out asset);
    }

    private static bool TryGetTemporaryCached(CardPortraitReference reference, out CardPortraitAsset asset)
    {
        if (TemporaryAssetsByRun.TryGetValue(
                reference.RunStartTime,
                out Dictionary<string, CardPortraitAsset>? assets)
            && assets.TryGetValue(reference.PortraitId, out asset)
            && asset.Record is { } record
            && record.RunStartTime == reference.RunStartTime
            && string.Equals(record.File, reference.RelativeFile, StringComparison.Ordinal))
        {
            return true;
        }

        asset = default;
        return false;
    }

    public static bool ResetPermanent(ModelId cardId)
    {
        EnsurePermanentLoaded();
        string? previousFile;
        lock (Gate)
        {
            if (_permanent.Assignments is null
                || !_permanent.Assignments.Remove(cardId.ToString(), out CardPortraitRecord? previous))
            {
                return false;
            }

            previousFile = previous.File;
            if (!SavePermanentLocked())
            {
                _permanent.Assignments[cardId.ToString()] = previous;
                return false;
            }
            PermanentAssets.Remove(cardId);
        }

        TryDeleteImage(GetPermanentImagesDirectory(_loadedPermanentScope), previousFile);
        PermanentChanged?.Invoke(cardId);
        return true;
    }

    public static void DeleteTemporaryRun(long runStartTime)
    {
        lock (Gate)
        {
            TemporaryByRun.Remove(runStartTime);
            TemporaryAssetsByRun.Remove(runStartTime);
        }

        try
        {
            string directory = ResolveDirectory($"user://{GetTemporaryRunDirectory(runStartTime)}");
            string root = Path.GetFullPath(ResolveDirectory($"user://{TemporaryDirectory}"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string target = Path.GetFullPath(directory);
            if (target.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(target))
                Directory.Delete(target, recursive: true);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardPortrait: failed deleting temporary run {runStartTime}. {exception.Message}");
        }
    }

    private static void EnsureRegistered()
    {
        if (_registered)
            return;

        _registered = true;
        PermanentCardCustomizationScopeService.EffectiveScopeChanged += OnScopeChanged;
        SaveManager.Instance.ProfileIdChanged += OnProfileChanged;
    }

    public static void Unregister()
    {
        if (!_registered)
            return;

        PermanentCardCustomizationScopeService.EffectiveScopeChanged -= OnScopeChanged;
        SaveManager.Instance.ProfileIdChanged -= OnProfileChanged;
        lock (Gate)
        {
            _permanentLoaded = false;
            _permanent = NewIndex();
            PermanentAssets.Clear();
            TemporaryByRun.Clear();
            TemporaryAssetsByRun.Clear();
        }
        _registered = false;
    }

    private static void EnsurePermanentLoaded()
    {
        EnsureRegistered();
        PermanentCardCustomizationScope scope = PermanentCardCustomizationScopeService.EffectiveScope;
        if (_permanentLoaded && _loadedPermanentScope == scope)
            return;

        lock (Gate)
        {
            if (_permanentLoaded && _loadedPermanentScope == scope)
                return;

            _loadedPermanentScope = scope;
            SaveUtility.LoadResult<PortraitIndex> loaded = scope == PermanentCardCustomizationScope.Global
                ? SaveUtility.LoadGlobalJson(GetPermanentIndexPath(), NewIndex())
                : SaveUtility.LoadProfileJson(GetPermanentIndexPath(), NewIndex());
            _permanent = NormalizeIndex(loaded.Value);
            RebuildPermanentAssetsLocked();
            _permanentLoaded = true;
            if (loaded.Loaded && loaded.Value.SchemaVersion != CurrentSchemaVersion)
                _ = SavePermanentLocked();
        }
    }

    private static PortraitIndex GetTemporaryIndex(long runStartTime)
    {
        EnsureRegistered();
        lock (Gate)
        {
            if (TemporaryByRun.TryGetValue(runStartTime, out PortraitIndex? cached))
                return cached;

            SaveUtility.LoadResult<PortraitIndex> loaded = SaveUtility.LoadGlobalJson(
                GetTemporaryIndexPath(runStartTime),
                NewIndex());
            PortraitIndex index = NormalizeIndex(loaded.Value);
            TemporaryByRun[runStartTime] = index;
            TemporaryAssetsByRun[runStartTime] = BuildAssetMap(
                index,
                $"user://{GetTemporaryRunDirectory(runStartTime)}");
            return index;
        }
    }

    private static bool SavePermanentLocked()
    {
        _permanent.SchemaVersion = CurrentSchemaVersion;
        if (_loadedPermanentScope == PermanentCardCustomizationScope.Global)
            return SaveUtility.TrySaveGlobalJson(GetPermanentIndexPath(), _permanent);
        return SaveUtility.TrySaveProfileJson(GetPermanentIndexPath(), _permanent);
    }

    private static void OnScopeChanged(PermanentCardCustomizationScope _)
    {
        ReloadPermanentAndNotify();
    }

    private static void OnProfileChanged(int _)
    {
        lock (Gate)
        {
            TemporaryByRun.Clear();
            TemporaryAssetsByRun.Clear();
        }
        ReloadPermanentAndNotify();
    }

    private static void ReloadPermanentAndNotify()
    {
        Dictionary<string, CardPortraitRecord> previous;
        lock (Gate)
        {
            previous = _permanent.Assignments is null
                ? new Dictionary<string, CardPortraitRecord>(StringComparer.Ordinal)
                : new Dictionary<string, CardPortraitRecord>(_permanent.Assignments, StringComparer.Ordinal);
            _permanentLoaded = false;
        }

        EnsurePermanentLoaded();
        Dictionary<string, CardPortraitRecord> current = _permanent.Assignments
            ?? new Dictionary<string, CardPortraitRecord>(StringComparer.Ordinal);
        HashSet<string> changedKeys = new(previous.Keys, StringComparer.Ordinal);
        changedKeys.UnionWith(current.Keys);
        List<ModelId> changed = ModelDb.AllCards
            .Where(card => changedKeys.Contains(card.Id.ToString())
                           && (!previous.TryGetValue(card.Id.ToString(), out CardPortraitRecord? oldRecord)
                               || !current.TryGetValue(card.Id.ToString(), out CardPortraitRecord? newRecord)
                               || oldRecord != newRecord))
            .Select(card => card.Id)
            .ToList();
        PermanentReloaded?.Invoke(changed);
    }

    private static PortraitIndex NormalizeIndex(PortraitIndex index)
    {
        index.SchemaVersion = CurrentSchemaVersion;
        index.Assignments = index.Assignments is null
            ? new Dictionary<string, CardPortraitRecord>(StringComparer.Ordinal)
            : index.Assignments
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key)
                               && pair.Value is not null
                               && IsSafeRelativeFile(pair.Value.File)
                               && !string.IsNullOrWhiteSpace(pair.Value.PortraitId))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return index;
    }

    private static PortraitIndex NewIndex()
    {
        return new PortraitIndex
        {
            SchemaVersion = CurrentSchemaVersion,
            Assignments = new Dictionary<string, CardPortraitRecord>(StringComparer.Ordinal)
        };
    }

    private static void RebuildPermanentAssetsLocked()
    {
        PermanentAssets.Clear();
        if (_permanent.Assignments is null)
            return;

        string directory = GetPermanentImagesDirectory(_loadedPermanentScope);
        foreach (CardModel card in ModelDb.AllCards)
        {
            if (_permanent.Assignments.TryGetValue(card.Id.ToString(), out CardPortraitRecord? record)
                && TryResolveImagePath(directory, record.File, out string path))
            {
                PermanentAssets[card.Id] = new CardPortraitAsset(record, path);
            }
        }
    }

    private static Dictionary<string, CardPortraitAsset> BuildAssetMap(
        PortraitIndex index,
        string directory)
    {
        Dictionary<string, CardPortraitAsset> assets = new(StringComparer.Ordinal);
        if (index.Assignments is null)
            return assets;

        foreach ((string key, CardPortraitRecord record) in index.Assignments)
        {
            if (TryResolveImagePath(directory, record.File, out string path))
                assets[key] = new CardPortraitAsset(record, path);
        }
        return assets;
    }

    private static string GetPermanentIndexPath() => $"{PermanentDirectory}/{IndexFileName}";

    private static string GetPermanentImagesDirectory(PermanentCardCustomizationScope scope)
    {
        string relative = $"{PermanentDirectory}/images";
        return scope == PermanentCardCustomizationScope.Global
            ? $"user://{relative}"
            : SaveUtility.GetProfileScopedPath(relative);
    }

    private static string GetTemporaryRunDirectory(long runStartTime) => $"{TemporaryDirectory}/{runStartTime}";

    private static string GetTemporaryIndexPath(long runStartTime) => $"{GetTemporaryRunDirectory(runStartTime)}/{IndexFileName}";

    private static string GetTemporaryImagesDirectory(long runStartTime) =>
        $"user://{GetTemporaryRunDirectory(runStartTime)}/images";

    private static int GetCurrentProfileId() =>
        SaveManager.Instance.IsProfileInitialized ? SaveManager.Instance.CurrentProfileId : 0;

    private static string SanitizeCardId(ModelId cardId)
    {
        char[] characters = cardId.ToString().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '-')
            .ToArray();
        string value = new(characters);
        return string.IsNullOrWhiteSpace(value) ? "card" : value;
    }

    private static bool TryValidateSavedFile(string directory, string savedPath, out string fileName)
    {
        fileName = Path.GetFileName(savedPath);
        if (!IsSafeRelativeFile(fileName))
            return false;

        string expectedRoot = Path.GetFullPath(ResolveDirectory(directory))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string actual = Path.GetFullPath(savedPath);
        return actual.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(actual);
    }

    private static bool TryResolveImagePath(string directory, string fileName, out string path)
    {
        path = string.Empty;
        if (!IsSafeRelativeFile(fileName))
            return false;

        string root = Path.GetFullPath(ResolveDirectory(directory))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(root, fileName));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return false;

        path = candidate;
        return true;
    }

    private static string ResolveDirectory(string directory) =>
        directory.StartsWith("user://", StringComparison.OrdinalIgnoreCase)
            ? ProjectSettings.GlobalizePath(directory)
            : directory;

    private static bool IsSafeFileName(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)
        && (fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(ImageAnimationPackage.Extension, StringComparison.OrdinalIgnoreCase));

    private static bool IsSafeRelativeFile(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.IsPathFullyQualified(fileName))
            return false;

        string normalized = fileName.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.Contains("://", StringComparison.Ordinal)
            || normalized.Split('/').Any(part => part is "" or "." or ".."))
        {
            return false;
        }

        return normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(ImageAnimationPackage.Extension, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteImage(string directory, string? fileName)
    {
        try
        {
            if (fileName is not null && TryResolveImagePath(directory, fileName, out string path))
                File.Delete(path);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardPortrait: failed deleting '{fileName}'. {exception.Message}");
        }
    }

    private sealed class PortraitIndex : ISerializable
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [JsonPropertyName("assignments")]
        public Dictionary<string, CardPortraitRecord>? Assignments { get; set; } =
            new(StringComparer.Ordinal);

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue(nameof(SchemaVersion), SchemaVersion);
            info.AddValue(nameof(Assignments), Assignments);
        }
    }
}
