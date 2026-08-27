#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class AllPlayersDiscardKeyword : LoadoutBasicMultiplayerKeywordModel
{
    public const string AmountVar = "LoadoutBasicMultiplayerDiscardAll";

    public static AllPlayersDiscardKeyword Instance { get; } = new();

    private AllPlayersDiscardKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.AllPlayersDiscard;

    public override string StorageKey => LoadoutKeywords.AllPlayersDiscardKey;

    public override string TitleLocKey => "LOADOUT-BASIC_MULTIPLAYER_ALL_PLAYERS_DISCARD.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_MULTIPLAYER_ALL_PLAYERS_DISCARD.cardText";

    public override LoadoutBasicMultiplayerTargetMode TargetMode =>
        LoadoutBasicMultiplayerTargetMode.AllPlayers;

    public override LoadoutBasicMultiplayerEffect Effect =>
        LoadoutBasicMultiplayerEffect.Discard;

    public override string AmountVarName => AmountVar;

    public override string AmountLabelLocKey => "DYNAMIC_VAR_LOADOUT_BASIC_DISCARD";
}
