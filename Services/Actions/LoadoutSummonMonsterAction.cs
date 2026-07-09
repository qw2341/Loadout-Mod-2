#nullable enable

namespace Loadout.Services.Actions;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

public sealed class LoadoutSummonMonsterAction(Player player, ModelId monsterId) : GameAction
{
    public override ulong OwnerId => Player.NetId;
    public override GameActionType ActionType => GameActionType.CombatPlayPhaseOnly;

    public Player Player { get; } = player;
    public ModelId MonsterId { get; } = monsterId;

    protected override Task ExecuteAction()
    {
        return LoadoutSummonMonsterService.SummonMonsterNowAsync(MonsterId);
    }

    public override INetAction ToNetAction()
    {
        return new NetLoadoutSummonMonsterAction
        {
            monsterId = MonsterId
        };
    }

    public override string ToString()
    {
        return $"LoadoutSummonMonsterAction player {Player.NetId} monster {MonsterId}";
    }
}

public struct NetLoadoutSummonMonsterAction : INetAction, IPacketSerializable
{
    public ModelId monsterId;

    public GameAction ToGameAction(Player player)
    {
        return new LoadoutSummonMonsterAction(player, monsterId);
    }

    public void Serialize(PacketWriter writer)
    {
        writer.WriteModelEntry(LoadoutModelIdSafety.OrNone(monsterId));
    }

    public void Deserialize(PacketReader reader)
    {
        monsterId = reader.ReadModelIdAssumingType<MonsterModel>();
    }

    public override readonly string ToString()
    {
        return $"NetLoadoutSummonMonsterAction monster {monsterId}";
    }
}

public static class LoadoutSummonMonsterService
{
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

            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new LoadoutSummonMonsterAction(localPlayer, monsterId));
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

        MonsterModel? canonicalMonster = ModelDb.Monsters.FirstOrDefault(monster => LoadoutModelIdSafety.Matches(monster, monsterId));
        if (canonicalMonster is null)
        {
            GD.PushWarning($"LoadoutSummonMonsterAction: unknown monster '{monsterId}'.");
            return;
        }

        try
        {
            MonsterModel monster = canonicalMonster.ToMutable();
            string? slotName = GetNextAvailableMonsterSlot(combatState);
            IReadOnlyList<NCreature> existingEnemyNodes = GetCurrentEnemyNodes();
            Creature creature = await CreatureCmd.Add(monster, combatState, CombatSide.Enemy, slotName);

            if (slotName is null)
                PositionUnslottedSummonedMonster(creature, existingEnemyNodes);
        }
        catch (Exception exception)
        {
            GD.PushError($"LoadoutSummonMonsterAction: failed to summon monster '{monsterId}': {exception}");
        }
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
