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
using MegaCrit.Sts2.Core.Saves.Runs;

internal static class CardPortraitDynamicPatches
{
    private const string VisualHarmonyId = "Loadout.CardPortraits.Visual";
    private const string TemporaryHarmonyId = "Loadout.CardPortraits.Temporary";
    private static readonly Harmony VisualHarmony = new(VisualHarmonyId);
    private static readonly Harmony TemporaryHarmony = new(TemporaryHarmonyId);
    private static readonly ConditionalWeakTable<NCard, CardPortraitAnimationController> Controllers = new();
    private static readonly ConditionalWeakTable<NCard, AppliedPortraitState> AppliedPortraits = new();
    private static readonly MethodInfo ReloadMethod =
        AccessTools.Method(typeof(NCard), "Reload")
        ?? throw new MissingMethodException(typeof(NCard).FullName, "Reload");
    private static readonly MethodInfo UpdateVisualsMethod =
        AccessTools.Method(
            typeof(NCard),
            nameof(NCard.UpdateVisuals),
            [typeof(PileType), typeof(CardPreviewMode)])
        ?? throw new MissingMethodException(typeof(NCard).FullName, nameof(NCard.UpdateVisuals));
    private static readonly MethodInfo UpdatePortraitMethod =
        AccessTools.Method(typeof(NCard), "UpdatePortrait")
        ?? throw new MissingMethodException(typeof(NCard).FullName, "UpdatePortrait");
    private static readonly MethodInfo EnterTreeMethod =
        AccessTools.Method(typeof(NCard), nameof(NCard._EnterTree))
        ?? throw new MissingMethodException(typeof(NCard).FullName, nameof(NCard._EnterTree));

    [ThreadStatic]
    private static CardModel? _portraitResolutionModel;

    [ThreadStatic]
    private static CardPortraitTextureSequence? _portraitResolutionSequence;

    [ThreadStatic]
    private static Texture2D? _portraitResolutionFallbackTexture;

    private static bool _visualInstalled;
    private static bool _temporaryInstalled;

    public static void EnsureVisualInstalled()
    {
        if (_visualInstalled)
            return;

        MethodInfo portraitGetter = AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.Portrait))
            ?? throw new MissingMethodException(typeof(CardModel).FullName, $"get_{nameof(CardModel.Portrait)}");
        VisualHarmony.Patch(
            portraitGetter,
            postfix: LastPostfix(portraitGetter, nameof(PortraitPostfix)));

        VisualHarmony.Patch(
            UpdatePortraitMethod,
            prefix: FirstPrefix(UpdatePortraitMethod, nameof(UpdatePortraitPrefix)),
            postfix: LastPostfix(UpdatePortraitMethod, nameof(UpdatePortraitPostfix)));
        VisualHarmony.Patch(
            UpdatePortraitMethod,
            postfix: FirstPostfix(
                UpdatePortraitMethod,
                nameof(UpdatePortraitBeforeForeignPostfix)));

        PatchVisualSandwich(UpdateVisualsMethod, nameof(UpdateVisualsPostfix));
        PatchVisualSandwich(EnterTreeMethod, nameof(EnterTreePostfix));
        PatchVisualSandwich(ReloadMethod, nameof(ReloadPostfix));
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
            RestorePortraitsFromLoadedCards();
            VisualHarmony.UnpatchAll(VisualHarmonyId);
            ClearPortraitResolution();
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

    public static void RefreshTemporary(CardModel card, string? previousPortraitId = null)
    {
        RefreshLoadedCards((node, model) =>
            CardPortraitFields.SharesIdentity(model, card)
            || (model.DeckVersion is CardModel deckCard
                && CardPortraitFields.SharesIdentity(deckCard, card))
            || (previousPortraitId is not null
                && AppliedPortraits.TryGetValue(node, out AppliedPortraitState? applied)
                && applied.Sequence is not null
                && string.Equals(
                    applied.Sequence.PortraitId,
                    previousPortraitId,
                    StringComparison.Ordinal)));
    }

    public static void RefreshModelId(ModelId cardId)
    {
        RefreshLoadedCards((_, model) => model.Id.Equals(cardId));
    }

    private static HarmonyMethod LastPostfix(MethodBase target, string methodName) =>
        new(typeof(CardPortraitDynamicPatches), methodName)
        {
            priority = Priority.Last,
            after = GetForeignOwners(target)
        };

    private static HarmonyMethod FirstPrefix(MethodBase target, string methodName) =>
        new(typeof(CardPortraitDynamicPatches), methodName)
        {
            priority = Priority.First,
            before = GetForeignOwners(target)
        };

    private static HarmonyMethod FirstPostfix(MethodBase target, string methodName) =>
        new(typeof(CardPortraitDynamicPatches), methodName)
        {
            priority = Priority.First,
            before = GetForeignOwners(target)
        };

    private static string[] GetForeignOwners(MethodBase target) =>
        Harmony.GetPatchInfo(target)?.Owners
            .Where(owner => !string.Equals(owner, VisualHarmonyId, StringComparison.Ordinal)
                            && !string.Equals(owner, TemporaryHarmonyId, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];

    private static void UpdatePortraitPrefix(NCard __instance)
    {
        ClearPortraitResolution();
        if (__instance.Model is not CardModel model)
            return;

        _portraitResolutionModel = model;
        if (CardPortraitRuntime.TryResolve(model, out CardPortraitTextureSequence sequence)
            && sequence.Frames.Count > 0)
        {
            _portraitResolutionFallbackTexture = GetFallbackTexture(__instance, model);
            if (_portraitResolutionFallbackTexture is not null)
                ApplyPortrait(__instance, _portraitResolutionFallbackTexture);
            _portraitResolutionSequence = sequence;
        }
    }

    private static void UpdatePortraitBeforeForeignPostfix(NCard __instance)
    {
        if (__instance.Model is CardModel model
            && ReferenceEquals(_portraitResolutionModel, model)
            && _portraitResolutionSequence is not null
            && _portraitResolutionFallbackTexture is not null)
        {
            ApplyPortrait(__instance, _portraitResolutionFallbackTexture);
        }
    }

    private static void PortraitPostfix(CardModel __instance, ref Texture2D __result)
    {
        if (ReferenceEquals(_portraitResolutionModel, __instance)
            && _portraitResolutionSequence is { Frames.Count: > 0 } sequence)
        {
            __result = sequence.Frames[0];
        }
    }

    private static void UpdatePortraitPostfix(NCard __instance)
    {
        try
        {
            if (__instance.Model is not CardModel model)
            {
                ClearAppliedPortrait(__instance);
                return;
            }

            if (!ReferenceEquals(_portraitResolutionModel, model)
                || _portraitResolutionSequence is not { Frames.Count: > 0 } sequence)
            {
                RecordNoOverride(__instance, model);
                return;
            }

            ApplyResolvedPortrait(
                __instance,
                model,
                sequence,
                _portraitResolutionFallbackTexture);
        }
        finally
        {
            ClearPortraitResolution();
        }
    }

    private static void UpdateVisualsPostfix(NCard __instance)
    {
        ReapplyOverride(__instance);
    }

    private static void EnterTreePostfix(NCard __instance)
    {
        ReapplyOverride(__instance);
    }

    private static void ReloadPostfix(NCard __instance)
    {
        ReapplyOverride(__instance);
    }

    private static void PrepareForForeignPortraitHooks(NCard __instance)
    {
        if (__instance.Model is not CardModel model
            || !AppliedPortraits.TryGetValue(__instance, out AppliedPortraitState? applied)
            || !ReferenceEquals(applied.Model, model)
            || applied.Sequence is null
            || applied.FallbackTexture is null)
        {
            return;
        }

        ApplyPortrait(__instance, applied.FallbackTexture);
    }

    private static void ReapplyOverride(NCard card)
    {
        if (card.Model is not CardModel model)
        {
            ClearAppliedPortrait(card);
            return;
        }

        if (AppliedPortraits.TryGetValue(card, out AppliedPortraitState? applied)
            && ReferenceEquals(applied.Model, model))
        {
            if (applied.Sequence is not null)
                ApplyResolvedPortrait(card, model, applied.Sequence);
            return;
        }

        if (CardPortraitRuntime.TryResolve(model, out CardPortraitTextureSequence sequence)
            && sequence.Frames.Count > 0)
        {
            ApplyResolvedPortrait(card, model, sequence);
            return;
        }

        RecordNoOverride(card, model);
    }

    private static void PoolPostfix(NCard __instance)
    {
        AppliedPortraits.Remove(__instance);
        ReleaseController(__instance);
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

    private static void PatchVisualSandwich(MethodInfo target, string finalPostfixName)
    {
        VisualHarmony.Patch(
            target,
            prefix: FirstPrefix(target, nameof(PrepareForForeignPortraitHooks)),
            postfix: LastPostfix(target, finalPostfixName));
        VisualHarmony.Patch(
            target,
            postfix: FirstPostfix(target, nameof(PrepareForForeignPortraitHooks)));
    }

    private static void PatchTemporaryPostfix(Type targetType, string methodName, string patchName)
    {
        TemporaryHarmony.Patch(
            AccessTools.Method(targetType, methodName)
            ?? throw new MissingMethodException(targetType.FullName, methodName),
            postfix: new HarmonyMethod(typeof(CardPortraitDynamicPatches), patchName));
    }

    private static void ClearPortraitResolution()
    {
        _portraitResolutionModel = null;
        _portraitResolutionSequence = null;
        _portraitResolutionFallbackTexture = null;
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

    private static TextureRect? GetPortraitNode(NCard card)
    {
        return card.Model is { } model
               && CardModificationRuntime.ShouldUseAncientRendering(model)
            ? card.GetNodeOrNull<TextureRect>("%AncientPortrait")
            : card.GetNodeOrNull<TextureRect>("%Portrait");
    }

    private static Texture2D? GetFallbackTexture(NCard card, CardModel model)
    {
        if (AppliedPortraits.TryGetValue(card, out AppliedPortraitState? applied)
            && ReferenceEquals(applied.Model, model)
            && applied.FallbackTexture is not null)
        {
            return applied.FallbackTexture;
        }

        return model.Portrait;
    }

    private static void ApplyPortrait(NCard card, Texture2D texture)
    {
        TextureRect? portrait = GetPortraitNode(card);
        if (portrait is not null && !ReferenceEquals(portrait.Texture, texture))
            portrait.Texture = texture;
    }

    private static void ApplyResolvedPortrait(
        NCard card,
        CardModel model,
        CardPortraitTextureSequence sequence,
        Texture2D? fallbackTexture = null)
    {
        if (fallbackTexture is null
            && AppliedPortraits.TryGetValue(card, out AppliedPortraitState? previous)
            && ReferenceEquals(previous.Model, model))
        {
            fallbackTexture = previous.FallbackTexture;
        }
        fallbackTexture ??= GetPortraitNode(card)?.Texture;

        ApplyPortrait(card, sequence.Frames[0]);
        if (!AppliedPortraits.TryGetValue(card, out AppliedPortraitState? applied)
            || !ReferenceEquals(applied.Model, model)
            || !ReferenceEquals(applied.Sequence, sequence)
            || !ReferenceEquals(applied.FallbackTexture, fallbackTexture))
        {
            AppliedPortraits.Remove(card);
            AppliedPortraits.Add(card, new AppliedPortraitState(model, sequence, fallbackTexture));
        }
        if (sequence.Frames.Count > 1)
        {
            CardPortraitAnimationController controller = GetOrCreateController(card);
            if (controller.IsBoundTo(sequence))
                controller.Reapply();
            else
                controller.Bind(card, sequence);
        }
        else
        {
            ReleaseController(card);
        }
    }

    private static void ClearAppliedPortrait(NCard card)
    {
        if (!AppliedPortraits.Remove(card))
            return;

        ReleaseController(card);
    }

    private static void RecordNoOverride(NCard card, CardModel model)
    {
        if (AppliedPortraits.TryGetValue(card, out AppliedPortraitState? applied)
            && ReferenceEquals(applied.Model, model)
            && applied.Sequence is null)
        {
            return;
        }

        AppliedPortraits.Remove(card);
        AppliedPortraits.Add(card, new AppliedPortraitState(model, null, null));
        ReleaseController(card);
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
                ApplyResolvedPortrait(card, model, sequence);
            }

            foreach (Node child in node.GetChildren())
                Visit(child);
        }
    }

    private static void RestorePortraitsFromLoadedCards()
    {
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root is null)
            return;

        Visit(tree.Root);
        return;

        static void Visit(Node node)
        {
            foreach (Node child in node.GetChildren())
                Visit(child);
            if (node is NCard card
                && AppliedPortraits.TryGetValue(card, out AppliedPortraitState? applied))
            {
                ClearAppliedPortrait(card);
                if (applied.Sequence is not null)
                    ReloadCard(card);
            }
        }
    }

    private static void RefreshLoadedCards(Func<NCard, CardModel, bool> predicate)
    {
        if (!_visualInstalled || Engine.GetMainLoop() is not SceneTree tree || tree.Root is null)
            return;

        Visit(tree.Root);
        return;

        void Visit(Node node)
        {
            foreach (Node child in node.GetChildren())
                Visit(child);

            if (node is NCard { Model: CardModel model } card && predicate(card, model))
                ReloadCard(card);
        }
    }

    private sealed record AppliedPortraitState(
        CardModel Model,
        CardPortraitTextureSequence? Sequence,
        Texture2D? FallbackTexture);
}
