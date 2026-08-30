#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class AllPlayersForgeKeyword : LoadoutBasicMultiplayerKeywordModel
{
    public const string AmountVar = "LoadoutBasicMultiplayerForgeAll";

    public static AllPlayersForgeKeyword Instance { get; } = new();

    private AllPlayersForgeKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.AllPlayersForge;

    public override string StorageKey => LoadoutKeywords.AllPlayersForgeKey;

    public override string TitleLocKey =>
        "LOADOUT-BASIC_MULTIPLAYER_ALL_PLAYERS_FORGE.title";

    public override string? CardTextLocKey =>
        "LOADOUT-BASIC_MULTIPLAYER_ALL_PLAYERS_FORGE.cardText";

    public override LoadoutBasicMultiplayerTargetMode TargetMode =>
        LoadoutBasicMultiplayerTargetMode.AllPlayers;

    public override LoadoutBasicMultiplayerEffect Effect =>
        LoadoutBasicMultiplayerEffect.Forge;

    public override string AmountVarName => AmountVar;

    public override string AmountLabelLocKey =>
        "DYNAMIC_VAR_LOADOUT_BASIC_FORGE";
}
