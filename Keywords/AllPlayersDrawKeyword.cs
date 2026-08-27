#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class AllPlayersDrawKeyword : LoadoutBasicMultiplayerKeywordModel
{
    public const string AmountVar = "LoadoutBasicMultiplayerDrawAll";

    public static AllPlayersDrawKeyword Instance { get; } = new();

    private AllPlayersDrawKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.AllPlayersDraw;

    public override string StorageKey => LoadoutKeywords.AllPlayersDrawKey;

    public override string TitleLocKey => "LOADOUT-BASIC_MULTIPLAYER_ALL_PLAYERS_DRAW.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_MULTIPLAYER_ALL_PLAYERS_DRAW.cardText";

    public override LoadoutBasicMultiplayerTargetMode TargetMode =>
        LoadoutBasicMultiplayerTargetMode.AllPlayers;

    public override LoadoutBasicMultiplayerEffect Effect =>
        LoadoutBasicMultiplayerEffect.Draw;

    public override string AmountVarName => AmountVar;

    public override string AmountLabelLocKey => "DYNAMIC_VAR_LOADOUT_BASIC_DRAW";
}
