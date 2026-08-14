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
    Integer = 0,
    Boolean = 1,
    Enum = 2,
    Text = 3,
    Card = 4,
    Relic = 5,
    Potion = 6,
    Power = 7,
    Monster = 8,
    Role = 9,
    PlayerTarget = 10,
    FilteredPool = 11,
    Variable = 12,
    NumericSource = 13,
    ModelFilter = 14,
    Event = 15,
    NumberVariable = 16,
    BooleanVariable = 17
}

public sealed record RuleParameterDescriptor(
    string Key,
    string DisplayName,
    RuleParameterKind Kind,
    bool Required = true)
{
    public int Minimum { get; init; } = int.MinValue;
    public int Maximum { get; init; } = int.MaxValue;
    public int DefaultInteger { get; init; } = 1;
    public bool AllowDouble { get; init; }
    public NumericConstantKind DefaultConstantKind { get; init; } = NumericConstantKind.Integer;
    public double DefaultNumeric { get; init; } = 1d;
    public SelectionModelKind ModelKind { get; init; } = SelectionModelKind.Card;
    public string? VisibleWhenParameterKey { get; init; }
    public string? VisibleWhenParameterValue { get; init; }
    public IReadOnlyList<RuleParameterOption> Options { get; init; } = [];
}

public sealed record RuleParameterOption(string Id, string DisplayName);

public interface IRuleComponentCompileHandler
{
    RuleComponentSpec Compile(RuleComponentSpec component);
}

public interface IRuleComponentRuntimeHandler
{
    string StableId { get; }
}

public sealed class BuiltInRuleComponentHandler(string stableId) : IRuleComponentCompileHandler, IRuleComponentRuntimeHandler
{
    public string StableId { get; } = stableId;

    public RuleComponentSpec Compile(RuleComponentSpec component)
    {
        return component;
    }
}

public sealed class RuleComponentDescriptor
{
    public required string StableId { get; init; }
    public required string DisplayName { get; init; }
    public required string Category { get; init; }
    public required RuleComponentKind Kind { get; init; }
    public IReadOnlyList<RuleParameterDescriptor> Parameters { get; init; } = [];
    public IReadOnlySet<string> CompatibleTriggerIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public bool HiddenFromPicker { get; init; }
    public Func<RuleComponentSpec, IReadOnlyList<string>>? Validate { get; init; }
    public IRuleComponentCompileHandler? CompilationHandler { get; init; }
    public IRuleComponentRuntimeHandler? RuntimeHandler { get; init; }
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
            RegisterBuiltIn(Triggers, RuleComponentKind.Trigger, "Loadout2:RunEnd", "Run End", "Run");
            RegisterBuiltIn(Triggers, RuleComponentKind.Trigger, "Loadout2:CombatStart", "Combat Start", "Combat");
            RegisterBuiltIn(Triggers, RuleComponentKind.Trigger, "Loadout2:CombatEnd", "Combat End", "Combat");
            RegisterBuiltIn(Triggers, RuleComponentKind.Trigger, "Loadout2:TurnStart", "Turn Start", "Combat");
            RegisterBuiltIn(Triggers, RuleComponentKind.Trigger, "Loadout2:TurnEnd", "Turn End", "Combat");
            RegisterBuiltIn(Triggers, RuleComponentKind.Trigger, "Loadout2:CardPlayed", "Card Played", "Cards");
            RegisterBuiltIn(Triggers, RuleComponentKind.Trigger, "Loadout2:CardDrawn", "Card Drawn", "Cards");
            RegisterBuiltIn(Triggers, RuleComponentKind.Trigger, "Loadout2:CardDiscarded", "Card Discarded", "Cards");
            RegisterBuiltIn(Triggers, RuleComponentKind.Trigger, "Loadout2:CardExhausted", "Card Exhausted", "Cards");
            RegisterBuiltIn(Triggers, RuleComponentKind.Trigger, "Loadout2:CardGenerated", "Card Generated", "Cards");
            RegisterBuiltIn(Triggers, RuleComponentKind.Trigger, "Loadout2:CardObtained", "Card Obtained", "Cards");
            RegisterBuiltIn(Triggers, RuleComponentKind.Trigger, "Loadout2:PlayerTakesDamage", "Player Takes Damage", "Players");
            RegisterBuiltIn(Triggers, RuleComponentKind.Trigger, "Loadout2:PlayerGainsBlock", "Player Gains Block", "Players");
            RegisterBuiltIn(Triggers, RuleComponentKind.Trigger, "Loadout2:PowerReceived", "Power Received", "Powers");
            RegisterBuiltIn(Triggers, RuleComponentKind.Trigger, "Loadout2:PotionUsed", "Potion Used", "Potions");
            RegisterBuiltIn(Triggers, RuleComponentKind.Trigger, "Loadout2:PotionObtained", "Potion Obtained", "Potions");
            RegisterBuiltIn(Triggers, RuleComponentKind.Trigger, "Loadout2:RelicObtained", "Relic Obtained", "Relics");
            RegisterBuiltIn(Triggers, RuleComponentKind.Trigger, "Loadout2:MonsterSpawned", "Monster Spawned", "Monsters");
            RegisterBuiltIn(Triggers, RuleComponentKind.Trigger, "Loadout2:MonsterMoveStarted", "Monster Move Started", "Monsters");
            RegisterBuiltIn(Triggers, RuleComponentKind.Trigger, "Loadout2:EnergySpent", "Energy Spent", "Players");
            RegisterBuiltIn(Triggers, RuleComponentKind.Trigger, "Loadout2:RoomEntered", "Room Entered", "Events");
            RegisterBuiltIn(Triggers, RuleComponentKind.Trigger, "Loadout2:RoomCompleted", "Room Completed", "Events");
            RegisterBuiltIn(Triggers, RuleComponentKind.Trigger, "Loadout2:EventEntered", "Event Entered", "Events");
            RegisterBuiltIn(Triggers, RuleComponentKind.Trigger, "Loadout2:NonAncientEventEntered", "Non-Ancient Event Entered", "Events");
            RegisterBuiltIn(Triggers, RuleComponentKind.Trigger, "Loadout2:AncientEventEntered", "Ancient Event Entered", "Events");

            RuleParameterDescriptor playerTarget = new("target", "Target", RuleParameterKind.PlayerTarget);
            RuleParameterDescriptor amount = new("amount", "Amount", RuleParameterKind.NumericSource);
            RuleParameterDescriptor card = new("cardId", "Card", RuleParameterKind.Card);
            RuleParameterDescriptor relic = new("relicId", "Relic", RuleParameterKind.Relic);
            RuleParameterDescriptor potion = new("potionId", "Potion", RuleParameterKind.Potion);
            RuleParameterDescriptor power = new("powerId", "Power", RuleParameterKind.Power);
            RuleParameterDescriptor eventModel = new("eventId", "Event", RuleParameterKind.Event);
            RuleParameterDescriptor variable = new("variableId", "Variable", RuleParameterKind.Variable);
            RuleParameterDescriptor cardMatcher = new("matcher", "Card matcher", RuleParameterKind.ModelFilter)
            {
                ModelKind = SelectionModelKind.Card
            };
            RuleParameterDescriptor relicMatcher = new("matcher", "Relic matcher", RuleParameterKind.ModelFilter)
            {
                ModelKind = SelectionModelKind.Relic
            };
            RuleParameterDescriptor potionMatcher = new("matcher", "Potion matcher", RuleParameterKind.ModelFilter)
            {
                ModelKind = SelectionModelKind.Potion
            };
            RuleParameterDescriptor powerMatcher = new("matcher", "Power matcher", RuleParameterKind.ModelFilter)
            {
                ModelKind = SelectionModelKind.Power
            };
            RuleParameterDescriptor monsterMatcher = new("matcher", "Monster matcher", RuleParameterKind.ModelFilter)
            {
                ModelKind = SelectionModelKind.Monster
            };
            RuleParameterDescriptor eventMatcher = new("matcher", "Event matcher", RuleParameterKind.ModelFilter)
            {
                ModelKind = SelectionModelKind.Event
            };
            RuleParameterDescriptor selectionMode = new("selectionMode", "Selection", RuleParameterKind.Enum)
            {
                Options =
                [
                    new("Random", "Random matches"),
                    new("Choose", "Player chooses")
                ]
            };
            RuleParameterDescriptor count = new("count", "Number to select", RuleParameterKind.NumericSource)
            {
                Minimum = 0,
                Maximum = 50,
                VisibleWhenParameterKey = "selectionMode",
                VisibleWhenParameterValue = "Random"
            };
            RuleParameterDescriptor minimumSelectionCount = new("minimumCount", "Minimum number to select", RuleParameterKind.NumericSource)
            {
                Minimum = 0,
                Maximum = 50,
                VisibleWhenParameterKey = "selectionMode",
                VisibleWhenParameterValue = "Choose"
            };
            RuleParameterDescriptor maximumSelectionCount = new("maximumCount", "Maximum number to select", RuleParameterKind.NumericSource)
            {
                Minimum = 0,
                Maximum = 50,
                VisibleWhenParameterKey = "selectionMode",
                VisibleWhenParameterValue = "Choose"
            };
            RuleParameterDescriptor minimumMatches = new("minimumMatches", "At least this many", RuleParameterKind.NumericSource)
            {
                Minimum = 0
            };
            RuleParameterDescriptor canSkip = new("canSkip", "Choice can be skipped", RuleParameterKind.Boolean)
            {
                VisibleWhenParameterKey = "selectionMode",
                VisibleWhenParameterValue = "Choose"
            };
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
            RegisterBuiltInScopedCondition(
                Conditions,
                "Loadout2:CardMatches",
                "Card Matches",
                "Cards",
                new HashSet<string>(["Loadout2:CardPlayed", "Loadout2:CardDrawn", "Loadout2:CardDiscarded", "Loadout2:CardExhausted", "Loadout2:CardGenerated", "Loadout2:CardObtained"], StringComparer.Ordinal),
                cardMatcher);
            RegisterBuiltInScopedCondition(
                Conditions,
                "Loadout2:RelicMatches",
                "Relic Matches",
                "Relics",
                new HashSet<string>(["Loadout2:RelicObtained"], StringComparer.Ordinal),
                relicMatcher);
            RegisterBuiltInScopedCondition(
                Conditions,
                "Loadout2:PotionMatches",
                "Potion Matches",
                "Potions",
                new HashSet<string>(["Loadout2:PotionUsed", "Loadout2:PotionObtained"], StringComparer.Ordinal),
                potionMatcher);
            RegisterBuiltInScopedCondition(
                Conditions,
                "Loadout2:PowerMatches",
                "Power Matches",
                "Powers",
                new HashSet<string>(["Loadout2:PowerReceived"], StringComparer.Ordinal),
                powerMatcher);
            RegisterBuiltInScopedCondition(
                Conditions,
                "Loadout2:MonsterMatches",
                "Monster Matches",
                "Monsters",
                new HashSet<string>(["Loadout2:MonsterSpawned", "Loadout2:MonsterMoveStarted"], StringComparer.Ordinal),
                monsterMatcher);
            RegisterBuiltInScopedCondition(
                Conditions,
                "Loadout2:EventMatches",
                "Event Matches",
                "Events",
                new HashSet<string>(
                    ["Loadout2:EventEntered", "Loadout2:NonAncientEventEntered", "Loadout2:AncientEventEntered"],
                    StringComparer.Ordinal),
                eventMatcher);
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
            RegisterBuiltIn(Conditions, RuleComponentKind.Condition, "Loadout2:PlayerHasCardsMatching", "Player Has Matching Cards", "Cards", cardMatcher, minimumMatches, playerTarget);
            RegisterBuiltIn(Conditions, RuleComponentKind.Condition, "Loadout2:PlayerHasRelicsMatching", "Player Has Matching Relics", "Relics", relicMatcher, minimumMatches, playerTarget);
            RegisterBuiltIn(Conditions, RuleComponentKind.Condition, "Loadout2:PlayerHasPotionsMatching", "Player Has Matching Potions", "Potions", potionMatcher, minimumMatches, playerTarget);
            RegisterBuiltIn(Conditions, RuleComponentKind.Condition, "Loadout2:PlayerHasPowersMatching", "Player Has Matching Powers", "Powers", powerMatcher, minimumMatches, playerTarget);
            RegisterBuiltIn(
                Conditions,
                RuleComponentKind.Condition,
                "Loadout2:NumericComparison",
                "Numeric Comparison",
                "Values",
                new RuleParameterDescriptor("left", "Left Value", RuleParameterKind.NumericSource) { AllowDouble = true, DefaultConstantKind = NumericConstantKind.Double },
                numericOperator,
                new RuleParameterDescriptor("right", "Right Value", RuleParameterKind.NumericSource) { AllowDouble = true, DefaultConstantKind = NumericConstantKind.Double });
            RegisterBuiltIn(
                Conditions,
                RuleComponentKind.Condition,
                "Loadout2:VariableComparison",
                "Variable Comparison",
                "Variables",
                variable,
                numericOperator,
                new RuleParameterDescriptor("value", "Value", RuleParameterKind.NumericSource) { AllowDouble = true, DefaultConstantKind = NumericConstantKind.Double });
            RegisterBuiltIn(
                Conditions,
                RuleComponentKind.Condition,
                "Loadout2:Chance",
                "Chance",
                "Values",
                new RuleParameterDescriptor("percent", "Percent", RuleParameterKind.NumericSource)
                {
                    AllowDouble = true,
                    DefaultConstantKind = NumericConstantKind.Double
                });

            RuleParameterDescriptor numericModification = new("operation", "Operation", RuleParameterKind.Enum)
            {
                Options =
                [
                    new RuleParameterOption(NumericModificationKind.Set.ToString(), "Set value to"),
                    new RuleParameterOption(NumericModificationKind.Add.ToString(), "Add"),
                    new RuleParameterOption(NumericModificationKind.Subtract.ToString(), "Subtract"),
                    new RuleParameterOption(NumericModificationKind.Multiply.ToString(), "Multiply by"),
                    new RuleParameterOption(NumericModificationKind.Divide.ToString(), "Divide by")
                ]
            };
            RuleParameterDescriptor numberVariable = new("variableId", "Variable", RuleParameterKind.NumberVariable);
            RuleParameterDescriptor booleanVariable = new("variableId", "Variable", RuleParameterKind.BooleanVariable);
            RuleParameterDescriptor numericOperand = new("amount", "Value", RuleParameterKind.NumericSource)
            {
                AllowDouble = true,
                DefaultConstantKind = NumericConstantKind.Double
            };

            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:GainPower", "Gain Power", "Powers", true, power, amount, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:GainEnergy", "Gain Energy", "Player", amount, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:DrawCards", "Draw Cards", "Cards", amount, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:GainGold", "Gain Gold", "Player", amount, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:Heal", "Heal", "Player", amount, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:LoseHp", "Lose HP", "Player", amount, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:ObtainCard", "Obtain Card", "Cards", true, card, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:ObtainRelic", "Obtain Relic", "Relics", true, relic, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:ObtainPotion", "Obtain Potion", "Potions", true, potion, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:ObtainCards", "Obtain Cards From Matcher", "Cards", cardMatcher, selectionMode, count, minimumSelectionCount, maximumSelectionCount, canSkip, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:ObtainRelics", "Obtain Relics From Matcher", "Relics", relicMatcher, selectionMode, count, minimumSelectionCount, maximumSelectionCount, canSkip, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:ObtainPotions", "Obtain Potions From Matcher", "Potions", potionMatcher, selectionMode, count, minimumSelectionCount, maximumSelectionCount, canSkip, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:GainPowers", "Gain Powers From Matcher", "Powers", powerMatcher, selectionMode, count, minimumSelectionCount, maximumSelectionCount, canSkip, amount, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:SpawnMonsters", "Spawn Monsters From Matcher", "Monsters", monsterMatcher, selectionMode, count, minimumSelectionCount, maximumSelectionCount, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:AddCardToHand", "Add Card To Hand", "Cards", true, card, amount, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:AddCardsToHand", "Add Cards To Hand From Matcher", "Cards", cardMatcher, selectionMode, count, minimumSelectionCount, maximumSelectionCount, canSkip, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:AddCardToDrawPile", "Add Card To Draw Pile", "Cards", true, card, amount, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:AddCardToDiscardPile", "Add Card To Discard Pile", "Cards", true, card, amount, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:AddCardsToDrawPile", "Add Cards To Draw Pile From Matcher", "Cards", cardMatcher, selectionMode, count, minimumSelectionCount, maximumSelectionCount, canSkip, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:AddCardsToDiscardPile", "Add Cards To Discard Pile From Matcher", "Cards", cardMatcher, selectionMode, count, minimumSelectionCount, maximumSelectionCount, canSkip, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:SetVariable", "Set Variable", "Variables", true, variable, amount, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:AddToVariable", "Add To Variable", "Variables", true, variable, amount, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:SubtractFromVariable", "Subtract From Variable", "Variables", true, variable, amount, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:ModifyVariable", "Modify Variable", "Variables", numberVariable, numericModification, numericOperand, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:SetBooleanVariable", "Set Boolean Variable", "Variables", booleanVariable, new RuleParameterDescriptor("value", "Value", RuleParameterKind.Boolean), playerTarget);
            RuleParameterDescriptor multiplier = new("percent", "Percent", RuleParameterKind.NumericSource)
            {
                DefaultNumeric = 100d
            };
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:SetPlayerDamageMultiplier", "Set Player Damage Multiplier", "Player", true, multiplier, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:SetMonsterDamageMultiplier", "Set Monster Damage Multiplier", "Player", true, multiplier, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:ModifyPlayerDamageMultiplier", "Modify Player Damage Multiplier", "Player", numericModification, multiplier, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:ModifyMonsterDamageMultiplier", "Modify Monster Damage Multiplier", "Player", numericModification, multiplier, playerTarget);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:EnterEvent", "Enter Event Now", "Events", eventModel);
            RegisterBuiltIn(Actions, RuleComponentKind.Action, "Loadout2:SetNextEvent", "Set Next Event", "Events", eventModel);

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

    public static IReadOnlyList<RuleComponentDescriptor> GetDescriptors(RuleComponentKind kind, string triggerId)
    {
        IReadOnlyList<RuleComponentDescriptor> descriptors = GetDescriptors(kind);
        if (kind != RuleComponentKind.Condition)
            return descriptors;
        return descriptors
            .Where(descriptor => descriptor.CompatibleTriggerIds.Count == 0
                                 || descriptor.CompatibleTriggerIds.Contains(triggerId))
            .ToList();
    }

    public static bool IsCompatibleWithTrigger(RuleComponentDescriptor descriptor, string triggerId)
    {
        return descriptor.Kind != RuleComponentKind.Condition
               || descriptor.CompatibleTriggerIds.Count == 0
               || descriptor.CompatibleTriggerIds.Contains(triggerId);
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
        RegisterBuiltIn(dictionary, kind, id, displayName, category, hiddenFromPicker: false, parameters);
    }

    private static void RegisterBuiltIn(
        Dictionary<string, RuleComponentDescriptor> dictionary,
        RuleComponentKind kind,
        string id,
        string displayName,
        string category,
        bool hiddenFromPicker,
        params RuleParameterDescriptor[] parameters)
    {
        dictionary[id] = new RuleComponentDescriptor
        {
            StableId = id,
            DisplayName = displayName,
            Category = category,
            Kind = kind,
            HiddenFromPicker = hiddenFromPicker,
            Parameters = parameters,
            CompilationHandler = new BuiltInRuleComponentHandler(id),
            RuntimeHandler = new BuiltInRuleComponentHandler(id)
        };
    }

    private static void RegisterBuiltInScopedCondition(
        Dictionary<string, RuleComponentDescriptor> dictionary,
        string id,
        string displayName,
        string category,
        IReadOnlySet<string> compatibleTriggerIds,
        params RuleParameterDescriptor[] parameters)
    {
        dictionary[id] = new RuleComponentDescriptor
        {
            StableId = id,
            DisplayName = displayName,
            Category = category,
            Kind = RuleComponentKind.Condition,
            CompatibleTriggerIds = compatibleTriggerIds,
            Parameters = parameters,
            CompilationHandler = new BuiltInRuleComponentHandler(id),
            RuntimeHandler = new BuiltInRuleComponentHandler(id)
        };
    }
}
