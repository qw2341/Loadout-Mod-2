#nullable enable

namespace Loadout.Keywords;

using System;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using Loadout.Services.Compatibility;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using LinqExpression = System.Linq.Expressions.Expression;

public static class CardResultLocationKeywordPatch
{
    internal static MethodInfo GetPostfixMethod()
    {
        if (!Sts2Compatibility.UsesNewCardLocation)
        {
            // 0.107-only compatibility fallback; remove or replace when 0.107 support is dropped.
            return AccessTools.Method(typeof(CardResultLocationKeywordPatch), nameof(LegacyPostfix))
                   ?? throw new MissingMethodException(
                       typeof(CardResultLocationKeywordPatch).FullName,
                       nameof(LegacyPostfix));
        }

        Type resultType = Sts2Compatibility.StickyCardPlayResultMethod.ReturnType;
        Type transformerType = typeof(CardLocationResult<>).MakeGenericType(resultType);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(transformerType.TypeHandle);

        MethodInfo genericPostfix = AccessTools.Method(
                                        typeof(CardResultLocationKeywordPatch),
                                        nameof(NewPostfix))
                                    ?? throw new MissingMethodException(
                                        typeof(CardResultLocationKeywordPatch).FullName,
                                        nameof(NewPostfix));
        return genericPostfix.MakeGenericMethod(resultType);
    }

    // Maintained newer API path. TResult closes over CardLocation at runtime,
    // keeping that newer-only type out of the compiled assembly references.
    [HarmonyPostfix]
    public static void NewPostfix<TResult>(CardModel card, ref TResult __result)
        where TResult : struct
    {
        if (LoadoutKeywords.Has(card, LoadoutKeywords.Sticky))
        {
            // The result player can differ from Card.Owner (THE_BALL does this).
            // Sticky belongs to the player who played the card, so it must replace
            // both the result pile and result player.
            __result = CardLocationResult<TResult>.Create(
                card.Owner,
                PileType.Hand,
                CardPilePosition.Bottom);
            return;
        }

        if (!LoadoutKeywords.Has(card, LoadoutKeywords.Passing))
            return;

        Player currentPlayer = CardLocationResult<TResult>.GetPlayer(__result);
        Player? receivingPlayer = PassingKeyword.GetTarget(card, currentPlayer);
        if (receivingPlayer is null)
            return;

        PileType originalPileType = CardLocationResult<TResult>.GetPileType(__result);
        CardPilePosition originalPosition = CardLocationResult<TResult>.GetPosition(__result);
        PileType pileType = originalPileType;
        CardPilePosition position = originalPosition;
        if (receivingPlayer != card.Owner && pileType == PileType.Discard)
        {
            pileType = PileType.Draw;
            position = CardPilePosition.Random;
        }

        if (receivingPlayer == currentPlayer
            && pileType == originalPileType
            && position == originalPosition)
        {
            return;
        }

        __result = CardLocationResult<TResult>.Create(
            receivingPlayer,
            pileType,
            position);
    }

    // 0.107-only compatibility fallback; remove or replace when 0.107 support is dropped.
    [HarmonyPostfix]
    public static void LegacyPostfix(
        CardModel card,
        ref ValueTuple<PileType, CardPilePosition> __result)
    {
        if (LoadoutKeywords.Has(card, LoadoutKeywords.Sticky))
            __result = (PileType.Hand, CardPilePosition.Bottom);
    }

    private static class CardLocationResult<TResult>
        where TResult : struct
    {
        internal static readonly Func<TResult, Player> GetPlayer =
            CreateGetter<Player>("player", "Player");
        internal static readonly Func<TResult, PileType> GetPileType =
            CreateGetter<PileType>("pileType", "PileType");
        internal static readonly Func<TResult, CardPilePosition> GetPosition =
            CreateGetter<CardPilePosition>("position", "Position");
        internal static readonly Func<Player, PileType, CardPilePosition, TResult> Create =
            CreateConstructor();

        private static Func<TResult, TMember> CreateGetter<TMember>(
            string fieldName,
            string propertyName)
        {
            Type resultType = typeof(TResult);
            const BindingFlags flags =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            MemberInfo? member = resultType.GetField(fieldName, flags)
                                 ?? (MemberInfo?)resultType.GetProperty(fieldName, flags)
                                 ?? resultType.GetProperty(propertyName, flags);
            if (member is null)
                throw new MissingMemberException(resultType.FullName, fieldName);

            ParameterExpression current = LinqExpression.Parameter(resultType, "current");
            System.Linq.Expressions.Expression value = member switch
            {
                FieldInfo field => LinqExpression.Field(current, field),
                PropertyInfo property => LinqExpression.Property(current, property),
                _ => throw new InvalidOperationException(
                    $"Unsupported member on {resultType.FullName}.")
            };
            return LinqExpression.Lambda<Func<TResult, TMember>>(value, current).Compile();
        }

        private static Func<Player, PileType, CardPilePosition, TResult> CreateConstructor()
        {
            Type resultType = typeof(TResult);
            const BindingFlags flags =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            ConstructorInfo constructor = resultType.GetConstructor(
                                              flags,
                                              binder: null,
                                              [
                                                  typeof(Player),
                                                  typeof(PileType),
                                                  typeof(CardPilePosition)
                                              ],
                                              modifiers: null)
                                          ?? throw new MissingMethodException(
                                              resultType.FullName,
                                              ".ctor(Player, PileType, CardPilePosition)");

            ParameterExpression player = LinqExpression.Parameter(typeof(Player), "player");
            ParameterExpression pileType = LinqExpression.Parameter(typeof(PileType), "pileType");
            ParameterExpression position =
                LinqExpression.Parameter(typeof(CardPilePosition), "position");
            NewExpression replacement = LinqExpression.New(
                constructor,
                player,
                pileType,
                position);
            return LinqExpression.Lambda<Func<Player, PileType, CardPilePosition, TResult>>(
                replacement,
                player,
                pileType,
                position).Compile();
        }
    }
}
