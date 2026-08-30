#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class AnotherPlayerForgeKeyword : LoadoutBasicMultiplayerKeywordModel
{
    public const string AmountVar = "LoadoutBasicMultiplayerForgeAnother";

    public static AnotherPlayerForgeKeyword Instance { get; } = new();

    private AnotherPlayerForgeKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.AnotherPlayerForge;

    public override string StorageKey => LoadoutKeywords.AnotherPlayerForgeKey;

    public override string TitleLocKey =>
        "LOADOUT-BASIC_MULTIPLAYER_ANOTHER_PLAYER_FORGE.title";

    public override string? CardTextLocKey =>
        "LOADOUT-BASIC_MULTIPLAYER_ANOTHER_PLAYER_FORGE.cardText";

    public override LoadoutBasicMultiplayerTargetMode TargetMode =>
        LoadoutBasicMultiplayerTargetMode.AnotherPlayer;

    public override LoadoutBasicMultiplayerEffect Effect =>
        LoadoutBasicMultiplayerEffect.Forge;

    public override string AmountVarName => AmountVar;

    public override string AmountLabelLocKey =>
        "DYNAMIC_VAR_LOADOUT_BASIC_FORGE";
}
