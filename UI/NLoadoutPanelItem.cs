using Godot;
using Loadout.UI.Managers;
using Loadout.UI.Screens;

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
	public int MinimumSizeY = 100;
	
	private PanelItemAnimationProfile _animationProfile;
	private float _hoverProgress;
	private bool _isHovered;
	private bool _isInsideContainer;
	private Vector2 _baseScale = Vector2.One;
	private Vector2 _basePosition;
	private NLoadoutSelectScreen _boundScreen;
	
	public override void _Ready()
	{
		_isInsideContainer = GetParent() is Container;
		_baseScale = Scale;
		_basePosition = Position;

		IgnoreTextureSize = true;
		StretchMode = StretchModeEnum.KeepCentered;
		SetCustomMinimumSize(new Vector2(0,MinimumSizeY));

		_animationProfile = ResolveAnimationProfile();
		ApplySkinTexture();
		UpdatePivotOffset();
		ApplyAnimationVisuals();

		MouseEntered += OnMouseEntered;
		MouseExited += OnMouseExited;
		Resized += OnResized;
		Pressed += OnLeftClick;

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
		Pressed -= OnLeftClick;

		if (UseGlobalSkin)
			LoadoutSkinManager.SkinChanged -= OnSkinChanged;

		if (UseGlobalAnimation)
			LoadoutPanelItemAnimationManager.AnimationChanged -= OnAnimationChanged;
	}

	public override void _Process(double delta)
	{
		float target = _isHovered ? 1f : 0f;
		_hoverProgress = LoadoutPanelItemAnimationManager.StepProgress(
			_hoverProgress,
			target,
			_animationProfile,
			(float)delta);

		ApplyAnimationVisuals();
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

	private void OnMouseEntered()
	{
		_isHovered = true;
	}

	private void OnMouseExited()
	{
		_isHovered = false;
	}

	private void OnResized()
	{
		UpdatePivotOffset();
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
	}

	private void OnLeftClick()
	{
		//open select screen
		if (_boundScreen == null)
		{
			var scene = GD.Load<PackedScene>("res://UI/Screens/SampleSelectScreen.tscn");
			_boundScreen = scene.Instantiate<NLoadoutSelectScreen>();
		}
		var root = GetNode<NLoadoutPanelRoot>("/root/LoadoutPanelRoot");
		root.OpenScreen(_boundScreen);
	}

	public NLoadoutSelectScreen BoundScreen
	{
		get => _boundScreen;
		set => _boundScreen = value;
	}
}
