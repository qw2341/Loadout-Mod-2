#nullable enable

namespace Loadout.Services.CreatureManipulation;

using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

public struct CreatureManipulationRequestMessage : INetMessage, IPacketSerializable
{
    public string payloadJson;
    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => false;
    public readonly void Serialize(PacketWriter writer) => writer.WriteString(payloadJson ?? string.Empty);
    public void Deserialize(PacketReader reader) => payloadJson = reader.ReadString();
}

public struct CreatureDragMessage : INetMessage, IPacketSerializable
{
    public int combatEpoch;
    public uint combatId;
    public ulong sessionId;
    public ulong ownerNetId;
    public CreatureDragPhase phase;
    public int sequence;
    public float x;
    public float y;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => false;

    public readonly void Serialize(PacketWriter writer)
    {
        writer.WriteInt(combatEpoch);
        writer.WriteUInt(combatId);
        writer.WriteULong(sessionId);
        writer.WriteULong(ownerNetId);
        writer.WriteInt((int)phase, 4);
        writer.WriteInt(sequence);
        writer.WriteFloat(x);
        writer.WriteFloat(y);
    }

    public void Deserialize(PacketReader reader)
    {
        combatEpoch = reader.ReadInt();
        combatId = reader.ReadUInt();
        sessionId = reader.ReadULong();
        ownerNetId = reader.ReadULong();
        phase = (CreatureDragPhase)reader.ReadInt(4);
        sequence = reader.ReadInt();
        x = reader.ReadFloat();
        y = reader.ReadFloat();
    }
}

public struct CreatureManipulationSnapshotMessage : INetMessage, IPacketSerializable
{
    public string snapshotJson;
    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => false;
    public readonly void Serialize(PacketWriter writer) => writer.WriteString(snapshotJson ?? string.Empty);
    public void Deserialize(PacketReader reader) => snapshotJson = reader.ReadString();
}
