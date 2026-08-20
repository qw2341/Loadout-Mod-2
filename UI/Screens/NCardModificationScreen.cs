#nullable enable

namespace Loadout.UI.Screens;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Loadout.PanelItems;
using Loadout.Services.Actions;
using Loadout.Services.CardModification;
using Loadout.Services.CardPortraits;
using Loadout.Patches.Cards.CardModification;
using Loadout.Keywords;
using Loadout.Services.Targets;
using Loadout.Services.Compatibility;
using Loadout.Services.Saving;
using Loadout.UI.ImageEditing;
using Loadout.UI.Managers;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Runs;

public partial class NCardModificationScreen : Control
{
    private enum TextEditTarget
    {
        Name,
        Description
    }

    private const string ScenePath = "res://UI/Screens/CardModificationScreen.tscn";
    private const string NoneOptionId = "__none__";
    private const float SidePanelWidth = 438f;
    private const float ActionButtonWidth = 318f;
    private const float CardEditButtonWidth = 246f;
    private const float CardEditThreeButtonWidth = 172f;
    private const float ActionButtonHeight = 42f;
    private const float KeywordPanelTopMargin = 24f;
    private const float HoverTipCardGap = 22f;
    private const float HoverTipViewportMargin = 24f;
    private const float HoverTipWidth = 360f;
    private const float HoverTipMinHeight = 220f;
    private const float HoverTipMaxHeight = 460f;
    private const float EnchantmentContentWidth = 426f;
    private const float EnchantmentEntryHeight = 100f;
    private const int VisibleEnchantmentEntries = 2;

    private LoadoutOwnedItem<CardModel>? _item;
    private List<LoadoutOwnedItem<CardModel>> _items = [];
    private int _itemIndex;
    private Action? _parentScrollRestore;
    private CardModificationSpec _workingState = new();
    private CardModificationSpec _temporaryState = new();
    private CardModificationSpec _lastAppliedState = new();
    private VBoxContainer? _leftControls;
    private VBoxContainer? _rightControls;
    private VBoxContainer? _attachmentControls;
    private VBoxContainer? _actionControls;
    private HBoxContainer? _cardEditActions;
    private Control? _nativeHoverTipAnchor;
    private NScrollableContainer? _nativeHoverTipScroll;
    private NHoverTipCardContainer? _nativeCardHoverTips;
    private Control? _backButtonMount;
    private Control? _previewHost;
    private Control? _leftArrowMount;
    private Control? _rightArrowMount;
    private NButton? _leftArrow;
    private NButton? _rightArrow;
    private NBackButton? _backButton;
    private NCard? _previewCard;
    private CardModel? _previewDisplayModel;
    private MegaLabel? _titleLabel;
    private Control? _textEditorOverlay;
    private bool _signalsBound;
    private bool _runContentEventsBound;
    private CardPile? _observedPile;
    private bool _hasPendingTemporaryCommit;
    private bool _suppressStateRefreshThisFrame;
    private bool _isClosing;
    private bool _hasBeenVisible;
    private bool _awaitingResetConfirmation;
    private bool _customRunAuthoringMode;
    private Func<LoadoutOwnedItem<CardModel>, CardModificationSpec>? _customRunStateProvider;
    private Action<LoadoutOwnedItem<CardModel>, CardModificationSpec>? _customRunStateSaved;
    private Action<LoadoutOwnedItem<CardModel>, int>? _customRunAddCopies;
    private string _selectedKeywordModId = NCardKeywordEditor.AllModFilterId;

    public static NCardModificationScreen Create()
    {
        if (ResourceLoader.Exists(ScenePath)
            && GD.Load<PackedScene>(ScenePath) is { } scene
            && scene.Instantiate<NCardModificationScreen>() is { } screen)
        {
            return screen;
        }

        GD.PushWarning($"CardModification: could not load scene '{ScenePath}'. Falling back to script-only screen.");
        return new NCardModificationScreen();
    }

    public void Init(
        LoadoutOwnedItem<CardModel> item,
        IReadOnlyList<LoadoutOwnedItem<CardModel>>? items = null,
        Action? parentScrollRestore = null)
    {
        _items = items?.Count > 0 ? items.ToList() : [item];
        _itemIndex = Math.Max(0, _items.FindIndex(candidate => SameOwnedItem(candidate, item)));
        _parentScrollRestore = parentScrollRestore;
        _selectedKeywordModId = NCardKeywordEditor.AllModFilterId;
        LoadItem(_items[_itemIndex]);

        if (IsNodeReady())
            RebuildScreen();
    }

    public void InitForCustomRun(
        LoadoutOwnedItem<CardModel> item,
        IReadOnlyList<LoadoutOwnedItem<CardModel>> items,
        Func<LoadoutOwnedItem<CardModel>, CardModificationSpec> stateProvider,
        Action<LoadoutOwnedItem<CardModel>, CardModificationSpec> stateSaved,
        Action<LoadoutOwnedItem<CardModel>, int> addCopies)
    {
        _customRunAuthoringMode = true;
        _customRunStateProvider = stateProvider;
        _customRunStateSaved = stateSaved;
        _customRunAddCopies = addCopies;
        Init(item, items);
    }

    public override void _Ready()
    {
        ApplyFullRectLayout(this);
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 120;
        BindSceneNodes();
        RebuildScreen();
    }

    public override void _Input(InputEvent inputEvent)
    {
        if (!IsVisibleInTree()
            || _isClosing
            || _textEditorOverlay is not null
            || inputEvent is not InputEventKey { Pressed: true, Echo: false } keyEvent
            || keyEvent.CtrlPressed
            || keyEvent.AltPressed
            || keyEvent.MetaPressed)
        {
            return;
        }

        int direction = keyEvent.Keycode switch
        {
            Key.Left => -1,
            Key.Right => 1,
            _ => 0
        };
        if (direction == 0)
            return;

        SwitchCard(direction);
        GetViewport().SetInputAsHandled();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized && !_isClosing)
        {
            ApplyFullRectLayout(this);
            RefreshPreview(forceReload: false);
        }

        if (what == NotificationVisibilityChanged)
        {
            RefreshNativeButtonState();
            if (Visible && IsInsideTree() && _item is not null && !_isClosing)
            {
                if (!_customRunAuthoringMode)
                    BindRunContentEvents();
                _hasBeenVisible = true;
                Callable.From(() => RefreshPreview(forceReload: false)).CallDeferred();
            }
            else if (!Visible)
            {
                UnbindRunContentEvents();
                CloseTextEditor();
                ClearHoverTips();
                if (_hasBeenVisible && _parentScrollRestore is not null)
                    Callable.From(_parentScrollRestore).CallDeferred();
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
        CardModificationRuntime.OwnedCardChanged += OnOwnedCardChanged;
        _runContentEventsBound = true;
        BindObservedPile();
    }

    private void UnbindRunContentEvents()
    {
        if (!_runContentEventsBound)
            return;

        LoadoutRunContentChangeService.Changed -= OnRunContentChanged;
        CardModificationRuntime.OwnedCardChanged -= OnOwnedCardChanged;
        UnbindObservedPile();
        _runContentEventsBound = false;
    }

    private void OnRunContentChanged(LoadoutRunContentChangedEventArgs change)
    {
        if (_item?.CardPileType is not null and not PileType.Deck
            || change.Kind != LoadoutRunContentKind.Cards
            || _item is null
            || !change.AffectsPlayer(_item.OwnerNetId)
            || !IsInsideTree()
            || _isClosing)
        {
            return;
        }

        if (change.Mode == LoadoutRunContentChangeMode.Add)
        {
            Callable.From(RefreshItemsAfterAdd).CallDeferred();
            return;
        }

        if (change.Mode == LoadoutRunContentChangeMode.Update)
        {
            foreach (LoadoutChangedCard changed in change.ChangedCards)
            {
                if (!MatchesChangedCard(_item, changed))
                    continue;

                if (_awaitingResetConfirmation)
                {
                    _awaitingResetConfirmation = false;
                    LoadoutCardVisualRefreshKind confirmedRefreshKind = changed.RefreshKind;
                    Callable.From(() =>
                    {
                        if (!_isClosing)
                            RefreshTargetedCardUpdate(confirmedRefreshKind);
                    }).CallDeferred();
                    return;
                }

                CardModificationSpec effectiveState = CardModificationRuntime.GetEffectiveSpec(_item);
                if (CardModificationRuntime.SpecsEquivalent(effectiveState, _workingState))
                {
                    // This is the confirmation for the state already displayed by this
                    // editor. Refresh only the source card slot; rebuilding the entire
                    // editor and preview here caused the close-screen hitch.
                    _temporaryState = CardModificationRuntime.GetTemporarySpec(_item);
                    _lastAppliedState = effectiveState.Clone();
                    return;
                }

                if (_suppressStateRefreshThisFrame)
                    return;

                LoadoutCardVisualRefreshKind refreshKind = changed.RefreshKind;
                Callable.From(() =>
                {
                    if (!_isClosing)
                        RefreshTargetedCardUpdate(refreshKind);
                }).CallDeferred();
                return;
            }

            return;
        }

        // Add/remove/replace can change deck indices, so only structural changes
        // rebuild the owned-item list.
        Callable.From(RefreshAfterDeckMutation).CallDeferred();
    }

    private void OnOwnedCardChanged(LoadoutOwnedItem<CardModel> changedItem, LoadoutCardVisualRefreshKind refreshKind)
    {
        if (_item is null
            || _item.CardPileType is null or PileType.Deck
            || _isClosing
            || !SameOwnedItem(_item, changedItem))
            return;

        Callable.From(() =>
        {
            if (!_isClosing)
                RefreshTargetedCardUpdate(refreshKind);
        }).CallDeferred();
    }

    private void RefreshTargetedCardUpdate(LoadoutCardVisualRefreshKind refreshKind)
    {
        if (_isClosing || _item is null || !IsInsideTree())
            return;

        if (!TryResolveCurrentLocation(_item, out LoadoutOwnedItem<CardModel>? refreshed)
            || refreshed is null)
        {
            RefreshAfterDeckMutation();
            return;
        }

        _item = refreshed;
        if (_itemIndex >= 0 && _itemIndex < _items.Count)
            _items[_itemIndex] = refreshed;

        LoadItem(refreshed);
        bool forceReload = refreshKind == LoadoutCardVisualRefreshKind.Reload;
        RebuildControls();
        RefreshPreview(forceReload);
    }

    private void RefreshAfterDeckMutation()
    {
        if (_isClosing || _item is null || !IsInsideTree())
            return;

        List<Player> owners = _items
            .Select(item => item.Owner)
            .Where(player => player is not null)
            .Distinct()
            .ToList();
        if (owners.Count == 0)
            owners.Add(_item.Owner);

        List<LoadoutOwnedItem<CardModel>> refreshedItems = BuildCurrentLocationItems(owners);

        if (refreshedItems.Count == 0)
        {
            _hasPendingTemporaryCommit = false;
            NLoadoutPanelRoot.CloseTopLoadoutScreen();
            return;
        }

        int refreshedIndex = refreshedItems.FindIndex(candidate => ReferenceEquals(candidate.Model, _item.Model));
        if (refreshedIndex < 0)
        {
            _hasPendingTemporaryCommit = false;
            if (_item.CardPileType is not null and not PileType.Deck)
            {
                NLoadoutPanelRoot.CloseTopLoadoutScreen();
                return;
            }
            refreshedIndex = Mathf.Clamp(_itemIndex, 0, refreshedItems.Count - 1);
        }

        _items = refreshedItems;
        _itemIndex = refreshedIndex;
        LoadItem(_items[_itemIndex]);
        RebuildControls();
        RefreshPreview(forceReload: true);
    }

    private void RefreshItemsAfterAdd()
    {
        if (_isClosing || _item is null || !IsInsideTree())
            return;

        List<Player> owners = _items
            .Select(candidate => candidate.Owner)
            .Append(_item.Owner)
            .Distinct()
            .ToList();
        List<LoadoutOwnedItem<CardModel>> refreshed = BuildCurrentLocationItems(owners);
        int selectedIndex = refreshed.FindIndex(candidate => ReferenceEquals(candidate.Model, _item.Model));
        if (selectedIndex < 0)
            return;

        _items = refreshed;
        _itemIndex = selectedIndex;
        _item = refreshed[selectedIndex];
        LayoutPreviewNavigation();
    }

    private void RebuildScreen()
    {
        if (_item is null)
            return;

        BindSceneNodes();
        RefreshPreview();
        RebuildControls();
    }

    private void BindSceneNodes()
    {
        _backButtonMount = GetNodeOrNull<Control>("%BackButtonMount");
        _leftControls = GetNodeOrNull<VBoxContainer>("%LeftControls");
        _rightControls = GetNodeOrNull<VBoxContainer>("%RightControls");
        _actionControls = GetNodeOrNull<VBoxContainer>("%ActionRow");
        _cardEditActions = GetNodeOrNull<HBoxContainer>("%CardEditActions");
        _nativeHoverTipAnchor = GetNodeOrNull<Control>("%NativeHoverTipAnchor");
        _previewHost = GetNodeOrNull<Control>("%PreviewCardHost");
        _leftArrowMount = GetNodeOrNull<Control>("%LeftArrow");
        _rightArrowMount = GetNodeOrNull<Control>("%RightArrow");
        DisableHorizontalEditorScroll(_leftControls);
        if (GetNodeOrNull<Control>("RightEditor") is { } rightEditor)
            rightEditor.OffsetTop = KeywordPanelTopMargin;
        _leftArrow = EnsureInspectArrowButton(_leftArrowMount, isLeft: true);
        _rightArrow = EnsureInspectArrowButton(_rightArrowMount, isLeft: false);

        EnsureBackButton();
        BindSceneSignals();
    }

    private static void DisableHorizontalEditorScroll(Control? controls)
    {
        Node? ancestor = controls?.GetParent();
        while (ancestor is not null && ancestor is not ScrollContainer)
            ancestor = ancestor.GetParent();

        if (ancestor is ScrollContainer scroll)
            scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
    }

    private static NButton? EnsureInspectArrowButton(Control? mount, bool isLeft)
    {
        if (mount is null)
            return null;

        if (mount.GetNodeOrNull<NButton>("ArrowButton") is { } existing)
            return existing;

        ShaderMaterial? material = CreateArrowMaterial();
        NButton button = material is null ? new NButton() : new NGoldArrowButton();
        button.Name = "ArrowButton";
        button.FocusMode = FocusModeEnum.All;
        button.MouseFilter = MouseFilterEnum.Stop;
        button.PivotOffset = new Vector2(64f, 64f);
        button.SetAnchorsPreset(LayoutPreset.FullRect);

        TextureRect image = new()
        {
            Name = "TextureRect",
            Texture = LoadArrowTexture(isLeft),
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

    private static ShaderMaterial? CreateArrowMaterial()
    {
        const string shaderPath = "res://shaders/hsv.gdshader";
        if (!ResourceLoader.Exists(shaderPath))
            return null;

        ShaderMaterial material = new()
        {
            ResourceLocalToScene = true,
            Shader = GD.Load<Shader>(shaderPath)
        };
        material.SetShaderParameter("h", 1f);
        material.SetShaderParameter("s", 1f);
        material.SetShaderParameter("v", 0.9f);
        return material;
    }

    private static Texture2D? LoadArrowTexture(bool isLeft)
    {
        string path = isLeft
            ? "res://images/packed/common_ui/settings_tiny_left_arrow.png"
            : "res://images/packed/common_ui/settings_tiny_right_arrow.png";

        return ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
    }

    private void BindSceneSignals()
    {
        if (_signalsBound)
            return;

        if (_leftArrow is not null)
            _leftArrow.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => SwitchCard(-1)));

        if (_rightArrow is not null)
            _rightArrow.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => SwitchCard(1)));

        _signalsBound = true;
    }

    private void EnsureBackButton()
    {
        if (_backButtonMount is null)
            return;

        if (_backButtonMount.GetNodeOrNull<NBackButton>("BackButton") is { } existingBackButton)
        {
            _backButton = existingBackButton;
            RefreshNativeButtonState();
            return;
        }

        NBackButton backButton = NLoadoutBackButtonFactory.Create();
        backButton.Name = "BackButton";
        backButton.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ =>
        {
            NLoadoutBackButtonFactory.ResetVisualState(backButton);
            BeginClose();
            NLoadoutPanelRoot.CloseTopLoadoutScreen();
        }));
        _backButtonMount.AddChild(backButton);
        _backButton = backButton;
        Callable.From(RefreshNativeButtonState).CallDeferred();
    }

    private void BeginClose()
    {
        if (_isClosing)
            return;

        _isClosing = true;
        // Stop mutation confirmations and queued structural changes from rebuilding
        // a screen that is about to be freed.
        UnbindRunContentEvents();
        CommitPendingTemporaryModification();
    }

    private void RefreshNativeButtonState()
    {
        if (_backButton is not null && GodotObject.IsInstanceValid(_backButton))
            _backButton.SetEnabled(Visible && IsInsideTree());

        LayoutPreviewNavigation();
    }

    private void LoadItem(LoadoutOwnedItem<CardModel> item)
    {
        _item = item;
        _awaitingResetConfirmation = false;
        _workingState = _customRunAuthoringMode
            ? _customRunStateProvider?.Invoke(item).Clone() ?? new CardModificationSpec()
            : CardModificationRuntime.GetEffectiveSpec(item);
        _temporaryState = _customRunAuthoringMode
            ? _workingState.Clone()
            : CardModificationRuntime.GetTemporarySpec(item);
        // The live deck card already contains its effective attached/permanent
        // state. Do not allocate and fully rebuild another mutable card merely to
        // open or close the editor. A detached preview clone is created lazily only
        // after the user actually changes a control.
        _previewDisplayModel = _customRunAuthoringMode && !_workingState.IsEmpty
            ? CardModificationRuntime.CreatePreviewCard(item.Model, _workingState)
            : item.Model;
        _lastAppliedState = _workingState.Clone();
        _hasPendingTemporaryCommit = false;
        BindObservedPile();
    }

    private void SwitchCard(int direction)
    {
        if (_items.Count == 0)
            return;

        int nextIndex = Mathf.Clamp(_itemIndex + direction, 0, _items.Count - 1);
        if (nextIndex == _itemIndex)
            return;

        CommitPendingTemporaryModification();
        _itemIndex = nextIndex;
        LoadItem(_items[_itemIndex]);
        RefreshPreview();
        RebuildControls();
    }

    private void LayoutPreviewNavigation()
    {
        if (_leftArrow is null || _rightArrow is null)
            return;

        bool hasPrevious = _itemIndex > 0;
        bool hasNext = _itemIndex < _items.Count - 1;
        if (_leftArrowMount is not null)
        {
            _leftArrowMount.Visible = hasPrevious;
            _leftArrowMount.MouseFilter = hasPrevious ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
        }

        if (_rightArrowMount is not null)
        {
            _rightArrowMount.Visible = hasNext;
            _rightArrowMount.MouseFilter = hasNext ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
        }

        _leftArrow.Visible = hasPrevious;
        _rightArrow.Visible = hasNext;
        _leftArrow.MouseFilter = hasPrevious ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
        _rightArrow.MouseFilter = hasNext ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
        _leftArrow.SetEnabled(hasPrevious && Visible);
        _rightArrow.SetEnabled(hasNext && Visible);
    }

    private void RebuildControls()
    {
        if (_leftControls is null || _rightControls is null || _actionControls is null || _item is null)
            return;

        ClearChildren(_leftControls);
        ClearChildren(_rightControls);
        ClearChildren(_actionControls);
        if (_cardEditActions is not null)
            ClearChildren(_cardEditActions);

        RebuildLeftControls();
        AddCardEditActions();
        AddKeywordControls();
        AddAttachmentControls();
        AddModifyUpgradeAction();

        if (_customRunAuthoringMode || CanSavePermanent())
        {
            NLoadoutActionButton permanentButton = CreateActionButton(
                "save_permanent",
                _customRunAuthoringMode ? "SAVE PERMANENT FOR THIS RUN" : LocMan.Loc("SAVE_PERMANENT", "Save Permanent"),
                CommonHelpers.LoadActionButtonIcon("CardPrinter.png"));
            ConnectActionButton(permanentButton, SavePermanent);
            _actionControls.AddChild(permanentButton);
            ConfigureActionButtonSize(permanentButton);
        }

        NLoadoutActionButton resetTemporaryButton = CreateActionButton(
            "reset_temporary",
            _customRunAuthoringMode ? "RESET PERMANENT FOR THIS RUN" : LocMan.Loc("RESET_TEMPORARY", "Reset Temporary"));
        ConnectActionButton(resetTemporaryButton, ResetTemporary);
        _actionControls.AddChild(resetTemporaryButton);
        ConfigureActionButtonSize(resetTemporaryButton);

        if (!_customRunAuthoringMode && CanSavePermanent())
        {
            NLoadoutActionButton resetPermanentButton = CreateActionButton("reset_permanent", LocMan.Loc("RESET_PERMANENT", "Reset Permanent"));
            ConnectActionButton(resetPermanentButton, ResetPermanent);
            _actionControls.AddChild(resetPermanentButton);
            ConfigureActionButtonSize(resetPermanentButton);
        }

        NLoadoutActionButton addCopiesButton = CreateActionButton(
            "add_copies_to_deck",
            LocMan.Loc("CARD_MOD_ADD_COPIES_TO_DECK", "Add Copies To Deck"),
            CommonHelpers.LoadActionButtonIcon("CardPrinter.png"));
        ConnectActionButton(addCopiesButton, AddCopiesToDeck);
        _actionControls.AddChild(addCopiesButton);
        ConfigureActionButtonSize(addCopiesButton);
    }

    private void RebuildLeftControls()
    {
        if (_leftControls is null || _item is null)
            return;

        ClearChildren(_leftControls);
        _titleLabel = CreateLabel(CardPrinter.FormatCardTitle(_item.Model), 32, StsColors.gold);
        _leftControls.AddChild(_titleLabel);
        _leftControls.AddChild(CreateCardIdLabel(_item.Model.Id.ToString()));
        _leftControls.AddChild(CreateSpacer(6f));

        AddDropdownControls();
        AddNumericControls();
    }

    private void AddCardEditActions()
    {
        if (_cardEditActions is null)
            return;

        float buttonWidth = _customRunAuthoringMode ? CardEditButtonWidth : CardEditThreeButtonWidth;
        NLoadoutActionButton nameButton = CreateActionButton("modify_name", LocMan.Loc("CARD_MOD_MODIFY_NAME", "Modify Name"));
        nameButton.CustomMinimumSize = new Vector2(buttonWidth, ActionButtonHeight);
        ConnectActionButton(nameButton, () => OpenTextEditor(TextEditTarget.Name));
        _cardEditActions.AddChild(nameButton);

        NLoadoutActionButton descriptionButton = CreateActionButton("modify_description", LocMan.Loc("CARD_MOD_MODIFY_DESCRIPTION", "Modify Description"));
        descriptionButton.CustomMinimumSize = new Vector2(buttonWidth, ActionButtonHeight);
        ConnectActionButton(descriptionButton, () => OpenTextEditor(TextEditTarget.Description));
        _cardEditActions.AddChild(descriptionButton);

        if (!_customRunAuthoringMode)
        {
            NLoadoutActionButton portraitButton = CreateActionButton(
                "change_portrait",
                LocMan.Loc("CARD_MOD_CHANGE_PORTRAIT", "Change Portrait"));
            portraitButton.CustomMinimumSize = new Vector2(buttonWidth, ActionButtonHeight);
            ConnectActionButton(portraitButton, () => TaskHelper.RunSafely(ChangePortraitAsync()));
            _cardEditActions.AddChild(portraitButton);
        }
    }

    private async Task ChangePortraitAsync()
    {
        if (_item is null || _isClosing || ImageEditorService.IsBusy)
            return;

        CommitPendingTemporaryModification();
        LoadoutOwnedItem<CardModel> selected = _item;
        CardModel frameCard = _previewDisplayModel ?? selected.Model;
        ImageEditFrameDefinition frame = ImageEditFramePresets.ForCard(
            frameCard.Type,
            CardModificationRuntime.ShouldUseAncientRendering(frameCard));
        CardPortraitSaveTarget permanentTarget = CardPortraitStore.CreatePermanentSaveTarget(frameCard.Id);
        CardPortraitSaveTarget? temporaryTarget = CardPortraitStore.CreateTemporarySaveTarget(
            SaveUtility.GetCurrentRunStartTime());

        List<ImageEditSaveOption> saveOptions = [];
        if (temporaryTarget is { } temporaryOptionTarget)
        {
            saveOptions.Add(new ImageEditSaveOption(
                "temporary",
                LocMan.Loc("CARD_PORTRAIT_SAVE_TEMPORARY", "Save Temporary"),
                temporaryOptionTarget.Directory,
                temporaryOptionTarget.FileName));
        }
        saveOptions.Add(new ImageEditSaveOption(
            "permanent",
            LocMan.Loc("CARD_PORTRAIT_SAVE_PERMANENT", "Save Permanent"),
            permanentTarget.Directory,
            permanentTarget.FileName));

        ImageEditSaveOption defaultOption = saveOptions[0];
        ImageEditRequest request = new(
            frame,
            defaultOption.DestinationDirectory,
            defaultOption.OutputFileName,
            LocMan.Loc("CARD_PORTRAIT_EDITOR_TITLE", "Change Card Portrait"),
            AllowAlphaEditing: false,
            AllowRotation: true,
            SaveOptions: saveOptions,
            UseLoadoutScreen: true,
            CardPreviewModel: frameCard);
        ImageEditResult result = await ImageEditorService.PickAndEditAsync(request);
        if (result.Status == ImageEditStatus.Cancelled)
            return;
        if (!result.Saved
            || string.IsNullOrWhiteSpace(result.SavedPath)
            || result.OutputDocument is null)
        {
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                GD.PushWarning($"CardPortrait: image editing failed. {result.ErrorMessage}");
            return;
        }

        if (_isClosing
            || !IsInsideTree()
            || !TryResolveCurrentLocation(selected, out LoadoutOwnedItem<CardModel>? resolved)
            || resolved is null)
        {
            TryDeletePortraitOutput(result.SavedPath);
            return;
        }

        bool saved = result.SaveOptionId switch
        {
            "temporary" when temporaryTarget is { } selectedTemporaryTarget => CardPortraitRuntime.SaveTemporary(
                resolved.Model,
                selectedTemporaryTarget,
                frame,
                result.OutputDocument,
                result.SavedPath),
            "permanent" => CardPortraitRuntime.SavePermanent(
                resolved.Model,
                permanentTarget,
                frame,
                result.OutputDocument,
                result.SavedPath),
            _ => false
        };
        if (!saved)
        {
            TryDeletePortraitOutput(result.SavedPath);
            GD.PushWarning("CardPortrait: the edited portrait could not be attached because its card, run, profile, or customization scope changed.");
            return;
        }

        _item = resolved;
        if (_itemIndex >= 0 && _itemIndex < _items.Count)
            _items[_itemIndex] = resolved;
        LoadItem(resolved);
        RebuildControls();
        RefreshPreview(forceReload: true);
        NotifyPortraitChanged(resolved);
    }

    private static void TryDeletePortraitOutput(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardPortrait: failed to remove unused edited image '{path}'. {exception.Message}");
        }
    }

    private void AddNumericControls()
    {
        if (_item is null || _leftControls is null)
            return;

        CardModel card = _item.Model;

        AddStepperRow(_leftControls, LocMan.Loc("CARD_MOD_ENERGY_COST", "Energy Cost"),
            _workingState.EnergyCost ?? (card.EnergyCost.CostsX ? 0 : card.EnergyCost.GetWithModifiers(CostModifiers.Local)),
            int.MinValue, int.MaxValue, value =>
            {
                _workingState.EnergyCost = value;
                _temporaryState.EnergyCost = value;
                ApplyWorkingState();
            });

        AddStepperRow(_leftControls, LocMan.Loc("CARD_MOD_REPLAY_COUNT", "Replay Count"),
            _workingState.BaseReplayCount ?? card.BaseReplayCount,
            int.MinValue, int.MaxValue, value =>
            {
                _workingState.BaseReplayCount = value;
                _temporaryState.BaseReplayCount = value;
                ApplyWorkingState();
            });

        AddStepperRow(_leftControls, LocMan.Loc("CARD_MOD_STAR_COST", "Star Cost"),
            _workingState.BaseStarCost ?? card.BaseStarCost,
            int.MinValue, int.MaxValue, value =>
            {
                _workingState.BaseStarCost = value;
                _temporaryState.BaseStarCost = value;
                ApplyWorkingState();
            });

        CardModel dynamicVarCard = _previewDisplayModel ?? card;
        foreach ((string name, var dynamicVar) in dynamicVarCard.DynamicVars.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            int current = _workingState.DynamicVars.TryGetValue(name, out decimal saved)
                ? Decimal.ToInt32(saved)
                : Decimal.ToInt32(dynamicVar.BaseValue);

            string label = LocMan.DynamicVarLoc(dynamicVar);
            int minimum = int.MinValue;
            int maximum = int.MaxValue;
            if (LoadoutKeywordRegistry.TryGetDynamicVar(
                    name,
                    out var keywordVarDefinition))
            {
                label = LocMan.Loc(keywordVarDefinition.LabelLocKey, name);
                minimum = keywordVarDefinition.Minimum;
                maximum = keywordVarDefinition.Maximum;
            }

            AddStepperRow(_leftControls, label, current, minimum, maximum, value =>
            {
                _workingState.DynamicVars[name] = value;
                _temporaryState.DynamicVars[name] = value;
                ApplyWorkingState();
            });
        }
    }

    private void AddDropdownControls()
    {
        if (_item is null || _leftControls is null)
            return;

        CardModel card = _item.Model;

        List<CardPoolModel> pools = CardPrinter.BuildOrderedCardPools()
            .Where(pool => !CommonHelpers.IsInternalPool(pool) || CommonHelpers.SamePool(pool, card.Pool))
            .ToList();
        if (!pools.Any(pool => CommonHelpers.SamePool(pool, card.Pool))
            && !CommonHelpers.IsInternalPool(card.Pool))
        {
            pools.Add(card.Pool);
        }

        AddDropdownRow(_leftControls,
            LocMan.Loc("FILTER_GROUP_CLASS", "Class"),
            pools.Select(pool => new LoadoutDropdownOption(pool.Id.ToString(), CommonHelpers.GetPoolLabel(pool))),
            _workingState.PoolId ?? card.Pool.Id.ToString(),
            selected =>
            {
                CapturePortraitOverride();
                _workingState.PoolId = selected;
                _temporaryState.PoolId = selected;
                ApplyWorkingState();
                Callable.From(RebuildControls).CallDeferred();
            });

        AddDropdownRow(_leftControls,
            LocMan.GameLoc("gameplay_ui", "SORT_TYPE", LocMan.Loc("FILTER_GROUP_TYPE", "Type")),
            Enum.GetValues<CardType>()
                .Where(type => type != CardType.None)
                .Select(type => new LoadoutDropdownOption(type.ToString(), CardPrinter.GetCardTypeLabel(type))),
            _workingState.Type ?? card.Type.ToString(),
            selected =>
            {
                _workingState.Type = selected;
                _temporaryState.Type = selected;
                ApplyWorkingState();
                Callable.From(RebuildControls).CallDeferred();
            });

        AddDropdownRow(_leftControls,
            LocMan.GameLoc("main_menu_ui", "CARD_LIBRARY_RARITY", LocMan.Loc("FILTER_GROUP_RARITY", "Rarity")),
            Enum.GetValues<CardRarity>()
                .Where(rarity => rarity != CardRarity.None)
                .OrderBy(CardPrinter.GetCardRaritySortValue)
                .Select(rarity => new LoadoutDropdownOption(rarity.ToString(), CardPrinter.GetCardRarityLabel(rarity))),
            _workingState.Rarity ?? card.Rarity.ToString(),
            selected =>
            {
                _workingState.Rarity = selected;
                _temporaryState.Rarity = selected;
                ApplyWorkingState();
                Callable.From(RebuildControls).CallDeferred();
            });

        NLoadoutToggle forceAncientRendering = CreateToggle(
            "force_ancient_portrait_rendering",
            LocMan.Loc("CARD_MOD_FORCE_ANCIENT_PORTRAIT_RENDERING", "Force Ancient Portrait Rendering"),
            _workingState.ForceAncientPortraitRendering == true,
            enabled =>
            {
                _workingState.ForceAncientPortraitRendering = enabled;
                _temporaryState.ForceAncientPortraitRendering = enabled;
                ApplyWorkingState();
            });
        _leftControls.AddChild(forceAncientRendering);
    }

    private void AddAttachmentControls()
    {
        if (_item is null || _rightControls is null)
            return;

        _attachmentControls = new VBoxContainer
        {
            Name = "AttachmentControls",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _attachmentControls.AddThemeConstantOverride("separation", 0);
        _rightControls.AddChild(_attachmentControls);
        PopulateAttachmentControls();
    }

    private void RebuildAttachmentControls()
    {
        if (_attachmentControls is null
            || !GodotObject.IsInstanceValid(_attachmentControls)
            || _attachmentControls.GetParent() is null)
        {
            return;
        }

        ClearChildren(_attachmentControls);
        PopulateAttachmentControls();
    }

    private void PopulateAttachmentControls()
    {
        if (_item is null || _attachmentControls is null)
            return;

        List<EnchantmentModel> enchantments = ModelDb.DebugEnchantments
            .Where(model => !IsInternalAttachment(model))
            .OrderBy(GetAttachmentTitle, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (MultiEnchantmentBridge.Available)
        {
            AddMultiEnchantmentEditor(
                _attachmentControls,
                LocMan.Loc("CARD_MOD_ENCHANTMENT", "Enchantment"),
                enchantments);
        }
        else
        {
            CardAttachmentSpec? enchantment = _workingState.Enchantments switch
            {
                null => null,
                { Count: 0 } => new CardAttachmentSpec { Clear = true },
                _ => _workingState.Enchantments[0]
            };

            AddAttachmentEditor(
                _attachmentControls,
                LocMan.Loc("CARD_MOD_ENCHANTMENT", "Enchantment"),
                enchantments,
                enchantment,
                _item.Model.Enchantment,
                true,
                spec =>
                {
                    List<CardAttachmentSpec>? value = spec switch
                    {
                        null => null,
                        { Clear: true } => [],
                        _ => [spec.Clone()]
                    };
                    SetEnchantmentDraft(value);
                },
                enchantment => enchantment.Icon);
        }

        AddAttachmentEditor(
            _attachmentControls,
            LocMan.Loc("CARD_MOD_AFFLICTION", "Affliction"),
            ModelDb.DebugAfflictions
                .Where(model => !IsInternalAttachment(model))
                .OrderBy(GetAttachmentTitle, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _workingState.Affliction,
            _item.Model.Affliction,
            false,
            spec =>
            {
                _workingState.Affliction = spec;
                _temporaryState.Affliction = spec?.Clone();
            });
    }

    private void AddModifyUpgradeAction()
    {
        if (_item is null || _rightControls is null)
            return;

        NLoadoutActionButton button = CreateActionButton(
            "modify_upgrade",
            LocMan.Loc(
                "CARD_MOD_MODIFY_UPGRADE",
                "Modify Upgrade"));
        ConnectActionButton(button, OpenUpgradeModificationScreen);
        _rightControls.AddChild(button);
        ConfigureActionButtonSize(button);
        button.SetEnabled(
            CardModificationRuntime.CanModifyUpgrade(
                _item.Model,
                _workingState));
    }

    private void OpenUpgradeModificationScreen()
    {
        if (_item is null || NLoadoutPanelRoot.Instance is null)
            return;

        NCardUpgradeModificationScreen screen =
            NCardUpgradeModificationScreen.Create();
        screen.Init(
            _item,
            _workingState,
            ApplyUpgradeModificationDraft);
        NLoadoutPanelRoot.Instance.OpenScreen(screen);
    }

    private void ApplyUpgradeModificationDraft(
        CardUpgradeModificationSpec draft)
    {
        if (_item is null)
            return;

        _workingState.UpgradeModification = draft.Clone();
        _temporaryState.UpgradeModification = draft.Clone();
        ApplyWorkingState();
        CommitPendingTemporaryModification();
    }

    private void AddMultiEnchantmentEditor(
        VBoxContainer container,
        string label,
        IReadOnlyList<EnchantmentModel> models)
    {
        container.AddChild(CreateLabel(label, 22, StsColors.gold));

        List<CardAttachmentSpec> specs = GetEnchantmentDraftSnapshot();
        IReadOnlyList<EnchantmentModel> current = _item is null
            ? Array.Empty<EnchantmentModel>()
            : MultiEnchantmentBridge.GetAll(_item.Model);

        bool needsScrolling = specs.Count > VisibleEnchantmentEntries;
        float contentWidth = needsScrolling
            ? EnchantmentContentWidth - NLoadoutNativeScrollbar.Width
            : EnchantmentContentWidth;
        VBoxContainer entries = new()
        {
            Name = "EnchantmentEntries",
            CustomMinimumSize = new Vector2(
                contentWidth,
                specs.Count * EnchantmentEntryHeight),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        entries.AddThemeConstantOverride("separation", 0);

        for (int index = 0; index < specs.Count; index++)
        {
            int capturedIndex = index;
            CardAttachmentSpec spec = specs[index];
            EnchantmentModel? model = models.FirstOrDefault(candidate =>
                    spec.ModelId is not null && MatchesModelId(candidate, spec.ModelId))
                ?? current.FirstOrDefault(candidate =>
                    spec.ModelId is not null && MatchesModelId(candidate, spec.ModelId));
            string title = model is null
                ? spec.ModelId ?? LocMan.Loc("CARD_MOD_ENCHANTMENT", "Enchantment")
                : GetAttachmentTitle(model);

            VBoxContainer entry = new()
            {
                Name = $"Enchantment_{capturedIndex}",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                MouseFilter = MouseFilterEnum.Ignore
            };
            entry.AddThemeConstantOverride("separation", 4);

            HBoxContainer row = new()
            {
                CustomMinimumSize = new Vector2(0f, 44f),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                MouseFilter = MouseFilterEnum.Pass
            };
            row.AddThemeConstantOverride("separation", 8);

            MegaLabel currentLabel = CreateLabel(title, 20, StsColors.cream);
            currentLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(currentLabel);
            if (model is not null)
                AttachHoverTips(row, () => GetAttachmentHoverTips(model));

            NLoadoutActionButton removeButton = CreateActionButton(
                $"remove_enchantment_{capturedIndex}",
                LocMan.Loc("REMOVE", "Remove"));
            removeButton.CustomMinimumSize = new Vector2(120f, 42f);
            ConnectActionButton(removeButton, () =>
            {
                List<CardAttachmentSpec> next = GetEnchantmentDraftSnapshot();
                if (capturedIndex >= next.Count)
                    return;

                next.RemoveAt(capturedIndex);
                SetEnchantmentDraft(next);
                ApplyWorkingState();
                RebuildAttachmentControls();
            });
            row.AddChild(removeButton);
            entry.AddChild(row);

            AddStepperRow(
                entry,
                LocMan.Loc("CARD_MOD_AMOUNT", "Amount"),
                Math.Max(1, spec.Amount),
                1,
                999,
                value =>
                {
                    List<CardAttachmentSpec> next = GetEnchantmentDraftSnapshot();
                    if (capturedIndex >= next.Count)
                        return;

                    next[capturedIndex].Amount = value;
                    SetEnchantmentDraft(next);
                    ApplyWorkingState();
                });
            entry.AddChild(CreateSpacer(4f));
            entries.AddChild(entry);
        }

        if (needsScrolling)
        {
            NScrollableContainer scroll = new()
            {
                Name = "EnchantmentScroll",
                CustomMinimumSize = new Vector2(
                    EnchantmentContentWidth,
                    VisibleEnchantmentEntries * EnchantmentEntryHeight),
                SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
                MouseFilter = MouseFilterEnum.Stop
            };
            Control mask = new()
            {
                Name = "Mask",
                ClipContents = true,
                MouseFilter = MouseFilterEnum.Ignore
            };
            mask.SetAnchorsPreset(LayoutPreset.FullRect);
            mask.OffsetRight = -NLoadoutNativeScrollbar.Width;
            scroll.AddChild(mask);

            entries.Name = "Content";
            entries.SetAnchorsPreset(LayoutPreset.TopWide);
            mask.AddChild(entries);

            NScrollbar scrollbar = NLoadoutNativeScrollbar.Create();
            scrollbar.Name = "Scrollbar";
            scrollbar.CustomMinimumSize = new Vector2(
                NLoadoutNativeScrollbar.Width,
                0f);
            scrollbar.SetAnchorsPreset(LayoutPreset.RightWide);
            scrollbar.OffsetLeft = -NLoadoutNativeScrollbar.Width;
            scrollbar.OffsetTop = NLoadoutNativeScrollbar.EndCapSize;
            scrollbar.OffsetBottom = -NLoadoutNativeScrollbar.EndCapSize;
            scroll.AddChild(scrollbar);
            scroll.DisableScrollingIfContentFits();
            container.AddChild(scroll);
            Callable.From(() =>
            {
                if (GodotObject.IsInstanceValid(scroll)
                    && GodotObject.IsInstanceValid(entries))
                {
                    scroll.SetContent(entries);
                }
            }).CallDeferred();
        }
        else
        {
            container.AddChild(entries);
        }

        IReadOnlyList<LoadoutDropdownOption> options = models
            .Where(model => !specs.Any(spec =>
                spec.ModelId is not null && MatchesModelId(model, spec.ModelId)))
            .Select(model =>
            {
                EnchantmentModel localModel = model;
                return new LoadoutDropdownOption(
                    localModel.Id.ToString(),
                    GetAttachmentTitle(localModel),
                    () => GetAttachmentHoverTips(localModel),
                    GetAttachmentIconSafely(localModel, enchantment => enchantment.Icon));
            })
            .ToList();

        NLoadoutDropdown dropdown = new()
        {
            CustomMinimumSize = new Vector2(0f, 52f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            DropdownWidth = 420f
        };
        dropdown.SetItems(
            LocMan.Loc("ADD", "Add"),
            options,
            options.FirstOrDefault().Id ?? NoneOptionId);
        dropdown.SelectedItemChanged += id =>
        {
            if (id == NoneOptionId)
                return;

            List<CardAttachmentSpec> next = GetEnchantmentDraftSnapshot();
            next.Add(new CardAttachmentSpec { ModelId = id, Amount = 1 });
            SetEnchantmentDraft(next);
            ApplyWorkingState();
            Callable.From(RebuildAttachmentControls).CallDeferred();
        };
        container.AddChild(dropdown);
        if (options.Count == 0)
        {
            Callable.From(() =>
            {
                if (GodotObject.IsInstanceValid(dropdown))
                    dropdown.SetEnabled(false);
            }).CallDeferred();
        }
        container.AddChild(CreateSpacer(8f));
    }

    private List<CardAttachmentSpec> GetEnchantmentDraftSnapshot()
    {
        if (_workingState.Enchantments is not null)
            return CardAttachmentSpec.CloneList(_workingState.Enchantments) ?? [];
        if (_item is null)
            return [];

        return MultiEnchantmentBridge.GetAll(_item.Model)
            .Select(enchantment => new CardAttachmentSpec
            {
                ModelId = enchantment.Id.ToString(),
                Amount = Math.Max(1, enchantment.Amount)
            })
            .ToList();
    }

    private void SetEnchantmentDraft(List<CardAttachmentSpec>? specs)
    {
        _workingState.Enchantments = CardAttachmentSpec.CloneList(specs);
        _temporaryState.Enchantments = CardAttachmentSpec.CloneList(specs);
    }

    private void AddKeywordControls()
    {
        if (_item is null || _rightControls is null)
            return;

        IReadOnlySet<CardKeyword> localKeywords =
            GetKeywordsSafely(_item.Model).ToHashSet();
        NCardKeywordEditor editor = new();
        editor.Init(
            _items.Select(item => item.Model).Append(_item.Model).ToList(),
            keyword =>
            {
                string key = LoadoutKeywords.GetStorageKey(keyword);
                return _workingState.KeywordOverrides.TryGetValue(
                    key,
                    out bool saved)
                    ? saved
                    : localKeywords.Contains(keyword);
            },
            (keyword, enabled) =>
            {
                string key = LoadoutKeywords.GetStorageKey(keyword);
                _workingState.KeywordOverrides[key] = enabled;
                _temporaryState.KeywordOverrides[key] = enabled;
                bool hasDefinition = LoadoutKeywordRegistry.TryGet(
                    keyword,
                    out LoadoutKeywordModel definition);
                if (hasDefinition)
                {
                    foreach (LoadoutKeywordDynamicVarDefinition dynamicVar
                             in definition.DynamicVars)
                    {
                        if (enabled)
                        {
                            decimal initial = dynamicVar.DefaultValue;
                            if (_item.Model.DynamicVars.TryGetValue(
                                    dynamicVar.Name,
                                    out var existing))
                            {
                                initial = existing.BaseValue;
                            }
                            _workingState.DynamicVars.TryAdd(
                                dynamicVar.Name,
                                initial);
                            _temporaryState.DynamicVars.TryAdd(
                                dynamicVar.Name,
                                initial);
                        }
                        else
                        {
                            _workingState.DynamicVars.Remove(dynamicVar.Name);
                            _temporaryState.DynamicVars.Remove(dynamicVar.Name);
                        }
                    }
                }

                ApplyWorkingState();
                if (hasDefinition)
                    Callable.From(RebuildLeftControls).CallDeferred();
            },
            _selectedKeywordModId,
            selectedId =>
        {
            _selectedKeywordModId = selectedId;
        });
        _rightControls.AddChild(editor);
    }

    private void AddAttachmentEditor<TModel>(
        VBoxContainer container,
        string label,
        IReadOnlyList<TModel> models,
        CardAttachmentSpec? savedSpec,
        TModel? currentModel,
        bool showAmountEditor,
        Action<CardAttachmentSpec?> setSpec,
        Func<TModel, Texture2D?>? iconProvider = null)
        where TModel : AbstractModel
    {
        container.AddChild(CreateLabel(label, 22, StsColors.gold));

        string selectedId = savedSpec?.Clear == true
            ? NoneOptionId
            : savedSpec?.ModelId ?? currentModel?.Id.ToString() ?? NoneOptionId;
        bool hasCurrent = selectedId != NoneOptionId;

        if (hasCurrent)
        {
            TModel? current = models.FirstOrDefault(model => MatchesModelId(model, selectedId));
            string currentTitle = current is not null
                ? GetAttachmentTitle(current)
                : currentModel is not null && MatchesModelId(currentModel, selectedId)
                    ? GetAttachmentTitle(currentModel)
                    : selectedId;
            HBoxContainer currentRow = new()
            {
                CustomMinimumSize = new Vector2(0f, 44f),
                MouseFilter = MouseFilterEnum.Pass
            };
            currentRow.AddThemeConstantOverride("separation", 8);
            MegaLabel currentLabel = CreateLabel(
                currentTitle,
                20,
                StsColors.cream);
            currentLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            currentRow.AddChild(currentLabel);
            if ((current ?? currentModel) is { } hoverModel)
                AttachHoverTips(currentRow, () => GetAttachmentHoverTips(hoverModel));

            NLoadoutActionButton removeButton = CreateActionButton($"remove_{label}", LocMan.Loc("REMOVE", "Remove"));
            removeButton.CustomMinimumSize = new Vector2(120f, 42f);
            ConnectActionButton(removeButton, () =>
            {
                setSpec(new CardAttachmentSpec { Clear = true });
                ApplyWorkingState();
                RebuildAttachmentControls();
            });
            currentRow.AddChild(removeButton);
            container.AddChild(currentRow);

            if (showAmountEditor)
            {
                int currentAmount = Math.Max(1, savedSpec?.Amount ?? GetAttachmentAmount(currentModel));
                AddStepperRow(container, LocMan.Loc("CARD_MOD_AMOUNT", "Amount"), currentAmount, 1, 999, value =>
                {
                    setSpec(new CardAttachmentSpec { ModelId = selectedId, Amount = value });
                    ApplyWorkingState();
                });
            }

            container.AddChild(CreateSpacer(8f));
            return;
        }

        IReadOnlyList<LoadoutDropdownOption> options = models
            .Select(model =>
            {
                TModel localModel = model;
                return new LoadoutDropdownOption(
                    localModel.Id.ToString(),
                    GetAttachmentTitle(localModel),
                    () => GetAttachmentHoverTips(localModel),
                    GetAttachmentIconSafely(localModel, iconProvider));
            })
            .ToList();

        if (options.Count == 0)
        {
            MegaLabel emptyLabel = CreateLabel(LocMan.Loc("CARD_MOD_NO_VALID_ATTACHMENTS", "No valid attachments available"), 18, StsColors.cream);
            emptyLabel.CustomMinimumSize = new Vector2(0f, 38f);
            container.AddChild(emptyLabel);
            container.AddChild(CreateSpacer(8f));
            return;
        }

        NLoadoutDropdown dropdown = new()
        {
            CustomMinimumSize = new Vector2(0f, 52f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            DropdownWidth = 420f
        };
        dropdown.SetItems(LocMan.Loc("ADD", "Add"), options, options.FirstOrDefault().Id ?? NoneOptionId);
        dropdown.SelectedItemChanged += id =>
        {
            if (id == NoneOptionId)
                return;

            setSpec(new CardAttachmentSpec { ModelId = id, Amount = 1 });
            ApplyWorkingState();
            Callable.From(RebuildAttachmentControls).CallDeferred();
        };
        container.AddChild(dropdown);
        container.AddChild(CreateSpacer(8f));
    }

    private static Texture2D? GetAttachmentIconSafely<TModel>(
        TModel model,
        Func<TModel, Texture2D?>? iconProvider)
        where TModel : AbstractModel
    {
        if (iconProvider is null)
            return null;

        try
        {
            return iconProvider(model);
        }
        catch (Exception exception)
        {
            GD.PushWarning(
                $"CardModification: failed to load the attachment icon for '{model.Id}'. " +
                $"The attachment will remain available without an icon. {exception.Message}");
            return null;
        }
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
        NLoadoutDropdown dropdown = new()
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

    private void SavePermanent()
    {
        if (_item is null)
            return;

        CardModificationSpec permanentState = _workingState.Clone();
        permanentState.Normalize();
        _hasPendingTemporaryCommit = false;

        if (_customRunAuthoringMode)
        {
            _customRunStateSaved?.Invoke(_item, permanentState);
            _temporaryState = permanentState.Clone();
            _workingState = permanentState.Clone();
            _lastAppliedState = permanentState.Clone();
            return;
        }

        bool requestedPermanent = LoadoutImmediateMutationService.RequestCardModification(
            CardModificationOperation.ApplyPermanent,
            _item,
            permanentState);
        if (!requestedPermanent)
            CardModificationRuntime.CommitPermanent(_item, permanentState);

        _temporaryState = new CardModificationSpec();
        _workingState = permanentState.Clone();
        _lastAppliedState = permanentState.Clone();

        // The authoritative targeted update refreshes only the originating card slot.
        // The editor and preview already display this exact state and remain untouched.
    }

    private void ResetTemporary()
    {
        if (_item is null)
            return;

        _hasPendingTemporaryCommit = false;
        if (_customRunAuthoringMode)
        {
            _workingState = new CardModificationSpec();
            _temporaryState = new CardModificationSpec();
            _lastAppliedState = new CardModificationSpec();
            _previewDisplayModel = _item.Model;
            _customRunStateSaved?.Invoke(_item, _workingState);
            RebuildControls();
            RefreshPreview(forceReload: true);
            return;
        }
        if (!TryResolveCurrentLocation(_item, out LoadoutOwnedItem<CardModel>? current)
            || current is null)
        {
            return;
        }
        _item = current;
        if (_itemIndex >= 0 && _itemIndex < _items.Count)
            _items[_itemIndex] = current;
        CardPortraitRuntime.ResetTemporary(_item.Model);
        if (_previewDisplayModel is { } temporaryPreview
            && !ReferenceEquals(temporaryPreview, _item.Model))
        {
            CardPortraitRuntime.ResetTemporary(temporaryPreview);
        }
        RefreshPreview(forceReload: true);
        NotifyPortraitChanged(_item);
        _awaitingResetConfirmation = true;
        bool requested = LoadoutImmediateMutationService.RequestCardModification(CardModificationOperation.ResetTemporaryToBasic, _item);
        if (!requested)
            CardModificationRuntime.ResetTemporaryToBasic(_item);
    }

    private void ResetPermanent()
    {
        if (_item is null)
            return;

        _hasPendingTemporaryCommit = false;
        if (_customRunAuthoringMode)
        {
            ResetTemporary();
            return;
        }
        if (!TryResolveCurrentLocation(_item, out LoadoutOwnedItem<CardModel>? current)
            || current is null)
        {
            return;
        }
        _item = current;
        if (_itemIndex >= 0 && _itemIndex < _items.Count)
            _items[_itemIndex] = current;
        CardPortraitRuntime.ResetPermanent(_item.Model);
        if (_previewDisplayModel is { } permanentPreview
            && !ReferenceEquals(permanentPreview, _item.Model))
        {
            CardPortraitRuntime.ResetTemporary(permanentPreview);
        }
        RefreshPreview(forceReload: true);
        NotifyPortraitChanged(_item);
        _awaitingResetConfirmation = true;
        bool requested = LoadoutImmediateMutationService.RequestCardModification(CardModificationOperation.ResetPermanentToBasic, _item);
        if (!requested)
            CardModificationRuntime.ResetPermanentToBasic(_item);
    }

    private void AddCopiesToDeck()
    {
        if (_item is null)
            return;

        // Make the card currently shown by the editor authoritative before the
        // exact-clone mutation is queued. Both operations share the FIFO mutation
        // executor, so host and guests clone the same finalized source state.
        CommitPendingTemporaryModification();

        int amount = NGenericSelectScreen.GetCurrentInputMultiplier();
        if (_customRunAuthoringMode)
        {
            _customRunAddCopies?.Invoke(_item, amount);
            return;
        }
        if (!CardModifier.AddCopiesToTargetDeck(_item, amount))
        {
            GD.PushWarning($"CardModification: failed adding {amount} copies of '{_item.Model.Id}' to player {_item.OwnerNetId}.");
        }
    }

    private void ApplyWorkingState()
    {
        if (_item is null)
            return;

        CardModificationSpec previousState = _lastAppliedState.Clone();
        CardModificationSpec previewState = _workingState.Clone();
        previewState.Normalize();
        bool forceReload = HasStructuralVisualChange(previousState, previewState);
        _previewDisplayModel = CardModificationRuntime.CreatePreviewCard(_item.Model, previewState);

        _lastAppliedState = previewState.Clone();
        _hasPendingTemporaryCommit = true;
        RefreshPreview(forceReload);
    }

    private bool CommitPendingTemporaryModification()
    {
        if (!_hasPendingTemporaryCommit || _item is null)
            return false;

        _hasPendingTemporaryCommit = false;
        if (_customRunAuthoringMode)
        {
            CardModificationSpec authored = _workingState.Clone();
            authored.Normalize();
            _customRunStateSaved?.Invoke(_item, authored);
            _temporaryState = authored.Clone();
            return true;
        }
        CardModificationSpec state = _temporaryState.Clone();
        state.Normalize();
        SuppressStateRefreshThisFrame();
        if (LoadoutImmediateMutationService.RequestCardModification(
                CardModificationOperation.SaveTemporary,
                _item,
                state))
        {
            return true;
        }

        CardModificationRuntime.SaveTemporary(_item, state);
        return true;
    }

    private void SuppressStateRefreshThisFrame()
    {
        _suppressStateRefreshThisFrame = true;
        Callable.From(() => _suppressStateRefreshThisFrame = false).CallDeferred();
    }

    private static bool HasStructuralVisualChange(CardModificationSpec previousState, CardModificationSpec nextState)
    {
        return CardModificationRuntime.GetVisualRefreshKind(previousState, nextState)
               == LoadoutCardVisualRefreshKind.Reload;
    }

    private void OpenTextEditor(TextEditTarget target)
    {
        if (_item is null)
            return;

        CloseTextEditor();

        Control overlay = new()
        {
            Name = "TextEditorOverlay",
            MouseFilter = MouseFilterEnum.Stop,
            ZIndex = 420
        };
        ApplyFullRectLayout(overlay);

        ColorRect dimmer = new()
        {
            Color = new Color(0f, 0f, 0f, 0.62f),
            MouseFilter = MouseFilterEnum.Stop
        };
        ApplyFullRectLayout(dimmer);
        overlay.AddChild(dimmer);

        Control panel = new()
        {
            CustomMinimumSize = target == TextEditTarget.Name
                ? new Vector2(720f, 228f)
                : new Vector2(820f, 468f),
            MouseFilter = MouseFilterEnum.Stop
        };
        panel.SetAnchorsPreset(LayoutPreset.Center);
        Vector2 panelSize = panel.CustomMinimumSize;
        panel.OffsetLeft = -panelSize.X * 0.5f;
        panel.OffsetTop = -panelSize.Y * 0.5f;
        panel.OffsetRight = panelSize.X * 0.5f;
        panel.OffsetBottom = panelSize.Y * 0.5f;
        overlay.AddChild(panel);

        ColorRect panelBackground = new()
        {
            Color = new Color(0.063f, 0.125f, 0.151f, 0.98f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        ApplyFullRectLayout(panelBackground);
        panel.AddChild(panelBackground);

        MarginContainer margin = new();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 24);
        margin.AddThemeConstantOverride("margin_top", 20);
        margin.AddThemeConstantOverride("margin_right", 24);
        margin.AddThemeConstantOverride("margin_bottom", 20);
        panel.AddChild(margin);

        VBoxContainer content = new()
        {
            MouseFilter = MouseFilterEnum.Ignore
        };
        content.AddThemeConstantOverride("separation", 12);
        margin.AddChild(content);

        content.AddChild(CreateSectionLabel(target == TextEditTarget.Name
            ? LocMan.Loc("CARD_MOD_MODIFY_NAME", "Modify Name")
            : LocMan.Loc("CARD_MOD_MODIFY_DESCRIPTION", "Modify Description")));

        Control input = CreateTextInput(target);
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
            SetCustomText(target, ReadTextInput(input));
            CloseTextEditor();
            ApplyWorkingState();
            RebuildControls();
        });
        buttons.AddChild(saveButton);
        content.AddChild(buttons);

        AddChild(overlay);
        _textEditorOverlay = overlay;

        if (input is LineEdit lineEdit)
            lineEdit.GrabFocus();
        else if (input is TextEdit textEdit)
            textEdit.GrabFocus();
    }

    private Control CreateTextInput(TextEditTarget target)
    {
        string currentText = GetRawCardTextForEditor(target);

        if (target == TextEditTarget.Name)
        {
            LineEdit lineEdit = new()
            {
                Text = currentText,
                CustomMinimumSize = new Vector2(0f, 52f),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                MouseFilter = MouseFilterEnum.Stop
            };
            return lineEdit;
        }

        TextEdit textEdit = new()
        {
            Text = currentText,
            CustomMinimumSize = new Vector2(0f, 280f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Stop
        };
        return textEdit;
    }

    private static string ReadTextInput(Control input)
    {
        return input switch
        {
            LineEdit lineEdit => lineEdit.Text ?? string.Empty,
            TextEdit textEdit => textEdit.Text ?? string.Empty,
            _ => string.Empty
        };
    }

    private void SetCustomText(TextEditTarget target, string value)
    {
        string? normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (target == TextEditTarget.Name)
        {
            _workingState.CustomTitle = normalized;
            _temporaryState.CustomTitle = normalized;
            return;
        }

        _workingState.CustomDescription = normalized;
        _temporaryState.CustomDescription = normalized;
    }

    private string GetRawCardTextForEditor(TextEditTarget target)
    {
        if (_item is null)
            return string.Empty;

        try
        {
            if (target == TextEditTarget.Name)
                return _workingState.CustomTitle ?? _item.Model.TitleLocString.GetRawText();

            return _workingState.CustomDescription ?? _item.Model.Description.GetRawText();
        }
        catch
        {
            return target == TextEditTarget.Name
                ? _item.Model.Title
                : _item.Model.GetDescriptionForPile(_item.CardPileType ?? PileType.Deck);
        }
    }

    private void CapturePortraitOverride()
    {
        if (_item is null)
            return;

        string portraitPath = _workingState.PortraitPath ?? _item.Model.PortraitPath;
        string betaPortraitPath = _workingState.BetaPortraitPath ?? _item.Model.BetaPortraitPath;
        _workingState.PortraitPath ??= portraitPath;
        _workingState.BetaPortraitPath ??= betaPortraitPath;
        _temporaryState.PortraitPath ??= portraitPath;
        _temporaryState.BetaPortraitPath ??= betaPortraitPath;
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

    private void RefreshPreview(bool forceReload = false)
    {
        if (_isClosing || !IsInsideTree() || _previewHost is null || _item is null)
            return;

        LayoutPreviewNavigation();

        if (_previewCard is null || !GodotObject.IsInstanceValid(_previewCard))
        {
            ClearChildren(_previewHost);
            _previewCard = NCard.Create(GetPreviewCardModel(_item.Model));
            if (_previewCard is null)
                return;

            _previewHost.AddChild(_previewCard);
            forceReload = false;
        }

        NCard card = _previewCard;
        if (card.GetParent() != _previewHost)
            _previewHost.AddChild(card);

        ReassignPreviewCardModel(card, GetPreviewCardModel(_item.Model), forceReload);
        card.SetAnchorsPreset(LayoutPreset.Center);
        card.Position = Vector2.Zero;
        card.Scale = Vector2.One * GetPreviewScale();
        card.MouseFilter = MouseFilterEnum.Ignore;
        Callable.From(() =>
        {
            if (!_isClosing && IsInsideTree() && GodotObject.IsInstanceValid(card))
                card.UpdateVisuals(_item.CardPileType ?? PileType.Deck, CardPreviewMode.Normal);
        }).CallDeferred();
        RefreshHoverTips();
    }

    private void RefreshHoverTips()
    {
        ClearHoverTips();

        if (_nativeHoverTipAnchor is null || !GodotObject.IsInstanceValid(_nativeHoverTipAnchor) || _item is null || !Visible || !IsInsideTree())
            return;

        IReadOnlyList<IHoverTip> tips;
        CardModel hoverTipModel = _previewCard?.Model ?? _item.Model;
        try
        {
            tips = IHoverTip.RemoveDupes(hoverTipModel.HoverTips)
                .Where(tip => tip is not null)
                .ToList();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: could not create hover tips for '{_item.Model.Id}'. {exception.Message}");
            return;
        }

        if (tips.Count == 0)
            return;

        LayoutNativeHoverTipAnchor();

        try
        {
            NHoverTipSet? tipSet = NHoverTipSet.CreateAndShow(_nativeHoverTipAnchor, tips, HoverTipAlignment.Right);
            if (tipSet is not null)
                KeepHoverTipsBelowScreenUi(tipSet);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: failed to show native hover tips for '{hoverTipModel.Id}'. {exception.Message}");
        }
    }

    private void ClearHoverTips()
    {
        if (_nativeHoverTipAnchor is not null && GodotObject.IsInstanceValid(_nativeHoverTipAnchor))
            NHoverTipSet.Remove(_nativeHoverTipAnchor);

        if (_nativeHoverTipScroll is not null && GodotObject.IsInstanceValid(_nativeHoverTipScroll))
        {
            _nativeHoverTipScroll.GetParent()?.RemoveChild(_nativeHoverTipScroll);
            _nativeHoverTipScroll.QueueFree();
        }

        if (_nativeCardHoverTips is not null && GodotObject.IsInstanceValid(_nativeCardHoverTips))
        {
            _nativeCardHoverTips.GetParent()?.RemoveChild(_nativeCardHoverTips);
            _nativeCardHoverTips.QueueFree();
        }

        _nativeHoverTipScroll = null;
        _nativeCardHoverTips = null;
    }

    private void LayoutNativeHoverTipAnchor()
    {
        if (_nativeHoverTipAnchor is null)
            return;

        Vector2 viewport = GetViewportRect().Size;
        if (viewport == Vector2.Zero)
            return;

        float x = viewport.X - HoverTipWidth - HoverTipViewportMargin;
        float y = MathF.Max(112f, viewport.Y * 0.40f);
        if (_previewCard is not null && GodotObject.IsInstanceValid(_previewCard))
        {
            Vector2 cardSize = NCard.defaultSize * _previewCard.Scale;
            x = _previewCard.GlobalPosition.X + (cardSize.X * 0.5f) + HoverTipCardGap;
            y = _previewCard.GlobalPosition.Y - 34f;
        }

        _nativeHoverTipAnchor.SetAnchorsPreset(LayoutPreset.TopLeft);
        x = Mathf.Clamp(x, HoverTipViewportMargin, MathF.Max(HoverTipViewportMargin, viewport.X - HoverTipWidth - HoverTipViewportMargin));
        if (_rightArrow is not null && GodotObject.IsInstanceValid(_rightArrow))
            y = _rightArrow.GlobalPosition.Y + _rightArrow.Size.Y * 1.5f;
        _nativeHoverTipAnchor.Position = new Vector2(x, y);
        _nativeHoverTipAnchor.Size = new Vector2(HoverTipWidth, GetHoverTipAvailableHeight(viewport, y));
        _nativeHoverTipAnchor.MouseFilter = MouseFilterEnum.Ignore;
    }

    private void KeepHoverTipsBelowScreenUi(NHoverTipSet tipSet)
    {
        if (_nativeHoverTipAnchor is null || !GodotObject.IsInstanceValid(_nativeHoverTipAnchor))
            return;

        NHoverTipCardContainer? cardTips = tipSet.GetNodeOrNull<NHoverTipCardContainer>("cardHoverTipContainer");
        if (cardTips is not null && cardTips.GetChildCount() > 0)
        {
            cardTips.GetParent()?.RemoveChild(cardTips);
            AddChild(cardTips);
            _nativeCardHoverTips = cardTips;
            LayoutNativeCardHoverTips(cardTips);
            cardTips.ZIndex = 20;
            cardTips.ZAsRelative = true;
            cardTips.MouseFilter = MouseFilterEnum.Ignore;
        }

        NScrollableContainer scroll = EnsureNativeHoverTipScroll();
        tipSet.GetParent()?.RemoveChild(tipSet);
        tipSet.Name = "Content";
        scroll.GetNode<Control>("Mask").AddChild(tipSet);
        NormalizeHoverTipSetForScroll(tipSet, scroll);
        tipSet.ZIndex = 0;
        tipSet.ZAsRelative = true;
        tipSet.MouseFilter = MouseFilterEnum.Ignore;

        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(tipSet) && GodotObject.IsInstanceValid(scroll))
                NormalizeHoverTipSetForScroll(tipSet, scroll);
            if (cardTips is not null && GodotObject.IsInstanceValid(cardTips))
                LayoutNativeCardHoverTips(cardTips);
        }).CallDeferred();
    }

    private void LayoutNativeCardHoverTips(NHoverTipCardContainer cardTips)
    {
        Vector2 viewport = GetViewportRect().Size;
        if (viewport == Vector2.Zero)
            return;

        float mirroredRightEdge = viewport.X * 0.25f;
        float bottomEdge = viewport.Y - HoverTipViewportMargin;
        if (_previewCard is not null && GodotObject.IsInstanceValid(_previewCard))
        {
            Vector2 cardSize = _previewCard.GetCurrentSize();
            mirroredRightEdge = _previewCard.GlobalPosition.X - cardSize.X * 0.5f;
            bottomEdge = _previewCard.GlobalPosition.Y + cardSize.Y * 0.5f;
        }

        cardTips.LayoutResizeAndReposition(new Vector2(mirroredRightEdge, bottomEdge), HoverTipAlignment.Left);
        cardTips.GlobalPosition = new Vector2(
            MathF.Max(HoverTipViewportMargin, cardTips.GlobalPosition.X),
            Mathf.Clamp(
                bottomEdge - cardTips.Size.Y,
                HoverTipViewportMargin,
                MathF.Max(HoverTipViewportMargin, viewport.Y - cardTips.Size.Y - HoverTipViewportMargin)));
    }

    private NScrollableContainer EnsureNativeHoverTipScroll()
    {
        if (_nativeHoverTipAnchor is null)
            throw new InvalidOperationException("Native hover tip anchor is not available.");

        if (_nativeHoverTipScroll is not null && GodotObject.IsInstanceValid(_nativeHoverTipScroll))
            return _nativeHoverTipScroll;

        NScrollableContainer scroll = new()
        {
            Name = "NativeHoverTipScroll",
            MouseFilter = MouseFilterEnum.Stop
        };
        Control mask = new()
        {
            Name = "Mask",
            ClipContents = true,
            MouseFilter = MouseFilterEnum.Ignore
        };
        mask.SetAnchorsPreset(LayoutPreset.FullRect);
        scroll.AddChild(mask);

        NScrollbar scrollbar = NLoadoutNativeScrollbar.Create();
        scrollbar.Name = "Scrollbar";
        scrollbar.CustomMinimumSize = new Vector2(
            NLoadoutNativeScrollbar.Width,
            0f);
        scrollbar.SetAnchorsPreset(LayoutPreset.RightWide);
        scrollbar.OffsetLeft = -NLoadoutNativeScrollbar.Width;
        scrollbar.OffsetTop = NLoadoutNativeScrollbar.EndCapSize;
        scrollbar.OffsetBottom = -NLoadoutNativeScrollbar.EndCapSize;
        scroll.AddChild(scrollbar);
        scroll.DisableScrollingIfContentFits();
        scroll.SetAnchorsPreset(LayoutPreset.FullRect);
        _nativeHoverTipAnchor.AddChild(scroll);
        scroll.Size = _nativeHoverTipAnchor.Size;
        scroll.CustomMinimumSize = _nativeHoverTipAnchor.Size;
        _nativeHoverTipScroll = scroll;
        return scroll;
    }

    private static void NormalizeHoverTipSetForScroll(
        NHoverTipSet tipSet,
        NScrollableContainer scroll)
    {
        Control? textTips = tipSet.GetNodeOrNull<Control>("textHoverTipContainer");
        Control? cardTips = tipSet.GetNodeOrNull<Control>("cardHoverTipContainer");
        float y = 0f;
        float width = HoverTipWidth;

        tipSet.Position = Vector2.Zero;
        if (textTips is not null)
        {
            textTips.Position = Vector2.Zero;
            width = MathF.Max(width, textTips.Size.X);
            y = MathF.Max(y, textTips.Size.Y);
        }

        if (cardTips is not null)
        {
            cardTips.Position = new Vector2(0f, y > 0f ? y + 5f : 0f);
            width = MathF.Max(width, cardTips.Size.X);
            y = MathF.Max(y, cardTips.Position.Y + cardTips.Size.Y);
        }

        Vector2 contentSize = new(MathF.Max(HoverTipWidth, width), MathF.Max(1f, y));
        tipSet.Size = contentSize;
        tipSet.CustomMinimumSize = contentSize;
        Control? anchor = scroll.GetParent() as Control;
        float availableHeight = anchor?.Size.Y ?? scroll.Size.Y;
        float viewportHeight = MathF.Min(availableHeight, contentSize.Y);
        bool needsScrolling = contentSize.Y > viewportHeight;
        float viewportWidth = HoverTipWidth
            + (needsScrolling ? NLoadoutNativeScrollbar.Width : 0f);

        if (anchor is not null)
        {
            anchor.Size = new Vector2(viewportWidth, anchor.Size.Y);
            Vector2 viewport = scroll.GetViewportRect().Size;
            float maximumX = MathF.Max(
                HoverTipViewportMargin,
                viewport.X - viewportWidth - HoverTipViewportMargin);
            anchor.Position = new Vector2(
                Mathf.Clamp(
                    anchor.Position.X,
                    HoverTipViewportMargin,
                    maximumX),
                anchor.Position.Y);
        }

        Control mask = scroll.GetNode<Control>("Mask");
        mask.OffsetRight = needsScrolling
            ? -NLoadoutNativeScrollbar.Width
            : 0f;
        scroll.Size = new Vector2(viewportWidth, MathF.Max(1f, viewportHeight));
        scroll.CustomMinimumSize = scroll.Size;
        scroll.SetContent(tipSet);
    }

    private static float GetHoverTipAvailableHeight(Vector2 viewport, float y)
    {
        float available = viewport.Y - y - HoverTipViewportMargin;
        return Mathf.Clamp(available, HoverTipMinHeight, HoverTipMaxHeight);
    }

    private static void ReassignPreviewCardModel(NCard card, CardModel model, bool forceReload)
    {
        if (forceReload || !ReferenceEquals(card.Model, model))
            card.Model = null;

        card.Model = model;
    }

    private static void NotifyPortraitChanged(LoadoutOwnedItem<CardModel> item)
    {
        if (item.CardPileType is null or PileType.Deck)
        {
            LoadoutRunContentChangeService.NotifyCardUpdated(
                item,
                LoadoutCardVisualRefreshKind.Reload);
            return;
        }

        CardModificationRuntime.NotifyCombatCardUpdated(
            item,
            LoadoutCardVisualRefreshKind.Reload);
    }

    private CardModel GetPreviewCardModel(CardModel fallback)
    {
        return _previewDisplayModel ?? fallback;
    }

    private float GetPreviewScale()
    {
        Vector2 viewport = GetViewportRect().Size;
        if (viewport == Vector2.Zero)
            return 2f;

        float laneWidth = MathF.Max(320f, viewport.X - (SidePanelWidth * 2f) - 220f);
        float laneHeight = MathF.Max(420f, viewport.Y - 184f);
        float byHeight = laneHeight / NCard.defaultSize.Y;
        float byWidth = laneWidth / NCard.defaultSize.X;
        return Mathf.Clamp(MathF.Min(byHeight, byWidth), 1.35f, 2.0f);
    }

    private static bool CanSavePermanent()
    {
        return true;
    }

    private static void ConfigureActionButtonSize(Control button)
    {
        button.CustomMinimumSize = new Vector2(ActionButtonWidth, ActionButtonHeight);
        button.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
    }

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

    private static bool SameOwnedItem(LoadoutOwnedItem<CardModel> left, LoadoutOwnedItem<CardModel> right)
    {
        return left.OwnerNetId == right.OwnerNetId
               && (left.CombatCardIndex.HasValue || right.CombatCardIndex.HasValue
                   ? left.CombatCardIndex == right.CombatCardIndex
                     && left.CardPileType == right.CardPileType
                   : left.Index == right.Index)
               && left.Model.Id.Equals(right.Model.Id);
    }

    private static bool MatchesChangedCard(LoadoutOwnedItem<CardModel> item, LoadoutChangedCard changed)
    {
        return item.OwnerNetId == changed.OwnerNetId
               && item.Index == changed.Index
               && item.Model.Id.Equals(changed.ModelId);
    }

    private void BindObservedPile()
    {
        UnbindObservedPile();
        if (!IsInsideTree() || _item?.CardPileType is not PileType pileType)
            return;

        _observedPile = pileType.GetPile(_item.Owner);
        _observedPile.ContentsChanged += OnObservedPileContentsChanged;
    }

    private void UnbindObservedPile()
    {
        if (_observedPile is not null)
            _observedPile.ContentsChanged -= OnObservedPileContentsChanged;
        _observedPile = null;
    }

    private void OnObservedPileContentsChanged()
    {
        if (_isClosing)
            return;
        Callable.From(RefreshAfterDeckMutation).CallDeferred();
    }

    private List<LoadoutOwnedItem<CardModel>> BuildCurrentLocationItems(IEnumerable<Player> owners)
    {
        LoadoutCardPileTarget pileTarget = LoadoutCardPileTargets.FromPileType(_item?.CardPileType ?? PileType.Deck)
            .NormalizeForOwnedCard();
        return owners
            .SelectMany(owner => LoadoutCardPileTargets.BuildOwnedCards(
                LoadoutTargetSelection.ForPlayer(owner.NetId),
                pileTarget))
            .ToList();
    }

    private static bool TryResolveCurrentLocation(
        LoadoutOwnedItem<CardModel> item,
        out LoadoutOwnedItem<CardModel>? resolved)
    {
        resolved = null;
        PileType pileType = item.CardPileType ?? PileType.Deck;
        if (pileType == PileType.Deck)
        {
            IReadOnlyList<CardModel> deckCards = item.Owner.Deck.Cards;
            for (int deckIndex = 0; deckIndex < deckCards.Count; deckIndex++)
            {
                if (!ReferenceEquals(deckCards[deckIndex], item.Model))
                    continue;

                resolved = new LoadoutOwnedItem<CardModel>(
                    item.Owner,
                    deckIndex,
                    deckCards[deckIndex],
                    PileType.Deck,
                    null);
                return true;
            }

            if (item.Index < 0 || item.Index >= item.Owner.Deck.Cards.Count)
                return false;
            CardModel card = deckCards[item.Index];
            if (!card.Id.Equals(item.Model.Id))
                return false;
            resolved = new LoadoutOwnedItem<CardModel>(item.Owner, item.Index, card, PileType.Deck, null);
            return true;
        }

        if (!item.CombatCardIndex.HasValue
            || !NetCombatCardDb.Instance.TryGetCard(item.CombatCardIndex.Value, out CardModel? combatCard)
            || combatCard is null
            || combatCard.Owner?.NetId != item.OwnerNetId
            || combatCard.Pile?.Type != pileType
            || !combatCard.Id.Equals(item.Model.Id))
        {
            return false;
        }

        int index = combatCard.Pile.Cards.ToList().IndexOf(combatCard);
        if (index < 0)
            return false;
        resolved = new LoadoutOwnedItem<CardModel>(item.Owner, index, combatCard, pileType, item.CombatCardIndex);
        return true;
    }

    private static NLoadoutActionButton CreateActionButton(string id, string label, Texture2D? icon = null)
    {
        NLoadoutActionButton button = new()
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0f, 42f)
        };
        button.Init(CommonHelpers.MakeSafeNodeName(id), label, icon);
        return button;
    }

    private static void ConnectActionButton(NLoadoutActionButton button, Action action)
    {
        button.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ => action()));
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

    private static MegaLabel CreateSectionLabel(string text)
    {
        MegaLabel label = CreateLabel(text, 25, StsColors.gold);
        label.CustomMinimumSize = new Vector2(0f, 42f);
        return label;
    }

    private static MegaLabel CreateCardIdLabel(string text)
    {
        MegaLabel label = CreateLabel(text, 18, StsColors.cream);
        label.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
        label.TextOverrunBehavior = TextServer.OverrunBehavior.NoTrimming;
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        label.CustomMinimumSize = new Vector2(0f, 24f);
        return label;
    }

    private static Control CreateSpacer(float height)
    {
        return new Control
        {
            CustomMinimumSize = new Vector2(0f, height),
            MouseFilter = MouseFilterEnum.Ignore
        };
    }

    private static void ClearChildren(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            node.RemoveChild(child);
            child.QueueFree();
        }
    }

    private static bool IsInternalAttachment(AbstractModel model)
    {
        string typeName = model.GetType().Name;
        return typeName.StartsWith("Mock", StringComparison.Ordinal)
               || typeName.StartsWith("Deprecated", StringComparison.Ordinal);
    }

    private static bool MatchesModelId(AbstractModel model, string id)
    {
        return string.Equals(model.Id.ToString(), id, StringComparison.Ordinal)
               || string.Equals(model.Id.Entry, id, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<CardKeyword> GetKeywordsSafely(CardModel card)
    {
        try
        {
            return card.GetKeywordsWithSources(KeywordSources.Local);
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<IHoverTip> GetAttachmentHoverTips(AbstractModel model)
    {
        try
        {
            IEnumerable<IHoverTip> tips = model switch
            {
                EnchantmentModel enchantment => enchantment.HoverTips,
                AfflictionModel affliction => affliction.HoverTips,
                _ => []
            };

            return IHoverTip.RemoveDupes(tips)
                .Where(tip => tip is not null)
                .ToList();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: failed to create hover tip for attachment '{model.Id}'. {exception.Message}");
            return [];
        }
    }

    private static void AttachHoverTips(Control control, Func<IReadOnlyList<IHoverTip>> tipsFactory)
    {
        control.MouseEntered += () => ShowHoverTips(control, tipsFactory);
        control.FocusEntered += () => ShowHoverTips(control, tipsFactory);
        control.MouseExited += () => NHoverTipSet.Remove(control);
        control.FocusExited += () => NHoverTipSet.Remove(control);
    }

    private static void ShowHoverTips(Control control, Func<IReadOnlyList<IHoverTip>> tipsFactory)
    {
        try
        {
            List<IHoverTip> tips = tipsFactory()
                .Where(tip => tip is not null)
                .ToList();
            if (tips.Count == 0)
                return;

            NHoverTipSet.Remove(control);
            NHoverTipSet.CreateAndShow(control, IHoverTip.RemoveDupes(tips), HoverTip.GetHoverTipAlignment(control))?.SetFollowOwner();
            NLoadoutPanelRoot.Instance?.AdoptGameHoverTips();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: failed to show hover tip. {exception.Message}");
        }
    }

    private static string GetAttachmentTitle(AbstractModel model)
    {
        try
        {
            return model switch
            {
                EnchantmentModel enchantment => enchantment.Title.GetFormattedText(),
                AfflictionModel affliction => affliction.Title.GetFormattedText(),
                _ => model.Id.Entry
            };
        }
        catch
        {
            return CommonHelpers.PrettifyPoolTypeName(model.GetType().Name);
        }
    }

    private static int GetAttachmentAmount(AbstractModel? model)
    {
        return model switch
        {
            EnchantmentModel enchantment => Math.Max(1, enchantment.Amount),
            AfflictionModel affliction => Math.Max(1, affliction.Amount),
            _ => 1
        };
    }
}
