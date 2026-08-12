#nullable enable

namespace Loadout.Services.CustomRuns.Runtime;

using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

public struct CustomRunDecisionBatchMessage : INetMessage, IPacketSerializable
{
    public string payload;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => false;

    public readonly void Serialize(PacketWriter writer) => writer.WriteString(payload ?? string.Empty);
    public void Deserialize(PacketReader reader) => payload = reader.ReadString();
}

public struct CustomRunRuntimeStateMessage : INetMessage, IPacketSerializable
{
    public string snapshotHash;
    public string payload;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => false;

    public readonly void Serialize(PacketWriter writer)
    {
        writer.WriteString(snapshotHash ?? string.Empty);
        writer.WriteString(payload ?? string.Empty);
    }

    public void Deserialize(PacketReader reader)
    {
        snapshotHash = reader.ReadString();
        payload = reader.ReadString();
    }
}

public struct CustomRunChoiceRequestMessage : INetMessage, IPacketSerializable
{
    public string payload;
    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => false;
    public readonly void Serialize(PacketWriter writer) => writer.WriteString(payload ?? string.Empty);
    public void Deserialize(PacketReader reader) => payload = reader.ReadString();
}

public struct CustomRunChoiceResponseMessage : INetMessage, IPacketSerializable
{
    public string payload;
    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => false;
    public readonly void Serialize(PacketWriter writer) => writer.WriteString(payload ?? string.Empty);
    public void Deserialize(PacketReader reader) => payload = reader.ReadString();
}
