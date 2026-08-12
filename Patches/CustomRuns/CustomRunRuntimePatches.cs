#nullable enable

namespace Loadout.Patches.CustomRuns;

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System.Threading.Tasks;
using BaseLib.Patches.Saves;
using HarmonyLib;
using Loadout.Services.CustomRuns.Compilation;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.CustomRuns.Networking;
using Loadout.Services.CustomRuns.Runtime;
using static Loadout.Patches.CustomRuns.CustomRunTriggerCapture;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;
using MegaCrit.Sts2.Core.ValueProps;

[HarmonyPatch]
public static class CustomRunPlayerCreationPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
                   typeof(Player),
                   nameof(Player.CreateForNewRun),
                   [typeof(CharacterModel), typeof(UnlockState), typeof(ulong)])
               ?? throw new MissingMethodException(typeof(Player).FullName, nameof(Player.CreateForNewRun));
    }

    [HarmonyPostfix]
    public static void Postfix(Player __result)
    {
        if (!CustomRunRuntimeSnapshotService.TryGetPendingPlayerSetup(__result.NetId, out ResolvedPlayerSetup setup))
            return;
        try
        {
            CustomRunSetupApplyService.ApplyToNewPlayer(__result, setup);
        }
        catch (Exception exception)
        {
            MainFile.Logger.Error($"[Loadout] Custom Run setup failed for player {__result.NetId}: {exception}");
        }
    }
}

[HarmonyPatch(typeof(RunState), nameof(RunState.CreateForNewRun))]
public static class CustomRunStateCreationPatch
{
    [HarmonyPrefix]
    public static void Prefix(ref int ascensionLevel)
    {
        if (CustomRunRuntimeSnapshotService.PendingSnapshot?.AscensionLevel is int customAscension)
            ascensionLevel = Math.Clamp(customAscension, 0, 10);
    }

    [HarmonyPostfix]
    public static void Postfix(RunState __result)
    {
        CustomRunRuntimeSnapshotService.AttachPending(__result);
    }
}

[HarmonyPatch(typeof(StartRunLobby), nameof(StartRunLobby.CleanUp))]
public static class CustomRunLobbyCleanupPatch
{
    [HarmonyPostfix]
    public static void Postfix(StartRunLobby __instance, bool disconnectSession)
    {
        if (disconnectSession)
            CustomRunLobbyService.CancelPreparedRun(__instance);
        else
            CustomRunLobbyService.CompletePreparedRun(__instance);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyHandDraw))]
public static class CustomRunHandDrawPatch
{
    [HarmonyPrefix]
    public static void Prefix(Player __1, ref decimal __2)
    {
        if (CustomRunRuntimeSnapshotService.TryGetPlayerSetup(__1, out ResolvedPlayerSetup setup)
            && setup.CardsDrawnPerTurn.HasValue)
        {
            __2 = setup.CardsDrawnPerTurn.Value;
        }
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.Launch))]
public static class CustomRunRuleRuntimeLaunchPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        CustomRunRuleRuntimeService.PrepareRunLaunch();
    }

    [HarmonyPostfix]
    public static void Postfix()
    {
        CustomRunSetupApplyService.ApplyInitialRuntimeSetup();
        CustomRunRuleRuntimeService.OnRunLaunched();
    }
}

[HarmonyPatch]
public static class CustomRunPostAscensionSetupPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(RunManager), "InitializeNewRun")
               ?? throw new MissingMethodException(typeof(RunManager).FullName, "InitializeNewRun");
    }

    [HarmonyPostfix]
    public static void Postfix(RunManager __instance)
    {
        RunState? runState = __instance.DebugOnlyGetState();
        if (runState is not null)
            CustomRunSetupApplyService.ApplyPostAscensionSetup(runState);
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
public static class CustomRunRuleRuntimeCleanupPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        CustomRunRuleRuntimeService.OnRunCleaningUp();
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.OnEnded))]
public static class CustomRunRuleRuntimeRunEndPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        CustomRunRuleRuntimeService.Capture("Loadout2:RunEnd", 0);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardPlayed))]
public static class CustomRunAfterCardPlayedPatch
{
    [HarmonyPostfix]
    public static void Postfix(CardPlay cardPlay, ref Task __result)
    {
        __result = CaptureAfterAsync(__result, "Loadout2:CardPlayed", cardPlay.Card.Owner.NetId,
            SelectionModelKind.Card, cardPlay.Card.Id.ToString());
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.BeforeCombatStart))]
public static class CustomRunBeforeCombatStartPatch
{
    [HarmonyPostfix]
    public static void Postfix(ICombatState combatState, ref Task __result)
    {
        __result = CaptureCombatStartAsync(__result, combatState);
    }

    private static async Task CaptureCombatStartAsync(Task nativeTask, ICombatState combatState)
    {
        await nativeTask;
        CustomRunRuleRuntimeService.OnNativeCombatStarted(combatState);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardDrawn))]
public static class CustomRunAfterCardDrawnPatch
{
    [HarmonyPostfix]
    public static void Postfix(CardModel card, ref Task __result)
    {
        __result = CaptureAfterAsync(__result, "Loadout2:CardDrawn", card.Owner.NetId,
            SelectionModelKind.Card, card.Id.ToString());
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardDiscarded))]
public static class CustomRunAfterCardDiscardedPatch
{
    [HarmonyPostfix]
    public static void Postfix(CardModel card, ref Task __result)
    {
        __result = CaptureAfterAsync(__result, "Loadout2:CardDiscarded", card.Owner.NetId,
            SelectionModelKind.Card, card.Id.ToString());
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardExhausted))]
public static class CustomRunAfterCardExhaustedPatch
{
    [HarmonyPostfix]
    public static void Postfix(CardModel card, ref Task __result)
    {
        __result = CaptureAfterAsync(__result, "Loadout2:CardExhausted", card.Owner.NetId,
            SelectionModelKind.Card, card.Id.ToString());
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardGeneratedForCombat))]
public static class CustomRunAfterCardGeneratedPatch
{
    [HarmonyPostfix]
    public static void Postfix(CardModel card, Player? creator, ref Task __result)
    {
        __result = CaptureAfterAsync(__result, "Loadout2:CardGenerated", creator?.NetId ?? card.Owner.NetId,
            SelectionModelKind.Card, card.Id.ToString());
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterDamageReceived))]
public static class CustomRunAfterDamageReceivedPatch
{
    [HarmonyPostfix]
    public static void Postfix(Creature target, DamageResult result, ref Task __result)
    {
        if (target.Player is not { } player || result.UnblockedDamage <= 0)
            return;
        __result = CaptureAfterAsync(__result, "Loadout2:PlayerTakesDamage", player.NetId,
            amount: result.UnblockedDamage);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterBlockGained))]
public static class CustomRunAfterBlockGainedPatch
{
    [HarmonyPostfix]
    public static void Postfix(Creature creature, decimal amount, ref Task __result)
    {
        if (creature.Player is not { } player || amount <= 0m)
            return;
        __result = CaptureAfterAsync(__result, "Loadout2:PlayerGainsBlock", player.NetId, amount: (double)amount);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPowerAmountChanged))]
public static class CustomRunAfterPowerChangedPatch
{
    [HarmonyPostfix]
    public static void Postfix(PowerModel power, decimal amount, ref Task __result)
    {
        if (power.Owner.Player is not { } player || amount <= 0m)
            return;
        __result = CaptureAfterAsync(__result, "Loadout2:PowerReceived", player.NetId,
            SelectionModelKind.Power, power.Id.ToString(), (double)amount);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPotionUsed))]
public static class CustomRunAfterPotionUsedPatch
{
    [HarmonyPostfix]
    public static void Postfix(PotionModel potion, ref Task __result)
    {
        __result = CaptureAfterAsync(__result, "Loadout2:PotionUsed", potion.Owner.NetId,
            SelectionModelKind.Potion, potion.Id.ToString());
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPotionProcured))]
public static class CustomRunAfterPotionProcuredPatch
{
    [HarmonyPostfix]
    public static void Postfix(PotionModel potion, ref Task __result)
    {
        __result = CaptureAfterAsync(__result, "Loadout2:PotionObtained", potion.Owner.NetId,
            SelectionModelKind.Potion, potion.Id.ToString());
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterEnergySpent))]
public static class CustomRunAfterEnergySpentPatch
{
    [HarmonyPostfix]
    public static void Postfix(CardModel card, int amount, ref Task __result)
    {
        if (amount <= 0)
            return;
        __result = CaptureAfterAsync(__result, "Loadout2:EnergySpent", card.Owner.NetId,
            SelectionModelKind.Card, card.Id.ToString(), amount);
    }
}

[HarmonyPatch]
public static class CustomRunCardObtainedPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.GetDeclaredMethods(typeof(CardPileCmd))
                   .Where(method => method.Name == nameof(CardPileCmd.Add))
                   .Single(method =>
                   {
                       ParameterInfo[] parameters = method.GetParameters();
                       return parameters.Length is 5 or 6
                              && parameters[0].ParameterType == typeof(IEnumerable<CardModel>)
                              && parameters[1].ParameterType == typeof(CardPile);
                   });
    }

    [HarmonyPostfix]
    public static void Postfix(CardPile newPile, ref Task<IReadOnlyList<CardPileAddResult>> __result)
    {
        if (newPile.Type != PileType.Deck)
            return;
        __result = CaptureCardsObtainedAsync(__result);
    }

    private static async Task<IReadOnlyList<CardPileAddResult>> CaptureCardsObtainedAsync(
        Task<IReadOnlyList<CardPileAddResult>> nativeTask)
    {
        IReadOnlyList<CardPileAddResult> results = await nativeTask;
        foreach (CardPileAddResult result in results.Where(result => result.success))
        {
            CardModel card = result.cardAdded;
            CustomRunRuleRuntimeService.Capture("Loadout2:CardObtained", card.Owner.NetId,
                SelectionModelKind.Card, card.Id.ToString());
        }
        return results;
    }
}

[HarmonyPatch(typeof(RelicCmd), nameof(RelicCmd.Obtain), typeof(RelicModel), typeof(Player), typeof(int))]
public static class CustomRunRelicObtainedPatch
{
    [HarmonyPostfix]
    public static void Postfix(ref Task<RelicModel> __result)
    {
        __result = CaptureRelicAsync(__result);
    }

    private static async Task<RelicModel> CaptureRelicAsync(Task<RelicModel> nativeTask)
    {
        RelicModel relic = await nativeTask;
        CustomRunRuleRuntimeService.Capture("Loadout2:RelicObtained", relic.Owner.NetId,
            SelectionModelKind.Relic, relic.Id.ToString());
        return relic;
    }
}

[HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Add), typeof(MonsterModel), typeof(ICombatState), typeof(CombatSide), typeof(string))]
public static class CustomRunMonsterSpawnedPatch
{
    [HarmonyPostfix]
    public static void Postfix(ref Task<Creature> __result)
    {
        __result = CaptureMonsterAsync(__result);
    }

    private static async Task<Creature> CaptureMonsterAsync(Task<Creature> nativeTask)
    {
        Creature creature = await nativeTask;
        if (creature.Monster is { } monster)
        {
            CustomRunRuleRuntimeService.Capture("Loadout2:MonsterSpawned", 0,
                SelectionModelKind.Monster, monster.Id.ToString());
        }
        return creature;
    }
}

[HarmonyPatch(typeof(MonsterModel), nameof(MonsterModel.PerformMove))]
public static class CustomRunMonsterMoveStartedPatch
{
    [HarmonyPrefix]
    public static void Prefix(MonsterModel __instance)
    {
        CustomRunRuleRuntimeService.Capture("Loadout2:MonsterMoveStarted", 0,
            SelectionModelKind.Monster, __instance.Id.ToString());
    }
}

internal static class CustomRunTriggerCapture
{
    public static async Task CaptureAfterAsync(
        Task nativeTask,
        string triggerId,
        ulong playerId,
        SelectionModelKind? kind = null,
        string? modelId = null,
        double amount = 0d)
    {
        await nativeTask;
        CustomRunRuleRuntimeService.Capture(triggerId, playerId, kind, modelId, amount);
    }
}

[HarmonyPatch]
public static class CustomRunExtendedSavePatch
{
    private const string EmbeddedSaveKey = "Loadout.custom_run.snapshot_v1";
    private static bool _registered;

    public static MethodBase TargetMethod()
    {
        Type type = AccessTools.TypeByName("BaseLib.Patches.PostModInitPatch")
                    ?? throw new TypeLoadException("BaseLib.Patches.PostModInitPatch");
        return AccessTools.Method(type, "LatePostInit")
               ?? throw new MissingMethodException(type.FullName, "LatePostInit");
    }

    [HarmonyPrefix]
    public static void Prefix()
    {
        if (_registered)
            return;

        _registered = true;
        ExtendedSaveHandlers<IRunState, SerializableRun>.RegisterSave<RunState, string>(
            EmbeddedSaveKey,
            CustomRunRuntimeSnapshotService.GetSerializedSnapshotForSave,
            CustomRunRuntimeSnapshotService.LoadSerializedSnapshot,
            static (payload, writer) => writer.WriteString(payload ?? string.Empty),
            static reader => reader.ReadString());
    }
}
