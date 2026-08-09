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
    NumericSource
}

public sealed record RuleParameterDescriptor(
    string Key,
    string DisplayName,
    RuleParameterKind Kind,
    bool Required = true);

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

            RegisterBuiltIn(Conditions, RuleComponentKind.Condition, "Loadout2:Always", "Always", "General");
            RegisterBuiltIn(Conditions, RuleComponentKind.Condition, "Loadout2:PlayerHasRole", "Player Has Role", "Players");
            RegisterBuiltIn(Conditions, RuleComponentKind.Condition, "Loadout2:CardMatches", "Card Matches", "Cards");
            RegisterBuiltIn(Conditions, RuleComponentKind.Condition, "Loadout2:PlayerHasCard", "Player Has Card", "Cards");
            RegisterBuiltIn(Conditions, RuleComponentKind.Condition, "Loadout2:PlayerHasRelic", "Player Has Relic", "Relics");
            RegisterBuiltIn(Conditions, RuleComponentKind.Condition, "Loadout2:PlayerHasPower", "Player Has Power", "Powers");
            RegisterBuiltIn(Conditions, RuleComponentKind.Condition, "Loadout2:NumericComparison", "Numeric Comparison", "Values");
            RegisterBuiltIn(Conditions, RuleComponentKind.Condition, "Loadout2:VariableComparison", "Variable Comparison", "Variables");
            RegisterBuiltIn(Conditions, RuleComponentKind.Condition, "Loadout2:Chance", "Chance", "Values");

            string[] actionIds =
            [
                "GainPower", "GainEnergy", "DrawCards", "GainGold", "Heal", "LoseHp",
                "ObtainCard", "ObtainRelic", "ObtainPotion", "AddCardToHand",
                "AddCardToDrawPile", "AddCardToDiscardPile", "SetVariable",
                "AddToVariable", "SubtractFromVariable"
            ];
            foreach (string id in actionIds)
                RegisterBuiltIn(Actions, RuleComponentKind.Action, $"Loadout2:{id}", SplitWords(id), "Actions");

            string[] targetIds =
            [
                "TriggeringPlayer", "Host", "AllPlayers", "AllOtherPlayers", "RandomPlayer",
                "SpecificPlayerSlot", "PlayersWithRole", "RandomPlayerWithRole"
            ];
            foreach (string id in targetIds)
                RegisterBuiltIn(Targets, RuleComponentKind.Target, $"Loadout2:{id}", SplitWords(id), "Players");
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
        string category)
    {
        dictionary[id] = new RuleComponentDescriptor
        {
            StableId = id,
            DisplayName = displayName,
            Category = category,
            Kind = kind
        };
    }

    private static string SplitWords(string value)
    {
        List<char> result = [];
        foreach (char character in value)
        {
            if (result.Count > 0 && char.IsUpper(character))
                result.Add(' ');
            result.Add(character);
        }
        return new string(result.ToArray());
    }
}
