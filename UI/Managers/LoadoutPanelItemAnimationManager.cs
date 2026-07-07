using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace Loadout.UI.Managers;

public enum PanelItemAnimationType
{
	LerpMagnify,
	SnapMagnify
}

public sealed class PanelItemAnimationProfile
{
	public string Id { get; }
	public PanelItemAnimationType Type { get; }
	public float HoverScale { get; }
	public float EnterSpeed { get; }
	public float ExitSpeed { get; }
	public float PositionLift { get; }
	public Tween.TransitionType Transition { get; }
	public Tween.EaseType Ease { get; }
	public bool KeepBottomAnchored { get; }
	public bool GlowEnabled { get; }
	public Color GlowColor { get; }
	public float GlowScale { get; }
	public float GlowMinAlpha { get; }
	public float GlowMaxAlpha { get; }
	public float GlowPulseSpeed { get; }

	public PanelItemAnimationProfile(
		string id,
		PanelItemAnimationType type,
		float hoverScale,
		float enterSpeed,
		float exitSpeed,
		float positionLift,
		Tween.TransitionType transition,
		Tween.EaseType ease,
		bool keepBottomAnchored,
		bool glowEnabled = false,
		Color? glowColor = null,
		float glowScale = 1.12f,
		float glowMinAlpha = 0.35f,
		float glowMaxAlpha = 1f,
		float glowPulseSpeed = 4f)
	{
		Id = id;
		Type = type;
		HoverScale = Mathf.Max(1f, hoverScale);
		EnterSpeed = Mathf.Max(0.001f, enterSpeed);
		ExitSpeed = Mathf.Max(0.001f, exitSpeed);
		PositionLift = Mathf.Max(0f, positionLift);
		Transition = transition;
		Ease = ease;
		KeepBottomAnchored = keepBottomAnchored;
		GlowEnabled = glowEnabled;
		GlowColor = glowColor ?? new Color("EFC851");
		GlowScale = Mathf.Max(1f, glowScale);
		GlowMinAlpha = Mathf.Clamp(glowMinAlpha, 0f, 1f);
		GlowMaxAlpha = Mathf.Clamp(glowMaxAlpha, GlowMinAlpha, 1f);
		GlowPulseSpeed = Mathf.Max(0f, glowPulseSpeed);
	}

	public static PanelItemAnimationProfile CreateDefault(string id)
	{
		return new PanelItemAnimationProfile(
			id: id,
			type: PanelItemAnimationType.LerpMagnify,
			hoverScale: 1f,
			enterSpeed: 16f,
			exitSpeed: 11f,
			positionLift: 0f,
			transition: Tween.TransitionType.Back,
			ease: Tween.EaseType.Out,
			keepBottomAnchored: true,
			glowEnabled: true);
	}
}

public static class LoadoutPanelItemAnimationManager
{
	public const string DefaultAnimationId = "yellow_glow_pulse";

	private const string BaseAnimationPath = "res://Loadout/animations/panel_items";
	private const string AnimationConfigName = "animation.json";

	private static readonly Dictionary<string, PanelItemAnimationProfile> ProfileCache = new();
	private static readonly HashSet<string> MissingAnimationCache = new();

	private static string _activeAnimationId = DefaultAnimationId;

	public static event Action<string> AnimationChanged;

	public static string ActiveAnimationId => _activeAnimationId;

	public static void SetActiveAnimation(string animationId)
	{
		string resolvedAnimationId = ResolveAnimationId(animationId);
		if (resolvedAnimationId == _activeAnimationId)
			return;

		_activeAnimationId = resolvedAnimationId;
		AnimationChanged?.Invoke(_activeAnimationId);
	}

	public static string[] GetAvailableAnimationIds()
	{
		var animationIds = new List<string>();
		DirAccess rootDir = DirAccess.Open(BaseAnimationPath);
		if (rootDir == null)
			return new[] { DefaultAnimationId };

		if (rootDir.ListDirBegin() != Error.Ok)
			return new[] { DefaultAnimationId };

		while (true)
		{
			string next = rootDir.GetNext();
			if (string.IsNullOrEmpty(next))
				break;

			if (next is "." or "..")
				continue;

			if (rootDir.CurrentIsDir())
				animationIds.Add(next);
		}

		rootDir.ListDirEnd();

		if (!animationIds.Contains(DefaultAnimationId))
			animationIds.Add(DefaultAnimationId);

		animationIds.Sort(StringComparer.OrdinalIgnoreCase);
		return animationIds.ToArray();
	}

	public static PanelItemAnimationProfile GetProfile(string animationId = "")
	{
		string requestedAnimationId = string.IsNullOrWhiteSpace(animationId) ? _activeAnimationId : animationId;
		string resolvedAnimationId = ResolveAnimationId(requestedAnimationId);

		PanelItemAnimationProfile profile = TryLoadProfile(resolvedAnimationId);
		if (profile != null)
			return profile;

		return PanelItemAnimationProfile.CreateDefault(resolvedAnimationId);
	}

	public static float StepProgress(float current, float target, PanelItemAnimationProfile profile, float delta)
	{
		float speed = target > current ? profile.EnterSpeed : profile.ExitSpeed;
		return profile.Type switch
		{
			PanelItemAnimationType.SnapMagnify => Mathf.MoveToward(current, target, speed * delta),
			_ => Mathf.Lerp(current, target, 1f - Mathf.Exp(-speed * delta))
		};
	}

	public static float EaseProgress(float progress, PanelItemAnimationProfile profile)
	{
		float clampedProgress = Mathf.Clamp(progress, 0f, 1f);
		float eased = (float)Tween.InterpolateValue(
			initialValue: 0f,
			deltaValue: 1f,
			elapsedTime: clampedProgress,
			duration: 1f,
			transType: profile.Transition,
			easeType: profile.Ease);

		return Mathf.Clamp(eased, 0f, 1f);
	}

	public static bool AnimationExists(string animationId)
	{
		if (string.IsNullOrWhiteSpace(animationId))
			return false;

		return FileAccess.FileExists(GetAnimationConfigPath(animationId));
	}

	private static string ResolveAnimationId(string requestedAnimationId)
	{
		if (!string.IsNullOrWhiteSpace(requestedAnimationId) && AnimationExists(requestedAnimationId))
			return requestedAnimationId;

		return DefaultAnimationId;
	}

	private static PanelItemAnimationProfile TryLoadProfile(string animationId)
	{
		if (ProfileCache.TryGetValue(animationId, out PanelItemAnimationProfile cachedProfile))
			return cachedProfile;

		if (MissingAnimationCache.Contains(animationId))
			return null;

		string configPath = GetAnimationConfigPath(animationId);
		if (!FileAccess.FileExists(configPath))
		{
			MissingAnimationCache.Add(animationId);
			return null;
		}

		using FileAccess file = FileAccess.Open(configPath, FileAccess.ModeFlags.Read);
		if (file == null)
		{
			MissingAnimationCache.Add(animationId);
			return null;
		}

		Variant parsed = Json.ParseString(file.GetAsText());
		if (parsed.VariantType != Variant.Type.Dictionary)
		{
			GD.PushWarning($"Invalid panel item animation config: {configPath}");
			MissingAnimationCache.Add(animationId);
			return null;
		}

		var data = (Godot.Collections.Dictionary)parsed;
		PanelItemAnimationProfile profile = new(
			id: animationId,
			type: ParseType(ReadString(data, "type", "lerp_magnify")),
			hoverScale: ReadFloat(data, "hover_scale", 1.35f),
			enterSpeed: ReadFloat(data, "enter_speed", 16f),
			exitSpeed: ReadFloat(data, "exit_speed", 11f),
			positionLift: ReadFloat(data, "position_lift", 6f),
			transition: ParseTransition(ReadString(data, "transition", "back")),
			ease: ParseEase(ReadString(data, "ease", "out")),
			keepBottomAnchored: ReadBool(data, "keep_bottom_anchored", true),
			glowEnabled: ReadBool(data, "glow_enabled", false),
			glowColor: ReadColor(data, "glow_color", new Color("EFC851")),
			glowScale: ReadFloat(data, "glow_scale", 1.12f),
			glowMinAlpha: ReadFloat(data, "glow_min_alpha", 0.35f),
			glowMaxAlpha: ReadFloat(data, "glow_max_alpha", 1f),
			glowPulseSpeed: ReadFloat(data, "glow_pulse_speed", 4f));

		ProfileCache[animationId] = profile;
		return profile;
	}

	private static string GetAnimationConfigPath(string animationId)
	{
		return $"{BaseAnimationPath}/{animationId}/{AnimationConfigName}";
	}

	private static float ReadFloat(Godot.Collections.Dictionary data, string key, float fallback)
	{
		if (!data.ContainsKey(key))
			return fallback;

		object value = data[key];
		if (value is null)
			return fallback;

		return value switch
		{
			float f => f,
			double d => (float)d,
			int i => i,
			long l => l,
			_ when float.TryParse(
				value.ToString(),
				NumberStyles.Float,
				CultureInfo.InvariantCulture,
				out float parsedFloat) => parsedFloat,
			_ => fallback
		};
	}

	private static bool ReadBool(Godot.Collections.Dictionary data, string key, bool fallback)
	{
		if (!data.ContainsKey(key))
			return fallback;

		object value = data[key];
		if (value is null)
			return fallback;

		return value switch
		{
			bool b => b,
			_ when bool.TryParse(value.ToString(), out bool parsedBool) => parsedBool,
			_ => fallback
		};
	}

	private static string ReadString(Godot.Collections.Dictionary data, string key, string fallback)
	{
		if (!data.ContainsKey(key))
			return fallback;

		object value = data[key];
		return value?.ToString() ?? fallback;
	}

	private static Color ReadColor(Godot.Collections.Dictionary data, string key, Color fallback)
	{
		string rawValue = ReadString(data, key, string.Empty);
		if (string.IsNullOrWhiteSpace(rawValue))
			return fallback;

		try
		{
			return new Color(rawValue.Trim().TrimStart('#'));
		}
		catch
		{
			return fallback;
		}
	}

	private static PanelItemAnimationType ParseType(string rawType)
	{
		return rawType.Trim().ToLowerInvariant() switch
		{
			"snap_magnify" => PanelItemAnimationType.SnapMagnify,
			_ => PanelItemAnimationType.LerpMagnify
		};
	}

	private static Tween.TransitionType ParseTransition(string rawTransition)
	{
		return rawTransition.Trim().ToLowerInvariant() switch
		{
			"linear" => Tween.TransitionType.Linear,
			"sine" => Tween.TransitionType.Sine,
			"quint" => Tween.TransitionType.Quint,
			"quart" => Tween.TransitionType.Quart,
			"quad" => Tween.TransitionType.Quad,
			"expo" => Tween.TransitionType.Expo,
			"elastic" => Tween.TransitionType.Elastic,
			"cubic" => Tween.TransitionType.Cubic,
			"circ" => Tween.TransitionType.Circ,
			"bounce" => Tween.TransitionType.Bounce,
			"back" => Tween.TransitionType.Back,
			"spring" => Tween.TransitionType.Spring,
			_ => Tween.TransitionType.Back
		};
	}

	private static Tween.EaseType ParseEase(string rawEase)
	{
		return rawEase.Trim().ToLowerInvariant() switch
		{
			"in" => Tween.EaseType.In,
			"out" => Tween.EaseType.Out,
			"in_out" => Tween.EaseType.InOut,
			"out_in" => Tween.EaseType.OutIn,
			_ => Tween.EaseType.Out
		};
	}
}
