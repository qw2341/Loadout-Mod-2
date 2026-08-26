#nullable enable

namespace Loadout.UI.Screens;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Loadout.PanelItems;
using Loadout.Services.Configuration;
using Loadout.UI.Managers;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;

public partial class NCardModificationImportScreen : NCardSelectScreen, IScreenContext
{
    private const string ImportScenePath = "res://UI/Screens/CardModificationImportScreen.tscn";
    private const string SmithIconPath = "res://images/ui/rest_site/option_smith.png";
    private const float DragThresholdSquared = 20f;

    private readonly TaskCompletionSource<bool> _completion = new();
    private readonly HashSet<string> _paintedIds = new(StringComparer.Ordinal);
    private CardModificationImportSession? _session;
    private CardModificationImportEntry? _pressEntry;
    private Vector2 _pressPosition;
    private bool _paintIncoming;
    private bool _painting;
    private string? _suppressActivationId;
    private NPreviewCardHolder? _upgradePreview;
    private NInspectCardScreen? _foregroundInspect;
    private Node? _inspectOriginalParent;
    private int _inspectOriginalIndex;
    private int _inspectOriginalZIndex;
    private bool _inspectOriginalZAsRelative;
    private bool _completed;

    public Control? DefaultFocusedControl => GetNodeOrNull<Control>(CancelButtonPath);
    public Control? FocusedControlFromTopBar => DefaultFocusedControl;
    public Task<bool> Completion => _completion.Task;

    public static NCardModificationImportScreen Create()
    {
        if (ResourceLoader.Exists(ImportScenePath)
            && GD.Load<PackedScene>(ImportScenePath) is { } scene
            && scene.Instantiate<NCardModificationImportScreen>() is { } screen)
        {
            return screen;
        }

        GD.PushWarning($"Card modification import: could not load '{ImportScenePath}'. Falling back to a script-only screen.");
        return new NCardModificationImportScreen();
    }

    public void Initialize(CardModificationImportSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        if (IsNodeReady())
            ConfigureSession();
    }

    public override void _Ready()
    {
        base._Ready();
        if (_session is null)
            throw new InvalidOperationException("The card import screen must be initialized before it enters the tree.");

        ConfigureSession();
        SetScreenLifecycleActive(true);
    }

    public override void _ExitTree()
    {
        HideUpgradePreview();
        RestoreInspectParent();
        if (!_completed)
            Complete(false, closeModal: false);
        base._ExitTree();
    }

    public override void _Input(InputEvent inputEvent)
    {
        if (!IsVisibleInTree() || _foregroundInspect is { Visible: true })
            return;
        base._Input(inputEvent);
        if (!IsScreenActive
            || GetNodeOrNull<NModificationConflictOverlay>("CardModificationConflictChoice") is not null)
            return;

        switch (inputEvent)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } pressed:
                BeginPaint(pressed.GlobalPosition);
                break;
            case InputEventMouseMotion motion when _pressEntry is not null:
                ContinuePaint(motion.GlobalPosition);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }:
                EndPaint();
                break;
        }
    }

    private void ConfigureSession()
    {
        CardModificationImportSession session = _session!;
        SelectItemAdapter<CardModificationImportEntry> adapter = new()
        {
            GetId = entry => entry.Id.ToString(),
            GetName = entry => SafeTitle(entry.Canonical),
            GetSearchText = entry => $"{entry.Id} {SafeTitle(entry.Canonical)}",
            CreateView = (entry, _) => CreateCardView(entry),
            ViewReady = (entry, view) =>
            {
                RefreshEntryCard(entry, view, refreshDeferred: false);
                EnsureSmithBadge(entry, view);
                UpdateIdenticalLabel(entry, view);
                ApplyEntryVisual(entry, view);
                Callable.From(() =>
                {
                    if (!GodotObject.IsInstanceValid(view))
                        return;
                    RefreshEntryCard(entry, view, refreshDeferred: false);
                    UpdateIdenticalLabel(entry, view);
                    ApplyEntryVisual(entry, view);
                }).CallDeferred();
            },
            UpdateView = (entry, view, _) =>
            {
                RefreshEntryCard(entry, view, refreshDeferred: false);
                UpdateIdenticalLabel(entry, view);
                ApplyEntryVisual(entry, view);
            },
            BindActivationWithCleanup = (entry, view, activate) =>
            {
                Action? activationCleanup = CardPrinter.BindCardActivationWithCleanup(
                    view,
                    activate,
                    () => InspectEntry(entry));
                Action hoverTipCleanup = ModificationImportScreenUi.BindCardHoverTipsToFront(view);
                return () =>
                {
                    activationCleanup?.Invoke();
                    hoverTipCleanup();
                    RemoveIdenticalLabel(view);
                };
            }
        };

        Configure(session.Entries, adapter, builder =>
        {
            builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
            builder.Materialization(SelectMaterializationMode.Lazy);
            builder.HiddenPrewarm(false);
            builder.Layout(
                5,
                NCard.defaultSize * NCardHolder.smallScale,
                32,
                40,
                paddingTop: 250f,
                paddingBottom: 80f);
        });

        ItemActivated -= OnItemActivated;
        ItemActivated += OnItemActivated;
        Confirmed -= OnConfirmed;
        Confirmed += OnConfirmed;
        Cancelled -= OnCancelled;
        Cancelled += OnCancelled;
        SetConfirmAvailability(
            () => session.AllConflictsResolved,
            hideWhenUnavailable: true,
            showWithoutSelection: true);
        ModificationImportScreenUi.Install(
            this,
            () => ApplyBulk(ModificationImportDecision.KeepLocal),
            MergeNonConflicts,
            () => ApplyBulk(ModificationImportDecision.UseIncoming));
        RefreshConfirmAvailability();
    }

    private static Control CreateCardView(CardModificationImportEntry entry)
    {
        CardModel? model = entry.GetIncomingPreview();
        NCard? card = model is null ? null : NCard.Create(model);
        if (card is null || model is null)
            return new Control { CustomMinimumSize = NCard.defaultSize * NCardHolder.smallScale };

        NGridCardHolder? holder = NGridCardHolder.Create(card);
        if (holder is null)
        {
            card.CustomMinimumSize = card.GetCurrentSize();
            return card;
        }

        holder.MouseFilter = MouseFilterEnum.Pass;
        holder.Scale = holder.SmallScale;
        holder.CustomMinimumSize = NCard.defaultSize * holder.SmallScale;
        holder.ReassignToCard(model, PileType.None, null, ModelVisibility.Visible);
        return holder;
    }

    private void OnItemActivated(IGenericSelectItem item, SelectItemState _)
    {
        if (item.UntypedModel is not CardModificationImportEntry entry)
            return;
        if (string.Equals(_suppressActivationId, item.Id, StringComparison.Ordinal))
        {
            _suppressActivationId = null;
            return;
        }

        if (entry.HasConflict)
        {
            InspectEntry(entry);
            return;
        }

        entry.Toggle();
        RefreshEntry(entry);
    }

    private void InspectEntry(CardModificationImportEntry entry)
    {
        if (entry.HasConflict)
        {
            NModificationConflictOverlay.ShowCard(this, entry, () => OnConflictResolved(entry));
            return;
        }

        CardModel? preview = entry.GetIncomingPreview();
        if (preview is not null && NGame.Instance is { } game)
            OpenInspectOnTop(game.GetInspectCardScreen(), preview);
    }

    private void OnConflictResolved(CardModificationImportEntry entry)
    {
        RefreshEntry(entry);
        Callable.From(() => OpenNextUnresolvedConflict(entry)).CallDeferred();
    }

    private void OpenNextUnresolvedConflict(CardModificationImportEntry current)
    {
        if (_session is null
            || !GodotObject.IsInstanceValid(this)
            || GetNodeOrNull<NModificationConflictOverlay>("CardModificationConflictChoice") is not null)
        {
            return;
        }

        int currentIndex = -1;
        for (int index = 0; index < _session.Entries.Count; index++)
        {
            if (ReferenceEquals(_session.Entries[index], current))
            {
                currentIndex = index;
                break;
            }
        }
        if (currentIndex < 0)
            return;
        for (int offset = 1; offset <= _session.Entries.Count; offset++)
        {
            CardModificationImportEntry candidate = _session.Entries[(currentIndex + offset) % _session.Entries.Count];
            if (candidate.HasConflict && candidate.Decision == ModificationImportDecision.Unresolved)
            {
                NModificationConflictOverlay.ShowCard(this, candidate, () => OnConflictResolved(candidate));
                return;
            }
        }
    }

    private void OpenInspectOnTop(NInspectCardScreen inspect, CardModel preview)
    {
        if (inspect.Visible
            || inspect.GetParent() is not { } originalParent
            || NModalContainer.Instance is not { } modal)
        {
            return;
        }

        _foregroundInspect = inspect;
        _inspectOriginalParent = originalParent;
        _inspectOriginalIndex = inspect.GetIndex();
        _inspectOriginalZIndex = inspect.ZIndex;
        _inspectOriginalZAsRelative = inspect.ZAsRelative;
        inspect.VisibilityChanged += OnForegroundInspectVisibilityChanged;
        inspect.Reparent(modal, keepGlobalTransform: false);
        inspect.ZAsRelative = true;
        inspect.ZIndex = 700;
        modal.MoveChild(inspect, modal.GetChildCount() - 1);
        try
        {
            inspect.Open([preview], 0, false);
        }
        catch
        {
            RestoreInspectParent();
            throw;
        }
    }

    private void OnForegroundInspectVisibilityChanged()
    {
        if (_foregroundInspect is null || _foregroundInspect.Visible)
            return;
        RestoreInspectParent();
    }

    private void RestoreInspectParent()
    {
        if (_foregroundInspect is not null && GodotObject.IsInstanceValid(_foregroundInspect))
        {
            _foregroundInspect.VisibilityChanged -= OnForegroundInspectVisibilityChanged;
            if (_inspectOriginalParent is not null
                && GodotObject.IsInstanceValid(_inspectOriginalParent)
                && !ReferenceEquals(_foregroundInspect.GetParent(), _inspectOriginalParent))
            {
                _foregroundInspect.Reparent(_inspectOriginalParent, keepGlobalTransform: false);
                _inspectOriginalParent.MoveChild(
                    _foregroundInspect,
                    Math.Clamp(_inspectOriginalIndex, 0, _inspectOriginalParent.GetChildCount() - 1));
            }
            _foregroundInspect.ZIndex = _inspectOriginalZIndex;
            _foregroundInspect.ZAsRelative = _inspectOriginalZAsRelative;
        }
        _foregroundInspect = null;
        _inspectOriginalParent = null;
    }

    private void ApplyBulk(ModificationImportDecision decision)
    {
        foreach (CardModificationImportEntry entry in _session!.Entries)
        {
            if (!entry.HasConflict)
                continue;
            entry.SetDecision(decision);
            RefreshItemView(entry.Id.ToString());
        }
        RefreshConfirmAvailability();
    }

    private void MergeNonConflicts()
    {
        foreach (CardModificationImportEntry entry in _session!.Entries)
        {
            entry.SetDecision(entry.HasNonConflictingConflict
                ? ModificationImportDecision.UseMerged
                : entry.HasConflict
                    ? ModificationImportDecision.KeepLocal
                    : ModificationImportDecision.UseIncoming);
        }
        RefreshMaterializedEntries();
    }

    private void RefreshMaterializedEntries()
    {
        foreach (IGenericSelectItem item in Items)
        {
            if (item.View is not null)
                RefreshItemView(item.Id);
        }
        RefreshConfirmAvailability();
    }

    private void RefreshEntry(CardModificationImportEntry entry)
    {
        RefreshItemView(entry.Id.ToString());
        RefreshConfirmAvailability();
    }

    private static void ApplyEntryVisual(CardModificationImportEntry entry, Control view)
    {
        view.Modulate = entry.Decision == ModificationImportDecision.KeepLocal
            ? new Color(0.42f, 0.42f, 0.42f, 0.5f)
            : entry.IsIdenticalToLocal
                ? new Color(1f, 1f, 1f, 0.85f)
                : Colors.White;

        if (!CommonHelpers.TryFindDescendantOrSelf(view, out NGridCardHolder holder)
            || holder.CardNode is not { } card
            || card.CardHighlight is null)
        {
            return;
        }

        Color? highlightColor = entry.HasConflict
                                && entry.Decision == ModificationImportDecision.Unresolved
            ? entry.HasPortraitOnlyConflict
                ? new Color(0.18f, 1f, 0.34f, 1f)
                : entry.HasNonConflictingConflict
                    ? NCardHighlight.gold
                    : NCardHighlight.red
            : null;
        if (highlightColor is { } color)
        {
            card.CardHighlight.SelfModulate = Colors.White;
            card.CardHighlight.Modulate = color;
            card.CardHighlight.AnimShow();
        }
        else
        {
            card.CardHighlight.SelfModulate = Colors.White;
            card.CardHighlight.Modulate = NCardHighlight.playableColor;
            card.CardHighlight.AnimHideInstantly();
        }
    }

    private static void UpdateIdenticalLabel(CardModificationImportEntry entry, Control view)
    {
        if (!CommonHelpers.TryFindDescendantOrSelf(view, out NGridCardHolder holder))
            return;

        Control? existing = holder.GetNodeOrNull<Control>("ImportIdenticalLabel");
        if (!entry.IsIdenticalToLocal)
        {
            if (existing is not null)
                existing.Visible = false;
            return;
        }

        if (existing is null)
        {
            var label = ModificationImportScreenUi.CreateLabel(
                LocMan.Loc("MOD_IMPORT_IDENTICAL_LOCAL", "Identical with Local"),
                24,
                HorizontalAlignment.Center);
            label.Name = "ImportIdenticalLabel";
            label.Position = new Vector2(-135f, -34f);
            label.Size = new Vector2(270f, 68f);
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            label.ZIndex = 95;
            holder.AddChild(label);
            existing = label;
        }

        if (existing is MegaLabel textLabel)
        {
            textLabel.Text = LocMan.Loc("MOD_IMPORT_IDENTICAL_LOCAL", "Identical with Local");
            textLabel.Visible = true;
        }
        holder.MoveChild(existing, holder.GetChildCount() - 1);
    }

    private static void RemoveIdenticalLabel(Control view)
    {
        if (!CommonHelpers.TryFindDescendantOrSelf(view, out NGridCardHolder holder)
            || holder.GetNodeOrNull<Control>("ImportIdenticalLabel") is not { } label)
        {
            return;
        }

        holder.RemoveChild(label);
        label.QueueFree();
    }

    private void EnsureSmithBadge(CardModificationImportEntry entry, Control view)
    {
        if (!entry.HasUpgradeModification
            || !CommonHelpers.TryFindDescendantOrSelf(view, out NGridCardHolder holder)
            || holder.GetNodeOrNull<TextureRect>("ImportSmithBadge") is not null)
        {
            return;
        }

        Texture2D? texture;
        try { texture = PreloadManager.Cache.GetTexture2D(SmithIconPath); }
        catch { texture = null; }
        if (texture is null)
            return;

        TextureRect badge = new()
        {
            Name = "ImportSmithBadge",
            Texture = texture,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Position = new Vector2(NCard.defaultSize.X * 0.5f - 82f, -NCard.defaultSize.Y * 0.5f + 18f),
            Size = new Vector2(68f, 68f),
            MouseFilter = MouseFilterEnum.Stop,
            ZIndex = 90
        };
        badge.MouseEntered += () => ShowUpgradePreview(entry, holder);
        badge.MouseExited += HideUpgradePreview;
        holder.AddChild(badge);
    }

    private void ShowUpgradePreview(CardModificationImportEntry entry, Control source)
    {
        HideUpgradePreview();
        CardModel? model = entry.GetDisplayPreview(upgraded: true);
        NCard? card = model is null ? null : NCard.Create(model);
        NPreviewCardHolder? preview = card is null
            ? null
            : NPreviewCardHolder.Create(card, showHoverTips: true, scaleOnHover: true);
        if (preview is null)
            return;

        Vector2 sourceCenter = source.GetGlobalRect().GetCenter();
        float direction = sourceCenter.X < GetViewportRect().Size.X * 0.58f ? 1f : -1f;
        preview.Position = sourceCenter + new Vector2(direction * 250f, 0f) - GlobalPosition;
        preview.ZIndex = 350;
        AddChild(preview);
        ModificationImportScreenUi.BindCardHoverTipsToFront(preview);
        card!.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
        _upgradePreview = preview;
    }

    private void HideUpgradePreview()
    {
        if (_upgradePreview is not null && GodotObject.IsInstanceValid(_upgradePreview))
            _upgradePreview.QueueFree();
        _upgradePreview = null;
    }

    private void BeginPaint(Vector2 pointer)
    {
        _pressEntry = FindEntryAt(pointer);
        if (_pressEntry is null || _pressEntry.HasConflict)
        {
            _pressEntry = null;
            return;
        }

        _pressPosition = pointer;
        _paintIncoming = !_pressEntry.IsSelected;
        _painting = false;
        _paintedIds.Clear();
    }

    private void ContinuePaint(Vector2 pointer)
    {
        if (_pressEntry is null)
            return;
        if (!_painting && pointer.DistanceSquaredTo(_pressPosition) < DragThresholdSquared)
            return;

        if (!_painting)
        {
            _painting = true;
            Paint(_pressEntry);
        }
        if (FindEntryAt(pointer) is { } hovered)
            Paint(hovered);
    }

    private void Paint(CardModificationImportEntry entry)
    {
        if (entry.HasConflict || !_paintedIds.Add(entry.Id.ToString()))
            return;
        entry.SetDecision(_paintIncoming
            ? ModificationImportDecision.UseIncoming
            : ModificationImportDecision.KeepLocal);
        RefreshEntry(entry);
    }

    private void EndPaint()
    {
        if (_painting && _pressEntry is not null)
        {
            _suppressActivationId = _pressEntry.Id.ToString();
            Callable.From(() => _suppressActivationId = null).CallDeferred();
        }
        _pressEntry = null;
        _painting = false;
        _paintedIds.Clear();
    }

    private CardModificationImportEntry? FindEntryAt(Vector2 pointer)
    {
        foreach (IGenericSelectItem item in Items)
        {
            if (item.View is { } view
                && GodotObject.IsInstanceValid(view)
                && ModificationImportScreenUi.ContainsCardPoint(view, pointer)
                && item.UntypedModel is CardModificationImportEntry entry)
            {
                return entry;
            }
        }
        return null;
    }

    private static void RefreshEntryCard(
        CardModificationImportEntry entry,
        Control view,
        bool refreshDeferred = true)
    {
        ModificationImportScreenUi.RefreshExactCardView(
            view,
            entry.GetDisplayPreview(),
            refreshDeferred);
    }

    private void OnConfirmed(IReadOnlyList<IGenericSelectItem> _) => Complete(true, closeModal: true);
    private void OnCancelled() => Complete(false, closeModal: true);

    private void Complete(bool confirmed, bool closeModal)
    {
        if (_completed)
            return;

        _completed = true;
        try
        {
            if (closeModal
                && NModalContainer.Instance is { } modal
                && GodotObject.IsInstanceValid(modal)
                && ReferenceEquals(modal.OpenModal, this))
            {
                modal.Clear();
            }
            else if (closeModal)
            {
                QueueFree();
            }
        }
        finally
        {
            _completion.TrySetResult(confirmed);
        }
    }

    private static string SafeTitle(CardModel card)
    {
        try { return card.Title; }
        catch { return card.Id.Entry; }
    }
}
