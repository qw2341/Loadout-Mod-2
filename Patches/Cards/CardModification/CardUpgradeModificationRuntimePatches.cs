#nullable enable

namespace Loadout.Patches.Cards.CardModification;

using System;
using System.Collections.Generic;
using HarmonyLib;
using Loadout.Keywords;
using Loadout.Services.CardModification;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

internal static class CardUpgradeModificationRuntimePatches
{
    private const string HarmonyId = "Loadout.CardModification.Upgrade";
    private static readonly Harmony Harmony = new(HarmonyId);

    [ThreadStatic]
    private static Stack<CardUpgradeModificationSpec?>? _overrides;

    [ThreadStatic]
    private static Stack<EnergyReplayScope>? _energyReplays;

    private static bool _enabled;
    private static bool _energyEnabled;

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
            postfix: new HarmonyMethod(
                typeof(CardUpgradeModificationContextPatch),
                nameof(CardUpgradeModificationContextPatch.Postfix)),
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
        EnableEnergyCostPatch();
        _enabled = true;
    }

    private static void EnableEnergyCostPatch()
    {
        if (_energyEnabled)
            return;

        Harmony.Patch(
            AccessTools.Method(
                typeof(CardEnergyCost),
                nameof(CardEnergyCost.UpgradeBy))
            ?? throw new MissingMethodException(
                typeof(CardEnergyCost).FullName,
                nameof(CardEnergyCost.UpgradeBy)),
            prefix: new HarmonyMethod(
                typeof(CardUpgradeModificationEnergyCostPatch),
                nameof(CardUpgradeModificationEnergyCostPatch.Prefix)));
        _energyEnabled = true;
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
            LoadoutKeywordRuntimePatches.EnableFromPowerKeywordEntries(
                null,
                value.PowerKeywordEntryUpgrades,
                value.AddedPowerKeywordEntries);
            LoadoutKeywordRuntimePatches.EnableFromCardKeywordEntries(
                null,
                value.CardKeywordEntryUpgrades,
                value.AddedCardKeywordEntries);
        }

        _overrides ??= new Stack<CardUpgradeModificationSpec?>();
        _overrides.Push(value);
        return new OverrideScope();
    }

    public static EnergyReplayScope BeginEnergyReplay(CardModel card)
    {
        EnableEnergyCostPatch();
        _energyReplays ??= new Stack<EnergyReplayScope>();
        EnergyReplayScope scope = new(card.EnergyCost);
        _energyReplays.Push(scope);
        return scope;
    }

    internal static void RecordEnergyUpgrade(CardEnergyCost energyCost, int addend)
    {
        if (_energyReplays is { Count: > 0 })
            _energyReplays.Peek().Record(energyCost, addend);
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
        _energyEnabled = false;
        _overrides?.Clear();
        _energyReplays?.Clear();
        if (PermanentCardModificationStore.HasAnyUpgradeModifications)
            Enable();
    }

    public static void ClearAll()
    {
        Harmony.UnpatchAll(HarmonyId);
        _enabled = false;
        _energyEnabled = false;
        _overrides?.Clear();
        _energyReplays?.Clear();
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

    public sealed class EnergyReplayScope : IDisposable
    {
        private readonly CardEnergyCost _energyCost;
        private bool _disposed;

        internal EnergyReplayScope(CardEnergyCost energyCost)
        {
            _energyCost = energyCost;
            UpgradedCost = energyCost.CostsX
                ? null
                : energyCost.Canonical;
        }

        public int? UpgradedCost { get; private set; }

        internal void Record(CardEnergyCost energyCost, int addend)
        {
            if (!ReferenceEquals(_energyCost, energyCost)
                || !UpgradedCost.HasValue
                || addend == 0)
            {
                return;
            }

            UpgradedCost = (int)Math.Clamp(
                (long)UpgradedCost.Value + addend,
                0L,
                int.MaxValue);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_energyReplays is { Count: > 0 }
                && ReferenceEquals(_energyReplays.Peek(), this))
            {
                _energyReplays.Pop();
            }
        }
    }
}

internal readonly record struct CardUpgradeModificationContextState(
    CardModel? ActiveCard,
    CardUpgradeModificationSpec? Modification,
    int? LoadoutUpgradedEnergyCost,
    bool Applied);

internal static class CardUpgradeModificationContextPatch
{
    [ThreadStatic]
    internal static CardModel? ActiveCard;

    [ThreadStatic]
    internal static CardUpgradeModificationSpec? Modification;

    [ThreadStatic]
    internal static int? LoadoutUpgradedEnergyCost;

    [ThreadStatic]
    internal static bool Applied;

    public static void Prefix(
        CardModel __instance,
        out CardUpgradeModificationContextState __state)
    {
        __state = new CardUpgradeModificationContextState(
            ActiveCard,
            Modification,
            LoadoutUpgradedEnergyCost,
            Applied);
        CardUpgradeModificationSpec resolved =
            CardUpgradeModificationRuntimePatches.Resolve(__instance);
        ActiveCard = resolved.IsEmpty ? null : __instance;
        Modification = resolved.IsEmpty ? null : resolved;
        LoadoutUpgradedEnergyCost = resolved.EnergyCostDelta.HasValue
                                    && !__instance.EnergyCost.CostsX
            ? __instance.EnergyCost.Canonical
            : null;
        Applied = false;
    }

    public static Exception? Finalizer(
        CardUpgradeModificationContextState __state,
        Exception? __exception)
    {
        ActiveCard = __state.ActiveCard;
        Modification = __state.Modification;
        LoadoutUpgradedEnergyCost = __state.LoadoutUpgradedEnergyCost;
        Applied = __state.Applied;
        return __exception;
    }

    public static void Postfix(CardModel __instance)
    {
        LoadoutPowerKeywordState.Synchronize(__instance);
        LoadoutCardKeywordState.Synchronize(__instance);
    }
}

internal static class CardUpgradeModificationEnergyCostPatch
{
    public static void Prefix(CardEnergyCost __instance, int addend)
    {
        CardUpgradeModificationRuntimePatches.RecordEnergyUpgrade(
            __instance,
            addend);

        CardModel? card = CardUpgradeModificationContextPatch.ActiveCard;
        int? energyCost =
            CardUpgradeModificationContextPatch.LoadoutUpgradedEnergyCost;
        if (card is null
            || !energyCost.HasValue
            || __instance.CostsX
            || addend == 0
            || !ReferenceEquals(card.EnergyCost, __instance))
        {
            return;
        }

        CardUpgradeModificationContextPatch.LoadoutUpgradedEnergyCost =
            (int)Math.Clamp(
                (long)energyCost.Value + addend,
                0L,
                int.MaxValue);
    }
}

internal static class CardUpgradeModificationRecalculationPatch
{
    [HarmonyBefore("Loadout.Keyword.InfiniteUpgrade")]
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
        CardModificationRuntime.ApplyUpgradeModification(
            card,
            modification,
            CardUpgradeModificationContextPatch.LoadoutUpgradedEnergyCost);
    }
}
