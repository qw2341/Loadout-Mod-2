#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class AllOtherPlayersGainMaxHpKeyword : LoadoutBasicMultiplayerKeywordModel
{
    public const string AmountVar = "LoadoutBasicMultiplayerMaxHpAllOther";

    public static AllOtherPlayersGainMaxHpKeyword Instance { get; } = new();

    private AllOtherPlayersGainMaxHpKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.AllOtherPlayersGainMaxHp;

    public override string StorageKey => LoadoutKeywords.AllOtherPlayersGainMaxHpKey;

    public override string TitleLocKey => "LOADOUT-BASIC_MULTIPLAYER_ALL_OTHER_PLAYERS_GAIN_MAX_HP.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_MULTIPLAYER_ALL_OTHER_PLAYERS_GAIN_MAX_HP.cardText";

    public override LoadoutBasicMultiplayerTargetMode TargetMode =>
        LoadoutBasicMultiplayerTargetMode.AllOtherPlayers;

    public override LoadoutBasicMultiplayerEffect Effect =>
        LoadoutBasicMultiplayerEffect.GainMaxHp;

    public override string AmountVarName => AmountVar;

    public override string AmountLabelLocKey => "DYNAMIC_VAR_LOADOUT_BASIC_MAX_HP_GAIN";
}
