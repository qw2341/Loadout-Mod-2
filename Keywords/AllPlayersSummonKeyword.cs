#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class AllPlayersSummonKeyword : LoadoutBasicMultiplayerKeywordModel
{
    public const string AmountVar = "LoadoutBasicMultiplayerSummonAll";

    public static AllPlayersSummonKeyword Instance { get; } = new();

    private AllPlayersSummonKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.AllPlayersSummon;

    public override string StorageKey => LoadoutKeywords.AllPlayersSummonKey;

    public override string TitleLocKey =>
        "LOADOUT-BASIC_MULTIPLAYER_ALL_PLAYERS_SUMMON.title";

    public override string? CardTextLocKey =>
        "LOADOUT-BASIC_MULTIPLAYER_ALL_PLAYERS_SUMMON.cardText";

    public override LoadoutBasicMultiplayerTargetMode TargetMode =>
        LoadoutBasicMultiplayerTargetMode.AllPlayers;

    public override LoadoutBasicMultiplayerEffect Effect =>
        LoadoutBasicMultiplayerEffect.Summon;

    public override string AmountVarName => AmountVar;

    public override string AmountLabelLocKey =>
        "DYNAMIC_VAR_LOADOUT_BASIC_SUMMON";
}
