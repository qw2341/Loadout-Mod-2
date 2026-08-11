#nullable enable

namespace Loadout.Services.CustomRuns.Registry;

using System;
using System.Collections.Generic;
using System.Linq;
using Loadout.Services.CustomRuns.Models;

public enum RuleComponentKind
{
    Trigger,
    Condition,
    Action,
    Target
}

public enum RuleParameterKind
{
    Integer,
    Boolean,
    Enum,
    Text,
    Card,
    Relic,
    Potion,
    Power,
    Role,
    PlayerTarget,
    FilteredPool,
    Variable,
    NumericSource,
    CardFilter
}

public sealed record RuleParameterDescriptor(
    string Key,
    string DisplayName,
    RuleParameterKind Kind,
    bool Required = true)
{
    public int Minimum { get; init; } = -999999;
    public int Maximum { get; init; } = 999999;
    public IReadOnlyList<RuleParameterOption> Options { get; init; } = [];
}

public sealed record RuleParameterOption(string Id, string DisplayName);

public sealed class RuleComponentDescriptor
{
    public required string StableId { get; init; }
    public required string DisplayName { get; init; }
    public required string Category { get; init; }
    public required RuleComponentKind Kind { get; init; }
    public IReadOnlyList<RuleParameterDescriptor> Parameters { get; init; } = [];
    public Func<RuleComponentSpec, IReadOnlyList<string>>? Validate { get; init; }
    public object? CompilationHandler { get; init; }
    public object? RuntimeHandler { get; init; }
}

public static class CustomRunRegistry
{
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, RuleComponentDescriptor> Triggers = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, RuleComponentDescriptor> Conditions = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, RuleComponentDescriptor> Actions = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, RuleComponentDescriptor> Targets = new(StringComparer.Ordinal);
    private static bool _builtInsRegistered;

    public static void EnsureBuiltInsRegistered()
    {
        lock (SyncRoot)
        {
            if (_builtInsRegistered)
                return;
            _builtInsRegistered = true;

            RegisterBuiltIn(Triggers, RuleComponentKind.Trigger, "Loadout2:RunStart", "Run Start", "Run");
            RegisterBuiltIn(Triggers, RuleComponentKind.Trigger, "Loadout2:CombatStart", "Combat Start", "Combat");
            RegisterBuiltIn(Triggers, RuleComponentKind.Trigger, "Loadout2:TurnStart", "Turn Start", "Combat");
            RegisterBuiltIn(Triggers, RuleComponentKind.Trigger, "Loadout2:TurnEnd", "Turn End", "Combat");
            RegisterBuiltIn(Triggers, RuleComponentKind.Trigger, "Loadout2:CardPlayed", "Card Played", "Cards");
            RegisterBuiltIn(Triggers, RuleComponentKind.Trigger, "Loadout2:PlayerTakesDamage", "Player Takes Damage", "Players");

            RuleParameterDescriptor playerTarget = new("target", "Target", RuleParameterKind.PlayerTarget);
            RuleParameterDescriptor amount = new("amount", "Amount", RuleParameterKind.NumericSource);
            RuleParameterDescriptor card = new("cardId", "Card", RuleParameterKind.Card);
            RuleParameterDescriptor relic = new("relicId", "Relic", RuleParameterKind.Relic);
            RuleParameterDescriptor potion = new("potionId", "Potion", RuleParameterKind.Potion);
            RuleParameterDescriptor power = new("powerId", "Power", RuleParameterKind.Power);
            RuleParameterDescriptor variable = new("variableId", "Variable", RuleParameterKind.Variable);
            RuleParameterDescriptor numericOperator = new("operator", "Comparison", RuleParameterKind.Enum)
            {
                Options =
                [
                    new("Equal", "="),
                    new("NotEqual", "!="),
                    new("Less", "<"),
                    new("LessOrEqual", "<="),
                    new("Greater", ">"),
                    new("GreaterOrEqual", ">=")
                ]
            };

            RegisterBuiltIn(Conditions, RuleComponentKind.Condition, "Loadout2:Always", "Always", "General");
            RegisterBuiltIn(
                Conditions,
                RuleComponentKind.Condition,
                "Loadout2:TriggeringPlayerIs",
                "Triggering Player Is",
                "Players",
                playerTarget);
            RegisterBuiltIn(
                Conditions,
                RuleComponentKind.Condition,
                "Loadout2:PlayerHasRole",
                "Player Has Role",
                "Players",
                new RuleParameterDescriptor("roleId", "Role", RuleParameterKind.Role),
                playerTarget);
            RegisterBuiltIn(
                Conditions,
                RuleComponentKind.Condition,
                "Loadout2:CardMatches",
                "Card Matches",
                "Cards",
                new RuleParameterDescriptor("filter", "Cards", RuleParameterKind.CardFilter));
            RegisterBuiltIn(
                Conditions,
                RuleComponentKind.Condition,
                "Loadout2:PlayerHasCard",
                "Player Has Card",
                "Cards",
                card,
                playerTarget);
            RegisterBuiltIn(
                Conditions,
                RuleComponentKind.Condition,
                "Loadout2:PlayerHasRelic",
                "Player Has Relic",
                "Relics",
                relic,
                playerTarget);
            RegisterBuiltIn(
                Conditions,
                RuleComponentKind.Condition,
                "Loadout2:PlayerHasPower",
                "Player Has Power",
                "Powers",
                power,
                playerTarget);
            RegisterBuiltIn(
                Conditions,
                RuleComponentKind.Condition,
                "Loadout2:NumericComparison",
                "Numeric Comparison",
                "Values",
                new RuleParameterDescriptor("left", "Left Value", RuleParameterKind.NumericSource),
                numericOperator,
                new RuleParameterDescriptor("right", "Right Value", RuleParameterKind.NumericSource));
            RegisterBuiltIn(
                Conditions,
                RuleComponentKind.Condition,
                "Loadout2:VariableComparison",
                "Variable Comparison",
                "Variables",
                variable,
                numericOperator,
                new RuleParameterDescriptor("value", "Value", RuleParameterKind.NumericSource));
            RegisterBuiltIn(
                Conditions,
                RuleComponentKind.Condition,
                "Loadout2:Chance",
                "Chance",
                "Values",
                new RuleParameterDescriptor("percent", "Percent", RuleParameterKind.Integer)
                {
                    Minimum = 0,
                    Maximum = 100
                });

            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:GainPower", "Gain Power", "Powers", power, amount, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:GainEnergy", "Gain Energy", "Player", amount, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:DrawCards", "Draw Cards", "Cards", amount, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:GainGold", "Gain Gold", "Player", amount, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:Heal", "Heal", "Player", amount, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:LoseHp", "Lose HP", "Player", amount, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:ObtainCard", "Obtain Card", "Cards", card, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:ObtainRelic", "Obtain Relic", "Relics", relic, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:ObtainPotion", "Obtain Potion", "Potions", potion, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:AddCardToHand", "Add Card To Hand", "Cards", card, amount, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:AddCardToDrawPile", "Add Card To Draw Pile", "Cards", card, amount, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:AddCardToDiscardPile", "Add Card To Discard Pile", "Cards", card, amount, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:SetVariable", "Set Variable", "Variables", variable, amount, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:AddToVariable", "Add To Variable", "Variables", variable, amount, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:SubtractFromVariable", "Subtract From Variable", "Variables", variable, amount, playerTarget);

            RegisterBuiltIn(Targets, RuleComponentKind.Target, "Loadout2:TriggeringPlayer", "Triggering Player", "Players");
            RegisterBuiltIn(Targets, RuleComponentKind.Target, "Loadout2:Host", "Host", "Players");
            RegisterBuiltIn(Targets, RuleComponentKind.Target, "Loadout2:AllPlayers", "All Players", "Players");
            RegisterBuiltIn(Targets, RuleComponentKind.Target, "Loadout2:AllOtherPlayers", "All Other Players", "Players");
            RegisterBuiltIn(Targets, RuleComponentKind.Target, "Loadout2:RandomPlayer", "Random Player", "Players");
            RegisterBuiltIn(
                Targets,
                RuleComponentKind.Target,
                "Loadout2:SpecificPlayerSlot",
                "Specific Player Slot",
                "Players",
                new RuleParameterDescriptor("slot", "Player Slot", RuleParameterKind.Integer)
                {
                    Minimum = 1,
                    Maximum = 8
                });
            RegisterBuiltIn(
                Targets,
                RuleComponentKind.Target,
                "Loadout2:PlayersWithRole",
                "Players With Role",
                "Players",
                new RuleParameterDescriptor("roleId", "Role", RuleParameterKind.Role));
            RegisterBuiltIn(
                Targets,
                RuleComponentKind.Target,
                "Loadout2:RandomPlayerWithRole",
                "Random Player With Role",
                "Players",
                new RuleParameterDescriptor("roleId", "Role", RuleParameterKind.Role));
        }
    }

    public static void RegisterTrigger(RuleComponentDescriptor descriptor) => Register(Triggers, descriptor, RuleComponentKind.Trigger);
    public static void RegisterCondition(RuleComponentDescriptor descriptor) => Register(Conditions, descriptor, RuleComponentKind.Condition);
    public static void RegisterAction(RuleComponentDescriptor descriptor) => Register(Actions, descriptor, RuleComponentKind.Action);
    public static void RegisterTarget(RuleComponentDescriptor descriptor) => Register(Targets, descriptor, RuleComponentKind.Target);

    public static bool TryGetTrigger(string id, out RuleComponentDescriptor descriptor) => TryGet(Triggers, id, out descriptor);
    public static bool TryGetCondition(string id, out RuleComponentDescriptor descriptor) => TryGet(Conditions, id, out descriptor);
    public static bool TryGetAction(string id, out RuleComponentDescriptor descriptor) => TryGet(Actions, id, out descriptor);
    public static bool TryGetTarget(string id, out RuleComponentDescriptor descriptor) => TryGet(Targets, id, out descriptor);

    public static IReadOnlyList<RuleComponentDescriptor> GetDescriptors(RuleComponentKind kind)
    {
        EnsureBuiltInsRegistered();
        lock (SyncRoot)
        {
            return GetDictionary(kind).Values
                .OrderBy(descriptor => descriptor.Category, StringComparer.Ordinal)
                .ThenBy(descriptor => descriptor.DisplayName, StringComparer.Ordinal)
                .ToList();
        }
    }

    private static void Register(
        Dictionary<string, RuleComponentDescriptor> dictionary,
        RuleComponentDescriptor descriptor,
        RuleComponentKind expectedKind)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Kind != expectedKind)
            throw new ArgumentException($"Component '{descriptor.StableId}' has kind {descriptor.Kind}, expected {expectedKind}.");
        if (string.IsNullOrWhiteSpace(descriptor.StableId))
            throw new ArgumentException("Custom Run component IDs cannot be empty.");

        lock (SyncRoot)
            dictionary[descriptor.StableId] = descriptor;
    }

    private static bool TryGet(
        Dictionary<string, RuleComponentDescriptor> dictionary,
        string id,
        out RuleComponentDescriptor descriptor)
    {
        EnsureBuiltInsRegistered();
        lock (SyncRoot)
            return dictionary.TryGetValue(id ?? string.Empty, out descriptor!);
    }

    private static Dictionary<string, RuleComponentDescriptor> GetDictionary(RuleComponentKind kind)
    {
        return kind switch
        {
            RuleComponentKind.Trigger => Triggers,
            RuleComponentKind.Condition => Conditions,
            RuleComponentKind.Action => Actions,
            RuleComponentKind.Target => Targets,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static void RegisterBuiltIn(
        Dictionary<string, RuleComponentDescriptor> dictionary,
        RuleComponentKind kind,
        string id,
        string displayName,
        string category,
        params RuleParameterDescriptor[] parameters)
    {
        dictionary[id] = new RuleComponentDescriptor
        {
            StableId = id,
            DisplayName = displayName,
            Category = category,
            Kind = kind,
            Parameters = parameters
        };
    }
}
