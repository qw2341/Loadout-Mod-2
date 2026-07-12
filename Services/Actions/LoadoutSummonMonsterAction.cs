#nullable enable

namespace Loadout.Services.Actions;

using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

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
        return new NetLoadoutSummonMonsterAction { monsterId = MonsterId };
    }

    public override string ToString()
    {
        return $"LoadoutSummonMonsterAction player {Player.NetId} monster {MonsterId}";
    }
}

public struct NetLoadoutSummonMonsterAction : INetAction, IPacketSerializable
{
    public ModelId monsterId;

    public readonly GameAction ToGameAction(Player player)
    {
        return new LoadoutSummonMonsterAction(player, monsterId);
    }

    public readonly void Serialize(PacketWriter writer)
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
