#nullable enable

namespace Loadout.Patches.Cards;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Loadout.Cards;
using Loadout.Services.CardModification;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

[HarmonyPatch(typeof(SaveUtil), nameof(SaveUtil.CardOrDeprecated))]
public static class CreatedCardSaveLookupPatch
{
    public static void Prefix(ModelId id)
    {
        if (CreatedCardRegistry.IsCreatedId(id))
            CreatedCardRegistry.GetOrCreate(id);
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.ToSerializable))]
public static class CreatedCardSaveDefinitionPatch
{
    public static void Postfix(CardModel __instance, SerializableCard __result) =>
        CreatedCardRegistry.ExportDefinition(__instance, __result);
}

// A fixed template ID escapes dynamic IDs without changing the shared network ID table.
[HarmonyPatch(typeof(PacketWriterExtensions), nameof(PacketWriterExtensions.WriteModelEntry))]
public static class CreatedCardWriteEntryPatch
{
    public static bool Prefix(PacketWriter writer, ModelId id)
    {
        if (!CreatedCardRegistry.IsCreatedId(id) && id != CreatedCardRegistry.TemplateId)
            return true;
        writer.WriteInt(ModelIdSerializationCache.GetNetIdForEntry(CreatedCardRegistry.TemplateId.Entry),
            ModelIdSerializationCache.EntryIdBitSize);
        writer.WriteString(id == CreatedCardRegistry.TemplateId ? string.Empty : id.Entry);
        return false;
    }
}

[HarmonyPatch(typeof(PacketWriterExtensions), nameof(PacketWriterExtensions.WriteFullModelId))]
public static class CreatedCardWriteFullIdPatch
{
    public static bool Prefix(PacketWriter writer, ModelId id)
    {
        if (!CreatedCardRegistry.IsCreatedId(id) && id != CreatedCardRegistry.TemplateId)
            return true;
        writer.WriteInt(ModelIdSerializationCache.GetNetIdForCategory(id.Category), ModelIdSerializationCache.CategoryIdBitSize);
        writer.WriteModelEntry(id);
        return false;
    }
}

[HarmonyPatch]
public static class CreatedCardReadEntryPatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (Type type in new[]
        {
            typeof(SerializableCard), typeof(NetPlayCardAction), typeof(PlayerMapPointHistoryEntry),
            typeof(NetPlayerChoiceResult), typeof(HoveredModelData)
        })
            yield return AccessTools.Method(type, "Deserialize", [typeof(PacketReader)]);
    }

    // Patching the shared generic reader itself erases T for non-card callers.
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.operand is MethodInfo method
                && method.DeclaringType == typeof(PacketReaderExtensions)
                && method.IsGenericMethod
                && method.GetGenericArguments().SequenceEqual([typeof(CardModel)]))
            {
                string? replacement = method.Name switch
                {
                    nameof(PacketReaderExtensions.ReadModelIdAssumingType) => nameof(ReadCardId),
                    nameof(PacketReaderExtensions.ReadModelIdListAssumingType) => nameof(ReadCardIds),
                    nameof(PacketReaderExtensions.ReadModel) => nameof(ReadCard),
                    _ => null
                };
                if (replacement is not null)
                    instruction.operand = AccessTools.Method(typeof(CreatedCardReadEntryPatch), replacement);
            }
            yield return instruction;
        }
    }

    public static ModelId ReadCardId(PacketReader reader)
    {
        ModelId id = reader.ReadModelIdAssumingType<CardModel>();
        CreatedCardReadFullIdPatch.ReadCreatedId(reader, ref id);
        return id;
    }

    public static List<ModelId> ReadCardIds(PacketReader reader)
    {
        List<ModelId> ids = [];
        int count = reader.ReadInt();
        for (int i = 0; i < count; i++)
            ids.Add(ReadCardId(reader));
        return ids;
    }

    public static CardModel ReadCard(PacketReader reader) => ModelDb.GetById<CardModel>(ReadCardId(reader));
}

[HarmonyPatch(typeof(PacketReaderExtensions), nameof(PacketReaderExtensions.ReadFullModelId))]
public static class CreatedCardReadFullIdPatch
{
    public static void Postfix(PacketReader reader, ref ModelId __result) => ReadCreatedId(reader, ref __result);

    public static void ReadCreatedId(PacketReader reader, ref ModelId id)
    {
        if (id != CreatedCardRegistry.TemplateId)
            return;
        string entry = reader.ReadString();
        if (entry.Length == 0)
            return;
        if (!CreatedCardRegistry.TryParseId(entry, out ModelId createdId))
            throw new InvalidDataException("Invalid created card ID.");
        id = createdId;
        CreatedCardRegistry.GetOrCreate(id);
    }
}

[HarmonyPatch(typeof(DeterministicModelComparer), nameof(DeterministicModelComparer.Compare))]
public static class CreatedCardHookOrderPatch
{
    public static bool Prefix(AbstractModel? model1, AbstractModel? model2, ref int __result)
    {
        if (model1 is not CreatedCard || model2 is not CreatedCard || model1.Id == model2.Id)
            return true;
        __result = model1.Id.CompareTo(model2.Id);
        return false;
    }
}
