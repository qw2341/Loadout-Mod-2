#nullable enable

namespace Loadout.UI.Screens;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Loadout.Keywords;
using Loadout.PanelItems;
using Loadout.Patches.Cards.CardModification;
using Loadout.Services.CardModification;
using Loadout.Services.Targets;
using Loadout.UI.Managers;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Runs;

public partial class NCardUpgradeModificationScreen : Control
{
    private const string ScenePath =
        "res://UI/Screens/CardUpgradeModificationScreen.tscn";
    private const float EditorRowWidth = 426f;
    private const float EditorLabelWidth = 184f;
    private const float StepperWidth = 176f;

    private sealed record DynamicVarEditorDefinition(
        string Name,
        string Label,
        int Minimum,
        int Maximum);

    private LoadoutOwnedItem<CardModel>? _item;
    private CardModificationSpec _baseState = new();
    private CardUpgradeModificationSpec _permanentUpgrade = new();
    private CardUpgradeModificationSpec _draft = new();
    private Action<CardUpgradeModificationSpec>? _save;
    private VBoxContainer? _leftControls;
    private VBoxContainer? _rightControls;
    private Control? _previewHost;
    private Control? _backButtonMount;
    private NCardUpgradePreview? _upgradePreview;
    private NBackButton? _backButton;
    private CardModel? _previewSource;
    private CardModel? _previewUpgrade;
    private HashSet<CardKeyword> _nativeUpgradeKeywords = [];
    private Dictionary<string, DynamicVarEditorDefinition>
        _nativeDynamicVars = new(StringComparer.Ordinal);
    private string _selectedKeywordModId = NCardKeywordEditor.AllModFilterId;
    private bool _rebuildQueued;
    private bool _wasVisible;
    private bool _saved;

    public static NCardUpgradeModificationScreen Create()
    {
        if (ResourceLoader.Exists(ScenePath)
            && GD.Load<PackedScene>(ScenePath) is { } scene
            && scene.Instantiate<NCardUpgradeModificationScreen>() is { } screen)
        {
            return screen;
        }

        GD.PushWarning(
            $"CardModification: could not load scene '{ScenePath}'.");
        return new NCardUpgradeModificationScreen();
    }

    public void Init(
        LoadoutOwnedItem<CardModel> item,
        CardModificationSpec state,
        Action<CardUpgradeModificationSpec> save)
    {
        _item = item;
        _baseState = state.Clone();
        _permanentUpgrade =
            CardModificationRuntime.GetPermanentSpec(item.Model.Id)
                .UpgradeModification.Clone();
        _draft = state.UpgradeModification.Clone();
        _save = save;
        if (IsNodeReady())
            QueueRebuild();
    }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 130;
        _leftControls = GetNodeOrNull<VBoxContainer>(
            "%LeftControls");
        if (_leftControls is not null)
            _leftControls.CustomMinimumSize = new Vector2(EditorRowWidth, 0f);
        _rightControls = GetNodeOrNull<VBoxContainer>(
            "%RightControls");
        _previewHost = GetNodeOrNull<Control>("%PreviewHost");
        _backButtonMount = GetNodeOrNull<Control>("%BackButtonMount");
        AddTitle();
        EnsureUpgradePreview();
        EnsureBackButton();
        VisibilityChanged += OnVisibilityChanged;
        QueueRebuild();
    }

    public override void _ExitTree()
    {
        VisibilityChanged -= OnVisibilityChanged;
        SaveOnce();
        ReleasePreviewCards();
    }

    private void Rebuild()
    {
        if (_item is null
            || _leftControls is null
            || _rightControls is null
            || _upgradePreview is null
            || !_upgradePreview.IsNodeReady())
        {
            return;
        }

        BuildNativeUpgradeBaseline();
        RebuildLeftControls();
        RebuildKeywordControls();
        RefreshPreview();
    }

    private void QueueRebuild()
    {
        if (_rebuildQueued)
            return;

        _rebuildQueued = true;
        Callable.From(() =>
        {
            _rebuildQueued = false;
            if (IsInsideTree())
                Rebuild();
        }).CallDeferred();
    }

    private void RebuildLeftControls()
    {
        if (_leftControls is null)
            return;

        ClearChildren(_leftControls);
        _leftControls.AddChild(CreateSectionLabel(
            LocMan.Loc(
                "CARD_MOD_UPGRADE_DELTAS",
                "Upgrade Deltas")));

        AddStepperRow(
            _leftControls,
            LocMan.Loc("CARD_MOD_ENERGY_COST", "Energy Cost"),
            _draft.EnergyCostDelta ?? 0,
            int.MinValue,
            int.MaxValue,
            value =>
            {
                _draft.EnergyCostDelta = value;
                RefreshPreview();
            });
        AddStepperRow(
            _leftControls,
            LocMan.Loc("CARD_MOD_REPLAY_COUNT", "Replay Count"),
            _draft.BaseReplayCountDelta ?? 0,
            int.MinValue,
            int.MaxValue,
            value =>
            {
                _draft.BaseReplayCountDelta = value;
                RefreshPreview();
            });
        AddStepperRow(
            _leftControls,
            LocMan.Loc("CARD_MOD_STAR_COST", "Star Cost"),
            _draft.BaseStarCostDelta ?? 0,
            int.MinValue,
            int.MaxValue,
            value =>
            {
                _draft.BaseStarCostDelta = value;
                RefreshPreview();
            });

        Dictionary<string, DynamicVarEditorDefinition> definitions =
            new(_nativeDynamicVars, StringComparer.Ordinal);
        foreach (LoadoutKeywordModel keyword in LoadoutKeywordRegistry.All)
        {
            if (!IsKeywordEnabled(keyword.Keyword))
                continue;

            foreach (LoadoutKeywordDynamicVarDefinition dynamicVar
                     in keyword.DynamicVars)
            {
                definitions[dynamicVar.Name] =
                    new DynamicVarEditorDefinition(
                        dynamicVar.Name,
                        LocMan.Loc(
                            dynamicVar.LabelLocKey,
                            dynamicVar.Name),
                        dynamicVar.Minimum,
                        dynamicVar.Maximum);
            }
        }

        if (definitions.Count == 0)
        {
            MegaLabel empty = CreateLabel(
                LocMan.Loc(
                    "CARD_MOD_UPGRADE_NO_DYNAMIC_VARS",
                    "No dynamic variables"),
                20,
                StsColors.cream);
            empty.CustomMinimumSize = new Vector2(0f, 44f);
            _leftControls.AddChild(empty);
            return;
        }

        foreach (DynamicVarEditorDefinition definition in definitions.Values
                     .OrderBy(value => value.Name, StringComparer.Ordinal))
        {
            int value = Decimal.ToInt32(
                _draft.DynamicVarDeltas.GetValueOrDefault(definition.Name));
            string name = definition.Name;
            AddStepperRow(
                _leftControls,
                definition.Label,
                value,
                definition.Minimum,
                definition.Maximum,
                next =>
                {
                    _draft.DynamicVarDeltas[name] = next;
                    RefreshPreview();
                });
        }
    }

    private void RebuildKeywordControls()
    {
        if (_rightControls is null || _item is null)
            return;

        ClearChildren(_rightControls);
        NCardKeywordEditor editor = new();
        editor.Init(
            [_item.Model],
            IsKeywordEnabled,
            OnKeywordChanged,
            _selectedKeywordModId,
            selected => _selectedKeywordModId = selected);
        _rightControls.AddChild(editor);
    }

    private bool IsKeywordEnabled(CardKeyword keyword)
    {
        string key = LoadoutKeywords.GetStorageKey(keyword);
        return _draft.KeywordOverrides.TryGetValue(key, out bool enabled)
            ? enabled
            : _nativeUpgradeKeywords.Contains(keyword);
    }

    private void OnKeywordChanged(CardKeyword keyword, bool enabled)
    {
        string key = LoadoutKeywords.GetStorageKey(keyword);
        bool nativeEnabled = _nativeUpgradeKeywords.Contains(keyword);
        if (enabled == nativeEnabled)
        {
            if (_permanentUpgrade.KeywordOverrides.TryGetValue(
                    key,
                    out bool permanentEnabled)
                && permanentEnabled != enabled)
            {
                _draft.KeywordOverrides[key] = enabled;
            }
            else
            {
                _draft.KeywordOverrides.Remove(key);
            }
        }
        else
            _draft.KeywordOverrides[key] = enabled;

        if (LoadoutKeywordRegistry.TryGet(
                keyword,
                out LoadoutKeywordModel definition))
        {
            foreach (LoadoutKeywordDynamicVarDefinition dynamicVar
                     in definition.DynamicVars)
            {
                if (enabled)
                    _draft.DynamicVarDeltas.TryAdd(dynamicVar.Name, 0m);
                else
                    _draft.DynamicVarDeltas.Remove(dynamicVar.Name);
            }
        }

        RebuildLeftControls();
        RefreshPreview();
    }

    private void BuildNativeUpgradeBaseline()
    {
        _nativeUpgradeKeywords.Clear();
        _nativeDynamicVars.Clear();
        if (_item is null)
            return;

        CardModificationSpec nativeState = _baseState.Clone();
        nativeState.UpgradeModification = new CardUpgradeModificationSpec();
        CardModel? source =
            CardModificationRuntime.CreateUpgradePreviewSource(
                _item.Model,
                nativeState);
        if (source is null)
            return;

        CardModel? upgraded = null;
        try
        {
            ICardScope? scope = source.CardScope;
            if (scope is null)
                return;

            upgraded = scope.CloneCard(source);
            CardModificationFields.Clear(upgraded);
            using (CardUpgradeModificationRuntimePatches.BeginOverride(
                       new CardUpgradeModificationSpec()))
            {
                upgraded.UpgradeInternal();
            }
            foreach (CardKeyword keyword in upgraded.GetKeywordsWithSources(
                         KeywordSources.Local))
            {
                _nativeUpgradeKeywords.Add(keyword);
            }
            foreach ((string name, var dynamicVar) in upgraded.DynamicVars)
            {
                string label = LocMan.DynamicVarLoc(dynamicVar);
                int minimum = int.MinValue;
                int maximum = int.MaxValue;
                if (LoadoutKeywordRegistry.TryGetDynamicVar(
                        name,
                        out LoadoutKeywordDynamicVarDefinition definition))
                {
                    label = LocMan.Loc(definition.LabelLocKey, name);
                    minimum = definition.Minimum;
                    maximum = definition.Maximum;
                }
                _nativeDynamicVars[name] = new DynamicVarEditorDefinition(
                    name,
                    label,
                    minimum,
                    maximum);
            }
        }
        catch (Exception exception)
        {
            GD.PushWarning(
                $"CardModification: failed building native upgrade baseline for '{_item.Model.Id}'. {exception.Message}");
        }
        finally
        {
            CardModificationRuntime.ReleaseUpgradePreviewCard(upgraded);
            CardModificationRuntime.ReleaseUpgradePreviewCard(source);
        }
    }

    private void RefreshPreview()
    {
        if (_upgradePreview is null
            || !_upgradePreview.IsNodeReady()
            || _item is null)
            return;

        CardModificationSpec previewState = _baseState.Clone();
        previewState.UpgradeModification = _draft.Clone();
        CardModel? source =
            CardModificationRuntime.CreateUpgradePreviewSource(
                _item.Model,
                previewState);
        if (source is null)
        {
            ReleasePreviewCards();
            return;
        }

        CardModel? upgraded = null;
        try
        {
            ICardScope scope = source.CardScope
                               ?? throw new InvalidOperationException(
                                   "Upgrade preview source has no card scope.");
            upgraded = scope.CloneCard(source);
            using (CardUpgradeModificationRuntimePatches.BeginOverride(_draft))
                upgraded.UpgradeInternal();
            upgraded.UpgradePreviewType = CardUpgradePreviewType.Deck;

            _upgradePreview.SetCards(source, upgraded);
            CardModel? previousSource = _previewSource;
            CardModel? previousUpgrade = _previewUpgrade;
            _previewSource = source;
            _previewUpgrade = upgraded;
            source = null;
            upgraded = null;
            CardModificationRuntime.ReleaseUpgradePreviewCard(previousUpgrade);
            CardModificationRuntime.ReleaseUpgradePreviewCard(previousSource);
        }
        catch (Exception exception)
        {
            GD.PushWarning(
                $"CardModification: failed refreshing upgrade preview for '{_item.Model.Id}'. {exception.Message}");
            ReleasePreviewCards();
            CardModificationRuntime.ReleaseUpgradePreviewCard(upgraded);
            CardModificationRuntime.ReleaseUpgradePreviewCard(source);
        }
    }

    private void ReleasePreviewCards()
    {
        if (_upgradePreview is not null
            && GodotObject.IsInstanceValid(_upgradePreview))
            _upgradePreview.ClearCards();

        CardModificationRuntime.ReleaseUpgradePreviewCard(_previewUpgrade);
        CardModificationRuntime.ReleaseUpgradePreviewCard(_previewSource);
        _previewUpgrade = null;
        _previewSource = null;
    }

    private void EnsureUpgradePreview()
    {
        if (_previewHost is null || _upgradePreview is not null)
            return;

        _upgradePreview = new NCardUpgradePreview
        {
            Name = "UpgradePreview"
        };
        _upgradePreview.SetAnchorsPreset(LayoutPreset.Center);
        _previewHost.AddChild(_upgradePreview);
        if (!_upgradePreview.IsNodeReady())
        {
            _upgradePreview.Connect(
                Node.SignalName.Ready,
                Callable.From(QueueRebuild),
                (uint)ConnectFlags.OneShot);
        }
    }

    private void AddTitle()
    {
        Control? titleHost = GetNodeOrNull<Control>("%Title");
        if (titleHost is null)
            return;

        MegaLabel title = CreateLabel(
            LocMan.Loc(
                "CARD_MOD_MODIFY_UPGRADE",
                "Modify Upgrade"),
            34,
            StsColors.gold);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.SetAnchorsPreset(LayoutPreset.FullRect);
        titleHost.AddChild(title);
    }

    private void EnsureBackButton()
    {
        if (_backButtonMount is null)
            return;

        NBackButton button = NLoadoutBackButtonFactory.Create();
        button.Name = "BackButton";
        button.Connect(
            NClickableControl.SignalName.Released,
            Callable.From<NClickableControl>(_ =>
            {
                NLoadoutBackButtonFactory.ResetVisualState(button);
                SaveOnce();
                NLoadoutPanelRoot.CloseTopLoadoutScreen();
            }));
        _backButtonMount.AddChild(button);
        _backButton = button;
    }

    private void OnVisibilityChanged()
    {
        if (Visible)
        {
            _wasVisible = true;
            _backButton?.SetEnabled(true);
            return;
        }

        _backButton?.SetEnabled(false);
        if (!_wasVisible)
            return;

        SaveOnce();
        ReleasePreviewCards();
        Callable.From(QueueFree).CallDeferred();
    }

    private void SaveOnce()
    {
        if (_saved)
            return;

        _saved = true;
        _draft.Normalize();
        _save?.Invoke(_draft.Clone());
    }

    private static Control CreateRow(string label, Control input)
    {
        Control row = new()
        {
            CustomMinimumSize = new Vector2(EditorRowWidth, 44f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        MegaLabel text = CreateLabel(label, 21, StsColors.cream);
        text.Position = Vector2.Zero;
        text.Size = new Vector2(EditorLabelWidth, 44f);
        row.AddChild(text);
        input.Position = new Vector2(
            EditorRowWidth - StepperWidth,
            1f);
        input.Size = new Vector2(StepperWidth, 42f);
        input.CustomMinimumSize = new Vector2(StepperWidth, 42f);
        row.AddChild(input);
        return row;
    }

    private static void AddStepperRow(
        VBoxContainer container,
        string label,
        int value,
        int minimum,
        int maximum,
        Action<int> onChanged)
    {
        NLoadoutNumberStepper stepper = new();
        stepper.Init(value, minimum, maximum);
        stepper.ValueChanged += onChanged;
        container.AddChild(CreateRow(label, stepper));
    }

    private static MegaLabel CreateSectionLabel(string text)
    {
        MegaLabel label = CreateLabel(text, 25, StsColors.gold);
        label.CustomMinimumSize = new Vector2(0f, 42f);
        return label;
    }

    private static MegaLabel CreateLabel(
        string text,
        int fontSize,
        Color color)
    {
        MegaLabel label = new()
        {
            Text = text,
            AutoSizeEnabled = false,
            MinFontSize = Math.Max(12, fontSize - 8),
            MaxFontSize = fontSize,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        label.AddThemeFontOverride("font", CommonHelpers.LoadGameFont());
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    private static void ClearChildren(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            node.RemoveChild(child);
            child.QueueFree();
        }
    }
}
