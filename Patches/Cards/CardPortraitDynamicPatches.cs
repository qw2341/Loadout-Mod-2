#nullable enable

namespace Loadout.Services.CardPortraits;

using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using Loadout.Patches.Cards.CardModification;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Saves.Runs;

internal static class CardPortraitDynamicPatches
{
    private const string VisualHarmonyId = "Loadout.CardPortraits.Visual";
    private const string TemporaryHarmonyId = "Loadout.CardPortraits.Temporary";
    private static readonly Harmony VisualHarmony = new(VisualHarmonyId);
    private static readonly Harmony TemporaryHarmony = new(TemporaryHarmonyId);
    private static readonly ConditionalWeakTable<NCard, CardPortraitAnimationController> Controllers = new();
    private static readonly MethodInfo ReloadMethod =
        AccessTools.Method(typeof(NCard), "Reload")
        ?? throw new MissingMethodException(typeof(NCard).FullName, "Reload");
    private static readonly MethodInfo UpdateVisualsMethod =
        AccessTools.Method(
            typeof(NCard),
            nameof(NCard.UpdateVisuals),
            [typeof(PileType), typeof(CardPreviewMode)])
        ?? throw new MissingMethodException(typeof(NCard).FullName, nameof(NCard.UpdateVisuals));

    private static bool _visualInstalled;
    private static bool _temporaryInstalled;

    public static void EnsureVisualInstalled()
    {
        if (_visualInstalled)
            return;

        MethodInfo portraitGetter = AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.Portrait))
            ?? throw new MissingMethodException(typeof(CardModel).FullName, $"get_{nameof(CardModel.Portrait)}");
        MethodInfo beginDragMethod = AccessTools.Method(typeof(NHandCardHolder), nameof(NHandCardHolder.BeginDrag))
            ?? throw new MissingMethodException(typeof(NHandCardHolder).FullName, nameof(NHandCardHolder.BeginDrag));
        MethodInfo returnHolderMethod = AccessTools.Method(
            typeof(NPlayerHand),
            "ReturnHolderToHand",
            [typeof(NHandCardHolder)])
            ?? throw new MissingMethodException(typeof(NPlayerHand).FullName, "ReturnHolderToHand");
        VisualHarmony.Patch(portraitGetter, postfix: LastPostfix(portraitGetter, nameof(PortraitPostfix)));
        VisualHarmony.Patch(ReloadMethod, postfix: LastPostfix(ReloadMethod, nameof(ReloadPostfix)));
        VisualHarmony.Patch(UpdateVisualsMethod, postfix: LastPostfix(UpdateVisualsMethod, nameof(UpdateVisualsPostfix)));
        VisualHarmony.Patch(beginDragMethod, postfix: LastPostfix(beginDragMethod, nameof(BeginDragPostfix)));
        VisualHarmony.Patch(returnHolderMethod, postfix: LastPostfix(returnHolderMethod, nameof(ReturnHolderToHandPostfix)));
        PatchVisualPostfix(typeof(NCard), nameof(NCard.OnReturnedFromPool), nameof(PoolPostfix));
        PatchVisualPostfix(typeof(NCard), nameof(NCard.OnFreedToPool), nameof(PoolPostfix));
        PatchVisualPostfix(typeof(AbstractModel), nameof(AbstractModel.MutableClone), nameof(ClonePostfix));
        PatchVisualPostfix(
            typeof(AbstractModel),
            nameof(AbstractModel.ClonePreservingMutability),
            nameof(ClonePostfix));
        _visualInstalled = true;
        ApplyOverridesToLoadedCards();
    }

    public static void EnsureTemporaryInstalled()
    {
        EnsureVisualInstalled();
        if (_temporaryInstalled)
            return;

        PatchTemporaryPostfix(typeof(CardModel), nameof(CardModel.ToSerializable), nameof(ToSerializablePostfix));
        TemporaryHarmony.Patch(
            AccessTools.Method(typeof(ChecksumTracker), "ObtainAndTrackChecksum")
            ?? throw new MissingMethodException(typeof(ChecksumTracker).FullName, "ObtainAndTrackChecksum"),
            prefix: new HarmonyMethod(typeof(CardPortraitDynamicPatches), nameof(ChecksumPrefix)),
            finalizer: new HarmonyMethod(typeof(CardPortraitDynamicPatches), nameof(ChecksumFinalizer)));
        TemporaryHarmony.Patch(
            AccessTools.Method(typeof(SerializableCard), nameof(SerializableCard.Serialize))
            ?? throw new MissingMethodException(typeof(SerializableCard).FullName, nameof(SerializableCard.Serialize)),
            prefix: new HarmonyMethod(typeof(CardPortraitDynamicPatches), nameof(PacketPrefix)),
            finalizer: new HarmonyMethod(typeof(CardPortraitDynamicPatches), nameof(PacketFinalizer)));
        _temporaryInstalled = true;
    }

    public static void Clear()
    {
        if (_visualInstalled)
        {
            ReleaseControllersFromLoadedCards();
            VisualHarmony.UnpatchAll(VisualHarmonyId);
            _visualInstalled = false;
        }
        if (_temporaryInstalled)
        {
            TemporaryHarmony.UnpatchAll(TemporaryHarmonyId);
            _temporaryInstalled = false;
        }
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
            CardPortraitFields.SharesIdentity(model, card)
            || (model.DeckVersion is CardModel deckCard
                && CardPortraitFields.SharesIdentity(deckCard, card)));
    }

    public static void RefreshPermanent(ModelId cardId)
    {
        RefreshLoadedCards(model => model.Id.Equals(cardId));
    }

    private static HarmonyMethod LastPostfix(MethodBase target, string methodName) =>
        new(typeof(CardPortraitDynamicPatches), methodName)
        {
            priority = Priority.Last,
            after = Harmony.GetPatchInfo(target)?.Owners
                .Where(owner => !string.Equals(owner, VisualHarmonyId, StringComparison.Ordinal)
                                && !string.Equals(owner, TemporaryHarmonyId, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? []
        };

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
        {
            ReleaseController(__instance);
            return;
        }

        if (!CardPortraitRuntime.TryResolve(model, out CardPortraitTextureSequence sequence)
            || sequence.Frames.Count == 0)
        {
            ReleaseController(__instance);
            return;
        }

        ApplyPortrait(__instance, sequence.Frames[0]);
        if (sequence.Frames.Count > 1)
            GetOrCreateController(__instance).Bind(__instance, sequence);
        else
            ReleaseController(__instance);
    }

    private static void UpdateVisualsPostfix(NCard __instance)
    {
        if (Controllers.TryGetValue(__instance, out CardPortraitAnimationController? controller))
        {
            if (__instance.Model is CardModel currentModel
                && controller.IsBoundTo(currentModel))
            {
                controller.Reapply();
                return;
            }

            ReleaseController(__instance);
        }

        if (__instance.Model is not CardModel model
            || !CardPortraitRuntime.TryResolve(model, out CardPortraitTextureSequence sequence)
            || sequence.Frames.Count == 0)
        {
            return;
        }

        ApplyPortrait(__instance, sequence.Frames[0]);
        if (sequence.Frames.Count > 1)
            GetOrCreateController(__instance).Bind(__instance, sequence);
    }

    private static void PoolPostfix(NCard __instance)
    {
        ReleaseController(__instance);
    }

    private static void BeginDragPostfix(NHandCardHolder __instance)
    {
        if (__instance.CardNode is { } card)
            ReapplyOverride(card);
    }

    private static void ReturnHolderToHandPostfix(NHandCardHolder holder)
    {
        if (holder.CardNode is { } card)
            ReapplyOverride(card);
    }

    private static void ClonePostfix(AbstractModel __instance, AbstractModel __result)
    {
        if (__instance is CardModel source && __result is CardModel destination)
            CardPortraitFields.Copy(source, destination);
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

    private static void PatchVisualPostfix(Type targetType, string methodName, string patchName)
    {
        VisualHarmony.Patch(
            AccessTools.Method(targetType, methodName)
            ?? throw new MissingMethodException(targetType.FullName, methodName),
            postfix: new HarmonyMethod(typeof(CardPortraitDynamicPatches), patchName));
    }

    private static void PatchTemporaryPostfix(Type targetType, string methodName, string patchName)
    {
        TemporaryHarmony.Patch(
            AccessTools.Method(targetType, methodName)
            ?? throw new MissingMethodException(targetType.FullName, methodName),
            postfix: new HarmonyMethod(typeof(CardPortraitDynamicPatches), patchName));
    }

    private static CardPortraitAnimationController GetOrCreateController(NCard card)
    {
        if (Controllers.TryGetValue(card, out CardPortraitAnimationController? controller))
            return controller;

        controller = new CardPortraitAnimationController
        {
            Name = CardPortraitAnimationController.NodeName
        };
        Controllers.Add(card, controller);
        card.AddChild(controller);
        return controller;
    }

    private static void ReleaseController(NCard card)
    {
        if (!Controllers.TryGetValue(card, out CardPortraitAnimationController? controller))
            return;

        Controllers.Remove(card);
        controller.Release();
        if (controller.GetParent() == card)
            card.RemoveChild(controller);
        controller.QueueFree();
    }

    private static void ApplyPortrait(NCard card, Texture2D texture)
    {
        TextureRect? portrait = card.Model is { } model
                                && CardModificationRuntime.ShouldUseAncientRendering(model)
            ? card.GetNodeOrNull<TextureRect>("%AncientPortrait")
            : card.GetNodeOrNull<TextureRect>("%Portrait");
        if (portrait is not null && !ReferenceEquals(portrait.Texture, texture))
            portrait.Texture = texture;
    }

    private static void ReapplyOverride(NCard card)
    {
        if (Controllers.TryGetValue(card, out CardPortraitAnimationController? controller))
        {
            controller.Reapply();
            return;
        }

        if (card.Model is CardModel model
            && CardPortraitRuntime.TryResolve(model, out CardPortraitTextureSequence sequence)
            && sequence.Frames.Count > 0)
        {
            ApplyPortrait(card, sequence.Frames[0]);
        }
    }

    private static void ApplyOverridesToLoadedCards()
    {
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root is null)
            return;

        Visit(tree.Root);
        return;

        static void Visit(Node node)
        {
            if (node is NCard { Model: CardModel model } card
                && CardPortraitRuntime.HasOverride(model)
                && CardPortraitRuntime.TryResolve(model, out CardPortraitTextureSequence sequence)
                && sequence.Frames.Count > 0)
            {
                ApplyPortrait(card, sequence.Frames[0]);
                if (sequence.Frames.Count > 1)
                    GetOrCreateController(card).Bind(card, sequence);
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
            if (node is NCard card)
                ReleaseController(card);
        }
    }

    private static void RefreshLoadedCards(Func<CardModel, bool> predicate)
    {
        if (!_visualInstalled || Engine.GetMainLoop() is not SceneTree tree || tree.Root is null)
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
