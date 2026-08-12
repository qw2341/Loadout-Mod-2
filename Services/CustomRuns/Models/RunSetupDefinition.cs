#nullable enable

namespace Loadout.Services.CustomRuns.Models;

using System.Collections.Generic;
using System.Text.Json.Serialization;
using Loadout.Services.Loadouts;

public enum StartingLoadoutMode
{
    PerCharacter,
    Unified
}

public interface IStartingLoadoutDefinition
{
    SelectionSpec StartingDeck { get; set; }
    List<SavedCardLoadoutEntry> StartingCardEntries { get; set; }
    SelectionSpec StartingRelics { get; set; }
    List<SavedRelicLoadoutEntry> StartingRelicEntries { get; set; }
    SelectionSpec StartingPotions { get; set; }
    List<StartingPowerDefinition> StartingPowers { get; set; }
    string? StartingMorphModelId { get; set; }
}

public sealed class RunSetupDefinition : IStartingLoadoutDefinition
{
    [JsonPropertyName("character")]
    public SelectionSpec Character { get; set; } = SelectionSpec.Default(SelectionModelKind.Character);

    [JsonPropertyName("startingLoadoutMode")]
    public StartingLoadoutMode StartingLoadoutMode { get; set; } = StartingLoadoutMode.PerCharacter;

    [JsonPropertyName("characterStartingLoadouts")]
    public List<CharacterStartingLoadoutDefinition> CharacterStartingLoadouts { get; set; } = [];

    [JsonPropertyName("startingDeck")]
    public SelectionSpec StartingDeck { get; set; } = SelectionSpec.Default(SelectionModelKind.Card);

    [JsonPropertyName("startingCardEntries")]
    public List<SavedCardLoadoutEntry> StartingCardEntries { get; set; } = [];

    [JsonPropertyName("startingRelics")]
    public SelectionSpec StartingRelics { get; set; } = SelectionSpec.Default(SelectionModelKind.Relic);

    [JsonPropertyName("startingRelicEntries")]
    public List<SavedRelicLoadoutEntry> StartingRelicEntries { get; set; } = [];

    [JsonPropertyName("startingPotions")]
    public SelectionSpec StartingPotions { get; set; } = SelectionSpec.Default(SelectionModelKind.Potion);

    [JsonPropertyName("startingPowers")]
    public List<StartingPowerDefinition> StartingPowers { get; set; } = [];

    [JsonPropertyName("startingMorphModelId")]
    public string? StartingMorphModelId { get; set; }

    [JsonPropertyName("startingAscension")]
    public int? StartingAscension { get; set; }

    [JsonPropertyName("modifiersEnabled")]
    public bool ModifiersEnabled { get; set; }

    [JsonPropertyName("modifiers")]
    public List<RunModifierDefinition> Modifiers { get; set; } = [];

    [JsonPropertyName("potionSlots")]
    public int? PotionSlots { get; set; }

    [JsonPropertyName("startingGold")]
    public int? StartingGold { get; set; }

    [JsonPropertyName("startingCurrentHp")]
    public int? StartingCurrentHp { get; set; }

    [JsonPropertyName("startingMaxHp")]
    public int? StartingMaxHp { get; set; }

    [JsonPropertyName("baseEnergyPerTurn")]
    public int? BaseEnergyPerTurn { get; set; }

    [JsonPropertyName("cardsDrawnPerTurn")]
    public int? CardsDrawnPerTurn { get; set; }

    [JsonPropertyName("runSeed")]
    public string? RunSeed { get; set; }
}

public sealed class RunModifierDefinition
{
    [JsonPropertyName("modelId")]
    public string ModelId { get; set; } = string.Empty;

    [JsonPropertyName("characterModelId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CharacterModelId { get; set; }
}

public sealed class CharacterStartingLoadoutDefinition : IStartingLoadoutDefinition
{
    [JsonPropertyName("characterModelId")]
    public string CharacterModelId { get; set; } = string.Empty;

    [JsonPropertyName("startingDeck")]
    public SelectionSpec StartingDeck { get; set; } = SelectionSpec.Default(SelectionModelKind.Card);

    [JsonPropertyName("startingCardEntries")]
    public List<SavedCardLoadoutEntry> StartingCardEntries { get; set; } = [];

    [JsonPropertyName("startingRelics")]
    public SelectionSpec StartingRelics { get; set; } = SelectionSpec.Default(SelectionModelKind.Relic);

    [JsonPropertyName("startingRelicEntries")]
    public List<SavedRelicLoadoutEntry> StartingRelicEntries { get; set; } = [];

    [JsonPropertyName("startingPotions")]
    public SelectionSpec StartingPotions { get; set; } = SelectionSpec.Default(SelectionModelKind.Potion);

    [JsonPropertyName("startingPowers")]
    public List<StartingPowerDefinition> StartingPowers { get; set; } = [];

    [JsonPropertyName("startingMorphModelId")]
    public string? StartingMorphModelId { get; set; }
}

public sealed class StartingPowerDefinition
{
    [JsonPropertyName("modelId")]
    public string ModelId { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public int Amount { get; set; }
}
