#nullable enable

namespace Loadout.UI;

using System;
using Godot;
using Loadout.PanelItems;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Nodes.Relics;

public partial class NOneRelicSelectionVisual : Node
{
    public const string NodeName = "OneRelicSelectionVisual";
    public const string RareGlowScenePath = "res://scenes/vfx/relic_rare_glow_vfx.tscn";

    private ReferenceRect? _outline;
    private GpuParticles2D? _glow;
    private float _phase;

    public static void Apply(Control view, bool selected, bool dimmed)
    {
        Color selfModulate = Colors.White;
        selfModulate.A = dimmed ? 0.9f : 1f;
        view.SelfModulate = selfModulate;

        NOneRelicSelectionVisual? visual = view.GetNodeOrNull<NOneRelicSelectionVisual>(NodeName);
        if (visual is null && selected)
        {
            visual = new NOneRelicSelectionVisual { Name = NodeName };
            view.AddChild(visual);
            visual.Initialize(view);
        }
        visual?.SetSelected(selected);
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
            Name = "OneRelicRainbowOutline",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            BorderWidth = 7f,
            EditorOnly = false,
            ZIndex = 60
        };
        view.AddChild(_outline);
        _outline.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        if (!CommonHelpers.TryFindDescendantOrSelf(view, out NRelic relicView))
            return;

        try
        {
            _glow = PreloadManager.Cache.GetScene(RareGlowScenePath)
                .Instantiate<GpuParticles2D>(PackedScene.GenEditState.Disabled);
            _glow.Name = "OneRelicRainbowGlow";
            _glow.ShowBehindParent = true;
            _glow.Position = new Vector2(34f, 34f);
            _glow.Scale = Vector2.One * 0.2f;
            relicView.AddChild(_glow);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"OneRelic: could not create the native rare treasure glow. {exception.Message}");
        }
    }

    private void SetSelected(bool selected)
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
