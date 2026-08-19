#nullable enable

namespace Loadout.UI.Screens.Controls;

using Godot;
using Loadout.UI;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

// NMapShareButton is beta-only; this keeps its behavior on the stable clickable base.
public partial class NLoadoutMapShareButton : NClickableControl
{
    public const string DefaultButtonTexturePath = "res://images/packed/statistics_screen/share_button.png";
    public const string DefaultIconTexturePath = "res://images/packed/statistics_screen/share_stats.png";

    public static readonly Color NativeHoverColor = new(0.482353f, 0.105882f, 0.082353f);
    public static readonly Color NativeDefaultColor = new(0f, 0f, 0f, 0.7529412f);
    public static readonly Color NativeTextColor = new(1f, 0.964706f, 0.886275f);
    public static readonly Color NativeTextOutlineColor = new(0f, 0f, 0f, 0.5019608f);

    private TextureRect? _buttonImage;
    private TextureRect? _icon;
    private MegaLabel? _label;
    private MarginContainer? _labelContainer;
    private Tween? _tween;
    private Texture2D? _iconTexture;
    private string _text = string.Empty;
    private Color _hoverColor = NativeHoverColor;
    private Color _defaultColor = NativeDefaultColor;
    private Color _textColor = NativeTextColor;
    private Color _textOutlineColor = NativeTextOutlineColor;
    private float _rainbowPhase;
    private bool _hasCustomText;
    private bool _useRainbowHoverColor;

    public Texture2D? IconTexture
    {
        get => _iconTexture;
        set
        {
            _iconTexture = value;
            if (_icon is not null)
                _icon.Texture = value ?? LoadTexture(DefaultIconTexturePath);
        }
    }

    public string Text
    {
        get => _text;
        set
        {
            _text = value;
            _hasCustomText = true;
            _label?.SetTextAutoSize(value);
        }
    }

    public Color HoverColor
    {
        get => _hoverColor;
        set
        {
            _hoverColor = value;
            RefreshCurrentColors();
        }
    }

    public Color DefaultColor
    {
        get => _defaultColor;
        set
        {
            _defaultColor = value;
            RefreshCurrentColors();
        }
    }

    public Color TextColor
    {
        get => _textColor;
        set
        {
            _textColor = value;
            _label?.AddThemeColorOverride("font_color", value);
        }
    }

    public Color TextOutlineColor
    {
        get => _textOutlineColor;
        set
        {
            _textOutlineColor = value;
            _label?.AddThemeColorOverride("font_outline_color", value);
        }
    }

    public bool UseRainbowHoverColor
    {
        get => _useRainbowHoverColor;
        set
        {
            _useRainbowHoverColor = value;
            SetProcess(value && IsFocused);
            RefreshCurrentColors();
        }
    }

    public void Init(string text, Texture2D? iconTexture = null)
    {
        Text = text;
        if (iconTexture is not null)
            IconTexture = iconTexture;
    }

    public override void _Ready()
    {
        BuildControlTree();
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;
        ConnectSignals();
        SetProcess(false);
        RefreshVisuals();
    }

    public override void _Process(double delta)
    {
        if (!UseRainbowHoverColor || !IsFocused || _buttonImage is null)
            return;

        _rainbowPhase = Mathf.PosMod(
            _rainbowPhase + (float)delta * NLoadoutPanelButton.RainbowSpeed * Mathf.Tau,
            Mathf.Tau);
        _buttonImage.Modulate = NLoadoutPanelButton.GetSineRainbowColor(_rainbowPhase);
    }

    public override void _ExitTree()
    {
        _tween?.Kill();
        _tween = null;
        base._ExitTree();
    }

    protected override void OnFocus()
    {
        SfxCmd.Play("event:/sfx/ui/clicks/ui_hover");
        SetProcess(UseRainbowHoverColor);
        Color color = UseRainbowHoverColor
            ? NLoadoutPanelButton.GetSineRainbowColor(_rainbowPhase)
            : HoverColor;
        AnimateState(Vector2.One * 1.05f, color, Colors.White, 0.05);
    }

    protected override void OnUnfocus()
    {
        SetProcess(false);
        AnimateState(Vector2.One, DefaultColor, new Color(1f, 1f, 1f, 0.5019608f), 0.1);
    }

    protected override void OnRelease()
    {
    }

    private void BuildControlTree()
    {
        CustomMinimumSize = new Vector2(180f, 64f);
        Size = CustomMinimumSize;
        PivotOffset = Size * 0.5f;
        Texture2D buttonTexture = LoadTexture(DefaultButtonTexturePath) ?? CreateFallbackButtonTexture();

        _buttonImage = new TextureRect
        {
            Name = "ButtonImage",
            Texture = buttonTexture,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _buttonImage.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_buttonImage);

        TextureRect shadow = new()
        {
            Name = "Shadow",
            Texture = _buttonImage.Texture,
            Modulate = new Color(0f, 0f, 0f, 0.5019608f),
            ShowBehindParent = true,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            MouseFilter = MouseFilterEnum.Ignore
        };
        shadow.SetAnchorsPreset(LayoutPreset.FullRect);
        shadow.OffsetLeft = 6f;
        shadow.OffsetTop = 4f;
        shadow.OffsetRight = 6f;
        shadow.OffsetBottom = 4f;
        _buttonImage.AddChild(shadow);

        _labelContainer = new MarginContainer
        {
            Name = "LabelContainer",
            MouseFilter = MouseFilterEnum.Ignore,
            UseParentMaterial = true
        };
        _labelContainer.SetAnchorsPreset(LayoutPreset.FullRect);
        _labelContainer.AddThemeConstantOverride("margin_left", 8);
        _labelContainer.AddThemeConstantOverride("margin_right", 8);
        AddChild(_labelContainer);

        HBoxContainer row = new()
        {
            Name = "HBoxContainer",
            MouseFilter = MouseFilterEnum.Ignore
        };
        row.AddThemeConstantOverride("separation", 4);
        _labelContainer.AddChild(row);

        _icon = new TextureRect
        {
            Name = "Icon",
            CustomMinimumSize = new Vector2(48f, 48f),
            Texture = IconTexture ?? LoadTexture(DefaultIconTexturePath),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        row.AddChild(_icon);

        _label = new MegaLabel
        {
            Name = "Label",
            CustomMinimumSize = new Vector2(64f, 64f),
            MinFontSize = 8,
            MaxFontSize = 28,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _label.AddThemeConstantOverride("shadow_offset_x", 4);
        _label.AddThemeConstantOverride("shadow_offset_y", 3);
        _label.AddThemeConstantOverride("outline_size", 12);
        _label.AddThemeConstantOverride("shadow_outline_size", 0);
        _label.AddThemeFontOverride("font", LoadFont("res://themes/kreon_bold_glyph_space_two.tres"));
        _label.AddThemeFontSizeOverride("font_size", 28);
        row.AddChild(_label);
    }

    private void RefreshVisuals()
    {
        if (!_hasCustomText)
            _text = new LocString("map", "SHARE.title").GetFormattedText();
        _label?.SetTextAutoSize(Text);

        if (_icon is not null)
            _icon.Texture = IconTexture ?? LoadTexture(DefaultIconTexturePath);
        _label?.AddThemeColorOverride("font_color", TextColor);
        _label?.AddThemeColorOverride("font_outline_color", TextOutlineColor);
        RefreshCurrentColors();
    }

    private void RefreshCurrentColors()
    {
        if (_buttonImage is null || _labelContainer is null)
            return;

        if (IsFocused)
        {
            _buttonImage.Modulate = UseRainbowHoverColor
                ? NLoadoutPanelButton.GetSineRainbowColor(_rainbowPhase)
                : HoverColor;
            _labelContainer.Modulate = Colors.White;
        }
        else
        {
            _buttonImage.Modulate = DefaultColor;
            _labelContainer.Modulate = new Color(1f, 1f, 1f, 0.5019608f);
        }
    }

    private void AnimateState(Vector2 scale, Color buttonColor, Color labelColor, double seconds)
    {
        if (_buttonImage is null || _labelContainer is null)
            return;

        _tween?.Kill();
        _tween = CreateTween().SetParallel();
        _tween.TweenProperty(this, "scale", scale, seconds);
        _tween.TweenProperty(_buttonImage, "modulate", buttonColor, seconds);
        _tween.TweenProperty(_labelContainer, "modulate", labelColor, seconds);
    }

    private static Texture2D? LoadTexture(string path)
    {
        return ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
    }

    private static Font? LoadFont(string path)
    {
        return ResourceLoader.Exists(path) ? GD.Load<Font>(path) : null;
    }

    private static Texture2D CreateFallbackButtonTexture()
    {
        Image image = Image.CreateEmpty(2, 2, false, Image.Format.Rgba8);
        image.Fill(Colors.White);
        return ImageTexture.CreateFromImage(image);
    }
}
