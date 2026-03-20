using System;
using System.Collections.Generic;
using Godot;

namespace Loadout.UI.Managers;

public static class LoadoutSkinManager
{
	public const string DefaultSkinId = "default";

	private const string BaseSkinPath = "res://Loadout/images/relics";
	private const string MissingTexturePath = "res://Loadout/images/ui/missing_texture.png";

	private static readonly Dictionary<string, Texture2D> TextureCache = new();
	private static readonly HashSet<string> MissingTextureCache = new();

	private static string _activeSkinId = DefaultSkinId;
	private static Texture2D _missingTexture;

	public static event Action<string> SkinChanged;

	public static string ActiveSkinId => _activeSkinId;

	public static void SetActiveSkin(string skinId)
	{
		string resolvedSkinId = ResolveSkinId(skinId);
		if (resolvedSkinId == _activeSkinId)
			return;

		_activeSkinId = resolvedSkinId;
		SkinChanged?.Invoke(_activeSkinId);
	}

	public static Texture2D GetTexture(string textureFileName, string skinId = "")
	{
		string resolvedSkinId = ResolveSkinId(string.IsNullOrWhiteSpace(skinId) ? _activeSkinId : skinId);

		Texture2D skinTexture = TryLoadTexture(resolvedSkinId, textureFileName);
		if (skinTexture != null)
			return skinTexture;

		Texture2D defaultTexture = TryLoadTexture(DefaultSkinId, textureFileName);
		if (defaultTexture != null)
			return defaultTexture;

		_missingTexture ??= GD.Load<Texture2D>(MissingTexturePath);
		return _missingTexture;
	}

	public static bool SkinExists(string skinId)
	{
		if (string.IsNullOrWhiteSpace(skinId))
			return false;

		return DirAccess.Open($"{BaseSkinPath}/{skinId}") != null;
	}

	public static string[] GetAvailableSkinIds()
	{
		var skinIds = new List<string>();
		DirAccess rootDir = DirAccess.Open(BaseSkinPath);
		if (rootDir == null)
			return new[] { DefaultSkinId };

		if (rootDir.ListDirBegin() != Error.Ok)
			return new[] { DefaultSkinId };

		while (true)
		{
			string next = rootDir.GetNext();
			if (string.IsNullOrEmpty(next))
				break;

			if (next is "." or "..")
				continue;

			if (rootDir.CurrentIsDir())
				skinIds.Add(next);
		}

		rootDir.ListDirEnd();

		if (!skinIds.Contains(DefaultSkinId))
			skinIds.Add(DefaultSkinId);

		skinIds.Sort(StringComparer.OrdinalIgnoreCase);
		return skinIds.ToArray();
	}

	private static string ResolveSkinId(string requestedSkinId)
	{
		if (!string.IsNullOrWhiteSpace(requestedSkinId) && SkinExists(requestedSkinId))
			return requestedSkinId;

		return DefaultSkinId;
	}

	private static Texture2D TryLoadTexture(string skinId, string textureFileName)
	{
		if (string.IsNullOrWhiteSpace(textureFileName))
			return null;

		string key = $"{skinId}/{textureFileName}";
		if (TextureCache.TryGetValue(key, out Texture2D cachedTexture))
			return cachedTexture;

		if (MissingTextureCache.Contains(key))
			return null;

		string texturePath = $"{BaseSkinPath}/{skinId}/{textureFileName}";
		if (!ResourceLoader.Exists(texturePath))
		{
			MissingTextureCache.Add(key);
			return null;
		}

		Texture2D loadedTexture = GD.Load<Texture2D>(texturePath);
		if (loadedTexture == null)
		{
			MissingTextureCache.Add(key);
			return null;
		}

		TextureCache[key] = loadedTexture;
		return loadedTexture;
	}
}
