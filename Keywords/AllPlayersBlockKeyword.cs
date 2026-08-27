#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class AllPlayersBlockKeyword : LoadoutBasicMultiplayerKeywordModel
{
    public const string AmountVar = "LoadoutBasicMultiplayerBlockAll";

    public static AllPlayersBlockKeyword Instance { get; } = new();

    private AllPlayersBlockKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.AllPlayersBlock;

    public override string StorageKey => LoadoutKeywords.AllPlayersBlockKey;

    public override string TitleLocKey => "LOADOUT-BASIC_MULTIPLAYER_ALL_PLAYERS_BLOCK.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_MULTIPLAYER_ALL_PLAYERS_BLOCK.cardText";

    public override LoadoutBasicMultiplayerTargetMode TargetMode =>
        LoadoutBasicMultiplayerTargetMode.AllPlayers;

    public override LoadoutBasicMultiplayerEffect Effect =>
        LoadoutBasicMultiplayerEffect.Block;

    public override string AmountVarName => AmountVar;

    public override string AmountLabelLocKey => "DYNAMIC_VAR_LOADOUT_BASIC_BLOCK";
}
