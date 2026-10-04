#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

public sealed class ExecuteKeyword : LoadoutKeywordModel
{
    public const string ChanceVar = "LoadoutExecuteChance";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition> Variables =
    [
        new(ChanceVar, 10m, 0, int.MaxValue, "DYNAMIC_VAR_LOADOUT_EXECUTE_CHANCE")
    ];

    public static ExecuteKeyword Instance { get; } = new();
    public override CardKeyword Keyword => LoadoutKeywords.Execute;
    public override string StorageKey => LoadoutKeywords.ExecuteKey;
    public override string TitleLocKey => "LOADOUT-EXECUTE.title";
    public override string CardTextLocKey => "LOADOUT-EXECUTE.cardText";
    public override LoadoutKeywordPresentation Presentation => LoadoutKeywordPresentation.DescriptionOnly;
    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars => Variables;
    public override bool HasOnPlayEffect => true;
    public override bool ChangesTargeting => true;

    protected override void AddCardTextVariables(CardModel card, LocString cardText)
    {
        LocString text = new("card_keywords", GetChance(card) >= 100m
            ? "LOADOUT-EXECUTE.guaranteedText"
            : "LOADOUT-EXECUTE.chanceText");
        card.DynamicVars.AddTo(text);
        cardText.AddObj("ExecuteText", text.GetFormattedText());
    }

    public override async Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        if (cardPlay.Target is not { IsDead: false } target)
            return;

        decimal chance = GetChance(card);
        if (chance <= 0m || (chance < 100m
            && card.Owner.RunState.Rng.CombatCardSelection.NextDouble() * 100d >= (double)chance))
            return;

        await CreatureCmd.Kill(target);
    }

    public static decimal GetChance(CardModel card) =>
        LoadoutKeywordRegistry.TryGetValue(card, ChanceVar, out var chance)
            ? chance.BaseValue
            : 10m;
}
