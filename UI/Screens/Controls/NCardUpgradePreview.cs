#nullable enable

namespace Loadout.UI.Screens.Controls;

using System;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

public partial class NCardUpgradePreview : Control
{
    private const string ArrowTexturePath =
        "res://images/ui/cards/upgrade_preview/upgrade_arrow.png";

    private Control? _beforeMount;
    private Control? _afterMount;
    private Control? _arrows;
    private NPreviewCardHolder? _beforeHolder;
    private NPreviewCardHolder? _afterHolder;
    private NCard? _beforeCard;
    private NCard? _afterCard;
    private CardModel? _beforeModel;
    private CardModel? _afterModel;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        BuildLayout();
        RefreshCards();
    }

    public override void _ExitTree()
    {
        ClearCards();
        ReleaseCardViews();
    }

    public void SetCards(CardModel before, CardModel after)
    {
        _beforeModel = before;
        _afterModel = after;
        if (IsNodeReady())
            RefreshCards();
    }

    public void ClearCards()
    {
        _beforeModel = null;
        _afterModel = null;
        if (_beforeCard is not null
            && GodotObject.IsInstanceValid(_beforeCard))
        {
            _beforeCard.Model = null;
        }

        if (_afterCard is not null
            && GodotObject.IsInstanceValid(_afterCard))
        {
            _afterCard.Model = null;
        }

        if (_arrows is not null)
            _arrows.Visible = false;
    }

    private void BuildLayout()
    {
        if (_beforeMount is not null)
            return;

        _beforeMount = CreateCardMount("Before", -280f);
        AddChild(_beforeMount);

        _afterMount = CreateCardMount("After", 280f);
        AddChild(_afterMount);

        _arrows = new Control
        {
            Name = "Arrows",
            MouseFilter = MouseFilterEnum.Ignore
        };
        AddChild(_arrows);

        Texture2D? arrowTexture = ResourceLoader.Exists(ArrowTexturePath)
            ? GD.Load<Texture2D>(ArrowTexturePath)
            : null;
        if (arrowTexture is null)
        {
            GD.PushWarning(
                $"CardModification: missing upgrade arrow texture '{ArrowTexturePath}'.");
            return;
        }

        HBoxContainer shadow = CreateArrowRow(
            "Shadow",
            arrowTexture,
            new Vector2(-93.5f, -24.5f),
            new Vector2(109.5f, 40.5f));
        shadow.Modulate = new Color(0f, 0f, 0f, 0.25098f);
        _arrows.AddChild(shadow);

        _arrows.AddChild(CreateArrowRow(
            "ArrowRow",
            arrowTexture,
            new Vector2(-101.5f, -32.5f),
            new Vector2(101.5f, 32.5f)));
    }

    private void RefreshCards()
    {
        if (_beforeModel is null || _afterModel is null)
        {
            ClearCards();
            return;
        }

        EnsureCardViews();
        if (_beforeCard is null || _afterCard is null)
            return;

        _beforeCard.Model = _beforeModel;
        _beforeCard.UpdateVisuals(PileType.Deck, CardPreviewMode.Normal);

        _afterCard.Model = _afterModel;
        _afterCard.UpdateVisuals(PileType.Deck, CardPreviewMode.Normal);
        _afterCard.ShowUpgradePreview();

        if (_arrows is not null)
            _arrows.Visible = true;
    }

    private void EnsureCardViews()
    {
        if (_beforeHolder is not null && _afterHolder is not null)
            return;
        if (_beforeMount is null
            || _afterMount is null
            || _beforeModel is null
            || _afterModel is null)
        {
            return;
        }

        if (_beforeHolder is null)
        {
            _beforeCard = NCard.Create(_beforeModel)
                          ?? throw new InvalidOperationException(
                              "Could not create the pre-upgrade card view.");
            _beforeHolder = NPreviewCardHolder.Create(
                _beforeCard,
                showHoverTips: true,
                scaleOnHover: false)
                ?? throw new InvalidOperationException(
                    "Could not create the pre-upgrade card holder.");
            _beforeHolder.FocusMode = FocusModeEnum.All;
            _beforeMount.AddChildSafely(_beforeHolder);
        }

        if (_afterHolder is null)
        {
            _afterCard = NCard.Create(_afterModel)
                         ?? throw new InvalidOperationException(
                             "Could not create the upgraded card view.");
            _afterHolder = NPreviewCardHolder.Create(
                _afterCard,
                showHoverTips: true,
                scaleOnHover: false)
                ?? throw new InvalidOperationException(
                    "Could not create the upgraded card holder.");
            _afterHolder.FocusMode = FocusModeEnum.None;
            _afterMount.AddChildSafely(_afterHolder);
        }
    }

    private void ReleaseCardViews()
    {
        if (_beforeHolder is not null
            && GodotObject.IsInstanceValid(_beforeHolder))
        {
            _beforeHolder.QueueFreeSafely();
        }

        if (_afterHolder is not null
            && GodotObject.IsInstanceValid(_afterHolder))
        {
            _afterHolder.QueueFreeSafely();
        }

        _beforeHolder = null;
        _afterHolder = null;
        _beforeCard = null;
        _afterCard = null;
    }

    private static Control CreateCardMount(string name, float offset)
    {
        return new Control
        {
            Name = name,
            OffsetLeft = offset,
            OffsetRight = offset,
            MouseFilter = MouseFilterEnum.Ignore
        };
    }

    private static HBoxContainer CreateArrowRow(
        string name,
        Texture2D texture,
        Vector2 topLeft,
        Vector2 bottomRight)
    {
        HBoxContainer row = new()
        {
            Name = name,
            MouseFilter = MouseFilterEnum.Ignore
        };
        row.SetAnchorsPreset(LayoutPreset.Center);
        row.OffsetLeft = topLeft.X;
        row.OffsetTop = topLeft.Y;
        row.OffsetRight = bottomRight.X;
        row.OffsetBottom = bottomRight.Y;
        row.AddThemeConstantOverride("separation", 0);

        for (int index = 0; index < 3; index++)
        {
            row.AddChild(new TextureRect
            {
                Name = $"Arrow{index + 1}",
                Texture = texture,
                MouseFilter = MouseFilterEnum.Ignore
            });
        }

        return row;
    }
}
