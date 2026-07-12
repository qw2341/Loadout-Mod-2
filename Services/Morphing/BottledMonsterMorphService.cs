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
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Runs;

public static class BottledMonsterMorphService
{
    private const int CurrentSchemaVersion = 1;
    private const string RunDirectory = "loadout/services/bottled_monster_morph";
    private const string RunFilePrefix = "bottled_monster_morph_run";

    private static readonly object AssetLoadLock = new();
    private static readonly Dictionary<string, Task> AssetLoads = new(StringComparer.Ordinal);
    private static readonly Dictionary<ulong, int> PlayerRevisions = new();
    private static readonly Dictionary<ulong, int> MerchantRevisions = new();
    private static readonly Dictionary<ulong, int> RestSiteRevisions = new();
    private static readonly Dictionary<ulong, MorphVisualRuntime> ActiveVisuals = new();
    private static readonly FieldInfo? VisualsField = AccessTools.Field(typeof(NCreature), "<Visuals>k__BackingField");
    private static readonly FieldInfo? SpineAnimatorField = AccessTools.Field(typeof(NCreature), "_spineAnimator");
    private static readonly MethodInfo? ConnectAnimatorSignalsMethod = AccessTools.Method(typeof(NCreature), "ConnectSpineAnimatorSignals");
    private static readonly MethodInfo? UpdateBoundsMethod = AccessTools.Method(typeof(NCreature), "UpdateBounds", [typeof(NCreatureVisuals)]);
    private static readonly MethodInfo? SetOrbManagerPositionMethod = AccessTools.Method(typeof(NCreature), "SetOrbManagerPosition");
    private static readonly FieldInfo? AnyStateField = AccessTools.Field(typeof(CreatureAnimator), "_anyState");
    private static readonly FieldInfo? BranchedStatesField = AccessTools.Field(typeof(AnimState), "_branchedStates");
    private static readonly FieldInfo? MerchantPlayersField = AccessTools.Field(typeof(NMerchantRoom), "_players");
    private static readonly FieldInfo? MerchantPlayerVisualsField = AccessTools.Field(typeof(NMerchantRoom), "_playerVisuals");
    private static readonly FieldInfo? FakeMerchantPlayersField = AccessTools.Field(typeof(NFakeMerchant), "_players");
    private static readonly FieldInfo? FakeMerchantCharacterContainerField = AccessTools.Field(typeof(NFakeMerchant), "_characterContainer");
    private static readonly Dictionary<ulong, NCreatureVisuals> FakeMerchantVisuals = new();
    private static readonly Dictionary<ulong, RestSiteVisualRuntime> RestSiteVisuals = new();
    private static readonly HashSet<ulong> RestSiteVisualProxyIds = new();
    private static NFakeMerchant? _activeFakeMerchant;

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
        ScheduleMerchantVisualRefresh(player.NetId);
        ScheduleRestSiteVisualRefresh(player.NetId);
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

    public static void OnMerchantRoomReady(NMerchantRoom merchantRoom)
    {
        if (MerchantPlayersField?.GetValue(merchantRoom) is not List<Player> players)
        {
            GD.PushWarning("BottledMonsterMorph: current game build does not expose merchant player visuals.");
            return;
        }

        foreach (Player player in players)
        {
            if (!_state.Players.ContainsKey(player.NetId.ToString()))
                continue;

            int revision = NextMerchantRevision(player.NetId);
            TaskHelper.RunSafely(ApplyCurrentMerchantVisualAsync(player.NetId, revision, merchantRoom));
        }
    }

    public static void OnFakeMerchantReady(NFakeMerchant fakeMerchant)
    {
        if (FakeMerchantPlayersField?.GetValue(fakeMerchant) is not List<Player> players
            || FakeMerchantCharacterContainerField?.GetValue(fakeMerchant) is not Control container)
        {
            GD.PushWarning("BottledMonsterMorph: current game build does not expose fake merchant player visuals.");
            return;
        }

        List<NCreatureVisuals> visuals = container.GetChildren()
            .OfType<NCreatureVisuals>()
            .Reverse()
            .ToList();
        _activeFakeMerchant = fakeMerchant;
        FakeMerchantVisuals.Clear();
        for (int index = 0; index < players.Count && index < visuals.Count; index++)
            FakeMerchantVisuals[players[index].NetId] = visuals[index];

        foreach (Player player in players)
        {
            if (!_state.Players.ContainsKey(player.NetId.ToString()))
                continue;

            int revision = NextMerchantRevision(player.NetId);
            TaskHelper.RunSafely(ApplyCurrentFakeMerchantVisualAsync(player.NetId, revision, fakeMerchant));
        }
    }

    public static void OnRestSiteRoomReady(NRestSiteRoom restSiteRoom)
    {
        RestSiteVisuals.Clear();
        RestSiteVisualProxyIds.Clear();
        foreach (NRestSiteCharacter character in restSiteRoom.Characters)
        {
            if (!_state.Players.ContainsKey(character.Player.NetId.ToString()))
                continue;

            int revision = NextRestSiteRevision(character.Player.NetId);
            TaskHelper.RunSafely(ApplyCurrentRestSiteVisualAsync(character.Player.NetId, revision, restSiteRoom));
        }
    }

    public static bool ShouldSkipRestSiteVisualProxyReady(NRestSiteCharacter character)
    {
        return RestSiteVisualProxyIds.Contains(character.GetInstanceId());
    }

    public static void OnRestSiteFlameGlowHidden(NRestSiteCharacter character)
    {
        if (!RestSiteVisuals.TryGetValue(character.Player.NetId, out RestSiteVisualRuntime? runtime)
            || !ReferenceEquals(runtime.Host, character)
            || runtime.InjectedVisual is null)
        {
            return;
        }

        if (runtime.InjectedVisual is NMorphedRestSiteCharacter monsterVisual)
        {
            monsterVisual.HideFlameGlow();
            return;
        }

        PlayRestSiteTrack(runtime.InjectedVisual, "_tracks/light_off", track: 1);
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
                if (runtime.LoopingTriggerAnimations.TryGetValue(mappedTrigger, out string? loopingAnimation))
                {
                    PlayLoopingAnimationOnce(runtime, loopingAnimation);
                    return true;
                }

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

        // Several monster AssetPaths implementations lazily build their move state
        // machine. That mutates the model, so querying it on ModelDb's canonical
        // instance throws before the visual can be loaded. Use one isolated mutable
        // monster for the entire visual installation instead.
        visualModel = PrepareVisualModel(visualModel);
        await EnsureVisualAssetLoadedAsync(visualModel);
        if (!IsCurrentRevision(playerNetId, revision)
            || !GodotObject.IsInstanceValid(creatureNode)
            || !ReferenceEquals(NCombatRoom.Instance?.GetCreatureNode(player.Creature), creatureNode))
        {
            return;
        }

        ReplaceVisuals(creatureNode, visualModel, isMorphed);
    }

    private static async Task ApplyCurrentMerchantVisualAsync(
        ulong playerNetId,
        int revision,
        NMerchantRoom? expectedRoom = null)
    {
        Player? player = GetRunPlayer(playerNetId);
        NMerchantRoom? merchantRoom = expectedRoom ?? NMerchantRoom.Instance;
        if (player is null
            || merchantRoom is null
            || !GodotObject.IsInstanceValid(merchantRoom)
            || !TryGetMerchantSlot(merchantRoom, playerNetId, out int slot, out NMerchantCharacter oldVisual))
        {
            return;
        }

        AbstractModel visualModel = player.Character;
        if (_state.Players.TryGetValue(playerNetId.ToString(), out string? modelId))
            visualModel = ResolveMorphModel(modelId) ?? player.Character;

        visualModel = PrepareVisualModel(visualModel);
        string? assetPath = GetMerchantVisualAssetPath(visualModel);
        if (string.IsNullOrWhiteSpace(assetPath))
            return;

        await EnsureAssetLoadedAsync(assetPath);
        if (!IsCurrentMerchantRevision(playerNetId, revision)
            || !GodotObject.IsInstanceValid(merchantRoom)
            || !ReferenceEquals(NMerchantRoom.Instance, merchantRoom)
            || !TryGetMerchantSlot(merchantRoom, playerNetId, out int currentSlot, out NMerchantCharacter currentVisual)
            || currentSlot != slot
            || !ReferenceEquals(currentVisual, oldVisual))
        {
            return;
        }

        ReplaceMerchantVisual(merchantRoom, slot, oldVisual, visualModel, assetPath);
    }

    private static async Task ApplyCurrentFakeMerchantVisualAsync(
        ulong playerNetId,
        int revision,
        NFakeMerchant? expectedFakeMerchant = null)
    {
        Player? player = GetRunPlayer(playerNetId);
        NFakeMerchant? fakeMerchant = expectedFakeMerchant
                                      ?? NEventRoom.Instance?.CustomEventNode as NFakeMerchant;
        if (player is null
            || fakeMerchant is null
            || !GodotObject.IsInstanceValid(fakeMerchant)
            || !ReferenceEquals(_activeFakeMerchant, fakeMerchant)
            || !FakeMerchantVisuals.TryGetValue(playerNetId, out NCreatureVisuals? oldVisual)
            || !GodotObject.IsInstanceValid(oldVisual))
        {
            return;
        }

        AbstractModel visualModel = player.Character;
        if (_state.Players.TryGetValue(playerNetId.ToString(), out string? modelId))
            visualModel = ResolveMorphModel(modelId) ?? player.Character;

        visualModel = PrepareVisualModel(visualModel);
        string? assetPath = GetCombatVisualAssetPath(visualModel);
        if (string.IsNullOrWhiteSpace(assetPath))
            return;

        await EnsureAssetLoadedAsync(assetPath);
        if (!IsCurrentMerchantRevision(playerNetId, revision)
            || !GodotObject.IsInstanceValid(fakeMerchant)
            || !ReferenceEquals(NEventRoom.Instance?.CustomEventNode, fakeMerchant)
            || !ReferenceEquals(_activeFakeMerchant, fakeMerchant)
            || !FakeMerchantVisuals.TryGetValue(playerNetId, out NCreatureVisuals? currentVisual)
            || !ReferenceEquals(currentVisual, oldVisual))
        {
            return;
        }

        ReplaceFakeMerchantVisual(playerNetId, oldVisual, visualModel);
    }

    private static async Task ApplyCurrentRestSiteVisualAsync(
        ulong playerNetId,
        int revision,
        NRestSiteRoom? expectedRoom = null)
    {
        Player? player = GetRunPlayer(playerNetId);
        NRestSiteRoom? restSiteRoom = expectedRoom ?? NRestSiteRoom.Instance;
        NRestSiteCharacter? host = player is null ? null : restSiteRoom?.GetCharacterForPlayer(player);
        if (player is null
            || restSiteRoom is null
            || host is null
            || !GodotObject.IsInstanceValid(restSiteRoom)
            || !GodotObject.IsInstanceValid(host))
        {
            return;
        }

        RestSiteVisualRuntime runtime = GetOrCreateRestSiteRuntime(restSiteRoom, host);
        AbstractModel visualModel = player.Character;
        bool reset = true;
        if (_state.Players.TryGetValue(playerNetId.ToString(), out string? modelId)
            && ResolveMorphModel(modelId) is { } selected)
        {
            visualModel = selected;
            reset = selected.Id == player.Character.Id;
        }

        if (reset)
        {
            if (IsCurrentRestSiteRevision(playerNetId, revision))
                ResetRestSiteVisual(runtime);
            return;
        }

        visualModel = PrepareVisualModel(visualModel);
        string? assetPath = visualModel switch
        {
            CharacterModel character => character.RestSiteAnimPath,
            _ => GetCombatVisualAssetPath(visualModel)
        };
        if (string.IsNullOrWhiteSpace(assetPath))
            return;

        await EnsureAssetLoadedAsync(assetPath);
        if (!IsCurrentRestSiteRevision(playerNetId, revision)
            || !GodotObject.IsInstanceValid(restSiteRoom)
            || !ReferenceEquals(NRestSiteRoom.Instance, restSiteRoom)
            || !GodotObject.IsInstanceValid(host)
            || !RestSiteVisuals.TryGetValue(playerNetId, out RestSiteVisualRuntime? currentRuntime)
            || !ReferenceEquals(currentRuntime.Host, host))
        {
            return;
        }

        InstallRestSiteVisual(currentRuntime, visualModel, assetPath);
    }

    private static RestSiteVisualRuntime GetOrCreateRestSiteRuntime(
        NRestSiteRoom restSiteRoom,
        NRestSiteCharacter host)
    {
        ulong playerNetId = host.Player.NetId;
        if (RestSiteVisuals.TryGetValue(playerNetId, out RestSiteVisualRuntime? runtime)
            && ReferenceEquals(runtime.Room, restSiteRoom)
            && ReferenceEquals(runtime.Host, host))
        {
            return runtime;
        }

        runtime = new RestSiteVisualRuntime(
            restSiteRoom,
            host,
            host.GetChildren()
                .OfType<Node2D>()
                .Where(node => node.GetClass() == "SpineSprite")
                .ToList());
        RestSiteVisuals[playerNetId] = runtime;
        return runtime;
    }

    private static void InstallRestSiteVisual(
        RestSiteVisualRuntime runtime,
        AbstractModel visualModel,
        string assetPath)
    {
        Node2D? newVisual = null;
        bool proxyRegistered = false;
        try
        {
            int characterIndex = runtime.Room.Characters.IndexOf(runtime.Host);
            bool flippedSlot = characterIndex % 2 == 1;
            if (visualModel is CharacterModel)
            {
                NRestSiteCharacter proxy = PreloadManager.Cache.GetScene(assetPath)
                    .Instantiate<NRestSiteCharacter>(PackedScene.GenEditState.Disabled);
                newVisual = proxy;
                RestSiteVisualProxyIds.Add(proxy.GetInstanceId());
                proxyRegistered = true;
                proxy.GetNodeOrNull<CanvasItem>("ControlRoot")?.Hide();
                if (characterIndex >= 2
                    && proxy.GetNodeOrNull<Node2D>("Osty") is { } osty
                    && proxy.GetNodeOrNull<Node2D>("OstyRightAnchor") is { } rightAnchor)
                {
                    osty.Position = rightAnchor.Position;
                    proxy.MoveChild(osty, 0);
                }

                Transform2D authoredTransform = proxy.Transform;
                authoredTransform.Origin = Vector2.Zero;
                proxy.Transform = runtime.Host.Transform.AffineInverse() * authoredTransform;
            }
            else if (visualModel is MonsterModel monster)
            {
                NMorphedRestSiteCharacter monsterVisual = new();
                monsterVisual.Initialize(monster, monster.CreateVisuals(), flippedSlot);
                newVisual = monsterVisual;
            }

            if (newVisual is null)
                return;

            runtime.Host.AddChild(newVisual);
            if (newVisual is NRestSiteCharacter characterProxy)
            {
                if (flippedSlot)
                    FlipRestSiteProxy(characterProxy);
                PlayRestSiteTrack(characterProxy, GetRestSiteActAnimation(runtime.Host.Player), track: 0);
            }

            foreach (Node2D originalVisual in runtime.OriginalVisuals)
            {
                if (GodotObject.IsInstanceValid(originalVisual))
                    originalVisual.Visible = false;
            }

            RemoveInjectedRestSiteVisual(runtime);
            runtime.InjectedVisual = newVisual;
        }
        catch (Exception exception)
        {
            if (proxyRegistered && newVisual is not null)
                RestSiteVisualProxyIds.Remove(newVisual.GetInstanceId());
            if (newVisual is not null && GodotObject.IsInstanceValid(newVisual))
                newVisual.QueueFree();
            GD.PushWarning(
                $"BottledMonsterMorph: could not install rest-site player visual '{visualModel.Id}'. {exception.Message}");
        }
    }

    private static void ResetRestSiteVisual(RestSiteVisualRuntime runtime)
    {
        RemoveInjectedRestSiteVisual(runtime);
        foreach (Node2D originalVisual in runtime.OriginalVisuals)
        {
            if (GodotObject.IsInstanceValid(originalVisual))
                originalVisual.Visible = true;
        }
    }

    private static void RemoveInjectedRestSiteVisual(RestSiteVisualRuntime runtime)
    {
        if (runtime.InjectedVisual is null)
            return;

        RestSiteVisualProxyIds.Remove(runtime.InjectedVisual.GetInstanceId());
        if (GodotObject.IsInstanceValid(runtime.InjectedVisual))
            runtime.InjectedVisual.QueueFree();
        runtime.InjectedVisual = null;
    }

    private static string GetRestSiteActAnimation(Player player)
    {
        return player.RunState.CurrentActIndex switch
        {
            0 => "overgrowth_loop",
            1 => "hive_loop",
            2 => "glory_loop",
            _ => "relaxed_loop"
        };
    }

    private static void PlayRestSiteTrack(Node root, string animation, int track)
    {
        foreach (Node2D spineNode in root.GetChildren()
                     .OfType<Node2D>()
                     .Where(node => node.GetClass() == "SpineSprite"))
        {
            MegaSprite spine = new(spineNode);
            root.RunWhenSpineReady(spine, animationState =>
            {
                if (spine.HasAnimation(animation))
                    animationState.SetAnimation(animation, true, track);
            });
        }
    }

    private static void FlipRestSiteProxy(NRestSiteCharacter proxy)
    {
        foreach (Node2D spineNode in proxy.GetChildren()
                     .OfType<Node2D>()
                     .Where(node => node.GetClass() == "SpineSprite"))
        {
            spineNode.Scale = new Vector2(-spineNode.Scale.X, spineNode.Scale.Y);
            spineNode.Position = new Vector2(-spineNode.Position.X, spineNode.Position.Y);
        }
    }

    private static bool TryGetMerchantSlot(
        NMerchantRoom merchantRoom,
        ulong playerNetId,
        out int slot,
        out NMerchantCharacter visual)
    {
        slot = -1;
        visual = null!;
        if (MerchantPlayersField?.GetValue(merchantRoom) is not List<Player> players
            || MerchantPlayerVisualsField?.GetValue(merchantRoom) is not List<NMerchantCharacter> visuals)
        {
            return false;
        }

        slot = players.FindIndex(player => player.NetId == playerNetId);
        if (slot < 0 || slot >= visuals.Count)
            return false;

        visual = visuals[slot];
        return GodotObject.IsInstanceValid(visual);
    }

    private static string? GetMerchantVisualAssetPath(AbstractModel model)
    {
        return model switch
        {
            CharacterModel character => character.MerchantAnimPath,
            MonsterModel monster => monster.AssetPaths.FirstOrDefault(),
            _ => null
        };
    }

    private static string? GetCombatVisualAssetPath(AbstractModel model)
    {
        return model switch
        {
            MonsterModel monster => monster.AssetPaths.FirstOrDefault(),
            CharacterModel character => character.AssetPaths.FirstOrDefault(),
            _ => null
        };
    }

    private static void ReplaceMerchantVisual(
        NMerchantRoom merchantRoom,
        int slot,
        NMerchantCharacter oldVisual,
        AbstractModel visualModel,
        string assetPath)
    {
        if (MerchantPlayerVisualsField?.GetValue(merchantRoom) is not List<NMerchantCharacter> playerVisuals)
            return;

        NMerchantCharacter? newVisual = null;
        try
        {
            newVisual = visualModel switch
            {
                CharacterModel => PreloadManager.Cache.GetScene(assetPath)
                    .Instantiate<NMerchantCharacter>(PackedScene.GenEditState.Disabled),
                MonsterModel monster => CreateMonsterMerchantVisual(monster),
                _ => null
            };
            if (newVisual is null || oldVisual.GetParent() is not Node parent)
                return;

            int siblingIndex = oldVisual.GetIndex();
            newVisual.Position = oldVisual.Position;
            newVisual.Modulate = oldVisual.Modulate;
            newVisual.Visible = oldVisual.Visible;
            parent.AddChild(newVisual);
            parent.MoveChild(newVisual, Math.Max(0, siblingIndex));
            playerVisuals[slot] = newVisual;
            oldVisual.QueueFree();
        }
        catch (Exception exception)
        {
            if (newVisual is not null && GodotObject.IsInstanceValid(newVisual))
                newVisual.QueueFree();
            GD.PushWarning(
                $"BottledMonsterMorph: could not install merchant player visual '{visualModel.Id}'. {exception.Message}");
        }
    }

    private static NMerchantCharacter CreateMonsterMerchantVisual(MonsterModel monster)
    {
        NMorphedMerchantCharacter merchantCharacter = new();
        merchantCharacter.Initialize(monster, monster.CreateVisuals());
        return merchantCharacter;
    }

    private static void ReplaceFakeMerchantVisual(
        ulong playerNetId,
        NCreatureVisuals oldVisual,
        AbstractModel visualModel)
    {
        NCreatureVisuals? newVisual = null;
        try
        {
            newVisual = CreateVisuals(visualModel);
            if (newVisual is null || oldVisual.GetParent() is not Node parent)
                return;

            int siblingIndex = oldVisual.GetIndex();
            newVisual.Position = oldVisual.Position;
            newVisual.Modulate = oldVisual.Modulate;
            newVisual.Visible = oldVisual.Visible;
            newVisual.Scale = oldVisual.Scale;
            parent.AddChild(newVisual);
            parent.MoveChild(newVisual, Math.Max(0, siblingIndex));
            if (visualModel is MonsterModel monster)
            {
                newVisual.UpdatePhobiaMode(monster);
                newVisual.SetUpSkin(monster);
                FlipMonsterVisuals(newVisual);
            }

            PlayRelaxedOrIdle(newVisual);
            FakeMerchantVisuals[playerNetId] = newVisual;
            oldVisual.QueueFree();
        }
        catch (Exception exception)
        {
            if (newVisual is not null && GodotObject.IsInstanceValid(newVisual))
                newVisual.QueueFree();
            GD.PushWarning(
                $"BottledMonsterMorph: could not install fake merchant player visual '{visualModel.Id}'. {exception.Message}");
        }
    }

    private static void PlayRelaxedOrIdle(NCreatureVisuals visuals)
    {
        if (visuals.SpineBody is not { } spine)
            return;

        foreach (string animation in new[] { "relaxed_loop", "idle_loop", "idle" })
        {
            if (!spine.HasAnimation(animation))
                continue;

            spine.GetAnimationState().SetAnimation(animation, true);
            return;
        }
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
                    ReadLoopingTriggers(animator),
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
            CreatureAnimator animator = monster.GenerateAnimator(visuals.SpineBody);
            visuals.SetUpSkin(monster);
            return animator;
        }

        return model is CharacterModel character
            ? character.GenerateAnimator(visuals.SpineBody)
            : null;
    }

    private static AbstractModel PrepareVisualModel(AbstractModel model)
    {
        if (model is not MonsterModel monster)
            return model;

        MonsterModel visualMonster = monster.ToMutable();
        visualMonster.SetUpForCombat();
        _ = new Creature(visualMonster, CombatSide.Enemy, null)
        {
            CombatState = new NullCombatState()
        };
        return visualMonster;
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
        await EnsureAssetLoadedAsync(GetCombatVisualAssetPath(model));
    }

    private static async Task EnsureAssetLoadedAsync(string? assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath)
            || PreloadManager.Cache.GetLoadedCacheAssets().Contains(assetPath))
            return;

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

    private static Dictionary<string, string> ReadLoopingTriggers(CreatureAnimator animator)
    {
        Dictionary<string, string> triggers = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (AnyStateField?.GetValue(animator) is not AnimState anyState
                || BranchedStatesField?.GetValue(anyState) is not IDictionary branches)
            {
                return triggers;
            }

            foreach (object? key in branches.Keys)
            {
                if (key is string trigger
                    && anyState.CallTrigger(trigger) is { IsLooping: true } state)
                {
                    triggers[trigger] = state.Id;
                }
            }
        }
        catch
        {
            // Unknown conditional triggers retain their native animator behavior.
        }

        return triggers;
    }

    private static void PlayLoopingAnimationOnce(MorphVisualRuntime runtime, string animation)
    {
        MegaAnimationState animationState = runtime.Spine.GetAnimationState();
        animationState.SetAnimation(animation, false);

        foreach (string idleAnimation in new[] { "idle_loop", "idle", "relaxed_loop" })
        {
            if (!runtime.Spine.HasAnimation(idleAnimation))
                continue;

            animationState.AddAnimation(idleAnimation, 0f, true);
            return;
        }
    }

    private static string? MapTrigger(IReadOnlyCollection<string> triggers, string trigger)
    {
        string? exact = FindExact(triggers, trigger);
        if (exact is not null)
            return exact;

        if (trigger.Equals("Attack", StringComparison.OrdinalIgnoreCase))
            return FindExact(triggers, "Attack") ?? FindByTokens(triggers, "attack", "attack_light", "slash", "bite", "chomp", "ram", "strike");
        
        if (trigger.Equals("heavyAttack", StringComparison.OrdinalIgnoreCase))
            return FindExact(triggers, "heavyAttack") ?? FindByTokens(triggers, "attack_heavy", "crush", "bite", "slash", "chomp", "ram", "strike","attack");
        
        if (trigger.Equals("MultiAttack", StringComparison.OrdinalIgnoreCase))
            return FindExact(triggers, "MultiAttack") ?? FindByTokens(triggers, "attack_multi", "multi", "attack", "slash", "bite", "chomp", "ram", "strike");

        if (trigger.Equals("Cast", StringComparison.OrdinalIgnoreCase))
        {
            return FindExact(triggers, "Cast")
                   ?? FindByTokens(triggers, "cast", "charge", "rally", "summon");
        }
        
        if (trigger.Equals("PowerUp", StringComparison.OrdinalIgnoreCase))
        {
            return FindExact(triggers, "PowerUp")
                   ?? FindByTokens(triggers, "power", "buff", "charge", "rally" , "heal", "summon","cast");
        }

        if (trigger.Equals("Hit", StringComparison.OrdinalIgnoreCase))
            return FindExact(triggers, "Hit") ?? FindByTokens(triggers, "hit", "hurt", "debuff", "stun");

        if (trigger.Equals("Dead", StringComparison.OrdinalIgnoreCase))
            return FindExact(triggers, "Dead") ?? FindByTokens(triggers, "dead", "die", "death");

        if (trigger.Equals("Revive", StringComparison.OrdinalIgnoreCase))
            return FindExact(triggers, "Revive") ?? FindByTokens(triggers, "revive", "wake", "respawn");

        if (trigger.Equals("Idle", StringComparison.OrdinalIgnoreCase))
        {
            return FindExact(triggers, "Idle") ?? FindByTokens(triggers, "idle", "idle_loop", "awake_loop");
        }
        
        if (trigger.Equals("Relaxed", StringComparison.OrdinalIgnoreCase))
        {
            return FindExact(triggers, "Relaxed") ?? FindExact(triggers, "Idle");
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

    private static int NextMerchantRevision(ulong playerNetId)
    {
        int revision = MerchantRevisions.GetValueOrDefault(playerNetId) + 1;
        MerchantRevisions[playerNetId] = revision;
        return revision;
    }

    private static bool IsCurrentMerchantRevision(ulong playerNetId, int revision)
    {
        return MerchantRevisions.TryGetValue(playerNetId, out int current) && current == revision;
    }

    private static int NextRestSiteRevision(ulong playerNetId)
    {
        int revision = RestSiteRevisions.GetValueOrDefault(playerNetId) + 1;
        RestSiteRevisions[playerNetId] = revision;
        return revision;
    }

    private static bool IsCurrentRestSiteRevision(ulong playerNetId, int revision)
    {
        return RestSiteRevisions.TryGetValue(playerNetId, out int current) && current == revision;
    }

    private static void ScheduleMerchantVisualRefresh(ulong playerNetId)
    {
        int revision = NextMerchantRevision(playerNetId);
        TaskHelper.RunSafely(ApplyCurrentMerchantVisualAsync(playerNetId, revision));
        TaskHelper.RunSafely(ApplyCurrentFakeMerchantVisualAsync(playerNetId, revision));
    }

    private static void ScheduleRestSiteVisualRefresh(ulong playerNetId)
    {
        int revision = NextRestSiteRevision(playerNetId);
        TaskHelper.RunSafely(ApplyCurrentRestSiteVisualAsync(playerNetId, revision));
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
            ScheduleMerchantVisualRefresh(playerNetId);
            ScheduleRestSiteVisualRefresh(playerNetId);
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
                ScheduleMerchantVisualRefresh(playerNetId);
                ScheduleRestSiteVisualRefresh(playerNetId);
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
        MerchantRevisions.Clear();
        RestSiteRevisions.Clear();
        ActiveVisuals.Clear();
        FakeMerchantVisuals.Clear();
        _activeFakeMerchant = null;
        RestSiteVisuals.Clear();
        RestSiteVisualProxyIds.Clear();
        lock (AssetLoadLock)
            AssetLoads.Clear();
    }

    private sealed record MorphVisualRuntime(
        NCreature Node,
        CreatureAnimator Animator,
        MegaSprite Spine,
        HashSet<string> Triggers,
        Dictionary<string, string> LoopingTriggerAnimations,
        string ModelId,
        HashSet<string> FailedTriggers);

    private sealed class RestSiteVisualRuntime(
        NRestSiteRoom room,
        NRestSiteCharacter host,
        List<Node2D> originalVisuals)
    {
        public NRestSiteRoom Room { get; } = room;
        public NRestSiteCharacter Host { get; } = host;
        public List<Node2D> OriginalVisuals { get; } = originalVisuals;
        public Node2D? InjectedVisual { get; set; }
    }

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
