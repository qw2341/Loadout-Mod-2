#nullable enable

using System;
using Godot;
using Loadout.Companions;
using Loadout.UI.Managers;
using Loadout.Services.Loadouts;
using MegaCrit.Sts2.Core.Audio;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Nodes.Vfx.Utilities;

namespace Loadout.UI;

public partial class NLoadoutPanelButton : Button
{
	private static readonly StyleBoxEmpty EmptyStyle = new();

	private const string TabTextureFileName = "SidePanelTab.png";
	private const string ArrowTextureFileName = "SidePanelArrow.png";
	public const float RainbowSpeed = 0.12f;
	private const double CompanionEnterSeconds = 0.28;
	private const double CompanionExitSeconds = 0.18;
	private const float HiddenPanelCompanionOffsetX = 33f;
	private static readonly Vector2 CompanionSize = new(48f, 56f);
	private static readonly Vector2 CompanionHiddenPosition = new(-44f, 32f);
	private static readonly Vector2 CompanionVisiblePosition = new(15f, -15f);
	

	private NLoadoutPanel _nLoadoutPanel = null!;
	private TextureRect _tabImage = null!;
	private TextureRect _arrowImage = null!;
	private Control _companionPresentationAnchor = null!;
	private TextureRect _companionImage = null!;
	private Control _companionSpeechAnchor = null!;
	private Timer? _companionAnimationTimer;
	private LoadoutCompanion? _companion;
	private CompanionTextureSequence? _companionTextureSequence;
	private NSpeechBubbleVfx? _speechBubble;
	private Tween? _companionMotionTween;
	private Tween? _companionHoldTween;
	private float _rainbowPhase;
	private bool _mouseInside;
	private bool _panelHovered;
	private bool _timedPeekActive;
	private bool _hasOpenLoadoutScreen;
	private bool _signalsConnected;
	private int _companionAnimationFrame;

	public override void _Ready()
	{
		_nLoadoutPanel = GetParent<NLoadoutPanel>();
		_hasOpenLoadoutScreen = NLoadoutPanelRoot.Instance?.HasOpenScreen == true;

		BuildVisuals();
		_nLoadoutPanel.VisibilityStateChanged += RefreshState;
		LoadoutPanelAccessService.AccessChanged += RefreshState;
		NLoadoutPanelRoot.OpenScreenStateChanged += OnOpenScreenStateChanged;

		LoadoutCompanionRegistry.ActiveCompanionChanged += OnActiveCompanionChanged;
		LoadoutCompanionRegistry.PresentationRequested += OnCompanionPresentationRequested;
		Pressed += OnPressed;
		MouseEntered += OnMouseEntered;
		MouseExited += OnMouseExited;
		Resized += OnResized;
		_signalsConnected = true;
		RefreshState();
	}

	public override void _ExitTree()
	{
		SetProcessInput(false);
		ClearCompanionPresentation();

		if (!_signalsConnected)
			return;

		Pressed -= OnPressed;
		MouseEntered -= OnMouseEntered;
		MouseExited -= OnMouseExited;
		Resized -= OnResized;
		DestroyCompanionAnimationTimer();
		_nLoadoutPanel.VisibilityStateChanged -= RefreshState;
		LoadoutPanelAccessService.AccessChanged -= RefreshState;
		NLoadoutPanelRoot.OpenScreenStateChanged -= OnOpenScreenStateChanged;

		LoadoutCompanionRegistry.ActiveCompanionChanged -= OnActiveCompanionChanged;
		LoadoutCompanionRegistry.PresentationRequested -= OnCompanionPresentationRequested;
		_signalsConnected = false;
	}

	public override void _Process(double delta)
	{
		_rainbowPhase = Mathf.PosMod(_rainbowPhase + (float)delta * RainbowSpeed * Mathf.Tau, Mathf.Tau);
		UpdateRainbowColor(_rainbowPhase);
	}

	public override void _Input(InputEvent inputEvent)
	{
		if (inputEvent is not InputEventMouseMotion mouseMotion)
			return;

		bool panelHovered = !_nLoadoutPanel.Hidden
		                    && _nLoadoutPanel.Shown
		                    && !HasOpenLoadoutScreen()
		                    && _nLoadoutPanel.GetGlobalRect().HasPoint(mouseMotion.GlobalPosition);
		if (panelHovered == _panelHovered)
			return;

		_panelHovered = panelHovered;
		if (_panelHovered)
			ShowCompanion();
		else if (!_mouseInside && !_timedPeekActive)
			HideCompanion();
	}

	private void BuildVisuals()
	{
		Text = string.Empty;
		ToggleMode = false;
		FocusMode = FocusModeEnum.None;
		MouseFilter = MouseFilterEnum.Stop;
		CustomMinimumSize = new Vector2(32f, 128f);

		AddThemeStyleboxOverride("normal", EmptyStyle);
		AddThemeStyleboxOverride("hover", EmptyStyle);
		AddThemeStyleboxOverride("pressed", EmptyStyle);
		AddThemeStyleboxOverride("focus", EmptyStyle);
		AddThemeStyleboxOverride("disabled", EmptyStyle);

		_companionPresentationAnchor = GetNodeOrNull<Control>("CompanionPresentationAnchor")
		                              ?? CreateCompanionPresentationAnchor();
		_companionImage = _companionPresentationAnchor.GetNodeOrNull<TextureRect>("CompanionImage")
		                  ?? CreateTextureRect("CompanionImage", false, _companionPresentationAnchor);
		_companionSpeechAnchor = _companionPresentationAnchor.GetNodeOrNull<Control>("CompanionSpeechAnchor")
		                         ?? CreateCompanionSpeechAnchor();
		_tabImage = GetNodeOrNull<TextureRect>("TabImage") ?? CreateTextureRect("TabImage", true);
		_arrowImage = GetNodeOrNull<TextureRect>("ArrowImage") ?? CreateTextureRect("ArrowImage", false);
		MoveChild(_companionPresentationAnchor, 0);

		_tabImage.Texture = LoadPanelTexture(TabTextureFileName);
		_tabImage.StretchMode = TextureRect.StretchModeEnum.Scale;
		_arrowImage.Texture = LoadPanelTexture(ArrowTextureFileName);
		_tabImage.Material = null;
		_arrowImage.Material = null;
		_companionImage.Material = null;
		_companionSpeechAnchor.MouseFilter = MouseFilterEnum.Ignore;
		_companionSpeechAnchor.ZIndex = _companionImage.ZIndex + 1;
		_companionImage.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
		_companionImage.Size = CompanionSize;
		_companionImage.PivotOffset = CompanionSize * 0.5f;
		_companionImage.Rotation = Mathf.Pi * 0.25f;
		_companionImage.Position = Vector2.Zero;
		_companionImage.Modulate = Colors.Transparent;
		_companionImage.Visible = false;
		_companionPresentationAnchor.Position = CompanionHiddenPosition;
		_companionSpeechAnchor.Position = new Vector2(CompanionSize.X * 2f, -CompanionSize.Y);
		RefreshCompanion();
		OnResized();
	}

	private TextureRect CreateTextureRect(string nodeName, bool fullRect, Control? parent = null)
	{
		TextureRect image = new()
		{
			Name = nodeName,
			MouseFilter = MouseFilterEnum.Ignore,
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered
		};

		if (fullRect)
			image.SetAnchorsPreset(LayoutPreset.FullRect);

		(parent ?? this).AddChild(image);
		return image;
	}

	private Control CreateCompanionPresentationAnchor()
	{
		Control anchor = new()
		{
			Name = "CompanionPresentationAnchor",
			MouseFilter = MouseFilterEnum.Ignore
		};
		AddChild(anchor);
		return anchor;
	}

	private Control CreateCompanionSpeechAnchor()
	{
		Control anchor = new()
		{
			Name = "CompanionSpeechAnchor",
			MouseFilter = MouseFilterEnum.Ignore,
			ZIndex = _companionImage.ZIndex + 1
		};
		_companionPresentationAnchor.AddChild(anchor);
		return anchor;
	}

	private void OnPressed()
	{
		if (_nLoadoutPanel.Hidden)
		{
			RefreshState();
			return;
		}

		_nLoadoutPanel.ToggleShown();
		RefreshState();
	}

	private void OnMouseEntered()
	{
		_mouseInside = true;
		if (!Disabled)
		{
			SfxCmd.Play(FmodSfx.uiHover);
			ShowCompanion();
		}
	}

	private void OnMouseExited()
	{
		_mouseInside = false;
		if (!_panelHovered && !_timedPeekActive)
			HideCompanion();
	}

	private void RefreshState()
	{
		bool hasAccess = NLoadoutPanel.ConfigPreviewVisible || LoadoutPanelAccessService.CanLocalPlayerUsePanel();
		bool canPresentCompanion = _companion is not null
		                           && (hasAccess || MegaCrit.Sts2.Core.Runs.RunManager.Instance.IsInProgress);
		Visible = hasAccess && !_nLoadoutPanel.Hidden || canPresentCompanion;
		Disabled = !hasAccess || _nLoadoutPanel.Hidden;
		Modulate = Colors.White;
		_tabImage.Visible = !Disabled;
		_arrowImage.Visible = !Disabled;
		RefreshPanelHoverInput();
		if (!Visible || _companion is null)
			ClearCompanionPresentation();
		else if (!_nLoadoutPanel.Shown && !_timedPeekActive)
		{
			HideCompanion();
		}

		if (_arrowImage is null || !IsInstanceValid(_arrowImage))
			return;

		_arrowImage.Rotation = _nLoadoutPanel.Shown ? Mathf.Pi : 0f;
	}

	private void OnActiveCompanionChanged(string _)
	{
		RefreshCompanion();
		RefreshState();
	}

	private void OnCompanionPresentationRequested(LoadoutCompanionPresentationRequest request)
	{
		if (_companion is null
		    || !Visible
		    || !string.Equals(_companion.CompanionId, request.Companion.CompanionId, StringComparison.OrdinalIgnoreCase))
			return;

		if (!string.IsNullOrWhiteSpace(request.Text))
			ShowCompanionSpeech(request.Text, request.Seconds);
		BeginTimedCompanionPeek(request.Seconds);
	}

	private void RefreshCompanion()
	{
		ClearCompanionPresentation();
		DestroyCompanionAnimationTimer();
		_companionAnimationFrame = 0;
		_companion = LoadoutCompanionRegistry.GetActiveCompanion();
		_companionTextureSequence = null;
		_companionImage.Texture = null;
		RefreshPanelHoverInput();

		if ((_mouseInside || _panelHovered) && !Disabled && Visible && !HasOpenLoadoutScreen())
			ShowCompanion();
	}

	private void EnsureCompanionTextureLoaded()
	{
		if (_companion is null || _companionTextureSequence is not null)
			return;

		_companionTextureSequence = LoadoutCompanionRegistry.GetTextureSequence(_companion);
		_companionAnimationFrame = 0;
		_companionImage.Texture = _companionTextureSequence?.Frames[0];
	}

	private void AdvanceCompanionAnimation()
	{
		if (_companionTextureSequence is not { Frames.Count: > 1 } sequence
		    || !_companionImage.Visible
		    || !Visible
		    || Disabled && !_timedPeekActive
		    || HasOpenLoadoutScreen() && !_timedPeekActive)
			return;
		_companionAnimationFrame = (_companionAnimationFrame + 1) % sequence.Frames.Count;
		_companionImage.Texture = sequence.Frames[_companionAnimationFrame];
		RestartCompanionAnimation();
	}

	private void RestartCompanionAnimation()
	{
		if (_companionTextureSequence is not { Frames.Count: > 1 } sequence
		    || !_companionImage.Visible
		    || !Visible
		    || Disabled && !_timedPeekActive
		    || HasOpenLoadoutScreen() && !_timedPeekActive)
			return;
		Timer timer = EnsureCompanionAnimationTimer();
		timer.WaitTime = System.Math.Clamp(
			sequence.Durations[_companionAnimationFrame],
			0.02,
			10.0);
		timer.Start();
	}

	private Timer EnsureCompanionAnimationTimer()
	{
		if (_companionAnimationTimer is not null && IsInstanceValid(_companionAnimationTimer))
			return _companionAnimationTimer;

		_companionAnimationTimer = GetNodeOrNull<Timer>("CompanionAnimationTimer") ?? new Timer
		{
			Name = "CompanionAnimationTimer",
			OneShot = true
		};
		if (_companionAnimationTimer.GetParent() is null)
			AddChild(_companionAnimationTimer);
		_companionAnimationTimer.Timeout += AdvanceCompanionAnimation;
		return _companionAnimationTimer;
	}

	private void DestroyCompanionAnimationTimer()
	{
		if (_companionAnimationTimer is null || !IsInstanceValid(_companionAnimationTimer))
		{
			_companionAnimationTimer = null;
			return;
		}

		_companionAnimationTimer.Stop();
		_companionAnimationTimer.Timeout -= AdvanceCompanionAnimation;
		_companionAnimationTimer.QueueFree();
		_companionAnimationTimer = null;
	}

	private void OnOpenScreenStateChanged(bool hasOpenScreen)
	{
		_hasOpenLoadoutScreen = hasOpenScreen;
		RefreshPanelHoverInput();
		if (!hasOpenScreen)
			return;

		if (!_timedPeekActive)
			HideCompanion();
	}

	private void RefreshPanelHoverInput()
	{
		bool trackPanelHover = _companion is not null
		                       && !Disabled
		                       && Visible
		                       && !HasOpenLoadoutScreen()
		                       && _nLoadoutPanel is { Hidden: false, Shown: true };
		if (!trackPanelHover)
			_panelHovered = false;

		SetProcessInput(trackPanelHover);
	}

	private void ShowCompanion()
	{
		EnsureCompanionTextureLoaded();
		if (_companion is null
		    || _companionImage.Texture is null
		    || !Visible
		    || Disabled && !_timedPeekActive
		    || HasOpenLoadoutScreen() && !_timedPeekActive)
			return;

		KillTween(ref _companionMotionTween);
		_companionImage.Visible = true;
		RestartCompanionAnimation();
		_companionMotionTween = CreateTween();
		_companionMotionTween.TweenProperty(
			_companionPresentationAnchor,
			"position",
			GetCompanionVisiblePosition(),
			CompanionEnterSeconds)
			.SetTrans(Tween.TransitionType.Back)
			.SetEase(Tween.EaseType.Out);
		_companionMotionTween.Parallel().TweenProperty(
			_companionImage,
			"modulate",
			Colors.White,
			CompanionEnterSeconds * 0.6);
	}

	private void BeginTimedCompanionPeek(double seconds)
	{
		KillTween(ref _companionHoldTween);
		_timedPeekActive = true;
		ShowCompanion();
		_companionHoldTween = CreateTween();
		_companionHoldTween.TweenInterval(seconds);
		_companionHoldTween.TweenCallback(Callable.From(() =>
		{
			_timedPeekActive = false;
			_companionHoldTween = null;
			if (!_mouseInside && !_panelHovered)
				HideCompanion();
		}));
	}

	private void ShowCompanionSpeech(string text, double seconds)
	{
		ClearCompanionSpeech();
		_speechBubble = NSpeechBubbleVfx.Create(
			text,
			DialogueSide.Left,
			_companionSpeechAnchor.GlobalPosition,
			seconds,
			VfxColor.White);
		if (_speechBubble is not null)
			_companionSpeechAnchor.AddChild(_speechBubble);
	}

	private void ClearCompanionSpeech()
	{
		if (_speechBubble is not null
		    && IsInstanceValid(_speechBubble)
		    && !_speechBubble.IsQueuedForDeletion())
			_speechBubble.QueueFree();
		_speechBubble = null;
	}

	private void HideCompanion()
	{
		_companionAnimationTimer?.Stop();
		if (_companionImage.Texture is null || !_companionImage.Visible)
			return;

		KillTween(ref _companionMotionTween);
		_companionMotionTween = CreateTween();
		_companionMotionTween.TweenProperty(
			_companionPresentationAnchor,
			"position",
			CompanionHiddenPosition,
			CompanionExitSeconds)
			.SetTrans(Tween.TransitionType.Quad)
			.SetEase(Tween.EaseType.In);
		_companionMotionTween.Parallel().TweenProperty(
			_companionImage,
			"modulate",
			Colors.Transparent,
			CompanionExitSeconds);
		_companionMotionTween.TweenCallback(Callable.From(() =>
		{
			_companionImage.Visible = false;
			_companionMotionTween = null;
		}));
	}

	private void ClearCompanionPresentation()
	{
		_companionAnimationTimer?.Stop();
		_mouseInside = false;
		_panelHovered = false;
		_timedPeekActive = false;
		KillTween(ref _companionMotionTween);
		KillTween(ref _companionHoldTween);
		ClearCompanionSpeech();

		if (_companionImage is null || !IsInstanceValid(_companionImage))
			return;

		_companionPresentationAnchor.Position = CompanionHiddenPosition;
		_companionImage.Position = Vector2.Zero;
		_companionImage.Modulate = Colors.Transparent;
		_companionImage.Visible = false;
		if (_companionSpeechAnchor is not null && IsInstanceValid(_companionSpeechAnchor))
			_companionSpeechAnchor.Position = new Vector2(_companionImage.Size.X * 2f, -_companionImage.Size.Y);
	}

	private Vector2 GetCompanionVisiblePosition()
	{
		return _nLoadoutPanel.Hidden
			? CompanionVisiblePosition + new Vector2(HiddenPanelCompanionOffsetX, 0f)
			: CompanionVisiblePosition;
	}

	private static void KillTween(ref Tween? tween)
	{
		if (tween is not null && IsInstanceValid(tween))
			tween.Kill();

		tween = null;
	}

	private bool HasOpenLoadoutScreen()
	{
		return _hasOpenLoadoutScreen;
	}

	private void OnResized()
	{
		if (_tabImage is not null && IsInstanceValid(_tabImage))
		{
			_tabImage.Position = Vector2.Zero;
			_tabImage.Size = Size;
			_tabImage.PivotOffset = Size * 0.5f;
		}

		if (_arrowImage is not null && IsInstanceValid(_arrowImage))
		{
			Vector2 arrowSize = new(32f, 32f);
			_arrowImage.Size = arrowSize;
			_arrowImage.Position = (Size - arrowSize) * 0.5f;
			_arrowImage.PivotOffset = arrowSize * 0.5f;
		}
	}

	private void UpdateRainbowColor(float phase)
	{
		Color tabColor = GetSineRainbowColor(phase);
		Color arrowColor = GetSineRainbowColor(phase + Mathf.Tau * 0.08f).Lightened(0.25f);

		SelfModulate = tabColor;

		if (_tabImage is not null && IsInstanceValid(_tabImage))
			_tabImage.Modulate = tabColor;

		if (_arrowImage is not null && IsInstanceValid(_arrowImage))
			_arrowImage.Modulate = arrowColor;
	}

	public static Color GetSineRainbowColor(float phase)
	{
		const float baseChannel = 0.18f;
		const float channelRange = 0.82f;
		float red = baseChannel + channelRange * Sine01(phase);
		float green = baseChannel + channelRange * Sine01(phase + Mathf.Tau / 3f);
		float blue = baseChannel + channelRange * Sine01(phase + 2f * Mathf.Tau / 3f);

		return new Color(red, green, blue, 1f);
	}

	private static float Sine01(float value)
	{
		return (Mathf.Sin(value) + 1f) * 0.5f;
	}

	private static Texture2D LoadPanelTexture(string fileName)
	{
		return LoadoutSkinManager.GetTexture(fileName);
	}
}
