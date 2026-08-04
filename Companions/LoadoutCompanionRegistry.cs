#nullable enable

namespace Loadout.Companions;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Godot;
using Loadout.UI.ImageEditing;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

public static class LoadoutCompanionRegistry
{
    public const string NoneId = "none";

    private static readonly Dictionary<string, LoadoutCompanion> CompanionsById = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, CompanionTextureSequence> TextureSequencesById = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> MissingTextures = new(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<LoadoutCompanion> _companions = [];
    private static string _activeCompanionId = NoneId;
    private static bool _initialized;

    public static event Action<string>? ActiveCompanionChanged;
    public static event Action? CompanionsChanged;
    public static event Action<CustomLoadoutCompanion>? CustomCompanionAdded;
    public static event Action<CustomLoadoutCompanion>? CustomCompanionUpdated;
    public static event Action<string>? CustomCompanionRemoved;
    public static event Action<LoadoutCompanionPresentationRequest>? PresentationRequested;

    public static string ActiveCompanionId => _activeCompanionId;
    public static IReadOnlyList<LoadoutCompanion> Companions => _companions;

    public static bool Initialize()
    {
        if (_initialized)
            return true;

        Type[] companionTypes = GetCompanionTypes();
        if (!ModelDb.Contains(typeof(CustomLoadoutCompanion))
            || companionTypes.Length > 0 && companionTypes.Any(type => !ModelDb.Contains(type)))
            return false;

        CompanionsById.Clear();
        foreach (Type type in companionTypes)
        {
            LoadoutCompanion companion = ModelDb.GetById<LoadoutCompanion>(ModelDb.GetId(type));
            TryRegisterCompanion(companion, type.FullName ?? type.Name);
        }

        foreach (CustomLoadoutCompanion companion in CustomCompanionStore.LoadCompanions())
            TryRegisterCompanion(companion, companion.DisplayName);

        RebuildCompanionList();
        _initialized = true;

        string resolvedId = ResolveId(_activeCompanionId);
        if (!string.Equals(resolvedId, _activeCompanionId, StringComparison.OrdinalIgnoreCase))
        {
            _activeCompanionId = resolvedId;
            ActiveCompanionChanged?.Invoke(_activeCompanionId);
        }

        return true;
    }

    public static bool AddCustomCompanion(CustomLoadoutCompanion companion)
    {
        ArgumentNullException.ThrowIfNull(companion);
        if (!Initialize() || !companion.IsCustom)
            return false;

        string id = NormalizeId(companion.CompanionId);
        if (id == NoneId || CompanionsById.ContainsKey(id))
            return false;

        CompanionsById[id] = companion;
        InvalidateTextureCache(id);
        RebuildCompanionList();
        CustomCompanionAdded?.Invoke(companion);
        CompanionsChanged?.Invoke();
        return true;
    }

    public static bool RemoveCustomCompanion(string companionId)
    {
        if (!Initialize())
            return false;

        string id = NormalizeId(companionId);
        if (!CompanionsById.TryGetValue(id, out LoadoutCompanion? companion) || !companion.IsCustom)
            return false;

        if (string.Equals(_activeCompanionId, id, StringComparison.OrdinalIgnoreCase))
            SetActiveCompanion(NoneId);
        CompanionsById.Remove(id);
        InvalidateTextureCache(id);
        RebuildCompanionList();
        CustomCompanionRemoved?.Invoke(id);
        CompanionsChanged?.Invoke();
        return true;
    }

    public static bool UpdateCustomCompanion(CustomLoadoutCompanion companion)
    {
        ArgumentNullException.ThrowIfNull(companion);
        if (!Initialize() || !companion.IsCustom)
            return false;

        string id = NormalizeId(companion.CompanionId);
        if (!CompanionsById.TryGetValue(id, out LoadoutCompanion? existing) || !existing.IsCustom)
            return false;

        CompanionsById[id] = companion;
        InvalidateTextureCache(id);
        RebuildCompanionList();
        CustomCompanionUpdated?.Invoke(companion);
        CompanionsChanged?.Invoke();
        if (string.Equals(_activeCompanionId, id, StringComparison.OrdinalIgnoreCase))
            ActiveCompanionChanged?.Invoke(id);
        return true;
    }

    public static void InvalidateTextureCache(string companionId)
    {
        string id = NormalizeId(companionId);
        TextureSequencesById.Remove(id);
        MissingTextures.Remove(id);
    }

    public static LoadoutCompanion? GetCompanion(string? companionId)
    {
        Initialize();
        return CompanionsById.GetValueOrDefault(NormalizeId(companionId));
    }

    public static void SetActiveCompanion(string? companionId)
    {
        string requestedId = NormalizeId(companionId);
        string resolvedId = _initialized ? ResolveId(requestedId) : requestedId;
        if (string.Equals(resolvedId, _activeCompanionId, StringComparison.OrdinalIgnoreCase))
            return;

        _activeCompanionId = resolvedId;
        ActiveCompanionChanged?.Invoke(_activeCompanionId);
    }

    public static LoadoutCompanion? GetActiveCompanion()
    {
        Initialize();
        return CompanionsById.GetValueOrDefault(_activeCompanionId);
    }

    public static Texture2D? GetTexture(LoadoutCompanion companion)
    {
        return GetTextureSequence(companion)?.Frames[0];
    }

    public static CompanionTextureSequence? GetTextureSequence(LoadoutCompanion companion)
    {
        string id = NormalizeId(companion.CompanionId);
        if (TextureSequencesById.TryGetValue(id, out CompanionTextureSequence? cached))
            return cached;

        if (MissingTextures.Contains(id))
        {
            return null;
        }

        CompanionTextureSequence? sequence = LoadTextureSequence(companion.SpritePath);
        if (sequence is null)
        {
            MissingTextures.Add(id);
            return null;
        }

        if (companion.SpriteRegion is { } region)
        {
            List<Texture2D> regions = new(sequence.Frames.Count);
            foreach (Texture2D texture in sequence.Frames)
                regions.Add(new AtlasTexture { Atlas = texture, Region = region });
            sequence = new CompanionTextureSequence(regions, sequence.Durations);
        }

        TextureSequencesById[id] = sequence;
        return sequence;
    }

    public static bool IsLocalOwner(LoadoutCompanion companion)
    {
        if (companion.OwnerNetId == 0)
            return true;

        try
        {
            if (!RunManager.Instance.IsInProgress || RunManager.Instance.DebugOnlyGetState() is not { } runState)
                return false;

            return LocalContext.GetMe(runState)?.NetId == companion.OwnerNetId;
        }
        catch
        {
            return false;
        }
    }

    internal static void RequestPresentation(LoadoutCompanion companion, string? text, double seconds)
    {
        if (!IsLocalOwner(companion))
            return;

        PresentationRequested?.Invoke(new LoadoutCompanionPresentationRequest(
            companion,
            text,
            Math.Max(0.1, seconds)));
    }

    private static Type[] GetCompanionTypes()
    {
        try
        {
            return typeof(LoadoutCompanion).Assembly
                .GetTypes()
                .Where(type => type != typeof(CustomLoadoutCompanion)
                               && !type.IsAbstract
                               && type.IsSubclassOf(typeof(LoadoutCompanion)))
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToArray();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types
                .Where(type => type is not null
                               && type != typeof(CustomLoadoutCompanion)
                               && !type.IsAbstract
                               && type.IsSubclassOf(typeof(LoadoutCompanion)))
                .Cast<Type>()
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToArray();
        }
    }

    private static string ResolveId(string companionId)
    {
        return companionId == NoneId || CompanionsById.ContainsKey(companionId)
            ? companionId
            : NoneId;
    }

    private static string NormalizeId(string? companionId)
    {
        return string.IsNullOrWhiteSpace(companionId)
            ? NoneId
            : companionId.Trim().ToLowerInvariant();
    }

    private static bool TryRegisterCompanion(LoadoutCompanion companion, string sourceName)
    {
        string id = NormalizeId(companion.CompanionId);
        if (id == NoneId || CompanionsById.ContainsKey(id))
        {
            GD.PushWarning($"Loadout companion '{sourceName}' has an invalid or duplicate id '{companion.CompanionId}'.");
            return false;
        }

        CompanionsById[id] = companion;
        return true;
    }

    private static void RebuildCompanionList()
    {
        _companions = CompanionsById.Values
            .OrderBy(companion => companion.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(companion => companion.CompanionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static CompanionTextureSequence? LoadTextureSequence(string path)
    {
        if (path.StartsWith("res://", StringComparison.OrdinalIgnoreCase) && ResourceLoader.Exists(path))
        {
            Texture2D texture = ResourceLoader.Load<Texture2D>(path, null, ResourceLoader.CacheMode.Reuse);
            return new CompanionTextureSequence([texture], [0.1]);
        }

        try
        {
            string globalPath = ProjectSettings.GlobalizePath(path);
            if (!File.Exists(globalPath))
                return null;

            ImageMediaDocument document = ImageMediaLoader.LoadDocumentFromFile(globalPath);
            List<Texture2D> textures = new(document.Frames.Count);
            List<double> durations = new(document.Frames.Count);
            foreach (ImageMediaFrame mediaFrame in document.Frames)
            {
                mediaFrame.Image.GenerateMipmaps();
                textures.Add(ImageTexture.CreateFromImage(mediaFrame.Image));
                durations.Add(Math.Clamp(mediaFrame.DurationSeconds, 0.02, 10.0));
            }
            return new CompanionTextureSequence(textures, durations);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Loadout: failed to load companion image '{path}'. {exception.Message}");
            return null;
        }
    }
}

public sealed record CompanionTextureSequence(
    IReadOnlyList<Texture2D> Frames,
    IReadOnlyList<double> Durations);
