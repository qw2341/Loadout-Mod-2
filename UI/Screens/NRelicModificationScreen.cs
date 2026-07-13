#nullable enable

namespace Loadout.UI.Screens;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Loadout.PanelItems;
using Loadout.Services.Actions;
using Loadout.Services.RelicModification;
using Loadout.Services.Targets;
using Loadout.Services.TildeKey;
using Loadout.UI.Managers;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.HoverTips;

public partial class NRelicModificationScreen : Control
{
    private const string ScenePath = "res://UI/Screens/RelicModificationScreen.tscn";
    private const float ActionButtonWidth = 318f;
    private const float ActionButtonHeight = 42f;
    private const float RelicEditButtonWidth = 164f;

    private LoadoutOwnedItem<RelicModel>? _item;
    private List<LoadoutOwnedItem<RelicModel>> _items = [];
    private int _itemIndex;
    private Action<LoadoutOwnedItem<RelicModel>>? _parentRefresh;
    private RelicModificationState _workingState = new();
    private VBoxContainer? _leftControls;
    private VBoxContainer? _rightControls;
    private VBoxContainer? _actionControls;
    private HBoxContainer? _relicEditActions;
    private Control? _backButtonMount;
    private Control? _previewHost;
    private Control? _leftArrowMount;
    private Control? _rightArrowMount;
    private Control? _nativeHoverTipAnchor;
    private NButton? _leftArrow;
    private NButton? _rightArrow;
    private NBackButton? _backButton;
    private MegaLabel? _nameLabel;
    private MegaLabel? _rarityLabel;
    private MegaRichTextLabel? _descriptionLabel;
    private MegaRichTextLabel? _flavorLabel;
    private TextureRect? _relicImage;
    private ShaderMaterial? _frameMaterial;
    private Control? _textEditorOverlay;
    private bool _signalsBound;
    private bool _runContentEventsBound;
    private bool _hasPendingTemporaryCommit;
    private bool _isClosing;
    private bool _reloadAfterNextUpdate;
    private bool _previewRefreshQueued;

    public static NRelicModificationScreen Create()
    {
        if (ResourceLoader.Exists(ScenePath)
            && GD.Load<PackedScene>(ScenePath) is { } scene
            && scene.Instantiate<NRelicModificationScreen>() is { } screen)
        {
            return screen;
        }

        GD.PushWarning($"RelicModification: could not load scene '{ScenePath}'. Falling back to script-only screen.");
        return new NRelicModificationScreen();
    }

    public void Init(
        LoadoutOwnedItem<RelicModel> item,
        IReadOnlyList<LoadoutOwnedItem<RelicModel>>? items = null,
        Action<LoadoutOwnedItem<RelicModel>>? parentRefresh = null)
    {
        _items = items?.Count > 0 ? items.ToList() : [item];
        _itemIndex = Math.Max(0, _items.FindIndex(candidate => SameOwnedItem(candidate, item)));
        _parentRefresh = parentRefresh;
        LoadItem(_items[_itemIndex]);

        if (IsNodeReady())
            RebuildScreen();
    }

    public override void _Ready()
    {
        ApplyFullRectLayout(this);
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 120;
        BindSceneNodes();
        BindRunContentEvents();
        RebuildScreen();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized && !_isClosing)
        {
            ApplyFullRectLayout(this);
            RefreshPreview();
        }

        if (what == NotificationVisibilityChanged)
        {
            RefreshNativeButtonState();
            if (!Visible)
            {
                CloseTextEditor();
                ClearHoverTips();
            }
        }
    }

    public override void _ExitTree()
    {
        BeginClose();
        CloseTextEditor();
        ClearHoverTips();
    }

    private void BindRunContentEvents()
    {
        if (_runContentEventsBound)
            return;

        LoadoutRunContentChangeService.Changed += OnRunContentChanged;
        _runContentEventsBound = true;
    }

    private void UnbindRunContentEvents()
    {
        if (!_runContentEventsBound)
            return;

        LoadoutRunContentChangeService.Changed -= OnRunContentChanged;
        _runContentEventsBound = false;
    }

    private void LoadItem(LoadoutOwnedItem<RelicModel> item)
    {
        _item = item;
        _workingState = RelicModificationStateService.GetEffectiveState(item.Model);

        foreach ((string key, var dynamicVar) in item.Model.DynamicVars)
            _workingState.DynamicVars.TryAdd(key, dynamicVar.BaseValue);

        foreach (RelicSavedPropertyDescriptor descriptor in RelicModificationStateService.GetSavedPropertyDescriptors(item.Model))
        {
            if (_workingState.PrimitiveValues.ContainsKey(descriptor.Key))
                continue;

            try
            {
                _workingState.PrimitiveValues[descriptor.Key] = RelicPrimitiveValue.FromObject(
                    descriptor.GetValue(item.Model), descriptor.ValueType);
            }
            catch (Exception exception)
            {
                GD.PushWarning($"RelicModification: failed reading saved property '{descriptor.Key}'. {exception.Message}");
            }
        }

        _hasPendingTemporaryCommit = false;
    }

    private void RebuildScreen()
    {
        if (_item is null)
            return;

        BindSceneNodes();
        RebuildControls();
        RefreshPreview();
    }

    private void BindSceneNodes()
    {
        EnsureFallbackScene();
        _backButtonMount = GetNodeOrNull<Control>("%BackButtonMount");
        _leftControls = GetNodeOrNull<VBoxContainer>("%LeftControls");
        _rightControls = GetNodeOrNull<VBoxContainer>("%RightControls");
        _actionControls = GetNodeOrNull<VBoxContainer>("%ActionRow");
        _relicEditActions = GetNodeOrNull<HBoxContainer>("%RelicEditActions");
        _previewHost = GetNodeOrNull<Control>("%PreviewRelicHost");
        _leftArrowMount = GetNodeOrNull<Control>("%LeftArrow");
        _rightArrowMount = GetNodeOrNull<Control>("%RightArrow");
        _nativeHoverTipAnchor = GetNodeOrNull<Control>("%NativeHoverTipAnchor");
        _leftArrow = EnsureInspectArrowButton(_leftArrowMount, isLeft: true);
        _rightArrow = EnsureInspectArrowButton(_rightArrowMount, isLeft: false);
        EnsureInspectionPresentation();
        EnsureBackButton();
        BindSceneSignals();
    }

    private void EnsureFallbackScene()
    {
        if (GetNodeOrNull<Control>("LeftControls") is not null || GetNodeOrNull<Control>("%LeftControls") is not null)
            return;

        VBoxContainer left = new() { Name = "LeftControls", UniqueNameInOwner = true };
        left.SetAnchorsPreset(LayoutPreset.LeftWide);
        left.CustomMinimumSize = new Vector2(438f, 0f);
        AddChild(left);

        VBoxContainer right = new() { Name = "RightControls", UniqueNameInOwner = true };
        right.SetAnchorsPreset(LayoutPreset.RightWide);
        right.CustomMinimumSize = new Vector2(438f, 0f);
        AddChild(right);

        VBoxContainer actions = new() { Name = "ActionRow", UniqueNameInOwner = true };
        actions.SetAnchorsPreset(LayoutPreset.BottomLeft);
        AddChild(actions);

        Control preview = new() { Name = "PreviewRelicHost", UniqueNameInOwner = true };
        preview.SetAnchorsPreset(LayoutPreset.Center);
        preview.Size = new Vector2(864f, 864f);
        preview.Position = -preview.Size * 0.5f;
        AddChild(preview);

        HBoxContainer editActions = new() { Name = "RelicEditActions", UniqueNameInOwner = true };
        editActions.SetAnchorsPreset(LayoutPreset.Center);
        editActions.Position = new Vector2(-270f, 456f);
        AddChild(editActions);

        foreach ((string name, float x) in new[] { ("LeftArrow", -488f), ("RightArrow", 360f) })
        {
            Control arrow = new() { Name = name, UniqueNameInOwner = true };
            arrow.SetAnchorsPreset(LayoutPreset.Center);
            arrow.Position = new Vector2(x, -64f);
            arrow.Size = new Vector2(128f, 128f);
            AddChild(arrow);
        }

        Control hover = new() { Name = "NativeHoverTipAnchor", UniqueNameInOwner = true };
        AddChild(hover);
        Control back = new() { Name = "BackButtonMount", UniqueNameInOwner = true };
        back.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(back);
    }

    private static NButton? EnsureInspectArrowButton(Control? mount, bool isLeft)
    {
        if (mount is null)
            return null;

        if (mount.GetNodeOrNull<NButton>("ArrowButton") is { } existing)
            return existing;

        ShaderMaterial? material = CreateHsvMaterial(1f, 1f, 0.9f);
        NButton button = material is null ? new NButton() : new NGoldArrowButton();
        button.Name = "ArrowButton";
        button.FocusMode = FocusModeEnum.All;
        button.MouseFilter = MouseFilterEnum.Stop;
        button.PivotOffset = new Vector2(64f, 64f);
        button.SetAnchorsPreset(LayoutPreset.FullRect);

        TextureRect image = new()
        {
            Name = "TextureRect",
            Texture = LoadTexture(isLeft
                ? "res://images/packed/common_ui/settings_tiny_left_arrow.png"
                : "res://images/packed/common_ui/settings_tiny_right_arrow.png"),
            Material = material,
            ExpandMode = TextureRect.ExpandModeEnum.FitWidthProportional,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore,
            PivotOffset = new Vector2(64f, 64f)
        };
        image.SetAnchorsPreset(LayoutPreset.FullRect);
        button.AddChild(image);
        mount.AddChild(button);
        return button;
    }

    private void EnsureInspectionPresentation()
    {
        if (_previewHost is null)
            return;

        if (_previewHost.GetNodeOrNull<Control>("Popup") is not null)
        {
            BindInspectionNodes();
            return;
        }

        Control popup = new()
        {
            Name = "Popup",
            CustomMinimumSize = new Vector2(864f, 864f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        popup.SetAnchorsPreset(LayoutPreset.Center);
        popup.OffsetLeft = -432f;
        popup.OffsetTop = -432f;
        popup.OffsetRight = 432f;
        popup.OffsetBottom = 432f;
        _previewHost.AddChild(popup);

        TextureRect background = new()
        {
            Name = "Bg",
            Texture = LoadTexture("res://images/ui/reward_screen/reward_panel.png"),
            ExpandMode = TextureRect.ExpandModeEnum.FitWidth,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        background.SetAnchorsPreset(LayoutPreset.FullRect);
        popup.AddChild(background);

        _nameLabel = CreateLabel(string.Empty, 36, StsColors.gold);
        _nameLabel.Name = "RelicName";
        _nameLabel.SetAnchorsPreset(LayoutPreset.Center);
        _nameLabel.Position = new Vector2(-385f, -392f);
        _nameLabel.Size = new Vector2(770f, 64f);
        _nameLabel.HorizontalAlignment = HorizontalAlignment.Center;
        popup.AddChild(_nameLabel);

        _rarityLabel = CreateLabel(string.Empty, 24, StsColors.cream);
        _rarityLabel.Name = "Rarity";
        _rarityLabel.SetAnchorsPreset(LayoutPreset.Center);
        _rarityLabel.Position = new Vector2(-221f, -330f);
        _rarityLabel.Size = new Vector2(442f, 137f);
        _rarityLabel.HorizontalAlignment = HorizontalAlignment.Center;
        popup.AddChild(_rarityLabel);

        Control frame = new() { Name = "RelicFrame", CustomMinimumSize = new Vector2(304f, 304f), MouseFilter = MouseFilterEnum.Ignore };
        frame.SetAnchorsPreset(LayoutPreset.Center);
        frame.Position = new Vector2(-152f, -265f);
        frame.Size = new Vector2(304f, 304f);
        popup.AddChild(frame);

        _relicImage = new TextureRect
        {
            Name = "RelicImage",
            CustomMinimumSize = new Vector2(192f, 192f),
            ExpandMode = TextureRect.ExpandModeEnum.FitWidth,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _relicImage.SetAnchorsPreset(LayoutPreset.Center);
        _relicImage.Position = new Vector2(-96f, -96f);
        _relicImage.Size = new Vector2(192f, 192f);
        frame.AddChild(_relicImage);

        _frameMaterial = CreateHsvMaterial(1f, 1f, 1f);
        TextureRect frameImage = new()
        {
            Name = "Frame",
            Texture = LoadTexture("res://images/packed/inspect_relic_screen/relic_inspect_frame.png"),
            Material = _frameMaterial,
            ExpandMode = TextureRect.ExpandModeEnum.FitWidth,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        frameImage.SetAnchorsPreset(LayoutPreset.FullRect);
        frame.AddChild(frameImage);

        VBoxContainer text = new() { Name = "Text", MouseFilter = MouseFilterEnum.Ignore };
        text.SetAnchorsPreset(LayoutPreset.Center);
        text.Position = new Vector2(-261f, 83f);
        text.Size = new Vector2(522f, 288f);
        popup.AddChild(text);

        _descriptionLabel = CreateRichLabel(28, CommonHelpers.LoadGameFont(), StsColors.cream);
        _descriptionLabel.Name = "RelicDescription";
        _descriptionLabel.CustomMinimumSize = new Vector2(480f, 0f);
        text.AddChild(_descriptionLabel);

        text.AddChild(CreateSpacer(44f));

        _flavorLabel = CreateRichLabel(22, LoadFont("res://themes/bitter_medium_italic_glyph_space_one.tres"), StsColors.cream);
        _flavorLabel.Name = "FlavorText";
        _flavorLabel.CustomMinimumSize = new Vector2(520f, 0f);
        text.AddChild(_flavorLabel);
    }

    private void BindInspectionNodes()
    {
        if (_previewHost is null)
            return;

        _nameLabel = _previewHost.GetNodeOrNull<MegaLabel>("Popup/RelicName");
        _rarityLabel = _previewHost.GetNodeOrNull<MegaLabel>("Popup/Rarity");
        _descriptionLabel = _previewHost.GetNodeOrNull<MegaRichTextLabel>("Popup/Text/RelicDescription");
        _flavorLabel = _previewHost.GetNodeOrNull<MegaRichTextLabel>("Popup/Text/FlavorText");
        _relicImage = _previewHost.GetNodeOrNull<TextureRect>("Popup/RelicFrame/RelicImage");
        _frameMaterial = _previewHost.GetNodeOrNull<TextureRect>("Popup/RelicFrame/Frame")?.Material as ShaderMaterial;
    }

    private void EnsureBackButton()
    {
        if (_backButtonMount is null)
            return;

        if (_backButtonMount.GetNodeOrNull<NBackButton>("BackButton") is { } existing)
        {
            _backButton = existing;
            RefreshNativeButtonState();
            return;
        }

        NBackButton backButton = NLoadoutBackButtonFactory.Create();
        backButton.Name = "BackButton";
        backButton.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ =>
        {
            NLoadoutBackButtonFactory.ResetVisualState(backButton);
            Close();
        }));
        _backButtonMount.AddChild(backButton);
        _backButton = backButton;
        Callable.From(RefreshNativeButtonState).CallDeferred();
    }

    private void BindSceneSignals()
    {
        if (_signalsBound)
            return;

        if (_leftArrow is not null)
            _leftArrow.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => Navigate(-1)));
        if (_rightArrow is not null)
            _rightArrow.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => Navigate(1)));
        _signalsBound = true;
    }

    private void RebuildControls()
    {
        if (_leftControls is null || _rightControls is null || _actionControls is null || _item is null)
            return;

        ClearChildren(_leftControls);
        ClearChildren(_rightControls);
        ClearChildren(_actionControls);
        if (_relicEditActions is not null)
            ClearChildren(_relicEditActions);

        _leftControls.AddChild(CreateLabel(GetTitleSafely(_item.Model), 32, StsColors.gold));
        _leftControls.AddChild(CreateLabel(_item.Model.Id.ToString(), 18, StsColors.cream));
        _leftControls.AddChild(CreateSpacer(6f));

        AddDropdownRow(
            _leftControls,
            LocMan.GameLoc("main_menu_ui", "CARD_LIBRARY_RARITY", LocMan.Loc("FILTER_GROUP_RARITY", "Rarity")),
            Enum.GetValues<RelicRarity>()
                .Where(rarity => rarity != RelicRarity.None)
                .Select(rarity => new LoadoutDropdownOption(rarity.ToString(), GetRarityLabel(rarity))),
            _workingState.Rarity ?? _item.Model.Rarity.ToString(),
            selected =>
            {
                _workingState.Rarity = selected;
                MarkDirty(rebuildControls: true);
            });

        _leftControls.AddChild(CreateSectionLabel(LocMan.Loc("RELICMODIFIER_DYNAMIC_VARS", "Canonical / Dynamic Vars")));
        foreach ((string name, var dynamicVar) in _item.Model.DynamicVars.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            int current = _workingState.DynamicVars.TryGetValue(name, out decimal saved)
                ? Decimal.ToInt32(saved)
                : Decimal.ToInt32(dynamicVar.BaseValue);
            AddStepperRow(_leftControls, name, current, int.MinValue, int.MaxValue, value =>
            {
                _workingState.DynamicVars[name] = value;
                MarkDirty();
            });
        }

        AddRelicStateControls();
        AddSavedPropertyControls();
        AddRelicEditActions();
        AddActionControls();
    }

    private void AddRelicStateControls()
    {
        if (_rightControls is null || _item is null)
            return;

        RelicModel model = _item.Model;
        _rightControls.AddChild(CreateSectionLabel(LocMan.Loc("RELICMODIFIER_STATE", "Relic State")));
        _rightControls.AddChild(CreateToggle(
            "is_wax",
            LocMan.Loc("RELICMODIFIER_IS_WAX", "Is Wax"),
            _workingState.IsWax ?? model.IsWax,
            value =>
            {
                _workingState.IsWax = value;
                MarkDirty();
            }));

        NLoadoutToggle meltedToggle = CreateToggle(
            "is_melted",
            LocMan.Loc("RELICMODIFIER_IS_MELTED", "Is Melted"),
            _workingState.IsMelted ?? model.IsMelted,
            value =>
            {
                _workingState.IsMelted = value;
                MarkDirty();
            });
        bool canMelt = _workingState.NeverMelt != true;
        meltedToggle.MouseFilter = canMelt ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
        meltedToggle.Modulate = canMelt ? Colors.White : new Color(0.5f, 0.5f, 0.5f, 0.8f);
        _rightControls.AddChild(meltedToggle);

        AddDropdownRow(
            _rightControls,
            LocMan.Loc("RELICMODIFIER_STATUS", "Relic Status"),
            Enum.GetValues<RelicStatus>().Select(status => new LoadoutDropdownOption(status.ToString(), status.ToString())),
            _workingState.Status ?? model.Status.ToString(),
            selected =>
            {
                _workingState.Status = _workingState.NeverUsed == true && selected == RelicStatus.Disabled.ToString()
                    ? RelicStatus.Normal.ToString()
                    : selected;
                MarkDirty(rebuildControls: true);
            });

        _rightControls.AddChild(CreateToggle(
            "never_melt",
            LocMan.Loc("RELICMODIFIER_NEVER_MELT", "Never Melt"),
            _workingState.NeverMelt == true,
            value =>
            {
                _workingState.NeverMelt = value;
                if (value)
                    _workingState.IsMelted = false;
                MarkDirty(rebuildControls: true);
            }));

        _rightControls.AddChild(CreateToggle(
            "never_used",
            LocMan.Loc("RELICMODIFIER_NEVER_USED", "Never Used"),
            _workingState.NeverUsed == true,
            value =>
            {
                _workingState.NeverUsed = value;
                if (value && _workingState.Status == RelicStatus.Disabled.ToString())
                    _workingState.Status = RelicStatus.Normal.ToString();
                MarkDirty(rebuildControls: true);
            }));

        AddCounterControls(model);
    }

    private void AddCounterControls(RelicModel model)
    {
        if (_rightControls is null
            || !TildeKeyStateService.TryGetRelicCounterMember(model, out string counterMember)
            || !TildeKeyStateService.TryGetRelicCounterValue(model, counterMember, out int counter))
        {
            return;
        }

        _rightControls.AddChild(CreateSectionLabel(LocMan.Loc("RELICMODIFIER_COUNTER", "Counter")));
        AddStepperRow(_rightControls, counterMember, _workingState.CounterValue ?? counter, int.MinValue, int.MaxValue, value =>
        {
            TildeKeyStateService.RequestRelicCounterAbsolute(model, counterMember, value);
            _workingState.CounterMember = counterMember;
            _workingState.CounterValue = value;
            RelicSavedPropertyDescriptor? descriptor = RelicModificationStateService.GetSavedPropertyDescriptors(model)
                .FirstOrDefault(candidate => counterMember.EndsWith($":{candidate.Name}", StringComparison.Ordinal));
            if (descriptor is not null)
                _workingState.PrimitiveValues[descriptor.Key] = RelicPrimitiveValue.FromObject(value, descriptor.ValueType);
            MarkDirty();
        });

        _rightControls.AddChild(CreateToggle(
            "counter_lock",
            LocMan.Loc("RELICMODIFIER_COUNTER_LOCK", "Lock Counter"),
            TildeKeyStateService.IsRelicCounterLocked(model, counterMember),
            locked =>
            {
                TildeKeyStateService.TryGetRelicCounterValue(model, counterMember, out int current);
                TildeKeyStateService.RequestRelicCounterLocked(model, counterMember, current, locked);
            }));
    }

    private void AddSavedPropertyControls()
    {
        if (_rightControls is null || _item is null)
            return;

        IReadOnlyList<RelicSavedPropertyDescriptor> descriptors = RelicModificationStateService.GetSavedPropertyDescriptors(_item.Model);
        if (descriptors.Count == 0)
            return;

        _rightControls.AddChild(CreateSectionLabel(LocMan.Loc("RELICMODIFIER_SAVED_PROPERTIES", "Saved Properties")));
        foreach (RelicSavedPropertyDescriptor descriptor in descriptors)
            AddPrimitiveControl(_rightControls, descriptor);
    }

    private void AddPrimitiveControl(VBoxContainer container, RelicSavedPropertyDescriptor descriptor)
    {
        if (!_workingState.PrimitiveValues.TryGetValue(descriptor.Key, out RelicPrimitiveValue? primitive))
            return;

        Type type = Nullable.GetUnderlyingType(descriptor.ValueType) ?? descriptor.ValueType;
        if (type == typeof(bool))
        {
            bool value = primitive.Value == "1" || bool.TryParse(primitive.Value, out bool parsed) && parsed;
            container.AddChild(CreateToggle(descriptor.Key, descriptor.Name, value, next => SetPrimitive(descriptor, next)));
            return;
        }

        if (type.IsEnum)
        {
            AddDropdownRow(
                container,
                descriptor.Name,
                Enum.GetNames(type).Select(name => new LoadoutDropdownOption(name, name)),
                primitive.Value,
                selected => SetPrimitive(descriptor, selected));
            return;
        }

        if (type == typeof(string) || type == typeof(decimal) || type == typeof(float) || type == typeof(double))
        {
            NLoadoutActionButton editButton = CreateActionButton(
                $"edit_{descriptor.Key}",
                $"{descriptor.Name}: {primitive.Value}");
            ConnectActionButton(editButton, () => OpenTextEditor(
                descriptor.Name,
                primitive.Value,
                multiline: type == typeof(string),
                value =>
                {
                    if (type != typeof(string)
                        && !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
                    {
                        return;
                    }

                    SetPrimitive(descriptor, value);
                    RebuildControls();
                }));
            container.AddChild(editButton);
            return;
        }

        if (!long.TryParse(primitive.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer))
            integer = 0;
        AddStepperRow(
            container,
            descriptor.Name,
            (int)Math.Clamp(integer, int.MinValue, int.MaxValue),
            int.MinValue,
            int.MaxValue,
            value => SetPrimitive(descriptor, value));
    }

    private void AddRelicEditActions()
    {
        if (_relicEditActions is null || _item is null)
            return;

        AddRelicEditButton(
            "modify_name",
            LocMan.Loc("RELICMODIFIER_MODIFY_NAME", "Modify Name"),
            _workingState.CustomTitle ?? GetTitleSafely(_item.Model),
            multiline: false,
            value => _workingState.CustomTitle = EmptyToNull(value));
        AddRelicEditButton(
            "modify_description",
            LocMan.Loc("RELICMODIFIER_MODIFY_DESCRIPTION", "Modify Description"),
            _workingState.CustomDescription ?? GetDescriptionSafely(_item.Model),
            multiline: true,
            value => _workingState.CustomDescription = EmptyToNull(value));
        AddRelicEditButton(
            "modify_flavor",
            LocMan.Loc("RELICMODIFIER_MODIFY_FLAVOR", "Modify Flavor"),
            _workingState.CustomFlavor ?? GetFlavorSafely(_item.Model),
            multiline: true,
            value => _workingState.CustomFlavor = EmptyToNull(value));
    }

    private void AddRelicEditButton(string id, string label, string current, bool multiline, Action<string> assign)
    {
        if (_relicEditActions is null)
            return;

        NLoadoutActionButton button = CreateActionButton(id, label);
        button.CustomMinimumSize = new Vector2(RelicEditButtonWidth, ActionButtonHeight);
        ConnectActionButton(button, () => OpenTextEditor(label, current, multiline, value =>
        {
            assign(value);
            MarkDirty(rebuildControls: true);
        }));
        _relicEditActions.AddChild(button);
    }

    private void AddActionControls()
    {
        if (_actionControls is null)
            return;

        NLoadoutActionButton permanentButton = CreateActionButton(
            "save_permanent",
            LocMan.Loc("SAVE_PERMANENT", "Save Permanent"),
            CommonHelpers.LoadActionButtonIcon("CardPrinter.png"));
        ConnectActionButton(permanentButton, SavePermanent);
        _actionControls.AddChild(permanentButton);
        ConfigureActionButtonSize(permanentButton);

        NLoadoutActionButton resetTemporaryButton = CreateActionButton(
            "reset_temporary",
            LocMan.Loc("RESET_TEMPORARY", "Reset Temporary"));
        ConnectActionButton(resetTemporaryButton, ResetTemporary);
        _actionControls.AddChild(resetTemporaryButton);
        ConfigureActionButtonSize(resetTemporaryButton);

        NLoadoutActionButton resetPermanentButton = CreateActionButton(
            "reset_permanent",
            LocMan.Loc("RESET_PERMANENT", "Reset Permanent"));
        ConnectActionButton(resetPermanentButton, ResetPermanent);
        _actionControls.AddChild(resetPermanentButton);
        ConfigureActionButtonSize(resetPermanentButton);

        NLoadoutActionButton addCopiesButton = CreateActionButton(
            "add_copies",
            LocMan.Loc("ADD_COPIES", "Add Copies"),
            CommonHelpers.LoadActionButtonIcon("CardPrinter.png"));
        ConnectActionButton(addCopiesButton, AddCopies);
        _actionControls.AddChild(addCopiesButton);
        ConfigureActionButtonSize(addCopiesButton);
    }

    private void SetPrimitive(RelicSavedPropertyDescriptor descriptor, object value)
    {
        _workingState.PrimitiveValues[descriptor.Key] = RelicPrimitiveValue.FromObject(value, descriptor.ValueType);
        MarkDirty();
    }

    private void MarkDirty(bool rebuildControls = false)
    {
        _hasPendingTemporaryCommit = true;
        if (rebuildControls)
            Callable.From(RebuildControls).CallDeferred();

        if (_previewRefreshQueued)
            return;

        _previewRefreshQueued = true;
        Callable.From(RefreshPreview).CallDeferred();
    }

    private void RefreshPreview()
    {
        _previewRefreshQueued = false;
        if (_item is null || _isClosing || !IsInsideTree())
            return;

        EnsureInspectionPresentation();
        LayoutPreviewNavigation();
        RelicModel preview = RelicModificationStateService.CreatePreviewRelic(_item.Model, _workingState);

        if (_nameLabel is not null)
            _nameLabel.SetTextAutoSize(GetTitleSafely(preview));
        if (_rarityLabel is not null)
        {
            _rarityLabel.SetTextAutoSize(GetRarityLabel(preview.Rarity));
            SetRarityVisuals(preview.Rarity);
        }
        if (_descriptionLabel is not null)
            _descriptionLabel.SetTextAutoSize(GetDescriptionSafely(preview));
        if (_flavorLabel is not null)
            _flavorLabel.SetTextAutoSize($"[center]{GetFlavorSafely(preview)}[/center]");
        if (_relicImage is not null)
            _relicImage.Texture = preview.BigIcon;

        RefreshHoverTips(preview);
    }

    private void RefreshHoverTips(RelicModel preview)
    {
        ClearHoverTips();
        if (_nativeHoverTipAnchor is null || !Visible)
            return;

        try
        {
            IReadOnlyList<IHoverTip> tips = IHoverTip.RemoveDupes(preview.HoverTipsExcludingRelic)
                .Where(tip => tip is not null)
                .ToList();
            if (tips.Count == 0)
                return;

            _nativeHoverTipAnchor.SetAnchorsPreset(LayoutPreset.TopLeft);
            _nativeHoverTipAnchor.Position = new Vector2(GetViewportRect().Size.X * 0.5f + 360f, 150f);
            NHoverTipSet.CreateAndShow(_nativeHoverTipAnchor, tips, HoverTipAlignment.Right)?.SetFollowOwner();
            NLoadoutPanelRoot.Instance?.AdoptGameHoverTips();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"RelicModification: failed to show hover tips for '{preview.Id}'. {exception.Message}");
        }
    }

    private void ClearHoverTips()
    {
        if (_nativeHoverTipAnchor is not null && GodotObject.IsInstanceValid(_nativeHoverTipAnchor))
            NHoverTipSet.Remove(_nativeHoverTipAnchor);
    }

    private void SetRarityVisuals(RelicRarity rarity)
    {
        Vector3 hsv = rarity switch
        {
            RelicRarity.None or RelicRarity.Starter or RelicRarity.Common => new Vector3(0.95f, 0.25f, 0.9f),
            RelicRarity.Uncommon => new Vector3(0.426f, 0.8f, 1.1f),
            RelicRarity.Rare => new Vector3(1f, 0.8f, 1.15f),
            RelicRarity.Shop => new Vector3(0.525f, 2.5f, 0.85f),
            RelicRarity.Event => new Vector3(0.23f, 0.75f, 0.9f),
            RelicRarity.Ancient => new Vector3(0.875f, 3f, 0.9f),
            _ => Vector3.One
        };

        if (_rarityLabel is not null)
        {
            _rarityLabel.Modulate = rarity switch
            {
                RelicRarity.Uncommon or RelicRarity.Shop => StsColors.blue,
                RelicRarity.Rare => StsColors.gold,
                RelicRarity.Event => StsColors.green,
                RelicRarity.Ancient => StsColors.red,
                _ => StsColors.cream
            };
        }

        _frameMaterial?.SetShaderParameter("h", hsv.X);
        _frameMaterial?.SetShaderParameter("s", hsv.Y);
        _frameMaterial?.SetShaderParameter("v", hsv.Z);
    }

    private void Navigate(int direction)
    {
        if (_items.Count == 0)
            return;

        int nextIndex = Mathf.Clamp(_itemIndex + direction, 0, _items.Count - 1);
        if (nextIndex == _itemIndex || !CommitPendingTemporaryModification())
            return;

        _itemIndex = nextIndex;
        LoadItem(_items[_itemIndex]);
        RebuildControls();
        RefreshPreview();
    }

    private bool CommitPendingTemporaryModification()
    {
        if (!_hasPendingTemporaryCommit || _item is null)
            return true;

        RelicModificationState state = _workingState.Clone();
        state.Normalize();
        if (!RelicModificationMultiplayerSyncService.RequestOperation(
                RelicModificationOperation.SaveTemporary,
                _item,
                state))
        {
            return false;
        }

        _hasPendingTemporaryCommit = false;
        RefreshParentView();
        return true;
    }

    private void SavePermanent()
    {
        if (_item is null)
            return;

        RelicModificationState state = _workingState.Clone();
        state.Normalize();
        if (!RelicModificationMultiplayerSyncService.RequestOperation(
                RelicModificationOperation.ApplyPermanent,
                _item,
                state))
        {
            return;
        }

        _hasPendingTemporaryCommit = false;
        RefreshParentView();
    }

    private void ResetTemporary()
    {
        if (_item is null)
            return;

        _hasPendingTemporaryCommit = false;
        _reloadAfterNextUpdate = true;
        if (!RelicModificationMultiplayerSyncService.RequestOperation(
                RelicModificationOperation.ResetTemporaryToBasic,
                _item))
        {
            _reloadAfterNextUpdate = false;
        }
    }

    private void ResetPermanent()
    {
        if (_item is null)
            return;

        _hasPendingTemporaryCommit = false;
        _reloadAfterNextUpdate = true;
        if (!RelicModificationMultiplayerSyncService.RequestOperation(
                RelicModificationOperation.ResetPermanentToBasic,
                _item))
        {
            _reloadAfterNextUpdate = false;
        }
    }

    private void AddCopies()
    {
        if (_item is null || !CommitPendingTemporaryModification())
            return;

        RelicModificationMultiplayerSyncService.RequestAddCopies(
            _item,
            Math.Max(1, NGenericSelectScreen.GetCurrentInputMultiplier()));
    }

    private void Close()
    {
        if (!CommitPendingTemporaryModification())
            return;

        BeginClose();
        NLoadoutPanelRoot.CloseTopLoadoutScreen();
    }

    private void BeginClose()
    {
        if (_isClosing)
            return;

        _isClosing = true;
        UnbindRunContentEvents();
        CommitPendingTemporaryModification();
    }

    private void OnRunContentChanged(LoadoutRunContentChangedEventArgs change)
    {
        if (_item is null
            || change.Kind != LoadoutRunContentKind.Relics
            || !change.AffectsPlayer(_item.OwnerNetId)
            || _isClosing)
        {
            return;
        }

        if (change.Mode == LoadoutRunContentChangeMode.Update
            && change.ChangedRelics.Any(changed =>
                changed.OwnerNetId == _item.OwnerNetId
                && changed.Index == _item.Index
                && changed.ModelId.Equals(_item.Model.Id)))
        {
            RefreshParentView();
            if (_reloadAfterNextUpdate)
            {
                _reloadAfterNextUpdate = false;
                LoadItem(_item);
                RebuildControls();
                RefreshPreview();
            }
        }

        if (change.Mode is LoadoutRunContentChangeMode.Remove or LoadoutRunContentChangeMode.Replace)
            Callable.From(RecoverAfterStructuralChange).CallDeferred();
        else if (change.Mode == LoadoutRunContentChangeMode.Add)
            _items = Loadout.PanelItems.RelicModifier.GetSelectedTargetRelics().ToList();
    }

    private void RecoverAfterStructuralChange()
    {
        if (_item is null || _isClosing)
            return;

        List<LoadoutOwnedItem<RelicModel>> live = Loadout.PanelItems.RelicModifier.GetSelectedTargetRelics().ToList();
        int exact = live.FindIndex(candidate => ReferenceEquals(candidate.Model, _item.Model));
        if (exact >= 0)
        {
            _items = live;
            _itemIndex = exact;
            _item = live[exact];
            return;
        }

        _hasPendingTemporaryCommit = false;
        if (live.Count == 0)
        {
            BeginClose();
            NLoadoutPanelRoot.CloseTopLoadoutScreen();
            return;
        }

        _items = live;
        _itemIndex = Math.Min(_itemIndex, live.Count - 1);
        LoadItem(_items[_itemIndex]);
        RebuildControls();
        RefreshPreview();
    }

    private void RefreshParentView()
    {
        if (_parentRefresh is null || _item is null)
            return;

        try
        {
            _parentRefresh.Invoke(_item);
        }
        catch (ObjectDisposedException)
        {
            // The originating grid slot may be recycled while the editor is open.
        }
    }

    private void OpenTextEditor(string title, string currentText, bool multiline, Action<string> onSave)
    {
        CloseTextEditor();

        Control overlay = new() { Name = "TextEditorOverlay", MouseFilter = MouseFilterEnum.Stop, ZIndex = 420 };
        ApplyFullRectLayout(overlay);

        ColorRect dimmer = new() { Color = new Color(0f, 0f, 0f, 0.62f), MouseFilter = MouseFilterEnum.Stop };
        ApplyFullRectLayout(dimmer);
        overlay.AddChild(dimmer);

        Control panel = new()
        {
            CustomMinimumSize = multiline ? new Vector2(820f, 468f) : new Vector2(720f, 228f),
            MouseFilter = MouseFilterEnum.Stop
        };
        panel.SetAnchorsPreset(LayoutPreset.Center);
        Vector2 size = panel.CustomMinimumSize;
        panel.OffsetLeft = -size.X * 0.5f;
        panel.OffsetTop = -size.Y * 0.5f;
        panel.OffsetRight = size.X * 0.5f;
        panel.OffsetBottom = size.Y * 0.5f;
        overlay.AddChild(panel);

        ColorRect panelBackground = new() { Color = new Color(0.063f, 0.125f, 0.151f, 0.98f), MouseFilter = MouseFilterEnum.Ignore };
        ApplyFullRectLayout(panelBackground);
        panel.AddChild(panelBackground);

        MarginContainer margin = new();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 24);
        margin.AddThemeConstantOverride("margin_top", 20);
        margin.AddThemeConstantOverride("margin_right", 24);
        margin.AddThemeConstantOverride("margin_bottom", 20);
        panel.AddChild(margin);

        VBoxContainer content = new() { MouseFilter = MouseFilterEnum.Ignore };
        content.AddThemeConstantOverride("separation", 12);
        margin.AddChild(content);
        content.AddChild(CreateSectionLabel(title));

        Control input = multiline
            ? new TextEdit
            {
                Text = currentText,
                CustomMinimumSize = new Vector2(0f, 280f),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill,
                MouseFilter = MouseFilterEnum.Stop
            }
            : new LineEdit
            {
                Text = currentText,
                CustomMinimumSize = new Vector2(0f, 52f),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                MouseFilter = MouseFilterEnum.Stop
            };
        content.AddChild(input);

        HBoxContainer buttons = new()
        {
            CustomMinimumSize = new Vector2(0f, 48f),
            SizeFlagsHorizontal = SizeFlags.ShrinkEnd,
            MouseFilter = MouseFilterEnum.Ignore
        };
        buttons.AddThemeConstantOverride("separation", 10);

        NLoadoutActionButton cancelButton = CreateActionButton("text_cancel", LocMan.Loc("CANCEL", "Cancel"));
        cancelButton.CustomMinimumSize = new Vector2(160f, ActionButtonHeight);
        ConnectActionButton(cancelButton, CloseTextEditor);
        buttons.AddChild(cancelButton);

        NLoadoutActionButton saveButton = CreateActionButton("text_save", LocMan.Loc("SAVE", "Save"));
        saveButton.CustomMinimumSize = new Vector2(160f, ActionButtonHeight);
        ConnectActionButton(saveButton, () =>
        {
            string value = input switch
            {
                LineEdit lineEdit => lineEdit.Text ?? string.Empty,
                TextEdit textEdit => textEdit.Text ?? string.Empty,
                _ => string.Empty
            };
            onSave(value);
            CloseTextEditor();
        });
        buttons.AddChild(saveButton);
        content.AddChild(buttons);

        AddChild(overlay);
        _textEditorOverlay = overlay;
        input.GrabFocus();
    }

    private void CloseTextEditor()
    {
        if (_textEditorOverlay is null || !GodotObject.IsInstanceValid(_textEditorOverlay))
        {
            _textEditorOverlay = null;
            return;
        }

        _textEditorOverlay.GetParent()?.RemoveChild(_textEditorOverlay);
        _textEditorOverlay.QueueFree();
        _textEditorOverlay = null;
    }

    private void LayoutPreviewNavigation()
    {
        if (_leftArrow is null || _rightArrow is null)
            return;

        bool hasPrevious = _itemIndex > 0;
        bool hasNext = _itemIndex < _items.Count - 1;
        SetArrowState(_leftArrowMount, _leftArrow, hasPrevious);
        SetArrowState(_rightArrowMount, _rightArrow, hasNext);
    }

    private static void SetArrowState(Control? mount, NButton button, bool enabled)
    {
        if (mount is not null)
        {
            mount.Visible = enabled;
            mount.MouseFilter = enabled ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
        }

        button.Visible = enabled;
        button.MouseFilter = enabled ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
        button.SetEnabled(enabled);
    }

    private void RefreshNativeButtonState()
    {
        if (_backButton is not null && GodotObject.IsInstanceValid(_backButton))
            _backButton.SetEnabled(Visible && IsInsideTree());
        LayoutPreviewNavigation();
    }

    private static void AddStepperRow(VBoxContainer container, string label, int value, int min, int max, Action<int> onChanged)
    {
        NLoadoutNumberStepper stepper = new();
        stepper.Init(value, min, max);
        stepper.ValueChanged += onChanged;
        container.AddChild(CreateRow(label, stepper));
    }

    private static void AddDropdownRow(
        VBoxContainer container,
        string label,
        IEnumerable<LoadoutDropdownOption> options,
        string selectedId,
        Action<string> onChanged)
    {
        NSelectFilterDropdown dropdown = new()
        {
            CustomMinimumSize = new Vector2(0f, 52f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            DropdownWidth = 420f
        };
        dropdown.SetItems(label, options, selectedId);
        dropdown.SelectedItemChanged += onChanged;
        container.AddChild(dropdown);
    }

    private static NLoadoutToggle CreateToggle(string id, string label, bool value, Action<bool> changed)
    {
        NLoadoutToggle toggle = new()
        {
            CustomMinimumSize = new Vector2(426f, 44f),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin
        };
        toggle.Init(CommonHelpers.MakeSafeNodeName(id), label, value);
        toggle.Toggled += state => changed(state.IsChecked);
        return toggle;
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

    private static NLoadoutActionButton CreateActionButton(string id, string label, Texture2D? icon = null)
    {
        NLoadoutActionButton button = new()
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0f, ActionButtonHeight)
        };
        button.Init(CommonHelpers.MakeSafeNodeName(id), label, icon);
        return button;
    }

    private static void ConnectActionButton(NLoadoutActionButton button, Action action)
    {
        button.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => action()));
    }

    private static void ConfigureActionButtonSize(Control button)
    {
        button.CustomMinimumSize = new Vector2(ActionButtonWidth, ActionButtonHeight);
        button.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
    }

    private static MegaLabel CreateLabel(string text, int fontSize, Color color)
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
        label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.45f));
        label.AddThemeConstantOverride("shadow_offset_x", 3);
        label.AddThemeConstantOverride("shadow_offset_y", 2);
        return label;
    }

    private static MegaRichTextLabel CreateRichLabel(int fontSize, Font? font, Color color)
    {
        MegaRichTextLabel label = new()
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            AutoSizeEnabled = false,
            MinFontSize = Math.Max(12, fontSize - 10),
            MaxFontSize = fontSize,
            MouseFilter = MouseFilterEnum.Ignore
        };
        if (font is not null)
            label.AddThemeFontOverride("normal_font", font);
        label.AddThemeFontSizeOverride("normal_font_size", fontSize);
        label.AddThemeColorOverride("default_color", color);
        label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.25f));
        label.AddThemeConstantOverride("shadow_offset_x", 3);
        label.AddThemeConstantOverride("shadow_offset_y", 2);
        return label;
    }

    private static MegaLabel CreateSectionLabel(string text)
    {
        MegaLabel label = CreateLabel(text, 25, StsColors.gold);
        label.CustomMinimumSize = new Vector2(0f, 42f);
        return label;
    }

    private static Control CreateSpacer(float height) => new()
    {
        CustomMinimumSize = new Vector2(0f, height),
        MouseFilter = MouseFilterEnum.Ignore
    };

    private static ShaderMaterial? CreateHsvMaterial(float h, float s, float v)
    {
        const string shaderPath = "res://shaders/hsv.gdshader";
        if (!ResourceLoader.Exists(shaderPath))
            return null;

        ShaderMaterial material = new()
        {
            ResourceLocalToScene = true,
            Shader = GD.Load<Shader>(shaderPath)
        };
        material.SetShaderParameter("h", h);
        material.SetShaderParameter("s", s);
        material.SetShaderParameter("v", v);
        return material;
    }

    private static Texture2D? LoadTexture(string path)
    {
        string localPath = path
            .Replace("res://images/atlases/", "res://Loadout/images/atlases/")
            .Replace("res://images/packed/common_ui/", "res://Loadout/images/atlases/ui_atlas.sprites/");
        if (ResourceLoader.Exists(localPath))
            return GD.Load<Texture2D>(localPath);
        return ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
    }

    private static Font? LoadFont(string path)
    {
        string localPath = path.Replace("res://themes/", "res://Loadout/themes/default/");
        if (ResourceLoader.Exists(localPath))
            return GD.Load<Font>(localPath);
        return ResourceLoader.Exists(path) ? GD.Load<Font>(path) : null;
    }

    private static string GetRarityLabel(RelicRarity rarity)
        => LocMan.GameLoc("gameplay_ui", $"RELIC_RARITY.{rarity.ToString().ToUpperInvariant()}", rarity.ToString());

    private static string GetTitleSafely(RelicModel relic)
    {
        try { return relic.Title.GetFormattedText(); }
        catch { return relic.Id.Entry; }
    }

    private static string GetDescriptionSafely(RelicModel relic)
    {
        try { return relic.DynamicDescription.GetFormattedText(); }
        catch { return relic.Id.Entry; }
    }

    private static string GetFlavorSafely(RelicModel relic)
    {
        try { return relic.Flavor.GetFormattedText(); }
        catch { return string.Empty; }
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ApplyFullRectLayout(Control control)
    {
        control.SetAnchorsPreset(LayoutPreset.FullRect);
        control.AnchorLeft = 0f;
        control.AnchorTop = 0f;
        control.AnchorRight = 1f;
        control.AnchorBottom = 1f;
        control.OffsetLeft = 0f;
        control.OffsetTop = 0f;
        control.OffsetRight = 0f;
        control.OffsetBottom = 0f;
    }

    private static void ClearChildren(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            node.RemoveChild(child);
            child.QueueFree();
        }
    }

    private static bool SameOwnedItem(LoadoutOwnedItem<RelicModel> left, LoadoutOwnedItem<RelicModel> right)
        => left.OwnerNetId == right.OwnerNetId
           && left.Index == right.Index
           && left.Model.Id.Equals(right.Model.Id);
}
