#nullable enable

namespace Loadout.Patches.Cards.CardModification;

using System;
using System.Collections.Generic;
using HarmonyLib;
using Loadout.Keywords;
using Loadout.Services.CardModification;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

internal static class CardUpgradeModificationRuntimePatches
{
    private const string HarmonyId = "Loadout.CardModification.Upgrade";
    private static readonly Harmony Harmony = new(HarmonyId);

    [ThreadStatic]
    private static Stack<CardUpgradeModificationSpec?>? _overrides;

    private static bool _enabled;

    public static void Enable()
    {
        if (_enabled)
            return;

        Harmony.Patch(
            AccessTools.Method(typeof(CardModel), nameof(CardModel.UpgradeInternal))
            ?? throw new MissingMethodException(
                typeof(CardModel).FullName,
                nameof(CardModel.UpgradeInternal)),
            prefix: new HarmonyMethod(
                typeof(CardUpgradeModificationContextPatch),
                nameof(CardUpgradeModificationContextPatch.Prefix)),
            finalizer: new HarmonyMethod(
                typeof(CardUpgradeModificationContextPatch),
                nameof(CardUpgradeModificationContextPatch.Finalizer)));
        Harmony.Patch(
            AccessTools.Method(
                typeof(DynamicVarSet),
                nameof(DynamicVarSet.RecalculateForUpgradeOrEnchant))
            ?? throw new MissingMethodException(
                typeof(DynamicVarSet).FullName,
                nameof(DynamicVarSet.RecalculateForUpgradeOrEnchant)),
            prefix: new HarmonyMethod(
                typeof(CardUpgradeModificationRecalculationPatch),
                nameof(CardUpgradeModificationRecalculationPatch.Prefix)));
        _enabled = true;
    }

    public static IDisposable BeginOverride(
        CardUpgradeModificationSpec? modification)
    {
        CardUpgradeModificationSpec value =
            modification?.Clone() ?? new CardUpgradeModificationSpec();
        value.Normalize();
        if (!value.IsEmpty)
        {
            Enable();
            LoadoutKeywordRuntimePatches.EnableFromOverrides(
                value.KeywordOverrides);
        }

        _overrides ??= new Stack<CardUpgradeModificationSpec?>();
        _overrides.Push(value);
        return new OverrideScope();
    }

    public static CardUpgradeModificationSpec Resolve(CardModel card)
    {
        if (_overrides is { Count: > 0 })
            return _overrides.Peek()?.Clone()
                   ?? new CardUpgradeModificationSpec();

        CardModificationSpec permanent =
            PermanentCardModificationStore.Get(card.Id);
        CardModificationDelta? temporary =
            CardModificationFields.TryGet(card, out CardModificationCardData data)
                ? data.Delta
                : null;
        return CardModificationRuntime.ResolveUpgradeModification(
            permanent,
            temporary);
    }

    public static void ResetRunPatches()
    {
        Harmony.UnpatchAll(HarmonyId);
        _enabled = false;
        _overrides?.Clear();
        if (PermanentCardModificationStore.HasAnyUpgradeModifications)
            Enable();
    }

    public static void ClearAll()
    {
        Harmony.UnpatchAll(HarmonyId);
        _enabled = false;
        _overrides?.Clear();
    }

    private sealed class OverrideScope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_overrides is { Count: > 0 })
                _overrides.Pop();
        }
    }
}

internal readonly record struct CardUpgradeModificationContextState(
    CardModel? ActiveCard,
    CardUpgradeModificationSpec? Modification,
    bool Applied);

internal static class CardUpgradeModificationContextPatch
{
    [ThreadStatic]
    internal static CardModel? ActiveCard;

    [ThreadStatic]
    internal static CardUpgradeModificationSpec? Modification;

    [ThreadStatic]
    internal static bool Applied;

    public static void Prefix(
        CardModel __instance,
        out CardUpgradeModificationContextState __state)
    {
        __state = new CardUpgradeModificationContextState(
            ActiveCard,
            Modification,
            Applied);
        CardUpgradeModificationSpec resolved =
            CardUpgradeModificationRuntimePatches.Resolve(__instance);
        ActiveCard = resolved.IsEmpty ? null : __instance;
        Modification = resolved.IsEmpty ? null : resolved;
        Applied = false;
    }

    public static Exception? Finalizer(
        CardUpgradeModificationContextState __state,
        Exception? __exception)
    {
        ActiveCard = __state.ActiveCard;
        Modification = __state.Modification;
        Applied = __state.Applied;
        return __exception;
    }
}

internal static class CardUpgradeModificationRecalculationPatch
{
    [HarmonyAfter("Loadout.Keyword.InfiniteUpgrade")]
    public static void Prefix(DynamicVarSet __instance)
    {
        CardModel? card = CardUpgradeModificationContextPatch.ActiveCard;
        CardUpgradeModificationSpec? modification =
            CardUpgradeModificationContextPatch.Modification;
        if (CardUpgradeModificationContextPatch.Applied
            || card is null
            || modification is null
            || !ReferenceEquals(card.DynamicVars, __instance))
        {
            return;
        }

        CardUpgradeModificationContextPatch.Applied = true;
        CardModificationRuntime.ApplyUpgradeModification(card, modification);
    }
}
