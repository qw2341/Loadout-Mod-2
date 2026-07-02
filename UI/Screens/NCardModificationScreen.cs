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
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

public partial class NCardModificationScreen : Control
{
    private const string NoneOptionId = "__none__";
    private const float SidebarWidth = 430f;
    private const float PreviewScale = 1.48f;

    private LoadoutOwnedItem<CardModel>? _item;
    private Action? _parentRefresh;
    private CardModificationState _workingState = new();
    private VBoxContainer? _controls;
    private Control? _previewHost;
    private MegaLabel? _titleLabel;

    public void Init(LoadoutOwnedItem<CardModel> item, Action? parentRefresh = null)
    {
        _item = item;
        _parentRefresh = parentRefresh;
        _workingState = CardModificationStateService.GetEffectiveState(item);

        if (IsNodeReady())
            RebuildScreen();
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        RebuildScreen();
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
            Color = new Color(0.018f, 0.027f, 0.031f, 0.94f),
            MouseFilter = MouseFilterEnum.Stop
        };
        background.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(background);

        HBoxContainer layout = new()
        {
            Name = "CardModificationLayout",
            MouseFilter = MouseFilterEnum.Ignore
        };
        layout.SetAnchorsPreset(LayoutPreset.FullRect);
        layout.OffsetLeft = 56f;
        layout.OffsetTop = 42f;
        layout.OffsetRight = -56f;
        layout.OffsetBottom = -46f;
        layout.AddThemeConstantOverride("separation", 34);
        AddChild(layout);

        ScrollContainer sidebarScroller = new()
        {
            Name = "EditorSidebar",
            CustomMinimumSize = new Vector2(SidebarWidth, 0f),
            SizeFlagsVertical = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Stop
        };
        layout.AddChild(sidebarScroller);

        MarginContainer sidebarMargin = new()
        {
            Name = "SidebarMargin",
            MouseFilter = MouseFilterEnum.Ignore
        };
        sidebarMargin.AddThemeConstantOverride("margin_left", 8);
        sidebarMargin.AddThemeConstantOverride("margin_right", 8);
        sidebarMargin.AddThemeConstantOverride("margin_top", 8);
        sidebarMargin.AddThemeConstantOverride("margin_bottom", 8);
        sidebarScroller.AddChild(sidebarMargin);

        _controls = new VBoxContainer
        {
            Name = "Controls",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _controls.AddThemeConstantOverride("separation", 10);
        sidebarMargin.AddChild(_controls);

        _previewHost = new CenterContainer
        {
            Name = "PreviewHost",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        layout.AddChild(_previewHost);
    }

    private void RebuildControls()
    {
        if (_controls is null || _item is null)
            return;

        foreach (Node child in _controls.GetChildren())
        {
            _controls.RemoveChild(child);
            child.QueueFree();
        }

        NLoadoutActionButton backButton = CreateActionButton("back", LocMan.Loc("BACK", "Back"));
        ConnectActionButton(backButton, NLoadoutPanelRoot.CloseTopLoadoutScreen);
        _controls.AddChild(backButton);

        _titleLabel = CreateLabel(CardPrinter.FormatCardTitle(_item.Model), 30, StsColors.gold);
        _controls.AddChild(_titleLabel);
        _controls.AddChild(CreateLabel(_item.Model.Id.ToString(), 18, StsColors.cream));
        _controls.AddChild(CreateSpacer(8f));

        AddNumericControls();
        AddDropdownControls();
        AddAttachmentControls();
        AddKeywordControls();

        _controls.AddChild(CreateSpacer(8f));
        NLoadoutActionButton temporaryButton = CreateActionButton("save_temporary", LocMan.Loc("SAVE_TEMPORARY", "Save Temporary"), CommonHelpers.LoadActionButtonIcon("CardModifier.png"));
        ConnectActionButton(temporaryButton, SaveTemporary);
        _controls.AddChild(temporaryButton);

        NLoadoutActionButton permanentButton = CreateActionButton("save_permanent", LocMan.Loc("SAVE_PERMANENT", "Save Permanent"), CommonHelpers.LoadActionButtonIcon("CardPrinter.png"));
        ConnectActionButton(permanentButton, SavePermanent);
        _controls.AddChild(permanentButton);

        NLoadoutActionButton resetTemporaryButton = CreateActionButton("reset_temporary", LocMan.Loc("RESET_TEMPORARY", "Reset Temporary"));
        ConnectActionButton(resetTemporaryButton, ResetTemporary);
        _controls.AddChild(resetTemporaryButton);

        NLoadoutActionButton resetPermanentButton = CreateActionButton("reset_permanent", LocMan.Loc("RESET_PERMANENT", "Reset Permanent"));
        ConnectActionButton(resetPermanentButton, ResetPermanent);
        _controls.AddChild(resetPermanentButton);
    }

    private void AddNumericControls()
    {
        if (_item is null || _controls is null)
            return;

        CardModel card = _item.Model;
        _controls.AddChild(CreateSectionLabel(LocMan.Loc("CARD_MOD_NUMERIC_STATS", "Numeric Stats")));

        AddStepperRow(
            LocMan.Loc("CARD_MOD_ENERGY_COST", "Energy Cost"),
            _workingState.EnergyCost ?? (card.EnergyCost.CostsX ? 0 : card.EnergyCost.GetWithModifiers(CostModifiers.Local)),
            -1,
            99,
            value =>
            {
                _workingState.EnergyCost = value;
                ApplyWorkingState();
            });

        AddStepperRow(
            LocMan.Loc("CARD_MOD_REPLAY_COUNT", "Replay Count"),
            _workingState.BaseReplayCount ?? card.BaseReplayCount,
            0,
            99,
            value =>
            {
                _workingState.BaseReplayCount = value;
                ApplyWorkingState();
            });

        AddStepperRow(
            LocMan.Loc("CARD_MOD_STAR_COST", "Star Cost"),
            _workingState.BaseStarCost ?? card.BaseStarCost,
            -1,
            99,
            value =>
            {
                _workingState.BaseStarCost = value;
                ApplyWorkingState();
            });

        foreach ((string name, var dynamicVar) in card.DynamicVars.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            int current = _workingState.DynamicVars.TryGetValue(name, out decimal saved)
                ? Decimal.ToInt32(saved)
                : Decimal.ToInt32(dynamicVar.BaseValue);

            AddStepperRow(name, current, -999, 999, value =>
            {
                _workingState.DynamicVars[name] = value;
                ApplyWorkingState();
            });
        }
    }

    private void AddDropdownControls()
    {
        if (_item is null || _controls is null)
            return;

        CardModel card = _item.Model;
        _controls.AddChild(CreateSectionLabel(LocMan.Loc("CARD_MOD_CARD_FIELDS", "Card Fields")));

        IReadOnlyList<CardPoolModel> pools = ModelDb.AllCardPools
            .Where(pool => !CommonHelpers.IsInternalPool(pool) || CommonHelpers.SamePool(pool, card.Pool))
            .OrderBy(CommonHelpers.GetPoolLabel, StringComparer.OrdinalIgnoreCase)
            .ToList();
        AddDropdownRow(
            LocMan.Loc("CARD_MOD_CLASS", "Class"),
            pools.Select(pool => new LoadoutDropdownOption(pool.Id.ToString(), CommonHelpers.GetPoolLabel(pool))),
            _workingState.PoolId ?? card.Pool.Id.ToString(),
            selected =>
            {
                _workingState.PoolId = selected;
                ApplyWorkingState();
            });

        AddDropdownRow(
            LocMan.Loc("CARD_MOD_TYPE", "Type"),
            Enum.GetValues<CardType>()
                .Where(type => type != CardType.None)
                .Select(type => new LoadoutDropdownOption(type.ToString(), CardPrinter.GetCardTypeLabel(type))),
            _workingState.Type ?? card.Type.ToString(),
            selected =>
            {
                _workingState.Type = selected;
                ApplyWorkingState();
            });

        AddDropdownRow(
            LocMan.Loc("CARD_MOD_RARITY", "Rarity"),
            Enum.GetValues<CardRarity>()
                .Where(rarity => rarity != CardRarity.None)
                .OrderBy(CardPrinter.GetCardRaritySortValue)
                .Select(rarity => new LoadoutDropdownOption(rarity.ToString(), CardPrinter.GetCardRarityLabel(rarity))),
            _workingState.Rarity ?? card.Rarity.ToString(),
            selected =>
            {
                _workingState.Rarity = selected;
                ApplyWorkingState();
            });
    }

    private void AddAttachmentControls()
    {
        if (_item is null || _controls is null)
            return;

        _controls.AddChild(CreateSectionLabel(LocMan.Loc("CARD_MOD_ATTACHMENTS", "Attachments")));

        AddAttachmentEditor(
            LocMan.Loc("CARD_MOD_ENCHANTMENT", "Enchantment"),
            ModelDb.DebugEnchantments
                .Where(model => !IsInternalAttachment(model))
                .OrderBy(GetAttachmentTitle, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _workingState.Enchantment,
            _item.Model.Enchantment,
            spec => _workingState.Enchantment = spec);

        AddAttachmentEditor(
            LocMan.Loc("CARD_MOD_AFFLICTION", "Affliction"),
            ModelDb.DebugAfflictions
                .Where(model => !IsInternalAttachment(model))
                .OrderBy(GetAttachmentTitle, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _workingState.Affliction,
            _item.Model.Affliction,
            spec => _workingState.Affliction = spec);
    }

    private void AddKeywordControls()
    {
        if (_item is null || _controls is null)
            return;

        _controls.AddChild(CreateSectionLabel(LocMan.Loc("CARD_MOD_KEYWORDS", "Keywords")));

        GridContainer grid = new()
        {
            Columns = 2,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 0);
        _controls.AddChild(grid);

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
                CustomMinimumSize = new Vector2(192f, 46f),
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            toggle.Init($"keyword_{key}", CardPrinter.GetCardKeywordLabel(keyword), isChecked);
            toggle.Toggled += changed =>
            {
                _workingState.KeywordOverrides[key] = changed.IsChecked;
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
        if (_controls is null)
            return;

        string selectedId = savedSpec?.Clear == true
            ? NoneOptionId
            : savedSpec?.ModelId ?? currentModel?.Id.ToString() ?? NoneOptionId;
        int amount = savedSpec?.Amount ?? GetAttachmentAmount(currentModel) ?? 1;

        List<LoadoutDropdownOption> options =
        [
            new LoadoutDropdownOption(NoneOptionId, LocMan.Loc("NONE", "None"))
        ];
        options.AddRange(models.Select(model => new LoadoutDropdownOption(model.Id.ToString(), GetAttachmentTitle(model))));

        NLoadoutDropdown dropdown = new()
        {
            CustomMinimumSize = new Vector2(0f, 52f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        dropdown.SetItems(label, options, selectedId);
        dropdown.SelectedItemChanged += id => selectedId = id;
        _controls.AddChild(dropdown);

        HBoxContainer row = new()
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        row.AddThemeConstantOverride("separation", 8);
        _controls.AddChild(row);

        NLoadoutNumberStepper amountStepper = new();
        amountStepper.Init(amount, 1, 999);
        amountStepper.ValueChanged += value => amount = value;
        row.AddChild(amountStepper);

        NLoadoutActionButton addButton = CreateActionButton($"add_{label}", LocMan.Loc("ADD", "Add"));
        ConnectActionButton(addButton, () =>
        {
            setSpec(selectedId == NoneOptionId
                ? new CardAttachmentSpec { Clear = true }
                : new CardAttachmentSpec { ModelId = selectedId, Amount = amount });
            ApplyWorkingState();
            RebuildControls();
        });
        row.AddChild(addButton);

        NLoadoutActionButton clearButton = CreateActionButton($"clear_{label}", LocMan.Loc("CLEAR", "Clear"));
        ConnectActionButton(clearButton, () =>
        {
            setSpec(new CardAttachmentSpec { Clear = true });
            ApplyWorkingState();
            RebuildControls();
        });
        row.AddChild(clearButton);
    }

    private void AddStepperRow(string label, int value, int min, int max, Action<int> onChanged)
    {
        NLoadoutNumberStepper stepper = new();
        stepper.Init(value, min, max);
        stepper.ValueChanged += onChanged;
        _controls?.AddChild(CreateRow(label, stepper));
    }

    private void AddDropdownRow(
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
        _controls?.AddChild(dropdown);
    }

    private Control CreateRow(string label, Control input)
    {
        HBoxContainer row = new()
        {
            CustomMinimumSize = new Vector2(0f, 44f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        row.AddThemeConstantOverride("separation", 8);

        MegaLabel text = CreateLabel(label, 21, StsColors.cream);
        text.CustomMinimumSize = new Vector2(190f, 44f);
        text.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(text);

        input.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        row.AddChild(input);
        return row;
    }

    private void SaveTemporary()
    {
        if (_item is null)
            return;

        CardModificationStateService.SaveTemporary(_item, _workingState);
        CardModificationStateService.ApplyEffectiveStateToOwnedCard(_item);
        _parentRefresh?.Invoke();
        RefreshPreview();
    }

    private void SavePermanent()
    {
        if (_item is null)
            return;

        CardModificationStateService.SavePermanent(_item.Model.Id, _workingState);
        CardModificationStateService.ApplyEffectiveStateToOwnedCard(_item);
        _parentRefresh?.Invoke();
        RefreshPreview();
    }

    private void ResetTemporary()
    {
        if (_item is null)
            return;

        CardModificationStateService.ResetTemporary(_item);
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
        CardModificationStateService.ApplyEffectiveStateToOwnedCard(_item);
        _parentRefresh?.Invoke();
        RebuildControls();
        RefreshPreview();
    }

    private void ApplyWorkingState()
    {
        if (_item is null)
            return;

        CardModificationStateService.ApplyStateToCard(_item.Model, _workingState);
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        if (_previewHost is null || _item is null)
            return;

        foreach (Node child in _previewHost.GetChildren())
        {
            _previewHost.RemoveChild(child);
            child.QueueFree();
        }

        NCard? card = NCard.Create(_item.Model);
        if (card is null)
            return;

        NGridCardHolder? holder = NGridCardHolder.Create(card);
        if (holder is null)
        {
            _previewHost.AddChild(card);
            return;
        }

        holder.Scale = Vector2.One * PreviewScale;
        holder.CustomMinimumSize = NCard.defaultSize * PreviewScale;
        holder.MouseFilter = MouseFilterEnum.Pass;
        _previewHost.AddChild(holder);
        Callable.From(() => holder.CardNode?.UpdateVisuals(PileType.None, CardPreviewMode.Normal)).CallDeferred();
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
        label.CustomMinimumSize = new Vector2(0f, 46f);
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

    private static bool IsInternalAttachment(AbstractModel model)
    {
        string typeName = model.GetType().Name;
        return typeName.StartsWith("Mock", StringComparison.Ordinal)
               || typeName.StartsWith("Deprecated", StringComparison.Ordinal);
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

    private static int? GetAttachmentAmount(AbstractModel? model)
    {
        return model switch
        {
            EnchantmentModel enchantment => enchantment.Amount,
            AfflictionModel affliction => affliction.Amount,
            _ => null
        };
    }
}
