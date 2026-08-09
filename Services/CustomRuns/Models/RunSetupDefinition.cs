#nullable enable

namespace Loadout.Services.CustomRuns.Models;

using System.Text.Json.Serialization;

public sealed class RunSetupDefinition
{
    [JsonPropertyName("character")]
    public SelectionSpec Character { get; set; } = SelectionSpec.Default(SelectionModelKind.Character);

    [JsonPropertyName("startingDeck")]
    public SelectionSpec StartingDeck { get; set; } = SelectionSpec.Default(SelectionModelKind.Card);

    [JsonPropertyName("startingRelics")]
    public SelectionSpec StartingRelics { get; set; } = SelectionSpec.Default(SelectionModelKind.Relic);

    [JsonPropertyName("startingPotions")]
    public SelectionSpec StartingPotions { get; set; } = SelectionSpec.Default(SelectionModelKind.Potion);

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
