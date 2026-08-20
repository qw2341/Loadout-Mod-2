#nullable enable

namespace Loadout.Services.CardPortraits;

using System;
using Godot;
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

    public override void _EnterTree()
    {
        CardPortraitRuntime.TemporaryChanged += OnTemporaryChanged;
        CardPortraitRuntime.PermanentChanged += OnPermanentChanged;
    }

    public override void _Ready()
    {
        _card = GetParentOrNull<NCard>();
        _portrait = _card?.GetNodeOrNull<TextureRect>("%Portrait");
        _ancientPortrait = _card?.GetNodeOrNull<TextureRect>("%AncientPortrait");
        EnsureTimer();
        if (_card is not null)
            _card.VisibilityChanged += OnVisibilityChanged;
    }

    public override void _ExitTree()
    {
        CardPortraitRuntime.TemporaryChanged -= OnTemporaryChanged;
        CardPortraitRuntime.PermanentChanged -= OnPermanentChanged;
        if (_card is not null)
            _card.VisibilityChanged -= OnVisibilityChanged;
        Stop();
        base._ExitTree();
    }

    public void Bind(NCard card, CardPortraitTextureSequence? sequence)
    {
        _card ??= card;
        _portrait ??= card.GetNodeOrNull<TextureRect>("%Portrait");
        _ancientPortrait ??= card.GetNodeOrNull<TextureRect>("%AncientPortrait");
        Stop();
        if (sequence is not { Frames.Count: > 1 })
            return;

        _sequence = sequence;
        _frameIndex = 0;
        ApplyCurrentFrame();
        RestartTimer();
    }

    public void Stop()
    {
        _timer?.Stop();
        _sequence = null;
        _frameIndex = 0;
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
        if (model.Rarity == CardRarity.Ancient)
        {
            if (_ancientPortrait is not null)
                _ancientPortrait.Texture = texture;
        }
        else if (_portrait is not null)
        {
            _portrait.Texture = texture;
        }
    }

    private void RestartTimer()
    {
        if (_card is null
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

    private void OnVisibilityChanged()
    {
        if (_card?.IsVisibleInTree() == true)
            RestartTimer();
        else
            _timer?.Stop();
    }

    private void OnTemporaryChanged(CardModel card)
    {
        if (_card?.Model is CardModel model && ReferenceEquals(model, card))
            CardPortraitDynamicPatches.ReloadCard(_card);
    }

    private void OnPermanentChanged(ModelId cardId)
    {
        if (_card?.Model is CardModel model && model.Id.Equals(cardId))
            CardPortraitDynamicPatches.ReloadCard(_card);
    }

}
