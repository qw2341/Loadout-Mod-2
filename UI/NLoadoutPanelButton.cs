using Godot;
using Loadout.UI.Managers;
using MegaCrit.Sts2.Core.Audio;
using MegaCrit.Sts2.Core.Commands;

namespace Loadout.UI;

public partial class NLoadoutPanelButton : Button
{
	private static readonly StyleBoxEmpty EmptyStyle = new();

	private const string TabTextureFileName = "SidePanelTab.png";
	private const string ArrowTextureFileName = "SidePanelArrow.png";
	private const float RainbowSpeed = 0.12f;

	private NLoadoutPanel _nLoadoutPanel;
	private TextureRect _tabImage;
	private TextureRect _arrowImage;
	private float _rainbowPhase;
	private bool _signalsConnected;

	public override void _Ready()
	{
		_nLoadoutPanel = GetParent<NLoadoutPanel>();
		BuildVisuals();
		_nLoadoutPanel.VisibilityStateChanged += RefreshState;
		Pressed += OnPressed;
		MouseEntered += OnMouseEntered;
		Resized += OnResized;
		_signalsConnected = true;
		RefreshState();
	}

	public override void _ExitTree()
	{
		if (!_signalsConnected)
			return;

		Pressed -= OnPressed;
		MouseEntered -= OnMouseEntered;
		Resized -= OnResized;
		_nLoadoutPanel.VisibilityStateChanged -= RefreshState;
		_signalsConnected = false;
	}

	public override void _Process(double delta)
	{
		_rainbowPhase = Mathf.PosMod(_rainbowPhase + (float)delta * RainbowSpeed * Mathf.Tau, Mathf.Tau);
		UpdateRainbowColor(_rainbowPhase);
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

		_tabImage = GetNodeOrNull<TextureRect>("TabImage") ?? CreateTextureRect("TabImage", true);
		_arrowImage = GetNodeOrNull<TextureRect>("ArrowImage") ?? CreateTextureRect("ArrowImage", false);

		_tabImage.Texture = LoadPanelTexture(TabTextureFileName);
		_arrowImage.Texture = LoadPanelTexture(ArrowTextureFileName);
		_tabImage.Material = null;
		_arrowImage.Material = null;
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
		if (!Disabled)
			SfxCmd.Play(FmodSfx.uiHover);
	}

	private void RefreshState()
	{
		Disabled = _nLoadoutPanel.Hidden;
		Modulate = Disabled ? new Color(1f, 1f, 1f, 0.55f) : Colors.White;

		if (_arrowImage is null || !IsInstanceValid(_arrowImage))
			return;

		_arrowImage.Rotation = _nLoadoutPanel.Shown ? Mathf.Pi : 0f;
	}

	private void OnResized()
	{
		if (_tabImage is not null && IsInstanceValid(_tabImage))
		{
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
