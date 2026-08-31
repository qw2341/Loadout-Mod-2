#nullable enable

namespace Loadout.UI;

using System;
using Godot;
using MegaCrit.Sts2.Core.Assets;

public partial class NOneEventSelectionVisual : Node
{
    public const string NodeName = "OneEventSelectionVisual";
    public const string RareGlowScenePath = NOneRelicSelectionVisual.RareGlowScenePath;
    private const string OutlineName = "OneEventRainbowOutline";

    private ReferenceRect? _outline;
    private GpuParticles2D? _glow;
    private float _phase;

    public static void Apply(Control view, bool selected, bool dimmed)
    {
        Color selfModulate = Colors.White;
        selfModulate.A = dimmed ? 0.9f : 1f;
        view.SelfModulate = selfModulate;

        NOneEventSelectionVisual? visual = view.GetNodeOrNull<NOneEventSelectionVisual>(NodeName);
        if (visual is null && selected)
        {
            visual = new NOneEventSelectionVisual { Name = NodeName };
            view.AddChild(visual);
            visual.Initialize(view);
        }
        visual?.ApplySelection(selected);
    }

    public override void _Process(double delta)
    {
        _phase = Mathf.PosMod(
            _phase + (float)delta * NLoadoutPanelButton.RainbowSpeed * Mathf.Tau,
            Mathf.Tau);
        Color color = NLoadoutPanelButton.GetSineRainbowColor(_phase);
        if (_outline is not null && IsInstanceValid(_outline))
            _outline.BorderColor = color;
        if (_glow is not null && IsInstanceValid(_glow))
            _glow.Modulate = color;
    }

    private void Initialize(Control view)
    {
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

        try
        {
            _glow = PreloadManager.Cache.GetScene(RareGlowScenePath)
                .Instantiate<GpuParticles2D>(PackedScene.GenEditState.Disabled);
            _glow.Name = "OneEventRainbowGlow";
            _glow.ShowBehindParent = true;
            Vector2 bounds = view.Size.X > 0f && view.Size.Y > 0f
                ? view.Size
                : view.CustomMinimumSize;
            _glow.Position = bounds * 0.5f;
            _glow.Scale = new Vector2(0.65f, 0.42f);
            view.AddChild(_glow);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"OneEvent: could not create the native rare treasure glow. {exception.Message}");
        }
    }

    private void ApplySelection(bool selected)
    {
        if (_outline is not null && IsInstanceValid(_outline))
            _outline.Visible = selected;
        if (_glow is not null && IsInstanceValid(_glow))
        {
            _glow.Visible = selected;
            _glow.Emitting = selected;
            if (selected)
                _glow.Restart();
        }
        SetProcess(selected);
    }
}
