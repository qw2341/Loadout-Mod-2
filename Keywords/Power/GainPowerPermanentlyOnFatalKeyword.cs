#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

public sealed class GainPowerPermanentlyOnFatalKeyword : LoadoutPowerKeywordModel
{
    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        Variables = CreateDisplayVariables(
            "LoadoutGainPowerPermanentlyOnFatalList",
            "DYNAMIC_VAR_LOADOUT_GAIN_POWER_PERMANENTLY_ON_FATAL_LIST",
            LoadoutKeywords.GainPowerPermanentlyOnFatalKey,
            LoadoutPowerKeywordDescriptionStyle.FatalPermanentGainLose);

    public static GainPowerPermanentlyOnFatalKeyword Instance { get; } = new();

    private GainPowerPermanentlyOnFatalKeyword()
    {
    }

    public override CardKeyword Keyword =>
        LoadoutKeywords.GainPowerPermanentlyOnFatal;

    public override string StorageKey =>
        LoadoutKeywords.GainPowerPermanentlyOnFatalKey;

    public override string TitleLocKey =>
        "LOADOUT-GAIN_POWER_PERMANENTLY_ON_FATAL.title";

    public override string? CardTextLocKey =>
        "LOADOUT-GAIN_POWER_PERMANENTLY_ON_FATAL.cardText";

    public override string DisplayVarName =>
        "LoadoutGainPowerPermanentlyOnFatalList";

    public override string AmountLabelLocKey =>
        "CARD_MOD_GAIN_POWER_PERMANENTLY_ON_FATAL_AMOUNT";

    public override string PowerLabelLocKey =>
        "CARD_MOD_GAIN_POWER_PERMANENTLY_ON_FATAL_POWER";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        Variables;

    public override LoadoutPowerKeywordTargetMode TargetMode =>
        LoadoutPowerKeywordTargetMode.Self;

    public override bool HasOnPlayEffect => false;

    public override bool HasFatalEffect => true;

    public override Task AfterFatal(
        CardModel card,
        PlayerChoiceContext choiceContext,
        int fatalCount) =>
        ApplyPermanentlyToPowerGiver(
            card,
            choiceContext,
            StorageKey,
            fatalCount);
}
