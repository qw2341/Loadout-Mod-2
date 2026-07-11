#nullable enable

using Loadout.UI.Managers;

namespace Loadout.UI.Config;

using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

public partial class NLoadoutSettingsButton : NButton
{
    private const string DefaultLabel = "Reset all permanent card modifications";

    private MegaLabel? _label;
    private Tween? _tween;

    public override void _Ready()
    {
        BuildControlTree();
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;
        ConnectSignals();
    }

    public override void _ExitTree()
    {
        _tween?.Kill();
        _tween = null;
        base._ExitTree();
    }

    public void ShowResetComplete()
    {
        if (_label is null)
            return;

        _label.SetTextAutoSize(LocMan.Loc(
            "CONFIG_PERMANENT_CARD_MODIFICATIONS_RESET",
            "Permanent card modifications reset"));
        GetTree().CreateTimer(1.4).Timeout += RestoreLabel;
    }

    protected override void OnFocus()
    {
        base.OnFocus();
        AnimateScale(Vector2.One * 1.05f, 0.05f);
    }

    protected override void OnUnfocus()
    {
        base.OnUnfocus();
        AnimateScale(Vector2.One, 0.5f);
    }

    protected override void OnPress()
    {
        base.OnPress();
        AnimateScale(Vector2.One * 0.95f, 0.25f);
    }

    protected override void OnRelease()
    {
        base.OnRelease();
        AnimateScale(Vector2.One, 0.05f);
    }

    private void BuildControlTree()
    {
        CustomMinimumSize = new Vector2(520f, 64f);
        PivotOffset = new Vector2(260f, 32f);

        TextureRect image = new()
        {
            Name = "Image",
            Texture = LoadTexture("res://images/ui/reward_screen/reward_skip_button.png"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore,
            PivotOffset = new Vector2(260f, 32f)
        };
        image.SetAnchorsPreset(LayoutPreset.FullRect);

        if (ResourceLoader.Exists("res://shaders/hsv.gdshader"))
        {
            ShaderMaterial material = new()
            {
                ResourceLocalToScene = true,
                Shader = GD.Load<Shader>("res://shaders/hsv.gdshader")
            };
            material.SetShaderParameter("h", 0.45f);
            material.SetShaderParameter("s", 1.5f);
            material.SetShaderParameter("v", 0.8f);
            image.Material = material;
        }

        AddChild(image);

        _label = new MegaLabel
        {
            Name = "Label",
            AutoSizeEnabled = true,
            MinFontSize = 18,
            MaxFontSize = 28,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _label.SetAnchorsPreset(LayoutPreset.FullRect);
        _label.AddThemeColorOverride("font_color", new Color(0.91f, 0.86359f, 0.7462f));
        _label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.25098f));
        _label.AddThemeColorOverride("font_outline_color", new Color(0.29f, 0.14703f, 0.1421f));
        _label.AddThemeConstantOverride("shadow_offset_x", 4);
        _label.AddThemeConstantOverride("shadow_offset_y", 3);
        _label.AddThemeConstantOverride("outline_size", 12);
        _label.AddThemeConstantOverride("shadow_outline_size", 0);
        _label.AddThemeFontOverride("font", LoadFont("res://themes/kreon_bold_glyph_space_two.tres"));
        _label.AddThemeFontSizeOverride("font_size", 28);
        AddChild(_label);
        RestoreLabel();
    }

    private void RestoreLabel()
    {
        if (GodotObject.IsInstanceValid(_label))
            _label!.SetTextAutoSize(LocMan.Loc("CONFIG_RESET_ALL_PERMA_MOD",DefaultLabel));
    }

    private void AnimateScale(Vector2 scale, float seconds)
    {
        _tween?.Kill();
        _tween = CreateTween();
        _tween.TweenProperty(this, "scale", scale, seconds)
            .SetEase(Tween.EaseType.Out)
            .SetTrans(Tween.TransitionType.Expo);
    }

    private static Texture2D? LoadTexture(string path)
    {
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
