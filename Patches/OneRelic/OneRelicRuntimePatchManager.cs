#nullable enable

namespace Loadout.Patches.OneRelic;

using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;

internal static class OneRelicRuntimePatchManager
{
    private const string HarmonyId = "Loadout.OneRelic.Runtime";
    private static readonly Harmony RuntimeHarmony = new(HarmonyId);
    private static bool _active;

    internal static void Reconcile(bool active)
    {
        if (_active == active)
            return;
        RuntimeHarmony.UnpatchAll(HarmonyId);
        _active = false;
        if (!active)
            return;

        try
        {
            PatchFactory(nameof(RelicFactory.PullNextRelicFromFront));
            PatchFactory(nameof(RelicFactory.PullNextRelicFromBack));

            MethodInfo obtain = AccessTools.Method(
                typeof(RelicCmd),
                nameof(RelicCmd.Obtain),
                [typeof(RelicModel), typeof(Player), typeof(int)])
                ?? throw new MissingMethodException(typeof(RelicCmd).FullName, nameof(RelicCmd.Obtain));
            RuntimeHarmony.Patch(
                obtain,
                prefix: PatchMethod(typeof(OneRelicObtainPatch), nameof(OneRelicObtainPatch.Prefix), Priority.First),
                postfix: PatchMethod(typeof(OneRelicObtainPatch), nameof(OneRelicObtainPatch.Postfix)),
                finalizer: PatchMethod(typeof(OneRelicObtainPatch), nameof(OneRelicObtainPatch.Finalizer)));

            MethodInfo toMutable = AccessTools.Method(typeof(RelicModel), nameof(RelicModel.ToMutable), Type.EmptyTypes)
                                   ?? throw new MissingMethodException(typeof(RelicModel).FullName, nameof(RelicModel.ToMutable));
            RuntimeHarmony.Patch(
                toMutable,
                prefix: PatchMethod(typeof(OneRelicToMutablePatch), nameof(OneRelicToMutablePatch.Prefix), Priority.First));

            PatchTypedObtainMethods();
            _active = true;
        }
        catch
        {
            RuntimeHarmony.UnpatchAll(HarmonyId);
            throw;
        }
    }

    private static void PatchFactory(string methodName)
    {
        MethodInfo method = AccessTools.Method(
            typeof(RelicFactory),
            methodName,
            [typeof(Player), typeof(RelicRarity), typeof(Func<RelicModel, bool>)])
            ?? throw new MissingMethodException(typeof(RelicFactory).FullName, methodName);
        HarmonyMethod postfix = PatchMethod(typeof(OneRelicFactoryPatch), nameof(OneRelicFactoryPatch.Postfix));
        postfix.after = ["Loadout.CustomRuns.Runtime"];
        RuntimeHarmony.Patch(method, postfix: postfix);
    }

    private static void PatchTypedObtainMethods()
    {
        MethodInfo definition = typeof(RelicCmd).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == nameof(RelicCmd.Obtain)
                              && method.IsGenericMethodDefinition
                              && method.GetParameters().Length == 1);
        MethodInfo prefixDefinition = AccessTools.DeclaredMethod(
            typeof(OneRelicTypedObtainPatch),
            nameof(OneRelicTypedObtainPatch.Prefix))
            ?? throw new MissingMethodException(typeof(OneRelicTypedObtainPatch).FullName, nameof(OneRelicTypedObtainPatch.Prefix));

        foreach (Type relicType in ModelDb.AllRelics.Select(relic => relic.GetType()).Distinct())
        {
            MethodInfo original = definition.MakeGenericMethod(relicType);
            MethodInfo prefix = prefixDefinition.MakeGenericMethod(relicType);
            RuntimeHarmony.Patch(original, prefix: new HarmonyMethod(prefix) { priority = Priority.First });
        }
    }

    private static HarmonyMethod PatchMethod(Type type, string name, int priority = Priority.Normal)
    {
        MethodInfo method = AccessTools.DeclaredMethod(type, name)
                            ?? throw new MissingMethodException(type.FullName, name);
        return new HarmonyMethod(method) { priority = priority };
    }
}
