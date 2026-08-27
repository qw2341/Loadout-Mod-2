#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class AnotherPlayerEnergyKeyword : LoadoutBasicMultiplayerKeywordModel
{
    public const string AmountVar = "LoadoutBasicMultiplayerEnergyAnother";

    public static AnotherPlayerEnergyKeyword Instance { get; } = new();

    private AnotherPlayerEnergyKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.AnotherPlayerEnergy;

    public override string StorageKey => LoadoutKeywords.AnotherPlayerEnergyKey;

    public override string TitleLocKey => "LOADOUT-BASIC_MULTIPLAYER_ANOTHER_PLAYER_ENERGY.title";

    public override string? CardTextLocKey => "LOADOUT-BASIC_MULTIPLAYER_ANOTHER_PLAYER_ENERGY.cardText";

    public override LoadoutBasicMultiplayerTargetMode TargetMode =>
        LoadoutBasicMultiplayerTargetMode.AnotherPlayer;

    public override LoadoutBasicMultiplayerEffect Effect =>
        LoadoutBasicMultiplayerEffect.Energy;

    public override string AmountVarName => AmountVar;

    public override string AmountLabelLocKey => "DYNAMIC_VAR_LOADOUT_BASIC_ENERGY";
}
