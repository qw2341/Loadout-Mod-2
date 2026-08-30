#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class AllOtherPlayersForgeKeyword : LoadoutBasicMultiplayerKeywordModel
{
    public const string AmountVar = "LoadoutBasicMultiplayerForgeAllOther";

    public static AllOtherPlayersForgeKeyword Instance { get; } = new();

    private AllOtherPlayersForgeKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.AllOtherPlayersForge;

    public override string StorageKey => LoadoutKeywords.AllOtherPlayersForgeKey;

    public override string TitleLocKey =>
        "LOADOUT-BASIC_MULTIPLAYER_ALL_OTHER_PLAYERS_FORGE.title";

    public override string? CardTextLocKey =>
        "LOADOUT-BASIC_MULTIPLAYER_ALL_OTHER_PLAYERS_FORGE.cardText";

    public override LoadoutBasicMultiplayerTargetMode TargetMode =>
        LoadoutBasicMultiplayerTargetMode.AllOtherPlayers;

    public override LoadoutBasicMultiplayerEffect Effect =>
        LoadoutBasicMultiplayerEffect.Forge;

    public override string AmountVarName => AmountVar;

    public override string AmountLabelLocKey =>
        "DYNAMIC_VAR_LOADOUT_BASIC_FORGE";
}
