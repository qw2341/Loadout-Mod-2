#nullable enable

namespace Loadout.Companions;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Loadout.UI.ImageEditing;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Modding;

public static class LoadoutCompanionRegistry
{
    public const string NoneId = "none";

    private static readonly Dictionary<string, LoadoutCompanion> CompanionsById = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, CompanionTextureSequence> TextureSequencesById = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Texture2D> PreviewTexturesById = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> MissingTextures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly AbstractModel[] NoNativeHookListeners = [];

    private static IReadOnlyList<LoadoutCompanion> _companions = [];
    private static AbstractModel[] _runStateHookListeners = NoNativeHookListeners;
    private static AbstractModel[] _combatStateHookListeners = NoNativeHookListeners;
    private static string _activeCompanionId = NoneId;
    private static LoadoutCompanion? _hookedCompanion;
    private static bool _initialized;
    private static bool _customCompanionsLoaded;
    private static bool _nativeHookSubscriptionsRegistered;

    public static event Action<string>? ActiveCompanionChanged;
    public static event Action? CompanionsChanged;
    public static event Action<CustomLoadoutCompanion>? CustomCompanionAdded;
    public static event Action<CustomLoadoutCompanion>? CustomCompanionUpdated;
    public static event Action<string>? CustomCompanionRemoved;
    public static event Action<LoadoutCompanionPresentationRequest>? PresentationRequested;

    public static string ActiveCompanionId => _activeCompanionId;
    public static IReadOnlyList<LoadoutCompanion> Companions
    {
        get
        {
            Initialize();
            EnsureCustomCompanionsLoaded();
            return _companions;
        }
    }

    public static bool Initialize()
    {
        if (_initialized)
            return true;

        CompanionsById.Clear();
        TryRegisterCompanion(GetCompanionModel<XGGGCompanion>(), nameof(XGGGCompanion));
        RebuildCompanionList();
        _initialized = true;

        if (IsCustomId(_activeCompanionId))
            EnsureCustomCompanionsLoaded();
        string resolvedId = ResolveId(_activeCompanionId);
        if (!string.Equals(resolvedId, _activeCompanionId, StringComparison.OrdinalIgnoreCase))
        {
            _activeCompanionId = resolvedId;
            ActiveCompanionChanged?.Invoke(_activeCompanionId);
        }
        RegisterActiveCompanionHooks();

        return true;
    }

    public static bool AddCustomCompanion(CustomLoadoutCompanion companion)
    {
        ArgumentNullException.ThrowIfNull(companion);
        if (!Initialize() || !companion.IsCustom)
            return false;
        EnsureCustomCompanionsLoaded();

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
        EnsureCustomCompanionsLoaded();

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
        EnsureCustomCompanionsLoaded();

        string id = NormalizeId(companion.CompanionId);
        if (!CompanionsById.TryGetValue(id, out LoadoutCompanion? existing) || !existing.IsCustom)
            return false;

        bool isActive = string.Equals(_activeCompanionId, id, StringComparison.OrdinalIgnoreCase);
        if (isActive)
            UnregisterActiveCompanionHooks();
        CompanionsById[id] = companion;
        InvalidateTextureCache(id);
        RebuildCompanionList();
        CustomCompanionUpdated?.Invoke(companion);
        CompanionsChanged?.Invoke();
        if (isActive)
        {
            ActiveCompanionChanged?.Invoke(id);
            RegisterActiveCompanionHooks();
        }
        return true;
    }

    public static void InvalidateTextureCache(string companionId)
    {
        string id = NormalizeId(companionId);
        TextureSequencesById.Remove(id);
        PreviewTexturesById.Remove(id);
        MissingTextures.Remove(id);
    }

    public static LoadoutCompanion? GetCompanion(string? companionId)
    {
        Initialize();
        string id = NormalizeId(companionId);
        if (IsCustomId(id))
            EnsureCustomCompanionsLoaded();
        return CompanionsById.GetValueOrDefault(id);
    }

    public static void SetActiveCompanion(string? companionId)
    {
        string requestedId = NormalizeId(companionId);
        Initialize();
        if (IsCustomId(requestedId))
            EnsureCustomCompanionsLoaded();
        string resolvedId = ResolveId(requestedId);
        if (string.Equals(resolvedId, _activeCompanionId, StringComparison.OrdinalIgnoreCase))
            return;

        string previousId = _activeCompanionId;
        UnregisterActiveCompanionHooks();
        _activeCompanionId = resolvedId;
        if (CompanionsById.GetValueOrDefault(previousId)?.IsCustom == true)
            InvalidateTextureCache(previousId);
        ActiveCompanionChanged?.Invoke(_activeCompanionId);
        RegisterActiveCompanionHooks();
    }

    public static LoadoutCompanion? GetActiveCompanion()
    {
        Initialize();
        if (IsCustomId(_activeCompanionId))
            EnsureCustomCompanionsLoaded();
        return CompanionsById.GetValueOrDefault(_activeCompanionId);
    }

    public static Texture2D? GetTexture(LoadoutCompanion companion)
    {
        string id = NormalizeId(companion.CompanionId);
        if (PreviewTexturesById.TryGetValue(id, out Texture2D? cached))
            return cached;
        if (TextureSequencesById.TryGetValue(id, out CompanionTextureSequence? sequence))
            return sequence.Frames[0];
        if (MissingTextures.Contains(id))
            return null;

        Texture2D? texture = LoadPreviewTexture(companion.SpritePath);
        if (texture is null)
        {
            MissingTextures.Add(id);
            return null;
        }
        if (companion.SpriteRegion is { } region)
            texture = new AtlasTexture { Atlas = texture, Region = region };
        PreviewTexturesById[id] = texture;
        return texture;
    }

    public static Texture2D? GetCachedTexture(LoadoutCompanion companion)
    {
        string id = NormalizeId(companion.CompanionId);
        if (TextureSequencesById.TryGetValue(id, out CompanionTextureSequence? sequence))
            return sequence.Frames[0];
        return PreviewTexturesById.GetValueOrDefault(id);
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
        PreviewTexturesById[id] = sequence.Frames[0];
        return sequence;
    }

    public static bool RegisterCompanion(LoadoutCompanion companion)
    {
        ArgumentNullException.ThrowIfNull(companion);
        Initialize();
        if (!TryRegisterCompanion(companion, companion.GetType().FullName ?? companion.GetType().Name))
            return false;
        RebuildCompanionList();
        CompanionsChanged?.Invoke();
        return true;
    }

    internal static void RequestPresentation(LoadoutCompanion companion, string? text, double seconds)
    {
        ArgumentNullException.ThrowIfNull(companion);
        Initialize();
        if (!double.IsFinite(seconds)
            || !ReferenceEquals(CompanionsById.GetValueOrDefault(_activeCompanionId), companion))
            return;

        PresentationRequested?.Invoke(new LoadoutCompanionPresentationRequest(
            companion,
            text,
            Math.Max(seconds, 0.1)));
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

    private static bool IsCustomId(string companionId)
    {
        return companionId.StartsWith("custom-", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureCustomCompanionsLoaded()
    {
        if (_customCompanionsLoaded)
            return;

        _customCompanionsLoaded = true;
        foreach (CustomLoadoutCompanion companion in CustomCompanionStore.LoadCompanions())
            TryRegisterCompanion(companion, companion.DisplayName);
        RebuildCompanionList();
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

    private static void RegisterActiveCompanionHooks()
    {
        LoadoutCompanion? companion = CompanionsById.GetValueOrDefault(_activeCompanionId);
        if (companion is null || ReferenceEquals(companion, _hookedCompanion))
            return;

        try
        {
            companion.RegisterSelectedHooks();
            SetNativeHookListeners(companion);
            _hookedCompanion = companion;
        }
        catch (Exception exception)
        {
            SetNativeHookListeners(null);
            try
            {
                companion.UnregisterSelectedHooks();
            }
            catch (Exception cleanupException)
            {
                GD.PushError($"Loadout: companion '{companion.CompanionId}' hook cleanup failed. {cleanupException}");
            }
            GD.PushError($"Loadout: companion '{companion.CompanionId}' hook registration failed. {exception}");
        }
    }

    private static void UnregisterActiveCompanionHooks()
    {
        LoadoutCompanion? companion = _hookedCompanion;
        _hookedCompanion = null;
        SetNativeHookListeners(null);
        if (companion is null)
            return;

        try
        {
            companion.UnregisterSelectedHooks();
        }
        catch (Exception exception)
        {
            GD.PushError($"Loadout: companion '{companion.CompanionId}' hook unregistration failed. {exception}");
        }
    }

    private static T GetCompanionModel<T>() where T : LoadoutCompanion, new()
    {
        return ModelDb.Contains(typeof(T))
            ? ModelDb.GetById<T>(ModelDb.GetId<T>())
            : new T();
    }

    private static void SetNativeHookListeners(LoadoutCompanion? companion)
    {
        bool usesRunHooks = companion?.UsesRunStateHooks == true;
        bool usesCombatHooks = companion?.ShouldReceiveCombatHooks == true;
        if ((usesRunHooks || usesCombatHooks) && !_nativeHookSubscriptionsRegistered)
        {
            ModHelper.SubscribeForRunStateHooks(
                "Loadout.Companions.Run",
                _ => _runStateHookListeners);
            ModHelper.SubscribeForCombatStateHooks(
                "Loadout.Companions.Combat",
                _ => _combatStateHookListeners);
            _nativeHookSubscriptionsRegistered = true;
        }

        _runStateHookListeners = usesRunHooks ? [companion!] : NoNativeHookListeners;
        _combatStateHookListeners = usesCombatHooks ? [companion!] : NoNativeHookListeners;
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

    private static Texture2D? LoadPreviewTexture(string path)
    {
        if (path.StartsWith("res://", StringComparison.OrdinalIgnoreCase) && ResourceLoader.Exists(path))
            return ResourceLoader.Load<Texture2D>(path, null, ResourceLoader.CacheMode.Reuse);

        try
        {
            string globalPath = ProjectSettings.GlobalizePath(path);
            if (!File.Exists(globalPath))
                return null;
            Image image = ImageMediaLoader.LoadPreviewFromFile(globalPath);
            return ImageTexture.CreateFromImage(image);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Loadout: failed to load companion preview '{path}'. {exception.Message}");
            return null;
        }
    }
}

public sealed record CompanionTextureSequence(
    IReadOnlyList<Texture2D> Frames,
    IReadOnlyList<double> Durations);
