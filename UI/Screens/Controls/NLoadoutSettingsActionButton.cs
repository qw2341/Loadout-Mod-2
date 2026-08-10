#nullable enable

namespace Loadout.UI.Screens.Controls;

using Godot;
using Loadout.UI;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;

public partial class NLoadoutSettingsActionButton : NSettingsButton
{
    private const string ReticleScenePath = "res://scenes/ui/selection_reticle.tscn";

    private MegaLabel? _label;
    private TextureRect? _image;
    private string _pendingLabel = string.Empty;
    private float _rainbowHue;

    public string ActionButtonId { get; private set; } = string.Empty;
    public bool UseRainbowColor { get; set; }
    public bool UseDangerColor { get; set; }

    public void Init(string id, string label)
    {
        ActionButtonId = id;
        _pendingLabel = label;
        if (_label is not null)
            _label.SetTextAutoSize(label);
    }

    public override void _Ready()
    {
        BuildControlTree();
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;
        ConnectSignals();
        _label?.SetTextAutoSize(_pendingLabel);
        SetProcess(UseRainbowColor);
    }

    public override void _Process(double delta)
    {
        if (!UseRainbowColor || _image is null)
            return;

        _rainbowHue = Mathf.PosMod(
            _rainbowHue + (float)delta * NLoadoutPanelButton.RainbowSpeed * Mathf.Tau,
            Mathf.Tau);
        _image.Modulate = NLoadoutPanelButton.GetSineRainbowColor(_rainbowHue);
    }

    private void BuildControlTree()
    {
        TextureRect image = new()
        {
            Name = "Image",
            Texture = LoadTexture("res://images/ui/reward_screen/reward_skip_button.png"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _image = image;
        image.SetAnchorsPreset(LayoutPreset.FullRect);
        if (UseRainbowColor)
        {
            image.Modulate = NLoadoutPanelButton.GetSineRainbowColor(0f);
        }
        else if (!UseDangerColor && ResourceLoader.Exists("res://shaders/hsv.gdshader"))
        {
            ShaderMaterial buttonMaterial = new()
            {
                ResourceLocalToScene = true,
                Shader = GD.Load<Shader>("res://shaders/hsv.gdshader")
            };
            buttonMaterial.SetShaderParameter("h", 0.82f);
            buttonMaterial.SetShaderParameter("s", 1.4f);
            buttonMaterial.SetShaderParameter("v", 0.8f);
            image.Material = buttonMaterial;
        }
        AddChild(image);

        _label = new MegaLabel
        {
            Name = "Label",
            AutoSizeEnabled = false,
            MinFontSize = 18,
            MaxFontSize = 28,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _label.SetAnchorsPreset(LayoutPreset.FullRect);
        _label.AddThemeColorOverride("font_color", new Color(0.91f, 0.86359f, 0.7462f));
        _label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.25098f));
        _label.AddThemeColorOverride("font_outline_color", new Color(0.1274f, 0.26f, 0.14066f));
        _label.AddThemeConstantOverride("shadow_offset_x", 4);
        _label.AddThemeConstantOverride("shadow_offset_y", 3);
        _label.AddThemeConstantOverride("outline_size", 12);
        _label.AddThemeFontOverride("font", LoadFont("res://themes/kreon_bold_glyph_space_two.tres"));
        _label.AddThemeFontSizeOverride("font_size", 28);
        AddChild(_label);

        NSelectionReticle reticle = CreateReticle();
        reticle.Name = "SelectionReticle";
        reticle.SetAnchorsPreset(LayoutPreset.FullRect);
        reticle.OffsetLeft = 0f;
        reticle.OffsetTop = 0f;
        reticle.OffsetRight = 0f;
        reticle.OffsetBottom = 0f;
        AddChild(reticle);
    }

    private static NSelectionReticle CreateReticle()
    {
        if (ResourceLoader.Exists(ReticleScenePath)
            && GD.Load<PackedScene>(ReticleScenePath) is { } scene
            && scene.Instantiate<NSelectionReticle>() is { } reticle)
        {
            return reticle;
        }

        return new NSelectionReticle { MouseFilter = MouseFilterEnum.Ignore };
    }

    private static Texture2D? LoadTexture(string path)
    {
        string localPath = path.Replace("res://images/", "res://Loadout/images/");
        if (ResourceLoader.Exists(localPath))
            return GD.Load<Texture2D>(localPath);
        return ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
    }

    private static Font? LoadFont(string path)
    {
        return ResourceLoader.Exists(path) ? GD.Load<Font>(path) : null;
    }
}
