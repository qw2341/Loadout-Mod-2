#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class AllOtherPlayersStarsKeyword : LoadoutBasicMultiplayerKeywordModel
{
    public const string AmountVar = "LoadoutBasicMultiplayerStarsAllOther";

    public static AllOtherPlayersStarsKeyword Instance { get; } = new();

    private AllOtherPlayersStarsKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.AllOtherPlayersStars;

    public override string StorageKey => LoadoutKeywords.AllOtherPlayersStarsKey;

    public override string TitleLocKey => "LOADOUT-BASIC_MULTIPLAYER_ALL_OTHER_PLAYERS_STARS.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_MULTIPLAYER_ALL_OTHER_PLAYERS_STARS.cardText";

    public override LoadoutBasicMultiplayerTargetMode TargetMode =>
        LoadoutBasicMultiplayerTargetMode.AllOtherPlayers;

    public override LoadoutBasicMultiplayerEffect Effect =>
        LoadoutBasicMultiplayerEffect.Stars;

    public override string AmountVarName => AmountVar;

    public override string AmountLabelLocKey => "DYNAMIC_VAR_LOADOUT_BASIC_STARS";
}
