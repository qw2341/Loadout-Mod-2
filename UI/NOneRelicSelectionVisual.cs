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

    private NRelic? _relicView;
    private GpuParticles2D? _glow;
    private Color _baseOutlineColor = Colors.White;
    private bool _hasBaseOutlineColor;
    private float _phase;
    private bool _selected;

    public static void Apply(Control view, bool selected, bool dimmed)
    {
        Color selfModulate = Colors.White;
        selfModulate.A = dimmed ? 0.9f : 1f;
        view.SelfModulate = selfModulate;

        if (!CommonHelpers.TryFindDescendantOrSelf(view, out NRelic relicView))
            return;

        NOneRelicSelectionVisual? visual = view.GetNodeOrNull<NOneRelicSelectionVisual>(NodeName);
        if (visual is null && selected)
        {
            visual = new NOneRelicSelectionVisual { Name = NodeName };
            view.AddChild(visual);
            visual.Initialize(relicView);
        }
        visual?.ApplySelection(selected);
    }

    public override void _Process(double delta)
    {
        _phase = Mathf.PosMod(
            _phase + (float)delta * NLoadoutPanelButton.RainbowSpeed * Mathf.Tau,
            Mathf.Tau);
        Color color = NLoadoutPanelButton.GetSineRainbowColor(_phase);
        if (TryGetOutline(out TextureRect outline))
            outline.SelfModulate = color;
        if (_glow is not null && IsInstanceValid(_glow))
            _glow.Modulate = color;
    }

    private void Initialize(NRelic relicView)
    {
        _relicView = relicView;
        if (TryGetOutline(out TextureRect outline))
            CaptureBaseOutline(outline);
        else
            relicView.Connect(
                Node.SignalName.Ready,
                Callable.From(OnRelicReady),
                (uint)GodotObject.ConnectFlags.OneShot);

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

    private void OnRelicReady()
    {
        if (!TryGetOutline(out TextureRect outline))
            return;

        CaptureBaseOutline(outline);
        outline.SelfModulate = _selected
            ? NLoadoutPanelButton.GetSineRainbowColor(_phase)
            : _baseOutlineColor;
        ApplyGlow(_selected, restart: _selected);
    }

    private void ApplySelection(bool selected)
    {
        if (!_selected && TryGetOutline(out TextureRect outline))
            CaptureBaseOutline(outline);

        _selected = selected;
        Color rainbow = NLoadoutPanelButton.GetSineRainbowColor(_phase);
        if (TryGetOutline(out outline))
            outline.SelfModulate = selected
                ? rainbow
                : _baseOutlineColor;
        ApplyGlow(selected, restart: selected);
        SetProcess(selected);
    }

    private void ApplyGlow(bool visible, bool restart)
    {
        if (_glow is null || !IsInstanceValid(_glow))
            return;

        _glow.Visible = visible;
        _glow.Emitting = visible;
        if (!visible)
            return;

        _glow.Modulate = NLoadoutPanelButton.GetSineRainbowColor(_phase);
        if (restart)
            _glow.Restart();
    }

    private bool TryGetOutline(out TextureRect outline)
    {
        outline = null!;
        if (_relicView is null
            || !IsInstanceValid(_relicView)
            || !_relicView.IsNodeReady())
            return false;

        outline = _relicView.Outline;
        return outline is not null;
    }

    private void CaptureBaseOutline(TextureRect outline)
    {
        if (_hasBaseOutlineColor)
            return;

        _baseOutlineColor = outline.SelfModulate;
        _hasBaseOutlineColor = true;
    }
}
