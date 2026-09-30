#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class AnotherPlayerSummonKeyword : LoadoutBasicMultiplayerKeywordModel
{
    public const string AmountVar = "LoadoutBasicMultiplayerSummonAnother";

    public static AnotherPlayerSummonKeyword Instance { get; } = new();

    private AnotherPlayerSummonKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.AnotherPlayerSummon;

    public override string StorageKey => LoadoutKeywords.AnotherPlayerSummonKey;

    public override string TitleLocKey =>
        "LOADOUT-BASIC_MULTIPLAYER_ANOTHER_PLAYER_SUMMON.title";

    public override string? CardTextLocKey =>
        "LOADOUT-BASIC_MULTIPLAYER_ANOTHER_PLAYER_SUMMON.cardText";

    public override LoadoutBasicMultiplayerTargetMode TargetMode =>
        LoadoutBasicMultiplayerTargetMode.AnotherPlayer;

    public override LoadoutBasicMultiplayerEffect Effect =>
        LoadoutBasicMultiplayerEffect.Summon;

    public override string AmountVarName => AmountVar;

    public override string AmountLabelLocKey =>
        "DYNAMIC_VAR_LOADOUT_BASIC_SUMMON";
}
