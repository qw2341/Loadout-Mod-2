#nullable enable

namespace Loadout.Patches.Cards;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;

/// <summary>
/// STS1-style dynamic constructor instrumentation. Every concrete CardModel type gets
/// a postfix on each constructor declared by that exact type. Base constructor calls are
/// ignored by checking the runtime type, so only the most-derived card class can apply.
/// Discovery is parallel; Harmony writes are serialized.
/// </summary>
internal static class CardConstructorPatcher
{
    private static readonly object PatchGate = new();
    private static readonly HashSet<MethodBase> Patched = [];
    private static readonly ConcurrentDictionary<Assembly, byte> SeenAssemblies = new();
    private static readonly HarmonyMethod Postfix = new(
        AccessTools.Method(typeof(CardConstructorPatcher), nameof(AfterConstructor))!);

    private static Harmony? _harmony;
    private static int _installed;

    internal static int PatchedConstructorCount
    {
        get { lock (PatchGate) return Patched.Count; }
    }

    internal static void Install(Harmony harmony)
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0)
            return;

        _harmony = harmony;
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoaded;

        ConstructorInfo[] constructors = AppDomain.CurrentDomain.GetAssemblies()
            .AsParallel()
            .WithDegreeOfParallelism(Math.Max(1, Environment.ProcessorCount - 1))
            .SelectMany(Discover)
            .Distinct()
            .OrderBy(ctor => ctor.DeclaringType?.FullName, StringComparer.Ordinal)
            .ThenBy(ctor => ctor.MetadataToken)
            .ToArray();

        Patch(constructors);
    }

    private static void OnAssemblyLoaded(object? sender, AssemblyLoadEventArgs args)
    {
        if (_harmony is null || Volatile.Read(ref _installed) == 0)
            return;
        Patch(Discover(args.LoadedAssembly));
    }

    private static IEnumerable<ConstructorInfo> Discover(Assembly assembly)
    {
        if (!SeenAssemblies.TryAdd(assembly, 0))
            yield break;

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            types = exception.Types.Where(type => type is not null).Cast<Type>().ToArray();
        }
        catch
        {
            yield break;
        }

        foreach (Type type in types)
        {
            if (type.IsAbstract
                || type.ContainsGenericParameters
                || !typeof(CardModel).IsAssignableFrom(type))
            {
                continue;
            }

            ConstructorInfo[] constructors;
            try
            {
                constructors = type.GetConstructors(
                    BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly);
            }
            catch
            {
                continue;
            }

            foreach (ConstructorInfo constructor in constructors)
                yield return constructor;
        }
    }

    private static void Patch(IEnumerable<ConstructorInfo> constructors)
    {
        Harmony? harmony = _harmony;
        if (harmony is null)
            return;

        lock (PatchGate)
        {
            foreach (ConstructorInfo constructor in constructors)
            {
                if (!Patched.Add(constructor))
                    continue;

                try
                {
                    harmony.Patch(constructor, postfix: Postfix);
                }
                catch (Exception exception)
                {
                    Patched.Remove(constructor);
                    MainFile.Logger.Warn(
                        $"[Loadout] Failed to patch card constructor {constructor.DeclaringType?.FullName}: {exception.Message}");
                }
            }
        }
    }

    private static void AfterConstructor(CardModel __instance, MethodBase __originalMethod)
    {
        if (__instance is null
            || __originalMethod.DeclaringType != __instance.GetType())
        {
            return;
        }

        CardModificationPatcher.ApplyPermanentToCard(__instance);
    }
}
