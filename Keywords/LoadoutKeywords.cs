#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using BaseLib.Patches.Content;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

public static class LoadoutKeywords
{
    public const string InevitableKey = "Inevitable";
    public const string StickyKey = "Sticky";
    public const string PassingKey = "Passing";
    public const string LividKey = "Livid";
    public const string XCostKey = "XCost";
    public const string XValueKey = "XValue";
    public const string AltXValueKey = "AltXValue";
    public const string InfiniteUpgradeKey = "InfiniteUpgrade";
    public const string JokeInfiniteUpgradeKey = "JokeInfiniteUpgrade";
    public const string KarmicKey = "Karmic";
    public const string LessonLearnedKey = "LessonLearned";
    public const string HeavenlyKey = "Heavenly";
    public const string AltHeavenlyKey = "AltHeavenly";
    public const string MultiHitKey = "MultiHit";
    public const string MultiBlockKey = "MultiBlock";
    public const string LifestealKey = "Lifesteal";
    public const string WallopKey = "Wallop";
    public const string AutoplayKey = "Autoplay";
    public const string ReplayXKey = "ReplayX";
    public const string GatlingKey = "Gatling";
    public const string MegaGatlingKey = "MegaGatling";
    public const string BolasKey = "Bolas";
    public const string ParticleKey = "Particle";
    public const string AddRandomCardKey = "AddRandomCard";
    public const string AddCardKey = "AddCard";
    public const string AngerKey = "Anger";
    public const string BlankSlateKey = "BlankSlate";
    public const string BasicDamageKey = "BasicDamage";
    public const string BasicDamageAoeKey = "BasicDamageAoe";
    public const string BasicMultiHitKey = "BasicMultiHit";
    public const string BasicMultiHitAoeKey = "BasicMultiHitAoe";
    public const string BasicBlockKey = "BasicBlock";
    public const string BasicDrawKey = "BasicDraw";
    public const string BasicDiscardKey = "BasicDiscard";
    public const string BasicExhaustKey = "BasicExhaust";
    public const string BasicTransformKey = "BasicTransform";
    public const string BasicHealKey = "BasicHeal";
    public const string BasicGainMaxHpKey = "BasicGainMaxHp";
    public const string BasicLoseHealthKey = "BasicLoseHealth";
    public const string BasicEnergyKey = "BasicEnergy";
    public const string BasicStarsKey = "BasicStars";
    public const string BasicForgeKey = "BasicForge";
    public const string AnotherPlayerBlockKey = "BasicMultiplayerAnotherPlayerBlock";
    public const string AllOtherPlayersBlockKey = "BasicMultiplayerAllOtherPlayersBlock";
    public const string AllPlayersBlockKey = "BasicMultiplayerAllPlayersBlock";
    public const string AnotherPlayerDrawKey = "BasicMultiplayerAnotherPlayerDraw";
    public const string AllOtherPlayersDrawKey = "BasicMultiplayerAllOtherPlayersDraw";
    public const string AllPlayersDrawKey = "BasicMultiplayerAllPlayersDraw";
    public const string AnotherPlayerDiscardKey = "BasicMultiplayerAnotherPlayerDiscard";
    public const string AllOtherPlayersDiscardKey = "BasicMultiplayerAllOtherPlayersDiscard";
    public const string AllPlayersDiscardKey = "BasicMultiplayerAllPlayersDiscard";
    public const string AnotherPlayerExhaustKey = "BasicMultiplayerAnotherPlayerExhaust";
    public const string AllOtherPlayersExhaustKey = "BasicMultiplayerAllOtherPlayersExhaust";
    public const string AllPlayersExhaustKey = "BasicMultiplayerAllPlayersExhaust";
    public const string AnotherPlayerTransformKey = "BasicMultiplayerAnotherPlayerTransform";
    public const string AllOtherPlayersTransformKey = "BasicMultiplayerAllOtherPlayersTransform";
    public const string AllPlayersTransformKey = "BasicMultiplayerAllPlayersTransform";
    public const string AnotherPlayerHealKey = "BasicMultiplayerAnotherPlayerHeal";
    public const string AllOtherPlayersHealKey = "BasicMultiplayerAllOtherPlayersHeal";
    public const string AllPlayersHealKey = "BasicMultiplayerAllPlayersHeal";
    public const string AnotherPlayerGainMaxHpKey = "BasicMultiplayerAnotherPlayerGainMaxHp";
    public const string AllOtherPlayersGainMaxHpKey = "BasicMultiplayerAllOtherPlayersGainMaxHp";
    public const string AllPlayersGainMaxHpKey = "BasicMultiplayerAllPlayersGainMaxHp";
    public const string AnotherPlayerLoseHealthKey = "BasicMultiplayerAnotherPlayerLoseHealth";
    public const string AllOtherPlayersLoseHealthKey = "BasicMultiplayerAllOtherPlayersLoseHealth";
    public const string AllPlayersLoseHealthKey = "BasicMultiplayerAllPlayersLoseHealth";
    public const string AnotherPlayerEnergyKey = "BasicMultiplayerAnotherPlayerEnergy";
    public const string AllOtherPlayersEnergyKey = "BasicMultiplayerAllOtherPlayersEnergy";
    public const string AllPlayersEnergyKey = "BasicMultiplayerAllPlayersEnergy";
    public const string AnotherPlayerStarsKey = "BasicMultiplayerAnotherPlayerStars";
    public const string AllOtherPlayersStarsKey = "BasicMultiplayerAllOtherPlayersStars";
    public const string AllPlayersStarsKey = "BasicMultiplayerAllPlayersStars";
    public const string AnotherPlayerForgeKey = "BasicMultiplayerAnotherPlayerForge";
    public const string AllOtherPlayersForgeKey = "BasicMultiplayerAllOtherPlayersForge";
    public const string AllPlayersForgeKey = "BasicMultiplayerAllPlayersForge";
    public const string IncreaseDamageDealtThisTurnKey =
        "BasicMultiplierIncreaseDamageDealtThisTurn";
    public const string IncreaseDamageDealtThisCombatKey =
        "BasicMultiplierIncreaseDamageDealtThisCombat";
    public const string IncreaseDamageDealtPermanentlyKey =
        "BasicMultiplierIncreaseDamageDealtPermanently";
    public const string IncreaseMonsterDamageThisTurnKey =
        "BasicMultiplierIncreaseMonsterDamageThisTurn";
    public const string IncreaseMonsterDamageThisCombatKey =
        "BasicMultiplierIncreaseMonsterDamageThisCombat";
    public const string IncreaseMonsterDamagePermanentlyKey =
        "BasicMultiplierIncreaseMonsterDamagePermanently";
    public const string ApplyPowerKey = "ApplyPower";
    public const string ApplySelfKey = "ApplySelf";
    public const string ApplyToAllEnemiesKey = "ApplyToAllEnemies";
    public const string ApplyToAllPlayersKey = "ApplyToAllPlayers";
    public const string ApplyToAnotherPlayerKey = "ApplyToAnotherPlayer";
    public const string GainPowerPermanentlyKey =
        "GainPowerPermanently";
    public const string GainPowerPermanentlyOnFatalKey =
        "GainPowerPermanentlyOnFatal";
    public const string DiscardHandKey = "RestrictiveDiscardHand";
    public const string NoDrawKey = "RestrictiveNoDraw";
    public const string InHandLoseHealthKey =
        "RestrictiveInHandLoseHealth";
    public const string InHandTakeDamageKey =
        "RestrictiveInHandTakeDamage";
    public const string InHandLoseGoldKey = "RestrictiveInHandLoseGold";
    public const string EnthralledKey = "RestrictiveEnthralled";
    public const string ClashKey = "RestrictiveClash";
    public const string EndTurnKey = "RestrictiveEndTurn";
    public const string GrandKey = "RestrictiveGrand";
    public const string BorrowedKey = "RestrictiveBorrowed";
    public const string LoseStrengthKey = "RestrictiveLoseStrength";
    public const string LoseDexterityKey = "RestrictiveLoseDexterity";
    public const string LoseMaxHealthKey = "RestrictiveLoseMaxHealth";
    public const string FeedKey = "FatalFeed";
    public const string GreedKey = "FatalGreed";
    public const string HuntKey = "FatalHunt";
    public const string SunderKey = "FatalSunder";
    public const string AlchemyKey = "FatalAlchemy";
    public const string VintageKey = "FatalVintage";
    public const string MaxHpStealKey = "FatalMaxHpSteal";
    public const string BuffStealKey = "FatalBuffSteal";
    public const string PermanentBuffStealKey = "FatalPermanentBuffSteal";
    public const string EffectStealKey = "FatalEffectSteal";
    public const string PermanentEffectStealKey =
        "FatalPermanentEffectSteal";
    public const string PermanentFormStealKey =
        "FatalPermanentFormSteal";
    public const string PermanentFormStealStackingKey =
        "FatalPermanentFormStealStacking";
    public const string DamageOnPlayKey = "ImprovementDamageOnPlay";
    public const string DoubleDamageOnPlayKey =
        "ImprovementDoubleDamageOnPlay";
    public const string AllDamageOnPlayKey = "ImprovementAllDamageOnPlay";
    public const string PermanentDamageOnPlayKey =
        "ImprovementPermanentDamageOnPlay";
    public const string PermanentDamageOnFatalKey =
        "ImprovementPermanentDamageOnFatal";
    public const string BlockOnPlayKey = "ImprovementBlockOnPlay";
    public const string AllBlockOnPlayKey = "ImprovementAllBlockOnPlay";
    public const string PermanentBlockOnPlayKey =
        "ImprovementPermanentBlockOnPlay";
    public const string DoubleBlockOnPlayKey =
        "ImprovementDoubleBlockOnPlay";
    public const string VariablesOnPlayKey = "ImprovementVariablesOnPlay";
    public const string AllVariablesOnPlayKey =
        "ImprovementAllVariablesOnPlay";
    public const string PermanentVariablesOnPlayKey =
        "ImprovementPermanentVariablesOnPlay";
    public const string PermanentVariablesOnFatalKey =
        "ImprovementPermanentVariablesOnFatal";
    public const string DoubleVariablesOnPlayKey =
        "ImprovementDoubleVariablesOnPlay";

    [CustomEnum, KeywordProperties(AutoKeywordPosition.Before)]
    public static CardKeyword Inevitable;

    [CustomEnum, KeywordProperties(AutoKeywordPosition.Before)]
    public static CardKeyword Sticky;

    [CustomEnum, KeywordProperties(AutoKeywordPosition.Before)]
    public static CardKeyword Passing;

    [CustomEnum, KeywordProperties(AutoKeywordPosition.Before)]
    public static CardKeyword Livid;

    [CustomEnum("X_COST")]
    public static CardKeyword XCost;

    [CustomEnum("X_VALUE"), KeywordProperties(AutoKeywordPosition.Before)]
    public static CardKeyword XValue;

    [CustomEnum("ALT_X_VALUE"), KeywordProperties(AutoKeywordPosition.Before)]
    public static CardKeyword AltXValue;

    [CustomEnum("INFINITE_UPGRADE"), KeywordProperties(AutoKeywordPosition.Before)]
    public static CardKeyword InfiniteUpgrade;

    [CustomEnum("JOKE_INFINITE_UPGRADE"), KeywordProperties(AutoKeywordPosition.Before)]
    public static CardKeyword JokeInfiniteUpgrade;

    [CustomEnum("KARMIC"), KeywordProperties(AutoKeywordPosition.Before)]
    public static CardKeyword Karmic;

    [CustomEnum("LESSON_LEARNED")]
    public static CardKeyword LessonLearned;

    [CustomEnum("HEAVENLY")]
    public static CardKeyword Heavenly;

    [CustomEnum("ALT_HEAVENLY")]
    public static CardKeyword AltHeavenly;

    [CustomEnum("MULTI_HIT"),KeywordProperties(AutoKeywordPosition.Before)]
    public static CardKeyword MultiHit;

    [CustomEnum("MULTI_BLOCK"), KeywordProperties(AutoKeywordPosition.Before)]
    public static CardKeyword MultiBlock;

    [CustomEnum("LIFESTEAL")]
    public static CardKeyword Lifesteal;

    [CustomEnum("WALLOP")]
    public static CardKeyword Wallop;

    [CustomEnum("AUTOPLAY"), KeywordProperties(AutoKeywordPosition.Before)]
    public static CardKeyword Autoplay;

    [CustomEnum("REPLAY_X"),KeywordProperties(AutoKeywordPosition.After)]
    public static CardKeyword ReplayX;

    [CustomEnum("GATLING")]
    public static CardKeyword Gatling;

    [CustomEnum("MEGA_GATLING")]
    public static CardKeyword MegaGatling;

    [CustomEnum("BOLAS")]
    public static CardKeyword Bolas;

    [CustomEnum("PARTICLE")]
    public static CardKeyword Particle;

    [CustomEnum("ADD_RANDOM_CARD")]
    public static CardKeyword AddRandomCard;

    [CustomEnum("ADD_CARD")]
    public static CardKeyword AddCard;

    [CustomEnum("ANGER")]
    public static CardKeyword Anger;

    [CustomEnum("BLANK_SLATE")]
    public static CardKeyword BlankSlate;

    [CustomEnum("BASIC_DAMAGE")]
    public static CardKeyword BasicDamage;

    [CustomEnum("BASIC_DAMAGE_AOE")]
    public static CardKeyword BasicDamageAoe;

    [CustomEnum("BASIC_MULTI_HIT")]
    public static CardKeyword BasicMultiHit;

    [CustomEnum("BASIC_MULTI_HIT_AOE")]
    public static CardKeyword BasicMultiHitAoe;

    [CustomEnum("BASIC_BLOCK")]
    public static CardKeyword BasicBlock;

    [CustomEnum("BASIC_DRAW")]
    public static CardKeyword BasicDraw;

    [CustomEnum("BASIC_DISCARD")]
    public static CardKeyword BasicDiscard;

    [CustomEnum("BASIC_EXHAUST")]
    public static CardKeyword BasicExhaust;

    [CustomEnum("BASIC_TRANSFORM")]
    public static CardKeyword BasicTransform;

    [CustomEnum("BASIC_HEAL")]
    public static CardKeyword BasicHeal;

    [CustomEnum("BASIC_GAIN_MAX_HP")]
    public static CardKeyword BasicGainMaxHp;

    [CustomEnum("BASIC_LOSE_HEALTH")]
    public static CardKeyword BasicLoseHealth;

    [CustomEnum("BASIC_ENERGY")]
    public static CardKeyword BasicEnergy;

    [CustomEnum("BASIC_STARS")]
    public static CardKeyword BasicStars;

    [CustomEnum("BASIC_FORGE")]
    public static CardKeyword BasicForge;

    [CustomEnum("BASIC_MULTIPLAYER_ANOTHER_PLAYER_BLOCK")]
    public static CardKeyword AnotherPlayerBlock;

    [CustomEnum("BASIC_MULTIPLAYER_ALL_OTHER_PLAYERS_BLOCK")]
    public static CardKeyword AllOtherPlayersBlock;

    [CustomEnum("BASIC_MULTIPLAYER_ALL_PLAYERS_BLOCK")]
    public static CardKeyword AllPlayersBlock;

    [CustomEnum("BASIC_MULTIPLAYER_ANOTHER_PLAYER_DRAW")]
    public static CardKeyword AnotherPlayerDraw;

    [CustomEnum("BASIC_MULTIPLAYER_ALL_OTHER_PLAYERS_DRAW")]
    public static CardKeyword AllOtherPlayersDraw;

    [CustomEnum("BASIC_MULTIPLAYER_ALL_PLAYERS_DRAW")]
    public static CardKeyword AllPlayersDraw;

    [CustomEnum("BASIC_MULTIPLAYER_ANOTHER_PLAYER_DISCARD")]
    public static CardKeyword AnotherPlayerDiscard;

    [CustomEnum("BASIC_MULTIPLAYER_ALL_OTHER_PLAYERS_DISCARD")]
    public static CardKeyword AllOtherPlayersDiscard;

    [CustomEnum("BASIC_MULTIPLAYER_ALL_PLAYERS_DISCARD")]
    public static CardKeyword AllPlayersDiscard;

    [CustomEnum("BASIC_MULTIPLAYER_ANOTHER_PLAYER_EXHAUST")]
    public static CardKeyword AnotherPlayerExhaust;

    [CustomEnum("BASIC_MULTIPLAYER_ALL_OTHER_PLAYERS_EXHAUST")]
    public static CardKeyword AllOtherPlayersExhaust;

    [CustomEnum("BASIC_MULTIPLAYER_ALL_PLAYERS_EXHAUST")]
    public static CardKeyword AllPlayersExhaust;

    [CustomEnum("BASIC_MULTIPLAYER_ANOTHER_PLAYER_TRANSFORM")]
    public static CardKeyword AnotherPlayerTransform;

    [CustomEnum("BASIC_MULTIPLAYER_ALL_OTHER_PLAYERS_TRANSFORM")]
    public static CardKeyword AllOtherPlayersTransform;

    [CustomEnum("BASIC_MULTIPLAYER_ALL_PLAYERS_TRANSFORM")]
    public static CardKeyword AllPlayersTransform;

    [CustomEnum("BASIC_MULTIPLAYER_ANOTHER_PLAYER_HEAL")]
    public static CardKeyword AnotherPlayerHeal;

    [CustomEnum("BASIC_MULTIPLAYER_ALL_OTHER_PLAYERS_HEAL")]
    public static CardKeyword AllOtherPlayersHeal;

    [CustomEnum("BASIC_MULTIPLAYER_ALL_PLAYERS_HEAL")]
    public static CardKeyword AllPlayersHeal;

    [CustomEnum("BASIC_MULTIPLAYER_ANOTHER_PLAYER_GAIN_MAX_HP")]
    public static CardKeyword AnotherPlayerGainMaxHp;

    [CustomEnum("BASIC_MULTIPLAYER_ALL_OTHER_PLAYERS_GAIN_MAX_HP")]
    public static CardKeyword AllOtherPlayersGainMaxHp;

    [CustomEnum("BASIC_MULTIPLAYER_ALL_PLAYERS_GAIN_MAX_HP")]
    public static CardKeyword AllPlayersGainMaxHp;

    [CustomEnum("BASIC_MULTIPLAYER_ANOTHER_PLAYER_LOSE_HEALTH")]
    public static CardKeyword AnotherPlayerLoseHealth;

    [CustomEnum("BASIC_MULTIPLAYER_ALL_OTHER_PLAYERS_LOSE_HEALTH")]
    public static CardKeyword AllOtherPlayersLoseHealth;

    [CustomEnum("BASIC_MULTIPLAYER_ALL_PLAYERS_LOSE_HEALTH")]
    public static CardKeyword AllPlayersLoseHealth;

    [CustomEnum("BASIC_MULTIPLAYER_ANOTHER_PLAYER_ENERGY")]
    public static CardKeyword AnotherPlayerEnergy;

    [CustomEnum("BASIC_MULTIPLAYER_ALL_OTHER_PLAYERS_ENERGY")]
    public static CardKeyword AllOtherPlayersEnergy;

    [CustomEnum("BASIC_MULTIPLAYER_ALL_PLAYERS_ENERGY")]
    public static CardKeyword AllPlayersEnergy;

    [CustomEnum("BASIC_MULTIPLAYER_ANOTHER_PLAYER_STARS")]
    public static CardKeyword AnotherPlayerStars;

    [CustomEnum("BASIC_MULTIPLAYER_ALL_OTHER_PLAYERS_STARS")]
    public static CardKeyword AllOtherPlayersStars;

    [CustomEnum("BASIC_MULTIPLAYER_ALL_PLAYERS_STARS")]
    public static CardKeyword AllPlayersStars;

    [CustomEnum("BASIC_MULTIPLAYER_ANOTHER_PLAYER_FORGE")]
    public static CardKeyword AnotherPlayerForge;

    [CustomEnum("BASIC_MULTIPLAYER_ALL_OTHER_PLAYERS_FORGE")]
    public static CardKeyword AllOtherPlayersForge;

    [CustomEnum("BASIC_MULTIPLAYER_ALL_PLAYERS_FORGE")]
    public static CardKeyword AllPlayersForge;

    [CustomEnum("BASIC_MULTIPLIER_INCREASE_DAMAGE_DEALT_THIS_TURN")]
    public static CardKeyword IncreaseDamageDealtThisTurn;

    [CustomEnum("BASIC_MULTIPLIER_INCREASE_DAMAGE_DEALT_THIS_COMBAT")]
    public static CardKeyword IncreaseDamageDealtThisCombat;

    [CustomEnum("BASIC_MULTIPLIER_INCREASE_DAMAGE_DEALT_PERMANENTLY")]
    public static CardKeyword IncreaseDamageDealtPermanently;

    [CustomEnum("BASIC_MULTIPLIER_INCREASE_MONSTER_DAMAGE_THIS_TURN")]
    public static CardKeyword IncreaseMonsterDamageThisTurn;

    [CustomEnum("BASIC_MULTIPLIER_INCREASE_MONSTER_DAMAGE_THIS_COMBAT")]
    public static CardKeyword IncreaseMonsterDamageThisCombat;

    [CustomEnum("BASIC_MULTIPLIER_INCREASE_MONSTER_DAMAGE_PERMANENTLY")]
    public static CardKeyword IncreaseMonsterDamagePermanently;

    [CustomEnum("APPLY_POWER")]
    public static CardKeyword ApplyPower;

    [CustomEnum("APPLY_SELF")]
    public static CardKeyword ApplySelf;

    [CustomEnum("APPLY_TO_ALL_ENEMIES")]
    public static CardKeyword ApplyToAllEnemies;

    [CustomEnum("APPLY_TO_ALL_PLAYERS")]
    public static CardKeyword ApplyToAllPlayers;

    [CustomEnum("APPLY_TO_ANOTHER_PLAYER")]
    public static CardKeyword ApplyToAnotherPlayer;

    [CustomEnum("GAIN_POWER_PERMANENTLY")]
    public static CardKeyword GainPowerPermanently;

    [CustomEnum("GAIN_POWER_PERMANENTLY_ON_FATAL")]
    public static CardKeyword GainPowerPermanentlyOnFatal;

    [CustomEnum("RESTRICTIVE_DISCARD_HAND")]
    public static CardKeyword DiscardHand;

    [CustomEnum("RESTRICTIVE_NO_DRAW")]
    public static CardKeyword NoDraw;

    [CustomEnum("RESTRICTIVE_IN_HAND_LOSE_HEALTH")]
    public static CardKeyword InHandLoseHealth;

    [CustomEnum("RESTRICTIVE_IN_HAND_TAKE_DAMAGE")]
    public static CardKeyword InHandTakeDamage;

    [CustomEnum("RESTRICTIVE_IN_HAND_LOSE_GOLD")]
    public static CardKeyword InHandLoseGold;

    [CustomEnum("RESTRICTIVE_ENTHRALLED")]
    public static CardKeyword Enthralled;

    [CustomEnum("RESTRICTIVE_CLASH")]
    public static CardKeyword Clash;

    [CustomEnum("RESTRICTIVE_END_TURN")]
    public static CardKeyword EndTurn;

    [CustomEnum("RESTRICTIVE_GRAND")]
    public static CardKeyword Grand;

    [CustomEnum("RESTRICTIVE_BORROWED")]
    public static CardKeyword Borrowed;

    [CustomEnum("RESTRICTIVE_LOSE_STRENGTH")]
    public static CardKeyword LoseStrength;

    [CustomEnum("RESTRICTIVE_LOSE_DEXTERITY")]
    public static CardKeyword LoseDexterity;

    [CustomEnum("RESTRICTIVE_LOSE_MAX_HEALTH")]
    public static CardKeyword LoseMaxHealth;

    [CustomEnum("IMPROVEMENT_DAMAGE_ON_PLAY")]
    public static CardKeyword DamageOnPlay;

    [CustomEnum("IMPROVEMENT_DOUBLE_DAMAGE_ON_PLAY")]
    public static CardKeyword DoubleDamageOnPlay;

    [CustomEnum("IMPROVEMENT_ALL_DAMAGE_ON_PLAY")]
    public static CardKeyword AllDamageOnPlay;

    [CustomEnum("IMPROVEMENT_PERMANENT_DAMAGE_ON_PLAY")]
    public static CardKeyword PermanentDamageOnPlay;

    [CustomEnum("IMPROVEMENT_PERMANENT_DAMAGE_ON_FATAL")]
    public static CardKeyword PermanentDamageOnFatal;

    [CustomEnum("IMPROVEMENT_BLOCK_ON_PLAY")]
    public static CardKeyword BlockOnPlay;

    [CustomEnum("IMPROVEMENT_ALL_BLOCK_ON_PLAY")]
    public static CardKeyword AllBlockOnPlay;

    [CustomEnum("IMPROVEMENT_PERMANENT_BLOCK_ON_PLAY")]
    public static CardKeyword PermanentBlockOnPlay;

    [CustomEnum("IMPROVEMENT_DOUBLE_BLOCK_ON_PLAY")]
    public static CardKeyword DoubleBlockOnPlay;

    [CustomEnum("IMPROVEMENT_VARIABLES_ON_PLAY")]
    public static CardKeyword VariablesOnPlay;

    [CustomEnum("IMPROVEMENT_ALL_VARIABLES_ON_PLAY")]
    public static CardKeyword AllVariablesOnPlay;

    [CustomEnum("IMPROVEMENT_PERMANENT_VARIABLES_ON_PLAY")]
    public static CardKeyword PermanentVariablesOnPlay;

    [CustomEnum("IMPROVEMENT_PERMANENT_VARIABLES_ON_FATAL")]
    public static CardKeyword PermanentVariablesOnFatal;

    [CustomEnum("IMPROVEMENT_DOUBLE_VARIABLES_ON_PLAY")]
    public static CardKeyword DoubleVariablesOnPlay;

    [CustomEnum("FATAL_FEED")]
    public static CardKeyword Feed;

    [CustomEnum("FATAL_GREED")]
    public static CardKeyword Greed;

    [CustomEnum("FATAL_HUNT")]
    public static CardKeyword Hunt;

    [CustomEnum("FATAL_SUNDER")]
    public static CardKeyword Sunder;

    [CustomEnum("FATAL_ALCHEMY")]
    public static CardKeyword Alchemy;

    [CustomEnum("FATAL_VINTAGE")]
    public static CardKeyword Vintage;

    [CustomEnum("FATAL_MAX_HP_STEAL")]
    public static CardKeyword MaxHpSteal;

    [CustomEnum("FATAL_BUFF_STEAL")]
    public static CardKeyword BuffSteal;

    [CustomEnum("FATAL_PERMANENT_BUFF_STEAL")]
    public static CardKeyword PermanentBuffSteal;

    [CustomEnum("FATAL_EFFECT_STEAL")]
    public static CardKeyword EffectSteal;

    [CustomEnum("FATAL_PERMANENT_EFFECT_STEAL")]
    public static CardKeyword PermanentEffectSteal;

    [CustomEnum("FATAL_PERMANENT_FORM_STEAL")]
    public static CardKeyword PermanentFormSteal;

    [CustomEnum("FATAL_PERMANENT_FORM_STEAL_STACKING")]
    public static CardKeyword PermanentFormStealStacking;

    public static IEnumerable<CardKeyword> All =>
        LoadoutKeywordRegistry.All.Select(model => model.Keyword);

    public static bool Has(CardModel? card, CardKeyword keyword)
    {
        return card is not null
               && keyword != CardKeyword.None
               && (card.GetKeywordsWithSources(KeywordSources.Local).Contains(keyword)
                   || (keyword == MultiHit && Powers.MultiHitPower.GrantsKeyword(card)));
    }

    public static string GetStorageKey(CardKeyword keyword)
    {
        return LoadoutKeywordRegistry.TryGet(keyword, out LoadoutKeywordModel model)
            ? model.StorageKey
            : keyword.ToString();
    }

    public static bool TryResolve(string? key, out CardKeyword keyword)
    {
        string? normalized = key?.Trim();
        if (normalized is not null && LoadoutKeywordRegistry.TryGet(normalized, out LoadoutKeywordModel model))
        {
            keyword = model.Keyword;
            return keyword != CardKeyword.None;
        }

        if (Enum.TryParse(normalized, ignoreCase: true, out keyword)
            && keyword != CardKeyword.None)
            return true;

        keyword = CardKeyword.None;
        return false;
    }
}
