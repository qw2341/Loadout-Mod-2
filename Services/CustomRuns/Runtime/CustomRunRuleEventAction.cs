#nullable enable

namespace Loadout.Services.CustomRuns.Runtime;

using System.Text.Json;
using System.Threading.Tasks;
using Loadout.Services.CustomRuns.Persistence;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

public sealed class CustomRunRuleEventAction(Player owner, CustomRunRuntimeEvent runtimeEvent) : GameAction
{
    public override ulong OwnerId => Owner.NetId;
    public override GameActionType ActionType => GameActionType.Any;

    public Player Owner { get; } = owner;
    public CustomRunRuntimeEvent RuntimeEvent { get; } = runtimeEvent;

    protected override Task ExecuteAction()
    {
        return CustomRunRuleRuntimeService.ExecuteSynchronizedEventAsync(RuntimeEvent);
    }

    public override INetAction ToNetAction()
    {
        return new NetCustomRunRuleEventAction
        {
            payload = JsonSerializer.Serialize(RuntimeEvent, CustomRunSerializationService.SharedJsonOptions)
        };
    }
}

public struct NetCustomRunRuleEventAction : INetAction, IPacketSerializable
{
    public string payload;

    public readonly GameAction ToGameAction(Player player)
    {
        CustomRunRuntimeEvent runtimeEvent = JsonSerializer.Deserialize<CustomRunRuntimeEvent>(
            payload,
            CustomRunSerializationService.SharedJsonOptions) ?? new CustomRunRuntimeEvent();
        return new CustomRunRuleEventAction(player, runtimeEvent);
    }

    public readonly void Serialize(PacketWriter writer)
    {
        writer.WriteString(payload ?? string.Empty);
    }

    public void Deserialize(PacketReader reader)
    {
        payload = reader.ReadString();
    }
}
