#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class AnotherPlayerDiscardKeyword : LoadoutBasicMultiplayerKeywordModel
{
    public const string AmountVar = "LoadoutBasicMultiplayerDiscardAnother";

    public static AnotherPlayerDiscardKeyword Instance { get; } = new();

    private AnotherPlayerDiscardKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.AnotherPlayerDiscard;

    public override string StorageKey => LoadoutKeywords.AnotherPlayerDiscardKey;

    public override string TitleLocKey => "LOADOUT-BASIC_MULTIPLAYER_ANOTHER_PLAYER_DISCARD.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_MULTIPLAYER_ANOTHER_PLAYER_DISCARD.cardText";

    public override LoadoutBasicMultiplayerTargetMode TargetMode =>
        LoadoutBasicMultiplayerTargetMode.AnotherPlayer;

    public override LoadoutBasicMultiplayerEffect Effect =>
        LoadoutBasicMultiplayerEffect.Discard;

    public override string AmountVarName => AmountVar;

    public override string AmountLabelLocKey => "DYNAMIC_VAR_LOADOUT_BASIC_DISCARD";
}
