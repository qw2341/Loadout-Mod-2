#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class AllOtherPlayersTransformKeyword : LoadoutBasicMultiplayerKeywordModel
{
    public const string AmountVar = "LoadoutBasicMultiplayerTransformAllOther";

    public static AllOtherPlayersTransformKeyword Instance { get; } = new();

    private AllOtherPlayersTransformKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.AllOtherPlayersTransform;

    public override string StorageKey => LoadoutKeywords.AllOtherPlayersTransformKey;

    public override string TitleLocKey => "LOADOUT-BASIC_MULTIPLAYER_ALL_OTHER_PLAYERS_TRANSFORM.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_MULTIPLAYER_ALL_OTHER_PLAYERS_TRANSFORM.cardText";

    public override LoadoutBasicMultiplayerTargetMode TargetMode =>
        LoadoutBasicMultiplayerTargetMode.AllOtherPlayers;

    public override LoadoutBasicMultiplayerEffect Effect =>
        LoadoutBasicMultiplayerEffect.Transform;

    public override string AmountVarName => AmountVar;

    public override string AmountLabelLocKey => "DYNAMIC_VAR_LOADOUT_BASIC_TRANSFORM";
}
