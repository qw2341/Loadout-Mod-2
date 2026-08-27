#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class AllPlayersGainMaxHpKeyword : LoadoutBasicMultiplayerKeywordModel
{
    public const string AmountVar = "LoadoutBasicMultiplayerMaxHpAll";

    public static AllPlayersGainMaxHpKeyword Instance { get; } = new();

    private AllPlayersGainMaxHpKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.AllPlayersGainMaxHp;

    public override string StorageKey => LoadoutKeywords.AllPlayersGainMaxHpKey;

    public override string TitleLocKey => "LOADOUT-BASIC_MULTIPLAYER_ALL_PLAYERS_GAIN_MAX_HP.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_MULTIPLAYER_ALL_PLAYERS_GAIN_MAX_HP.cardText";

    public override LoadoutBasicMultiplayerTargetMode TargetMode =>
        LoadoutBasicMultiplayerTargetMode.AllPlayers;

    public override LoadoutBasicMultiplayerEffect Effect =>
        LoadoutBasicMultiplayerEffect.GainMaxHp;

    public override string AmountVarName => AmountVar;

    public override string AmountLabelLocKey => "DYNAMIC_VAR_LOADOUT_BASIC_MAX_HP_GAIN";
}
