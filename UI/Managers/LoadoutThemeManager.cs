using System;
using System.Collections.Generic;
using Godot;

namespace Loadout.UI.Managers;

public static class LoadoutThemeManager
{
	public const string DefaultThemeId = "default";

	private const string BaseThemePath = "res://Loadout/themes";
	private const string ThemeFileName = "theme.tres";

	private static readonly Dictionary<string, Theme> ThemeCache = new();
	private static readonly HashSet<string> MissingThemeCache = new();

	private static string _activeThemeId = DefaultThemeId;

	public static event Action<string> ThemeChanged;

	public static string ActiveThemeId => _activeThemeId;

	public static void SetActiveTheme(string themeId)
	{
		string resolvedThemeId = ResolveThemeId(themeId);
		if (resolvedThemeId == _activeThemeId)
			return;

		_activeThemeId = resolvedThemeId;
		ThemeChanged?.Invoke(_activeThemeId);
	}

	public static Theme GetTheme(string themeId = "")
	{
		string requestedThemeId = string.IsNullOrWhiteSpace(themeId) ? _activeThemeId : themeId;
		string resolvedThemeId = ResolveThemeId(requestedThemeId);

		Theme theme = TryLoadTheme(resolvedThemeId);
		if (theme != null)
			return theme;

		return TryLoadTheme(DefaultThemeId) ?? new Theme();
	}

	public static void ApplyTheme(Control rootControl, string themeId = "")
	{
		if (rootControl == null)
			return;

		rootControl.Theme = GetTheme(themeId);
	}

	public static bool ThemeExists(string themeId)
	{
		if (string.IsNullOrWhiteSpace(themeId))
			return false;

		return FileAccess.FileExists(GetThemePath(themeId));
	}

	public static string[] GetAvailableThemeIds()
	{
		var themeIds = new List<string>();
		DirAccess rootDir = DirAccess.Open(BaseThemePath);
		if (rootDir == null)
			return new[] { DefaultThemeId };

		if (rootDir.ListDirBegin() != Error.Ok)
			return new[] { DefaultThemeId };

		while (true)
		{
			string next = rootDir.GetNext();
			if (string.IsNullOrEmpty(next))
				break;

			if (next is "." or "..")
				continue;

			if (rootDir.CurrentIsDir() && ThemeExists(next))
				themeIds.Add(next);
		}

		rootDir.ListDirEnd();

		if (!themeIds.Contains(DefaultThemeId))
			themeIds.Add(DefaultThemeId);

		themeIds.Sort(StringComparer.OrdinalIgnoreCase);
		return themeIds.ToArray();
	}

	private static string ResolveThemeId(string requestedThemeId)
	{
		if (!string.IsNullOrWhiteSpace(requestedThemeId) && ThemeExists(requestedThemeId))
			return requestedThemeId;

		return DefaultThemeId;
	}

	private static Theme TryLoadTheme(string themeId)
	{
		if (ThemeCache.TryGetValue(themeId, out Theme cachedTheme))
			return cachedTheme;

		if (MissingThemeCache.Contains(themeId))
			return null;

		string themePath = GetThemePath(themeId);
		if (!ResourceLoader.Exists(themePath))
		{
			MissingThemeCache.Add(themeId);
			return null;
		}

		Theme loadedTheme = GD.Load<Theme>(themePath);
		if (loadedTheme == null)
		{
			MissingThemeCache.Add(themeId);
			return null;
		}

		ThemeCache[themeId] = loadedTheme;
		return loadedTheme;
	}

	private static string GetThemePath(string themeId)
	{
		return $"{BaseThemePath}/{themeId}/{ThemeFileName}";
	}
}
