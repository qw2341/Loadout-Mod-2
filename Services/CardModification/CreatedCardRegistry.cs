#nullable enable

namespace Loadout.Services.CardModification;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using Loadout.Cards;
using Loadout.Patches.Cards.CardModification;
using Loadout.UI.Managers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

public static class CreatedCardRegistry
{
    public const string SavedDefinitionField = "loadout_created_card_definition_v1";

    private static readonly FieldInfo IdField = AccessTools.Field(typeof(AbstractModel), "<Id>k__BackingField");
    private static readonly FieldInfo MutableField = AccessTools.Field(typeof(AbstractModel), "<IsMutable>k__BackingField");
    private static readonly FieldInfo PoolField = AccessTools.Field(typeof(CardModel), "_pool");
    private static readonly FieldInfo CanonicalField = AccessTools.Field(typeof(CardModel), "_canonicalInstance");
    private static readonly FieldInfo TitleField = AccessTools.Field(typeof(CardModel), "_titleLocString");
    private static readonly FieldInfo ContentField = AccessTools.Field(typeof(ModelDb), "_contentById");
    private static readonly Dictionary<ModelId, CreatedCard> Cards = new();
    private static readonly Dictionary<ModelId, CardModificationDelta> RunDefinitions = new();
    private static ModelId? _templateId;

    public static long CollectionRevision { get; private set; }

    public static ModelId TemplateId => _templateId ??= ModelDb.GetId<CreatedCard>();

    public static IEnumerable<CardModel> CollectionCards => Cards.Values
        .Where(card => PermanentCardModificationStore.HasCollectionDefinition(card.Id))
        .OrderBy(card => card.Id.Entry, StringComparer.Ordinal);

    public static bool IsCreatedId(ModelId? id) => id?.Category == "CARD" && IsCreatedEntry(id.Entry);

    public static bool TryParseId(string? value, out ModelId id)
    {
        string? entry = value?.StartsWith("CARD.", StringComparison.Ordinal) == true ? value[5..] : value;
        if (IsCreatedEntry(entry))
        {
            id = new ModelId("CARD", entry!);
            return true;
        }
        id = ModelId.none;
        return false;
    }

    private static bool IsCreatedEntry(string? entry)
    {
        if (entry is null || entry.Length is < 10 or > 29)
            return false;
        int separator = entry.Length - 9;
        if (entry[separator] != '-'
            || !ulong.TryParse(entry.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out ulong userId)
            || userId == 0)
            return false;
        for (int i = separator + 1; i < entry.Length; i++)
            if (entry[i] is < '0' or > '9')
                return false;
        return true;
    }

    public static ModelId CreateId(ulong userId)
    {
        if (userId == 0)
            throw new ArgumentOutOfRangeException(nameof(userId));
        PermanentCardModificationStore.EnsureLoaded();
        ModelId id;
        do
        {
            // Identity entropy is generated once and carried by the synchronized add action.
            int suffix = RandomNumberGenerator.GetInt32(100_000_000);
            id = new ModelId("CARD", string.Create(CultureInfo.InvariantCulture, $"{userId}-{suffix:D8}"));
        } while (ModelDb.GetByIdOrNull<CardModel>(id) is not null);
        return id;
    }

    public static CreatedCard GetOrCreate(ModelId id)
    {
        if (!IsCreatedId(id))
            throw new ArgumentException("Not a created card ID.", nameof(id));
        if (Cards.TryGetValue(id, out CreatedCard? existing))
            return existing;

        CreatedCard template = ModelDb.Card<CreatedCard>();
        PoolField.SetValue(template, ModelDb.CardPool<ColorlessCardPool>());
        CreatedCard card = (CreatedCard)template.MutableClone();
        IdField.SetValue(card, id);
        MutableField.SetValue(card, false);
        CanonicalField.SetValue(card, card);
        TitleField.SetValue(card, null);
        ((Dictionary<ModelId, AbstractModel>)ContentField.GetValue(null)!)[id] = card;
        Cards.Add(id, card);
        CardModificationRuntime.MarkCustomTextOverridesPresent();
        return card;
    }

    public static bool TryGetDefaultText(LocString text, out string value)
    {
        value = string.Empty;
        if (text.LocTable != "cards")
            return false;
        string key = text.LocEntryKey;
        int separator = key.LastIndexOf('.');
        if (separator < 0 || !IsCreatedEntry(key[..separator]))
            return false;
        if (key.AsSpan(separator + 1).SequenceEqual("title"))
        {
            value = LocMan.Loc("NEW_CARD", "New Card");
            return true;
        }
        return key.AsSpan(separator + 1).SequenceEqual("description");
    }

    public static void NotifyCollectionChanged() => CollectionRevision++;

    public static bool TryGetRunDefinition(ModelId id, out CardModificationDelta delta) =>
        RunDefinitions.TryGetValue(id, out delta!);

    public static IEnumerable<KeyValuePair<ModelId, CardModificationDelta>> GetRunDefinitions() => RunDefinitions;

    public static bool ClearRunDefinition(ModelId id)
    {
        if (!IsCreatedId(id))
            return false;
        bool changed = RunDefinitions.TryGetValue(id, out CardModificationDelta? previous) && !previous.IsEmpty;
        RunDefinitions[id] = new CardModificationDelta();
        return changed;
    }

    public static void ExportDefinition(CardModel card, SerializableCard save)
    {
        if (card is not CreatedCard)
            return;
        CardModificationDelta definition = PermanentCardModificationStore.TryGetDelta(card.Id, out CardModificationDelta saved)
            ? saved
            : new CardModificationDelta();
        save.Props ??= new SavedProperties();
        save.Props.strings ??= [];
        save.Props.strings.Add(new SavedProperties.SavedProperty<string>(
            SavedDefinitionField, CardModificationCodec.SerializeDelta(definition)));
    }

    public static void RestoreDefinition(SerializableCard save)
    {
        if (!IsCreatedId(save.Id))
            return;
        ModelId id = save.Id!;
        GetOrCreate(id);
        if (RunDefinitions.ContainsKey(id))
            return;
        string? json = save.Props?.strings?.FirstOrDefault(entry => entry.name == SavedDefinitionField).value;
        if (string.IsNullOrWhiteSpace(json)
            || !CardModificationCodec.TryDeserializeDelta(json, out CardModificationDelta definition))
            return;
        RunDefinitions[id] = definition;
        CardModificationRuntime.ReconcileQuietPermanentImport([id]);
    }

    public static void ClearRunState()
    {
        RunDefinitions.Clear();
        foreach ((ModelId id, CreatedCard card) in Cards.ToArray())
        {
            if (PermanentCardModificationStore.HasCollectionDefinition(id))
                continue;
            CanonicalCardModificationRegistry.RemoveCreatedCard(id);
            ((Dictionary<ModelId, AbstractModel>)ContentField.GetValue(null)!).Remove(id);
            Cards.Remove(id);
        }
    }
}
