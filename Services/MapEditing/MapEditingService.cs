#nullable enable

namespace Loadout.Services.MapEditing;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using Loadout.Services.Compatibility;
using Loadout.Services.Networking;
using Loadout.UI.MapEditing;
using Loadout.UI.Managers;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Odds;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

public static class MapEditingService
{
    public const int CurrentSchemaVersion = 2;
    public const string ClipboardPrefix = "STS2_LOADOUT_MAP_V2:";
    public const int MaxArchiveBytes = 512 * 1024;

    private static readonly FieldInfo? RunStateRngField =
        AccessTools.Field(typeof(RunState), "<Rng>k__BackingField");
    private static readonly FieldInfo? RunStateOddsField =
        AccessTools.Field(typeof(RunState), "<Odds>k__BackingField");
    private static readonly FieldInfo? PlayerRngField =
        AccessTools.Field(typeof(Player), "<PlayerRng>k__BackingField");
    private static readonly FieldInfo? PlayerOddsField =
        AccessTools.Field(typeof(Player), "<PlayerOdds>k__BackingField");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly ConditionalWeakTable<RunState, MapEditingArchive> Archives = new();
    private static readonly ConditionalWeakTable<RunState, FutureMapBackups> FutureMapBackupsByRun = new();
    private static INetGameService? _runNetService;
    private static RunLobby? _runLobby;
    private static Delegate? _playerRejoinedHandler;
    private static string? _pendingSnapshotJson;

    public static string GetSerializedRunState(RunState runState)
    {
        if (!Archives.TryGetValue(runState, out MapEditingArchive? archive) || archive.Acts.Count == 0)
            return string.Empty;

        return JsonSerializer.Serialize(archive, JsonOptions);
    }

    public static void LoadSerializedRunState(RunState runState, string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return;

        try
        {
            MapEditingArchive? archive = JsonSerializer.Deserialize<MapEditingArchive>(payload, JsonOptions);
            if (archive is null || !TryValidateArchive(archive, runState, requireKnownActs: false, out _))
                return;

            Archives.Remove(runState);
            Archives.Add(runState, archive);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Loadout map editor: could not restore run archive. {exception.Message}");
        }
    }

    public static void PrepareRunLaunch()
    {
        RunManager manager = RunManager.Instance;
        RegisterRunNetService(manager.NetService);
        BindRunLobby(manager.RunLobby);
    }

    public static void OnRunLaunched()
    {
        RunState? runState = TryGetRunState();
        if (runState is null)
            return;

        if (_runNetService?.Type == NetGameType.Client && _pendingSnapshotJson is { } pending)
        {
            _pendingSnapshotJson = null;
            ApplySnapshotJson(runState, pending);
        }
        QueueFutureMaps(runState);
        if (_runNetService?.Type == NetGameType.Host)
            BroadcastSnapshot(runState);
    }

    public static void OnRunCleaningUp()
    {
        MapEditingUiService.Detach();
        _pendingSnapshotJson = null;
        UnbindRunLobby();
        UnregisterRunNetService();
    }

    public static MapEditingActArchive CaptureCurrentAct(
        RunState runState,
        IReadOnlyDictionary<string, MapEditingPosition> positions)
    {
        return new MapEditingActArchive
        {
            ActIndex = runState.CurrentActIndex,
            ActModelId = runState.Act.Id.ToString(),
            Map = SerializableActMap.FromActMap(runState.Map),
            Positions = positions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        };
    }

    public static bool TryCaptureHistoryState(
        RunState runState,
        IReadOnlyDictionary<string, MapEditingPosition> positions,
        out MapEditingHistoryState state,
        out string error)
    {
        try
        {
            MapEditingArchive archive = Archives.TryGetValue(runState, out MapEditingArchive? current)
                ? JsonSerializer.Deserialize<MapEditingArchive>(
                    JsonSerializer.Serialize(current, JsonOptions),
                    JsonOptions) ?? new MapEditingArchive()
                : CreateArchive(runState);
            archive.RunSeed = runState.Rng.StringSeed;
            archive.Acts[runState.CurrentActIndex] = CaptureCurrentAct(runState, positions);

            Dictionary<int, SerializableActMap> pending = RunManager.Instance.SavedMapsToLoad ?? [];
            state = new MapEditingHistoryState
            {
                ArchiveJson = JsonSerializer.Serialize(archive, JsonOptions),
                PendingMapsJson = JsonSerializer.Serialize(pending, JsonOptions),
                CurrentActQuests = CaptureQuests(runState.Map)
            };
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            state = new MapEditingHistoryState();
            error = $"could not capture map editor history: {exception.Message}";
            return false;
        }
    }

    public static bool TryRestoreHistoryState(
        RunState runState,
        NMapScreen screen,
        MapEditingHistoryState state,
        out string error)
    {
        try
        {
            error = string.Empty;
            MapEditingArchive? archive = JsonSerializer.Deserialize<MapEditingArchive>(state.ArchiveJson, JsonOptions);
            Dictionary<int, SerializableActMap>? pending =
                JsonSerializer.Deserialize<Dictionary<int, SerializableActMap>>(state.PendingMapsJson, JsonOptions);
            if (archive is null || pending is null
                || !TryValidateArchive(archive, runState, requireKnownActs: true, out error)
                || !archive.Acts.TryGetValue(runState.CurrentActIndex, out MapEditingActArchive? currentAct))
            {
                if (string.IsNullOrWhiteSpace(error))
                    error = "map editor history is incomplete";
                return false;
            }

            long currentRevision = Archives.TryGetValue(runState, out MapEditingArchive? current)
                ? current.Revision
                : 0L;
            archive.SchemaVersion = CurrentSchemaVersion;
            archive.Revision = Math.Max(currentRevision, archive.Revision) + 1L;
            if (!TryReplaceRunSeed(runState, archive.RunSeed, out error))
                return false;
            NMapEditingToolbar.ApplyActIncrementally(runState, screen, currentAct, state.CurrentActQuests);
            Archives.Remove(runState);
            Archives.Add(runState, archive);
            RunManager.Instance.SavedMapsToLoad = pending;
            FutureMapBackupsByRun.Remove(runState);
            BroadcastSnapshot(runState);
            SaveCurrentRun();
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = $"could not restore map editor history: {exception.Message}";
            return false;
        }
    }

    public static IReadOnlyDictionary<string, MapEditingPosition> GetCurrentPositions(RunState runState)
    {
        return Archives.TryGetValue(runState, out MapEditingArchive? archive)
               && archive.Acts.TryGetValue(runState.CurrentActIndex, out MapEditingActArchive? act)
            ? act.Positions
            : new Dictionary<string, MapEditingPosition>(StringComparer.Ordinal);
    }

    public static bool IsCurrentActEdited(RunState runState)
        => Archives.TryGetValue(runState, out MapEditingArchive? archive)
           && archive.Acts.ContainsKey(runState.CurrentActIndex);

    public static bool CommitCurrentAct(
        RunState runState,
        SerializableActMap map,
        IReadOnlyDictionary<string, MapEditingPosition> positions,
        out string error)
    {
        MapEditingActArchive act = new()
        {
            ActIndex = runState.CurrentActIndex,
            ActModelId = runState.Act.Id.ToString(),
            Map = map,
            Positions = positions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        };

        if (!TryValidateAct(act, out error))
            return false;

        MapEditingArchive archive = GetOrCreateArchive(runState);
        bool hadPrevious = archive.Acts.TryGetValue(runState.CurrentActIndex, out MapEditingActArchive? previous);
        long previousRevision = archive.Revision;
        archive.SchemaVersion = CurrentSchemaVersion;
        archive.RunSeed = runState.Rng.StringSeed;
        archive.Revision++;
        archive.Acts[runState.CurrentActIndex] = act;
        if (JsonSerializer.SerializeToUtf8Bytes(archive, JsonOptions).Length > MaxArchiveBytes)
        {
            archive.Revision = previousRevision;
            if (hadPrevious)
                archive.Acts[runState.CurrentActIndex] = previous!;
            else
                archive.Acts.Remove(runState.CurrentActIndex);
            error = "map archive exceeds the sharing and network size limit";
            return false;
        }
        BroadcastSnapshot(runState);
        SaveCurrentRun();
        return true;
    }

    public static bool CopyToClipboard(RunState runState, out string message)
    {
        if (!Archives.TryGetValue(runState, out MapEditingArchive? archive) || archive.Acts.Count == 0)
        {
            message = LocMan.Loc("MAP_EDITOR_NOTHING_TO_COPY", "No edited maps to copy.");
            return false;
        }

        try
        {
            archive.RunSeed = runState.Rng.StringSeed;
            string json = JsonSerializer.Serialize(archive, JsonOptions);
            byte[] payload = Encoding.UTF8.GetBytes(json);
            using MemoryStream output = new();
            using (GZipStream gzip = new(output, CompressionLevel.SmallestSize, leaveOpen: true))
                gzip.Write(payload, 0, payload.Length);

            DisplayServer.ClipboardSet(ClipboardPrefix + ToBase64Url(output.ToArray()));
            message = LocMan.Loc(
                "MAP_EDITOR_COPIED",
                "Copied {0} edited act map(s).",
                archive.Acts.Count);
            return true;
        }
        catch (Exception exception)
        {
            message = LocMan.Loc("MAP_EDITOR_COPY_FAILED", "Could not copy maps: {0}", exception.Message);
            return false;
        }
    }

    public static bool CanImportCurrentAct(RunState runState)
    {
        MapCoord? current = runState.CurrentMapCoord;
        return !current.HasValue || current.Value == runState.Map.StartingMapPoint.coord;
    }

    public static async Task<(bool Success, string Message)> ImportFromClipboard(
        RunState runState,
        NMapScreen screen)
    {
        if (!CanImportCurrentAct(runState))
        {
            return (false, LocMan.Loc(
                "MAP_EDITOR_IMPORT_FIRST_FLOOR",
                "Maps can only be imported while you are still in the first room of the current act."));
        }

        string previousSeed = runState.Rng.StringSeed;
        ActMap previousMap = runState.Map;
        bool seedChanged = false;
        try
        {
            string text = DisplayServer.ClipboardGet().Trim();
            if (text.Length > MaxArchiveBytes * 2)
            {
                return (false, LocMan.Loc(
                    "MAP_EDITOR_IMPORT_INVALID",
                    "Could not import maps: {0}",
                    "archive is too large"));
            }
            if (!text.StartsWith(ClipboardPrefix, StringComparison.Ordinal))
            {
                return (false, LocMan.Loc(
                    "MAP_EDITOR_CLIPBOARD_INVALID",
                    "Clipboard does not contain a Loadout map archive."));
            }

            byte[] compressed = FromBase64Url(text[ClipboardPrefix.Length..]);
            using MemoryStream input = new(compressed);
            using GZipStream gzip = new(input, CompressionMode.Decompress);
            using MemoryStream output = new();
            CopyWithLimit(gzip, output, MaxArchiveBytes);
            MapEditingArchive? imported = JsonSerializer.Deserialize<MapEditingArchive>(
                Encoding.UTF8.GetString(output.ToArray()),
                JsonOptions);
            if (imported is null)
            {
                return (false, LocMan.Loc(
                    "MAP_EDITOR_IMPORT_INVALID",
                    "Could not import maps: {0}",
                    "empty archive"));
            }
            if (!TryValidateArchive(imported, runState, requireKnownActs: false, out string error))
            {
                return (false, LocMan.Loc("MAP_EDITOR_IMPORT_INVALID", "Could not import maps: {0}", error));
            }

            (int ActIndex, MapEditingActArchive Act)[] compatibleActs = imported.Acts
                .OrderBy(pair => pair.Key)
                .Where(pair => pair.Key >= runState.CurrentActIndex
                               && pair.Key < runState.Acts.Count
                               && string.Equals(
                                   runState.Acts[pair.Key].Id.ToString(),
                                   pair.Value.ActModelId,
                                   StringComparison.Ordinal))
                .Select(pair => (pair.Key, pair.Value))
                .ToArray();
            if (compatibleActs.Length == 0)
            {
                return (false, LocMan.Loc(
                    "MAP_EDITOR_IMPORT_INVALID",
                    "Could not import maps: {0}",
                    "archive has no compatible current or future act maps"));
            }

            seedChanged = !string.Equals(previousSeed, imported.RunSeed, StringComparison.Ordinal);
            if (!TryReplaceRunSeed(runState, imported.RunSeed, out error))
                return (false, LocMan.Loc("MAP_EDITOR_IMPORT_INVALID", "Could not import maps: {0}", error));

            Dictionary<int, SerializableActMap>? pending = RunManager.Instance.SavedMapsToLoad;
            pending?.Remove(runState.CurrentActIndex);
            await RunManager.Instance.GenerateMap();

            MapEditingArchive local = GetOrCreateArchive(runState);
            local.RunSeed = imported.RunSeed;
            foreach (int actIndex in local.Acts.Keys
                         .Where(index => index >= runState.CurrentActIndex)
                         .ToArray())
            {
                local.Acts.Remove(actIndex);
            }
            int applied = 0;
            int queued = 0;
            int skipped = imported.Acts.Count - compatibleActs.Length;
            bool importedCurrentAct = false;
            foreach ((int actIndex, MapEditingActArchive act) in compatibleActs)
            {
                local.Acts[actIndex] = act;
                if (actIndex == runState.CurrentActIndex)
                {
                    ApplyAct(runState, screen, act);
                    importedCurrentAct = true;
                    applied++;
                }
                else
                {
                    queued++;
                }
            }
            if (!importedCurrentAct)
            {
                local.Acts[runState.CurrentActIndex] = CaptureCurrentAct(
                    runState,
                    new Dictionary<string, MapEditingPosition>(StringComparer.Ordinal));
                applied++;
            }

            local.SchemaVersion = CurrentSchemaVersion;
            local.Revision++;
            QueueFutureMaps(runState);
            BroadcastSnapshot(runState);
            SaveCurrentRun();
            return (true, LocMan.Loc(
                "MAP_EDITOR_IMPORTED",
                "Imported maps: {0} applied, {1} queued, {2} skipped.",
                applied,
                queued,
                skipped));
        }
        catch (Exception exception)
        {
            if (seedChanged)
                TryReplaceRunSeed(runState, previousSeed, out _);
            if (!ReferenceEquals(runState.Map, previousMap))
            {
                runState.Map = previousMap;
                screen.SetMap(previousMap, runState.Rng.Seed, clearDrawings: false);
            }
            return (false, LocMan.Loc(
                "MAP_EDITOR_IMPORT_INVALID",
                "Could not import maps: {0}",
                exception.Message));
        }
    }

    public static string KeyFor(MapPoint point, ActMap map)
    {
        if (ReferenceEquals(point, map.StartingMapPoint))
            return "start";
        if (ReferenceEquals(point, map.BossMapPoint))
            return "boss";
        if (ReferenceEquals(point, map.SecondBossMapPoint))
            return "boss2";
        return $"p:{point.coord.col}:{point.coord.row}";
    }

    private static MapEditingArchive GetOrCreateArchive(RunState runState)
        => Archives.GetValue(runState, CreateArchive);

    private static MapEditingArchive CreateArchive(RunState runState)
        => new() { RunSeed = runState.Rng.StringSeed };

    private static void ApplyAct(RunState runState, NMapScreen screen, MapEditingActArchive act)
    {
        ApplyAct(runState, screen, act, CaptureQuests(runState.Map));
    }

    private static void ApplyAct(
        RunState runState,
        NMapScreen screen,
        MapEditingActArchive act,
        IReadOnlyDictionary<MapCoord, List<AbstractModel>> quests)
    {
        SavedActMap rebuilt = new(act.Map);
        RestoreQuests(rebuilt, quests);
        runState.Map = rebuilt;
        screen.SetMap(rebuilt, runState.Rng.Seed, clearDrawings: false);
    }

    private static Dictionary<MapCoord, List<AbstractModel>> CaptureQuests(ActMap map)
    {
        Dictionary<MapCoord, List<AbstractModel>> quests = new();
        foreach (MapPoint point in EnumerateIncludingAnchors(map))
        {
            if (point.Quests.Count > 0)
                quests[point.coord] = point.Quests.ToList();
        }
        return quests;
    }

    private static void RestoreQuests(ActMap map, IReadOnlyDictionary<MapCoord, List<AbstractModel>> quests)
    {
        foreach ((MapCoord coord, List<AbstractModel> models) in quests)
        {
            MapPoint? point = map.GetPoint(coord);
            if (point is null)
                continue;
            foreach (AbstractModel model in models)
                point.AddQuest(model);
        }
    }

    private static IEnumerable<MapPoint> EnumerateIncludingAnchors(ActMap map)
    {
        yield return map.StartingMapPoint;
        foreach (MapPoint point in map.GetAllMapPoints())
            yield return point;
        yield return map.BossMapPoint;
        if (map.SecondBossMapPoint is not null)
            yield return map.SecondBossMapPoint;
    }

    private static void QueueFutureMaps(RunState runState)
    {
        if (!Archives.TryGetValue(runState, out MapEditingArchive? archive))
            return;

        Dictionary<int, SerializableActMap> pending = RunManager.Instance.SavedMapsToLoad ??= [];
        FutureMapBackups backups = FutureMapBackupsByRun.GetValue(runState, static _ => new FutureMapBackups());
        foreach (int actIndex in backups.Maps.Keys.ToArray())
        {
            bool remainsEdited = archive.Acts.TryGetValue(actIndex, out MapEditingActArchive? archivedAct)
                                 && actIndex > runState.CurrentActIndex
                                 && actIndex < runState.Acts.Count
                                 && string.Equals(
                                     runState.Acts[actIndex].Id.ToString(),
                                     archivedAct.ActModelId,
                                     StringComparison.Ordinal);
            if (remainsEdited)
                continue;

            SerializableActMap? original = backups.Maps[actIndex];
            if (original is null)
                pending.Remove(actIndex);
            else
                pending[actIndex] = original;
            backups.Maps.Remove(actIndex);
        }

        foreach ((int actIndex, MapEditingActArchive act) in archive.Acts)
        {
            if (actIndex > runState.CurrentActIndex
                && actIndex < runState.Acts.Count
                && string.Equals(runState.Acts[actIndex].Id.ToString(), act.ActModelId, StringComparison.Ordinal))
            {
                if (!backups.Maps.ContainsKey(actIndex))
                    backups.Maps[actIndex] = pending.GetValueOrDefault(actIndex);
                pending[actIndex] = act.Map;
            }
        }
    }

    private static bool TryReplaceRunSeed(RunState runState, string seed, out string error)
    {
        error = string.Empty;
        if (string.Equals(runState.Rng.StringSeed, seed, StringComparison.Ordinal))
            return true;
        if (string.IsNullOrWhiteSpace(seed))
        {
            error = "archive run seed is missing";
            return false;
        }
        if (RunStateRngField is null || RunStateOddsField is null
            || PlayerRngField is null || PlayerOddsField is null)
        {
            error = "this game version does not expose the seed state required for map import";
            return false;
        }

        RunRngSet previousRng = runState.Rng;
        RunOddsSet previousOdds = runState.Odds;
        List<PlayerSeedState> playerStates = runState.Players
            .Select(player => new PlayerSeedState(player, player.PlayerRng, player.PlayerOdds))
            .ToList();

        try
        {
            SerializableRunRngSet serializedRunRng = previousRng.ToSerializable();
            serializedRunRng.Seed = seed;
            RunRngSet replacementRng = RunRngSet.FromSave(serializedRunRng);
            RunOddsSet replacementOdds = RunOddsSet.FromSerializable(
                previousOdds.ToSerializable(),
                replacementRng.UnknownMapPoint);

            List<PlayerSeedState> replacements = [];
            foreach (PlayerSeedState previous in playerStates)
            {
                SerializablePlayerRngSet serializedPlayerRng = previous.Rng.ToSerializable();
                serializedPlayerRng.Seed = unchecked(
                    (uint)StringHelper.GetDeterministicHashCode(seed)
                    + (uint)runState.GetPlayerSlotIndex(previous.Player));
                PlayerRngSet replacementPlayerRng = PlayerRngSet.FromSerializable(serializedPlayerRng);
                PlayerOddsSet replacementPlayerOdds = PlayerOddsSet.FromSerializable(
                    previous.Odds.ToSerializable(),
                    replacementPlayerRng);
                replacements.Add(new PlayerSeedState(
                    previous.Player,
                    replacementPlayerRng,
                    replacementPlayerOdds));
            }

            RunStateRngField.SetValue(runState, replacementRng);
            RunStateOddsField.SetValue(runState, replacementOdds);
            foreach (PlayerSeedState replacement in replacements)
            {
                PlayerRngField.SetValue(replacement.Player, replacement.Rng);
                PlayerOddsField.SetValue(replacement.Player, replacement.Odds);
            }
            return true;
        }
        catch (Exception exception)
        {
            try
            {
                RunStateRngField.SetValue(runState, previousRng);
                RunStateOddsField.SetValue(runState, previousOdds);
                foreach (PlayerSeedState previous in playerStates)
                {
                    PlayerRngField.SetValue(previous.Player, previous.Rng);
                    PlayerOddsField.SetValue(previous.Player, previous.Odds);
                }
            }
            catch
            {
                // Preserve the original failure below; a partial reflection failure is already non-recoverable here.
            }
            error = $"could not replace the run seed: {exception.Message}";
            return false;
        }
    }

    private static bool TryValidateArchive(
        MapEditingArchive archive,
        RunState runState,
        bool requireKnownActs,
        out string error)
    {
        if (archive.SchemaVersion != CurrentSchemaVersion)
        {
            error = $"unsupported schema {archive.SchemaVersion}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(archive.RunSeed) || archive.RunSeed.Length > 256)
        {
            error = "archive run seed is missing or invalid";
            return false;
        }

        if (archive.Acts.Count > 16)
        {
            error = "too many acts";
            return false;
        }

        foreach ((int key, MapEditingActArchive act) in archive.Acts)
        {
            if (key != act.ActIndex)
            {
                error = $"act key {key} does not match its payload";
                return false;
            }
            if (!TryValidateAct(act, out error))
                return false;

            if (requireKnownActs
                && (key < 0 || key >= runState.Acts.Count
                    || !string.Equals(runState.Acts[key].Id.ToString(), act.ActModelId, StringComparison.Ordinal)))
            {
                error = $"act {key} does not match this run";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool TryValidateAct(MapEditingActArchive act, out string error)
    {
        SerializableActMap map = act.Map;
        if (map is null || map.StartingPoint is null || map.BossPoint is null || string.IsNullOrWhiteSpace(act.ActModelId))
        {
            error = "required map anchors are missing";
            return false;
        }

        if (map.GridWidth is < 1 or > 255 || map.GridHeight is < 1 or > 255 || map.Points.Count > ushort.MaxValue)
        {
            error = "map dimensions exceed native limits";
            return false;
        }

        List<SerializableMapPoint> all = [map.StartingPoint, .. map.Points, map.BossPoint];
        if (map.SecondBossPoint is not null)
            all.Add(map.SecondBossPoint);

        HashSet<MapCoord> coords = [];
        foreach (SerializableMapPoint point in map.Points)
        {
            if (point.Coord.col >= map.GridWidth || point.Coord.row >= map.GridHeight)
            {
                error = "an ordinary node is outside the native map grid";
                return false;
            }
        }
        foreach (SerializableMapPoint point in all)
        {
            if (point.Coord.col is < 0 or > 255 || point.Coord.row is < 0 or > 255
                || !Enum.IsDefined(point.PointType)
                || point.ChildCoords is { Count: > 255 }
                || !coords.Add(point.Coord))
            {
                error = "map contains an invalid or duplicate node";
                return false;
            }
        }

        if (map.StartMapPointCoords is { Count: > 255 })
        {
            error = "map has too many starting nodes";
            return false;
        }

        foreach (SerializableMapPoint point in all)
        {
            if (point.ChildCoords is not null && point.ChildCoords.Any(child => !coords.Contains(child)))
            {
                error = "a connection points to a missing node";
                return false;
            }
        }

        foreach (MapEditingPosition position in act.Positions.Values)
        {
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y))
            {
                error = "map contains an invalid visual position";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static RunState? TryGetRunState()
    {
        try
        {
            return RunManager.Instance.IsInProgress ? RunManager.Instance.DebugOnlyGetState() : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCurrentRun()
    {
        try
        {
            TaskHelper.RunSafely(SaveManager.Instance.SaveRun(null));
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Loadout map editor: could not request a run save. {exception.Message}");
        }
    }

    private static void RegisterRunNetService(INetGameService netService)
    {
        if (ReferenceEquals(_runNetService, netService))
            return;

        UnregisterRunNetService();
        _runNetService = netService;
        _runNetService.RegisterMessageHandler<MapEditingSnapshotMessage>(HandleSnapshot);
    }

    private static void UnregisterRunNetService()
    {
        if (_runNetService is null)
            return;
        _runNetService.UnregisterMessageHandler<MapEditingSnapshotMessage>(HandleSnapshot);
        _runNetService = null;
    }

    private static void BindRunLobby(RunLobby? lobby)
    {
        if (ReferenceEquals(_runLobby, lobby))
            return;
        UnbindRunLobby();
        _runLobby = lobby;
        if (_runLobby is not null)
            _playerRejoinedHandler = Sts2Compatibility.SubscribeRunLobbyPlayerRejoined(_runLobby, OnPlayerRejoined);
    }

    private static void UnbindRunLobby()
    {
        if (_runLobby is null)
            return;
        if (_playerRejoinedHandler is not null)
            Sts2Compatibility.UnsubscribeRunLobbyPlayerRejoined(_runLobby, _playerRejoinedHandler);
        _playerRejoinedHandler = null;
        _runLobby = null;
    }

    private static void OnPlayerRejoined(ulong playerId)
    {
        RunState? runState = TryGetRunState();
        if (_runNetService?.Type == NetGameType.Host && runState is not null && playerId != _runNetService.NetId)
            SendSnapshot(runState, playerId);
    }

    private static void BroadcastSnapshot(RunState runState)
    {
        if (_runNetService?.Type != NetGameType.Host
            || !Archives.TryGetValue(runState, out MapEditingArchive? archive))
        {
            return;
        }

        LoadoutNetworkBroadcast.SendToRunClients(
            _runNetService,
            recipient => SendSnapshot(runState, recipient),
            "map editor snapshot");
    }

    private static void SendSnapshot(RunState runState, ulong recipient)
    {
        if (_runNetService?.Type != NetGameType.Host
            || !Archives.TryGetValue(runState, out MapEditingArchive? archive))
        {
            return;
        }

        _runNetService.SendMessage(new MapEditingSnapshotMessage
        {
            SnapshotJson = JsonSerializer.Serialize(archive, JsonOptions)
        }, recipient);
    }

    private static void HandleSnapshot(MapEditingSnapshotMessage message, ulong senderId)
    {
        if (_runNetService?.Type != NetGameType.Client
            || !LoadoutNetworkBroadcast.IsExpectedHostSender(senderId, _runNetService))
        {
            return;
        }

        RunState? runState = TryGetRunState();
        if (message.SnapshotJson.Length > MaxArchiveBytes)
            return;
        if (runState is null)
        {
            _pendingSnapshotJson = message.SnapshotJson;
            return;
        }

        ApplySnapshotJson(runState, message.SnapshotJson);
    }

    private static void ApplySnapshotJson(RunState runState, string snapshotJson)
    {
        try
        {
            MapEditingArchive? incoming = JsonSerializer.Deserialize<MapEditingArchive>(snapshotJson, JsonOptions);
            if (incoming is null || !TryValidateArchive(incoming, runState, requireKnownActs: false, out _))
                return;

            if (Archives.TryGetValue(runState, out MapEditingArchive? current) && incoming.Revision <= current.Revision)
                return;

            bool seedChanged = !string.Equals(
                runState.Rng.StringSeed,
                incoming.RunSeed,
                StringComparison.Ordinal);
            if (!TryReplaceRunSeed(runState, incoming.RunSeed, out string seedError))
            {
                GD.PushWarning($"Loadout map editor: could not apply imported run seed. {seedError}");
                return;
            }
            Archives.Remove(runState);
            Archives.Add(runState, incoming);
            QueueFutureMaps(runState);
            if (incoming.Acts.TryGetValue(runState.CurrentActIndex, out MapEditingActArchive? act))
            {
                NMapScreen? screen = TryGetMapScreen();
                if (screen is not null)
                {
                    if (!seedChanged && NMapEditingToolbar.CanApplyActIncrementally(screen))
                    {
                        NMapEditingToolbar.ApplyActIncrementally(
                            runState,
                            screen,
                            act,
                            CaptureQuests(runState.Map));
                    }
                    else
                    {
                        ApplyAct(runState, screen, act);
                    }
                }
                else
                {
                    Dictionary<MapCoord, List<AbstractModel>> quests = CaptureQuests(runState.Map);
                    SavedActMap rebuilt = new(act.Map);
                    RestoreQuests(rebuilt, quests);
                    runState.Map = rebuilt;
                }
            }
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Loadout map editor: ignored invalid multiplayer snapshot. {exception.Message}");
        }
    }

    private static void CopyWithLimit(Stream input, Stream output, int maxBytes)
    {
        byte[] buffer = new byte[8192];
        int total = 0;
        while (true)
        {
            int read = input.Read(buffer, 0, buffer.Length);
            if (read == 0)
                return;
            total += read;
            if (total > maxBytes)
                throw new InvalidDataException("archive is too large");
            output.Write(buffer, 0, read);
        }
    }

    private static NMapScreen? TryGetMapScreen()
    {
        try
        {
            return NMapScreen.Instance;
        }
        catch
        {
            return null;
        }
    }

    private static string ToBase64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        string normalized = text.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        return Convert.FromBase64String(normalized);
    }
}

public sealed class MapEditingArchive
{
    public int SchemaVersion { get; set; } = MapEditingService.CurrentSchemaVersion;
    public string RunSeed { get; set; } = string.Empty;
    public long Revision { get; set; }
    public Dictionary<int, MapEditingActArchive> Acts { get; set; } = [];
}

public sealed class MapEditingActArchive
{
    public int ActIndex { get; set; }
    public string ActModelId { get; set; } = string.Empty;
    public SerializableActMap Map { get; set; } = new();
    public Dictionary<string, MapEditingPosition> Positions { get; set; } = new(StringComparer.Ordinal);
}

public sealed class MapEditingHistoryState
{
    public string ArchiveJson { get; set; } = string.Empty;
    public string PendingMapsJson { get; set; } = string.Empty;
    public Dictionary<MapCoord, List<AbstractModel>> CurrentActQuests { get; set; } = [];
}

internal sealed class FutureMapBackups
{
    public Dictionary<int, SerializableActMap?> Maps { get; } = [];
}

internal sealed record PlayerSeedState(Player Player, PlayerRngSet Rng, PlayerOddsSet Odds);

public readonly record struct MapEditingPosition(float X, float Y)
{
    public Vector2 ToVector2() => new(X, Y);
    public static MapEditingPosition FromVector2(Vector2 value) => new(value.X, value.Y);
}

public struct MapEditingSnapshotMessage : INetMessage, IPacketSerializable
{
    public string SnapshotJson;
    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => true;

    public readonly void Serialize(PacketWriter writer) => writer.WriteString(SnapshotJson ?? string.Empty);
    public void Deserialize(PacketReader reader) => SnapshotJson = reader.ReadString();
}
