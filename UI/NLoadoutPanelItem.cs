using Godot;
using Loadout.UI.Managers;
using Loadout.UI.Screens;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using System;
using System.Reflection;
using System.Threading.Tasks;

namespace Loadout.UI;

public partial class NLoadoutPanelItem : TextureButton
{
	[Export]
	public string TextureFileName = "LoadoutBag.png";

	[Export]
	public bool UseGlobalSkin = true;

	[Export]
	public string SkinId = LoadoutSkinManager.DefaultSkinId;

	[Export]
	public bool UseGlobalAnimation = true;

	[Export]
	public string AnimationId = LoadoutPanelItemAnimationManager.DefaultAnimationId;

	[Export] 
	public int MinimumSizeY = 76;

	[Export]
	public string DisplayName = string.Empty;

	[Export]
	public string Description = string.Empty;
	
	private PanelItemAnimationProfile _animationProfile;
	private float _hoverProgress;
	private bool _isHovered;
	private bool _isInsideContainer;
	private bool _quickActionInFlight;
	private Vector2 _baseScale = Vector2.One;
	private Vector2 _basePosition;
	private TextureRect _outline;
	private ShaderMaterial _outlineMaterial;
	private float _glowPulseTime;
	private NGenericSelectScreen _boundScreen;
	private Action<NGenericSelectScreen> _beforeOpen;
	private Action<NGenericSelectScreen> _afterOpen;
	private static readonly Color OutlineRestColor = Colors.Black;
	private static readonly Shader OutlineTintShader = new()
	{
		Code = """
		       shader_type canvas_item;
		       uniform vec4 outline_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);

		       void fragment() {
		       	vec4 tex = texture(TEXTURE, UV);
		       	COLOR = vec4(outline_color.rgb, tex.a * outline_color.a);
		       }
		       """
	};
	private static readonly FieldInfo HoverTipTitleField = typeof(HoverTip).GetField("<Title>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
	private static readonly FieldInfo HoverTipDescriptionField = typeof(HoverTip).GetField("<Description>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);

	public NLoadoutPanelItem()
	{
	}

	public NLoadoutPanelItem(string textureFileName, string displayName, string description)
	{
		TextureFileName = textureFileName;
		DisplayName = displayName;
		Description = description;
	}
	
	public override void _Ready()
	{
		_isInsideContainer = GetParent() is Container;
		_baseScale = Scale;
		_basePosition = Position;

		IgnoreTextureSize = true;
		StretchMode = StretchModeEnum.KeepCentered;
		SetCustomMinimumSize(new Vector2(0, MinimumSizeY));

		_animationProfile = ResolveAnimationProfile();
		EnsureOutlineNode();
		ApplySkinTexture();
		UpdatePivotOffset();
		ApplyAnimationVisuals();

		MouseEntered += OnMouseEntered;
		MouseExited += OnMouseExited;
		Resized += OnResized;
		Pressed += OpenSelectScreen;

		if (UseGlobalSkin)
			LoadoutSkinManager.SkinChanged += OnSkinChanged;

		if (UseGlobalAnimation)
			LoadoutPanelItemAnimationManager.AnimationChanged += OnAnimationChanged;
		
		// GD.Print($"Initialized Loadout Panel Item, Size: {Size}");
	}

	public override void _ExitTree()
	{
		MouseEntered -= OnMouseEntered;
		MouseExited -= OnMouseExited;
		Resized -= OnResized;
		Pressed -= OpenSelectScreen;

		if (UseGlobalSkin)
			LoadoutSkinManager.SkinChanged -= OnSkinChanged;

		if (UseGlobalAnimation)
			LoadoutPanelItemAnimationManager.AnimationChanged -= OnAnimationChanged;

		NHoverTipSet.Remove(this);
	}

	public override void _Process(double delta)
	{
		_glowPulseTime += (float)delta;
		float target = _isHovered ? 1f : 0f;
		_hoverProgress = LoadoutPanelItemAnimationManager.StepProgress(
			_hoverProgress,
			target,
			_animationProfile,
			(float)delta);

		ApplyAnimationVisuals();
	}

	public override void _GuiInput(InputEvent @event)
	{
		base._GuiInput(@event);

		if (@event is not InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: false } mouseButton)
			return;

		Func<Task> quickAction = GetQuickActionForGesture(mouseButton);
		if (quickAction is not null)
		{
			AcceptEvent();
			if (!_quickActionInFlight)
			{
				_quickActionInFlight = true;
				_ = RunQuickActionAsync(quickAction);
			}
			return;
		}

		AcceptEvent();
		OpenSelectScreen();
	}

	public void RefreshVisuals()
	{
		_animationProfile = ResolveAnimationProfile();
		ApplySkinTexture();
		UpdatePivotOffset();
		ApplyAnimationVisuals();
	}

	private void ApplySkinTexture()
	{
		Texture2D texture = LoadoutSkinManager.GetTexture(
			TextureFileName,
			UseGlobalSkin ? string.Empty : SkinId);

		TextureNormal = texture;
		TextureHover = texture;
		TexturePressed = texture;
		TextureDisabled = texture;

		ApplyGlowTexture();
	}

	private PanelItemAnimationProfile ResolveAnimationProfile()
	{
		return LoadoutPanelItemAnimationManager.GetProfile(
			UseGlobalAnimation ? string.Empty : AnimationId);
	}

	private void ApplyAnimationVisuals()
	{
		float easedProgress = LoadoutPanelItemAnimationManager.EaseProgress(_hoverProgress, _animationProfile);
		float scaleAmount = Mathf.Lerp(1f, _animationProfile.HoverScale, easedProgress);

		Scale = _baseScale * scaleAmount;
		ApplyOutlineVisuals(easedProgress);

		if (_isInsideContainer || _animationProfile.PositionLift <= 0f)
			return;

		float liftAmount = Mathf.Lerp(0f, _animationProfile.PositionLift, easedProgress);
		Position = _basePosition + new Vector2(0f, -liftAmount);
	}

	private void UpdatePivotOffset()
	{
		if (_animationProfile.KeepBottomAnchored)
		{
			PivotOffset = new Vector2(Size.X * 0.5f, Size.Y);
			return;
		}

		PivotOffset = Size * 0.5f;
	}

	private void EnsureOutlineNode()
	{
		if (_outline is not null && IsInstanceValid(_outline))
			return;

		_outlineMaterial = new ShaderMaterial
		{
			Shader = OutlineTintShader
		};
		_outlineMaterial.SetShaderParameter("outline_color", OutlineRestColor);

		_outline = new TextureRect
		{
			Name = "Outline",
			MouseFilter = MouseFilterEnum.Ignore,
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			StretchMode = TextureRect.StretchModeEnum.KeepCentered,
			ShowBehindParent = true,
			Material = _outlineMaterial
		};
		_outline.SetAnchorsPreset(LayoutPreset.FullRect);
		AddChild(_outline);
	}

	private void ApplyGlowTexture()
	{
		EnsureOutlineNode();

		Texture2D outlineTexture = TryLoadOutlineTexture();
		_outline.Texture = outlineTexture;
		_outline.Visible = outlineTexture is not null;
	}

	private Texture2D TryLoadOutlineTexture()
	{
		string skinId = UseGlobalSkin ? LoadoutSkinManager.ActiveSkinId : SkinId;
		Texture2D texture = TryLoadOutlineTexture(skinId);
		if (texture is not null)
			return texture;

		return TryLoadOutlineTexture(LoadoutSkinManager.DefaultSkinId);
	}

	private Texture2D TryLoadOutlineTexture(string skinId)
	{
		if (string.IsNullOrWhiteSpace(skinId) || string.IsNullOrWhiteSpace(TextureFileName))
			return null;

		string texturePath = $"res://Loadout/images/relics/{skinId}/outline/{TextureFileName}";
		return ResourceLoader.Exists(texturePath) ? GD.Load<Texture2D>(texturePath) : null;
	}

	private void ApplyOutlineVisuals(float easedProgress)
	{
		if (_outline is null || !IsInstanceValid(_outline))
			return;

		if (_outline.Texture is null)
		{
			_outline.Visible = false;
			return;
		}

		_outline.Visible = true;

		float pulse = _animationProfile.GlowPulseSpeed <= 0f
			? 1f
			: (Mathf.Sin(_glowPulseTime * _animationProfile.GlowPulseSpeed) + 1f) * 0.5f;
		float glowAmount = _animationProfile.GlowEnabled
			? Mathf.Lerp(_animationProfile.GlowMinAlpha, _animationProfile.GlowMaxAlpha, pulse) * easedProgress
			: 0f;
		Color outlineColor = OutlineRestColor.Lerp(_animationProfile.GlowColor, glowAmount);
		_outlineMaterial?.SetShaderParameter("outline_color", outlineColor);
		_outline.Scale = Vector2.One * Mathf.Lerp(1f, _animationProfile.GlowScale, glowAmount);
		_outline.Position = Vector2.Zero;
		_outline.Size = Size;
		_outline.PivotOffset = Size * 0.5f;
	}

	private void OnMouseEntered()
	{
		_isHovered = true;
		ShowHoverTip();
	}

	private void OnMouseExited()
	{
		_isHovered = false;
		NHoverTipSet.Remove(this);
	}

	private void ShowHoverTip()
	{
		if (string.IsNullOrWhiteSpace(DisplayName) && string.IsNullOrWhiteSpace(Description))
			return;

		NHoverTipSet.Remove(this);
		NHoverTipSet.CreateAndShow(this, CreatePlainHoverTip(), HoverTip.GetHoverTipAlignment(this))?.SetFollowOwner();
		NLoadoutPanelRoot.Instance?.AdoptGameHoverTips();
	}

	private HoverTip CreatePlainHoverTip()
	{
		HoverTip hoverTip = default;
		object boxed = hoverTip;
		HoverTipTitleField?.SetValue(boxed, string.IsNullOrWhiteSpace(DisplayName) ? TextureFileName : DisplayName);
		HoverTipDescriptionField?.SetValue(boxed, Description);
		hoverTip = (HoverTip)boxed;
		hoverTip.Id = $"loadout_panel_item:{TextureFileName}:{DisplayName}";
		return hoverTip;
	}

	private void OnResized()
	{
		UpdatePivotOffset();
		ApplyAnimationVisuals();
	}

	private void OnSkinChanged(string _)
	{
		if (!UseGlobalSkin)
			return;

		ApplySkinTexture();
	}

	private void OnAnimationChanged(string _)
	{
		if (!UseGlobalAnimation)
			return;

		_animationProfile = ResolveAnimationProfile();
		UpdatePivotOffset();
		ApplyAnimationVisuals();
	}

	private Func<Task> GetQuickActionForGesture(InputEventMouseButton mouseButton)
	{
		if ((mouseButton.CtrlPressed || Input.IsKeyPressed(Key.Ctrl)) && QuickAction is not null)
			return QuickAction;

		if ((mouseButton.ShiftPressed || Input.IsKeyPressed(Key.Shift)) && ShiftQuickAction is not null)
			return ShiftQuickAction;

		return null;
	}

	private void OpenSelectScreen()
	{
		//open select screen
		if (_boundScreen == null)
		{
			var scene = GD.Load<PackedScene>("res://UI/Screens/SampleSelectScreen.tscn");
			_boundScreen = scene.Instantiate<NGenericSelectScreen>();
		}

		_beforeOpen?.Invoke(_boundScreen);

		var root = NLoadoutPanelRoot.Instance ?? NLoadoutPanelRoot.GetOrAttach(GetTree());
		if (root == null)
		{
			GD.PushError("LoadoutPanelItem: could not find or attach LoadoutPanelRoot.");
			return;
		}

		root.OpenScreen(_boundScreen);
		_afterOpen?.Invoke(_boundScreen);
	}

	public NGenericSelectScreen BoundScreen
	{
		get => _boundScreen;
		set => _boundScreen = value;
	}

	public Action<NGenericSelectScreen> BeforeOpen
	{
		get => _beforeOpen;
		set => _beforeOpen = value;
	}

	public Action<NGenericSelectScreen> AfterOpen
	{
		get => _afterOpen;
		set => _afterOpen = value;
	}

	public Func<Task> QuickAction { get; set; }

	public Func<Task> ShiftQuickAction { get; set; }

	private async Task RunQuickActionAsync(Func<Task> quickAction)
	{
		try
		{
			await quickAction();
		}
		catch (Exception exception)
		{
			GD.PushError($"LoadoutPanelItem: quick action failed for '{DisplayName}': {exception}");
		}
		finally
		{
			_quickActionInFlight = false;
		}
	}
}
