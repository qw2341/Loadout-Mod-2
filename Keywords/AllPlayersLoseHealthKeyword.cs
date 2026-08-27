#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class AllPlayersLoseHealthKeyword : LoadoutBasicMultiplayerKeywordModel
{
    public const string AmountVar = "LoadoutBasicMultiplayerHpLossAll";

    public static AllPlayersLoseHealthKeyword Instance { get; } = new();

    private AllPlayersLoseHealthKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.AllPlayersLoseHealth;

    public override string StorageKey => LoadoutKeywords.AllPlayersLoseHealthKey;

    public override string TitleLocKey => "LOADOUT-BASIC_MULTIPLAYER_ALL_PLAYERS_LOSE_HEALTH.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_MULTIPLAYER_ALL_PLAYERS_LOSE_HEALTH.cardText";

    public override LoadoutBasicMultiplayerTargetMode TargetMode =>
        LoadoutBasicMultiplayerTargetMode.AllPlayers;

    public override LoadoutBasicMultiplayerEffect Effect =>
        LoadoutBasicMultiplayerEffect.LoseHealth;

    public override string AmountVarName => AmountVar;

    public override string AmountLabelLocKey => "DYNAMIC_VAR_LOADOUT_BASIC_HP_LOSS";
}
