#nullable enable

namespace Loadout.Companions;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

public static class LoadoutCompanionRegistry
{
    public const string NoneId = "none";

    private static readonly Dictionary<string, LoadoutCompanion> CompanionsById = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Texture2D> TexturesById = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> MissingTextures = new(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<LoadoutCompanion> _companions = [];
    private static string _activeCompanionId = NoneId;
    private static bool _initialized;

    public static event Action<string>? ActiveCompanionChanged;
    public static event Action<LoadoutCompanionPresentationRequest>? PresentationRequested;

    public static string ActiveCompanionId => _activeCompanionId;
    public static IReadOnlyList<LoadoutCompanion> Companions => _companions;

    public static bool Initialize()
    {
        if (_initialized)
            return true;

        Type[] companionTypes = GetCompanionTypes();
        if (companionTypes.Length > 0 && companionTypes.Any(type => !ModelDb.Contains(type)))
            return false;

        CompanionsById.Clear();
        foreach (Type type in companionTypes)
        {
            LoadoutCompanion companion = ModelDb.GetById<LoadoutCompanion>(ModelDb.GetId(type));
            string id = NormalizeId(companion.CompanionId);
            if (id == NoneId || CompanionsById.ContainsKey(id))
            {
                GD.PushWarning($"Loadout companion '{type.FullName}' has an invalid or duplicate id '{companion.CompanionId}'.");
                continue;
            }

            CompanionsById[id] = companion;
        }

        _companions = CompanionsById.Values
            .OrderBy(companion => companion.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(companion => companion.CompanionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _initialized = true;

        string resolvedId = ResolveId(_activeCompanionId);
        if (!string.Equals(resolvedId, _activeCompanionId, StringComparison.OrdinalIgnoreCase))
        {
            _activeCompanionId = resolvedId;
            ActiveCompanionChanged?.Invoke(_activeCompanionId);
        }

        return true;
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
        string id = NormalizeId(companion.CompanionId);
        if (TexturesById.TryGetValue(id, out Texture2D? cached))
            return cached;

        if (MissingTextures.Contains(id) || !ResourceLoader.Exists(companion.SpritePath))
        {
            MissingTextures.Add(id);
            return null;
        }

        Texture2D? texture = GD.Load<Texture2D>(companion.SpritePath);
        if (texture is null)
        {
            MissingTextures.Add(id);
            return null;
        }

        if (companion.SpriteRegion is { } region)
        {
            texture = new AtlasTexture
            {
                Atlas = texture,
                Region = region
            };
        }

        TexturesById[id] = texture;
        return texture;
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
                .Where(type => !type.IsAbstract && type.IsSubclassOf(typeof(LoadoutCompanion)))
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToArray();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types
                .Where(type => type is not null && !type.IsAbstract && type.IsSubclassOf(typeof(LoadoutCompanion)))
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
}
