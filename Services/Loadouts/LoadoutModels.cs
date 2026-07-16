#nullable enable

namespace Loadout.Services.Loadouts;

using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Loadout.Patches.Cards;
using Loadout.Services.RelicModification;

public enum LoadoutKind
{
    Cards,
    Relics,
    CardsAndRelics
}

public enum LoadoutSpecialPreset
{
    None,
    StartingDeck
}

public sealed class SavedLoadout
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public LoadoutKind Kind { get; set; }

    [JsonPropertyName("specialPreset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public LoadoutSpecialPreset SpecialPreset { get; set; }

    [JsonPropertyName("createdAt")]
    public long CreatedAtUnixSeconds { get; set; }

    [JsonPropertyName("updatedAt")]
    public long UpdatedAtUnixSeconds { get; set; }

    [JsonPropertyName("cards")]
    public List<SavedCardLoadoutEntry> Cards { get; set; } = [];

    [JsonPropertyName("relics")]
    public List<SavedRelicLoadoutEntry> Relics { get; set; } = [];

    [JsonIgnore]
    public bool IsRemote { get; set; }

    [JsonIgnore]
    public string? RemoteOwnerLabel { get; set; }

    [JsonIgnore]
    public bool HasCards => Kind is LoadoutKind.Cards or LoadoutKind.CardsAndRelics;

    [JsonIgnore]
    public bool HasRelics => Kind is LoadoutKind.Relics or LoadoutKind.CardsAndRelics;

    public SavedLoadout Clone()
    {
        return new SavedLoadout
        {
            Id = Id,
            Name = Name,
            Kind = Kind,
            SpecialPreset = SpecialPreset,
            CreatedAtUnixSeconds = CreatedAtUnixSeconds,
            UpdatedAtUnixSeconds = UpdatedAtUnixSeconds,
            Cards = Cards.ConvertAll(card => card.Clone()),
            Relics = Relics.ConvertAll(relic => relic.Clone()),
            IsRemote = IsRemote,
            RemoteOwnerLabel = RemoteOwnerLabel
        };
    }
}

public sealed class SavedCardLoadoutEntry
{
    [JsonPropertyName("modelId")]
    public string ModelId { get; set; } = string.Empty;

    [JsonPropertyName("upgradeLevel")]
    public int UpgradeLevel { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; } = 1;

    [JsonPropertyName("state")]
    public CardModificationState? ModificationState { get; set; }

    [JsonIgnore]
    public bool HasModificationState => ModificationState is not null && !ModificationState.IsEmpty;

    public SavedCardLoadoutEntry Clone()
    {
        return new SavedCardLoadoutEntry
        {
            ModelId = ModelId,
            UpgradeLevel = UpgradeLevel,
            Count = Count,
            ModificationState = ModificationState?.Clone()
        };
    }
}

public sealed class SavedRelicLoadoutEntry
{
    [JsonPropertyName("modelId")]
    public string ModelId { get; set; } = string.Empty;

    [JsonPropertyName("count")]
    public int Count { get; set; } = 1;

    [JsonPropertyName("state")]
    public RelicModificationState? ModificationState { get; set; }

    [JsonIgnore]
    public bool HasModificationState => ModificationState is not null && !ModificationState.IsEmpty;

    public SavedRelicLoadoutEntry Clone()
    {
        return new SavedRelicLoadoutEntry
        {
            ModelId = ModelId,
            Count = Count,
            ModificationState = ModificationState?.Clone()
        };
    }
}

public sealed class LoadoutProfileSaveData : ISerializable
{
    public LoadoutProfileSaveData()
    {
    }

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = LoadoutStorageService.CurrentSchemaVersion;

    [JsonPropertyName("loadouts")]
    public List<SavedLoadout> Loadouts { get; set; } = [];

    public void GetObjectData(SerializationInfo info, StreamingContext context)
    {
        info.AddValue(nameof(SchemaVersion), SchemaVersion);
        info.AddValue(nameof(Loadouts), Loadouts);
    }
}

public sealed class HostLoadoutCatalog
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = LoadoutStorageService.CurrentSchemaVersion;

    [JsonPropertyName("loadouts")]
    public List<SavedLoadout> Loadouts { get; set; } = [];
}

public readonly record struct LoadoutCatalogEntry(string OptionId, SavedLoadout Loadout, bool Editable);
