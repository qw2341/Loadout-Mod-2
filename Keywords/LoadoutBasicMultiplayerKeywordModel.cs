#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

public enum LoadoutBasicMultiplayerTargetMode
{
    AnotherPlayer,
    AllPlayers
}

public enum LoadoutBasicMultiplayerEffect
{
    Block,
    Draw,
    Discard,
    Exhaust,
    Transform,
    Heal,
    GainMaxHp,
    LoseHealth,
    Energy,
    Stars
}

public abstract class LoadoutBasicMultiplayerKeywordModel
    : LoadoutBasicKeywordModel
{
    private IReadOnlyList<LoadoutKeywordDynamicVarDefinition>? _dynamicVars;

    public abstract LoadoutBasicMultiplayerTargetMode TargetMode { get; }

    public abstract LoadoutBasicMultiplayerEffect Effect { get; }

    public abstract string AmountVarName { get; }

    public abstract string AmountLabelLocKey { get; }

    public override LoadoutKeywordEditorSection EditorSection =>
        LoadoutKeywordEditorSection.BasicMultiplayer;

    public override bool ChangesTargeting =>
        TargetMode == LoadoutBasicMultiplayerTargetMode.AnotherPlayer;

    public override bool RequiresAnotherPlayerTarget => ChangesTargeting;

    public override bool ReportsGainsBlock =>
        Effect == LoadoutBasicMultiplayerEffect.Block;

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        _dynamicVars ??=
        [
            new(
                AmountVarName,
                1m,
                int.MinValue,
                int.MaxValue,
                AmountLabelLocKey,
                CreateDynamicVar)
        ];

    public override async Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        foreach (Creature target in ResolveTargets(card, cardPlay))
            await ApplyToTarget(card, choiceContext, cardPlay, target);
    }

    private DynamicVar CreateDynamicVar(string name, decimal value)
    {
        return Effect switch
        {
            LoadoutBasicMultiplayerEffect.Block =>
                new BlockVar(name, value, ValueProp.Move),
            LoadoutBasicMultiplayerEffect.Draw
                or LoadoutBasicMultiplayerEffect.Discard
                or LoadoutBasicMultiplayerEffect.Exhaust
                or LoadoutBasicMultiplayerEffect.Transform =>
                new CardsVar(name, decimal.ToInt32(value)),
            LoadoutBasicMultiplayerEffect.Heal => new HealVar(name, value),
            LoadoutBasicMultiplayerEffect.GainMaxHp => new MaxHpVar(name, value),
            LoadoutBasicMultiplayerEffect.LoseHealth => new HpLossVar(name, value),
            LoadoutBasicMultiplayerEffect.Energy =>
                new EnergyVar(name, decimal.ToInt32(value)),
            LoadoutBasicMultiplayerEffect.Stars =>
                new StarsVar(name, decimal.ToInt32(value)),
            _ => new DynamicVar(name, value)
        };
    }

    private IReadOnlyList<Creature> ResolveTargets(
        CardModel card,
        CardPlay cardPlay)
    {
        Creature source = card.Owner.Creature;
        if (TargetMode == LoadoutBasicMultiplayerTargetMode.AnotherPlayer)
        {
            return cardPlay.Target is { IsAlive: true, IsPlayer: true } target
                   && !ReferenceEquals(target, source)
                   && target.Side == source.Side
                ? [target]
                : [];
        }

        return source.CombatState?.GetTeammatesOf(source)
                   .Where(target => target.IsAlive && target.IsPlayer)
                   .ToArray()
               ?? [];
    }

    private async Task ApplyToTarget(
        CardModel sourceCard,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        Creature target)
    {
        DynamicVar amountVar = GetAmount(sourceCard, AmountVarName);
        decimal amount = Math.Max(0m, amountVar.BaseValue);
        if (amount <= 0m)
            return;

        Player? targetPlayer = target.Player;
        if (targetPlayer is null)
            return;
        switch (Effect)
        {
            case LoadoutBasicMultiplayerEffect.Block:
                if (amountVar is BlockVar block)
                    await CreatureCmd.GainBlock(target, block, cardPlay);
                break;
            case LoadoutBasicMultiplayerEffect.Draw:
                await CardPileCmd.Draw(choiceContext, amountVar.IntValue, targetPlayer);
                break;
            case LoadoutBasicMultiplayerEffect.Discard:
                await SelectAndDiscard(sourceCard, choiceContext, targetPlayer, amountVar.IntValue);
                break;
            case LoadoutBasicMultiplayerEffect.Exhaust:
                await SelectAndExhaust(sourceCard, choiceContext, targetPlayer, amountVar.IntValue);
                break;
            case LoadoutBasicMultiplayerEffect.Transform:
                await SelectAndTransform(sourceCard, choiceContext, targetPlayer, amountVar.IntValue);
                break;
            case LoadoutBasicMultiplayerEffect.Heal:
                await CreatureCmd.Heal(target, amount);
                break;
            case LoadoutBasicMultiplayerEffect.GainMaxHp:
                await CreatureCmd.GainMaxHp(target, amount);
                break;
            case LoadoutBasicMultiplayerEffect.LoseHealth:
                await CreatureCmd.Damage(
                    choiceContext,
                    target,
                    amount,
                    ValueProp.Unblockable | ValueProp.Unpowered | ValueProp.Move,
                    sourceCard,
                    cardPlay);
                break;
            case LoadoutBasicMultiplayerEffect.Energy:
                await PlayerCmd.GainEnergy(amount, targetPlayer);
                break;
            case LoadoutBasicMultiplayerEffect.Stars:
                await PlayerCmd.GainStars(amount, targetPlayer);
                break;
        }
    }

    private static async Task SelectAndDiscard(
        CardModel sourceCard,
        PlayerChoiceContext choiceContext,
        Player target,
        int requested)
    {
        int amount = GetHandCount(target, requested);
        if (amount == 0)
            return;

        IEnumerable<CardModel> selection = await CardSelectCmd.FromHandForDiscard(
            choiceContext,
            target,
            new CardSelectorPrefs(CardSelectorPrefs.DiscardSelectionPrompt, amount),
            null,
            sourceCard);
        await CardCmd.Discard(choiceContext, selection);
    }

    private static async Task SelectAndExhaust(
        CardModel sourceCard,
        PlayerChoiceContext choiceContext,
        Player target,
        int requested)
    {
        int amount = GetHandCount(target, requested);
        if (amount == 0)
            return;

        IEnumerable<CardModel> selection = await CardSelectCmd.FromHand(
            choiceContext,
            target,
            new CardSelectorPrefs(CardSelectorPrefs.ExhaustSelectionPrompt, amount),
            null,
            sourceCard);
        foreach (CardModel selected in selection.ToList())
            await CardCmd.Exhaust(choiceContext, selected);
    }

    private static async Task SelectAndTransform(
        CardModel sourceCard,
        PlayerChoiceContext choiceContext,
        Player target,
        int requested)
    {
        int amount = Math.Min(
            Math.Max(0, requested),
            PileType.Hand.GetPile(target).Cards.Count(card => card.IsTransformable));
        if (amount == 0)
            return;

        IReadOnlyList<CardModel> selection = (await CardSelectCmd.FromHand(
            choiceContext,
            target,
            new CardSelectorPrefs(CardSelectorPrefs.TransformSelectionPrompt, amount),
            card => card.IsTransformable,
            sourceCard)).ToList();
        await CardCmd.Transform(
            selection.Select(card => new CardTransformation(card)),
            target.RunState.Rng.CombatCardSelection);
    }

    private static int GetHandCount(Player player, int requested)
    {
        return Math.Min(
            Math.Max(0, requested),
            PileType.Hand.GetPile(player).Cards.Count);
    }
}
