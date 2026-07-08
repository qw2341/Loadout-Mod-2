#nullable enable

namespace Loadout.Patches.TildeKey;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using Loadout.Services.Actions;
using Loadout.Services.TildeKey;
using MegaCrit.Sts2.Core.Audio.Debug;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyHandDraw))]
public static class TildeKeyModifyHandDrawPatch
{
    [HarmonyPostfix]
    public static void Postfix(Player player, ref decimal __result)
    {
        if (TildeKeyStateService.TryGetDrawPerTurnOverride(player, out int drawPerTurn))
            __result = drawPerTurn;
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyDamage))]
public static class TildeKeyModifyDamagePatch
{
    private static readonly System.Threading.AsyncLocal<int> SuppressDepth = new();

    [HarmonyPostfix]
    public static void Postfix(
        IRunState runState,
        ICombatState? combatState,
        Creature? target,
        Creature? dealer,
        ValueProp props,
        CardModel? cardSource,
        ModifyDamageHookType modifyDamageHookType,
        CardPreviewMode previewMode,
        ref decimal __result)
    {
        if (SuppressDepth.Value > 0 || !modifyDamageHookType.HasFlag(ModifyDamageHookType.Multiplicative))
            return;

        if (!TryGetMultiplier(target, dealer, out int multiplier) || multiplier == 100)
            return;

        decimal multiplied = __result * multiplier / 100m;
        SuppressDepth.Value++;
        try
        {
            __result = Hook.ModifyDamage(
                runState,
                combatState,
                target,
                dealer,
                multiplied,
                props,
                cardSource,
                ModifyDamageHookType.Cap,
                previewMode,
                out _);
        }
        finally
        {
            SuppressDepth.Value = Math.Max(0, SuppressDepth.Value - 1);
        }
    }

    private static bool TryGetMultiplier(Creature? target, Creature? dealer, out int multiplier)
    {
        Player? dealerOwner = dealer?.Player ?? dealer?.PetOwner;
        if (dealerOwner is not null && TildeKeyStateService.TryGetPlayerDamageMultiplier(dealerOwner, out multiplier))
            return true;

        Player? targetOwner = target?.Player ?? target?.PetOwner;
        if (dealer?.IsMonster == true
            && targetOwner is not null
            && TildeKeyStateService.TryGetEnemyDamageMultiplier(targetOwner, out multiplier))
        {
            return true;
        }

        multiplier = 100;
        return false;
    }
}

[HarmonyPatch(typeof(CombatManager), "SetupPlayerTurn")]
public static class TildeKeySetupPlayerTurnHandLimitPatch
{
    [HarmonyPrefix]
    public static void Prefix(Player player, out IDisposable? __state)
    {
        __state = TildeKeyHandLimitPatchHelpers.BeginHandLimitOverride(player);
    }

    [HarmonyPostfix]
    public static void Postfix(ref Task __result, IDisposable? __state)
    {
        if (__state is not null)
            __result = TildeKeyHandLimitPatchHelpers.DisposeAfter(__result, __state);
    }
}

[HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.Draw), typeof(PlayerChoiceContext), typeof(decimal), typeof(Player), typeof(bool))]
public static class TildeKeyDrawHandLimitPatch
{
    [HarmonyPrefix]
    public static bool Prefix(
        PlayerChoiceContext choiceContext,
        decimal count,
        Player player,
        bool fromHandDraw,
        ref Task<IEnumerable<CardModel>> __result)
    {
        if (LoadoutCardAddRules.ShouldIgnoreHandLimit
            || !TildeKeyStateService.TryGetHandSizeOverride(player, out int handSize)
            || handSize == CardPile.MaxCardsInHand)
            return true;

        __result = DrawWithEffectiveHandSize(choiceContext, count, player, fromHandDraw, handSize);
        return false;
    }

    private static async Task<IEnumerable<CardModel>> DrawWithEffectiveHandSize(
        PlayerChoiceContext choiceContext,
        decimal count,
        Player player,
        bool fromHandDraw,
        int handSize)
    {
        if (CombatManager.Instance.IsOverOrEnding)
            return Array.Empty<CardModel>();

        ICombatState? combatState = player.Creature.CombatState;
        if (combatState is null)
            return Array.Empty<CardModel>();

        if (!Hook.ShouldDraw(combatState, player, fromHandDraw, out AbstractModel? modifier))
        {
            if (modifier is not null)
                await Hook.AfterPreventingDraw(combatState, modifier);
            return Array.Empty<CardModel>();
        }

        List<CardModel> result = [];
        CardPile hand = PileType.Hand.GetPile(player);
        CardPile drawPile = PileType.Draw.GetPile(player);
        int drawsRequested = count > 0m ? (int)Math.Ceiling(count) : 0;
        if (drawsRequested == 0)
            return result;

        int handSpace = Math.Max(0, handSize - hand.Cards.Count);
        if (handSpace == 0)
        {
            CheckIfDrawIsPossibleAndShowThoughtBubbleIfNot(player, handSize);
            return result;
        }

        for (int i = 0; i < drawsRequested; i++)
        {
            if (handSpace <= 0 || CombatManager.Instance.IsOverOrEnding)
                break;

            if (!CheckIfDrawIsPossibleAndShowThoughtBubbleIfNot(player, handSize))
                break;

            await CardPileCmd.ShuffleIfNecessary(choiceContext, player);
            if (!CheckIfDrawIsPossibleAndShowThoughtBubbleIfNot(player, handSize))
                break;

            CardModel? card = drawPile.Cards.FirstOrDefault();
            if (card is null || hand.Cards.Count >= handSize)
                break;

            result.Add(card);
            using (LoadoutCardAddRules.OverrideHandLimit(handSize))
                await CardPileCmd.Add(card, hand);

            CombatManager.Instance.History.CardDrawn(combatState, card, fromHandDraw);
            await Hook.AfterCardDrawn(combatState, choiceContext, card, fromHandDraw);
            card.InvokeDrawn();
            NDebugAudioManager.Instance?.Play("card_deal.mp3", 0.25f, PitchVariance.Small);
            handSpace = Math.Max(0, handSize - hand.Cards.Count);
        }

        return result;
    }

    internal static bool CheckIfDrawIsPossibleAndShowThoughtBubbleIfNot(Player player, int handSize)
    {
        if (PileType.Draw.GetPile(player).Cards.Count + PileType.Discard.GetPile(player).Cards.Count == 0)
        {
            ThinkCmd.Play(new LocString("combat_messages", "NO_DRAW"), player.Creature, 2.0);
            return false;
        }

        if (PileType.Hand.GetPile(player).Cards.Count >= handSize)
        {
            ThinkCmd.Play(new LocString("combat_messages", "HAND_FULL"), player.Creature, 2.0);
            return false;
        }

        return true;
    }
}

[HarmonyPatch(typeof(CardPileCmd), "CheckIfDrawIsPossibleAndShowThoughtBubbleIfNot")]
public static class TildeKeyDrawPossibleHandLimitPatch
{
    [HarmonyPrefix]
    public static bool Prefix(Player player, ref bool __result)
    {
        if (LoadoutCardAddRules.ShouldIgnoreHandLimit
            || !TildeKeyStateService.TryGetHandSizeOverride(player, out int handSize)
            || handSize == CardPile.MaxCardsInHand)
            return true;

        __result = TildeKeyDrawHandLimitPatch.CheckIfDrawIsPossibleAndShowThoughtBubbleIfNot(player, handSize);
        return false;
    }
}

[HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.Add), typeof(IEnumerable<CardModel>), typeof(CardPile), typeof(CardPilePosition), typeof(AbstractModel), typeof(bool))]
public static class TildeKeyAddToHandLimitPatch
{
    [HarmonyPrefix]
    public static void Prefix(IEnumerable<CardModel> cards, CardPile newPile, out IDisposable? __state)
    {
        __state = null;
        if (newPile.Type != PileType.Hand)
            return;

        CardModel? firstCard = cards.FirstOrDefault();
        if (firstCard?.Owner is not null)
            __state = TildeKeyHandLimitPatchHelpers.BeginHandLimitOverride(firstCard.Owner);
    }

    [HarmonyPostfix]
    public static void Postfix(ref Task<IReadOnlyList<CardPileAddResult>> __result, IDisposable? __state)
    {
        if (__state is not null)
            __result = TildeKeyHandLimitPatchHelpers.DisposeAfter(__result, __state);
    }
}

[HarmonyPatch(typeof(NClickableControl), nameof(NClickableControl._GuiInput))]
public static class TildeKeyRelicCounterInputPatch
{
    [HarmonyPrefix]
    public static bool Prefix(NClickableControl __instance, InputEvent inputEvent)
    {
        if (__instance is not NRelicInventoryHolder holder
            || inputEvent is not InputEventMouseButton mouseButton
            || !TildeKeyStateService.IsScrollRelicCounterEnabledForLocalPlayer())
        {
            return true;
        }

        RelicModel relic = holder.Relic.Model;
        if (!TildeKeyStateService.TryGetRelicCounterMember(relic, out string counterMember))
            return true;

        if (mouseButton.Pressed && mouseButton.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            __instance.AcceptEvent();
            if (!TildeKeyStateService.IsRelicCounterLocked(relic, counterMember))
            {
                int delta = mouseButton.ButtonIndex == MouseButton.WheelUp ? 1 : -1;
                LoadoutImmediateMutationService.RequestTildeRelicCounterDelta(relic, delta, counterMember);
            }

            return false;
        }

        if (mouseButton.ButtonIndex == MouseButton.Middle)
        {
            __instance.AcceptEvent();
            if (mouseButton.Pressed)
                return false;

            bool isLocked = TildeKeyStateService.IsRelicCounterLocked(relic, counterMember);
            if (!isLocked && !TildeKeyStateService.TryGetRelicCounterValue(relic, counterMember, out _))
                return false;

            int value = TildeKeyStateService.TryGetRelicCounterValue(relic, counterMember, out int currentValue)
                ? currentValue
                : 0;
            LoadoutImmediateMutationService.RequestTildeRelicCounterLock(relic, counterMember, value, !isLocked);
            return false;
        }

        return true;
    }
}

[HarmonyPatch(typeof(NRelicInventoryHolder), nameof(NRelicInventoryHolder._Ready))]
public static class TildeKeyRelicCounterBadgeReadyPatch
{
    [HarmonyPostfix]
    public static void Postfix(NRelicInventoryHolder __instance)
    {
        TildeKeyStateService.RefreshRelicCounterLockBadge(__instance);
    }
}

[HarmonyPatch(typeof(NRelicInventoryHolder), "OnModelChanged")]
public static class TildeKeyRelicCounterBadgeModelChangedPatch
{
    [HarmonyPostfix]
    public static void Postfix(NRelicInventoryHolder __instance)
    {
        TildeKeyStateService.RefreshRelicCounterLockBadge(__instance);
    }
}

[HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Damage), typeof(PlayerChoiceContext), typeof(IEnumerable<Creature>), typeof(decimal), typeof(ValueProp), typeof(Creature), typeof(CardModel))]
public static class TildeKeyGodmodeDamagePatch
{
    [HarmonyPrefix]
    public static bool Prefix(ref IEnumerable<Creature> targets, ValueProp props, ref Task<IEnumerable<DamageResult>> __result)
    {
        List<Creature> targetList = targets.ToList();
        if (targetList.Count == 0)
            return true;

        List<Creature> unprotectedTargets = targetList
            .Where(target => !TildeKeyStateService.IsGodmodeProtected(target))
            .ToList();
        if (unprotectedTargets.Count == targetList.Count)
            return true;

        if (unprotectedTargets.Count == 0)
        {
            __result = Task.FromResult<IEnumerable<DamageResult>>(Array.Empty<DamageResult>());
            return false;
        }

        targets = unprotectedTargets;
        return true;
    }
}

[HarmonyPatch(typeof(Creature), nameof(Creature.LoseHpInternal))]
public static class TildeKeyGodmodeLoseHpPatch
{
    [HarmonyPrefix]
    public static bool Prefix(Creature __instance, ValueProp props, ref DamageResult __result)
    {
        if (!TildeKeyStateService.IsGodmodeProtected(__instance))
            return true;

        __result = new DamageResult(__instance, props);
        return false;
    }
}

[HarmonyPatch(typeof(CreatureCmd), "KillWithoutCheckingWinCondition")]
public static class TildeKeyGodmodeKillPatch
{
    [HarmonyPrefix]
    public static bool Prefix(Creature creature, ref Task __result)
    {
        if (!TildeKeyStateService.IsGodmodeProtected(creature))
            return true;

        __result = Task.CompletedTask;
        return false;
    }
}

internal static class TildeKeyHandLimitPatchHelpers
{
    public static IDisposable? BeginHandLimitOverride(Player player)
    {
        if (LoadoutCardAddRules.ShouldIgnoreHandLimit)
            return null;

        return TildeKeyStateService.TryGetHandSizeOverride(player, out int handSize)
            ? LoadoutCardAddRules.OverrideHandLimit(handSize)
            : null;
    }

    public static async Task DisposeAfter(Task task, IDisposable scope)
    {
        try
        {
            await task;
        }
        finally
        {
            scope.Dispose();
        }
    }

    public static async Task<T> DisposeAfter<T>(Task<T> task, IDisposable scope)
    {
        try
        {
            return await task;
        }
        finally
        {
            scope.Dispose();
        }
    }
}
