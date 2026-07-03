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
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Runs;

public partial class NCardModificationScreen : Control
{
    private const string ScenePath = "res://UI/Screens/CardModificationScreen.tscn";
    private const string NoneOptionId = "__none__";
    private const float SidePanelWidth = 438f;
    private const float ActionButtonWidth = 174f;
    private const float ActionButtonHeight = 42f;

    private LoadoutOwnedItem<CardModel>? _item;
    private List<LoadoutOwnedItem<CardModel>> _items = [];
    private int _itemIndex;
    private Action? _parentRefresh;
    private CardModificationState _workingState = new();
    private CardModificationState _temporaryState = new();
    private VBoxContainer? _leftControls;
    private VBoxContainer? _rightControls;
    private HBoxContainer? _actionControls;
    private Control? _backButtonMount;
    private Control? _previewHost;
    private NButton? _leftArrow;
    private NButton? _rightArrow;
    private NCard? _previewCard;
    private MegaLabel? _titleLabel;
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
            RefreshPreview();
        }
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
        _actionControls = GetNodeOrNull<HBoxContainer>("%ActionRow");
        _previewHost = GetNodeOrNull<Control>("%PreviewCardHost");
        _leftArrow = GetNodeOrNull<NButton>("%LeftArrow");
        _rightArrow = GetNodeOrNull<NButton>("%RightArrow");

        EnsureBackButton();
        BindSceneSignals();
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
        if (_backButtonMount is null || _backButtonMount.GetNodeOrNull<NBackButton>("BackButton") is not null)
            return;

        NBackButton backButton = NLoadoutBackButtonFactory.Create();
        backButton.Name = "BackButton";
        PositionBackButton(backButton);
        backButton.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ =>
        {
            NLoadoutBackButtonFactory.ResetVisualState(backButton);
            NLoadoutPanelRoot.CloseTopLoadoutScreen();
        }));
        _backButtonMount.AddChild(backButton);
    }

    private static void PositionBackButton(Control backButton)
    {
        backButton.SetAnchorsPreset(LayoutPreset.TopLeft);
        backButton.OffsetLeft = 0f;
        backButton.OffsetTop = 0f;
        backButton.OffsetRight = 200f;
        backButton.OffsetBottom = 110f;
        backButton.PivotOffset = new Vector2(20f, 40f);
    }

    private void LoadItem(LoadoutOwnedItem<CardModel> item)
    {
        _item = item;
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
        _leftArrow.Visible = hasPrevious;
        _rightArrow.Visible = hasNext;
        _leftArrow.MouseFilter = hasPrevious ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
        _rightArrow.MouseFilter = hasNext ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
    }

    private void RebuildControls()
    {
        if (_leftControls is null || _rightControls is null || _actionControls is null || _item is null)
            return;

        ClearChildren(_leftControls);
        ClearChildren(_rightControls);
        ClearChildren(_actionControls);

        _titleLabel = CreateLabel(CardPrinter.FormatCardTitle(_item.Model), 32, StsColors.gold);
        _leftControls.AddChild(_titleLabel);
        _leftControls.AddChild(CreateLabel(_item.Model.Id.ToString(), 18, StsColors.cream));
        _leftControls.AddChild(CreateSpacer(6f));

        AddNumericControls();
        AddDropdownControls();
        AddAttachmentControls();
        AddKeywordControls();

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

        IReadOnlyList<CardPoolModel> pools = ModelDb.AllCardPools
            .Where(pool => !CommonHelpers.IsInternalPool(pool) || CommonHelpers.SamePool(pool, card.Pool))
            .OrderBy(CommonHelpers.GetPoolLabel, StringComparer.OrdinalIgnoreCase)
            .ToList();
        AddDropdownRow(_leftControls,
            LocMan.Loc("CARD_MOD_CLASS", "Class"),
            pools.Select(pool => new LoadoutDropdownOption(pool.Id.ToString(), CommonHelpers.GetPoolLabel(pool))),
            _workingState.PoolId ?? card.Pool.Id.ToString(),
            selected =>
            {
                _workingState.PoolId = selected;
                _temporaryState.PoolId = selected;
                ApplyWorkingState();
            });

        AddDropdownRow(_leftControls,
            LocMan.Loc("CARD_MOD_TYPE", "Type"),
            Enum.GetValues<CardType>()
                .Where(type => type != CardType.None)
                .Select(type => new LoadoutDropdownOption(type.ToString(), CardPrinter.GetCardTypeLabel(type))),
            _workingState.Type ?? card.Type.ToString(),
            selected =>
            {
                _workingState.Type = selected;
                _temporaryState.Type = selected;
                ApplyWorkingState();
            });

        AddDropdownRow(_leftControls,
            LocMan.Loc("CARD_MOD_RARITY", "Rarity"),
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

        _rightControls.AddChild(CreateSectionLabel(LocMan.Loc("CARD_MOD_KEYWORDS", "Keywords")));

        GridContainer grid = new()
        {
            Columns = 2,
            CustomMinimumSize = new Vector2(420f, 0f),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            MouseFilter = MouseFilterEnum.Ignore
        };
        grid.AddThemeConstantOverride("h_separation", 8);
        grid.AddThemeConstantOverride("v_separation", 0);
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
                CustomMinimumSize = new Vector2(202f, 46f),
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
            HBoxContainer currentRow = new()
            {
                CustomMinimumSize = new Vector2(0f, 44f),
                MouseFilter = MouseFilterEnum.Ignore
            };
            currentRow.AddThemeConstantOverride("separation", 8);
            MegaLabel currentLabel = CreateLabel(
                current is null ? selectedId : GetAttachmentTitle(current),
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
        string addId = options.FirstOrDefault().Id ?? NoneOptionId;

        NLoadoutDropdown dropdown = new()
        {
            CustomMinimumSize = new Vector2(0f, 52f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        dropdown.SetItems(LocMan.Loc("ADD", "Add"), options, addId);
        dropdown.SelectedItemChanged += id => addId = id;
        _rightControls.AddChild(dropdown);

        NLoadoutActionButton addButton = CreateActionButton($"add_{label}", LocMan.Loc("ADD", "Add"));
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
            SizeFlagsHorizontal = SizeFlags.ExpandFill
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
        RefreshPreview();
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
        RefreshPreview();
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
        RefreshPreview();
    }

    private void ApplyWorkingState()
    {
        if (_item is null)
            return;

        CardModificationStateService.SaveTemporary(_item, _temporaryState);
        CardModificationStateService.ApplyStateToCard(_item.Model, _workingState);
        _parentRefresh?.Invoke();
        RefreshPreview();
    }

    private void RefreshPreview()
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
            _previewCard.Model = _item.Model;
        }

        NCard card = _previewCard;
        if (card.GetParent() != _previewHost)
            _previewHost.AddChild(card);

        card.SetAnchorsPreset(LayoutPreset.Center);
        card.Position = Vector2.Zero;
        card.Scale = Vector2.One * GetPreviewScale();
        card.MouseFilter = MouseFilterEnum.Ignore;
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(card))
                card.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
        }).CallDeferred();
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
