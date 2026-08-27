#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class AnotherPlayerStarsKeyword : LoadoutBasicMultiplayerKeywordModel
{
    public const string AmountVar = "LoadoutBasicMultiplayerStarsAnother";

    public static AnotherPlayerStarsKeyword Instance { get; } = new();

    private AnotherPlayerStarsKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.AnotherPlayerStars;

    public override string StorageKey => LoadoutKeywords.AnotherPlayerStarsKey;

    public override string TitleLocKey => "LOADOUT-BASIC_MULTIPLAYER_ANOTHER_PLAYER_STARS.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_MULTIPLAYER_ANOTHER_PLAYER_STARS.cardText";

    public override LoadoutBasicMultiplayerTargetMode TargetMode =>
        LoadoutBasicMultiplayerTargetMode.AnotherPlayer;

    public override LoadoutBasicMultiplayerEffect Effect =>
        LoadoutBasicMultiplayerEffect.Stars;

    public override string AmountVarName => AmountVar;

    public override string AmountLabelLocKey => "DYNAMIC_VAR_LOADOUT_BASIC_STARS";
}
