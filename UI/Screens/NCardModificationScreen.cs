#nullable enable

namespace Loadout.UI.Screens;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Loadout.PanelItems;
using Loadout.Services.CardModification;
using Loadout.Services.Targets;
using Loadout.UI.Managers;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
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

    private LoadoutOwnedItem<CardModel>? _item;
    private List<LoadoutOwnedItem<CardModel>> _items = [];
    private int _itemIndex;
    private Action? _parentRefresh;
    private CardModificationState _workingState = new();
    private CardModificationState _temporaryState = new();
    private VBoxContainer? _leftControls;
    private VBoxContainer? _rightControls;
    private VBoxContainer? _actionControls;
    private HBoxContainer? _cardEditActions;
    private ScrollContainer? _hoverTipHost;
    private VBoxContainer? _hoverTipControls;
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
        RebuildScreen();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized)
        {
            ApplyFullRectLayout(this);
            RefreshPreview(forceReload: false);
            LayoutHoverTipHost();
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
        CloseTextEditor();
        ClearHoverTips();
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
        _hoverTipHost = GetNodeOrNull<ScrollContainer>("%HoverTipHost");
        _hoverTipControls = GetNodeOrNull<VBoxContainer>("%HoverTipControls");
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
        CardModificationStateService.ApplyEffectiveStateToOwnedCard(item);
        _workingState = CardModificationStateService.GetEffectiveState(item);
        _temporaryState = CardModificationStateService.GetTemporaryState(item);
    }

    private void SwitchCard(int direction)
    {
        if (_items.Count == 0)
            return;

        int nextIndex = Mathf.Clamp(_itemIndex + direction, 0, _items.Count - 1);
        if (nextIndex == _itemIndex)
            return;

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

        AddNumericControls();
        AddDropdownControls();
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
        _leftControls.AddChild(CreateSectionLabel(LocMan.Loc("CARD_MOD_NUMERIC_STATS", "Numeric Stats")));

        AddStepperRow(_leftControls, LocMan.Loc("CARD_MOD_ENERGY_COST", "Energy Cost"),
            _workingState.EnergyCost ?? (card.EnergyCost.CostsX ? 0 : card.EnergyCost.GetWithModifiers(CostModifiers.Local)),
            -1, 99, value =>
            {
                _workingState.EnergyCost = value;
                _temporaryState.EnergyCost = value;
                ApplyWorkingState();
            });

        AddStepperRow(_leftControls, LocMan.Loc("CARD_MOD_REPLAY_COUNT", "Replay Count"),
            _workingState.BaseReplayCount ?? card.BaseReplayCount,
            0, 99, value =>
            {
                _workingState.BaseReplayCount = value;
                _temporaryState.BaseReplayCount = value;
                ApplyWorkingState();
            });

        AddStepperRow(_leftControls, LocMan.Loc("CARD_MOD_STAR_COST", "Star Cost"),
            _workingState.BaseStarCost ?? card.BaseStarCost,
            -1, 99, value =>
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

            AddStepperRow(_leftControls, name, current, -999, 999, value =>
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
        _leftControls.AddChild(CreateSectionLabel(LocMan.Loc("CARD_MOD_CARD_FIELDS", "Card Fields")));

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

        _rightControls.AddChild(CreateSectionLabel(LocMan.Loc("CARD_MOD_ATTACHMENTS", "Attachments")));

        AddAttachmentEditor(
            LocMan.Loc("CARD_MOD_ENCHANTMENT", "Enchantment"),
            ModelDb.DebugEnchantments
                .Where(model => !IsInternalAttachment(model))
                .Where(model => CanApplyAttachment(model, _item.Model))
                .OrderBy(GetAttachmentTitle, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _workingState.Enchantment,
            _item.Model.Enchantment,
            spec =>
            {
                _workingState.Enchantment = spec;
                _temporaryState.Enchantment = spec?.Clone();
            });

        AddAttachmentEditor(
            LocMan.Loc("CARD_MOD_AFFLICTION", "Affliction"),
            ModelDb.DebugAfflictions
                .Where(model => !IsInternalAttachment(model))
                .Where(model => CanApplyAttachment(model, _item.Model))
                .OrderBy(GetAttachmentTitle, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _workingState.Affliction,
            _item.Model.Affliction,
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
        foreach (CardKeyword keyword in Enum.GetValues<CardKeyword>()
                     .Where(keyword => keyword != CardKeyword.None)
                     .OrderBy(keyword => Convert.ToInt32(keyword)))
        {
            string key = keyword.ToString();
            bool isChecked = _workingState.KeywordOverrides.TryGetValue(key, out bool saved)
                ? saved
                : localKeywords.Contains(keyword);

            NLoadoutToggle toggle = new()
            {
                CustomMinimumSize = new Vector2(206f, 44f),
                SizeFlagsHorizontal = SizeFlags.ShrinkBegin
            };
            toggle.Init($"keyword_{key}", CardPrinter.GetCardKeywordLabel(keyword), isChecked);
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
                MouseFilter = MouseFilterEnum.Ignore
            };
            currentRow.AddThemeConstantOverride("separation", 8);
            MegaLabel currentLabel = CreateLabel(
                currentTitle,
                20,
                StsColors.cream);
            currentLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            currentRow.AddChild(currentLabel);

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
            _rightControls.AddChild(CreateSpacer(8f));
            return;
        }

        IReadOnlyList<LoadoutDropdownOption> options = models
            .Select(model => new LoadoutDropdownOption(model.Id.ToString(), GetAttachmentTitle(model)))
            .ToList();

        if (options.Count == 0)
        {
            MegaLabel emptyLabel = CreateLabel(LocMan.Loc("CARD_MOD_NO_VALID_ATTACHMENTS", "No valid attachments available"), 18, StsColors.cream);
            emptyLabel.CustomMinimumSize = new Vector2(0f, 38f);
            _rightControls.AddChild(emptyLabel);
            _rightControls.AddChild(CreateSpacer(8f));
            return;
        }

        string addId = options.FirstOrDefault().Id ?? NoneOptionId;

        NLoadoutDropdown dropdown = new()
        {
            CustomMinimumSize = new Vector2(0f, 52f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            DropdownWidth = 420f
        };
        dropdown.SetItems(LocMan.Loc("ADD", "Add"), options, addId);
        dropdown.SelectedItemChanged += id => addId = id;
        _rightControls.AddChild(dropdown);

        NLoadoutActionButton addButton = CreateActionButton($"add_{label}", LocMan.Loc("ADD", "Add"));
        addButton.CustomMinimumSize = new Vector2(180f, 42f);
        addButton.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        ConnectActionButton(addButton, () =>
        {
            if (addId == NoneOptionId)
                return;

            setSpec(new CardAttachmentSpec { ModelId = addId, Amount = 1 });
            ApplyWorkingState();
            RebuildControls();
        });
        _rightControls.AddChild(addButton);
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

        CardModificationStateService.SavePermanent(_item.Model.Id, _workingState);
        CardModificationStateService.ResetTemporary(_item);
        _temporaryState = new CardModificationState();
        _workingState = CardModificationStateService.GetEffectiveState(_item);
        CardModificationStateService.ApplyEffectiveStateToOwnedCard(_item);
        _parentRefresh?.Invoke();
        RebuildControls();
        RefreshPreview(forceReload: true);
    }

    private void ResetTemporary()
    {
        if (_item is null)
            return;

        CardModificationStateService.ResetTemporary(_item);
        _temporaryState = new CardModificationState();
        _workingState = CardModificationStateService.GetEffectiveState(_item);
        CardModificationStateService.ApplyEffectiveStateToOwnedCard(_item);
        _parentRefresh?.Invoke();
        RebuildControls();
        RefreshPreview(forceReload: true);
    }

    private void ResetPermanent()
    {
        if (_item is null)
            return;

        CardModificationStateService.ResetPermanent(_item.Model.Id);
        _workingState = CardModificationStateService.GetEffectiveState(_item);
        _temporaryState = CardModificationStateService.GetTemporaryState(_item);
        CardModificationStateService.ApplyEffectiveStateToOwnedCard(_item);
        _parentRefresh?.Invoke();
        RebuildControls();
        RefreshPreview(forceReload: true);
    }

    private void ApplyWorkingState()
    {
        if (_item is null)
            return;

        CardModificationStateService.SaveTemporary(_item, _temporaryState);
        CardModificationStateService.ApplyStateToCard(_item.Model, _workingState);
        _parentRefresh?.Invoke();
        RefreshPreview(forceReload: true);
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
        string currentText = target == TextEditTarget.Name
            ? _workingState.CustomTitle ?? _item?.Model.Title ?? string.Empty
            : _workingState.CustomDescription ?? _item?.Model.GetDescriptionForPile(PileType.None) ?? string.Empty;

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

        if (_previewCard is null || !GodotObject.IsInstanceValid(_previewCard))
        {
            ClearChildren(_previewHost);
            _previewCard = NCard.Create(_item.Model);
            if (_previewCard is null)
                return;

            _previewHost.AddChild(_previewCard);
        }
        else
        {
            ReassignPreviewCardModel(_previewCard, _item.Model, forceReload);
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
        if (_hoverTipHost is null || _hoverTipControls is null || _item is null)
            return;

        ClearChildren(_hoverTipControls);
        LayoutHoverTipHost();

        IReadOnlyList<IHoverTip> tips;
        try
        {
            tips = IHoverTip.RemoveDupes(_item.Model.HoverTips)
                .Where(tip => tip is not null)
                .ToList();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: could not render hover tips for '{_item.Model.Id}'. {exception.Message}");
            _hoverTipHost.Visible = false;
            return;
        }

        if (tips.Count == 0)
        {
            _hoverTipHost.Visible = false;
            _hoverTipHost.MouseFilter = MouseFilterEnum.Ignore;
            return;
        }

        _hoverTipHost.Visible = true;
        _hoverTipHost.MouseFilter = MouseFilterEnum.Stop;
        _hoverTipControls.AddChild(CreateSectionLabel(LocMan.Loc("CARD_MOD_HOVER_TIPS", "Hover Tips")));
        foreach (IHoverTip tip in tips)
            _hoverTipControls.AddChild(CreateHoverTipView(tip));
    }

    private void ClearHoverTips()
    {
        if (_hoverTipControls is not null && GodotObject.IsInstanceValid(_hoverTipControls))
            ClearChildren(_hoverTipControls);

        if (_hoverTipHost is not null && GodotObject.IsInstanceValid(_hoverTipHost))
        {
            _hoverTipHost.Visible = false;
            _hoverTipHost.MouseFilter = MouseFilterEnum.Ignore;
        }
    }

    private void LayoutHoverTipHost()
    {
        if (_hoverTipHost is null)
            return;

        Vector2 viewport = GetViewportRect().Size;
        if (viewport == Vector2.Zero)
            return;

        float rightPanelLeft = viewport.X - 474f;
        float cardGapRight = (viewport.X * 0.5f) + 270f;
        float width = Mathf.Clamp(rightPanelLeft - cardGapRight - 30f, 280f, 340f);
        float left = rightPanelLeft - width - 26f;
        _hoverTipHost.SetAnchorsPreset(LayoutPreset.TopLeft);
        _hoverTipHost.Position = new Vector2(Mathf.Max(cardGapRight + 18f, left), 112f);
        _hoverTipHost.Size = new Vector2(width, MathF.Max(260f, viewport.Y - 332f));
        _hoverTipHost.CustomMinimumSize = _hoverTipHost.Size;
    }

    private Control CreateHoverTipView(IHoverTip tip)
    {
        return tip switch
        {
            HoverTip hoverTip => CreateTextHoverTipView(hoverTip),
            CardHoverTip cardHoverTip => CreateCardHoverTipView(cardHoverTip.Card),
            _ => CreateFallbackHoverTipView(tip)
        };
    }

    private static Control CreateTextHoverTipView(HoverTip hoverTip)
    {
        PanelContainer panel = CreateHoverTipPanel(hoverTip.IsDebuff);
        VBoxContainer content = CreateHoverTipContent(panel);

        if (!string.IsNullOrWhiteSpace(hoverTip.Title))
            content.AddChild(CreateLabel(hoverTip.Title, 21, StsColors.gold));

        HBoxContainer body = new()
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        body.AddThemeConstantOverride("separation", 8);

        if (hoverTip.Icon is not null)
        {
            TextureRect icon = new()
            {
                Texture = hoverTip.Icon,
                CustomMinimumSize = new Vector2(42f, 42f),
                ExpandMode = TextureRect.ExpandModeEnum.FitWidthProportional,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = MouseFilterEnum.Ignore
            };
            body.AddChild(icon);
        }

        MegaRichTextLabel description = new()
        {
            Text = hoverTip.Description ?? string.Empty,
            FitContent = true,
            CustomMinimumSize = new Vector2(0f, 34f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        description.AddThemeFontOverride("normal_font", CommonHelpers.LoadGameFont());
        description.AddThemeFontSizeOverride("normal_font_size", 18);
        description.AddThemeColorOverride("default_color", StsColors.cream);
        body.AddChild(description);
        content.AddChild(body);

        return panel;
    }

    private static Control CreateCardHoverTipView(CardModel cardModel)
    {
        PanelContainer panel = CreateHoverTipPanel(isDebuff: false);
        VBoxContainer content = CreateHoverTipContent(panel);

        NCard? card = NCard.Create(cardModel);
        if (card is null)
        {
            content.AddChild(CreateLabel(cardModel.Id.ToString(), 18, StsColors.cream));
            return panel;
        }

        const float scale = 0.52f;
        Control slot = new()
        {
            CustomMinimumSize = NCard.defaultSize * scale,
            MouseFilter = MouseFilterEnum.Ignore,
            ClipContents = false
        };
        card.Scale = Vector2.One * scale;
        card.MouseFilter = MouseFilterEnum.Ignore;
        slot.AddChild(card);
        content.AddChild(slot);
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(card))
                card.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
        }).CallDeferred();
        return panel;
    }

    private static Control CreateFallbackHoverTipView(IHoverTip tip)
    {
        PanelContainer panel = CreateHoverTipPanel(tip.IsDebuff);
        VBoxContainer content = CreateHoverTipContent(panel);
        string text = tip.CanonicalModel?.Id.ToString() ?? tip.Id;
        content.AddChild(CreateLabel(text, 18, StsColors.cream));
        return panel;
    }

    private static PanelContainer CreateHoverTipPanel(bool isDebuff)
    {
        PanelContainer panel = new()
        {
            CustomMinimumSize = new Vector2(300f, 0f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        StyleBoxFlat style = new()
        {
            BgColor = isDebuff
                ? new Color(0.21f, 0.09f, 0.11f, 0.93f)
                : new Color(0.06f, 0.12f, 0.15f, 0.93f),
            BorderColor = StsColors.quarterTransparentWhite,
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6
        };
        panel.AddThemeStyleboxOverride("panel", style);
        return panel;
    }

    private static VBoxContainer CreateHoverTipContent(PanelContainer panel)
    {
        MarginContainer margin = new()
        {
            MouseFilter = MouseFilterEnum.Ignore
        };
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddThemeConstantOverride("margin_top", 8);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_bottom", 8);
        panel.AddChild(margin);

        VBoxContainer content = new()
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        content.AddThemeConstantOverride("separation", 5);
        margin.AddChild(content);
        return content;
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

    private static bool CanApplyAttachment(AbstractModel model, CardModel card)
    {
        try
        {
            return model switch
            {
                EnchantmentModel enchantment => enchantment.CanEnchant(card),
                AfflictionModel affliction => affliction.CanAfflict(card),
                _ => true
            };
        }
        catch (Exception exception)
        {
            GD.PushWarning($"CardModification: skipped attachment '{model.Id}' for '{card.Id}'. {exception.Message}");
            return false;
        }
    }

    private static bool MatchesModelId(AbstractModel model, string id)
    {
        return string.Equals(model.Id.ToString(), id, StringComparison.Ordinal)
               || string.Equals(model.Id.Entry, id, StringComparison.OrdinalIgnoreCase);
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
}
