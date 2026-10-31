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
    private int _revision;

    public void Init(LoadoutCardKeywordEntry entry, Action<LoadoutCardKeywordEntry> changed, bool showAmount = true)
    {
        _entry = entry.Clone();
        _changed = changed;
        _showAmount = showAmount;
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
        if (entry.IsKeywordGrant)
        {
            if (showAmount && entry.KeywordKey != LoadoutKeywords.AddKeywordToAllCardsKey)
                AddNumber(container, LocMan.Loc("CARD_MOD_KEYWORD_TARGET_AMOUNT", "Target Cards"), entry.Amount, 0,
                    value => { entry.Amount = value; Commit(); });
            NLoadoutPileTypeStepper pile = new();
            pile.Init(entry.Pile, value => { entry.Pile = value; Commit(); }, combatOnly: true);
            container.AddChild(CreateRow(LocMan.Loc("CARD_MOD_KEYWORD_SOURCE_PILE", "Card Pile"), pile));
            entry.GrantedKeyword ??= LoadoutKeywordPayload.Create(CardKeyword.Retain);
            AddPayloadControls(container, entry.GrantedKeyword, replacement => entry.GrantedKeyword = replacement, depth);
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
    }

    private void AddPayloadControls(VBoxContainer container, LoadoutKeywordPayload payload, Action<LoadoutKeywordPayload> replace, int depth)
    {
        Button keyword = new()
        {
            Text = payload.GetLabel(),
            ToggleMode = true,
            CustomMinimumSize = new Vector2(218f, 42f),
            Alignment = HorizontalAlignment.Left,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseFilter = MouseFilterEnum.Stop
        };
        keyword.AddThemeFontOverride("font", CommonHelpers.LoadGameFont());
        keyword.AddThemeFontSizeOverride("font_size", 18);
        foreach (string state in new[] { "normal", "hover", "pressed", "hover_pressed" })
        {
            keyword.AddThemeStyleboxOverride(state, new StyleBoxFlat
            {
                BgColor = state == "normal"
                    ? new Color(0.172549f, 0.262745f, 0.309804f)
                    : new Color(0.215686f, 0.411765f, 0.501961f),
                ContentMarginLeft = 12f,
                ContentMarginRight = 44f,
                ContentMarginTop = 4f,
                ContentMarginBottom = 4f
            });
        }
        foreach (string color in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_hover_pressed_color", "font_focus_color" })
            keyword.AddThemeColorOverride(color, StsColors.gold);
        TextureRect arrow = new()
        {
            Name = "Arrow",
            Texture = GD.Load<Texture2D>("res://images/atlases/ui_atlas.sprites/settings_tiny_left_arrow.tres"),
            Rotation = -Mathf.Pi * 0.5f,
            PivotOffset = new Vector2(13f, 13f),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        arrow.SetAnchorsPreset(LayoutPreset.CenterRight);
        arrow.OffsetLeft = -36f;
        arrow.OffsetRight = -10f;
        arrow.OffsetTop = -13f;
        arrow.OffsetBottom = 13f;
        keyword.AddChild(arrow);
        container.AddChild(CreateRow(LocMan.Loc("CARD_MOD_GRANTED_KEYWORD", "Keyword to Add"), keyword));
        VBoxContainer pickerHost = new() { Visible = false };
        VBoxContainer valuesHost = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        container.AddChild(pickerHost);
        container.AddChild(valuesHost);
        AddPayloadValueControls(valuesHost, payload, depth);

        NCardKeywordEditor? picker = null;
        keyword.Toggled += expanded =>
        {
            if (expanded && picker is null)
            {
                LoadoutKeywords.TryResolve(payload.KeywordKey, out CardKeyword current);
                picker = new NCardKeywordEditor();
                picker.InitSingleSelection(current, selected =>
                {
                    keyword.ButtonPressed = false;
                    keyword.GrabFocus();
                    if (LoadoutKeywords.GetStorageKey(selected) == payload.KeywordKey)
                        return;
                    _revision++;
                    payload = LoadoutKeywordPayload.Create(selected);
                    replace(payload);
                    keyword.Text = payload.GetLabel();
                    foreach (Node child in valuesHost.GetChildren())
                    {
                        valuesHost.RemoveChild(child);
                        child.QueueFree();
                    }
                    AddPayloadValueControls(valuesHost, payload, depth);
                    Commit();
                }, contentWidth: depth == 0 ? 426f : 414f);
                pickerHost.AddChild(picker);
            }
            pickerHost.Visible = expanded;
            arrow.Rotation = (expanded ? 1f : -1f) * Mathf.Pi * 0.5f;
        };
    }

    private void AddPayloadValueControls(VBoxContainer container, LoadoutKeywordPayload payload, int depth)
    {
        LoadoutKeywordRegistry.TryGet(payload.KeywordKey, out LoadoutKeywordModel model);
        var definitions = model?.DynamicVars.Where(variable => variable.EditorVisible).ToArray() ?? [];
        VBoxContainer values = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        values.AddThemeConstantOverride("separation", 4);
        if (definitions.Length > 0)
        {
            NLoadoutDropdownStepper variableSelector = new();
            VBoxContainer valueHost = new();
            void ShowValue(string name)
            {
                foreach (Node child in valueHost.GetChildren())
                {
                    valueHost.RemoveChild(child);
                    child.QueueFree();
                }
                var definition = definitions.First(variable => variable.Name == name);
                NLoadoutDecimalStepper value = new();
                value.Init((double)payload.Values.GetValueOrDefault(name, definition.DefaultValue), definition.Minimum, definition.Maximum);
                value.ValueChanged += changed => { payload.Values[name] = (decimal)changed; Commit(); };
                valueHost.AddChild(CreateRow(LocMan.Loc(definition.LabelLocKey, name), value));
            }
            variableSelector.Init(definitions.Select(definition => new LoadoutDropdownOption(
                definition.Name, LocMan.Loc(definition.LabelLocKey, definition.Name))), definitions[0].Name, ShowValue);
            values.AddChild(CreateRow(LocMan.Loc("CARD_MOD_KEYWORD_VALUES", "Keyword Values"), variableSelector));
            values.AddChild(valueHost);
            ShowValue(definitions[0].Name);
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
        if (values.GetChildCount() == 0)
        {
            NLoadoutDropdownStepper noValues = new();
            noValues.Init([new LoadoutDropdownOption("none", LocMan.Loc("CARD_MOD_KEYWORD_NO_VALUES", "No editable values"))], "none", _ => { });
            values.AddChild(CreateRow(LocMan.Loc("CARD_MOD_KEYWORD_VALUES", "Keyword Values"), noValues));
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
