#nullable enable

namespace Loadout.UI.Screens.Controls;

using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;

public partial class NLoadoutToggle : Control
{
    [Signal]
    public delegate void ToggledEventHandler(NLoadoutToggle toggle);

    private const float RowHeight = 56f;
    private const float TickboxSize = 64f;
    private const float LabelLeft = 60f;
    private const float BaseTickboxScale = 0.56f;
    private const float HoverTickboxScale = 0.62f;
    private const float PressTickboxScale = 0.50f;

    private Control? _tickboxVisuals;
    private TextureRect? _tickedImage;
    private TextureRect? _notTickedImage;
    private MegaLabel? _label;
    private Tween? _tween;
    private bool _signalsConnected;

    public string ToggleId { get; private set; } = string.Empty;

    public bool IsChecked { get; private set; }

    public void Init(string id, string label, bool checkedByDefault)
    {
        ToggleId = id;
        IsChecked = checkedByDefault;

        if (IsNodeReady())
        {
            SetLabel(label);
            RefreshVisualState();
        }
        else
        {
            _pendingLabel = label;
        }
    }

    private string _pendingLabel = string.Empty;

    public override void _Ready()
    {
        EnsureControlTree();
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;

        MouseEntered += OnHoverStart;
        MouseExited += OnHoverEnd;
        FocusEntered += OnHoverStart;
        FocusExited += OnHoverEnd;
        GuiInput += OnGuiInput;
        _signalsConnected = true;

        SetLabel(_pendingLabel);
        RefreshVisualState();
    }

    public override void _ExitTree()
    {
        if (_signalsConnected)
        {
            MouseEntered -= OnHoverStart;
            MouseExited -= OnHoverEnd;
            FocusEntered -= OnHoverStart;
            FocusExited -= OnHoverEnd;
            GuiInput -= OnGuiInput;
            _signalsConnected = false;
        }

        if (_tween is not null && GodotObject.IsInstanceValid(_tween))
            _tween.Kill();

        _tween = null;
    }

    public void SetChecked(bool isChecked, bool emit = false)
    {
        if (IsChecked == isChecked)
            return;

        IsChecked = isChecked;
        RefreshVisualState();

        if (emit)
            EmitSignal(SignalName.Toggled, this);
    }

    public void SetLabel(string label)
    {
        _pendingLabel = label;
        if (_label is not null)
            _label.Text = label;
    }

    private void OnGuiInput(InputEvent inputEvent)
    {
        if (inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.Left } mouseButton)
        {
            if (mouseButton.Pressed)
            {
                AnimateTickbox(PressTickboxScale);
                AcceptEvent();
                return;
            }

            SetChecked(!IsChecked, emit: true);
            AnimateTickbox(GetGlobalRect().HasPoint(mouseButton.GlobalPosition) ? HoverTickboxScale : BaseTickboxScale);
            AcceptEvent();
            return;
        }

        if (inputEvent.IsActionPressed("ui_accept"))
        {
            SetChecked(!IsChecked, emit: true);
            AcceptEvent();
        }
    }

    private void OnHoverStart()
    {
        AnimateTickbox(HoverTickboxScale);
    }

    private void OnHoverEnd()
    {
        if (!HasFocus())
            AnimateTickbox(BaseTickboxScale);
    }

    private void AnimateTickbox(float scale)
    {
        if (_tickboxVisuals is null)
            return;

        _tween?.Kill();
        _tween = CreateTween();
        _tween.TweenProperty(_tickboxVisuals, "scale", Vector2.One * scale, 0.08f);
    }

    private void RefreshVisualState()
    {
        if (_tickedImage is not null)
            _tickedImage.Visible = IsChecked;

        if (_notTickedImage is not null)
            _notTickedImage.Visible = !IsChecked;
    }

    private void EnsureControlTree()
    {
        float rowHeight = CustomMinimumSize.Y > 0f ? CustomMinimumSize.Y : RowHeight;
        bool hasRequestedWidth = CustomMinimumSize.X > 0f;
        float rowWidth = hasRequestedWidth ? CustomMinimumSize.X : 256f;
        CustomMinimumSize = new Vector2(rowWidth, rowHeight);
        SizeFlagsHorizontal = hasRequestedWidth ? SizeFlags.ShrinkBegin : SizeFlags.ExpandFill;

        _tickboxVisuals = GetNodeOrNull<Control>("TickboxVisuals");
        if (_tickboxVisuals is null)
        {
            Control visuals = new()
            {
                Name = "TickboxVisuals",
                Position = new Vector2(-4f, (rowHeight - TickboxSize) * 0.5f),
                Size = Vector2.One * TickboxSize,
                PivotOffset = Vector2.One * (TickboxSize * 0.5f),
                Scale = Vector2.One * BaseTickboxScale,
                MouseFilter = MouseFilterEnum.Ignore
            };
            AddChild(visuals);
            _tickboxVisuals = visuals;
        }

        _notTickedImage = _tickboxVisuals.GetNodeOrNull<TextureRect>("NotTicked");
        if (_notTickedImage is null)
        {
            TextureRect notTicked = CreateTickboxImage("NotTicked", "res://images/atlases/ui_atlas.sprites/checkbox_unticked.tres");
            _tickboxVisuals.AddChild(notTicked);
            _notTickedImage = notTicked;
        }

        _tickedImage = _tickboxVisuals.GetNodeOrNull<TextureRect>("Ticked");
        if (_tickedImage is null)
        {
            TextureRect ticked = CreateTickboxImage("Ticked", "res://images/atlases/ui_atlas.sprites/checkbox_ticked.tres");
            _tickboxVisuals.AddChild(ticked);
            _tickedImage = ticked;
        }

        _label = GetNodeOrNull<MegaLabel>("Label");
        if (_label is not null)
            return;

        MegaLabel label = new()
        {
            Name = "Label",
            AutoSizeEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        label.SetAnchorsPreset(LayoutPreset.FullRect);
        label.OffsetLeft = LabelLeft;
        label.OffsetRight = -4f;
        label.AddThemeColorOverride("font_color", StsColors.gold);
        label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.45f));
        label.AddThemeConstantOverride("shadow_offset_x", 4);
        label.AddThemeConstantOverride("shadow_offset_y", 3);
        label.AddThemeFontOverride("font", LoadGameFont("res://themes/kreon_bold_glyph_space_one.tres"));
        label.AddThemeFontSizeOverride("font_size", rowHeight < RowHeight ? 21 : 24);
        AddChild(label);
        _label = label;
    }

    private static TextureRect CreateTickboxImage(string name, string texturePath)
    {
        TextureRect image = new()
        {
            Name = name,
            Texture = LoadGameTexture(texturePath),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        image.SetAnchorsPreset(LayoutPreset.FullRect);
        return image;
    }

    private static Texture2D? LoadGameTexture(string path)
    {
        string localPath = path.Replace("res://images/atlases/", "res://Loadout/images/atlases/");
        if (ResourceLoader.Exists(localPath))
            return GD.Load<Texture2D>(localPath);

        return ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
    }

    private static Font? LoadGameFont(string path)
    {
        string localPath = path.Replace("res://themes/", "res://Loadout/themes/default/");
        if (ResourceLoader.Exists(localPath))
            return GD.Load<Font>(localPath);

        return ResourceLoader.Exists(path) ? GD.Load<Font>(path) : null;
    }
}
