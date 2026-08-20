#nullable enable

namespace Loadout.Services.CardPortraits;

using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Saves.Runs;

internal static class CardPortraitDynamicPatches
{
    private const string HarmonyId = "Loadout.CardPortraits.Dynamic";
    private static readonly Harmony Harmony = new(HarmonyId);
    private static readonly MethodInfo ReloadMethod =
        AccessTools.Method(typeof(NCard), "Reload")
        ?? throw new MissingMethodException(typeof(NCard).FullName, "Reload");
    private static readonly MethodInfo UpdateVisualsMethod =
        AccessTools.Method(
            typeof(NCard),
            nameof(NCard.UpdateVisuals),
            [typeof(PileType), typeof(CardPreviewMode)])
        ?? throw new MissingMethodException(typeof(NCard).FullName, nameof(NCard.UpdateVisuals));

    private static bool _installed;

    public static void EnsureInstalled()
    {
        if (_installed)
            return;

        HarmonyMethod lastPortraitPostfix = new(
            typeof(CardPortraitDynamicPatches),
            nameof(PortraitPostfix))
        {
            priority = Priority.Last
        };
        HarmonyMethod lastReloadPostfix = new(
            typeof(CardPortraitDynamicPatches),
            nameof(ReloadPostfix))
        {
            priority = Priority.Last
        };

        Harmony.Patch(
            AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.Portrait))
            ?? throw new MissingMethodException(typeof(CardModel).FullName, $"get_{nameof(CardModel.Portrait)}"),
            postfix: lastPortraitPostfix);
        Harmony.Patch(ReloadMethod, postfix: lastReloadPostfix);
        Harmony.Patch(
            UpdateVisualsMethod,
            postfix: new HarmonyMethod(
                typeof(CardPortraitDynamicPatches),
                nameof(UpdateVisualsPostfix))
            {
                priority = Priority.Last
            });
        Harmony.Patch(
            AccessTools.Method(typeof(NCard), nameof(NCard.OnReturnedFromPool))
            ?? throw new MissingMethodException(typeof(NCard).FullName, nameof(NCard.OnReturnedFromPool)),
            postfix: new HarmonyMethod(typeof(CardPortraitDynamicPatches), nameof(PoolPostfix)));
        Harmony.Patch(
            AccessTools.Method(typeof(NCard), nameof(NCard.OnFreedToPool))
            ?? throw new MissingMethodException(typeof(NCard).FullName, nameof(NCard.OnFreedToPool)),
            postfix: new HarmonyMethod(typeof(CardPortraitDynamicPatches), nameof(PoolPostfix)));
        PatchPostfix(typeof(CardModel), nameof(CardModel.ToSerializable), nameof(ToSerializablePostfix));
        Harmony.Patch(
            AccessTools.Method(typeof(ChecksumTracker), "ObtainAndTrackChecksum")
            ?? throw new MissingMethodException(typeof(ChecksumTracker).FullName, "ObtainAndTrackChecksum"),
            prefix: new HarmonyMethod(typeof(CardPortraitDynamicPatches), nameof(ChecksumPrefix)),
            finalizer: new HarmonyMethod(typeof(CardPortraitDynamicPatches), nameof(ChecksumFinalizer)));
        Harmony.Patch(
            AccessTools.Method(typeof(SerializableCard), nameof(SerializableCard.Serialize))
            ?? throw new MissingMethodException(typeof(SerializableCard).FullName, nameof(SerializableCard.Serialize)),
            prefix: new HarmonyMethod(typeof(CardPortraitDynamicPatches), nameof(PacketPrefix)),
            finalizer: new HarmonyMethod(typeof(CardPortraitDynamicPatches), nameof(PacketFinalizer)));
        _installed = true;
        AttachControllersToLoadedCards();
    }

    public static void Clear()
    {
        if (!_installed)
            return;

        ReleaseControllersFromLoadedCards();
        Harmony.UnpatchAll(HarmonyId);
        _installed = false;
    }

    public static void ReloadCard(NCard card)
    {
        if (!GodotObject.IsInstanceValid(card) || !card.IsInsideTree())
            return;

        ReloadMethod.Invoke(card, null);
        if (card.Model is not null && card.IsNodeReady())
            card.UpdateVisuals(card.DisplayingPile, CardPreviewMode.Normal);
    }

    public static void RefreshTemporary(CardModel card)
    {
        RefreshLoadedCards(model =>
            ReferenceEquals(model, card)
            || ReferenceEquals(model.DeckVersion, card));
    }

    public static void RefreshPermanent(ModelId cardId)
    {
        RefreshLoadedCards(model => model.Id.Equals(cardId));
    }

    private static void PortraitPostfix(CardModel __instance, ref Texture2D __result)
    {
        if (CardPortraitRuntime.TryResolve(__instance, out CardPortraitTextureSequence sequence)
            && sequence.Frames.Count > 0)
        {
            __result = sequence.Frames[0];
        }
    }

    private static void ReloadPostfix(NCard __instance)
    {
        if (__instance.Model is not CardModel model)
            return;

        CardPortraitAnimationController? controller =
            __instance.GetNodeOrNull<CardPortraitAnimationController>(CardPortraitAnimationController.NodeName);
        if (CardPortraitRuntime.TryResolve(model, out CardPortraitTextureSequence sequence)
            && sequence.Frames.Count > 0)
        {
            (controller ?? GetOrCreateController(__instance)).Bind(__instance, sequence);
        }
        else if (controller is not null)
        {
            ReleaseController(__instance, controller);
        }
    }

    private static void UpdateVisualsPostfix(NCard __instance)
    {
        __instance
            .GetNodeOrNull<CardPortraitAnimationController>(CardPortraitAnimationController.NodeName)
            ?.Reapply();
    }

    private static void PoolPostfix(NCard __instance)
    {
        CardPortraitAnimationController? controller =
            __instance.GetNodeOrNull<CardPortraitAnimationController>(CardPortraitAnimationController.NodeName);
        if (controller is null)
            return;

        ReleaseController(__instance, controller);
    }

    private static void ToSerializablePostfix(CardModel __instance, SerializableCard __result) =>
        CardPortraitPersistence.Export(__instance, __result);

    private static void ChecksumPrefix(out IDisposable __state) =>
        __state = CardPortraitPersistence.BeginChecksumSerialization();

    private static Exception? ChecksumFinalizer(IDisposable __state, Exception? __exception)
    {
        __state.Dispose();
        return __exception;
    }

    private static void PacketPrefix(SerializableCard __instance, out CardPortraitPacketState __state) =>
        __state = CardPortraitPersistence.RemoveForPacket(__instance);

    private static Exception? PacketFinalizer(
        SerializableCard __instance,
        CardPortraitPacketState __state,
        Exception? __exception)
    {
        CardPortraitPersistence.RestoreAfterPacket(__instance, __state);
        return __exception;
    }

    private static void PatchPostfix(Type targetType, string methodName, string patchName)
    {
        Harmony.Patch(
            AccessTools.Method(targetType, methodName)
            ?? throw new MissingMethodException(targetType.FullName, methodName),
            postfix: new HarmonyMethod(typeof(CardPortraitDynamicPatches), patchName));
    }

    private static CardPortraitAnimationController GetOrCreateController(NCard card)
    {
        CardPortraitAnimationController? controller =
            card.GetNodeOrNull<CardPortraitAnimationController>(CardPortraitAnimationController.NodeName);
        if (controller is not null)
            return controller;

        controller = new CardPortraitAnimationController
        {
            Name = CardPortraitAnimationController.NodeName
        };
        card.AddChild(controller);
        return controller;
    }

    private static void ReleaseController(NCard card, CardPortraitAnimationController controller)
    {
        controller.Stop();
        if (controller.GetParent() == card)
            card.RemoveChild(controller);
        controller.QueueFree();
    }

    private static void AttachControllersToLoadedCards()
    {
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root is null)
            return;

        Visit(tree.Root);
        return;

        static void Visit(Node node)
        {
            if (node is NCard { Model: CardModel model } card
                && CardPortraitRuntime.TryResolve(model, out CardPortraitTextureSequence sequence)
                && sequence.Frames.Count > 0)
            {
                CardPortraitAnimationController controller = GetOrCreateController(card);
                controller.Bind(card, sequence);
            }

            foreach (Node child in node.GetChildren())
                Visit(child);
        }
    }

    private static void ReleaseControllersFromLoadedCards()
    {
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root is null)
            return;

        Visit(tree.Root);
        return;

        static void Visit(Node node)
        {
            foreach (Node child in node.GetChildren())
                Visit(child);

            if (node is not NCard card)
                return;

            CardPortraitAnimationController? controller =
                card.GetNodeOrNull<CardPortraitAnimationController>(CardPortraitAnimationController.NodeName);
            if (controller is null)
                return;

            ReleaseController(card, controller);
        }
    }

    private static void RefreshLoadedCards(Func<CardModel, bool> predicate)
    {
        if (!_installed || Engine.GetMainLoop() is not SceneTree tree || tree.Root is null)
            return;

        Visit(tree.Root);
        return;

        void Visit(Node node)
        {
            foreach (Node child in node.GetChildren())
                Visit(child);

            if (node is NCard { Model: CardModel model } card && predicate(model))
                ReloadCard(card);
        }
    }
}
