#nullable enable

namespace Loadout.UI.Screens.Controls;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Loadout.Keywords;
using Loadout.PanelItems;
using Loadout.Services.CardModification;
using Loadout.UI.Managers;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;

public partial class NLoadoutKeywordEntryEditor : VBoxContainer
{
    private LoadoutCardKeywordEntry _entry = new();
    private Action<LoadoutCardKeywordEntry> _changed = _ => { };
    private bool _showAmount;
    private bool _extrasOnly;
    private int _revision;

    public void Init(LoadoutCardKeywordEntry entry, Action<LoadoutCardKeywordEntry> changed,
        bool showAmount = true, bool extrasOnly = false)
    {
        _entry = entry.Clone();
        _changed = changed;
        _showAmount = showAmount;
        _extrasOnly = extrasOnly;
        if (IsNodeReady())
            Rebuild();
    }

    public override void _Ready()
    {
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 4);
        Rebuild();
    }

    private void Rebuild()
    {
        _revision++;
        foreach (Node child in GetChildren())
        {
            RemoveChild(child);
            child.QueueFree();
        }
        if (_extrasOnly)
        {
            AddExtraKeywordControls(this, _entry, 0);
            AddEnchantmentControls(this, _entry);
        }
        else
            AddEntryControls(this, _entry, _showAmount);
    }

    private void Commit()
    {
        if (!IsInsideTree())
            return;
        _changed(_entry.Clone());
    }

    private void AddEntryControls(VBoxContainer container, LoadoutCardKeywordEntry entry, bool showAmount = true, int depth = 0)
    {
        if (entry.IsCardGrant)
        {
            if (showAmount && entry.KeywordKey != LoadoutKeywords.AddKeywordToAllCardsKey
                && entry.KeywordKey != LoadoutKeywords.AddEnchantmentToAllCardsKey)
                AddNumber(container, LocMan.Loc("CARD_MOD_KEYWORD_TARGET_AMOUNT", "Target Cards"), entry.Amount, 0,
                    value => { entry.Amount = value; Commit(); });
            NLoadoutPileTypeStepper pile = new();
            pile.Init(entry.Pile, value => { entry.Pile = value; Commit(); }, combatOnly: true);
            container.AddChild(CreateRow(LocMan.Loc("CARD_MOD_KEYWORD_SOURCE_PILE", "Card Pile"), pile));
            if (entry.IsKeywordGrant)
            {
                entry.GrantedKeyword ??= LoadoutKeywordPayload.Create(CardKeyword.Retain);
                AddPayloadControls(container, entry.GrantedKeyword, replacement => entry.GrantedKeyword = replacement, depth);
            }
            else
                AddEnchantmentControls(container, entry);
            return;
        }

        if (entry.IsRandom)
        {
            NLoadoutCardFilterStepper pool = new();
            pool.InitPool(entry.PoolId, value => { entry.PoolId = value; Commit(); });
            container.AddChild(CreateRow(LocMan.Loc("CARD_MOD_RANDOM_CARD_POOL", "Card Pool"), pool));
            NLoadoutCardFilterStepper rarity = new();
            rarity.InitRarity(entry.Rarity, value => { entry.Rarity = value; Commit(); });
            container.AddChild(CreateRow(LocMan.Loc("FILTER_GROUP_RARITY", "Rarity"), rarity));
            NLoadoutCardFilterStepper upgraded = new();
            upgraded.InitUpgraded(entry.Upgraded, value => { entry.Upgraded = value; Commit(); });
            container.AddChild(CreateRow(LocMan.Loc("CARD_MOD_RANDOM_CARD_UPGRADED", "Upgraded"), upgraded));
        }
        else
        {
            NLoadoutCardSelector selector = new();
            selector.Init(entry.CardId, entry.Upgraded);
            selector.SelectRequested += () =>
            {
                int revision = _revision;
                if (!CardPrinter.TryOpenKeywordCardPicker((selected, upgraded) =>
                    {
                        if (!GodotObject.IsInstanceValid(this) || !IsInsideTree() || revision != _revision)
                            return;
                        entry.CardId = selected.Id.ToString();
                        entry.Upgraded = upgraded;
                        selector.Init(entry.CardId, entry.Upgraded);
                        Commit();
                    }, out string error))
                    GD.PushWarning(error);
            };
            container.AddChild(CreateRow(LocMan.Loc("CARD_MOD_ADD_CARD_CARD", "Card"), selector));
        }
        AddNumber(container, LocMan.Loc("CARD_MOD_ADD_CARD_AMOUNT", "Amount"), entry.Amount, 0,
            value => { entry.Amount = value; Commit(); });
        NLoadoutPileTypeStepper destination = new();
        destination.Init(entry.Pile, value => { entry.Pile = value; Commit(); });
        container.AddChild(CreateRow(LocMan.Loc("CARD_MOD_ADD_CARD_PILE", "Destination Pile"), destination));
        NLoadoutCardCostStepper cost = new();
        cost.Init(entry.FreeToPlay, value => { entry.FreeToPlay = value; Commit(); });
        container.AddChild(CreateRow(LocMan.Loc("CARD_MOD_CARD_FREE", "Free to Play"), cost));
        AddExtraKeywordControls(container, entry, depth);
        AddEnchantmentControls(container, entry);
    }

    private void AddExtraKeywordControls(VBoxContainer container, LoadoutCardKeywordEntry entry, int depth) =>
        AddPayloadControls(container, entry.GrantedKeyword, replacement => entry.GrantedKeyword = replacement, depth, allowNone: true);

    private void AddEnchantmentControls(VBoxContainer container, LoadoutCardKeywordEntry entry)
    {
        NLoadoutEnchantmentStepper enchantment = new();
        VBoxContainer amountHost = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        void RefreshAmount()
        {
            foreach (Node child in amountHost.GetChildren())
            {
                amountHost.RemoveChild(child);
                child.QueueFree();
            }
            if (entry.GrantedEnchantment is { IsEmpty: false, Clear: false } spec)
                AddNumber(amountHost, LocMan.Loc("CARD_MOD_ENCHANTMENT_AMOUNT", "Enchantment Amount"),
                    spec.Amount, 1, value => { spec.Amount = value; Commit(); });
        }
        enchantment.Init(entry.GrantedEnchantment, selected =>
        {
            entry.GrantedEnchantment = selected;
            RefreshAmount();
            Commit();
        }, allowNone: !entry.IsEnchantmentGrant);
        container.AddChild(CreateRow(entry.IsEnchantmentGrant
            ? LocMan.Loc("CARD_MOD_GRANTED_ENCHANTMENT", "Enchantment to Add")
            : LocMan.Loc("CARD_MOD_CARD_EXTRA_ENCHANTMENT", "Extra Enchantment"), enchantment));
        container.AddChild(amountHost);
        RefreshAmount();
    }

    private void AddPayloadControls(VBoxContainer container, LoadoutKeywordPayload? payload,
        Action<LoadoutKeywordPayload?> replace, int depth, bool allowNone = false)
    {
        NLoadoutDropdown keyword = new()
        {
            CustomMinimumSize = new Vector2(218f, 40f),
            ButtonHeight = 40f,
            LabelMinFontSize = 15,
            LabelMaxFontSize = 20,
            ExpandToAvailableWidth = false
        };
        void RefreshSelection() => keyword.SetItems(string.Empty,
            [new LoadoutDropdownOption(payload?.KeywordKey ?? string.Empty, payload?.GetLabel() ?? LocMan.Loc("NONE", "None"))],
            payload?.KeywordKey ?? string.Empty);
        RefreshSelection();
        container.AddChild(CreateRow(allowNone
            ? LocMan.Loc("CARD_MOD_CARD_EXTRA_KEYWORD", "Extra Keyword")
            : LocMan.Loc("CARD_MOD_GRANTED_KEYWORD", "Keyword to Add"), keyword));
        VBoxContainer pickerHost = new() { Visible = false };
        VBoxContainer valuesHost = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        container.AddChild(pickerHost);
        container.AddChild(valuesHost);
        if (payload is not null)
            AddPayloadValueControls(valuesHost, payload, depth);

        NCardKeywordEditor? picker = null;
        keyword.InlineExpansionRequested += () =>
        {
            bool expanded = !pickerHost.Visible;
            if (expanded && picker is null)
            {
                LoadoutKeywords.TryResolve(payload?.KeywordKey, out CardKeyword current);
                picker = new NCardKeywordEditor();
                picker.InitSingleSelection(current, selected =>
                {
                    pickerHost.Hide();
                    keyword.CloseLoadoutDropdown(restoreFocus: false);
                    string selectedKey = selected == CardKeyword.None ? string.Empty : LoadoutKeywords.GetStorageKey(selected);
                    if (selectedKey == (payload?.KeywordKey ?? string.Empty))
                        return;
                    _revision++;
                    payload = selected == CardKeyword.None ? null : LoadoutKeywordPayload.Create(selected);
                    replace(payload);
                    RefreshSelection();
                    foreach (Node child in valuesHost.GetChildren())
                    {
                        valuesHost.RemoveChild(child);
                        child.QueueFree();
                    }
                    if (payload is not null)
                        AddPayloadValueControls(valuesHost, payload, depth);
                    Commit();
                }, contentWidth: depth == 0 ? 426f : 414f, allowNone: allowNone);
                pickerHost.AddChild(picker);
            }
            pickerHost.Visible = expanded;
        };
    }

    private void AddPayloadValueControls(VBoxContainer container, LoadoutKeywordPayload payload, int depth)
    {
        LoadoutKeywordRegistry.TryGet(payload.KeywordKey, out LoadoutKeywordModel model);
        var definitions = model?.DynamicVars.Where(variable => variable.EditorVisible).ToArray() ?? [];
        if (definitions.Length == 0 && payload.Power is null && payload.Card is null
            && (payload.Commands?.Count ?? 0) == 0)
            return;
        VBoxContainer values = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        values.AddThemeConstantOverride("separation", 4);
        foreach (var definition in definitions)
        {
            string name = definition.Name;
            NLoadoutDecimalStepper value = new();
            value.Init((double)payload.Values.GetValueOrDefault(name, definition.DefaultValue), definition.Minimum, definition.Maximum);
            value.ValueChanged += changed => { payload.Values[name] = (decimal)changed; Commit(); };
            values.AddChild(CreateRow(LocMan.Loc(definition.LabelLocKey, name), value));
        }
        if (payload.Power is { } power)
        {
            NLoadoutPowerSelector selector = new();
            selector.Init(power.PowerId);
            selector.SelectRequested += () =>
            {
                int revision = _revision;
                if (!PowerGiver.TryOpenKeywordPowerPicker(selected =>
                    {
                        if (!GodotObject.IsInstanceValid(this) || !IsInsideTree() || revision != _revision)
                            return;
                        power.PowerId = selected.Id.ToString();
                        selector.Init(power.PowerId);
                        Commit();
                    }, out string error))
                    GD.PushWarning(error);
            };
            values.AddChild(CreateRow(LocMan.Loc("CARD_MOD_SELECT_POWER", "Select Power"), selector));
            AddNumber(values, LocMan.Loc("CARD_MOD_POWER_AMOUNT", "Power Amount"), power.Amount, int.MinValue,
                value => { power.Amount = value; Commit(); });
            if (model is LoadoutPowerKeywordModel { SupportsPowerDescription: true })
            {
                NLoadoutDropdownStepper description = new();
                description.Init(power.UseDescription, enabled => { power.UseDescription = enabled; Commit(); });
                values.AddChild(CreateRow(
                    LocMan.Loc("CARD_MOD_POWER_USE_DESCRIPTION", "Use Description"), description));
            }
        }
        if (payload.Card is { } nested)
            AddEntryControls(values, nested, depth: depth + 1);
        if (payload.Commands is { } commands)
        {
            for (int index = 0; index < commands.Count; index++)
            {
                int commandIndex = index;
                NLoadoutTextStepper command = new();
                command.Init(commands[index], value => { commands[commandIndex] = value; Commit(); });
                values.AddChild(CreateRow(LocMan.Loc("CARD_MOD_EXECUTE_COMMAND", "Console Command"), command));
            }
        }
        MarginContainer inset = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        inset.AddThemeConstantOverride("margin_left", depth == 0 ? 12 : 0);
        MegaLabel heading = new()
        {
            Text = payload.GetLabel(),
            CustomMinimumSize = new Vector2(0f, 36f),
            AutoSizeEnabled = true,
            MinFontSize = 14,
            MaxFontSize = 22,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        heading.AddThemeFontOverride("font", CommonHelpers.LoadGameFont());
        heading.AddThemeFontSizeOverride("font_size", 22);
        heading.AddThemeColorOverride("font_color", StsColors.gold);
        values.AddChild(heading);
        values.MoveChild(heading, 0);
        inset.AddChild(values);
        container.AddChild(inset);
    }

    private static void AddNumber(VBoxContainer container, string label, int value, int minimum, Action<int> changed)
    {
        NLoadoutNumberStepper stepper = new();
        stepper.Init(value, minimum);
        stepper.ValueChanged += changed;
        container.AddChild(CreateRow(label, stepper));
    }

    private static Control CreateRow(string label, Control input)
    {
        HBoxContainer row = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 8);
        MegaLabel text = new()
        {
            Text = label,
            CustomMinimumSize = new Vector2(150f, 44f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            AutoSizeEnabled = true,
            MinFontSize = 13,
            MaxFontSize = 21,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        text.AddThemeFontOverride("font", CommonHelpers.LoadGameFont());
        text.AddThemeColorOverride("font_color", StsColors.cream);
        row.AddChild(text);
        input.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        row.AddChild(input);
        return row;
    }
}
