#nullable enable

namespace Loadout.Services.Morphing;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using Loadout.Services.Networking;
using Loadout.Services.Saving;
using Loadout.Services.Targets;
using MegaCrit.Sts2.Core.Animation;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

public static class BottledMonsterMorphService
{
    private const int CurrentSchemaVersion = 1;
    private const string RunDirectory = "loadout/services/bottled_monster_morph";
    private const string RunFilePrefix = "bottled_monster_morph_run";

    private static readonly object AssetLoadLock = new();
    private static readonly Dictionary<string, Task> AssetLoads = new(StringComparer.Ordinal);
    private static readonly Dictionary<ulong, int> PlayerRevisions = new();
    private static readonly Dictionary<ulong, MorphVisualRuntime> ActiveVisuals = new();
    private static readonly FieldInfo? VisualsField = AccessTools.Field(typeof(NCreature), "<Visuals>k__BackingField");
    private static readonly FieldInfo? SpineAnimatorField = AccessTools.Field(typeof(NCreature), "_spineAnimator");
    private static readonly MethodInfo? ConnectAnimatorSignalsMethod = AccessTools.Method(typeof(NCreature), "ConnectSpineAnimatorSignals");
    private static readonly MethodInfo? UpdateBoundsMethod = AccessTools.Method(typeof(NCreature), "UpdateBounds", [typeof(NCreatureVisuals)]);
    private static readonly MethodInfo? SetOrbManagerPositionMethod = AccessTools.Method(typeof(NCreature), "SetOrbManagerPosition");
    private static readonly FieldInfo? AnyStateField = AccessTools.Field(typeof(CreatureAnimator), "_anyState");
    private static readonly FieldInfo? BranchedStatesField = AccessTools.Field(typeof(AnimState), "_branchedStates");

    private static MorphRunSaveData _state = new();
    private static INetGameService? _runNetService;
    private static RunLobby? _runLobby;

    public static void OnRunLaunched()
    {
        ClearRuntimeState();

        try
        {
            INetGameService netService = RunManager.Instance.NetService;
            RegisterRunNetService(netService);

            if (netService.Type is NetGameType.Host or NetGameType.Singleplayer or NetGameType.Replay)
            {
                LoadRunState();
                ApplyLoadedStateToCurrentCombat();
            }

            if (netService.Type == NetGameType.Host)
                BroadcastSnapshot();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"BottledMonsterMorph: failed to initialize run state. {exception.Message}");
        }
    }

    public static void OnRunCleaningUp()
    {
        UnbindRunLobby();
        UnregisterRunNetService();
        ClearRuntimeState();
    }

    public static IReadOnlyList<AbstractModel> GetMorphModels()
    {
        return ModelDb.AllCharacters
            .Where(character => character.IsPlayable)
            .Cast<AbstractModel>()
            .Concat(ModelDb.Monsters.Cast<AbstractModel>())
            .GroupBy(model => model.Id.ToString(), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    public static void ApplySynchronizedMorph(ModelId modelId, LoadoutTargetSelection target)
    {
        if (target.Scope != LoadoutTargetScope.Player || !target.PlayerNetId.HasValue)
            return;

        Player? player = GetRunPlayer(target.PlayerNetId.Value);
        if (player is null)
            return;

        AbstractModel? model = ResolveMorphModel(modelId);
        bool reset = IsEmptyModelId(modelId)
                     || model is CharacterModel character && character.Id == player.Character.Id;

        if (!reset && model is null)
        {
            GD.PushWarning($"BottledMonsterMorph: ignored unknown morph model '{modelId}'.");
            return;
        }

        string playerKey = player.NetId.ToString();
        bool changed;
        if (reset)
        {
            changed = _state.Players.Remove(playerKey);
        }
        else
        {
            string modelKey = model!.Id.ToString();
            changed = !_state.Players.TryGetValue(playerKey, out string? currentModelKey)
                      || !string.Equals(currentModelKey, modelKey, StringComparison.Ordinal);
            if (changed)
                _state.Players[playerKey] = modelKey;
        }

        if (!changed)
            return;

        SaveRunStateIfAuthoritative();
        int revision = NextRevision(player.NetId);
        TaskHelper.RunSafely(ApplyCurrentVisualAsync(player.NetId, revision));
    }

    public static void OnCreatureReady(NCreature creatureNode)
    {
        if (creatureNode.Entity?.Player is not { } player
            || !_state.Players.ContainsKey(player.NetId.ToString()))
        {
            return;
        }

        int revision = NextRevision(player.NetId);
        TaskHelper.RunSafely(ApplyCurrentVisualAsync(player.NetId, revision, creatureNode));
    }

    public static bool TryHandleAnimation(NCreature creatureNode, string trigger)
    {
        if (!ActiveVisuals.TryGetValue(creatureNode.GetInstanceId(), out MorphVisualRuntime? runtime)
            || !GodotObject.IsInstanceValid(creatureNode)
            || !ReferenceEquals(runtime.Node, creatureNode))
        {
            return false;
        }

        string? mappedTrigger = MapTrigger(runtime.Triggers, trigger);
        if (!string.IsNullOrWhiteSpace(mappedTrigger) && !runtime.FailedTriggers.Contains(mappedTrigger))
        {
            try
            {
                runtime.Animator.SetTrigger(mappedTrigger);
                return true;
            }
            catch (Exception exception)
            {
                runtime.FailedTriggers.Add(mappedTrigger);
                GD.PushWarning(
                    $"BottledMonsterMorph: disabled unsafe trigger '{mappedTrigger}' for '{runtime.ModelId}' and fell back to idle. {exception.Message}");
            }
        }

        SetIdle(runtime);
        return true;
    }

    private static async Task ApplyCurrentVisualAsync(ulong playerNetId, int revision, NCreature? expectedNode = null)
    {
        Player? player = GetRunPlayer(playerNetId);
        if (player is null)
            return;

        NCreature? creatureNode = expectedNode ?? NCombatRoom.Instance?.GetCreatureNode(player.Creature);
        if (creatureNode is null || !GodotObject.IsInstanceValid(creatureNode))
            return;

        AbstractModel visualModel = player.Character;
        bool isMorphed = false;
        if (_state.Players.TryGetValue(playerNetId.ToString(), out string? modelId))
        {
            AbstractModel? selected = ResolveMorphModel(modelId);
            if (selected is not null)
            {
                visualModel = selected;
                isMorphed = selected.Id != player.Character.Id;
            }
        }

        await EnsureVisualAssetLoadedAsync(visualModel);
        if (!IsCurrentRevision(playerNetId, revision)
            || !GodotObject.IsInstanceValid(creatureNode)
            || !ReferenceEquals(NCombatRoom.Instance?.GetCreatureNode(player.Creature), creatureNode))
        {
            return;
        }

        ReplaceVisuals(creatureNode, visualModel, isMorphed);
    }

    private static void ReplaceVisuals(NCreature creatureNode, AbstractModel visualModel, bool isMorphed)
    {
        if (VisualsField is null || SpineAnimatorField is null || UpdateBoundsMethod is null)
        {
            GD.PushWarning("BottledMonsterMorph: current game build does not expose the expected NCreature visual fields.");
            return;
        }

        NCreatureVisuals? newVisuals = null;
        NCreatureVisuals? oldVisuals = null;
        object? oldAnimator = null;
        bool fieldsSwapped = false;
        try
        {
            newVisuals = CreateVisuals(visualModel);
            if (newVisuals is null)
                return;

            oldVisuals = creatureNode.Visuals;
            oldAnimator = SpineAnimatorField.GetValue(creatureNode);
            int oldIndex = oldVisuals.GetIndex();
            Color oldModulate = oldVisuals.Modulate;

            creatureNode.AddChild(newVisuals);
            creatureNode.MoveChild(newVisuals, Math.Max(0, oldIndex));
            newVisuals.Position = Vector2.Zero;
            newVisuals.Modulate = oldModulate;
            newVisuals.UpdatePhobiaMode(visualModel as MonsterModel);
            if (visualModel is MonsterModel)
                FlipMonsterVisuals(newVisuals);

            CreatureAnimator? animator = CreateAnimator(visualModel, newVisuals);
            VisualsField.SetValue(creatureNode, newVisuals);
            SpineAnimatorField.SetValue(creatureNode, animator);
            fieldsSwapped = true;
            ConnectAnimatorSignalsMethod?.Invoke(creatureNode, null);
            UpdateBoundsMethod.Invoke(creatureNode, [newVisuals]);
            SetOrbManagerPositionMethod?.Invoke(creatureNode, null);

            ulong nodeId = creatureNode.GetInstanceId();
            PruneInvalidVisuals();
            if (isMorphed && animator is not null && newVisuals.SpineBody is not null)
            {
                ActiveVisuals[nodeId] = new MorphVisualRuntime(
                    creatureNode,
                    animator,
                    newVisuals.SpineBody,
                    ReadAvailableTriggers(animator),
                    visualModel.Id.ToString(),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                SetIdle(ActiveVisuals[nodeId]);
            }
            else
            {
                ActiveVisuals.Remove(nodeId);
                creatureNode.SetAnimationTrigger("Idle");
            }

            oldVisuals.GetParent()?.RemoveChild(oldVisuals);
            oldVisuals.QueueFree();
        }
        catch (Exception exception)
        {
            ActiveVisuals.Remove(creatureNode.GetInstanceId());
            if (fieldsSwapped && oldVisuals is not null && GodotObject.IsInstanceValid(oldVisuals))
            {
                try
                {
                    VisualsField.SetValue(creatureNode, oldVisuals);
                    SpineAnimatorField.SetValue(creatureNode, oldAnimator);
                    ConnectAnimatorSignalsMethod?.Invoke(creatureNode, null);
                    UpdateBoundsMethod.Invoke(creatureNode, [oldVisuals]);
                    SetOrbManagerPositionMethod?.Invoke(creatureNode, null);
                }
                catch
                {
                    // Preserve the original failure as the useful diagnostic.
                }
            }

            if (newVisuals is not null && GodotObject.IsInstanceValid(newVisuals))
            {
                newVisuals.GetParent()?.RemoveChild(newVisuals);
                newVisuals.QueueFree();
            }

            GD.PushWarning($"BottledMonsterMorph: could not install '{visualModel.Id}' on player creature. {exception.Message}");
        }
    }

    private static NCreatureVisuals? CreateVisuals(AbstractModel model)
    {
        return model switch
        {
            MonsterModel monster => monster.CreateVisuals(),
            CharacterModel character => character.CreateVisuals(),
            _ => null
        };
    }

    private static CreatureAnimator? CreateAnimator(AbstractModel model, NCreatureVisuals visuals)
    {
        if (!visuals.HasSpineAnimation || visuals.SpineBody is null)
            return null;

        if (model is MonsterModel monster)
        {
            // Some monster animators (for example Tunneler) evaluate predicates through
            // MonsterModel.Creature. Give the visual-only animator an isolated, valid
            // creature instead of letting it touch the real player or a canonical model.
            MonsterModel animatorModel = monster.ToMutable();
            animatorModel.SetUpForCombat();
            _ = new Creature(animatorModel, CombatSide.Enemy, null)
            {
                CombatState = new NullCombatState()
            };

            CreatureAnimator animator = animatorModel.GenerateAnimator(visuals.SpineBody);
            visuals.SetUpSkin(animatorModel);
            return animator;
        }

        return model is CharacterModel character
            ? character.GenerateAnimator(visuals.SpineBody)
            : null;
    }

    private static void FlipMonsterVisuals(NCreatureVisuals visuals)
    {
        FlipBody(visuals.GetNodeOrNull<Node2D>("%Visuals"));
        FlipBody(visuals.GetNodeOrNull<Node2D>("%PhobiaModeVisuals"));
    }

    private static void FlipBody(Node2D? body)
    {
        if (body is null)
            return;

        body.Scale = new Vector2(-body.Scale.X, body.Scale.Y);
    }

    private static async Task EnsureVisualAssetLoadedAsync(AbstractModel model)
    {
        string? assetPath = model switch
        {
            MonsterModel monster => monster.AssetPaths.FirstOrDefault(),
            CharacterModel character => character.AssetPaths.FirstOrDefault(),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(assetPath)
            || PreloadManager.Cache.GetLoadedCacheAssets().Contains(assetPath))
        {
            return;
        }

        Task loadTask;
        lock (AssetLoadLock)
        {
            if (!AssetLoads.TryGetValue(assetPath, out loadTask!))
            {
                loadTask = LoadVisualAssetAsync(assetPath);
                AssetLoads[assetPath] = loadTask;
            }
        }

        await loadTask;
    }

    private static async Task LoadVisualAssetAsync(string assetPath)
    {
        try
        {
            AssetLoadingSession session = PreloadManager.Cache.CreateSession("Loadout Bottled Monster morph", [assetPath]);
            if (NAssetLoader.Instance is null)
                return;

            await NAssetLoader.Instance.LoadInTheBackground(session);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"BottledMonsterMorph: targeted visual preload failed for '{assetPath}'. {exception.Message}");
        }
    }

    private static HashSet<string> ReadAvailableTriggers(CreatureAnimator animator)
    {
        HashSet<string> triggers = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (AnyStateField?.GetValue(animator) is AnimState anyState
                && BranchedStatesField?.GetValue(anyState) is IDictionary branches)
            {
                foreach (object? key in branches.Keys)
                {
                    if (key is string trigger && !string.IsNullOrWhiteSpace(trigger))
                        triggers.Add(trigger);
                }
            }
        }
        catch
        {
            // Standard triggers and idle fallback remain available.
        }

        return triggers;
    }

    private static string? MapTrigger(IReadOnlyCollection<string> triggers, string trigger)
    {
        string? exact = FindExact(triggers, trigger);
        if (exact is not null)
            return exact;

        if (trigger.Equals("Attack", StringComparison.OrdinalIgnoreCase))
            return FindByTokens(triggers, "attack", "slash", "bite", "chomp", "ram", "strike")
                   ?? FindExact(triggers, "Cast")
                   ?? FindExact(triggers, "PowerUp");

        if (trigger.Equals("Cast", StringComparison.OrdinalIgnoreCase)
            || trigger.Equals("PowerUp", StringComparison.OrdinalIgnoreCase))
        {
            return FindExact(triggers, trigger)
                   ?? FindExact(triggers, trigger.Equals("Cast", StringComparison.OrdinalIgnoreCase) ? "PowerUp" : "Cast")
                   ?? FindByTokens(triggers, "cast", "buff", "power", "charge", "rally", "heal", "summon")
                   ?? FindExact(triggers, "Attack");
        }

        if (trigger.Equals("Hit", StringComparison.OrdinalIgnoreCase))
            return FindExact(triggers, "Hit") ?? FindByTokens(triggers, "hit", "hurt", "debuff", "stun");

        if (trigger.Equals("Dead", StringComparison.OrdinalIgnoreCase))
            return FindExact(triggers, "Dead") ?? FindByTokens(triggers, "dead", "die", "death");

        if (trigger.Equals("Revive", StringComparison.OrdinalIgnoreCase))
            return FindExact(triggers, "Revive") ?? FindByTokens(triggers, "revive", "wake", "respawn");

        if (trigger.Equals("Idle", StringComparison.OrdinalIgnoreCase)
            || trigger.Equals("Relaxed", StringComparison.OrdinalIgnoreCase))
        {
            return FindExact(triggers, trigger) ?? FindExact(triggers, "Idle");
        }

        return FindByTokens(triggers, trigger);
    }

    private static string? FindExact(IEnumerable<string> triggers, string value)
    {
        return triggers.FirstOrDefault(candidate => candidate.Equals(value, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindByTokens(IEnumerable<string> triggers, params string[] tokens)
    {
        return triggers
            .OrderBy(candidate => candidate, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(candidate => tokens.Any(token => candidate.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    private static void SetIdle(MorphVisualRuntime runtime)
    {
        string? idleTrigger = FindExact(runtime.Triggers, "Idle");
        if (idleTrigger is not null)
        {
            try
            {
                runtime.Animator.SetTrigger(idleTrigger);
                return;
            }
            catch
            {
                runtime.FailedTriggers.Add(idleTrigger);
            }
        }

        foreach (string animation in new[] { "idle_loop", "idle", "relaxed_loop" })
        {
            if (!runtime.Spine.HasAnimation(animation))
                continue;

            runtime.Spine.GetAnimationState().SetAnimation(animation, true);
            return;
        }
    }

    private static AbstractModel? ResolveMorphModel(ModelId modelId)
    {
        return IsEmptyModelId(modelId) ? null : ResolveMorphModel(modelId.ToString(), modelId.Entry);
    }

    private static AbstractModel? ResolveMorphModel(string rawId, string? entry = null)
    {
        return GetMorphModels().FirstOrDefault(model =>
            string.Equals(model.Id.ToString(), rawId, StringComparison.Ordinal)
            || (!string.IsNullOrWhiteSpace(entry)
                && string.Equals(model.Id.Entry, entry, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsEmptyModelId(ModelId modelId)
    {
        return modelId == ModelId.none
               || string.IsNullOrWhiteSpace(modelId.Category)
               || string.IsNullOrWhiteSpace(modelId.Entry);
    }

    private static Player? GetRunPlayer(ulong netId)
    {
        try
        {
            return RunManager.Instance.IsInProgress
                ? RunManager.Instance.DebugOnlyGetState()?.GetPlayer(netId)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static int NextRevision(ulong playerNetId)
    {
        int revision = PlayerRevisions.GetValueOrDefault(playerNetId) + 1;
        PlayerRevisions[playerNetId] = revision;
        return revision;
    }

    private static bool IsCurrentRevision(ulong playerNetId, int revision)
    {
        return PlayerRevisions.TryGetValue(playerNetId, out int current) && current == revision;
    }

    private static void PruneInvalidVisuals()
    {
        foreach (ulong nodeId in ActiveVisuals
                     .Where(pair => !GodotObject.IsInstanceValid(pair.Value.Node))
                     .Select(pair => pair.Key)
                     .ToList())
        {
            ActiveVisuals.Remove(nodeId);
        }
    }

    private static void LoadRunState()
    {
        long? runStartTime = SaveUtility.GetCurrentRunStartTime();
        if (!runStartTime.HasValue)
        {
            _state = new MorphRunSaveData();
            return;
        }

        string path = SaveUtility.GetRunSidecarPath(RunDirectory, RunFilePrefix, runStartTime.Value);
        _state = SaveUtility.LoadProfileJson(path, new MorphRunSaveData()).Value;
        _state.SchemaVersion = CurrentSchemaVersion;
        _state.RunStartTime = runStartTime.Value;
        _state.Players ??= new Dictionary<string, string>(StringComparer.Ordinal);

        HashSet<string> validPlayers = RunManager.Instance.DebugOnlyGetState()!.Players
            .Select(player => player.NetId.ToString())
            .ToHashSet(StringComparer.Ordinal);
        foreach (string key in _state.Players.Keys.ToList())
        {
            if (!validPlayers.Contains(key) || ResolveMorphModel(_state.Players[key]) is null)
                _state.Players.Remove(key);
        }
    }

    private static void SaveRunStateIfAuthoritative()
    {
        try
        {
            NetGameType type = (_runNetService ?? RunManager.Instance.NetService).Type;
            if (type == NetGameType.Client)
                return;

            long? runStartTime = SaveUtility.GetCurrentRunStartTime();
            if (!runStartTime.HasValue)
                return;

            _state.SchemaVersion = CurrentSchemaVersion;
            _state.RunStartTime = runStartTime.Value;
            string path = SaveUtility.GetRunSidecarPath(RunDirectory, RunFilePrefix, runStartTime.Value);
            SaveUtility.SaveProfileJson(path, _state);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"BottledMonsterMorph: failed to save run state. {exception.Message}");
        }
    }

    private static void ApplyLoadedStateToCurrentCombat()
    {
        foreach (string key in _state.Players.Keys)
        {
            if (!ulong.TryParse(key, out ulong playerNetId))
                continue;

            int revision = NextRevision(playerNetId);
            TaskHelper.RunSafely(ApplyCurrentVisualAsync(playerNetId, revision));
        }
    }

    private static void RegisterRunNetService(INetGameService netService)
    {
        if (ReferenceEquals(_runNetService, netService))
            return;

        UnregisterRunNetService();
        _runNetService = netService;
        _runNetService.RegisterMessageHandler<BottledMonsterMorphSnapshotMessage>(HandleSnapshot);
        BindRunLobby(RunManager.Instance.RunLobby);
    }

    private static void UnregisterRunNetService()
    {
        if (_runNetService is null)
            return;

        _runNetService.UnregisterMessageHandler<BottledMonsterMorphSnapshotMessage>(HandleSnapshot);
        _runNetService = null;
    }

    private static void BindRunLobby(RunLobby? runLobby)
    {
        if (ReferenceEquals(_runLobby, runLobby))
            return;

        UnbindRunLobby();
        _runLobby = runLobby;
        if (_runLobby is not null)
            _runLobby.PlayerRejoined += OnPlayerRejoined;
    }

    private static void UnbindRunLobby()
    {
        if (_runLobby is null)
            return;

        _runLobby.PlayerRejoined -= OnPlayerRejoined;
        _runLobby = null;
    }

    private static void OnPlayerRejoined(ulong playerId)
    {
        if (_runNetService?.Type != NetGameType.Host || playerId == _runNetService.NetId)
            return;

        SendSnapshot(playerId);
    }

    private static void BroadcastSnapshot()
    {
        if (_runNetService?.Type != NetGameType.Host)
            return;

        LoadoutNetworkBroadcast.SendToRunClients(
            _runNetService,
            SendSnapshot,
            "Bottled Monster morph snapshot");
    }

    private static void SendSnapshot(ulong recipient)
    {
        if (_runNetService?.Type != NetGameType.Host)
            return;

        _runNetService.SendMessage(new BottledMonsterMorphSnapshotMessage
        {
            snapshotJson = JsonSerializer.Serialize(_state)
        }, recipient);
    }

    private static void HandleSnapshot(BottledMonsterMorphSnapshotMessage message, ulong senderId)
    {
        if (_runNetService?.Type != NetGameType.Client
            || !LoadoutNetworkBroadcast.IsExpectedHostSender(senderId, _runNetService))
        {
            return;
        }

        try
        {
            MorphRunSaveData? incoming = JsonSerializer.Deserialize<MorphRunSaveData>(message.snapshotJson);
            if (incoming is null)
                return;

            long? currentRunStartTime = SaveUtility.GetCurrentRunStartTime();
            if (currentRunStartTime.HasValue
                && incoming.RunStartTime != 0
                && incoming.RunStartTime != currentRunStartTime.Value)
            {
                return;
            }

            HashSet<ulong> affectedPlayers = _state.Players.Keys
                .Concat(incoming.Players?.Keys.AsEnumerable() ?? Enumerable.Empty<string>())
                .Select(key => ulong.TryParse(key, out ulong netId) ? netId : 0)
                .Where(netId => netId != 0)
                .ToHashSet();

            _state = incoming;
            _state.Players ??= new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string key in _state.Players.Keys.ToList())
            {
                if (ResolveMorphModel(_state.Players[key]) is null)
                    _state.Players.Remove(key);
            }

            foreach (ulong playerNetId in affectedPlayers)
            {
                int revision = NextRevision(playerNetId);
                TaskHelper.RunSafely(ApplyCurrentVisualAsync(playerNetId, revision));
            }
        }
        catch (Exception exception)
        {
            GD.PushWarning($"BottledMonsterMorph: failed to apply host snapshot. {exception.Message}");
        }
    }

    private static void ClearRuntimeState()
    {
        _state = new MorphRunSaveData();
        PlayerRevisions.Clear();
        ActiveVisuals.Clear();
        lock (AssetLoadLock)
            AssetLoads.Clear();
    }

    private sealed record MorphVisualRuntime(
        NCreature Node,
        CreatureAnimator Animator,
        MegaSprite Spine,
        HashSet<string> Triggers,
        string ModelId,
        HashSet<string> FailedTriggers);

    public sealed class MorphRunSaveData : ISerializable
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public long RunStartTime { get; set; }
        public Dictionary<string, string> Players { get; set; } = new(StringComparer.Ordinal);

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue(nameof(SchemaVersion), SchemaVersion);
            info.AddValue(nameof(RunStartTime), RunStartTime);
            info.AddValue(nameof(Players), Players);
        }
    }
}

public struct BottledMonsterMorphSnapshotMessage : INetMessage, IPacketSerializable
{
    public string snapshotJson;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteString(snapshotJson ?? string.Empty);
    }

    public void Deserialize(PacketReader reader)
    {
        snapshotJson = reader.ReadString();
    }
}
