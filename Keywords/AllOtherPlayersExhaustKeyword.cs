#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class AllOtherPlayersExhaustKeyword : LoadoutBasicMultiplayerKeywordModel
{
    public const string AmountVar = "LoadoutBasicMultiplayerExhaustAllOther";

    public static AllOtherPlayersExhaustKeyword Instance { get; } = new();

    private AllOtherPlayersExhaustKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.AllOtherPlayersExhaust;

    public override string StorageKey => LoadoutKeywords.AllOtherPlayersExhaustKey;

    public override string TitleLocKey => "LOADOUT-BASIC_MULTIPLAYER_ALL_OTHER_PLAYERS_EXHAUST.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_MULTIPLAYER_ALL_OTHER_PLAYERS_EXHAUST.cardText";

    public override LoadoutBasicMultiplayerTargetMode TargetMode =>
        LoadoutBasicMultiplayerTargetMode.AllOtherPlayers;

    public override LoadoutBasicMultiplayerEffect Effect =>
        LoadoutBasicMultiplayerEffect.Exhaust;

    public override string AmountVarName => AmountVar;

    public override string AmountLabelLocKey => "DYNAMIC_VAR_LOADOUT_BASIC_EXHAUST";
}
