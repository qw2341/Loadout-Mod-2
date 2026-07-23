#nullable enable

namespace Loadout.Services.CreatureManipulation;

using System.Text.Json;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

public sealed class LoadoutCreatureManipulationAction(Player player, string payloadJson) : GameAction
{
    public override ulong OwnerId => Player.NetId;
    public override GameActionType ActionType => GameActionType.Combat;

    public Player Player { get; } = player;
    public string PayloadJson { get; } = payloadJson;

    protected override Task ExecuteAction() =>
        CreatureManipulationStateService.ApplySynchronizedActionAsync(PayloadJson);

    public override INetAction ToNetAction() =>
        new NetLoadoutCreatureManipulationAction { payloadJson = PayloadJson };

    public override string ToString() =>
        $"LoadoutCreatureManipulationAction owner {Player.NetId}";
}

public struct NetLoadoutCreatureManipulationAction : INetAction, IPacketSerializable
{
    public string payloadJson;

    public readonly GameAction ToGameAction(Player player) =>
        new LoadoutCreatureManipulationAction(player, payloadJson ?? string.Empty);

    public readonly void Serialize(PacketWriter writer) =>
        writer.WriteString(payloadJson ?? string.Empty);

    public void Deserialize(PacketReader reader) =>
        payloadJson = reader.ReadString();

    public override readonly string ToString()
    {
        try
        {
            CreatureManipulationPayload? payload =
                JsonSerializer.Deserialize<CreatureManipulationPayload>(payloadJson);
            return $"NetLoadoutCreatureManipulationAction {payload?.Operation} target {payload?.TargetCombatId}";
        }
        catch
        {
            return "NetLoadoutCreatureManipulationAction invalid payload";
        }
    }
}
