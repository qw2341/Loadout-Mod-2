#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class AnotherPlayerGainMaxHpKeyword : LoadoutBasicMultiplayerKeywordModel
{
    public const string AmountVar = "LoadoutBasicMultiplayerMaxHpAnother";

    public static AnotherPlayerGainMaxHpKeyword Instance { get; } = new();

    private AnotherPlayerGainMaxHpKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.AnotherPlayerGainMaxHp;

    public override string StorageKey => LoadoutKeywords.AnotherPlayerGainMaxHpKey;

    public override string TitleLocKey => "LOADOUT-BASIC_MULTIPLAYER_ANOTHER_PLAYER_GAIN_MAX_HP.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_MULTIPLAYER_ANOTHER_PLAYER_GAIN_MAX_HP.cardText";

    public override LoadoutBasicMultiplayerTargetMode TargetMode =>
        LoadoutBasicMultiplayerTargetMode.AnotherPlayer;

    public override LoadoutBasicMultiplayerEffect Effect =>
        LoadoutBasicMultiplayerEffect.GainMaxHp;

    public override string AmountVarName => AmountVar;

    public override string AmountLabelLocKey => "DYNAMIC_VAR_LOADOUT_BASIC_MAX_HP_GAIN";
}
