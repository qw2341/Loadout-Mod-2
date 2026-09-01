#nullable enable

namespace Loadout.UI;

using Godot;
using Loadout.Services.OneEvent;

public partial class NOneEventSelectionVisual : Node
{
    public const string NodeName = "OneEventSelectionVisual";
    private const string OutlineName = "OneEventRainbowOutline";
    private const string PaintName = "OneEventRainbowPaint";
    private const float PaintAlpha = 0.42f;

    private ReferenceRect? _outline;
    private ColorRect? _paint;
    private Control? _title;
    private Control? _epithet;
    private Color _titleColor;
    private Color _epithetColor;
    private float _phase;
    private OneEventMode _mode;

    public static void Apply(Control view, OneEventMode mode)
    {
        view.SelfModulate = Colors.White;

        NOneEventSelectionVisual? visual = view.GetNodeOrNull<NOneEventSelectionVisual>(NodeName);
        if (visual is null && mode != OneEventMode.Off)
        {
            visual = new NOneEventSelectionVisual { Name = NodeName };
            view.AddChild(visual);
            visual.Initialize(view);
        }
        visual?.ApplyMode(mode);
    }

    public override void _Process(double delta)
    {
        _phase = Mathf.PosMod(
            _phase + (float)delta * NLoadoutPanelButton.RainbowSpeed * Mathf.Tau,
            Mathf.Tau);
        Color color = NLoadoutPanelButton.GetSineRainbowColor(_phase);
        if (_outline is not null && IsInstanceValid(_outline))
            _outline.BorderColor = color;
        if (_mode == OneEventMode.All)
        {
            if (_paint is not null && IsInstanceValid(_paint))
                _paint.Color = new Color(color.R, color.G, color.B, PaintAlpha);
            ApplyFontColor(color);
        }
    }

    private void Initialize(Control view)
    {
        _title = view.GetNodeOrNull<Control>("EventTitle");
        _epithet = view.GetNodeOrNull<Control>("AncientEpithet");
        if (_title is not null)
            _titleColor = _title.GetThemeColor("font_color");
        if (_epithet is not null)
            _epithetColor = _epithet.GetThemeColor("font_color");

        _paint = new ColorRect
        {
            Name = PaintName,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Color = Colors.Transparent
        };
        _paint.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        view.AddChild(_paint);
        if (_title is not null)
            view.MoveChild(_paint, _title.GetIndex());

        _outline = new ReferenceRect
        {
            Name = OutlineName,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            BorderColor = NLoadoutPanelButton.GetSineRainbowColor(0f),
            BorderWidth = 7f,
            EditorOnly = false,
            ZIndex = 900
        };
        _outline.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        view.AddChild(_outline);
    }

    private void ApplyMode(OneEventMode mode)
    {
        _mode = mode;
        if (_outline is not null && IsInstanceValid(_outline))
            _outline.Visible = mode != OneEventMode.Off;
        if (_paint is not null && IsInstanceValid(_paint))
            _paint.Visible = mode == OneEventMode.All;

        if (mode == OneEventMode.All)
        {
            Color color = NLoadoutPanelButton.GetSineRainbowColor(_phase);
            if (_paint is not null && IsInstanceValid(_paint))
                _paint.Color = new Color(color.R, color.G, color.B, PaintAlpha);
            ApplyFontColor(color);
        }
        else
        {
            RestoreFontColors();
        }

        SetProcess(mode != OneEventMode.Off);
    }

    private void ApplyFontColor(Color color)
    {
        if (_title is not null && IsInstanceValid(_title))
            _title.AddThemeColorOverride("font_color", color);
        if (_epithet is not null && IsInstanceValid(_epithet))
            _epithet.AddThemeColorOverride("font_color", color);
    }

    private void RestoreFontColors()
    {
        if (_title is not null && IsInstanceValid(_title))
            _title.AddThemeColorOverride("font_color", _titleColor);
        if (_epithet is not null && IsInstanceValid(_epithet))
            _epithet.AddThemeColorOverride("font_color", _epithetColor);
    }
}
