#nullable enable

namespace Loadout.UI.Screens;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Loadout.PanelItems;
using Loadout.Services.Configuration;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;

public partial class NRelicModificationImportScreen : NRelicSelectScreen, IScreenContext
{
    private const string ImportScenePath = "res://UI/Screens/RelicModificationImportScreen.tscn";
    private const float DragThresholdSquared = 20f;

    private readonly TaskCompletionSource<bool> _completion = new();
    private readonly HashSet<string> _paintedIds = new(StringComparer.Ordinal);
    private RelicModificationImportSession? _session;
    private RelicModificationImportEntry? _pressEntry;
    private Vector2 _pressPosition;
    private bool _paintIncoming;
    private bool _painting;
    private string? _suppressActivationId;
    private bool _completed;

    public Control? DefaultFocusedControl => GetNodeOrNull<Control>(CancelButtonPath);
    public Control? FocusedControlFromTopBar => DefaultFocusedControl;
    public Task<bool> Completion => _completion.Task;

    public static NRelicModificationImportScreen Create()
    {
        if (ResourceLoader.Exists(ImportScenePath)
            && GD.Load<PackedScene>(ImportScenePath) is { } scene
            && scene.Instantiate<NRelicModificationImportScreen>() is { } screen)
        {
            return screen;
        }

        GD.PushWarning($"Relic modification import: could not load '{ImportScenePath}'. Falling back to a script-only screen.");
        return new NRelicModificationImportScreen();
    }

    public void Initialize(RelicModificationImportSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        if (IsNodeReady())
            ConfigureSession();
    }

    public override void _Ready()
    {
        base._Ready();
        if (_session is null)
            throw new InvalidOperationException("The relic import screen must be initialized before it enters the tree.");

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
        base._Input(inputEvent);
        if (!IsScreenActive
            || GetNodeOrNull<NModificationConflictOverlay>("RelicModificationConflictChoice") is not null)
        {
            return;
        }

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
        RelicModificationImportSession session = _session!;
        SelectItemAdapter<RelicModificationImportEntry> adapter = new()
        {
            GetId = entry => entry.Id.ToString(),
            GetName = entry => CommonHelpers.FormatRelicTitle(entry.Canonical),
            GetSearchText = entry => $"{entry.Id} {CommonHelpers.FormatRelicTitle(entry.Canonical)}",
            CreateView = (entry, _) => CreateRelicView(entry),
            ViewReady = ApplyEntryVisual,
            UpdateView = (entry, view, _) => ApplyEntryVisual(entry, view),
            BindActivationWithCleanup = (entry, view, activate) =>
            {
                Action unbindLeft = LoadoutBag.BindRelicActivationWithCleanup(view, activate);
                Action? unbindRight = RelicModifier.BindRightClickWithCleanup(view, () => InspectEntry(entry));
                return () =>
                {
                    unbindLeft();
                    unbindRight?.Invoke();
                };
            }
        };

        Configure(session.Entries, adapter, builder =>
        {
            builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
            builder.Materialization(SelectMaterializationMode.Lazy);
            builder.HiddenPrewarm(false);
            builder.Layout(
                10,
                new Vector2(78f, 78f),
                32,
                34,
                paddingTop: 130f,
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

    private static Control CreateRelicView(RelicModificationImportEntry entry)
    {
        NRelicBasicHolder? holder = NRelicBasicHolder.Create(entry.GetIncomingPreview());
        if (holder is null)
            return new Control { CustomMinimumSize = new Vector2(78f, 78f) };
        holder.MouseFilter = MouseFilterEnum.Pass;
        holder.CustomMinimumSize = new Vector2(78f, 78f);
        return holder;
    }

    private void OnItemActivated(IGenericSelectItem item, SelectItemState _)
    {
        if (item.UntypedModel is not RelicModificationImportEntry entry)
            return;
        if (string.Equals(_suppressActivationId, item.Id, StringComparison.Ordinal))
        {
            _suppressActivationId = null;
            return;
        }

        entry.Toggle();
        RefreshEntry(entry);
    }

    private void InspectEntry(RelicModificationImportEntry entry)
    {
        NModificationConflictOverlay.ShowRelic(
            this,
            entry,
            allowChoice: entry.HasConflict,
            resolved: () => OnConflictResolved(entry));
    }

    private void OnConflictResolved(RelicModificationImportEntry entry)
    {
        RefreshEntry(entry);
        Callable.From(() => OpenNextUnresolvedConflict(entry)).CallDeferred();
    }

    private void OpenNextUnresolvedConflict(RelicModificationImportEntry current)
    {
        if (_session is null
            || !GodotObject.IsInstanceValid(this)
            || GetNodeOrNull<NModificationConflictOverlay>("RelicModificationConflictChoice") is not null)
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
            RelicModificationImportEntry candidate = _session.Entries[(currentIndex + offset) % _session.Entries.Count];
            if (candidate.HasConflict && candidate.Decision == ModificationImportDecision.Unresolved)
            {
                NModificationConflictOverlay.ShowRelic(
                    this,
                    candidate,
                    allowChoice: true,
                    resolved: () => OnConflictResolved(candidate));
                return;
            }
        }
    }

    private void ApplyBulk(ModificationImportDecision decision)
    {
        foreach (RelicModificationImportEntry entry in _session!.Entries)
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
        foreach (RelicModificationImportEntry entry in _session!.Entries)
        {
            entry.SetDecision(entry.HasConflict
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

    private void RefreshEntry(RelicModificationImportEntry entry)
    {
        RefreshItemView(entry.Id.ToString());
        RefreshConfirmAvailability();
    }

    private static void ApplyEntryVisual(RelicModificationImportEntry entry, Control view)
    {
        view.Modulate = entry.Decision == ModificationImportDecision.KeepLocal
            ? new Color(0.42f, 0.42f, 0.42f, 0.5f)
            : Colors.White;

        if (!CommonHelpers.TryFindDescendantOrSelf(view, out NRelic relic)
            || relic.Outline is null)
        {
            return;
        }

        bool unresolved = entry.HasConflict
                          && entry.Decision == ModificationImportDecision.Unresolved;
        relic.Outline.Visible = unresolved;
        if (unresolved)
            relic.Outline.SelfModulate = new Color(1f, 0.28f, 0.14f, 0.95f);
    }

    private void BeginPaint(Vector2 pointer)
    {
        _pressEntry = FindEntryAt(pointer);
        if (_pressEntry is null)
            return;

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

    private void Paint(RelicModificationImportEntry entry)
    {
        if (!_paintedIds.Add(entry.Id.ToString()))
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

    private RelicModificationImportEntry? FindEntryAt(Vector2 pointer)
    {
        foreach (IGenericSelectItem item in Items)
        {
            if (item.View is { } view
                && GodotObject.IsInstanceValid(view)
                && view.IsVisibleInTree()
                && view.GetGlobalRect().HasPoint(pointer)
                && item.UntypedModel is RelicModificationImportEntry entry)
            {
                return entry;
            }
        }
        return null;
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
}
