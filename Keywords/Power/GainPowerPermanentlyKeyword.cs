#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

public sealed class GainPowerPermanentlyKeyword : LoadoutPowerKeywordModel
{
    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        Variables = CreateDisplayVariables(
            "LoadoutGainPowerPermanentlyList",
            "DYNAMIC_VAR_LOADOUT_GAIN_POWER_PERMANENTLY_LIST",
            LoadoutKeywords.GainPowerPermanentlyKey,
            LoadoutPowerKeywordDescriptionStyle.PermanentGainLose);

    public static GainPowerPermanentlyKeyword Instance { get; } = new();

    private GainPowerPermanentlyKeyword()
    {
    }

    public override CardKeyword Keyword =>
        LoadoutKeywords.GainPowerPermanently;

    public override string StorageKey =>
        LoadoutKeywords.GainPowerPermanentlyKey;

    public override string TitleLocKey =>
        "LOADOUT-GAIN_POWER_PERMANENTLY.title";

    public override string? CardTextLocKey =>
        "LOADOUT-GAIN_POWER_PERMANENTLY.cardText";

    public override string DisplayVarName =>
        "LoadoutGainPowerPermanentlyList";

    public override string AmountLabelLocKey =>
        "CARD_MOD_GAIN_POWER_PERMANENTLY_AMOUNT";

    public override string PowerLabelLocKey =>
        "CARD_MOD_GAIN_POWER_PERMANENTLY_POWER";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        Variables;

    public override LoadoutPowerKeywordTargetMode TargetMode =>
        LoadoutPowerKeywordTargetMode.Self;

    public override Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState) =>
        ApplyPermanentlyToPowerGiver(
            card,
            choiceContext,
            StorageKey,
            repetitionCount: 1);
}
