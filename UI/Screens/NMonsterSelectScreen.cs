#nullable enable

namespace Loadout.UI.Screens;

using Godot;
using System;
using System.Collections.Generic;

public partial class NMonsterSelectScreen : NGenericSelectScreen
{
    public const string ScenePath = "res://UI/Screens/MonsterSelectScreen.tscn";

    private const int InitialMonsterBudget = 4;
    private const int HiddenPrewarmBatch = 1;
    private const int VisibleWarmBatch = 1;
    private const double HiddenPrewarmFrameBudgetMsec = 0.35d;
    private const float WarmRowsBehind = 3f;
    private const float WarmRowsAhead = 8f;

    private readonly HashSet<Control> _activeViewportViews = new();
    private readonly HashSet<Control> _nextViewportViews = new();
    private readonly HashSet<Control> _pendingCullViews = new();
    private float _retainedLayoutViewportWidth = float.NaN;
    private float _lastWarmTarget = float.NaN;
    private ulong _lastWarmGeneration = ulong.MaxValue;
    private bool _viewportWarmPending = true;

    protected override void OnItemsConfigured()
    {
        _activeViewportViews.Clear();
        _nextViewportViews.Clear();
        _pendingCullViews.Clear();
        _retainedLayoutViewportWidth = float.NaN;
        _lastWarmTarget = float.NaN;
        _lastWarmGeneration = ulong.MaxValue;
        _viewportWarmPending = true;
        foreach (IGenericSelectItem item in ConfiguredItems)
        {
            if (item.View is Control view && GodotObject.IsInstanceValid(view))
                _pendingCullViews.Add(view);
        }

        SetHiddenPrewarmEnabled(true);
        SetMaterializationMode(SelectMaterializationMode.Lazy);
    }

    public override void _Process(double delta)
    {
        base._Process(delta);

        if (CurrentMaterializationMode != SelectMaterializationMode.Lazy
            || ConfiguredItemCount == 0)
        {
            return;
        }

        bool layoutChanged = _lastWarmGeneration != CurrentLayoutGeneration;
        bool targetChanged = float.IsNaN(_lastWarmTarget)
                             || Mathf.Abs(_lastWarmTarget - TargetScrollOffset) > 0.5f;
        if (layoutChanged || targetChanged)
        {
            _lastWarmGeneration = CurrentLayoutGeneration;
            _lastWarmTarget = TargetScrollOffset;
            _viewportWarmPending = true;
        }

        if (!_viewportWarmPending)
            return;

        int materialized = MaterializeSpecializationWindow(
            TargetScrollOffset,
            WarmRowsBehind,
            WarmRowsAhead,
            VisibleWarmBatch,
            updateExistingViews: false);
        if (materialized > 0)
            RefreshSpecializationViewportCulling();

        if (materialized == 0)
            _viewportWarmPending = false;
    }

    public override void _ExitTree()
    {
        _activeViewportViews.Clear();
        _nextViewportViews.Clear();
        _pendingCullViews.Clear();
        base._ExitTree();
    }

    protected override void OnItemViewMaterialized(Control view)
    {
        _pendingCullViews.Add(view);
    }

    protected override IReadOnlyList<IGenericSelectItem> BuildHiddenPrewarmItemList()
    {
        _retainedLayoutViewportWidth = CurrentViewportLayoutWidth;
        return base.BuildHiddenPrewarmItemList();
    }

    protected override int GetHiddenPrewarmBatchSize() => HiddenPrewarmBatch;
    protected override double GetHiddenPrewarmFrameBudgetMsec() => HiddenPrewarmFrameBudgetMsec;
    protected override int GetInitialMaterializeBudget() => InitialMonsterBudget;
    protected override int GetRemovalMaterializeBudget() => InitialMonsterBudget;
    protected override int GetScrollMaterializeBudget() => 0;
    protected override float GetMaterializeRowsBehind() => WarmRowsBehind;
    protected override float GetMaterializeRowsAhead() => WarmRowsAhead;

    protected override void ApplyRetainedItemLayouts()
    {
        float currentWidth = CurrentViewportLayoutWidth;
        if (float.IsNaN(_retainedLayoutViewportWidth))
        {
            _retainedLayoutViewportWidth = currentWidth;
            base.ApplyRetainedItemLayouts();
            return;
        }

        if (Mathf.Abs(currentWidth - _retainedLayoutViewportWidth) <= 0.5f)
            return;

        _retainedLayoutViewportWidth = currentWidth;
        base.ApplyRetainedItemLayouts();
    }

    protected override void ApplyViewportCulling(float cullTop, float cullBottom)
    {
        CullNonItemLayoutNodes(cullTop, cullBottom);

        _nextViewportViews.Clear();
        CollectMaterializedItemViewsInWindow(cullTop, cullBottom, _nextViewportViews);

        foreach (Control view in _activeViewportViews)
        {
            if (!_nextViewportViews.Contains(view) && GodotObject.IsInstanceValid(view))
                SetLayoutNodeActive(view, active: false);
        }

        foreach (Control view in _pendingCullViews)
        {
            if (!_nextViewportViews.Contains(view) && GodotObject.IsInstanceValid(view))
                SetLayoutNodeActive(view, active: false);
        }

        foreach (Control view in _nextViewportViews)
        {
            if (GodotObject.IsInstanceValid(view))
                SetLayoutNodeActive(view, active: true);
        }

        _pendingCullViews.Clear();
        _activeViewportViews.Clear();
        _activeViewportViews.UnionWith(_nextViewportViews);
    }

    protected override void SetLayoutNodeActive(Control control, bool active)
    {
        if (control.Visible != active)
            control.Visible = active;

        ProcessModeEnum desiredMode = active
            ? ProcessModeEnum.Inherit
            : ProcessModeEnum.Disabled;
        if (control.ProcessMode != desiredMode)
            control.ProcessMode = desiredMode;
    }

    protected override void RecycleDistantItemViews()
    {
        // NCreature cannot be safely rebound after leaving the tree.
    }
}
