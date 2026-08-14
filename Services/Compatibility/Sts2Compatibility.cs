#nullable enable

namespace Loadout.Services.Compatibility;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Animation;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

internal sealed record StartRunLobbyPlayerInfo(
    ulong PlayerId,
    int SlotId,
    CharacterModel? Character,
    bool IsReady);

/// <summary>
/// Resolves supported STS2 release and beta API shapes once. Gameplay call sites
/// use cached compiled delegates, so compatibility probing never adds reflection
/// to hot paths.
/// </summary>
internal static class Sts2Compatibility
{
    private delegate CreatureAnimator CharacterAnimatorInvoker(
        CharacterModel character,
        MegaSprite controller,
        Creature creature);

    private delegate Task<IReadOnlyList<CardPileAddResult>> BatchCardAddInvoker(
        IEnumerable<CardModel> cards,
        CardPile newPile,
        CardPilePosition position,
        AbstractModel? clonedBy,
        bool skipVisuals,
        bool isChangingOwners);

    private delegate decimal ModifyDamageInvoker(
        IRunState runState,
        ICombatState? combatState,
        Creature? target,
        Creature? dealer,
        decimal damage,
        ValueProp props,
        CardModel? cardSource,
        CardPlay? cardPlay,
        ModifyDamageHookType modifyDamageHookType,
        CardPreviewMode previewMode);

    private delegate Task HookPlayerChoiceBeginInvoker(
        HookPlayerChoiceContext context,
        Player player,
        PlayerChoiceOptions options);

    private delegate Task HookPlayerChoiceEndInvoker(
        HookPlayerChoiceContext context,
        Player player);

    private static readonly Type AbstractModelEnumerableByRef =
        typeof(IEnumerable<AbstractModel>).MakeByRefType();

    private static readonly Func<IEnumerable<AbstractModel>>? AllModelsGetter =
        ResolveModelDbEnumerableGetter<AbstractModel>("All");
    private static readonly Func<IEnumerable<EncounterModel>>? EventEncountersGetter =
        ResolveModelDbEnumerableGetter<EncounterModel>("EventEncounters");
    private static readonly Func<IEnumerable<EncounterModel>>? AllEncountersGetter =
        ResolveModelDbEnumerableGetter<EncounterModel>("AllEncounters");

    private static readonly MethodInfo HookPlayerChoiceBeginMethod =
        ResolveHookPlayerChoiceMethod(
            nameof(HookPlayerChoiceContext.SignalPlayerChoiceBegun),
            [typeof(Player), typeof(PlayerChoiceOptions)],
            [typeof(PlayerChoiceOptions)]);
    private static readonly HookPlayerChoiceBeginInvoker BeginHookPlayerChoice =
        CreateHookPlayerChoiceBeginInvoker();
    private static readonly MethodInfo HookPlayerChoiceEndMethod =
        ResolveHookPlayerChoiceMethod(
            nameof(HookPlayerChoiceContext.SignalPlayerChoiceEnded),
            [typeof(Player)],
            Type.EmptyTypes);
    private static readonly HookPlayerChoiceEndInvoker EndHookPlayerChoice =
        CreateHookPlayerChoiceEndInvoker();

    private static readonly EventInfo StartRunLobbyPlayerConnectedEvent =
        ResolveEvent(typeof(StartRunLobby), "PlayerConnected");
    private static readonly EventInfo StartRunLobbyPlayerDisconnectedEvent =
        ResolveEvent(typeof(StartRunLobby), "PlayerDisconnected");
    private static readonly Type StartRunLobbyPlayerType =
        ResolvePlayerEventPayloadType(
            StartRunLobbyPlayerConnectedEvent,
            "MegaCrit.Sts2.Core.Entities.Multiplayer.LobbyPlayer",
            "MegaCrit.Sts2.Core.Entities.Multiplayer.StartRunLobbyPlayer");
    private static readonly PropertyInfo StartRunLobbyPlayersProperty =
        ResolveProperty(typeof(StartRunLobby), "Players");
    private static readonly Func<StartRunLobby, IEnumerable> GetStartRunLobbyPlayers =
        CreateStartRunLobbyPlayersGetter();
    private static readonly Func<object, ulong> GetStartRunLobbyPlayerId =
        CreatePlayerIdAccessor(StartRunLobbyPlayerType);
    private static readonly Func<object, CharacterModel> GetStartRunLobbyPlayerCharacter =
        CreatePlayerMemberAccessor<CharacterModel>(StartRunLobbyPlayerType, "character", "Character");
    private static readonly Func<object, int> GetStartRunLobbyPlayerSlot =
        CreatePlayerMemberAccessor<int>(StartRunLobbyPlayerType, "slotId", "slot", "SlotId", "Slot");
    private static readonly Func<object, bool> GetStartRunLobbyPlayerReady =
        CreatePlayerMemberAccessor<bool>(StartRunLobbyPlayerType, "isReady", "ready", "IsReady", "Ready");

    private static readonly EventInfo RunLobbyPlayerRejoinedEvent =
        ResolveEvent(typeof(RunLobby), "PlayerRejoined");
    private static readonly Type RunLobbyPlayerRejoinedPayloadType =
        ResolvePlayerEventPayloadType(
            RunLobbyPlayerRejoinedEvent,
            typeof(ulong).FullName!,
            "MegaCrit.Sts2.Core.Entities.Multiplayer.RunLobbyPlayer");
    private static readonly Func<object, ulong> GetRunLobbyPlayerId =
        CreatePlayerIdAccessor(RunLobbyPlayerRejoinedPayloadType);

    private static readonly PropertyInfo LoadRunLobbyPlayerIdsProperty =
        ResolveProperty(typeof(LoadRunLobby), "PlayerIds", "ConnectedPlayerIds");
    private static readonly Func<LoadRunLobby, IEnumerable<ulong>> GetLoadRunLobbyPlayerIds =
        CreateLoadRunLobbyPlayerIdsGetter();

    internal static MethodInfo BatchCardAddMethod { get; } = ResolveBatchCardAddMethod();
    internal static bool UsesNewBatchCardAdd { get; } = BatchCardAddMethod.GetParameters().Length == 6;
    private static readonly BatchCardAddInvoker BatchCardAdd = CreateBatchCardAddInvoker();

    internal static MethodInfo ModifyDamageMethod { get; } = ResolveModifyDamageMethod();
    internal static bool UsesNewModifyDamage { get; } = ModifyDamageMethod.GetParameters().Length == 11;
    private static readonly ModifyDamageInvoker InvokeModifyDamage = CreateModifyDamageInvoker();

    private static readonly MethodInfo SetAnimationMethod = ResolveAnimationMethod(
        nameof(MegaAnimationState.SetAnimation),
        [typeof(string), typeof(bool), typeof(int)]);
    private static readonly Action<MegaAnimationState, string, bool, int> InvokeSetAnimation =
        CreateAnimationInvoker<Action<MegaAnimationState, string, bool, int>>(SetAnimationMethod);

    private static readonly MethodInfo AddAnimationMethod = ResolveAnimationMethod(
        nameof(MegaAnimationState.AddAnimation),
        [typeof(string), typeof(float), typeof(bool), typeof(int)]);
    private static readonly Action<MegaAnimationState, string, float, bool, int> InvokeAddAnimation =
        CreateAnimationInvoker<Action<MegaAnimationState, string, float, bool, int>>(AddAnimationMethod);

    internal static MethodInfo StickyCardPlayResultMethod { get; } = ResolveStickyCardPlayResultMethod();
    internal static bool UsesNewCardLocation { get; } =
        string.Equals(StickyCardPlayResultMethod.Name, "ModifyCardPlayResultLocation", StringComparison.Ordinal);

    // CreateCloneForPlayer was added after 0.107. Keep the 0.110-only method
    // reflection-only so loading/JITing this compatibility class on 0.107 never
    // resolves a missing metadata reference.
    private static readonly MethodInfo? NativeCreateCloneForPlayerMethod =
        AccessTools.Method(typeof(CardModel), "CreateCloneForPlayer", [typeof(Player)]);
    internal static bool UsesNativeCreateCloneForPlayer => NativeCreateCloneForPlayerMethod is not null;
    private static readonly Func<CardModel, Player, CardModel> CloneCardForPlayer =
        CreateCloneForPlayerInvoker();

    internal static MethodInfo MultiTargetDamageMethod { get; } = ResolveMultiTargetDamageMethod();
    internal static bool UsesNewMultiTargetDamage { get; } =
        MultiTargetDamageMethod.GetParameters().Length == 7;

    private static readonly MethodInfo? AttackCommandCardPlayGetter =
        AccessTools.PropertyGetter(typeof(AttackCommand), "CardPlay");
    private static readonly Func<AttackCommand, CardPlay?>? GetAttackCommandCardPlay =
        CreateAttackCommandCardPlayGetter();
    internal static bool UsesAttackCommandCardPlay =>
        AttackCommandCardPlayGetter is not null;

    private static readonly MethodInfo CharacterGenerateAnimatorMethod =
        ResolveCharacterGenerateAnimatorMethod();
    private static readonly CharacterAnimatorInvoker GenerateCharacterAnimatorInvoker =
        CreateCharacterAnimatorInvoker();

    private static readonly MethodInfo EndCombatInternalMethod =
        AccessTools.Method(typeof(CombatManager), "EndCombatInternal", Type.EmptyTypes)
        ?? throw new MissingMethodException(typeof(CombatManager).FullName, "EndCombatInternal()");
    private static readonly Func<CombatManager, Task> EndCombatInternalInvoker =
        AccessTools.MethodDelegate<Func<CombatManager, Task>>(EndCombatInternalMethod);

    // 0.110 API shape.
    private static readonly FieldInfo? NewModAssembliesField =
        AccessTools.Field(typeof(Mod), "assemblies");

    // 0.107-only compatibility fallback; remove or replace when 0.107 support is dropped.
    private static readonly FieldInfo? LegacyModAssemblyField =
        AccessTools.Field(typeof(Mod), "assembly");

    internal static string LStickPressAction { get; } = ResolveControllerAction("lStickPress", "joystickPress");
    internal static string LStickLeftAction { get; } = ResolveControllerAction("lStickLeft", "joystickLeft");
    internal static string LStickRightAction { get; } = ResolveControllerAction("lStickRight", "joystickRight");
    internal static string LStickUpAction { get; } = ResolveControllerAction("lStickUp", "joystickUp");
    internal static string LStickDownAction { get; } = ResolveControllerAction("lStickDown", "joystickDown");
    internal static string DPadLeftAction { get; } = ResolveControllerAction("dPadLeft", "dPadWest");
    internal static string DPadRightAction { get; } = ResolveControllerAction("dPadRight", "dPadEast");
    internal static string DPadUpAction { get; } = ResolveControllerAction("dPadUp", "dPadNorth");
    internal static string DPadDownAction { get; } = ResolveControllerAction("dPadDown", "dPadSouth");

    internal static void Initialize()
    {
        if (NewModAssembliesField is null && LegacyModAssemblyField is null)
        {
            throw new MissingFieldException(
                typeof(Mod).FullName,
                "assemblies (0.110) or assembly (0.107 compatibility fallback)");
        }

        if (EventEncountersGetter is null && AllEncountersGetter is null)
        {
            throw new MissingMemberException(
                typeof(ModelDb).FullName,
                "EventEncounters (0.110) or AllEncounters (0.107 compatibility fallback)");
        }

        MainFile.Logger.Info(
            $"[Loadout] STS2 API shape: " +
            $"CardPileCmd.Add={(UsesNewBatchCardAdd ? "0.110" : "0.107")}, " +
            $"Hook.ModifyDamage={(UsesNewModifyDamage ? "0.110" : "0.107")}, " +
            $"CreatureCmd.Damage multi-target={(UsesNewMultiTargetDamage ? "0.110" : "0.107")}, " +
            $"AttackCommand.CardPlay={(UsesAttackCommandCardPlay ? "0.110" : "0.107 fallback")}, " +
            $"MegaAnimationState animations={(SetAnimationMethod.ReturnType == typeof(void) ? "0.110" : "0.107")}, " +
            $"card result hook={(UsesNewCardLocation ? "0.110" : "0.107")}, " +
            $"card clone-for-player={(UsesNativeCreateCloneForPlayer ? "0.110" : "0.107 fallback")}, " +
            $"mod assemblies={(NewModAssembliesField is not null ? "0.110" : "0.107")}, " +
            $"ModelDb monsters={(AllModelsGetter is not null ? "All (0.110)" : "Monsters (0.107)")}, " +
            $"ModelDb encounters={(EventEncountersGetter is not null ? "acts + EventEncounters (0.110)" : "AllEncounters (0.107)")}, " +
            $"start lobby players={(StartRunLobbyPlayerType.Name == "StartRunLobbyPlayer" ? "0.110" : "0.107")}, " +
            $"run lobby rejoin={(RunLobbyPlayerRejoinedPayloadType == typeof(ulong) ? "0.107" : "0.110")}, " +
            $"load lobby player ids={(LoadRunLobbyPlayerIdsProperty.Name == "PlayerIds" ? "0.110" : "0.107")}.");
    }

    internal static IEnumerable<ulong> EnumerateStartRunLobbyPlayerIds(StartRunLobby lobby)
    {
        ArgumentNullException.ThrowIfNull(lobby);

        foreach (object player in GetStartRunLobbyPlayers(lobby))
            yield return GetStartRunLobbyPlayerId(player);
    }

    internal static Task SignalHookPlayerChoiceBegun(
        HookPlayerChoiceContext context,
        Player player,
        PlayerChoiceOptions options)
    {
        return BeginHookPlayerChoice(context, player, options);
    }

    internal static Task SignalHookPlayerChoiceEnded(
        HookPlayerChoiceContext context,
        Player player)
    {
        return EndHookPlayerChoice(context, player);
    }

    internal static IEnumerable<StartRunLobbyPlayerInfo> EnumerateStartRunLobbyPlayers(StartRunLobby lobby)
    {
        ArgumentNullException.ThrowIfNull(lobby);

        foreach (object player in GetStartRunLobbyPlayers(lobby))
        {
            yield return new StartRunLobbyPlayerInfo(
                GetStartRunLobbyPlayerId(player),
                GetStartRunLobbyPlayerSlot(player),
                GetStartRunLobbyPlayerCharacter(player),
                GetStartRunLobbyPlayerReady(player));
        }
    }

    internal static IEnumerable<ulong> EnumerateLoadRunLobbyPlayerIds(LoadRunLobby lobby)
    {
        ArgumentNullException.ThrowIfNull(lobby);
        return GetLoadRunLobbyPlayerIds(lobby);
    }

    internal static Delegate SubscribeStartRunLobbyPlayerConnected(
        StartRunLobby lobby,
        Action<ulong> handler)
    {
        ArgumentNullException.ThrowIfNull(lobby);
        ArgumentNullException.ThrowIfNull(handler);

        Delegate adapter = CreatePlayerIdEventAdapter(
            StartRunLobbyPlayerConnectedEvent,
            GetStartRunLobbyPlayerId,
            handler);
        StartRunLobbyPlayerConnectedEvent.AddEventHandler(lobby, adapter);
        return adapter;
    }

    internal static void UnsubscribeStartRunLobbyPlayerConnected(
        StartRunLobby lobby,
        Delegate adapter)
    {
        ArgumentNullException.ThrowIfNull(lobby);
        ArgumentNullException.ThrowIfNull(adapter);
        StartRunLobbyPlayerConnectedEvent.RemoveEventHandler(lobby, adapter);
    }

    internal static Delegate SubscribeStartRunLobbyPlayerDisconnected(
        StartRunLobby lobby,
        Action<ulong> handler)
    {
        ArgumentNullException.ThrowIfNull(lobby);
        ArgumentNullException.ThrowIfNull(handler);

        Delegate adapter = CreatePlayerIdEventAdapter(
            StartRunLobbyPlayerDisconnectedEvent,
            GetStartRunLobbyPlayerId,
            handler);
        StartRunLobbyPlayerDisconnectedEvent.AddEventHandler(lobby, adapter);
        return adapter;
    }

    internal static void UnsubscribeStartRunLobbyPlayerDisconnected(
        StartRunLobby lobby,
        Delegate adapter)
    {
        ArgumentNullException.ThrowIfNull(lobby);
        ArgumentNullException.ThrowIfNull(adapter);
        StartRunLobbyPlayerDisconnectedEvent.RemoveEventHandler(lobby, adapter);
    }

    internal static Delegate SubscribeRunLobbyPlayerRejoined(
        RunLobby lobby,
        Action<ulong> handler)
    {
        ArgumentNullException.ThrowIfNull(lobby);
        ArgumentNullException.ThrowIfNull(handler);

        Delegate adapter = CreatePlayerIdEventAdapter(
            RunLobbyPlayerRejoinedEvent,
            GetRunLobbyPlayerId,
            handler);
        RunLobbyPlayerRejoinedEvent.AddEventHandler(lobby, adapter);
        return adapter;
    }

    internal static void UnsubscribeRunLobbyPlayerRejoined(
        RunLobby lobby,
        Delegate adapter)
    {
        ArgumentNullException.ThrowIfNull(lobby);
        ArgumentNullException.ThrowIfNull(adapter);
        RunLobbyPlayerRejoinedEvent.RemoveEventHandler(lobby, adapter);
    }

    internal static IEnumerable<MonsterModel> EnumerateMonsterModels()
    {
        return AllModelsGetter is null
            ? ModelDb.Monsters
            : AllModelsGetter().OfType<MonsterModel>();
    }

    internal static IEnumerable<EncounterModel> EnumerateEncounters()
    {
        if (EventEncountersGetter is not null)
        {
            return ModelDb.Acts
                .Where(act => act.Index >= 0)
                .SelectMany(act => act.AllEncounters)
                .Concat(EventEncountersGetter());
        }

        return AllEncountersGetter?.Invoke()
               ?? throw new MissingMemberException(
                   typeof(ModelDb).FullName,
                   "EventEncounters or AllEncounters");
    }

    internal static Task<IReadOnlyList<CardPileAddResult>> AddCards(
        IEnumerable<CardModel> cards,
        CardPile newPile,
        CardPilePosition position = CardPilePosition.Bottom,
        AbstractModel? clonedBy = null,
        bool skipVisuals = false,
        bool isChangingOwners = false)
    {
        return BatchCardAdd(cards, newPile, position, clonedBy, skipVisuals, isChangingOwners);
    }

    internal static Task<IReadOnlyList<CardPileAddResult>> AddCards(
        IEnumerable<CardModel> cards,
        PileType newPileType,
        CardPilePosition position = CardPilePosition.Bottom,
        AbstractModel? clonedBy = null,
        bool skipVisuals = false)
    {
        // This overload is unchanged between 0.107 and 0.110.
        return CardPileCmd.Add(cards, newPileType, position, clonedBy, skipVisuals);
    }

    internal static decimal ModifyDamage(
        IRunState runState,
        ICombatState? combatState,
        Creature? target,
        Creature? dealer,
        decimal damage,
        ValueProp props,
        CardModel? cardSource,
        CardPlay? cardPlay,
        ModifyDamageHookType modifyDamageHookType,
        CardPreviewMode previewMode)
    {
        return InvokeModifyDamage(
            runState,
            combatState,
            target,
            dealer,
            damage,
            props,
            cardSource,
            cardPlay,
            modifyDamageHookType,
            previewMode);
    }

    internal static void SetAnimation(
        MegaAnimationState animationState,
        string animation,
        bool loop = true,
        int track = 0)
    {
        InvokeSetAnimation(animationState, animation, loop, track);
    }

    internal static CreatureAnimator GenerateCharacterAnimator(
        CharacterModel character,
        MegaSprite controller,
        Creature creature)
    {
        return GenerateCharacterAnimatorInvoker(character, controller, creature);
    }

    internal static Task EndCombatInternal(CombatManager combatManager)
    {
        return EndCombatInternalInvoker(combatManager);
    }

    internal static void AddAnimation(
        MegaAnimationState animationState,
        string animation,
        float delay = 0f,
        bool loop = true,
        int track = 0)
    {
        InvokeAddAnimation(animationState, animation, delay, loop, track);
    }

    internal static CardModel CreateCloneForPlayer(CardModel source, Player player)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(player);
        return CloneCardForPlayer(source, player);
    }

    internal static bool MatchesAttackCardPlay(
        AttackCommand command,
        CardModel source)
    {
        if (!ReferenceEquals(command.ModelSource, source))
            return false;

        // 0.107 only exposes ModelSource for card attribution.
        if (GetAttackCommandCardPlay is null)
            return true;

        CardPlay? cardPlay = GetAttackCommandCardPlay(command);
        // Target-only mod helpers can omit CardPlay.
        return cardPlay is null
               || ReferenceEquals(cardPlay.Card, source);
    }

    internal static IEnumerable<Assembly> GetModAssemblies(Mod mod)
    {
        if (NewModAssembliesField?.GetValue(mod) is IEnumerable<Assembly> assemblies)
            return assemblies;

        // 0.107-only compatibility fallback; remove or replace when 0.107 support is dropped.
        if (LegacyModAssemblyField?.GetValue(mod) is Assembly assembly)
            return [assembly];

        return Array.Empty<Assembly>();
    }

    private static MethodInfo ResolveBatchCardAddMethod()
    {
        // 0.110 API shape.
        MethodInfo? method = AccessTools.Method(
            typeof(CardPileCmd),
            nameof(CardPileCmd.Add),
            [
                typeof(IEnumerable<CardModel>),
                typeof(CardPile),
                typeof(CardPilePosition),
                typeof(AbstractModel),
                typeof(bool),
                typeof(bool)
            ]);
        if (method is not null)
            return method;

        // 0.107-only compatibility fallback; remove or replace when 0.107 support is dropped.
        method = AccessTools.Method(
            typeof(CardPileCmd),
            nameof(CardPileCmd.Add),
            [
                typeof(IEnumerable<CardModel>),
                typeof(CardPile),
                typeof(CardPilePosition),
                typeof(AbstractModel),
                typeof(bool)
            ]);
        return method ?? throw new MissingMethodException(
            typeof(CardPileCmd).FullName,
            "Add(IEnumerable<CardModel>, CardPile, CardPilePosition, AbstractModel, bool, bool) " +
            "or 0.107 Add(IEnumerable<CardModel>, CardPile, CardPilePosition, AbstractModel, bool)");
    }

    private static Func<IEnumerable<T>>? ResolveModelDbEnumerableGetter<T>(string propertyName)
    {
        MethodInfo? getter = AccessTools.PropertyGetter(typeof(ModelDb), propertyName);
        if (getter is null)
            return null;

        if (!typeof(IEnumerable<T>).IsAssignableFrom(getter.ReturnType))
        {
            throw new InvalidOperationException(
                $"Unexpected ModelDb.{propertyName} type: {getter.ReturnType.FullName}.");
        }

        MethodCallExpression call = Expression.Call(getter);
        UnaryExpression converted = Expression.Convert(call, typeof(IEnumerable<T>));
        return Expression.Lambda<Func<IEnumerable<T>>>(converted).Compile();
    }

    private static Func<CardModel, Player, CardModel> CreateCloneForPlayerInvoker()
    {
        ParameterExpression source = Expression.Parameter(typeof(CardModel), "source");
        ParameterExpression player = Expression.Parameter(typeof(Player), "player");

        if (NativeCreateCloneForPlayerMethod is not null)
        {
            MethodCallExpression nativeCall = Expression.Call(
                source,
                NativeCreateCloneForPlayerMethod,
                player);
            return Expression.Lambda<Func<CardModel, Player, CardModel>>(
                nativeCall,
                source,
                player).Compile();
        }

        // 0.107 compatibility fallback. The 0.110 native implementation is
        // exactly: CardModel clone = source.CreateClone(); clone._owner = player.
        // Resolve both members by reflection and compile the sequence once, so
        // the gameplay hot path is still a direct cached delegate invocation.
        MethodInfo legacyCreateClone = AccessTools.Method(
                                           typeof(CardModel),
                                           "CreateClone",
                                           Type.EmptyTypes)
                                       ?? throw new MissingMethodException(
                                           typeof(CardModel).FullName,
                                           "CreateClone()");
        FieldInfo ownerField = AccessTools.Field(typeof(CardModel), "_owner")
                               ?? throw new MissingFieldException(
                                   typeof(CardModel).FullName,
                                   "_owner");
        if (ownerField.FieldType != typeof(Player))
        {
            throw new InvalidOperationException(
                $"Unexpected CardModel._owner type: {ownerField.FieldType.FullName}.");
        }

        ParameterExpression clone = Expression.Variable(typeof(CardModel), "clone");
        BlockExpression legacyBody = Expression.Block(
            [clone],
            Expression.Assign(clone, Expression.Call(source, legacyCreateClone)),
            Expression.Assign(Expression.Field(clone, ownerField), player),
            clone);

        return Expression.Lambda<Func<CardModel, Player, CardModel>>(
            legacyBody,
            source,
            player).Compile();
    }

    private static BatchCardAddInvoker CreateBatchCardAddInvoker()
    {
        ParameterExpression cards = Expression.Parameter(typeof(IEnumerable<CardModel>), "cards");
        ParameterExpression newPile = Expression.Parameter(typeof(CardPile), "newPile");
        ParameterExpression position = Expression.Parameter(typeof(CardPilePosition), "position");
        ParameterExpression clonedBy = Expression.Parameter(typeof(AbstractModel), "clonedBy");
        ParameterExpression skipVisuals = Expression.Parameter(typeof(bool), "skipVisuals");
        ParameterExpression isChangingOwners = Expression.Parameter(typeof(bool), "isChangingOwners");

        Expression[] arguments = UsesNewBatchCardAdd
            ? [cards, newPile, position, clonedBy, skipVisuals, isChangingOwners]
            // 0.107-only compatibility fallback; remove or replace when 0.107 support is dropped.
            : [cards, newPile, position, clonedBy, skipVisuals];

        MethodCallExpression call = Expression.Call(BatchCardAddMethod, arguments);
        return Expression.Lambda<BatchCardAddInvoker>(
            call,
            cards,
            newPile,
            position,
            clonedBy,
            skipVisuals,
            isChangingOwners).Compile();
    }

    private static MethodInfo ResolveModifyDamageMethod()
    {
        // 0.110 API shape.
        MethodInfo? method = AccessTools.Method(
            typeof(Hook),
            nameof(Hook.ModifyDamage),
            [
                typeof(IRunState),
                typeof(ICombatState),
                typeof(Creature),
                typeof(Creature),
                typeof(decimal),
                typeof(ValueProp),
                typeof(CardModel),
                typeof(CardPlay),
                typeof(ModifyDamageHookType),
                typeof(CardPreviewMode),
                AbstractModelEnumerableByRef
            ]);
        if (method is not null)
            return method;

        // 0.107-only compatibility fallback; remove or replace when 0.107 support is dropped.
        method = AccessTools.Method(
            typeof(Hook),
            nameof(Hook.ModifyDamage),
            [
                typeof(IRunState),
                typeof(ICombatState),
                typeof(Creature),
                typeof(Creature),
                typeof(decimal),
                typeof(ValueProp),
                typeof(CardModel),
                typeof(ModifyDamageHookType),
                typeof(CardPreviewMode),
                AbstractModelEnumerableByRef
            ]);
        return method ?? throw new MissingMethodException(
            typeof(Hook).FullName,
            "ModifyDamage with CardPlay or 0.107 ModifyDamage without CardPlay");
    }

    private static ModifyDamageInvoker CreateModifyDamageInvoker()
    {
        ParameterExpression runState = Expression.Parameter(typeof(IRunState), "runState");
        ParameterExpression combatState = Expression.Parameter(typeof(ICombatState), "combatState");
        ParameterExpression target = Expression.Parameter(typeof(Creature), "target");
        ParameterExpression dealer = Expression.Parameter(typeof(Creature), "dealer");
        ParameterExpression damage = Expression.Parameter(typeof(decimal), "damage");
        ParameterExpression props = Expression.Parameter(typeof(ValueProp), "props");
        ParameterExpression cardSource = Expression.Parameter(typeof(CardModel), "cardSource");
        ParameterExpression cardPlay = Expression.Parameter(typeof(CardPlay), "cardPlay");
        ParameterExpression hookType = Expression.Parameter(typeof(ModifyDamageHookType), "modifyDamageHookType");
        ParameterExpression previewMode = Expression.Parameter(typeof(CardPreviewMode), "previewMode");
        ParameterExpression modifiers = Expression.Variable(typeof(IEnumerable<AbstractModel>), "modifiers");

        Expression[] arguments = UsesNewModifyDamage
            ? [
                runState, combatState, target, dealer, damage, props, cardSource,
                cardPlay, hookType, previewMode, modifiers
            ]
            // 0.107-only compatibility fallback; remove or replace when 0.107 support is dropped.
            : [
                runState, combatState, target, dealer, damage, props, cardSource,
                hookType, previewMode, modifiers
            ];

        MethodCallExpression call = Expression.Call(ModifyDamageMethod, arguments);
        BlockExpression body = Expression.Block([modifiers], call);
        return Expression.Lambda<ModifyDamageInvoker>(
            body,
            runState,
            combatState,
            target,
            dealer,
            damage,
            props,
            cardSource,
            cardPlay,
            hookType,
            previewMode).Compile();
    }

    private static MethodInfo ResolveStickyCardPlayResultMethod()
    {
        // The 0.110 return type intentionally stays reflection-only so the
        // compiled mod does not reference beta-only CardLocation.
        MethodInfo? method = typeof(Hook)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SingleOrDefault(candidate =>
            {
                if (!string.Equals(candidate.Name, "ModifyCardPlayResultLocation", StringComparison.Ordinal))
                    return false;

                ParameterInfo[] parameters = candidate.GetParameters();
                return parameters.Length == 6
                       && parameters[0].ParameterType == typeof(ICombatState)
                       && parameters[1].ParameterType == typeof(CardModel)
                       && parameters[2].ParameterType == typeof(bool)
                       && parameters[3].ParameterType == typeof(ResourceInfo)
                       && parameters[4].ParameterType == candidate.ReturnType
                       && parameters[5].ParameterType == AbstractModelEnumerableByRef;
            });
        if (method is not null)
            return method;

        // 0.107-only compatibility fallback; remove or replace when 0.107 support is dropped.
        method = AccessTools.Method(
            typeof(Hook),
            "ModifyCardPlayResultPileTypeAndPosition",
            [
                typeof(ICombatState),
                typeof(CardModel),
                typeof(bool),
                typeof(ResourceInfo),
                typeof(PileType),
                typeof(CardPilePosition),
                AbstractModelEnumerableByRef
            ]);
        return method ?? throw new MissingMethodException(
            typeof(Hook).FullName,
            "ModifyCardPlayResultLocation or 0.107 ModifyCardPlayResultPileTypeAndPosition");
    }

    private static MethodInfo ResolveMultiTargetDamageMethod()
    {
        // 0.110 API shape; CardPlay was added after cardSource.
        MethodInfo? method = AccessTools.Method(
            typeof(CreatureCmd),
            nameof(CreatureCmd.Damage),
            [
                typeof(PlayerChoiceContext),
                typeof(IEnumerable<Creature>),
                typeof(decimal),
                typeof(ValueProp),
                typeof(Creature),
                typeof(CardModel),
                typeof(CardPlay)
            ]);
        if (method is not null)
            return method;

        // 0.107-only compatibility fallback; remove or replace when 0.107 support is dropped.
        method = AccessTools.Method(
            typeof(CreatureCmd),
            nameof(CreatureCmd.Damage),
            [
                typeof(PlayerChoiceContext),
                typeof(IEnumerable<Creature>),
                typeof(decimal),
                typeof(ValueProp),
                typeof(Creature),
                typeof(CardModel)
            ]);
        return method ?? throw new MissingMethodException(
            typeof(CreatureCmd).FullName,
            "Damage(PlayerChoiceContext, IEnumerable<Creature>, decimal, ValueProp, Creature, CardModel, CardPlay) " +
            "or 0.107 Damage(PlayerChoiceContext, IEnumerable<Creature>, decimal, ValueProp, Creature, CardModel)");
    }

    private static Func<AttackCommand, CardPlay?>?
        CreateAttackCommandCardPlayGetter()
    {
        if (AttackCommandCardPlayGetter is null)
            return null;

        if (AttackCommandCardPlayGetter.ReturnType != typeof(CardPlay))
        {
            throw new InvalidOperationException(
                $"Unexpected AttackCommand.CardPlay type: " +
                $"{AttackCommandCardPlayGetter.ReturnType.FullName}.");
        }

        return AccessTools.MethodDelegate<Func<AttackCommand, CardPlay?>>(
            AttackCommandCardPlayGetter);
    }

    private static MethodInfo ResolveCharacterGenerateAnimatorMethod()
    {
        return AccessTools.Method(
                   typeof(CharacterModel),
                   nameof(CharacterModel.GenerateAnimator),
                   [typeof(MegaSprite), typeof(Creature)])
               ?? AccessTools.Method(
                   typeof(CharacterModel),
                   nameof(CharacterModel.GenerateAnimator),
                   [typeof(MegaSprite)])
               ?? throw new MissingMethodException(
                   typeof(CharacterModel).FullName,
                   "GenerateAnimator(MegaSprite, Creature) or GenerateAnimator(MegaSprite)");
    }

    private static CharacterAnimatorInvoker CreateCharacterAnimatorInvoker()
    {
        ParameterExpression character = Expression.Parameter(typeof(CharacterModel), "character");
        ParameterExpression controller = Expression.Parameter(typeof(MegaSprite), "controller");
        ParameterExpression creature = Expression.Parameter(typeof(Creature), "creature");
        Expression[] arguments = CharacterGenerateAnimatorMethod.GetParameters().Length == 2
            ? [controller, creature]
            : [controller];
        MethodCallExpression call = Expression.Call(character, CharacterGenerateAnimatorMethod, arguments);
        return Expression.Lambda<CharacterAnimatorInvoker>(
            call,
            character,
            controller,
            creature).Compile();
    }

    private static MethodInfo ResolveAnimationMethod(string methodName, Type[] parameterTypes)
    {
        MethodInfo? method = AccessTools.Method(typeof(MegaAnimationState), methodName, parameterTypes);
        if (method is null)
        {
            throw new MissingMethodException(
                typeof(MegaAnimationState).FullName,
                $"{methodName}({string.Join(", ", parameterTypes.Select(type => type.Name))}) " +
                "returning void (0.110) or MegaTrackEntry (0.107 compatibility fallback)");
        }

        // 0.110 returns void. The 0.107-only compatibility fallback
        // returns MegaTrackEntry; the cached delegate intentionally discards it.
        return method;
    }

    private static TDelegate CreateAnimationInvoker<TDelegate>(MethodInfo method)
        where TDelegate : Delegate
    {
        MethodInfo invokeMethod = typeof(TDelegate).GetMethod(nameof(Action.Invoke))
                                  ?? throw new MissingMethodException(typeof(TDelegate).FullName, nameof(Action.Invoke));
        ParameterInfo[] delegateParameters = invokeMethod.GetParameters();
        ParameterExpression[] parameters = delegateParameters
            .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name))
            .ToArray();
        MethodCallExpression call = Expression.Call(parameters[0], method, parameters.Skip(1));
        Expression body = method.ReturnType == typeof(void)
            ? call
            // 0.107-only compatibility fallback; remove or replace when 0.107 support is dropped.
            : Expression.Block(call, Expression.Empty());
        return Expression.Lambda<TDelegate>(body, parameters).Compile();
    }

    private static EventInfo ResolveEvent(Type declaringType, string eventName)
    {
        return declaringType.GetEvent(
                   eventName,
                   BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
               ?? throw new MissingMemberException(declaringType.FullName, eventName);
    }

    private static PropertyInfo ResolveProperty(Type declaringType, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            PropertyInfo? property = AccessTools.Property(declaringType, propertyName);
            if (property is not null)
                return property;
        }

        throw new MissingMemberException(
            declaringType.FullName,
            string.Join(" or ", propertyNames));
    }

    private static Type ResolvePlayerEventPayloadType(
        EventInfo eventInfo,
        params string[] allowedTypeNames)
    {
        Type? eventHandlerType = eventInfo.EventHandlerType;
        if (eventHandlerType is null
            || !eventHandlerType.IsGenericType
            || eventHandlerType.GetGenericTypeDefinition() != typeof(Action<>))
        {
            throw new InvalidOperationException(
                $"Unexpected {eventInfo.DeclaringType?.FullName}.{eventInfo.Name} handler type: " +
                $"{eventHandlerType?.FullName ?? "<null>"}.");
        }

        Type payloadType = eventHandlerType.GetGenericArguments()[0];
        if (!allowedTypeNames.Contains(payloadType.FullName, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unexpected {eventInfo.DeclaringType?.FullName}.{eventInfo.Name} payload type: " +
                $"{payloadType.FullName}.");
        }

        return payloadType;
    }

    private static Func<object, ulong> CreatePlayerIdAccessor(Type playerType)
    {
        ParameterExpression player = Expression.Parameter(typeof(object), "player");
        if (playerType == typeof(ulong))
        {
            return Expression.Lambda<Func<object, ulong>>(
                Expression.Convert(player, typeof(ulong)),
                player).Compile();
        }

        FieldInfo idField = AccessTools.Field(playerType, "id")
                            ?? throw new MissingFieldException(playerType.FullName, "id");
        if (idField.FieldType != typeof(ulong))
        {
            throw new InvalidOperationException(
                $"Unexpected {playerType.FullName}.id type: {idField.FieldType.FullName}.");
        }

        return Expression.Lambda<Func<object, ulong>>(
            Expression.Field(Expression.Convert(player, playerType), idField),
            player).Compile();
    }

    private static Func<object, T> CreatePlayerMemberAccessor<T>(Type playerType, params string[] names)
    {
        ParameterExpression player = Expression.Parameter(typeof(object), "player");
        Expression converted = Expression.Convert(player, playerType);

        foreach (string name in names)
        {
            FieldInfo? field = AccessTools.Field(playerType, name);
            if (field?.FieldType == typeof(T))
            {
                return Expression.Lambda<Func<object, T>>(
                    Expression.Field(converted, field),
                    player).Compile();
            }

            PropertyInfo? property = AccessTools.Property(playerType, name);
            if (property?.PropertyType == typeof(T) && property.GetMethod is not null)
            {
                return Expression.Lambda<Func<object, T>>(
                    Expression.Call(converted, property.GetMethod),
                    player).Compile();
            }
        }

        throw new MissingMemberException(playerType.FullName, string.Join(" or ", names));
    }

    private static Func<StartRunLobby, IEnumerable> CreateStartRunLobbyPlayersGetter()
    {
        if (!typeof(IEnumerable).IsAssignableFrom(StartRunLobbyPlayersProperty.PropertyType))
        {
            throw new InvalidOperationException(
                $"Unexpected StartRunLobby.Players type: " +
                $"{StartRunLobbyPlayersProperty.PropertyType.FullName}.");
        }

        Type? playerType = StartRunLobbyPlayersProperty.PropertyType
            .GetInterfaces()
            .Append(StartRunLobbyPlayersProperty.PropertyType)
            .FirstOrDefault(type =>
                type.IsGenericType
                && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
        if (playerType != StartRunLobbyPlayerType)
        {
            throw new InvalidOperationException(
                $"StartRunLobby.Players contains {playerType?.FullName ?? "<unknown>"}, " +
                $"but PlayerConnected supplies {StartRunLobbyPlayerType.FullName}.");
        }

        ParameterExpression lobby = Expression.Parameter(typeof(StartRunLobby), "lobby");
        MethodInfo getter = StartRunLobbyPlayersProperty.GetMethod
                            ?? throw new MissingMethodException(
                                typeof(StartRunLobby).FullName,
                                "get_Players");
        return Expression.Lambda<Func<StartRunLobby, IEnumerable>>(
            Expression.Convert(Expression.Call(lobby, getter), typeof(IEnumerable)),
            lobby).Compile();
    }

    private static Func<LoadRunLobby, IEnumerable<ulong>> CreateLoadRunLobbyPlayerIdsGetter()
    {
        if (!typeof(IEnumerable<ulong>).IsAssignableFrom(LoadRunLobbyPlayerIdsProperty.PropertyType))
        {
            throw new InvalidOperationException(
                $"Unexpected LoadRunLobby.{LoadRunLobbyPlayerIdsProperty.Name} type: " +
                $"{LoadRunLobbyPlayerIdsProperty.PropertyType.FullName}.");
        }

        ParameterExpression lobby = Expression.Parameter(typeof(LoadRunLobby), "lobby");
        MethodInfo getter = LoadRunLobbyPlayerIdsProperty.GetMethod
                            ?? throw new MissingMethodException(
                                typeof(LoadRunLobby).FullName,
                                $"get_{LoadRunLobbyPlayerIdsProperty.Name}");
        return Expression.Lambda<Func<LoadRunLobby, IEnumerable<ulong>>>(
            Expression.Convert(Expression.Call(lobby, getter), typeof(IEnumerable<ulong>)),
            lobby).Compile();
    }

    private static Delegate CreatePlayerIdEventAdapter(
        EventInfo eventInfo,
        Func<object, ulong> playerIdAccessor,
        Action<ulong> handler)
    {
        Type eventHandlerType = eventInfo.EventHandlerType
                                ?? throw new MissingMemberException(
                                    eventInfo.DeclaringType?.FullName,
                                    eventInfo.Name);
        Type payloadType = eventHandlerType.GetGenericArguments()[0];
        ParameterExpression payload = Expression.Parameter(payloadType, "player");
        InvocationExpression playerId = Expression.Invoke(
            Expression.Constant(playerIdAccessor),
            Expression.Convert(payload, typeof(object)));
        InvocationExpression invokeHandler = Expression.Invoke(
            Expression.Constant(handler),
            playerId);
        return Expression.Lambda(eventHandlerType, invokeHandler, payload).Compile();
    }

    private static MethodInfo ResolveHookPlayerChoiceMethod(
        string methodName,
        Type[] playerAwareParameters,
        Type[] legacyParameters)
    {
        return AccessTools.Method(typeof(HookPlayerChoiceContext), methodName, playerAwareParameters)
               ?? AccessTools.Method(typeof(HookPlayerChoiceContext), methodName, legacyParameters)
               ?? throw new MissingMethodException(
                   typeof(HookPlayerChoiceContext).FullName,
                   methodName);
    }

    private static HookPlayerChoiceBeginInvoker CreateHookPlayerChoiceBeginInvoker()
    {
        ParameterExpression context = Expression.Parameter(typeof(HookPlayerChoiceContext), "context");
        ParameterExpression player = Expression.Parameter(typeof(Player), "player");
        ParameterExpression options = Expression.Parameter(typeof(PlayerChoiceOptions), "options");
        MethodCallExpression call = HookPlayerChoiceBeginMethod.GetParameters().Length == 2
            ? Expression.Call(context, HookPlayerChoiceBeginMethod, player, options)
            : Expression.Call(context, HookPlayerChoiceBeginMethod, options);
        return Expression.Lambda<HookPlayerChoiceBeginInvoker>(
            call,
            context,
            player,
            options).Compile();
    }

    private static HookPlayerChoiceEndInvoker CreateHookPlayerChoiceEndInvoker()
    {
        ParameterExpression context = Expression.Parameter(typeof(HookPlayerChoiceContext), "context");
        ParameterExpression player = Expression.Parameter(typeof(Player), "player");
        MethodCallExpression call = HookPlayerChoiceEndMethod.GetParameters().Length == 1
            ? Expression.Call(context, HookPlayerChoiceEndMethod, player)
            : Expression.Call(context, HookPlayerChoiceEndMethod);
        return Expression.Lambda<HookPlayerChoiceEndInvoker>(call, context, player).Compile();
    }

    private static string ResolveControllerAction(string betaFieldName, string legacyFieldName)
    {
        FieldInfo? field = AccessTools.Field(typeof(Controller), betaFieldName);
        if (field is null)
        {
            // 0.107-only compatibility fallback; remove or replace when 0.107 support is dropped.
            field = AccessTools.Field(typeof(Controller), legacyFieldName);
        }

        object? value = field?.GetValue(null);
        string? action = value?.ToString();
        if (!string.IsNullOrWhiteSpace(action))
            return action;

        throw new MissingFieldException(
            typeof(Controller).FullName,
            $"{betaFieldName} (0.110) or {legacyFieldName} (0.107)");
    }
}
