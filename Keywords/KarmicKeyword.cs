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
        Installed = true;
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
        using FrameWindow frames = scope.TakeWindow();
        ICombatState? combat = dealer?.CombatState;
        List<DamageResult> results = new(await original);
        int consumed = 1;
        while (CanContinue())
        {
            int batch = await NextBatch(scope, frames, consumed);
            bool finished = batch < 0;
            int count = finished ? -batch - 1 : batch;
            for (int i = 0; i < count && CanContinue(); i++)
            {
                results.AddRange(await Sts2Compatibility.Damage(
                    context, targets, amount, props, dealer, scope.Card, cardPlay));
                consumed++;
            }
            if (finished)
                break;
        }
        return results;

        bool CanContinue() => dealer is { IsAlive: true }
                              && ReferenceEquals(dealer.CombatState, combat)
                              && !CombatManager.Instance.IsOverOrEnding
                              && targets.Any(target => target.IsAlive && ReferenceEquals(target.CombatState, combat));
    }

    private static async Task<int> NextBatch(
        AttackScope scope, FrameWindow frames, int consumed)
    {
        PlayerChoiceSynchronizer synchronizer = RunManager.Instance.PlayerChoiceSynchronizer;
        uint choiceId = synchronizer.ReserveChoiceId(scope.Card.Owner);
        int batch;
        if (scope.CaptureFrames)
        {
            batch = await frames.NextBatch(consumed);
            synchronizer.SyncLocalChoice(scope.Card.Owner, choiceId, PlayerChoiceResult.FromIndex(batch));
        }
        else
        {
            batch = (await synchronizer.WaitForRemoteChoice(scope.Card.Owner, choiceId)).AsIndex();
        }
        return batch;
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
            foreach (Node2D effect in _effects.Keys)
            {
                if (!GodotObject.IsInstanceValid(effect) || !effect.IsInsideTree() || !effect.IsVisibleInTree())
                    continue;
                _frames++;
                Pulse();
                break;
            }
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

        public async Task<int> NextBatch(int consumed)
        {
            while (_effects.Count > 0 && _frames <= consumed)
            {
                _changed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await _changed.Task;
            }
            bool finished = _effects.Count == 0;
            int count = Math.Max(0, (finished ? Math.Max(1, _frames) : _frames) - consumed);
            return finished ? -count - 1 : count;
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
