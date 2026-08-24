#nullable enable

namespace Loadout.UI.Screens;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Loadout.PanelItems;
using Loadout.Services.Configuration;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;

public partial class NCardModificationExportScreen : NCardSelectScreen, IScreenContext
{
    private const string ExportScenePath = "res://UI/Screens/CardModificationExportScreen.tscn";
    private const float DragThresholdSquared = 20f;

    private readonly TaskCompletionSource<bool> _completion = new();
    private readonly HashSet<string> _paintedIds = new(StringComparer.Ordinal);
    private CardModificationExportSession? _session;
    private CardModificationExportEntry? _pressEntry;
    private Vector2 _pressPosition;
    private bool _paintSelected;
    private bool _painting;
    private string? _suppressActivationId;
    private bool _completed;

    public Control? DefaultFocusedControl => GetNodeOrNull<Control>(CancelButtonPath);
    public Control? FocusedControlFromTopBar => DefaultFocusedControl;
    public Task<bool> Completion => _completion.Task;

    public static NCardModificationExportScreen Create()
    {
        if (ResourceLoader.Exists(ExportScenePath)
            && GD.Load<PackedScene>(ExportScenePath) is { } scene
            && scene.Instantiate<NCardModificationExportScreen>() is { } screen)
        {
            return screen;
        }

        GD.PushWarning($"Card modification export: could not load '{ExportScenePath}'. Falling back to a script-only screen.");
        return new NCardModificationExportScreen();
    }

    public void Initialize(CardModificationExportSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        if (IsNodeReady())
            ConfigureSession();
    }

    public override void _Ready()
    {
        base._Ready();
        if (_session is null)
            throw new InvalidOperationException("The card export screen must be initialized before it enters the tree.");

        ConfigureSession();
        SetScreenLifecycleActive(true);
    }

    public override void _ExitTree()
    {
        if (!_completed)
            Complete(false, closeModal: false);
        base._ExitTree();
    }

    public override void _Input(InputEvent inputEvent)
    {
        if (!IsVisibleInTree())
            return;
        base._Input(inputEvent);
        if (!IsScreenActive)
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
        CardModificationExportSession session = _session!;
        SelectItemAdapter<CardModificationExportEntry> adapter = new()
        {
            GetId = entry => entry.Id.ToString(),
            GetName = entry => SafeTitle(entry.Canonical),
            GetSearchText = entry => $"{entry.Id} {SafeTitle(entry.Canonical)}",
            CreateView = (entry, _) => CreateCardView(entry),
            ViewReady = (entry, view) =>
            {
                RefreshEntryCard(entry, view, refreshDeferred: false);
                ApplyEntryVisual(entry, view);
                Callable.From(() =>
                {
                    if (!GodotObject.IsInstanceValid(view))
                        return;
                    RefreshEntryCard(entry, view, refreshDeferred: false);
                    ApplyEntryVisual(entry, view);
                }).CallDeferred();
            },
            UpdateView = (entry, view, _) =>
            {
                RefreshEntryCard(entry, view, refreshDeferred: false);
                ApplyEntryVisual(entry, view);
            },
            BindActivationWithCleanup = (_, view, activate) =>
                CardPrinter.BindCardActivationWithCleanup(view, activate)
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
        SetConfirmAvailability(() => true, hideWhenUnavailable: false, showWithoutSelection: true);
        RefreshConfirmAvailability();
    }

    private static Control CreateCardView(CardModificationExportEntry entry)
    {
        CardModel? model = entry.GetPreview();
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
        if (item.UntypedModel is not CardModificationExportEntry entry)
            return;
        if (string.Equals(_suppressActivationId, item.Id, StringComparison.Ordinal))
        {
            _suppressActivationId = null;
            return;
        }

        entry.Toggle();
        RefreshEntry(entry);
    }

    private static void ApplyEntryVisual(CardModificationExportEntry entry, Control view)
    {
        view.Modulate = entry.IsSelected
            ? Colors.White
            : new Color(0.42f, 0.42f, 0.42f, 0.5f);
    }

    private void BeginPaint(Vector2 pointer)
    {
        _pressEntry = FindEntryAt(pointer);
        if (_pressEntry is null)
            return;

        _pressPosition = pointer;
        _paintSelected = !_pressEntry.IsSelected;
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

    private void Paint(CardModificationExportEntry entry)
    {
        if (!_paintedIds.Add(entry.Id.ToString()))
            return;
        entry.SetSelected(_paintSelected);
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

    private CardModificationExportEntry? FindEntryAt(Vector2 pointer)
    {
        foreach (IGenericSelectItem item in Items)
        {
            if (item.View is { } view
                && GodotObject.IsInstanceValid(view)
                && ModificationImportScreenUi.ContainsCardPoint(view, pointer)
                && item.UntypedModel is CardModificationExportEntry entry)
            {
                return entry;
            }
        }
        return null;
    }

    private void RefreshEntry(CardModificationExportEntry entry) =>
        RefreshItemView(entry.Id.ToString());

    private static void RefreshEntryCard(
        CardModificationExportEntry entry,
        Control view,
        bool refreshDeferred = true)
    {
        ModificationImportScreenUi.RefreshExactCardView(view, entry.GetPreview(), refreshDeferred);
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
