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
    private const float RelicPopDistance = 40f;
    private const float RelicFadeDuration = 0.1f;
    private const float RelicPopDuration = 0.35f;
    private static readonly string RelicFlashPath = SceneHelper.GetScenePath("vfx/relic_inventory_flash_vfx");

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

        TextureRect sourceIcon = source.Relic.Icon;
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
            Scale = sourceIcon.Scale,
            Rotation = sourceIcon.Rotation,
            PivotOffset = sourceIcon.PivotOffset,
            Modulate = sourceIcon.Modulate,
            SelfModulate = sourceIcon.SelfModulate
        };
        _relics.AddChildSafely(icon);
        icon.GlobalPosition = sourceIcon.GlobalPosition;
        Vector2 flashPosition = source.GlobalPosition + source.Size * 0.5f;

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
        tween.TweenProperty(icon, "position:y", destination.Y, RelicPopDuration);
        tween.TweenCallback(Callable.From(() =>
        {
            ShowRelicFlash(flashPosition, relic);
            icon.QueueFreeSafely();
        }));
    }

    public void Clear()
    {
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

    private void ShowRelicFlash(Vector2 globalPosition, RelicModel relic)
    {
        try
        {
            Node2D flash = PreloadManager.Cache.GetScene(RelicFlashPath)
                .Instantiate<Node2D>(PackedScene.GenEditState.Disabled);
            flash.GetNode<GpuParticles2D>("Particles").Texture = relic.Icon;
            flash.GlobalPosition = globalPosition;
            _relics.AddChildSafely(flash);
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
}
