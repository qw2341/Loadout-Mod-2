#nullable enable

namespace Loadout.Patches.CustomRuns;

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BaseLib.Patches.Saves;
using HarmonyLib;
using Loadout.Services.Compatibility;
using Loadout.Services.CustomRuns.Compilation;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.CustomRuns.Networking;
using Loadout.Services.CustomRuns.Runtime;
using static Loadout.Patches.CustomRuns.CustomRunTriggerCapture;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;
using MegaCrit.Sts2.Core.ValueProps;

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

public static class CustomRunStateCreationPatch
{
    public static void Prefix(
        ref IReadOnlyList<ModifierModel> modifiers,
        ref GameMode gameMode,
        ref int ascensionLevel)
    {
        ResolvedCustomRunSnapshot? snapshot = CustomRunRuntimeSnapshotService.PendingSnapshot;
        if (snapshot?.AscensionLevel is int customAscension)
            ascensionLevel = Math.Clamp(customAscension, 0, 10);
        if (snapshot?.ModifiersEnabled == true)
        {
            modifiers = CustomRunModifierResolver.ResolveAll(snapshot.Modifiers);
            gameMode = GameMode.Custom;
        }
    }

    public static void Postfix(RunState __result)
    {
        CustomRunRuntimeSnapshotService.AttachPending(__result);
    }
}

public static class CustomRunLobbyCleanupPatch
{
    public static void Postfix(StartRunLobby __instance, bool disconnectSession)
    {
        if (disconnectSession)
            CustomRunLobbyService.CancelPreparedRun(__instance);
        else
            CustomRunLobbyService.CompletePreparedRun(__instance);
    }
}

public static class CustomRunHandDrawPatch
{
    public static void Prefix(Player __1, ref decimal __2)
    {
        if (CustomRunRuntimeSnapshotService.TryGetPlayerSetup(__1, out ResolvedPlayerSetup setup)
            && setup.CardsDrawnPerTurn.HasValue)
        {
            __2 = setup.CardsDrawnPerTurn.Value;
        }
    }
}

public static class CustomRunRuleRuntimeLaunchPatch
{
    public static void Prefix()
    {
        CustomRunRuleRuntimeService.PrepareRunLaunch();
    }

    public static void Postfix()
    {
        CustomRunSetupApplyService.ApplyInitialRuntimeSetup();
        CustomRunRuleRuntimeService.OnRunLaunched();
    }
}

public static class CustomRunPostAscensionSetupPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(RunManager), "InitializeNewRun")
               ?? throw new MissingMethodException(typeof(RunManager).FullName, "InitializeNewRun");
    }

    public static void Postfix(RunManager __instance)
    {
        RunState? runState = __instance.DebugOnlyGetState();
        if (runState is not null)
            CustomRunSetupApplyService.ApplyPostAscensionSetup(runState);
    }
}

public static class CustomRunRuleRuntimeCleanupPatch
{
    public static void Prefix()
    {
        CustomRunRuleRuntimeService.OnRunCleaningUp();
    }

    public static void Postfix()
    {
        CustomRunRuntimePatchManager.Deactivate();
    }
}

public static class CustomRunRuleRuntimeRunEndPatch
{
    public static void Prefix()
    {
        CustomRunRuleRuntimeService.Capture("Loadout2:RunEnd", 0);
    }
}

public static class CustomRunAfterCardPlayedPatch
{
    public static void Postfix(PlayerChoiceContext choiceContext, CardPlay cardPlay, ref Task __result)
    {
        __result = CaptureAfterAsync(__result, choiceContext, cardPlay.Card.Owner,
            "Loadout2:CardPlayed", cardPlay.Card.Owner.NetId,
            SelectionModelKind.Card, cardPlay.Card.Id.ToString());
    }
}

public static class CustomRunAfterRoomEnteredPatch
{
    public static void Postfix(IRunState runState, AbstractRoom room, ref Task __result)
    {
        __result = CaptureAfterRoomEnteredAsync(__result, runState, room);
    }

    private static async Task CaptureAfterRoomEnteredAsync(Task nativeTask, IRunState runState, AbstractRoom room)
    {
        await nativeTask;
        if (runState is not RunState concrete || !CustomRunRuleRuntimeService.IsForRun(concrete))
            return;
        if (CustomRunRuleRuntimeService.UsesTrigger("Loadout2:RoomEntered"))
            CustomRunRuleRuntimeService.Capture("Loadout2:RoomEntered", 0);
        if (room is not EventRoom eventRoom)
            return;

        string eventId = eventRoom.CanonicalEvent.Id.ToString();
        CaptureEventTrigger("Loadout2:EventEntered", eventId);
        CaptureEventTrigger(
            eventRoom.CanonicalEvent is AncientEventModel
                ? "Loadout2:AncientEventEntered"
                : "Loadout2:NonAncientEventEntered",
            eventId);
    }

    private static void CaptureEventTrigger(string triggerId, string eventId)
    {
        CustomRunRuleRuntimeService.CaptureRoomEnteredTrigger(triggerId, eventId);
    }
}

public static class CustomRunRoomFadeInPatch
{
    private static readonly AsyncLocal<bool> Bypass = new();

    public static bool Prefix(NTransition __instance, bool showTransition, ref Task __result)
    {
        if (Bypass.Value || !CustomRunRuleRuntimeService.HasPendingRoomEntryRedirects)
            return true;
        __result = FadeInAfterRedirectsAsync(__instance, showTransition);
        return false;
    }

    private static async Task FadeInAfterRedirectsAsync(NTransition transition, bool showTransition)
    {
        await CustomRunRuleRuntimeService.WaitForPendingRoomEntryRedirectsAsync();
        Bypass.Value = true;
        try
        {
            await transition.RoomFadeIn(showTransition);
        }
        finally
        {
            Bypass.Value = false;
        }
    }
}

public static class CustomRunRoomCompletedPatch
{
    public static void Prefix(bool isOpenedFromTopBar, out AbstractRoom? __state)
    {
        __state = !isOpenedFromTopBar
            ? RunManager.Instance.DebugOnlyGetState()?.CurrentRoom
            : null;
    }

    public static void Postfix(AbstractRoom? __state)
    {
        if (__state is not null)
            CustomRunRuleRuntimeService.CaptureRoomCompleted(__state);
    }
}

public static class CustomRunPendingEventRoomPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.GetDeclaredMethods(typeof(RunManager))
                   .Single(method => method.Name == "CreateRoom"
                                     && method.GetParameters().Length == 3
                                     && method.GetParameters()[0].ParameterType == typeof(RoomType));
    }

    public static void Prefix(RoomType __0, ref AbstractModel? __2, out bool __state)
    {
        __state = false;
        if (__0 != RoomType.Event || __2 is not null
            || !CustomRunRuleRuntimeService.TryGetPendingEventModel(out EventModel eventModel))
            return;
        __2 = eventModel;
        __state = true;
    }

    public static void Postfix(bool __state)
    {
        if (__state)
            CustomRunRuleRuntimeService.ConsumePendingEventModel();
    }
}

public static class CustomRunBeforeCombatStartPatch
{
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

public static class CustomRunAfterCardDrawnPatch
{
    public static void Postfix(PlayerChoiceContext choiceContext, CardModel card, ref Task __result)
    {
        __result = CaptureAfterAsync(__result, choiceContext, card.Owner,
            "Loadout2:CardDrawn", card.Owner.NetId,
            SelectionModelKind.Card, card.Id.ToString());
    }
}

public static class CustomRunAfterCardDiscardedPatch
{
    public static void Postfix(PlayerChoiceContext choiceContext, CardModel card, ref Task __result)
    {
        __result = CaptureAfterAsync(__result, choiceContext, card.Owner,
            "Loadout2:CardDiscarded", card.Owner.NetId,
            SelectionModelKind.Card, card.Id.ToString());
    }
}

public static class CustomRunAfterCardExhaustedPatch
{
    public static void Postfix(PlayerChoiceContext choiceContext, CardModel card, ref Task __result)
    {
        __result = CaptureAfterAsync(__result, choiceContext, card.Owner,
            "Loadout2:CardExhausted", card.Owner.NetId,
            SelectionModelKind.Card, card.Id.ToString());
    }
}

public static class CustomRunAfterCardGeneratedPatch
{
    public static void Postfix(CardModel card, Player? creator, ref Task __result)
    {
        __result = CaptureAfterAsync(__result, "Loadout2:CardGenerated", creator?.NetId ?? card.Owner.NetId,
            SelectionModelKind.Card, card.Id.ToString());
    }
}

public static class CustomRunAfterDamageReceivedPatch
{
    public static void Postfix(Creature target, DamageResult result, ref Task __result)
    {
        if (target.Player is not { } player || result.UnblockedDamage <= 0)
            return;
        __result = CaptureAfterAsync(__result, "Loadout2:PlayerTakesDamage", player.NetId,
            amount: result.UnblockedDamage);
    }
}

public static class CustomRunAfterBlockGainedPatch
{
    public static void Postfix(Creature creature, decimal amount, ref Task __result)
    {
        if (creature.Player is not { } player || amount <= 0m)
            return;
        __result = CaptureAfterAsync(__result, "Loadout2:PlayerGainsBlock", player.NetId, amount: (double)amount);
    }
}

public static class CustomRunAfterPowerChangedPatch
{
    public static void Postfix(PowerModel power, decimal amount, ref Task __result)
    {
        if (power.Owner.Player is not { } player || amount <= 0m)
            return;
        __result = CaptureAfterAsync(__result, "Loadout2:PowerReceived", player.NetId,
            SelectionModelKind.Power, power.Id.ToString(), (double)amount);
    }
}

public static class CustomRunAfterPotionUsedPatch
{
    public static void Postfix(PotionModel potion, ref Task __result)
    {
        __result = CaptureAfterAsync(__result, "Loadout2:PotionUsed", potion.Owner.NetId,
            SelectionModelKind.Potion, potion.Id.ToString());
    }
}

public static class CustomRunAfterPotionProcuredPatch
{
    public static void Postfix(PotionModel potion, ref Task __result)
    {
        __result = CaptureAfterAsync(__result, "Loadout2:PotionObtained", potion.Owner.NetId,
            SelectionModelKind.Potion, potion.Id.ToString());
    }
}

public static class CustomRunAfterEnergySpentPatch
{
    public static void Postfix(CardModel card, int amount, ref Task __result)
    {
        if (amount <= 0)
            return;
        __result = CaptureAfterAsync(__result, "Loadout2:EnergySpent", card.Owner.NetId,
            SelectionModelKind.Card, card.Id.ToString(), amount);
    }
}

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
        foreach (CardPileAddResult result in results)
        {
            if (!result.success)
                continue;
            CardModel card = result.cardAdded;
            CustomRunRuleRuntimeService.Capture("Loadout2:CardObtained", card.Owner.NetId,
                SelectionModelKind.Card, card.Id.ToString());
        }
        return results;
    }
}

public static class CustomRunRelicObtainedPatch
{
    public static void Postfix(ref Task<RelicModel> __result)
    {
        __result = CaptureRelicAsync(__result);
    }

    private static async Task<RelicModel> CaptureRelicAsync(Task<RelicModel> nativeTask)
    {
        RelicModel relic = await nativeTask;
        if (relic is null)
            return null!;
        CustomRunRuleRuntimeService.Capture("Loadout2:RelicObtained", relic.Owner.NetId,
            SelectionModelKind.Relic, relic.Id.ToString());
        return relic;
    }
}

public static class CustomRunMonsterSpawnedPatch
{
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

public static class CustomRunMonsterMoveStartedPatch
{
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
        PlayerChoiceContext choiceContext,
        Player choicePlayer,
        string triggerId,
        ulong playerId,
        SelectionModelKind? kind = null,
        string? modelId = null,
        double amount = 0d)
    {
        await nativeTask;
        if (choiceContext is not HookPlayerChoiceContext hookContext
            || !CustomRunRuleRuntimeService.NeedsHookCompletionBarrier(triggerId))
        {
            CustomRunRuleRuntimeService.Capture(triggerId, playerId, kind, modelId, amount);
            return;
        }

        if (hookContext.GameAction is null)
        {
            // Put phase-driven autoplay behind the native synchronized hook boundary.
            await Sts2Compatibility.SignalHookPlayerChoiceBegun(
                hookContext,
                choicePlayer,
                PlayerChoiceOptions.None);
            await Sts2Compatibility.SignalHookPlayerChoiceEnded(hookContext, choicePlayer);
        }

        if (hookContext.GameAction is { } hookAction)
        {
            CustomRunRuleRuntimeService.CaptureAtActionFinish(
                hookAction,
                triggerId,
                playerId,
                kind,
                modelId,
                amount);
            return;
        }

        CustomRunRuleRuntimeService.Capture(triggerId, playerId, kind, modelId, amount);
    }

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
