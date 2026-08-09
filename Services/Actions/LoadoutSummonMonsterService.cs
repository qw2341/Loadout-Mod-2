#nullable enable

namespace Loadout.Services.Actions;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

public static class LoadoutSummonMonsterService
{
    private static readonly AsyncLocal<MonsterModel?> IntentFallbackMonster = new();

    public static bool RequestSummonMonster(ModelId monsterId)
    {
        if (!CombatManager.Instance.IsInProgress || LoadoutModelIdSafety.IsNoneOrEmpty(monsterId))
            return false;

        try
        {
            RunState? runState = RunManager.Instance.DebugOnlyGetState();
            Player? localPlayer = runState is null ? null : LocalContext.GetMe(runState);
            if (localPlayer is null)
                return false;

            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                new LoadoutSummonMonsterAction(localPlayer, monsterId));
            return true;
        }
        catch (Exception exception)
        {
            GD.PushError($"LoadoutSummonMonsterAction: failed requesting monster summon '{monsterId}': {exception}");
            return false;
        }
    }

    public static async Task SummonMonsterNowAsync(ModelId monsterId)
    {
        if (!CombatManager.Instance.IsInProgress)
            return;

        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        if (combatState is null)
            return;

        MonsterModel? canonicalMonster = ModelDb.GetByIdOrNull<MonsterModel>(monsterId);
        if (canonicalMonster is null)
        {
            GD.PushWarning($"LoadoutSummonMonster: unknown monster '{monsterId}'.");
            return;
        }

        try
        {
            MonsterModel monster = canonicalMonster.ToMutable();
            string? slotName = GetNextAvailableMonsterSlot(combatState);
            IReadOnlyList<NCreature> existingEnemyNodes = GetCurrentEnemyNodes();
            Creature creature = await AddMonsterWithIntentFallbackAsync(
                monster,
                combatState,
                CombatSide.Enemy,
                slotName);

            if (slotName is null)
                PositionUnslottedSummonedMonster(creature, existingEnemyNodes);
        }
        catch (Exception exception)
        {
            GD.PushError($"LoadoutSummonMonster: failed to summon monster '{monsterId}': {exception}");
        }
    }

    internal static async Task<Creature> AddMonsterWithIntentFallbackAsync(
        MonsterModel monster,
        CombatState combatState,
        CombatSide side,
        string? slotName)
    {
        MonsterModel? previous = IntentFallbackMonster.Value;
        IntentFallbackMonster.Value = monster;
        try
        {
            return await CreatureCmd.Add(monster, combatState, side, slotName);
        }
        finally
        {
            IntentFallbackMonster.Value = previous;
        }
    }

    internal static bool TryGetDefaultIntentStateId(
        Creature owner,
        Exception exception,
        out string stateId)
    {
        stateId = string.Empty;
        MonsterModel? monster = IntentFallbackMonster.Value;
        if (monster is null
            || !ReferenceEquals(owner.Monster, monster)
            || monster.MoveStateMachine is null
            || exception is not InvalidOperationException
            || !exception.Message.StartsWith("No valid next state found", StringComparison.Ordinal))
        {
            return false;
        }

        List<MoveState> fallbackMoves = monster.MoveStateMachine.States.Values
            .OfType<MoveState>()
            .Where(move => move.Intents.Count > 0)
            .ToList();
        if (fallbackMoves.Count == 0)
            return false;

        MoveState fallback = monster.RunRng!.MonsterAi.NextItem(fallbackMoves)!;
        stateId = fallback.Id;
        return true;
    }

    internal static bool TryHandleDecimillipedeSegmentAdded(
        DecimillipedeSegment segment,
        out Task result)
    {
        if (!ReferenceEquals(IntentFallbackMonster.Value, segment)
            || TryFindUnoccupiedDecimillipedeMaxHp(segment, out decimal maxHp))
        {
            result = Task.CompletedTask;
            return false;
        }

        result = SetUpDecimillipedeSegmentAsync(segment, maxHp);
        return true;
    }

    private static bool TryFindUnoccupiedDecimillipedeMaxHp(
        DecimillipedeSegment segment,
        out decimal maxHp)
    {
        Creature creature = segment.Creature;
        ICombatState combatState = segment.CombatState;
        maxHp = creature.MaxHp;
        if (maxHp % 2m == 1m)
            maxHp++;
        decimal fallbackMaxHp = maxHp;

        HashSet<decimal> occupiedMaxHp = combatState.GetTeammatesOf(creature)
            .Where(teammate => teammate != creature)
            .Select(teammate => (decimal)teammate.MaxHp)
            .ToHashSet();
        decimal maximum = Creature.ScaleHpForMultiplayer(
            segment.MaxInitialHp,
            combatState.Encounter,
            combatState.Players.Count,
            combatState.RunState.CurrentActIndex);
        decimal minimum = Creature.ScaleHpForMultiplayer(
            segment.MinInitialHp,
            combatState.Encounter,
            combatState.Players.Count,
            combatState.RunState.CurrentActIndex);

        for (int attempt = 0; attempt <= occupiedMaxHp.Count; attempt++)
        {
            if (!occupiedMaxHp.Contains(maxHp))
                return true;

            maxHp += 2m;
            if (maxHp > maximum)
                maxHp = minimum;
        }

        maxHp = fallbackMaxHp;
        return false;
    }

    private static async Task SetUpDecimillipedeSegmentAsync(
        DecimillipedeSegment segment,
        decimal maxHp)
    {
        Creature creature = segment.Creature;
        await CreatureCmd.SetMaxAndCurrentHp(creature, maxHp);
        await PowerCmd.Apply<ReattachPower>(
            new ThrowingPlayerChoiceContext(),
            creature,
            25m,
            creature,
            null);
    }

    private static string? GetNextAvailableMonsterSlot(CombatState combatState)
    {
        try
        {
            string? slotName = combatState.Encounter?.GetNextSlot(combatState);
            return string.IsNullOrWhiteSpace(slotName) ? null : slotName;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<NCreature> GetCurrentEnemyNodes()
    {
        try
        {
            return NCombatRoom.Instance?.CreatureNodes
                .Where(node => node.Entity.IsMonster && node.Entity.Side == CombatSide.Enemy)
                .ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void PositionUnslottedSummonedMonster(Creature creature, IReadOnlyList<NCreature> existingEnemyNodes)
    {
        NCreature? node = NCombatRoom.Instance?.GetCreatureNode(creature);
        if (node is null)
            return;

        if (existingEnemyNodes.Count == 0)
        {
            node.Position = new Vector2(520f, 200f);
            return;
        }

        NCreature? anchor = existingEnemyNodes
            .Where(GodotObject.IsInstanceValid)
            .OrderBy(existing => existing.Position.X)
            .LastOrDefault();
        if (anchor is null)
        {
            node.Position = new Vector2(520f, 200f);
            return;
        }

        float anchorHalfWidth = MathF.Max(45f, anchor.Visuals.Bounds.Size.X * 0.5f);
        float nodeHalfWidth = MathF.Max(45f, node.Visuals.Bounds.Size.X * 0.5f);
        float x = anchor.Position.X + anchorHalfWidth + nodeHalfWidth + 70f;
        float y = anchor.Position.Y;

        if (x > 900f)
        {
            int index = existingEnemyNodes.Count;
            x = 160f + index % 4 * 205f;
            y = 200f + index / 4 % 3 * 74f;
        }

        node.Position = new Vector2(Mathf.Clamp(x, 120f, 900f), Mathf.Clamp(y, 120f, 380f));
    }
}
