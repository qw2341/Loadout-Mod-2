using Godot;
using Loadout.UI.Managers;

namespace Loadout.UI;

public partial class NLoadoutPanelButton : Button
{
	private static readonly StringName ShaderH = new("h");
	private static readonly StringName ShaderS = new("s");
	private static readonly StringName ShaderV = new("v");
	private static readonly StyleBoxEmpty EmptyStyle = new();

	private const string TabTextureFileName = "SidePanelTab.png";
	private const string ArrowTextureFileName = "SidePanelArrow.png";
	private const string HsvShaderPath = "res://shaders/hsv.gdshader";
	private const float RainbowSpeed = 0.12f;

	private NLoadoutPanel _nLoadoutPanel;
	private TextureRect _tabImage;
	private TextureRect _arrowImage;
	private ShaderMaterial _tabMaterial;
	private ShaderMaterial _arrowMaterial;
	private float _hue;
	private bool _signalsConnected;

	public override void _Ready()
	{
		_nLoadoutPanel = GetParent<NLoadoutPanel>();
		BuildVisuals();
		_nLoadoutPanel.VisibilityStateChanged += RefreshState;
		Pressed += OnPressed;
		Resized += OnResized;
		_signalsConnected = true;
		RefreshState();
	}

	public override void _ExitTree()
	{
		if (!_signalsConnected)
			return;

		Pressed -= OnPressed;
		Resized -= OnResized;
		_nLoadoutPanel.VisibilityStateChanged -= RefreshState;
		_signalsConnected = false;
	}

	public override void _Process(double delta)
	{
		_hue = Mathf.PosMod(_hue + (float)delta * RainbowSpeed, 1f);
		UpdateHue(_hue);
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
		_tabMaterial = CreateHsvMaterial();
		_arrowMaterial = CreateHsvMaterial();
		_tabImage.Material = _tabMaterial;
		_arrowImage.Material = _arrowMaterial;
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

	private void UpdateHue(float hue)
	{
		SetHue(_tabMaterial, hue, 1.15f, 1.05f);
		SetHue(_arrowMaterial, Mathf.PosMod(hue + 0.08f, 1f), 1.35f, 1.25f);

		if (_tabMaterial is null || _arrowMaterial is null)
		{
			Color fallback = Color.FromHsv(hue, 0.85f, 1f);
			_tabImage.Modulate = fallback;
			_arrowImage.Modulate = fallback.Lightened(0.25f);
		}
	}

	private static void SetHue(ShaderMaterial material, float hue, float saturation, float value)
	{
		if (material is null)
			return;

		material.SetShaderParameter(ShaderH, hue);
		material.SetShaderParameter(ShaderS, saturation);
		material.SetShaderParameter(ShaderV, value);
	}

	private static ShaderMaterial CreateHsvMaterial()
	{
		if (!ResourceLoader.Exists(HsvShaderPath))
			return null;

		ShaderMaterial material = new()
		{
			ResourceLocalToScene = true,
			Shader = GD.Load<Shader>(HsvShaderPath)
		};
		SetHue(material, 0f, 1f, 1f);
		return material;
	}

	private static Texture2D LoadPanelTexture(string fileName)
	{
		return LoadoutSkinManager.GetTexture(fileName);
	}
}
