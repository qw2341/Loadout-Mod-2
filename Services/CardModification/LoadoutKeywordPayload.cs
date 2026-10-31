#nullable enable

namespace Loadout.Services.CardModification;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Loadout.Keywords;
using Loadout.PanelItems;
using Loadout.Patches.Cards.CardModification;
using Loadout.UI.Managers;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Commands;

public sealed class LoadoutKeywordPayload
{
    [JsonPropertyName("k")]
    public string KeywordKey { get; set; } = nameof(CardKeyword.Retain);

    [JsonPropertyName("v")]
    public SortedDictionary<string, decimal> Values { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("power")]
    public LoadoutPowerKeywordEntry? Power { get; set; }

    [JsonPropertyName("card")]
    public LoadoutCardKeywordEntry? Card { get; set; }

    [JsonPropertyName("commands")]
    public List<string>? Commands { get; set; }

    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(KeywordKey)
        || string.Equals(KeywordKey, nameof(CardKeyword.None), StringComparison.OrdinalIgnoreCase);

    public LoadoutKeywordPayload Clone() => new()
    {
        KeywordKey = KeywordKey,
        Values = new SortedDictionary<string, decimal>(Values, StringComparer.Ordinal),
        Power = Power?.Clone(),
        Card = Card?.Clone(),
        Commands = Commands?.ToList()
    };

    public static LoadoutKeywordPayload Create(CardKeyword keyword)
    {
        LoadoutKeywordPayload payload = new() { KeywordKey = LoadoutKeywords.GetStorageKey(keyword) };
        if (!LoadoutKeywordRegistry.TryGet(keyword, out LoadoutKeywordModel model))
            return payload;
        foreach (var variable in model.DynamicVars.Where(variable => variable.EditorVisible))
            payload.Values[variable.Name] = variable.DefaultValue;
        if (model is LoadoutPowerKeywordModel)
            payload.Power = new LoadoutPowerKeywordEntry
            {
                KeywordKey = model.StorageKey,
                PowerId = LoadoutPowerKeywordState.GetDefaultStrengthPowerId()
            };
        if (model is LoadoutCardKeywordModel cardModel)
            payload.Card = cardModel.CreateDefaultEntry();
        if (keyword == LoadoutKeywords.AltExecute)
            payload.Commands = [string.Empty];
        return payload;
    }

    public void Normalize()
    {
        KeywordKey = (KeywordKey ?? string.Empty).Trim();
        Values ??= new(StringComparer.Ordinal);
        if (!LoadoutKeywords.TryResolve(KeywordKey, out CardKeyword keyword)
            || !LoadoutKeywordRegistry.TryGet(keyword, out LoadoutKeywordModel model))
            return;
        KeywordKey = model.StorageKey;
        var definitions = model.DynamicVars.Where(variable => variable.EditorVisible).ToDictionary(variable => variable.Name);
        foreach (string name in Values.Keys.ToArray())
        {
            if (!definitions.TryGetValue(name, out var definition))
                Values.Remove(name);
            else
                Values[name] = Math.Clamp(Values[name], definition.Minimum, definition.Maximum);
        }
        foreach (var definition in definitions.Values)
            Values.TryAdd(definition.Name, definition.DefaultValue);
        if (model is LoadoutPowerKeywordModel)
        {
            Power ??= Create(keyword).Power;
            Power!.KeywordKey = KeywordKey;
            Power.PowerId = (Power.PowerId ?? string.Empty).Trim();
        }
        else
            Power = null;
        if (model is LoadoutCardKeywordModel cardModel)
        {
            Card ??= cardModel.CreateDefaultEntry();
            Card.KeywordKey = KeywordKey;
            LoadoutCardKeywordEntry.NormalizeList([Card]);
        }
        else
            Card = null;
        Commands = keyword == LoadoutKeywords.AltExecute
            ? AltExecuteKeyword.NormalizeCommands(Commands ?? [string.Empty])
            : null;
    }

    public static bool Equivalent(LoadoutKeywordPayload? left, LoadoutKeywordPayload? right) =>
        ReferenceEquals(left, right)
        || left is not null && right is not null
        && string.Equals(JsonSerializer.Serialize(left), JsonSerializer.Serialize(right), StringComparison.Ordinal);

    public void ApplyTo(CardModel target)
    {
        if (target.IsCanonical || IsEmpty || !LoadoutKeywords.TryResolve(KeywordKey, out CardKeyword keyword))
            return;
        CardModificationSpec state = CardModificationRuntime.GetEffectiveSpec(target);
        state.KeywordOverrides[KeywordKey] = true;
        foreach ((string name, decimal value) in Values)
            state.DynamicVars[name] = value;
        if (Power is { } power)
            (state.PowerKeywordEntries ??= []).Add(power.Clone());
        if (Card is { } nested)
            (state.CardKeywordEntries ??= []).Add(nested.Clone());
        if (Commands is { } commands)
        {
            List<string> effective = AltExecuteKeyword.GetCommands(target).ToList();
            effective.AddRange(commands);
            state.ExecuteCommands = effective;
            if (target.CurrentUpgradeLevel > 0)
                state.UpgradeModification.ExecuteCommands = effective.ToList();
        }
        CardModificationFields.Set(target, state);
        LoadoutPowerKeywordState.SetExplicitState(target, state);
        LoadoutCardKeywordState.SetExplicitState(target, state);
        AltExecuteKeyword.SetExplicitState(target, state);
        bool newlyAdded = !LoadoutKeywords.Has(target, keyword);
        LoadoutKeywordRegistry.SynchronizeDynamicVars(target, state.KeywordOverrides);
        foreach ((string name, decimal value) in Values)
        {
            if (target.DynamicVars.TryGetValue(name, out var variable))
                variable.BaseValue = value;
        }
        if (LoadoutKeywordRegistry.TryGet(keyword, out LoadoutKeywordModel model))
        {
            if (newlyAdded)
                model.OnAdded(target);
            if ((model.ModificationFlags & LoadoutCardModificationFlags.OverrideXCost) != 0)
                XCostKeywordMechanics.SynchronizeEnergyCost(target, state.KeywordOverrides, state.EnergyCost);
        }
        CardCmd.ApplyKeyword(target, keyword);
    }

    public string GetLabel() => LoadoutKeywords.TryResolve(KeywordKey, out CardKeyword keyword)
        ? NCardKeywordEditor.GetKeywordLabel(keyword)
        : KeywordKey;

    public string GetDescription()
    {
        List<string> settings = [];
        foreach ((string name, decimal value) in Values)
        {
            string label = LoadoutKeywordRegistry.TryGetDynamicVar(name, out var definition)
                ? LocMan.Loc(definition.LabelLocKey, name) : name;
            settings.Add($"{label}: {value.ToString(CultureInfo.InvariantCulture)}");
        }
        if (Power is { } power)
            settings.Add($"{(LoadoutPowerKeywordState.TryResolvePower(power.PowerId, out var model) ? CommonHelpers.FormatPowerTitle(model) : power.PowerId)}: {power.Amount}");
        if (Card is { IsKeywordGrant: true } grant)
            settings.Add(AsInlineClause(LoadoutKeywordKeywordModel.FormatEntry(grant)));
        else if (Card is { } card)
        {
            string cards = LoadoutCardKeywordState.FormatEntry(card, card.Amount.ToString(CultureInfo.InvariantCulture));
            settings.Add(AsInlineClause(LocMan.Loc("CARD_MOD_CARD_KEYWORD_SENTENCE", "Add {0} into your [gold]{1}[/gold].",
                cards, LoadoutCardKeywordState.GetPileLabel(card.Pile))));
            string cost = LoadoutCardKeywordState.GetFreeToPlayDescription(card.EffectiveFreeToPlay, card.Amount == 1);
            if (!string.IsNullOrEmpty(cost))
                settings.Add(AsInlineClause(cost));
        }
        if (Commands is { Count: > 0 })
            settings.AddRange(Commands);
        string separator = LocMan.Loc("CARD_MOD_KEYWORD_SETTINGS_SEPARATOR", ", ");
        return $"[gold]{GetLabel()}[/gold]" + (settings.Count == 0 ? string.Empty : $" ({string.Join(separator, settings)})");
    }

    private static string AsInlineClause(string sentence) => sentence.TrimEnd().TrimEnd('.', '。');
}
