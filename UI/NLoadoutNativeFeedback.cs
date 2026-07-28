#nullable enable

namespace Loadout.UI;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.TestSupport;

public partial class NLoadoutNativeFeedback : Control
{
    private const int MaxCardPreviews = 50;
    private const int MaxPendingRelicSources = 50;
    private const ulong RelicSourceLifetimeMsec = 15000;
    private const float RelicPopDistance = 40f;
    private const float RelicFadeDuration = 0.1f;
    private const float RelicFlyDuration = 0.35f;
    private static readonly string RelicFlashPath = SceneHelper.GetScenePath("vfx/relic_inventory_flash_vfx");

    private readonly List<PendingRelicSource> _pendingRelicSources = [];
    private long _nextRelicSourceToken;
    private int _relicFeedbackGeneration;
    private NCardPreviewContainer _horizontalCards = null!;
    private NMessyCardPreviewContainer _messyCards = null!;
    private NGridCardPreviewContainer _gridCards = null!;
    private Control _cardTrails = null!;
    private Control _relics = null!;

    public bool HasActiveCardFeedback =>
        GetPreviewCardCount() > 0 || _cardTrails.GetChildCount() > 0;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        MouseBehaviorRecursive = MouseBehaviorRecursiveEnum.Disabled;
        FocusBehaviorRecursive = FocusBehaviorRecursiveEnum.Disabled;
        CreateLayers();
    }

    public void PreviewCardPileAdd(
        IReadOnlyList<CardPileAddResult> results,
        float lingerTime,
        CardPreviewStyle style)
    {
        if (TestMode.IsOn || CombatManager.Instance.IsEnding)
            return;

        foreach (CardPileAddResult result in results)
        {
            if (GetPreviewCardCount() >= MaxCardPreviews)
                return;

            if (!result.success || !LocalContext.IsMine(result.cardAdded))
                continue;

            PreviewCardPileAdd(result, lingerTime, style);
        }
    }

    public void PreviewCardRemoval(IReadOnlyList<CardModel> cards)
    {
        if (TestMode.IsOn)
            return;

        foreach (CardModel card in cards)
        {
            if (GetPreviewCardCount() >= MaxCardPreviews)
                return;

            if (!LocalContext.IsMine(card))
                continue;

            NCard? cardNode = NCard.Create(card);
            if (cardNode is null)
                continue;

            _horizontalCards.AddChildSafely(cardNode);
            cardNode.UpdateVisuals(PileType.None, CardPreviewMode.Normal);

            Tween tween = cardNode.CreateTween();
            tween.TweenProperty(cardNode, "scale", Vector2.One, 0.25)
                .From(Vector2.Zero)
                .SetEase(Tween.EaseType.Out)
                .SetTrans(Tween.TransitionType.Cubic);
            tween.TweenProperty(cardNode, "scale:y", 0f, 0.3).SetDelay(1.5);
            tween.Parallel().TweenProperty(cardNode, "scale:x", 1.5f, 0.3).SetDelay(1.5);
            tween.Parallel().TweenProperty(cardNode, "modulate", Colors.Black, 0.2).SetDelay(1.5);
            tween.TweenCallback(Callable.From(cardNode.QueueFreeSafely));
        }
    }

    public void PreviewRelicObtained(RelicModel relic)
    {
        if (TestMode.IsOn || !LocalContext.IsMine(relic))
            return;

        bool hasStartRect = TryTakeRelicSource(relic.Id, out Rect2 startRect);
        int generation = _relicFeedbackGeneration;
        TaskHelper.RunSafely(PreviewRelicObtainedAfterLayout(
            relic,
            hasStartRect,
            startRect,
            generation));
    }

    private async Task PreviewRelicObtainedAfterLayout(
        RelicModel relic,
        bool hasStartRect,
        Rect2 startRect,
        int generation)
    {
        NRelicInventory? inventory = NRun.Instance?.GlobalUi?.RelicInventory;
        if (inventory is null)
            return;

        NRelicInventoryHolder? source = null;
        foreach (NRelicInventoryHolder holder in inventory.RelicNodes)
        {
            if (!ReferenceEquals(holder.Relic.Model, relic))
                continue;

            source = holder;
            break;
        }

        if (source is null)
            return;

        SignalAwaiter sortAwaiter = ToSignal(inventory, Container.SignalName.SortChildren);
        inventory.QueueSort();
        await sortAwaiter;
        if (generation != _relicFeedbackGeneration
            || !GodotObject.IsInstanceValid(this)
            || !GodotObject.IsInstanceValid(inventory)
            || !GodotObject.IsInstanceValid(source)
            || !IsInsideTree())
        {
            return;
        }

        TextureRect sourceIcon = source.Relic.Icon;
        Rect2 destinationRect = GetInventoryIconRect(source, sourceIcon);
        Color iconModulate = sourceIcon.Modulate;
        iconModulate.A = 1f;
        Color iconSelfModulate = sourceIcon.SelfModulate;
        iconSelfModulate.A = 1f;
        TextureRect icon = new()
        {
            Name = $"LoadoutRelicFeedback-{relic.Id}",
            Texture = sourceIcon.Texture,
            Material = sourceIcon.Material,
            ExpandMode = sourceIcon.ExpandMode,
            StretchMode = sourceIcon.StretchMode,
            TextureFilter = sourceIcon.TextureFilter,
            MouseFilter = MouseFilterEnum.Ignore,
            CustomMinimumSize = sourceIcon.CustomMinimumSize,
            Size = sourceIcon.Size,
            Rotation = sourceIcon.Rotation,
            PivotOffset = Vector2.Zero,
            Modulate = iconModulate,
            SelfModulate = iconSelfModulate
        };
        _relics.AddChildSafely(icon);
        icon.Position = destinationRect.Position;
        icon.Scale = GetScaleForRect(destinationRect, icon.Size);
        Vector2 flashPosition = destinationRect.GetCenter();

        if (hasStartRect)
        {
            icon.Position = startRect.Position;
            icon.Scale = GetScaleForRect(startRect, icon.Size);

            Tween flyTween = icon.CreateTween();
            flyTween.SetEase(Tween.EaseType.Out);
            flyTween.SetTrans(Tween.TransitionType.Sine);
            flyTween.TweenProperty(icon, "position", destinationRect.Position, RelicFlyDuration);
            flyTween.Parallel().TweenProperty(
                icon,
                "scale",
                GetScaleForRect(destinationRect, icon.Size),
                RelicFlyDuration);
            flyTween.TweenCallback(Callable.From(() =>
            {
                ShowRelicFlash(flashPosition, relic);
                icon.QueueFreeSafely();
            }));
            return;
        }

        Vector2 destination = icon.Position;
        icon.Position = destination + Vector2.Down * RelicPopDistance;
        Color modulate = icon.Modulate;
        modulate.A = 0f;
        icon.Modulate = modulate;

        Tween tween = icon.CreateTween();
        tween.TweenProperty(icon, "modulate:a", 1f, RelicFadeDuration);
        tween.Parallel();
        tween.SetEase(Tween.EaseType.Out);
        tween.SetTrans(Tween.TransitionType.Back);
        tween.TweenProperty(icon, "position:y", destination.Y, RelicFlyDuration);
        tween.TweenCallback(Callable.From(() =>
        {
            ShowRelicFlash(flashPosition, relic);
            icon.QueueFreeSafely();
        }));
    }

    public long QueueRelicObtainSource(ModelId relicId, Control sourceIcon, int amount)
    {
        if (amount <= 0
            || !GodotObject.IsInstanceValid(sourceIcon)
            || !GodotObject.IsInstanceValid(_relics))
        {
            return 0;
        }

        RemoveExpiredRelicSources();
        Rect2 sourceRect = GetRectRelativeTo(sourceIcon, _relics);
        if (sourceRect.Size.X <= 0f || sourceRect.Size.Y <= 0f)
            return 0;

        if (_pendingRelicSources.Count >= MaxPendingRelicSources)
            _pendingRelicSources.RemoveAt(0);

        long token = ++_nextRelicSourceToken;
        _pendingRelicSources.Add(new PendingRelicSource(
            token,
            relicId,
            sourceRect,
            amount,
            Time.GetTicksMsec()));
        return token;
    }

    public void CancelRelicObtainSource(long token)
    {
        if (token == 0)
            return;

        for (int index = _pendingRelicSources.Count - 1; index >= 0; index--)
        {
            if (_pendingRelicSources[index].Token != token)
                continue;

            _pendingRelicSources.RemoveAt(index);
            return;
        }
    }

    public void Clear()
    {
        _relicFeedbackGeneration++;
        _pendingRelicSources.Clear();
        ClearChildren(_horizontalCards);
        ClearChildren(_messyCards);
        ClearChildren(_gridCards);
        ClearChildren(_cardTrails);
        ClearChildren(_relics);
    }

    private void PreviewCardPileAdd(
        CardPileAddResult result,
        float lingerTime,
        CardPreviewStyle style)
    {
        CardModel card = result.cardAdded;
        if (card.Pile is null)
            return;

        PileType originalPileType = card.Pile.Type;
        Control container = ResolveCardContainer(style);
        NCard? cardNode = NCard.Create(card);
        if (cardNode is null)
            return;

        container.AddChildSafely(cardNode);
        cardNode.UpdateVisuals(originalPileType, CardPreviewMode.Normal);

        Tween tween = cardNode.CreateTween();
        tween.TweenProperty(cardNode, "scale", Vector2.One, 0.25)
            .From(Vector2.Zero)
            .SetEase(Tween.EaseType.Out)
            .SetTrans(Tween.TransitionType.Cubic);
        tween.TweenCallback(Callable.From(() =>
            TaskHelper.RunSafely(FlashRelics(cardNode, result.modifyingModels))));
        tween.TweenCallback(Callable.From(() =>
            FlyCardAway(cardNode, card, originalPileType))).SetDelay(lingerTime);
    }

    private Control ResolveCardContainer(CardPreviewStyle style)
    {
        return style switch
        {
            CardPreviewStyle.GridLayout => _gridCards,
            CardPreviewStyle.MessyLayout => _messyCards,
            CardPreviewStyle.HorizontalLayout when _horizontalCards.GetChildCount() > 5 => _messyCards,
            _ => _horizontalCards
        };
    }

    private void FlyCardAway(NCard cardNode, CardModel card, PileType originalPileType)
    {
        PileType pileType = card.Pile?.Type ?? originalPileType;
        NCardFlyVfx? flyVfx = NCardFlyVfx.Create(
            cardNode,
            pileType,
            isAddingToPile: true,
            card.Owner.Character.TrailPath);

        if (flyVfx is null)
        {
            cardNode.QueueFreeSafely();
            return;
        }

        _cardTrails.AddChildSafely(flyVfx);
    }

    private static Task FlashRelics(NCard cardNode, IEnumerable<AbstractModel>? modifyingModels)
    {
        if (modifyingModels is null)
            return Task.CompletedTask;

        foreach (AbstractModel model in modifyingModels)
        {
            if (model is not RelicModel relic)
                continue;

            relic.Flash();
            cardNode.FlashRelicOnCard(relic);
        }

        return Task.CompletedTask;
    }

    private void ShowRelicFlash(Vector2 localPosition, RelicModel relic)
    {
        try
        {
            Node2D flash = PreloadManager.Cache.GetScene(RelicFlashPath)
                .Instantiate<Node2D>(PackedScene.GenEditState.Disabled);
            flash.GetNode<GpuParticles2D>("Particles").Texture = relic.Icon;
            _relics.AddChildSafely(flash);
            flash.Position = localPosition;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"LoadoutNativeFeedback: failed to show relic flash for '{relic.Id}'. {exception.Message}");
        }
    }

    private int GetPreviewCardCount()
    {
        return _horizontalCards.GetChildCount()
               + _messyCards.GetChildCount()
               + _gridCards.GetChildCount();
    }

    private bool TryTakeRelicSource(ModelId relicId, out Rect2 sourceRect)
    {
        RemoveExpiredRelicSources();
        for (int index = 0; index < _pendingRelicSources.Count; index++)
        {
            PendingRelicSource pending = _pendingRelicSources[index];
            if (!pending.RelicId.Equals(relicId))
                continue;

            sourceRect = pending.SourceRect;
            pending.Remaining--;
            if (pending.Remaining <= 0)
                _pendingRelicSources.RemoveAt(index);
            return true;
        }

        sourceRect = default;
        return false;
    }

    private void RemoveExpiredRelicSources()
    {
        ulong now = Time.GetTicksMsec();
        for (int index = _pendingRelicSources.Count - 1; index >= 0; index--)
        {
            if (now - _pendingRelicSources[index].CreatedAtMsec > RelicSourceLifetimeMsec)
                _pendingRelicSources.RemoveAt(index);
        }
    }

    private static Rect2 GetRectRelativeTo(Control source, Control relativeTo)
    {
        Transform2D transform = relativeTo.GetGlobalTransformWithCanvas().AffineInverse()
                                * source.GetGlobalTransformWithCanvas();
        Vector2 topLeft = transform * Vector2.Zero;
        Vector2 topRight = transform * new Vector2(source.Size.X, 0f);
        Vector2 bottomLeft = transform * new Vector2(0f, source.Size.Y);
        Vector2 bottomRight = transform * source.Size;
        Vector2 minimum = new(
            Mathf.Min(Mathf.Min(topLeft.X, topRight.X), Mathf.Min(bottomLeft.X, bottomRight.X)),
            Mathf.Min(Mathf.Min(topLeft.Y, topRight.Y), Mathf.Min(bottomLeft.Y, bottomRight.Y)));
        Vector2 maximum = new(
            Mathf.Max(Mathf.Max(topLeft.X, topRight.X), Mathf.Max(bottomLeft.X, bottomRight.X)),
            Mathf.Max(Mathf.Max(topLeft.Y, topRight.Y), Mathf.Max(bottomLeft.Y, bottomRight.Y)));
        return new Rect2(minimum, maximum - minimum);
    }

    private Rect2 GetInventoryIconRect(NRelicInventoryHolder holder, TextureRect icon)
    {
        Rect2 holderRect = GetRectRelativeTo(holder, _relics);
        Vector2 iconSize = new(
            holder.Size.X > 0f ? holderRect.Size.X * icon.Size.X / holder.Size.X : icon.Size.X,
            holder.Size.Y > 0f ? holderRect.Size.Y * icon.Size.Y / holder.Size.Y : icon.Size.Y);
        return new Rect2(
            holderRect.Position + (holderRect.Size - iconSize) * 0.5f,
            iconSize);
    }

    private static Vector2 GetScaleForRect(Rect2 rect, Vector2 baseSize)
    {
        return new Vector2(
            baseSize.X > 0f ? rect.Size.X / baseSize.X : 1f,
            baseSize.Y > 0f ? rect.Size.Y / baseSize.Y : 1f);
    }

    private void CreateLayers()
    {
        _horizontalCards = CreateFullRect<NCardPreviewContainer>("HorizontalCards");
        _gridCards = CreateFullRect<NGridCardPreviewContainer>("GridCards");
        _messyCards = CreateFullRect<NMessyCardPreviewContainer>("MessyCards");
        _messyCards.OffsetLeft = 263f;
        _messyCards.OffsetTop = 196f;
        _messyCards.OffsetRight = -302f;
        _messyCards.OffsetBottom = -122f;
        _cardTrails = CreateFullRect<Control>("CardTrails");
        _relics = CreateFullRect<Control>("Relics");
    }

    private T CreateFullRect<T>(string name) where T : Control, new()
    {
        T control = new()
        {
            Name = name,
            MouseFilter = MouseFilterEnum.Ignore
        };
        control.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(control);
        return control;
    }

    private static void ClearChildren(Node parent)
    {
        if (!GodotObject.IsInstanceValid(parent))
            return;

        foreach (Node child in parent.GetChildren())
        {
            child.ProcessMode = ProcessModeEnum.Disabled;
            if (child is CanvasItem canvasItem)
                canvasItem.Visible = false;
            child.QueueFreeSafely();
        }
    }

    private sealed class PendingRelicSource(
        long token,
        ModelId relicId,
        Rect2 sourceRect,
        int remaining,
        ulong createdAtMsec)
    {
        public long Token { get; } = token;
        public ModelId RelicId { get; } = relicId;
        public Rect2 SourceRect { get; } = sourceRect;
        public int Remaining { get; set; } = remaining;
        public ulong CreatedAtMsec { get; } = createdAtMsec;
    }
}
