#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class AllOtherPlayersSummonKeyword : LoadoutBasicMultiplayerKeywordModel
{
    public const string AmountVar = "LoadoutBasicMultiplayerSummonAllOther";

    public static AllOtherPlayersSummonKeyword Instance { get; } = new();

    private AllOtherPlayersSummonKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.AllOtherPlayersSummon;

    public override string StorageKey => LoadoutKeywords.AllOtherPlayersSummonKey;

    public override string TitleLocKey =>
        "LOADOUT-BASIC_MULTIPLAYER_ALL_OTHER_PLAYERS_SUMMON.title";

    public override string? CardTextLocKey =>
        "LOADOUT-BASIC_MULTIPLAYER_ALL_OTHER_PLAYERS_SUMMON.cardText";

    public override LoadoutBasicMultiplayerTargetMode TargetMode =>
        LoadoutBasicMultiplayerTargetMode.AllOtherPlayers;

    public override LoadoutBasicMultiplayerEffect Effect =>
        LoadoutBasicMultiplayerEffect.Summon;

    public override string AmountVarName => AmountVar;

    public override string AmountLabelLocKey =>
        "DYNAMIC_VAR_LOADOUT_BASIC_SUMMON";
}
