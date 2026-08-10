#nullable enable

namespace Loadout.UI.CustomRuns;

using Godot;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

public partial class NCustomRunDeleteButton : NButton
{
    private TextureRect? _image;
    private Tween? _tween;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(62f, 58f);
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;
        PivotOffset = CustomMinimumSize * 0.5f;

        _image = new TextureRect
        {
            Name = "Image",
            Texture = LoadTexture("res://images/packed/main_menu/delete_button.png"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _image.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_image);
        ConnectSignals();
    }

    public override void _ExitTree()
    {
        _tween?.Kill();
        _tween = null;
        base._ExitTree();
    }

    protected override void OnFocus()
    {
        base.OnFocus();
        Animate(new Vector2(1.1f, 1.1f), new Color(1.2f, 0.8f, 0.75f, 1f), 0.14f);
    }

    protected override void OnUnfocus()
    {
        base.OnUnfocus();
        Animate(Vector2.One, Colors.White, 0.22f);
    }

    protected override void OnPress()
    {
        base.OnPress();
        Animate(new Vector2(0.94f, 0.94f), new Color(1.35f, 0.65f, 0.6f, 1f), 0.08f);
    }

    protected override void OnRelease()
    {
        base.OnRelease();
        Animate(new Vector2(1.1f, 1.1f), new Color(1.2f, 0.8f, 0.75f, 1f), 0.12f);
    }

    private void Animate(Vector2 scale, Color color, float duration)
    {
        if (_image is null)
            return;
        _tween?.Kill();
        _tween = CreateTween().SetParallel();
        _tween.TweenProperty(this, "scale", scale, duration)
            .SetEase(Tween.EaseType.Out)
            .SetTrans(Tween.TransitionType.Expo);
        _tween.TweenProperty(_image, "modulate", color, duration);
    }

    private static Texture2D? LoadTexture(string path)
    {
        string localPath = path.Replace("res://images/", "res://Loadout/images/");
        if (ResourceLoader.Exists(localPath))
            return GD.Load<Texture2D>(localPath);
        return ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
    }
}
