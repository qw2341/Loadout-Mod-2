#nullable enable

namespace Loadout.UI.Screens;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Loadout.PanelItems;
using Loadout.Services.Actions;
using Loadout.Services.CardModification;
using Loadout.Services.Targets;
using Loadout.UI.Managers;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.Cards;
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
    private const float ActionButtonHeight = 42f;
    private const float HoverTipCardGap = 22f;
    private const float HoverTipViewportMargin = 24f;
    private const float HoverTipWidth = 360f;
    private const float HoverTipMinHeight = 220f;
    private const float HoverTipMaxHeight = 460f;

    private LoadoutOwnedItem<CardModel>? _item;
    private List<LoadoutOwnedItem<CardModel>> _items = [];
    private int _itemIndex;
    private Action? _parentRefresh;
    private CardModificationState _workingState = new();
    private CardModificationState _temporaryState = new();
    private CardModificationState _lastAppliedState = new();
    private VBoxContainer? _leftControls;
    private VBoxContainer? _rightControls;
    private VBoxContainer? _actionControls;
    private HBoxContainer? _cardEditActions;
    private Control? _nativeHoverTipAnchor;
    private ScrollContainer? _nativeHoverTipScroll;
    private Control? _backButtonMount;
    private Control? _previewHost;
    private Control? _leftArrowMount;
    private Control? _rightArrowMount;
    private NButton? _leftArrow;
    private NButton? _rightArrow;
    private NBackButton? _backButton;
    private NCard? _previewCard;
    private MegaLabel? _titleLabel;
    private Control? _textEditorOverlay;
    private bool _signalsBound;
    private bool _stateEventsBound;
    private bool _runContentEventsBound;
    private bool _hasPendingTemporaryCommit;

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
        Action? parentRefresh = null)
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
        BindStateEvents();
        RebuildScreen();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized)
        {
            ApplyFullRectLayout(this);
            RefreshPreview(forceReload: false);
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
        CommitPendingTemporaryModification();
        UnbindStateEvents();
        UnbindRunContentEvents();
        CloseTextEditor();
        ClearHoverTips();
    }

    private void BindStateEvents()
    {
        if (_stateEventsBound)
            return;

        CardModificationStateService.StateChanged += OnCardModificationStateChanged;
        LoadoutRunContentChangeService.Changed += OnRunContentChanged;
        _stateEventsBound = true;
        _runContentEventsBound = true;
    }

    private void UnbindStateEvents()
    {
        if (!_stateEventsBound)
            return;

        CardModificationStateService.StateChanged -= OnCardModificationStateChanged;
        _stateEventsBound = false;
    }

    private void UnbindRunContentEvents()
    {
        if (!_runContentEventsBound)
            return;

        LoadoutRunContentChangeService.Changed -= OnRunContentChanged;
        _runContentEventsBound = false;
    }

    private void OnCardModificationStateChanged()
    {
        if (!IsInsideTree() || _item is null || _hasPendingTemporaryCommit)
            return;

        Callable.From(RefreshFromAppliedState).CallDeferred();
    }

    private void OnRunContentChanged(LoadoutRunContentChangedEventArgs change)
    {
        if (change.Kind != LoadoutRunContentKind.Cards
            || _item is null
            || !change.AffectsPlayer(_item.OwnerNetId)
            || !IsInsideTree()
            || change.Mode == LoadoutRunContentChangeMode.Add)
        {
            return;
        }

        if (change.Mode == LoadoutRunContentChangeMode.Update
            && change.ChangedCards.Count > 0
            && !change.ChangedCards.Any(changed => MatchesChangedCard(_item, changed)))
        {
            return;
        }

        Callable.From(RefreshAfterDeckMutation).CallDeferred();
    }

    private void RefreshFromAppliedState()
    {
        if (_item is null || !IsInsideTree())
            return;

        LoadItem(_item);
        RefreshParentView();
        RebuildControls();
        RefreshPreview(forceReload: true);
    }

    private void RefreshAfterDeckMutation()
    {
        if (_item is null || !IsInsideTree())
            return;

        List<Player> owners = _items
            .Select(item => item.Owner)
            .Where(player => player is not null)
            .Distinct()
            .ToList();
        if (owners.Count == 0)
            owners.Add(_item.Owner);

        List<LoadoutOwnedItem<CardModel>> refreshedItems = owners
            .SelectMany(player => player.Deck.Cards.Select((card, index) => new LoadoutOwnedItem<CardModel>(player, index, card)))
            .ToList();

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
            refreshedIndex = Mathf.Clamp(_itemIndex, 0, refreshedItems.Count - 1);
        }

        _items = refreshedItems;
        _itemIndex = refreshedIndex;
        LoadItem(_items[_itemIndex]);
        RefreshParentView();
        RebuildControls();
        RefreshPreview(forceReload: true);
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
        _backButtonMount = GetNodeOrNull<Control>("%BackButtonMount");
        _leftControls = GetNodeOrNull<VBoxContainer>("%LeftControls");
        _rightControls = GetNodeOrNull<VBoxContainer>("%RightControls");
        _actionControls = GetNodeOrNull<VBoxContainer>("%ActionRow");
        _cardEditActions = GetNodeOrNull<HBoxContainer>("%CardEditActions");
        _nativeHoverTipAnchor = GetNodeOrNull<Control>("%NativeHoverTipAnchor");
        _previewHost = GetNodeOrNull<Control>("%PreviewCardHost");
        _leftArrowMount = GetNodeOrNull<Control>("%LeftArrow");
        _rightArrowMount = GetNodeOrNull<Control>("%RightArrow");
        _leftArrow = EnsureInspectArrowButton(_leftArrowMount, isLeft: true);
        _rightArrow = EnsureInspectArrowButton(_rightArrowMount, isLeft: false);

        EnsureBackButton();
        BindSceneSignals();
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
            CommitPendingTemporaryModification();
            NLoadoutPanelRoot.CloseTopLoadoutScreen();
        }));
        _backButtonMount.AddChild(backButton);
        _backButton = backButton;
        Callable.From(RefreshNativeButtonState).CallDeferred();
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
        _workingState = CardModificationStateService.GetEffectiveState(item);
        _temporaryState = CardModificationStateService.GetTemporaryState(item);
        CardModificationStateService.ApplyEffectiveStateToOwnedCard(item);
        _lastAppliedState = _workingState.Clone();
        _hasPendingTemporaryCommit = false;
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
        RebuildControls();
        RefreshPreview();
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

        _titleLabel = CreateLabel(CardPrinter.FormatCardTitle(_item.Model), 32, StsColors.gold);
        _leftControls.AddChild(_titleLabel);
        _leftControls.AddChild(CreateLabel(_item.Model.Id.ToString(), 18, StsColors.cream));
        _leftControls.AddChild(CreateSpacer(6f));

        AddDropdownControls();
        AddNumericControls();
        AddCardEditActions();
        AddKeywordControls();
        AddAttachmentControls();

        if (CanSavePermanent())
        {
            NLoadoutActionButton permanentButton = CreateActionButton("save_permanent", LocMan.Loc("SAVE_PERMANENT", "Save Permanent"), CommonHelpers.LoadActionButtonIcon("CardPrinter.png"));
            ConnectActionButton(permanentButton, SavePermanent);
            _actionControls.AddChild(permanentButton);
            ConfigureActionButtonSize(permanentButton);
        }

        NLoadoutActionButton resetTemporaryButton = CreateActionButton("reset_temporary", LocMan.Loc("RESET_TEMPORARY", "Reset Temporary"));
        ConnectActionButton(resetTemporaryButton, ResetTemporary);
        _actionControls.AddChild(resetTemporaryButton);
        ConfigureActionButtonSize(resetTemporaryButton);

        if (CanSavePermanent())
        {
            NLoadoutActionButton resetPermanentButton = CreateActionButton("reset_permanent", LocMan.Loc("RESET_PERMANENT", "Reset Permanent"));
            ConnectActionButton(resetPermanentButton, ResetPermanent);
            _actionControls.AddChild(resetPermanentButton);
            ConfigureActionButtonSize(resetPermanentButton);
        }
    }

    private void AddCardEditActions()
    {
        if (_cardEditActions is null)
            return;

        NLoadoutActionButton nameButton = CreateActionButton("modify_name", LocMan.Loc("CARD_MOD_MODIFY_NAME", "Modify Name"));
        nameButton.CustomMinimumSize = new Vector2(CardEditButtonWidth, ActionButtonHeight);
        ConnectActionButton(nameButton, () => OpenTextEditor(TextEditTarget.Name));
        _cardEditActions.AddChild(nameButton);

        NLoadoutActionButton descriptionButton = CreateActionButton("modify_description", LocMan.Loc("CARD_MOD_MODIFY_DESCRIPTION", "Modify Description"));
        descriptionButton.CustomMinimumSize = new Vector2(CardEditButtonWidth, ActionButtonHeight);
        ConnectActionButton(descriptionButton, () => OpenTextEditor(TextEditTarget.Description));
        _cardEditActions.AddChild(descriptionButton);
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

        foreach ((string name, var dynamicVar) in card.DynamicVars.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            int current = _workingState.DynamicVars.TryGetValue(name, out decimal saved)
                ? Decimal.ToInt32(saved)
                : Decimal.ToInt32(dynamicVar.BaseValue);
            
            AddStepperRow(_leftControls, LocMan.DynamicVarLoc(dynamicVar), current, int.MinValue, int.MaxValue, value =>
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
            _workingState.PoolId = selected;
            _temporaryState.PoolId = selected;
            CapturePortraitOverride();
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
    }

    private void AddAttachmentControls()
    {
        if (_item is null || _rightControls is null)
            return;

        AddAttachmentEditor(
            LocMan.Loc("CARD_MOD_ENCHANTMENT", "Enchantment"),
            ModelDb.DebugEnchantments
                .Where(model => !IsInternalAttachment(model))
                .OrderBy(GetAttachmentTitle, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _workingState.Enchantment,
            _item.Model.Enchantment,
            true,
            spec =>
            {
                _workingState.Enchantment = spec;
                _temporaryState.Enchantment = spec?.Clone();
            });

        AddAttachmentEditor(
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

    private void AddKeywordControls()
    {
        if (_item is null || _rightControls is null)
            return;

        _rightControls.AddChild(CreateSectionLabel(LocMan.Loc("FILTER_GROUP_KEYWORD", "Keyword")));

        GridContainer grid = new()
        {
            Columns = 2,
            CustomMinimumSize = new Vector2(426f, 0f),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            MouseFilter = MouseFilterEnum.Ignore
        };
        grid.AddThemeConstantOverride("h_separation", 8);
        grid.AddThemeConstantOverride("v_separation", 2);
        _rightControls.AddChild(grid);

        IReadOnlySet<CardKeyword> localKeywords = _item.Model.GetKeywordsWithSources(KeywordSources.Local);
        foreach (CardKeyword keyword in GetAvailableKeywords(_item.Model))
        {
            CardKeyword localKeyword = keyword;
            string key = localKeyword.ToString();
            bool isChecked = _workingState.KeywordOverrides.TryGetValue(key, out bool saved)
                ? saved
                : localKeywords.Contains(localKeyword);

            NLoadoutToggle toggle = new()
            {
                CustomMinimumSize = new Vector2(206f, 44f),
                SizeFlagsHorizontal = SizeFlags.ShrinkBegin
            };
            toggle.SetHoverTipsFactory(() => GetKeywordHoverTips(localKeyword));
            toggle.Init($"keyword_{key}", CardPrinter.GetCardKeywordLabel(localKeyword), isChecked);
            toggle.Toggled += changed =>
            {
                _workingState.KeywordOverrides[key] = changed.IsChecked;
                _temporaryState.KeywordOverrides[key] = changed.IsChecked;
                ApplyWorkingState();
            };
            grid.AddChild(toggle);
        }
    }

    private void AddAttachmentEditor<TModel>(
        string label,
        IReadOnlyList<TModel> models,
        CardAttachmentSpec? savedSpec,
        TModel? currentModel,
        bool showAmountEditor,
        Action<CardAttachmentSpec?> setSpec)
        where TModel : AbstractModel
    {
        if (_rightControls is null)
            return;

        _rightControls.AddChild(CreateLabel(label, 22, StsColors.gold));

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
                RebuildControls();
            });
            currentRow.AddChild(removeButton);
            _rightControls.AddChild(currentRow);

            if (showAmountEditor)
            {
                int currentAmount = Math.Max(1, savedSpec?.Amount ?? GetAttachmentAmount(currentModel));
                AddStepperRow(_rightControls, LocMan.Loc("CARD_MOD_AMOUNT", "Amount"), currentAmount, 1, 999, value =>
                {
                    setSpec(new CardAttachmentSpec { ModelId = selectedId, Amount = value });
                    ApplyWorkingState();
                });
            }

            _rightControls.AddChild(CreateSpacer(8f));
            return;
        }

        IReadOnlyList<LoadoutDropdownOption> options = models
            .Select(model =>
            {
                TModel localModel = model;
                return new LoadoutDropdownOption(
                    localModel.Id.ToString(),
                    GetAttachmentTitle(localModel),
                    () => GetAttachmentHoverTips(localModel));
            })
            .ToList();

        if (options.Count == 0)
        {
            MegaLabel emptyLabel = CreateLabel(LocMan.Loc("CARD_MOD_NO_VALID_ATTACHMENTS", "No valid attachments available"), 18, StsColors.cream);
            emptyLabel.CustomMinimumSize = new Vector2(0f, 38f);
            _rightControls.AddChild(emptyLabel);
            _rightControls.AddChild(CreateSpacer(8f));
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
            Callable.From(RebuildControls).CallDeferred();
        };
        _rightControls.AddChild(dropdown);
        _rightControls.AddChild(CreateSpacer(8f));
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

        CardModificationState previousState = _lastAppliedState.Clone();
        CardModificationState permanentState = _workingState.Clone();
        _hasPendingTemporaryCommit = false;
        CardModificationStateService.SavePermanent(_item.Model.Id, permanentState);
        bool requestedPermanent = LoadoutImmediateMutationService.RequestCardModification(
            CardModificationOperation.ApplyPermanent,
            _item,
            permanentState);
        bool requestedTemporaryReset = LoadoutImmediateMutationService.RequestCardModification(
            CardModificationOperation.ResetTemporary,
            _item);

        _temporaryState = new CardModificationState();
        if (!requestedTemporaryReset)
            CardModificationStateService.ResetTemporary(_item);

        _workingState = CardModificationStateService.GetEffectiveState(_item);
        if (!requestedPermanent)
            CardModificationStateService.ApplyEffectiveStateToOwnedCard(_item, previousState);
        _lastAppliedState = _workingState.Clone();
        RefreshParentView();
        RebuildControls();
        RefreshPreview(forceReload: true);
    }

    private void ResetTemporary()
    {
        if (_item is null)
            return;

        _hasPendingTemporaryCommit = false;
        CardModificationStateService.ResetTemporaryToBasic(_item);
        LoadoutImmediateMutationService.RequestCardModification(CardModificationOperation.ResetTemporaryToBasic, _item);
        _temporaryState = new CardModificationState();
        _workingState = CardModificationStateService.GetEffectivePermanentState(_item.Model.Id);
        _lastAppliedState = _workingState.Clone();
        RefreshParentView();
        RebuildControls();
        RefreshPreview(forceReload: true);
    }

    private void ResetPermanent()
    {
        if (_item is null)
            return;

        _hasPendingTemporaryCommit = false;
        CardModificationStateService.ResetPermanentToBasic(_item);
        LoadoutImmediateMutationService.RequestCardModification(CardModificationOperation.ResetPermanentToBasic, _item);
        _workingState = CardModificationStateService.GetEffectiveState(_item);
        _temporaryState = CardModificationStateService.GetTemporaryState(_item);
        _lastAppliedState = _workingState.Clone();
        RefreshParentView();
        RebuildControls();
        RefreshPreview(forceReload: true);
    }

    private void ApplyWorkingState()
    {
        if (_item is null)
            return;

        CardModificationState previousState = _lastAppliedState.Clone();
        CardModificationState previewState = _workingState.Clone();
        previewState.Normalize();
        CardModificationStateService.ApplyPreviewStateToOwnedCard(_item, previewState, previousState);
        _lastAppliedState = _workingState.Clone();
        _hasPendingTemporaryCommit = true;
        RefreshParentView();
        RefreshPreview(forceReload: true);
    }

    private bool CommitPendingTemporaryModification()
    {
        if (!_hasPendingTemporaryCommit || _item is null)
            return false;

        _hasPendingTemporaryCommit = false;
        CardModificationState state = _temporaryState.Clone();
        state.Normalize();
        if (LoadoutImmediateMutationService.RequestCardModification(
                CardModificationOperation.SaveTemporary,
                _item,
                state))
        {
            return true;
        }

        CardModificationStateService.SaveTemporary(_item, state);
        return true;
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

    private void RefreshParentView()
    {
        if (_parentRefresh is null)
            return;

        try
        {
            _parentRefresh.Invoke();
        }
        catch (ObjectDisposedException)
        {
            // The originating select-screen slot can be recycled after deck mutations.
        }
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
                : _item.Model.GetDescriptionForPile(PileType.None);
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
        if (_previewHost is null || _item is null)
            return;

        LayoutPreviewNavigation();

        if (forceReload)
        {
            ClearChildren(_previewHost);
            _previewCard = null;
        }

        if (_previewCard is null || !GodotObject.IsInstanceValid(_previewCard))
        {
            ClearChildren(_previewHost);
            _previewCard = NCard.Create(_item.Model);
            if (_previewCard is null)
                return;

            _previewHost.AddChild(_previewCard);
            forceReload = false;
        }

        NCard card = _previewCard;
        if (card.GetParent() != _previewHost)
            _previewHost.AddChild(card);

        ReassignPreviewCardModel(card, _item.Model, forceReload);
        card.SetAnchorsPreset(LayoutPreset.Center);
        card.Position = Vector2.Zero;
        card.Scale = Vector2.One * GetPreviewScale();
        card.MouseFilter = MouseFilterEnum.Ignore;
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(card))
                card.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
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

        _nativeHoverTipScroll = null;
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

        ScrollContainer scroll = EnsureNativeHoverTipScroll();
        tipSet.GetParent()?.RemoveChild(tipSet);
        scroll.AddChild(tipSet);
        NormalizeHoverTipSetForScroll(tipSet, scroll);
        tipSet.ZIndex = 0;
        tipSet.ZAsRelative = true;
        tipSet.MouseFilter = MouseFilterEnum.Ignore;
    }

    private ScrollContainer EnsureNativeHoverTipScroll()
    {
        if (_nativeHoverTipAnchor is null)
            throw new InvalidOperationException("Native hover tip anchor is not available.");

        if (_nativeHoverTipScroll is not null && GodotObject.IsInstanceValid(_nativeHoverTipScroll))
            return _nativeHoverTipScroll;

        ScrollContainer scroll = new()
        {
            Name = "NativeHoverTipScroll",
            ClipContents = true,
            MouseFilter = MouseFilterEnum.Stop
        };
        scroll.SetAnchorsPreset(LayoutPreset.FullRect);
        _nativeHoverTipAnchor.AddChild(scroll);
        scroll.Size = _nativeHoverTipAnchor.Size;
        scroll.CustomMinimumSize = _nativeHoverTipAnchor.Size;
        _nativeHoverTipScroll = scroll;
        return scroll;
    }

    private static void NormalizeHoverTipSetForScroll(NHoverTipSet tipSet, ScrollContainer scroll)
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
        float viewportHeight = MathF.Min(scroll.Size.Y, contentSize.Y);
        scroll.Size = new Vector2(HoverTipWidth, MathF.Max(1f, viewportHeight));
        scroll.CustomMinimumSize = scroll.Size;
    }

    private static float GetHoverTipAvailableHeight(Vector2 viewport, float y)
    {
        float available = viewport.Y - y - HoverTipViewportMargin;
        return Mathf.Clamp(available, HoverTipMinHeight, HoverTipMaxHeight);
    }

    private static void ReassignPreviewCardModel(NCard card, CardModel model, bool forceReload)
    {
        if (forceReload)
            card.Model = null;

        card.Model = model;
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
        try
        {
            return !RunManager.Instance.IsInProgress || RunManager.Instance.NetService.Type != NetGameType.Client;
        }
        catch
        {
            return true;
        }
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
               && left.Index == right.Index
               && left.Model.Id.Equals(right.Model.Id);
    }

    private static bool MatchesChangedCard(LoadoutOwnedItem<CardModel> item, LoadoutChangedCard changed)
    {
        return item.OwnerNetId == changed.OwnerNetId
               && item.Index == changed.Index
               && item.Model.Id.Equals(changed.ModelId);
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

    private static IReadOnlyList<CardKeyword> GetAvailableKeywords(CardModel card)
    {
        HashSet<CardKeyword> keywords = Enum.GetValues<CardKeyword>()
            .Where(keyword => keyword != CardKeyword.None)
            .ToHashSet();

        foreach (CardKeyword keyword in GetKeywordsSafely(card))
            keywords.Add(keyword);

        foreach (CardModel model in ModelDb.AllCards)
        {
            foreach (CardKeyword keyword in GetKeywordsSafely(model))
                keywords.Add(keyword);
        }

        return keywords
            .Where(keyword => keyword != CardKeyword.None)
            .OrderBy(keyword => Convert.ToInt32(keyword))
            .ToList();
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

    private static IReadOnlyList<IHoverTip> GetKeywordHoverTips(CardKeyword keyword)
    {
        try
        {
            return [HoverTipFactory.FromKeyword(keyword)];
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: failed to create hover tip for keyword '{keyword}'. {exception.Message}");
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
