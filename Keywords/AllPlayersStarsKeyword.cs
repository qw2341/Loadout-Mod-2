#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class AllPlayersStarsKeyword : LoadoutBasicMultiplayerKeywordModel
{
    public const string AmountVar = "LoadoutBasicMultiplayerStarsAll";

    public static AllPlayersStarsKeyword Instance { get; } = new();

    private AllPlayersStarsKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.AllPlayersStars;

    public override string StorageKey => LoadoutKeywords.AllPlayersStarsKey;

    public override string TitleLocKey => "LOADOUT-BASIC_MULTIPLAYER_ALL_PLAYERS_STARS.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_MULTIPLAYER_ALL_PLAYERS_STARS.cardText";

    public override LoadoutBasicMultiplayerTargetMode TargetMode =>
        LoadoutBasicMultiplayerTargetMode.AllPlayers;

    public override LoadoutBasicMultiplayerEffect Effect =>
        LoadoutBasicMultiplayerEffect.Stars;

    public override string AmountVarName => AmountVar;

    public override string AmountLabelLocKey => "DYNAMIC_VAR_LOADOUT_BASIC_STARS";
}
