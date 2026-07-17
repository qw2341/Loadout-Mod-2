#nullable enable

namespace Loadout.Services.Compatibility;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

/// <summary>
/// Resolves the two supported STS2 API shapes once: the maintained newer API
/// and the legacy 0.107 API. Gameplay call sites use cached compiled delegates,
/// so compatibility probing never adds reflection to hot paths.
/// </summary>
internal static class Sts2Compatibility
{
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

    private static readonly Type AbstractModelEnumerableByRef =
        typeof(IEnumerable<AbstractModel>).MakeByRefType();

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

    internal static MethodInfo MultiTargetDamageMethod { get; } = ResolveMultiTargetDamageMethod();
    internal static bool UsesNewMultiTargetDamage { get; } =
        MultiTargetDamageMethod.GetParameters().Length == 7;

    // Maintained newer API shape.
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
                "assemblies (newer) or assembly (0.107 compatibility fallback)");
        }

        MainFile.Logger.Info(
            $"[Loadout] STS2 API shape: " +
            $"CardPileCmd.Add={(UsesNewBatchCardAdd ? "newer" : "0.107")}, " +
            $"Hook.ModifyDamage={(UsesNewModifyDamage ? "newer" : "0.107")}, " +
            $"CreatureCmd.Damage multi-target={(UsesNewMultiTargetDamage ? "newer" : "0.107")}, " +
            $"MegaAnimationState animations={(SetAnimationMethod.ReturnType == typeof(void) ? "newer" : "0.107")}, " +
            $"card result hook={(UsesNewCardLocation ? "newer" : "0.107")}, " +
            $"mod assemblies={(NewModAssembliesField is not null ? "newer" : "0.107")}.");
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
        // This overload is unchanged between 0.107 and the newer API.
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

    internal static void AddAnimation(
        MegaAnimationState animationState,
        string animation,
        float delay = 0f,
        bool loop = true,
        int track = 0)
    {
        InvokeAddAnimation(animationState, animation, delay, loop, track);
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
        // Maintained newer API shape.
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
        // Maintained newer API shape.
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
        // The newer return type intentionally stays reflection-only so the
        // compiled mod does not reference beta/newer-only CardLocation.
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
        // Maintained newer API shape; CardPlay was added after cardSource.
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

    private static MethodInfo ResolveAnimationMethod(string methodName, Type[] parameterTypes)
    {
        MethodInfo? method = AccessTools.Method(typeof(MegaAnimationState), methodName, parameterTypes);
        if (method is null)
        {
            throw new MissingMethodException(
                typeof(MegaAnimationState).FullName,
                $"{methodName}({string.Join(", ", parameterTypes.Select(type => type.Name))}) " +
                "returning void (newer) or MegaTrackEntry (0.107 compatibility fallback)");
        }

        // Maintained newer API returns void. The 0.107-only compatibility fallback
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

    private static string ResolveControllerAction(string newerFieldName, string legacyFieldName)
    {
        FieldInfo? field = AccessTools.Field(typeof(Controller), newerFieldName);
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
            $"{newerFieldName} or 0.107 {legacyFieldName}");
    }
}
