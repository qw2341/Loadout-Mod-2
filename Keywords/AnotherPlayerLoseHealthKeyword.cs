#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class AnotherPlayerLoseHealthKeyword : LoadoutBasicMultiplayerKeywordModel
{
    public const string AmountVar = "LoadoutBasicMultiplayerHpLossAnother";

    public static AnotherPlayerLoseHealthKeyword Instance { get; } = new();

    private AnotherPlayerLoseHealthKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.AnotherPlayerLoseHealth;

    public override string StorageKey => LoadoutKeywords.AnotherPlayerLoseHealthKey;

    public override string TitleLocKey => "LOADOUT-BASIC_MULTIPLAYER_ANOTHER_PLAYER_LOSE_HEALTH.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_MULTIPLAYER_ANOTHER_PLAYER_LOSE_HEALTH.cardText";

    public override LoadoutBasicMultiplayerTargetMode TargetMode =>
        LoadoutBasicMultiplayerTargetMode.AnotherPlayer;

    public override LoadoutBasicMultiplayerEffect Effect =>
        LoadoutBasicMultiplayerEffect.LoseHealth;

    public override string AmountVarName => AmountVar;

    public override string AmountLabelLocKey => "DYNAMIC_VAR_LOADOUT_BASIC_HP_LOSS";
}
