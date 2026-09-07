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
            MapEditingArchive archive = JsonSerializer.Deserialize<MapEditingArchive>(payload, JsonOptions)!;

            Archives.Remove(runState);
            Archives.Add(runState, archive);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Loadout map editor: could not restore run archive. {exception.Message}");
        }
    }

    public static void WriteMapsToNativeSave(RunState runState, SerializableRun save)
    {
        if (!Archives.TryGetValue(runState, out MapEditingArchive? archive))
            return;

        foreach ((int actIndex, MapEditingActArchive act) in archive.Acts)
            save.Acts[actIndex].SavedMap = act.Map;
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
        if (_runNetService?.Type == NetGameType.Host)
            BroadcastSnapshot(runState);
    }

    public static void OnRunCleaningUp()
    {
        RunState? runState = TryGetRunState();
        if (runState is not null)
            Archives.Remove(runState);
        RunManager.Instance.SavedMapsToLoad = null;
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
                PendingMapsJson = JsonSerializer.Serialize(pending, JsonOptions)
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
            MapEditingArchive archive = JsonSerializer.Deserialize<MapEditingArchive>(state.ArchiveJson, JsonOptions)!;
            Dictionary<int, SerializableActMap> pending =
                JsonSerializer.Deserialize<Dictionary<int, SerializableActMap>>(state.PendingMapsJson, JsonOptions)!;
            MapEditingActArchive currentAct = archive.Acts[runState.CurrentActIndex];

            long currentRevision = Archives.TryGetValue(runState, out MapEditingArchive? current)
                ? current.Revision
                : 0L;
            archive.SchemaVersion = CurrentSchemaVersion;
            archive.Revision = Math.Max(currentRevision, archive.Revision) + 1L;
            if (!TryReplaceRunSeed(runState, archive.RunSeed, out error))
                return false;
            Archives.Remove(runState);
            Archives.Add(runState, archive);
            ApplyAct(runState, screen, currentAct);
            RunManager.Instance.SavedMapsToLoad = pending.Count == 0 ? null : pending;
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

        MapEditingArchive archive = GetOrCreateArchive(runState);
        archive.SchemaVersion = CurrentSchemaVersion;
        archive.RunSeed = runState.Rng.StringSeed;
        archive.Revision++;
        archive.Acts[runState.CurrentActIndex] = act;
        error = string.Empty;
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

    public static (bool Success, string Message) ImportFromClipboard(
        RunState runState,
        NMapScreen screen)
    {
        if (!CanImportCurrentAct(runState))
        {
            return (false, LocMan.Loc(
                "MAP_EDITOR_IMPORT_FIRST_FLOOR",
                "Maps can only be imported while you are still in the first room of the current act."));
        }

        try
        {
            string text = DisplayServer.ClipboardGet().Trim();
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
            gzip.CopyTo(output);
            MapEditingArchive imported = JsonSerializer.Deserialize<MapEditingArchive>(
                Encoding.UTF8.GetString(output.ToArray()),
                JsonOptions)!;
            MapEditingActArchive currentAct = imported.Acts[runState.CurrentActIndex];
            SavedActMap replacement = new(currentAct.Map);

            if (!TryReplaceRunSeed(runState, imported.RunSeed, out string error))
                return (false, LocMan.Loc("MAP_EDITOR_IMPORT_INVALID", "Could not import maps: {0}", error));

            long previousRevision = Archives.TryGetValue(runState, out MapEditingArchive? previousArchive)
                ? previousArchive.Revision
                : 0L;
            imported.SchemaVersion = CurrentSchemaVersion;
            imported.Revision = Math.Max(previousRevision, imported.Revision) + 1L;
            Archives.Remove(runState);
            Archives.Add(runState, imported);
            runState.Map = replacement;
            screen.SetMap(replacement, runState.Rng.Seed, clearDrawings: false);
            InstallFutureMaps(runState, imported);
            BroadcastSnapshot(runState);
            SaveCurrentRun();
            return (true, LocMan.Loc(
                "MAP_EDITOR_IMPORTED",
                "Imported {0} edited act map(s).",
                imported.Acts.Count));
        }
        catch (Exception exception)
        {
            return (false, LocMan.Loc(
                "MAP_EDITOR_IMPORT_INVALID",
                "Could not import maps: {0}",
                exception.Message));
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
        SavedActMap rebuilt = new(act.Map);
        runState.Map = rebuilt;
        screen.SetMap(rebuilt, runState.Rng.Seed, clearDrawings: false);
    }

    private static void InstallFutureMaps(RunState runState, MapEditingArchive archive)
    {
        Dictionary<int, SerializableActMap> pending = [];

        foreach ((int actIndex, MapEditingActArchive act) in archive.Acts)
        {
            if (actIndex > runState.CurrentActIndex)
                pending[actIndex] = act.Map;
        }

        RunManager.Instance.SavedMapsToLoad = pending.Count == 0 ? null : pending;
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
            MapEditingArchive incoming = JsonSerializer.Deserialize<MapEditingArchive>(snapshotJson, JsonOptions)!;

            if (Archives.TryGetValue(runState, out MapEditingArchive? current) && incoming.Revision <= current.Revision)
                return;

            if (!TryReplaceRunSeed(runState, incoming.RunSeed, out string seedError))
            {
                GD.PushWarning($"Loadout map editor: could not apply imported run seed. {seedError}");
                return;
            }
            Archives.Remove(runState);
            Archives.Add(runState, incoming);
            InstallFutureMaps(runState, incoming);
            if (incoming.Acts.TryGetValue(runState.CurrentActIndex, out MapEditingActArchive? act))
            {
                NMapScreen? screen = TryGetMapScreen();
                if (screen is not null)
                    ApplyAct(runState, screen, act);
                else
                    runState.Map = new SavedActMap(act.Map);
            }
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Loadout map editor: ignored invalid multiplayer snapshot. {exception.Message}");
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
