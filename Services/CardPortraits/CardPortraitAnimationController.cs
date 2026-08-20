#nullable enable

namespace Loadout.Services.CardPortraits;

using System;
using Godot;
using Loadout.Patches.Cards.CardModification;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;

internal partial class CardPortraitAnimationController : Node
{
    public const string NodeName = "LoadoutCardPortraitAnimation";

    private NCard? _card;
    private TextureRect? _portrait;
    private TextureRect? _ancientPortrait;
    private Timer? _timer;
    private CardPortraitTextureSequence? _sequence;
    private int _frameIndex;
    private bool _visibilityConnected;

    public override void _EnterTree()
    {
        _card = GetParentOrNull<NCard>();
        Reapply();
        if (_sequence is { Frames.Count: > 0 })
            Callable.From(Reapply).CallDeferred();
        if (_sequence is { Frames.Count: > 1 })
            Callable.From(RestartTimer).CallDeferred();
    }

    public override void _Ready()
    {
        _card = GetParentOrNull<NCard>();
        _portrait = _card?.GetNodeOrNull<TextureRect>("%Portrait");
        _ancientPortrait = _card?.GetNodeOrNull<TextureRect>("%AncientPortrait");
    }

    public override void _ExitTree()
    {
        _timer?.Stop();
        base._ExitTree();
    }

    public void Bind(NCard card, CardPortraitTextureSequence? sequence)
    {
        _card = card;
        _portrait = card.GetNodeOrNull<TextureRect>("%Portrait");
        _ancientPortrait = card.GetNodeOrNull<TextureRect>("%AncientPortrait");
        ConnectVisibility(card);
        Stop();
        if (sequence is not { Frames.Count: > 0 })
            return;

        _sequence = sequence;
        _frameIndex = 0;
        ApplyCurrentFrame();
        if (sequence.Frames.Count > 1)
            RestartTimer();
    }

    public void Reapply()
    {
        if (_sequence is { Frames.Count: > 0 })
            ApplyCurrentFrame();
    }

    public bool IsBoundTo(CardModel model) =>
        _sequence is not null
        && _sequence.CardModelId.Equals(model.Id);

    public void Stop()
    {
        _timer?.Stop();
        _sequence = null;
        _frameIndex = 0;
    }

    public void Release()
    {
        Stop();
        if (_visibilityConnected && _card is not null && GodotObject.IsInstanceValid(_card))
            _card.VisibilityChanged -= OnCardVisibilityChanged;
        _visibilityConnected = false;
    }

    private void ConnectVisibility(NCard card)
    {
        if (_visibilityConnected)
            return;
        card.VisibilityChanged += OnCardVisibilityChanged;
        _visibilityConnected = true;
    }

    private void OnCardVisibilityChanged()
    {
        if (_card?.IsVisibleInTree() == true && _sequence is { Frames.Count: > 1 })
            RestartTimer();
        else
            _timer?.Stop();
    }

    private void AdvanceFrame()
    {
        if (_sequence is not { Frames.Count: > 1 })
            return;

        _frameIndex = (_frameIndex + 1) % _sequence.Frames.Count;
        ApplyCurrentFrame();
        RestartTimer();
    }

    private void ApplyCurrentFrame()
    {
        if (_card?.Model is not CardModel model || _sequence is null)
            return;

        Texture2D texture = _sequence.Frames[_frameIndex];
        if (CardModificationRuntime.ShouldUseAncientRendering(model))
        {
            if (_ancientPortrait is not null && !ReferenceEquals(_ancientPortrait.Texture, texture))
                _ancientPortrait.Texture = texture;
        }
        else if (_portrait is not null && !ReferenceEquals(_portrait.Texture, texture))
        {
            _portrait.Texture = texture;
        }
    }

    private void RestartTimer()
    {
        if (!IsInsideTree()
            || _card is null
            || !_card.IsVisibleInTree()
            || _sequence is not { Frames.Count: > 1 } sequence)
        {
            return;
        }

        Timer timer = EnsureTimer();
        timer.WaitTime = Math.Clamp(sequence.Durations[_frameIndex], 0.02, 10.0);
        timer.Start();
    }

    private Timer EnsureTimer()
    {
        if (_timer is not null && GodotObject.IsInstanceValid(_timer))
            return _timer;

        _timer = new Timer
        {
            Name = "FrameTimer",
            OneShot = true
        };
        _timer.Timeout += AdvanceFrame;
        AddChild(_timer);
        return _timer;
    }

}
