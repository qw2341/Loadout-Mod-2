#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class AllOtherPlayersHealKeyword : LoadoutBasicMultiplayerKeywordModel
{
    public const string AmountVar = "LoadoutBasicMultiplayerHealAllOther";

    public static AllOtherPlayersHealKeyword Instance { get; } = new();

    private AllOtherPlayersHealKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.AllOtherPlayersHeal;

    public override string StorageKey => LoadoutKeywords.AllOtherPlayersHealKey;

    public override string TitleLocKey => "LOADOUT-BASIC_MULTIPLAYER_ALL_OTHER_PLAYERS_HEAL.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_MULTIPLAYER_ALL_OTHER_PLAYERS_HEAL.cardText";

    public override LoadoutBasicMultiplayerTargetMode TargetMode =>
        LoadoutBasicMultiplayerTargetMode.AllOtherPlayers;

    public override LoadoutBasicMultiplayerEffect Effect =>
        LoadoutBasicMultiplayerEffect.Heal;

    public override string AmountVarName => AmountVar;

    public override string AmountLabelLocKey => "DYNAMIC_VAR_LOADOUT_BASIC_HEAL";
}
