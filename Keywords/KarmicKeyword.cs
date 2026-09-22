#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using Loadout.Services.Compatibility;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.ValueProps;

public sealed class KarmicKeyword : LoadoutKeywordModel
{
    public static KarmicKeyword Instance { get; } = new();

    private KarmicKeyword() { }

    public override CardKeyword Keyword => LoadoutKeywords.Karmic;
    public override string StorageKey => LoadoutKeywords.KarmicKey;
    public override string TitleLocKey => "LOADOUT-KARMIC.title";
    public override LoadoutKeywordEditorSection EditorSection => LoadoutKeywordEditorSection.Joke;
}

public static class KarmicKeywordPatches
{
    private static readonly Harmony Harmony = new("Loadout.Keyword.Karmic");
    private static readonly AsyncLocal<AttackScope?> Current = new();
    private static readonly AsyncLocal<AttackScope?> Repeating = new();
    private static bool Installed;

    public static void Prepare()
    {
        if (Installed)
            return;

        MethodInfo execute = AccessTools.Method(typeof(AttackCommand), nameof(AttackCommand.Execute));
        Harmony.Patch(execute,
            prefix: new HarmonyMethod(typeof(KarmicKeywordPatches), nameof(AttackPrefix)),
            postfix: new HarmonyMethod(typeof(KarmicKeywordPatches), nameof(AttackPostfix)),
            finalizer: new HarmonyMethod(typeof(KarmicKeywordPatches), nameof(AttackFinalizer)));
        Harmony.Patch(AccessTools.AsyncMoveNext(execute),
            transpiler: new HarmonyMethod(typeof(KarmicKeywordPatches), nameof(AttackTranspiler)));
        Harmony.Patch(AccessTools.Method(typeof(VfxCmd), nameof(VfxCmd.PlayVfx)),
            prefix: new HarmonyMethod(typeof(KarmicKeywordPatches), nameof(VfxPrefix)),
            postfix: new HarmonyMethod(typeof(KarmicKeywordPatches), nameof(VfxPostfix)));
        Harmony.Patch(Sts2Compatibility.MultiTargetDamageMethod,
            prefix: new HarmonyMethod(typeof(KarmicKeywordPatches), nameof(DamagePrefix)),
            finalizer: new HarmonyMethod(typeof(KarmicKeywordPatches), nameof(DamageFinalizer)));
        Installed = true;
    }

    private static void DamagePrefix(CardModel? __5, out CardEffectAnimationScope.Scope? __state)
    {
        AttackScope? scope = Current.Value ?? Repeating.Value;
        __state = scope is not null && ReferenceEquals(scope.Card, __5)
            ? CardEffectAnimationScope.EnterAttack(scope.Attack)
            : null;
        if (__state is not null)
            __state.Speed = CardEffectAnimationScope.AnimationSpeed.Instant;
    }

    private static Exception? DamageFinalizer(Exception? __exception, CardEffectAnimationScope.Scope? __state)
    {
        CardEffectAnimationScope.Exit(__state);
        return __exception;
    }

    private static void AttackPrefix(AttackCommand __instance, out (AttackScope? Previous, AttackScope? Active) __state)
    {
        AttackScope? active = __instance.ModelSource is CardModel card
                              && LoadoutKeywords.Has(card, LoadoutKeywords.Karmic)
                              && Sts2Compatibility.MatchesAttackCardPlay(__instance, card)
            ? new AttackScope(__instance, card)
            : null;
        __state = (Current.Value, active);
        Current.Value = active;
    }

    private static void AttackPostfix(
        (AttackScope? Previous, AttackScope? Active) __state, ref Task<AttackCommand> __result)
    {
        if (__state.Active is not null)
            __result = FinishAttack(__result, __state.Active);
    }

    private static Exception? AttackFinalizer(
        Exception? __exception, (AttackScope? Previous, AttackScope? Active) __state)
    {
        Current.Value = __state.Previous;
        if (__exception is not null)
            __state.Active?.Dispose();
        return __exception;
    }

    private static async Task<AttackCommand> FinishAttack(Task<AttackCommand> original, AttackScope scope)
    {
        try { return await original; }
        finally { scope.Dispose(); }
    }

    // Run after last-priority hit hooks; -1 means unspecified to Harmony.
    [HarmonyPriority(Priority.Last - 2)]
    public static IEnumerable<CodeInstruction> AttackTranspiler(
        IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        MethodInfo damage = Sts2Compatibility.MultiTargetDamageMethod;
        MethodInfo afterDamage = AccessTools.Method(typeof(KarmicKeywordPatches), nameof(AfterDamage));
        Type[] damageParameters = damage.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
        MethodInfo customVfx = AccessTools.Method(typeof(Func<Creature, Node2D>), "Invoke");
        MethodInfo trackVfx = AccessTools.Method(typeof(KarmicKeywordPatches), nameof(TrackCustomHitVfx));
        int replaced = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Call && instruction.operand is MethodInfo method
                && method.ReturnType == damage.ReturnType
                && method.GetParameters().Select(parameter => parameter.ParameterType)
                    .Take(damageParameters.Length).SequenceEqual(damageParameters))
            {
                // Preserve native calls and wrappers installed by other attack-hit mods.
                LocalBuilder[] arguments = method.GetParameters()
                    .Select(parameter => generator.DeclareLocal(parameter.ParameterType)).ToArray();
                for (int i = arguments.Length - 1; i >= 0; i--)
                {
                    CodeInstruction store = new(OpCodes.Stloc, arguments[i]);
                    if (i == arguments.Length - 1)
                        store.MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                    yield return store;
                }
                foreach (LocalBuilder argument in arguments)
                    yield return new CodeInstruction(OpCodes.Ldloc, argument);
                yield return instruction;
                for (int i = 0; i < damageParameters.Length; i++)
                    yield return new CodeInstruction(OpCodes.Ldloc, arguments[i]);
                if (!Sts2Compatibility.UsesNewMultiTargetDamage)
                    yield return new CodeInstruction(OpCodes.Ldnull);
                yield return new CodeInstruction(OpCodes.Call, afterDamage);
                replaced++;
                continue;
            }
            yield return instruction;
            if (instruction.Calls(customVfx))
                yield return new CodeInstruction(OpCodes.Call, trackVfx);
        }
        if (replaced != 1)
            throw new InvalidOperationException($"Karmic expected one AttackCommand damage call, found {replaced}.");
    }

    private static void VfxPrefix(string path, Control? vfxContainer, out int __state)
    {
        __state = Current.Value is { CaptureFrames: true } scope && path == scope.Attack.HitVfx
                  && vfxContainer is not null
            ? vfxContainer.GetChildCount()
            : -1;
    }

    private static void VfxPostfix(Control? vfxContainer, int __state)
    {
        if (__state < 0 || vfxContainer is null || Current.Value is not { } scope)
            return;
        for (int i = __state; i < vfxContainer.GetChildCount(); i++)
            if (vfxContainer.GetChild(i) is Node2D effect)
                scope.Track(effect);
    }

    public static Node2D? TrackCustomHitVfx(Node2D? effect)
    {
        if (effect is not null && Current.Value is { CaptureFrames: true } scope)
            scope.Track(effect);
        return effect;
    }

    public static Task<IEnumerable<DamageResult>> AfterDamage(
        Task<IEnumerable<DamageResult>> original, PlayerChoiceContext choiceContext,
        IEnumerable<Creature> targets, decimal amount,
        ValueProp props, Creature? dealer, CardModel? cardSource, CardPlay? cardPlay)
    {
        if (Current.Value is not { } scope || !ReferenceEquals(scope.Card, cardSource) || TestMode.IsOn)
            return original;
        return DamageFrames(original, scope, choiceContext, targets.ToArray(), amount, props, dealer, cardPlay);
    }

    private static async Task<IEnumerable<DamageResult>> DamageFrames(
        Task<IEnumerable<DamageResult>> original, AttackScope scope,
        PlayerChoiceContext context, Creature[] targets, decimal amount,
        ValueProp props, Creature? dealer, CardPlay? cardPlay)
    {
        // Call each native damage pipeline once; only AttackCommand aggregates the results.
        Current.Value = null;
        Repeating.Value = scope;
        using FrameWindow frames = scope.TakeWindow();
        ICombatState? combat = dealer?.CombatState;
        List<DamageResult> results = new(await original);
        while (CanContinue() && await NextFrame(scope, frames))
        {
            if (!CanContinue())
                break;
            results.AddRange(await Sts2Compatibility.Damage(
                context, targets, amount, props, dealer, scope.Card, cardPlay));
        }
        return results;

        bool CanContinue() => dealer is { IsAlive: true }
                              && ReferenceEquals(dealer.CombatState, combat)
                              && !CombatManager.Instance.IsOverOrEnding
                              && targets.Any(target => target.IsAlive && ReferenceEquals(target.CombatState, combat));
    }

    private static async Task<bool> NextFrame(AttackScope scope, FrameWindow frames)
    {
        PlayerChoiceSynchronizer synchronizer = RunManager.Instance.PlayerChoiceSynchronizer;
        uint choiceId = synchronizer.ReserveChoiceId(scope.Card.Owner);
        if (scope.CaptureFrames)
        {
            bool hit = await frames.NextFrame();
            synchronizer.SyncLocalChoice(scope.Card.Owner, choiceId, PlayerChoiceResult.FromIndex(hit ? 1 : 0));
            return hit;
        }
        return (await synchronizer.WaitForRemoteChoice(scope.Card.Owner, choiceId)).AsIndex() == 1;
    }

    private sealed class AttackScope(AttackCommand attack, CardModel card) : IDisposable
    {
        public AttackCommand Attack { get; } = attack;
        public CardModel Card { get; } = card;
        public bool CaptureFrames { get; } = LocalContext.IsMe(card.Owner)
                                            && RunManager.Instance.NetService.Type != NetGameType.Replay;
        private FrameWindow? _window;

        public void Track(Node2D effect) => (_window ??= new FrameWindow()).Track(effect);

        public FrameWindow TakeWindow()
        {
            FrameWindow window = _window ?? new FrameWindow();
            _window = null;
            return window;
        }

        public void Dispose() => _window?.Dispose();
    }

    private sealed class FrameWindow : IDisposable
    {
        private readonly Dictionary<Node2D, Action> _effects = [];
        private TaskCompletionSource? _changed;
        private int _frames;
        private bool _listening;

        public void Track(Node2D effect)
        {
            if (_effects.ContainsKey(effect))
                return;
            Action exited = () => Remove(effect);
            _effects.Add(effect, exited);
            effect.TreeExiting += exited;
            if (!_listening)
            {
                RenderingServer.FramePostDraw += OnFrame;
                _listening = true;
            }
        }

        private void OnFrame()
        {
            bool visible = false;
            foreach (Node2D effect in _effects.Keys)
            {
                if (!GodotObject.IsInstanceValid(effect) || !effect.IsInsideTree() || !effect.IsVisibleInTree())
                    continue;
                visible = true;
                break;
            }
            if (!visible)
                return;
            _frames++;
            Pulse();
        }

        private void Remove(Node2D effect)
        {
            if (_effects.Remove(effect, out Action? exited))
                effect.TreeExiting -= exited;
            if (_effects.Count == 0)
            {
                StopListening();
                Pulse();
            }
        }

        public async Task<bool> NextFrame()
        {
            // The native hit covers the first frame. Never replay missed frames.
            int frame = Math.Max(1, _frames);
            while (_listening && _frames <= frame)
            {
                _changed ??= new TaskCompletionSource();
                await _changed.Task;
            }
            return _listening;
        }

        private void Pulse()
        {
            TaskCompletionSource? changed = _changed;
            _changed = null;
            changed?.TrySetResult();
        }

        private void StopListening()
        {
            if (_listening)
                RenderingServer.FramePostDraw -= OnFrame;
            _listening = false;
        }

        public void Dispose()
        {
            StopListening();
            foreach ((Node2D effect, Action exited) in _effects)
                if (GodotObject.IsInstanceValid(effect))
                    effect.TreeExiting -= exited;
            _effects.Clear();
            Pulse();
        }
    }
}
