#nullable enable

namespace Loadout.UI.Screens.Controls;

using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

public partial class NLoadoutActionButton : NButton
{
    private static readonly StringName ShaderH = new("h");
    private static readonly StringName ShaderS = new("s");
    private static readonly StringName ShaderV = new("v");

    private const float ButtonHeight = 42f;
    private const float ContentHeight = 48f;
    private const float IconSize = 34f;

    private Control? _buttonImage;
    private ShaderMaterial? _hsv;
    private TextureRect? _icon;
    private MegaLabel? _label;
    private Tween? _tween;
    private string _pendingLabel = string.Empty;
    private Texture2D? _pendingIcon;

    public string ActionButtonId { get; private set; } = string.Empty;

    public void Init(string id, string label, Texture2D? icon = null)
    {
        ActionButtonId = id;
        _pendingLabel = label;
        _pendingIcon = icon;

        if (!IsNodeReady())
            return;

        ApplyLabelAndIcon();
    }

    public override void _Ready()
    {
        BuildControlTree();
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;
        ConnectSignals();
        ApplyLabelAndIcon();
    }

    public override void _ExitTree()
    {
        _tween?.Kill();
        _tween = null;
        base._ExitTree();
    }

    protected override void OnRelease()
    {
        base.OnRelease();
        AnimateVisuals(new Vector2(1.05f, 1.05f), 1f, 1f, 0.18f);
    }

    protected override void OnFocus()
    {
        base.OnFocus();
        AnimateVisuals(new Vector2(1.05f, 1.05f), 1f, 1f, 0.18f);
    }

    protected override void OnUnfocus()
    {
        base.OnUnfocus();
        AnimateVisuals(new Vector2(1.05f, 1f), 0.8f, 0.8f, 0.3f);
    }

    protected override void OnPress()
    {
        base.OnPress();
        AnimateVisuals(new Vector2(1f, 0.95f), 0.8f, 0.8f, 0.18f);
    }

    private void BuildControlTree()
    {
        CustomMinimumSize = new Vector2(0f, ButtonHeight);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;

        if (GetNodeOrNull<Control>("ButtonImage") is not { } buttonImage)
        {
            TextureRect image = new()
            {
                Name = "ButtonImage",
                UniqueNameInOwner = true,
                Modulate = new Color(0.8f, 0.8f, 0.8f, 1f),
                Texture = LoadTexture("res://images/ui/reward_screen/reward_item_button.png"),
                ExpandMode = TextureRect.ExpandModeEnum.FitWidth,
                MouseFilter = MouseFilterEnum.Ignore,
                Scale = new Vector2(1.05f, 1f),
                PivotOffset = new Vector2(128f, 24f)
            };
            image.SetAnchorsPreset(LayoutPreset.FullRect);

            ShaderMaterial? material = CreateHsvMaterial();
            if (material is not null)
                image.Material = material;

            AddChild(image);
            buttonImage = image;
        }

        _buttonImage = buttonImage;
        _hsv = _buttonImage.Material as ShaderMaterial;

        HBoxContainer content = GetNodeOrNull<HBoxContainer>("HBoxContainer") ?? CreateContentContainer();
        _icon = content.GetNodeOrNull<TextureRect>("Icon") ?? CreateIcon(content);
        _label = content.GetNodeOrNull<MegaLabel>("Label") ?? CreateLabel(content);
    }

    private HBoxContainer CreateContentContainer()
    {
        HBoxContainer content = new()
        {
            Name = "HBoxContainer",
            CustomMinimumSize = new Vector2(0f, ContentHeight),
            MouseFilter = MouseFilterEnum.Ignore
        };
        content.SetAnchorsPreset(LayoutPreset.FullRect);
        content.OffsetLeft = 10f;
        content.OffsetRight = -10f;
        content.AddThemeConstantOverride("separation", 9);
        AddChild(content);
        return content;
    }

    private static TextureRect CreateIcon(HBoxContainer content)
    {
        TextureRect icon = new()
        {
            Name = "Icon",
            CustomMinimumSize = new Vector2(IconSize, IconSize),
            ExpandMode = TextureRect.ExpandModeEnum.FitWidthProportional,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        content.AddChild(icon);
        return icon;
    }

    private static MegaLabel CreateLabel(HBoxContainer content)
    {
        MegaLabel label = new()
        {
            Name = "Label",
            UniqueNameInOwner = true,
            CustomMinimumSize = new Vector2(0f, ContentHeight),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            AutoSizeEnabled = false,
            MinFontSize = 16,
            MaxFontSize = 22,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        label.AddThemeColorOverride("font_color", StsColors.gold);
        label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.501961f));
        label.AddThemeConstantOverride("outline_size", 12);
        label.AddThemeConstantOverride("shadow_outline_size", 0);
        label.AddThemeFontOverride("font", LoadFont("res://themes/kreon_bold_glyph_space_one.tres"));
        label.AddThemeFontSizeOverride("font_size", 22);
        content.AddChild(label);
        return label;
    }

    private void ApplyLabelAndIcon()
    {
        if (_label is not null)
            _label.SetTextAutoSize(_pendingLabel);

        if (_icon is null)
            return;

        _icon.Texture = _pendingIcon;
        _icon.Visible = _pendingIcon is not null;
    }

    private void AnimateVisuals(Vector2 scale, float saturation, float value, float seconds)
    {
        if (_buttonImage is null)
            return;

        _tween?.Kill();
        _tween = CreateTween().SetParallel();
        _tween.TweenProperty(_buttonImage, "scale", scale, seconds);

        if (_hsv is null)
            return;

        _tween.TweenMethod(Callable.From<float>(UpdateShaderS), (float)_hsv.GetShaderParameter(ShaderS), saturation, seconds)
            .SetEase(Tween.EaseType.Out)
            .SetTrans(Tween.TransitionType.Expo);
        _tween.TweenMethod(Callable.From<float>(UpdateShaderV), (float)_hsv.GetShaderParameter(ShaderV), value, seconds)
            .SetEase(Tween.EaseType.Out)
            .SetTrans(Tween.TransitionType.Expo);
    }

    private void UpdateShaderS(float value)
    {
        _hsv?.SetShaderParameter(ShaderS, value);
    }

    private void UpdateShaderV(float value)
    {
        _hsv?.SetShaderParameter(ShaderV, value);
    }

    private static ShaderMaterial? CreateHsvMaterial()
    {
        if (!ResourceLoader.Exists("res://shaders/hsv.gdshader"))
            return null;

        ShaderMaterial material = new()
        {
            ResourceLocalToScene = true,
            Shader = GD.Load<Shader>("res://shaders/hsv.gdshader")
        };
        material.SetShaderParameter(ShaderH, 1f);
        material.SetShaderParameter(ShaderS, 0.8f);
        material.SetShaderParameter(ShaderV, 0.8f);
        return material;
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
        string localPath = path.Replace("res://themes/", "res://Loadout/themes/default/");
        if (ResourceLoader.Exists(localPath))
            return GD.Load<Font>(localPath);

        return ResourceLoader.Exists(path) ? GD.Load<Font>(path) : null;
    }
}
