#nullable enable

namespace Loadout.Services.Configuration;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Godot;
using Loadout.PanelItems;
using Loadout.Patches.Cards.CardModification;
using Loadout.Services.CardModification;
using Loadout.Services.CardPortraits;
using Loadout.Services.RelicModification;
using Loadout.UI.ImageEditing;
using Loadout.UI.Managers;
using Loadout.UI.Screens;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

public enum ModificationImportDecision
{
    Unresolved,
    KeepLocal,
    UseIncoming
}

public sealed record ModificationTransferResult(bool Cancelled, bool Succeeded, int Count, string? Error = null)
{
    public static ModificationTransferResult Cancel() => new(true, false, 0);
    public static ModificationTransferResult Success(int count) => new(false, true, count);
    public static ModificationTransferResult Failure(string error) => new(false, false, 0, error);
}

public sealed class ImportedCardPortrait
{
    private ImageMediaDocument? _document;

    public ImportedCardPortrait(PortraitArchiveRecord record, byte[] data)
    {
        Record = record;
        Data = data;
    }

    public PortraitArchiveRecord Record { get; }
    public byte[] Data { get; }

    public ImageMediaDocument GetDocument()
    {
        return _document ??= ImageMediaLoader.LoadDocumentFromBytes(Data, Path.GetExtension(Record.File));
    }
}

public sealed class CardModificationImportEntry
{
    private CardModel? _localPreview;
    private CardModel? _incomingPreview;
    private CardModel? _upgradedIncomingPreview;

    public required CardModel Canonical { get; init; }
    public CardModificationDelta? LocalDelta { get; init; }
    public CardModificationDelta? IncomingDelta { get; init; }
    public PermanentCardPortraitSnapshot? LocalPortrait { get; init; }
    public ImportedCardPortrait? IncomingPortrait { get; init; }
    public required bool HasModificationConflict { get; init; }
    public required bool HasPortraitConflict { get; init; }
    public ModificationImportDecision Decision { get; private set; }
    public ModelId Id => Canonical.Id;
    public bool HasConflict => HasModificationConflict || HasPortraitConflict;
    public bool HasPortraitOnlyConflict => HasPortraitConflict && !HasModificationConflict;
    public bool IsSelected => Decision == ModificationImportDecision.UseIncoming;
    public bool HasUpgradeModification => IncomingDelta is { } delta && !delta.UpgradeModification.IsEmpty;

    public void InitializeDecision()
    {
        Decision = HasConflict
            ? ModificationImportDecision.Unresolved
            : ModificationImportDecision.UseIncoming;
    }

    public void SetDecision(ModificationImportDecision decision) => Decision = decision;

    public void Toggle()
    {
        Decision = Decision == ModificationImportDecision.UseIncoming
            ? ModificationImportDecision.KeepLocal
            : ModificationImportDecision.UseIncoming;
    }

    public CardModel? GetLocalPreview()
    {
        return _localPreview ??= CardModificationRuntime.CreateExactPermanentPreview(Id, LocalDelta);
    }

    public CardModel? GetIncomingPreview(bool upgraded = false)
    {
        if (upgraded && _upgradedIncomingPreview is not null)
            return _upgradedIncomingPreview;
        if (!upgraded && _incomingPreview is not null)
            return _incomingPreview;

        CardModel? preview = CardModificationRuntime.CreateExactPermanentPreview(
            Id,
            IncomingDelta ?? LocalDelta,
            upgraded);
        if (preview is not null && IncomingPortrait is not null)
        {
            CardPortraitRuntime.AttachPreview(
                preview,
                IncomingPortrait.GetDocument(),
                IncomingPortrait.Record.File,
                IncomingPortrait.Record.FrameId);
        }

        if (upgraded)
            _upgradedIncomingPreview = preview;
        else
            _incomingPreview = preview;
        return preview;
    }
}

public sealed class RelicModificationImportEntry
{
    private RelicModel? _localPreview;
    private RelicModel? _incomingPreview;

    public required RelicModel Canonical { get; init; }
    public RelicModificationState? LocalState { get; init; }
    public required RelicModificationState IncomingState { get; init; }
    public required bool HasConflict { get; init; }
    public ModificationImportDecision Decision { get; private set; }
    public ModelId Id => Canonical.Id;
    public bool IsSelected => Decision == ModificationImportDecision.UseIncoming;

    public void InitializeDecision()
    {
        Decision = HasConflict
            ? ModificationImportDecision.Unresolved
            : ModificationImportDecision.UseIncoming;
    }

    public void SetDecision(ModificationImportDecision decision) => Decision = decision;

    public void Toggle()
    {
        Decision = Decision == ModificationImportDecision.UseIncoming
            ? ModificationImportDecision.KeepLocal
            : ModificationImportDecision.UseIncoming;
    }

    public RelicModel GetLocalPreview()
    {
        return _localPreview ??= RelicModificationStateService.CreateExactPreviewRelic(
            Canonical,
            LocalState ?? new RelicModificationState());
    }

    public RelicModel GetIncomingPreview()
    {
        return _incomingPreview ??= RelicModificationStateService.CreateExactPreviewRelic(
            Canonical,
            IncomingState);
    }
}

public sealed class CardModificationImportSession
{
    public required IReadOnlyList<CardModificationImportEntry> Entries { get; init; }
    public bool AllConflictsResolved => Entries.All(entry => !entry.HasConflict || entry.Decision != ModificationImportDecision.Unresolved);

    public ModificationTransferResult Commit()
    {
        CardModificationImportEntry[] selected = Entries.Where(entry => entry.IsSelected).ToArray();
        List<PermanentCardPortraitImport> portraits = selected
            .Where(entry => entry.IncomingPortrait is not null)
            .Select(entry => new PermanentCardPortraitImport(
                entry.Id,
                Path.GetFileName(entry.IncomingPortrait!.Record.File),
                entry.IncomingPortrait.Record.FrameId,
                entry.IncomingPortrait.Record.Width,
                entry.IncomingPortrait.Record.Height,
                entry.IncomingPortrait.Record.Animated,
                entry.IncomingPortrait.Data))
            .ToList();
        if (!CardPortraitStore.ApplyPermanentImportsQuiet(portraits, out int portraitCount))
            return ModificationTransferResult.Failure(LocMan.Loc(
                "MOD_ARCHIVE_PORTRAIT_SAVE_FAILED",
                "The card portraits could not be saved."));
        if (portraitCount > 0)
            CardPortraitRuntime.PrepareQuietPermanentImport();

        Dictionary<ModelId, CardModificationDelta> deltas = selected
            .Where(entry => entry.IncomingDelta is not null)
            .ToDictionary(entry => entry.Id, entry => entry.IncomingDelta!.Clone());
        IReadOnlyList<ModelId> changed = PermanentCardModificationStore.ApplyProfileEntriesQuiet(deltas);
        CardModificationRuntime.ReconcileQuietPermanentImport(changed);
        HashSet<ModelId> refreshIds = new(changed);
        foreach (CardModificationImportEntry entry in selected)
        {
            if (entry.IncomingPortrait is not null)
                refreshIds.Add(entry.Id);
        }
        CardPrinter.RefreshImportedPermanentCards(refreshIds);
        return ModificationTransferResult.Success(selected.Length);
    }
}

public sealed class RelicModificationImportSession
{
    public required IReadOnlyList<RelicModificationImportEntry> Entries { get; init; }
    public bool AllConflictsResolved => Entries.All(entry => !entry.HasConflict || entry.Decision != ModificationImportDecision.Unresolved);

    public ModificationTransferResult Commit()
    {
        RelicModificationImportEntry[] selected = Entries.Where(entry => entry.IsSelected).ToArray();
        Dictionary<ModelId, RelicModificationState> states = selected.ToDictionary(
            entry => entry.Id,
            entry => entry.IncomingState.Clone());
        RelicModificationStateService.ApplyPermanentEntriesQuiet(states);
        return ModificationTransferResult.Success(selected.Length);
    }
}

public sealed record PortraitArchiveRecord(
    string CardId,
    string File,
    string FrameId,
    int Width,
    int Height,
    bool Animated,
    string Sha256);

public static class PermanentModificationArchiveService
{
    private const string Format = "loadout-permanent-modifications";
    private const int Version = 1;
    private const string ManifestEntry = "manifest.json";
    private const string CardsEntry = "cards.json";
    private const string RelicsEntry = "relics.json";
    private const string PortraitIndexEntry = "portraits/index.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<ModificationTransferResult> ExportCardsAsync(bool includePortraits)
    {
        string? path = await PickArchivePathAsync(save: true, "Loadout-Card-Modifications.zip");
        if (path is null)
            return ModificationTransferResult.Cancel();

        try
        {
            IReadOnlyDictionary<ModelId, PermanentCardPortraitSnapshot> portraits = includePortraits
                ? CardPortraitStore.GetPermanentSnapshot()
                : new Dictionary<ModelId, PermanentCardPortraitSnapshot>();
            IReadOnlyCollection<string> cardIds = PermanentCardModificationStore.GetProfileEntryIdsSnapshot();
            string cardsJson = PermanentCardModificationStore.ExportProfileSnapshotJson();
            WriteArchiveAtomically(path, archive =>
            {
                WriteJson(archive, ManifestEntry, new ArchiveManifest(Format, Version, "cards", includePortraits));
                WriteText(archive, CardsEntry, cardsJson);
                if (includePortraits)
                    WritePortraits(archive, portraits.Values);
            });
            HashSet<string> exportedIds = new(cardIds, StringComparer.Ordinal);
            exportedIds.UnionWith(portraits.Keys.Select(id => id.ToString()));
            int count = exportedIds.Count;
            return ModificationTransferResult.Success(count);
        }
        catch (Exception exception)
        {
            GD.PushError($"Loadout: card modification export failed. {exception}");
            return ModificationTransferResult.Failure(exception.Message);
        }
    }

    public static async Task<ModificationTransferResult> ExportRelicsAsync()
    {
        string? path = await PickArchivePathAsync(save: true, "Loadout-Relic-Modifications.zip");
        if (path is null)
            return ModificationTransferResult.Cancel();

        try
        {
            WriteArchiveAtomically(path, archive =>
            {
                WriteJson(archive, ManifestEntry, new ArchiveManifest(Format, Version, "relics", false));
                WriteText(archive, RelicsEntry, RelicModificationStateService.ExportPermanentArchiveSnapshot());
            });
            return ModificationTransferResult.Success(RelicModificationStateService.GetPermanentSnapshot().Count);
        }
        catch (Exception exception)
        {
            GD.PushError($"Loadout: relic modification export failed. {exception}");
            return ModificationTransferResult.Failure(exception.Message);
        }
    }

    public static async Task<ModificationTransferResult> ImportCardsAsync()
    {
        string? path = await PickArchivePathAsync(save: false, "Loadout-Card-Modifications.zip");
        if (path is null)
            return ModificationTransferResult.Cancel();

        try
        {
            CardModificationImportSession session = ReadCardSession(path);
            NCardModificationImportScreen screen = NCardModificationImportScreen.Create();
            screen.Initialize(session);
            if (!TryOpenModal(screen))
                return ModificationTransferResult.Failure(LocMan.Loc(
                    "MOD_ARCHIVE_MODAL_BUSY",
                    "The game's modal UI is unavailable or busy."));
            bool confirmed = await screen.Completion;
            return confirmed ? session.Commit() : ModificationTransferResult.Cancel();
        }
        catch (Exception exception)
        {
            GD.PushError($"Loadout: card modification import failed. {exception}");
            return ModificationTransferResult.Failure(exception.Message);
        }
    }

    public static async Task<ModificationTransferResult> ImportRelicsAsync()
    {
        string? path = await PickArchivePathAsync(save: false, "Loadout-Relic-Modifications.zip");
        if (path is null)
            return ModificationTransferResult.Cancel();

        try
        {
            RelicModificationImportSession session = ReadRelicSession(path);
            NRelicModificationImportScreen screen = NRelicModificationImportScreen.Create();
            screen.Initialize(session);
            if (!TryOpenModal(screen))
                return ModificationTransferResult.Failure(LocMan.Loc(
                    "MOD_ARCHIVE_MODAL_BUSY",
                    "The game's modal UI is unavailable or busy."));
            bool confirmed = await screen.Completion;
            return confirmed ? session.Commit() : ModificationTransferResult.Cancel();
        }
        catch (Exception exception)
        {
            GD.PushError($"Loadout: relic modification import failed. {exception}");
            return ModificationTransferResult.Failure(exception.Message);
        }
    }

    private static CardModificationImportSession ReadCardSession(string path)
    {
        using FileStream stream = new(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read);
        ValidateManifest(archive, "cards", out ArchiveManifest manifest);
        string cardsJson = ReadText(archive, CardsEntry);
        if (!PermanentCardModificationStore.TryDeserializeProfileSnapshot(cardsJson, out IReadOnlyDictionary<ModelId, CardModificationDelta> incomingDeltas))
            throw new InvalidDataException(LocMan.Loc(
                "MOD_ARCHIVE_INVALID_CARD_PAYLOAD",
                "The card modification payload is invalid."));

        Dictionary<ModelId, ImportedCardPortrait> incomingPortraits = manifest.IncludesPortraits
            ? ReadPortraits(archive)
            : new Dictionary<ModelId, ImportedCardPortrait>();
        IReadOnlyDictionary<ModelId, CardModificationDelta> localDeltas = PermanentCardModificationStore.GetProfileDeltasSnapshot();
        IReadOnlyDictionary<ModelId, PermanentCardPortraitSnapshot> localPortraits = CardPortraitStore.GetPermanentSnapshot();
        Dictionary<ModelId, CardModel> cards = ModelDb.AllCards.ToDictionary(card => card.Id);
        List<CardModificationImportEntry> entries = [];
        foreach (ModelId id in incomingDeltas.Keys.Union(incomingPortraits.Keys).Distinct())
        {
            if (!cards.TryGetValue(id, out CardModel? canonical))
                throw new InvalidDataException(LocMan.Loc(
                    "MOD_ARCHIVE_CARD_NOT_INSTALLED",
                    "Card '{0}' is not installed.",
                    id));
            incomingDeltas.TryGetValue(id, out CardModificationDelta? incomingDelta);
            localDeltas.TryGetValue(id, out CardModificationDelta? localDelta);
            incomingPortraits.TryGetValue(id, out ImportedCardPortrait? incomingPortrait);
            localPortraits.TryGetValue(id, out PermanentCardPortraitSnapshot? localPortrait);
            bool deltaConflict = incomingDelta is not null
                                 && localDelta is not null
                                 && !CardModificationRuntime.PermanentDeltasEquivalent(localDelta, incomingDelta);
            bool portraitConflict = incomingPortrait is not null
                                    && localPortrait is not null
                                    && !string.Equals(
                                        HashFile(localPortrait.GlobalPath),
                                        incomingPortrait.Record.Sha256,
                                        StringComparison.OrdinalIgnoreCase);
            CardModificationImportEntry entry = new()
            {
                Canonical = canonical,
                LocalDelta = localDelta?.Clone(),
                IncomingDelta = incomingDelta?.Clone(),
                LocalPortrait = localPortrait,
                IncomingPortrait = incomingPortrait,
                HasModificationConflict = deltaConflict,
                HasPortraitConflict = portraitConflict
            };
            entry.InitializeDecision();
            entries.Add(entry);
        }

        return new CardModificationImportSession
        {
            Entries = entries.OrderBy(entry => SafeCardTitle(entry.Canonical), StringComparer.Ordinal).ToArray()
        };
    }

    private static RelicModificationImportSession ReadRelicSession(string path)
    {
        using FileStream stream = new(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read);
        ValidateManifest(archive, "relics", out _);
        string relicsJson = ReadText(archive, RelicsEntry);
        if (!RelicModificationStateService.TryDeserializePermanentArchiveSnapshot(
                relicsJson,
                out IReadOnlyDictionary<ModelId, RelicModificationState> incomingStates))
        {
            throw new InvalidDataException(LocMan.Loc(
                "MOD_ARCHIVE_INVALID_RELIC_PAYLOAD",
                "The relic modification payload is invalid."));
        }

        IReadOnlyDictionary<ModelId, RelicModificationState> localStates = RelicModificationStateService.GetPermanentSnapshot();
        Dictionary<ModelId, RelicModel> relics = ModelDb.AllRelics.ToDictionary(relic => relic.Id);
        List<RelicModificationImportEntry> entries = [];
        foreach ((ModelId id, RelicModificationState incoming) in incomingStates)
        {
            if (!relics.TryGetValue(id, out RelicModel? canonical))
                throw new InvalidDataException(LocMan.Loc(
                    "MOD_ARCHIVE_RELIC_NOT_INSTALLED",
                    "Relic '{0}' is not installed.",
                    id));
            localStates.TryGetValue(id, out RelicModificationState? local);
            RelicModificationImportEntry entry = new()
            {
                Canonical = canonical,
                LocalState = local?.Clone(),
                IncomingState = incoming.Clone(),
                HasConflict = local is not null && !RelicModificationStateService.StatesEquivalent(local, incoming)
            };
            entry.InitializeDecision();
            entries.Add(entry);
        }

        return new RelicModificationImportSession
        {
            Entries = entries.OrderBy(entry => SafeRelicTitle(entry.Canonical), StringComparer.Ordinal).ToArray()
        };
    }

    private static Dictionary<ModelId, ImportedCardPortrait> ReadPortraits(ZipArchive archive)
    {
        PortraitArchiveIndex index = JsonSerializer.Deserialize<PortraitArchiveIndex>(ReadText(archive, PortraitIndexEntry), JsonOptions)
                                     ?? throw new InvalidDataException(LocMan.Loc(
                                         "MOD_ARCHIVE_INVALID_PORTRAIT_INDEX",
                                         "The portrait index is invalid."));
        if (index.Portraits is null)
            throw new InvalidDataException(LocMan.Loc(
                "MOD_ARCHIVE_INVALID_PORTRAIT_INDEX",
                "The portrait index is invalid."));
        Dictionary<string, CardModel> cards = ModelDb.AllCards.ToDictionary(card => card.Id.ToString(), StringComparer.Ordinal);
        Dictionary<ModelId, ImportedCardPortrait> portraits = new();
        foreach (PortraitArchiveRecord record in index.Portraits)
        {
            if (!IsPortraitArchiveEntry(record.File))
                throw new InvalidDataException(LocMan.Loc(
                    "MOD_ARCHIVE_INVALID_PORTRAIT_INDEX",
                    "The portrait index is invalid."));
            if (!cards.TryGetValue(record.CardId, out CardModel? card))
                throw new InvalidDataException(LocMan.Loc(
                    "MOD_ARCHIVE_CARD_NOT_INSTALLED",
                    "Card '{0}' is not installed.",
                    record.CardId));
            byte[] data = ReadBytes(archive, record.File);
            string hash = Convert.ToHexString(SHA256.HashData(data));
            if (!string.Equals(hash, record.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(LocMan.Loc(
                    "MOD_ARCHIVE_PORTRAIT_DAMAGED",
                    "Portrait for '{0}' is damaged.",
                    record.CardId));
            portraits[card.Id] = new ImportedCardPortrait(record, data);
        }
        return portraits;
    }

    private static void WritePortraits(ZipArchive archive, IEnumerable<PermanentCardPortraitSnapshot> portraits)
    {
        List<PortraitArchiveRecord> records = [];
        int index = 0;
        foreach (PermanentCardPortraitSnapshot portrait in portraits.OrderBy(portrait => portrait.CardId.ToString(), StringComparer.Ordinal))
        {
            string extension = Path.GetExtension(portrait.FileName).ToLowerInvariant();
            string entryName = $"portraits/assets/{index:D4}{extension}";
            string hash = HashFile(portrait.GlobalPath);
            records.Add(new PortraitArchiveRecord(
                portrait.CardId.ToString(),
                entryName,
                portrait.FrameId,
                portrait.Width,
                portrait.Height,
                portrait.Animated,
                hash));
            ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
            using Stream output = entry.Open();
            using FileStream input = new(portrait.GlobalPath, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);
            input.CopyTo(output);
            index++;
        }
        WriteJson(archive, PortraitIndexEntry, new PortraitArchiveIndex(records));
    }

    private static void ValidateManifest(ZipArchive archive, string kind, out ArchiveManifest manifest)
    {
        manifest = JsonSerializer.Deserialize<ArchiveManifest>(ReadText(archive, ManifestEntry), JsonOptions)
                   ?? throw new InvalidDataException("The archive manifest is invalid.");
        if (!string.Equals(manifest.Format, Format, StringComparison.Ordinal)
            || manifest.Version != Version
            || !string.Equals(manifest.Kind, kind, StringComparison.Ordinal))
        {
            throw new InvalidDataException(LocMan.Loc(
                "MOD_ARCHIVE_UNSUPPORTED",
                "The selected archive is not a supported Loadout modification package."));
        }
    }

    private static bool TryOpenModal(Node screen)
    {
        NModalContainer? modal = NModalContainer.Instance;
        if (modal is null || !GodotObject.IsInstanceValid(modal) || modal.OpenModal is not null)
            return false;
        modal.Add(screen, showBackstop: false);
        return ReferenceEquals(modal.OpenModal, screen);
    }

    private static Task<string?> PickArchivePathAsync(bool save, string defaultName)
    {
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root is null)
            return Task.FromResult<string?>(null);

        TaskCompletionSource<string?> completion = new();
        FileDialog dialog = new()
        {
            Name = save ? "LoadoutModificationExportDialog" : "LoadoutModificationImportDialog",
            Title = save
                ? LocMan.Loc("MOD_EXPORT_DIALOG_TITLE", "Export Loadout Modifications")
                : LocMan.Loc("MOD_IMPORT_DIALOG_TITLE", "Import Loadout Modifications"),
            ModeOverridesTitle = false,
            FileMode = save ? FileDialog.FileModeEnum.SaveFile : FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = true,
            Exclusive = true,
            CurrentFile = defaultName,
            Filters = ["*.zip;ZIP Archives;application/zip"]
        };
        string documents = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);
        if (Directory.Exists(documents))
            dialog.CurrentDir = documents;

        void Complete(string? selected)
        {
            if (!completion.TrySetResult(selected))
                return;
            if (GodotObject.IsInstanceValid(dialog))
                dialog.QueueFree();
        }

        dialog.FileSelected += selected => Complete(save ? EnsureZipExtension(selected) : selected);
        dialog.Canceled += () => Complete(null);
        dialog.CloseRequested += () => Complete(null);
        tree.Root.AddChild(dialog);
        dialog.PopupCentered(new Vector2I(960, 720));
        return completion.Task;
    }

    private static string EnsureZipExtension(string path)
    {
        return string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase)
            ? path
            : Path.ChangeExtension(path, ".zip");
    }

    private static void WriteArchiveAtomically(string path, Action<ZipArchive> write)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
                           ?? throw new InvalidOperationException("The export directory is invalid.");
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, System.IO.FileAccess.Write, FileShare.None))
            using (ZipArchive archive = new(stream, ZipArchiveMode.Create))
                write(archive);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void WriteJson<T>(ZipArchive archive, string name, T value)
    {
        WriteBytes(archive, name, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), CompressionLevel.Fastest);
    }

    private static void WriteText(ZipArchive archive, string name, string value)
    {
        WriteBytes(archive, name, Encoding.UTF8.GetBytes(value), CompressionLevel.Fastest);
    }

    private static void WriteBytes(ZipArchive archive, string name, byte[] value, CompressionLevel compression)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, compression);
        using Stream stream = entry.Open();
        stream.Write(value);
    }

    private static string ReadText(ZipArchive archive, string name)
    {
        return Encoding.UTF8.GetString(ReadBytes(archive, name));
    }

    private static byte[] ReadBytes(ZipArchive archive, string name)
    {
        ZipArchiveEntry entry = archive.GetEntry(name)
                                ?? throw new InvalidDataException(LocMan.Loc(
                                    "MOD_ARCHIVE_ENTRY_MISSING",
                                    "Archive entry '{0}' is missing.",
                                    name));
        using Stream input = entry.Open();
        using MemoryStream output = new();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static string HashFile(string path)
    {
        using FileStream stream = new(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool IsPortraitArchiveEntry(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathFullyQualified(name))
            return false;
        string normalized = name.Replace('\\', '/');
        if (!normalized.StartsWith("portraits/", StringComparison.Ordinal)
            || normalized.Split('/').Any(part => part is "" or "." or ".."))
        {
            return false;
        }
        string extension = Path.GetExtension(normalized);
        return string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
               || string.Equals(extension, ImageAnimationPackage.Extension, StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeCardTitle(CardModel card)
    {
        try { return card.Title; }
        catch { return card.Id.Entry; }
    }

    private static string SafeRelicTitle(RelicModel relic)
    {
        try { return CommonHelpers.FormatRelicTitle(relic); }
        catch { return relic.Id.Entry; }
    }

    private sealed record ArchiveManifest(string Format, int Version, string Kind, bool IncludesPortraits);
    private sealed record PortraitArchiveIndex(IReadOnlyList<PortraitArchiveRecord> Portraits);
}
