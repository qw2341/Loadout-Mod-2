#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class AnotherPlayerBlockKeyword : LoadoutBasicMultiplayerKeywordModel
{
    public const string AmountVar = "LoadoutBasicMultiplayerBlockAnother";

    public static AnotherPlayerBlockKeyword Instance { get; } = new();

    private AnotherPlayerBlockKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.AnotherPlayerBlock;

    public override string StorageKey => LoadoutKeywords.AnotherPlayerBlockKey;

    public override string TitleLocKey => "LOADOUT-BASIC_MULTIPLAYER_ANOTHER_PLAYER_BLOCK.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_MULTIPLAYER_ANOTHER_PLAYER_BLOCK.cardText";

    public override LoadoutBasicMultiplayerTargetMode TargetMode =>
        LoadoutBasicMultiplayerTargetMode.AnotherPlayer;

    public override LoadoutBasicMultiplayerEffect Effect =>
        LoadoutBasicMultiplayerEffect.Block;

    public override string AmountVarName => AmountVar;

    public override string AmountLabelLocKey => "DYNAMIC_VAR_LOADOUT_BASIC_BLOCK";
}
