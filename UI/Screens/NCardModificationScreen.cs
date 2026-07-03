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
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Runs;

public partial class NCardModificationScreen : Control
{
    private const string NoneOptionId = "__none__";
    private const float SidePanelWidth = 438f;
    private const float SidePanelTop = 92f;
    private const float SidePanelBottom = 150f;
    private const float SideMargin = 36f;
    private const float PreviewHorizontalMargin = 510f;

    private LoadoutOwnedItem<CardModel>? _item;
    private Action? _parentRefresh;
    private CardModificationState _workingState = new();
    private CardModificationState _temporaryState = new();
    private VBoxContainer? _leftControls;
    private VBoxContainer? _rightControls;
    private HBoxContainer? _actionControls;
    private CenterContainer? _previewHost;
    private MegaLabel? _titleLabel;

    public void Init(LoadoutOwnedItem<CardModel> item, Action? parentRefresh = null)
    {
        _item = item;
        _parentRefresh = parentRefresh;
        _workingState = CardModificationStateService.GetEffectiveState(item);
        _temporaryState = CardModificationStateService.GetTemporaryState(item);

        if (IsNodeReady())
            RebuildScreen();
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 120;
        RebuildScreen();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized)
            RefreshPreview();
    }

    private void RebuildScreen()
    {
        foreach (Node child in GetChildren())
        {
            RemoveChild(child);
            child.QueueFree();
        }

        if (_item is null)
            return;

        BuildLayout();
        RebuildControls();
        RefreshPreview();
    }

    private void BuildLayout()
    {
        ColorRect background = new()
        {
            Color = new Color(0f, 0f, 0f, 0.965f),
            MouseFilter = MouseFilterEnum.Stop
        };
        background.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(background);

        NBackButton backButton = NLoadoutBackButtonFactory.Create();
        backButton.Name = "BackButton";
        backButton.Connect(NClickableControl.SignalName.Released, Callable.From<NClickableControl>(_ =>
        {
            NLoadoutBackButtonFactory.ResetVisualState(backButton);
            NLoadoutPanelRoot.CloseTopLoadoutScreen();
        }));
        AddChild(backButton);

        _previewHost = new CenterContainer
        {
            Name = "PreviewHost",
            MouseFilter = MouseFilterEnum.Ignore
        };
        _previewHost.SetAnchorsPreset(LayoutPreset.FullRect);
        _previewHost.OffsetLeft = PreviewHorizontalMargin;
        _previewHost.OffsetRight = -PreviewHorizontalMargin;
        _previewHost.OffsetTop = 42f;
        _previewHost.OffsetBottom = -76f;
        AddChild(_previewHost);

        _leftControls = CreateSideControls("LeftEditor", leftSide: true);
        _rightControls = CreateSideControls("RightEditor", leftSide: false);
        _actionControls = CreateActionRow();
    }

    private VBoxContainer CreateSideControls(string name, bool leftSide)
    {
        ScrollContainer scroller = new()
        {
            Name = name,
            ClipContents = true,
            MouseFilter = MouseFilterEnum.Stop
        };
        scroller.SetAnchorsPreset(LayoutPreset.FullRect);
        scroller.AnchorLeft = leftSide ? 0f : 1f;
        scroller.AnchorRight = leftSide ? 0f : 1f;
        scroller.OffsetLeft = leftSide ? SideMargin : -SidePanelWidth - SideMargin;
        scroller.OffsetRight = leftSide ? SidePanelWidth + SideMargin : -SideMargin;
        scroller.OffsetTop = SidePanelTop;
        scroller.OffsetBottom = -SidePanelBottom;
        AddChild(scroller);

        MarginContainer margin = new()
        {
            Name = "Margin",
            MouseFilter = MouseFilterEnum.Ignore
        };
        margin.AddThemeConstantOverride("margin_left", 6);
        margin.AddThemeConstantOverride("margin_right", 6);
        margin.AddThemeConstantOverride("margin_top", 6);
        margin.AddThemeConstantOverride("margin_bottom", 6);
        scroller.AddChild(margin);

        VBoxContainer controls = new()
        {
            Name = "Controls",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        controls.AddThemeConstantOverride("separation", 8);
        margin.AddChild(controls);
        return controls;
    }

    private HBoxContainer CreateActionRow()
    {
        HBoxContainer row = new()
        {
            Name = "ActionRow",
            MouseFilter = MouseFilterEnum.Ignore
        };
        row.SetAnchorsPreset(LayoutPreset.FullRect);
        row.AnchorLeft = 1f;
        row.AnchorRight = 1f;
        row.OffsetLeft = -SidePanelWidth - SideMargin;
        row.OffsetRight = -SideMargin;
        row.OffsetTop = -130f;
        row.OffsetBottom = -78f;
        row.AddThemeConstantOverride("separation", 10);
        AddChild(row);
        return row;
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
        }

        NLoadoutActionButton resetTemporaryButton = CreateActionButton("reset_temporary", LocMan.Loc("RESET_TEMPORARY", "Reset Temporary"));
        ConnectActionButton(resetTemporaryButton, ResetTemporary);
        _actionControls.AddChild(resetTemporaryButton);

        if (CanSavePermanent())
        {
            NLoadoutActionButton resetPermanentButton = CreateActionButton("reset_permanent", LocMan.Loc("RESET_PERMANENT", "Reset Permanent"));
            ConnectActionButton(resetPermanentButton, ResetPermanent);
            _actionControls.AddChild(resetPermanentButton);
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
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
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
                SizeFlagsHorizontal = SizeFlags.ExpandFill
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

        ClearChildren(_previewHost);

        NCard? card = NCard.Create(_item.Model);
        if (card is null)
            return;

        NGridCardHolder? holder = NGridCardHolder.Create(card);
        if (holder is null)
        {
            _previewHost.AddChild(card);
            return;
        }

        float previewScale = GetPreviewScale();
        holder.Scale = Vector2.One * previewScale;
        holder.CustomMinimumSize = NCard.defaultSize * previewScale;
        holder.MouseFilter = MouseFilterEnum.Pass;
        _previewHost.AddChild(holder);
        Callable.From(() => holder.CardNode?.UpdateVisuals(PileType.None, CardPreviewMode.Normal)).CallDeferred();
    }

    private float GetPreviewScale()
    {
        Vector2 viewport = GetViewportRect().Size;
        float laneWidth = MathF.Max(280f, viewport.X - PreviewHorizontalMargin * 2f);
        float laneHeight = MathF.Max(420f, viewport.Y - 128f);
        float byHeight = laneHeight * 0.82f / NCard.defaultSize.Y;
        float byWidth = laneWidth * 0.9f / NCard.defaultSize.X;
        return Mathf.Clamp(MathF.Min(byHeight, byWidth), 1.35f, 2.05f);
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
