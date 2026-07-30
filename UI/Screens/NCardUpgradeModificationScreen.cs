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
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Runs;

public partial class NCardUpgradeModificationScreen : Control
{
    private const string ScenePath =
        "res://UI/Screens/CardUpgradeModificationScreen.tscn";
    private const string NativePreviewPath = "cards/upgrade_preview";

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
    private NUpgradePreview? _upgradePreview;
    private NBackButton? _backButton;
    private CardModel? _previewSource;
    private HashSet<CardKeyword> _nativeUpgradeKeywords = [];
    private Dictionary<string, DynamicVarEditorDefinition>
        _nativeDynamicVars = new(StringComparer.Ordinal);
    private string _selectedKeywordModId = NCardKeywordEditor.AllModFilterId;
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
            Rebuild();
    }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 130;
        _leftControls = GetNodeOrNull<VBoxContainer>(
            "%LeftControls");
        _rightControls = GetNodeOrNull<VBoxContainer>(
            "%RightControls");
        _previewHost = GetNodeOrNull<Control>("%PreviewHost");
        _backButtonMount = GetNodeOrNull<Control>("%BackButtonMount");
        AddTitle();
        EnsureNativePreview();
        EnsureBackButton();
        VisibilityChanged += OnVisibilityChanged;
        Rebuild();
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
            || _upgradePreview is null)
        {
            return;
        }

        BuildNativeUpgradeBaseline();
        RebuildLeftControls();
        RebuildKeywordControls();
        RefreshPreview();
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
            NLoadoutNumberStepper stepper = new();
            stepper.Init(
                value,
                definition.Minimum,
                definition.Maximum);
            string name = definition.Name;
            stepper.ValueChanged += next =>
            {
                _draft.DynamicVarDeltas[name] = next;
                RefreshPreview();
            };
            _leftControls.AddChild(
                CreateRow(definition.Label, stepper));
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
        if (_upgradePreview is null || _item is null)
            return;

        ReleasePreviewCards();
        CardModificationSpec previewState = _baseState.Clone();
        previewState.UpgradeModification = _draft.Clone();
        _previewSource =
            CardModificationRuntime.CreateUpgradePreviewSource(
                _item.Model,
                previewState);
        if (_previewSource is null)
        {
            _upgradePreview.Card = null;
            return;
        }

        using (CardUpgradeModificationRuntimePatches.BeginOverride(_draft))
            _upgradePreview.Card = _previewSource;
    }

    private void ReleasePreviewCards()
    {
        if (_upgradePreview is not null
            && GodotObject.IsInstanceValid(_upgradePreview))
        {
            Control? after = _upgradePreview.GetNodeOrNull<Control>("%After");
            if (after is not null)
            {
                foreach (NPreviewCardHolder holder in after.GetChildren()
                             .OfType<NPreviewCardHolder>())
                {
                    CardModificationRuntime.ReleaseUpgradePreviewCard(
                        holder.CardNode?.Model);
                }
            }
            _upgradePreview.Card = null;
        }

        CardModificationRuntime.ReleaseUpgradePreviewCard(_previewSource);
        _previewSource = null;
    }

    private void EnsureNativePreview()
    {
        if (_previewHost is null || _upgradePreview is not null)
            return;

        try
        {
            string path = SceneHelper.GetScenePath(NativePreviewPath);
            PackedScene scene = PreloadManager.Cache.GetScene(path);
            _upgradePreview = scene.Instantiate<NUpgradePreview>(
                PackedScene.GenEditState.Disabled);
            _upgradePreview.Name = "UpgradePreview";
            _upgradePreview.SetAnchorsPreset(LayoutPreset.Center);
            _previewHost.AddChild(_upgradePreview);
        }
        catch (Exception exception)
        {
            GD.PushWarning(
                $"CardModification: failed creating native upgrade preview. {exception.Message}");
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
        HBoxContainer row = new()
        {
            CustomMinimumSize = new Vector2(0f, 44f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        row.AddThemeConstantOverride("separation", 8);
        MegaLabel text = CreateLabel(label, 21, StsColors.cream);
        text.CustomMinimumSize = new Vector2(184f, 44f);
        text.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(text);
        input.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        row.AddChild(input);
        return row;
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
