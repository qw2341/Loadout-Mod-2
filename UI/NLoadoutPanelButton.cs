#nullable enable

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
	private const float RainbowSpeed = 0.12f;
	private const double CompanionEnterSeconds = 0.28;
	private const double CompanionExitSeconds = 0.18;
	private static readonly Vector2 CompanionSize = new(48f, 56f);
	private static readonly Vector2 CompanionHiddenPosition = new(-44f, 32f);
	private static readonly Vector2 CompanionVisiblePosition = new(10f, -10f);

	private NLoadoutPanel _nLoadoutPanel = null!;
	private TextureRect _tabImage = null!;
	private TextureRect _arrowImage = null!;
	private TextureRect _companionImage = null!;
	private LoadoutCompanion? _companion;
	private NSpeechBubbleVfx? _speechBubble;
	private Tween? _companionMotionTween;
	private Tween? _companionHoldTween;
	private float _rainbowPhase;
	private bool _mouseInside;
	private bool _panelHovered;
	private bool _hasOpenLoadoutScreen;
	private bool _pressPeekActive;
	private bool _signalsConnected;

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
		else if (!_pressPeekActive && !_mouseInside)
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

		_companionImage = GetNodeOrNull<TextureRect>("CompanionImage") ?? CreateTextureRect("CompanionImage", false);
		_tabImage = GetNodeOrNull<TextureRect>("TabImage") ?? CreateTextureRect("TabImage", true);
		_arrowImage = GetNodeOrNull<TextureRect>("ArrowImage") ?? CreateTextureRect("ArrowImage", false);
		MoveChild(_companionImage, 0);

		_tabImage.Texture = LoadPanelTexture(TabTextureFileName);
		_tabImage.StretchMode = TextureRect.StretchModeEnum.Scale;
		_arrowImage.Texture = LoadPanelTexture(ArrowTextureFileName);
		_tabImage.Material = null;
		_arrowImage.Material = null;
		_companionImage.Material = null;
		_companionImage.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
		_companionImage.Size = CompanionSize;
		_companionImage.PivotOffset = CompanionSize * 0.5f;
		_companionImage.Rotation = Mathf.Pi * 0.25f;
		_companionImage.Position = CompanionHiddenPosition;
		_companionImage.Modulate = Colors.Transparent;
		_companionImage.Visible = false;
		RefreshCompanion();
		OnResized();
	}

	private TextureRect CreateTextureRect(string nodeName, bool fullRect)
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

		AddChild(image);
		return image;
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
		if (!_pressPeekActive && !_panelHovered)
			HideCompanion();
	}

	private void RefreshState()
	{
		bool hasAccess = NLoadoutPanel.ConfigPreviewVisible || LoadoutPanelAccessService.CanLocalPlayerUsePanel();
		Visible = hasAccess && !_nLoadoutPanel.Hidden;
		Disabled = !Visible;
		Modulate = Disabled ? new Color(1f, 1f, 1f, 0.55f) : Colors.White;
		RefreshPanelHoverInput();
		if (Disabled)
			ClearCompanionPresentation();
		else if (!_nLoadoutPanel.Shown)
		{
			_pressPeekActive = false;
			KillTween(ref _companionHoldTween);
			ClearCompanionSpeech();
			HideCompanion();
		}

		if (_arrowImage is null || !IsInstanceValid(_arrowImage))
			return;

		_arrowImage.Rotation = _nLoadoutPanel.Shown ? Mathf.Pi : 0f;
	}

	private void OnActiveCompanionChanged(string _)
	{
		RefreshCompanion();
	}

	private void RefreshCompanion()
	{
		ClearCompanionPresentation();
		_companion = LoadoutCompanionRegistry.GetActiveCompanion();
		Texture2D? texture = _companion is null
			? null
			: LoadoutCompanionRegistry.GetTexture(_companion);

		_companionImage.Texture = texture;
		RefreshPanelHoverInput();
		if (texture is null)
			return;

		if ((_mouseInside || _panelHovered) && !Disabled && Visible && !HasOpenLoadoutScreen())
			ShowCompanion();
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

	private void OnOpenScreenStateChanged(bool hasOpenScreen)
	{
		_hasOpenLoadoutScreen = hasOpenScreen;
		RefreshPanelHoverInput();
		if (!hasOpenScreen)
			return;

		_pressPeekActive = false;
		KillTween(ref _companionHoldTween);
		ClearCompanionSpeech();
		HideCompanion();
	}

	private void OnCompanionPresentationRequested(LoadoutCompanionPresentationRequest request)
	{
		if (_companion is null
		    || HasOpenLoadoutScreen()
		    || !string.Equals(_companion.CompanionId, request.Companion.CompanionId, System.StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		BeginTimedCompanionPeek(request.Seconds);
		if (!string.IsNullOrWhiteSpace(request.Text))
			ShowCompanionSpeech(request.Text, request.Seconds);
	}

	private void BeginTimedCompanionPeek(double seconds)
	{
		if (_companion is null || Disabled || !Visible)
			return;

		ShowCompanion();
		_pressPeekActive = true;
		KillTween(ref _companionHoldTween);
		_companionHoldTween = CreateTween();
		_companionHoldTween.TweenInterval(System.Math.Max(0.1, seconds));
		_companionHoldTween.TweenCallback(Callable.From(() =>
		{
			_pressPeekActive = false;
			_companionHoldTween = null;
			if (!_mouseInside && !_panelHovered)
				HideCompanion();
		}));
	}

	private void ShowCompanion()
	{
		if (_companion is null
		    || _companionImage.Texture is null
		    || Disabled
		    || !Visible
		    || HasOpenLoadoutScreen())
			return;

		KillTween(ref _companionMotionTween);
		_companionImage.Visible = true;
		_companionMotionTween = CreateTween();
		_companionMotionTween.TweenProperty(
			_companionImage,
			"position",
			CompanionVisiblePosition,
			CompanionEnterSeconds)
			.SetTrans(Tween.TransitionType.Back)
			.SetEase(Tween.EaseType.Out);
		_companionMotionTween.Parallel().TweenProperty(
			_companionImage,
			"modulate",
			Colors.White,
			CompanionEnterSeconds * 0.6);
	}

	private void HideCompanion()
	{
		if (_companionImage.Texture is null || !_companionImage.Visible)
			return;

		KillTween(ref _companionMotionTween);
		_companionMotionTween = CreateTween();
		_companionMotionTween.TweenProperty(
			_companionImage,
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

	private void ShowCompanionSpeech(string text, double seconds)
	{
		ClearCompanionSpeech();
		Vector2 speechPosition = GlobalPosition + CompanionVisiblePosition + new Vector2(CompanionSize.X, 4f);
		_speechBubble = NSpeechBubbleVfx.Create(
			text,
			DialogueSide.Left,
			speechPosition,
			seconds,
			VfxColor.Blue);
		if (_speechBubble is null)
			return;

		NLoadoutPanelRoot.Instance?.AddChild(_speechBubble);
	}

	private void ClearCompanionPresentation()
	{
		_mouseInside = false;
		_panelHovered = false;
		_pressPeekActive = false;
		KillTween(ref _companionMotionTween);
		KillTween(ref _companionHoldTween);
		ClearCompanionSpeech();

		if (_companionImage is null || !IsInstanceValid(_companionImage))
			return;

		_companionImage.Position = CompanionHiddenPosition;
		_companionImage.Modulate = Colors.Transparent;
		_companionImage.Visible = false;
	}

	private void ClearCompanionSpeech()
	{
		if (_speechBubble is not null && IsInstanceValid(_speechBubble))
			_speechBubble.QueueFree();

		_speechBubble = null;
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

	private static Color GetSineRainbowColor(float phase)
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
