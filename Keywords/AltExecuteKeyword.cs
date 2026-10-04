#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Loadout.Patches.Cards.CardModification;
using Loadout.Services.CardModification;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Debug;
using MegaCrit.Sts2.Core.Runs;

public sealed class AltExecuteKeyword : LoadoutKeywordModel
{
    private sealed record CommandState(List<string>? Commands, List<string>? UpgradeCommands);
    private static ConditionalWeakTable<CardModel, CommandState> ExplicitStates = new();

    public static AltExecuteKeyword Instance { get; } = new();
    public override CardKeyword Keyword => LoadoutKeywords.AltExecute;
    public override string StorageKey => LoadoutKeywords.AltExecuteKey;
    public override string TitleLocKey => "LOADOUT-ALT_EXECUTE.title";
    public override string CardTextLocKey => "LOADOUT-ALT_EXECUTE.cardText";
    public override LoadoutKeywordPresentation Presentation => LoadoutKeywordPresentation.DescriptionOnly;
    public override LoadoutKeywordEditorSection EditorSection => LoadoutKeywordEditorSection.Joke;
    public override LoadoutKeywordEditorControlKind EditorControlKind => LoadoutKeywordEditorControlKind.RepeatableText;
    public override bool HasOnPlayEffect => true;

    protected override void AddCardTextVariables(CardModel card, LocString cardText) =>
        cardText.AddObj("Commands", string.Join("\n", GetCommands(card)));

    public override object CaptureBeforeOnPlay(CardModel card, CardPlay cardPlay) =>
        GetCommands(card).ToArray();

    public override async Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        if (capturedState is not string[] commands)
            return;

        foreach (string command in commands)
        {
            if (!ReferenceEquals(RunManager.Instance.DebugOnlyGetState(), card.Owner.RunState))
                return;
            if (string.IsNullOrWhiteSpace(command))
                continue;

            // Card play already runs in the synchronized action queue.
            await NDevConsole.Instance.ProcessNetCommand(card.Owner, command);
        }
    }

    public static IReadOnlyList<string> GetCommands(CardModel card)
    {
        if (ExplicitStates.TryGetValue(card, out CommandState? state))
            return (card.CurrentUpgradeLevel > 0 ? state.UpgradeCommands ?? state.Commands : state.Commands) ?? [];

        List<string>? commands = null;
        List<string>? upgradeCommands = null;
        if (!CardModificationRuntime.UsesCreatedCardDefaults(card)
            && PermanentCardModificationStore.TryGetDelta(card.Id, out CardModificationDelta? permanent))
        {
            commands = permanent.ExecuteCommands;
            upgradeCommands = permanent.UpgradeModification.ExecuteCommands;
        }
        if (CardModificationFields.TryGet(card, out CardModificationCardData temporary))
        {
            commands = temporary.Delta.ExecuteCommands ?? commands;
            upgradeCommands = temporary.Delta.UpgradeModification.ExecuteCommands ?? upgradeCommands;
        }
        return (card.CurrentUpgradeLevel > 0 ? upgradeCommands ?? commands : commands) ?? [];
    }

    public static void SetExplicitState(CardModel card, CardModificationSpec state) =>
        SetExplicitState(card, state.ExecuteCommands, state.UpgradeModification.ExecuteCommands);

    public static void SetExplicitState(CardModel card, IReadOnlyList<string>? commands, IReadOnlyList<string>? upgradeCommands)
    {
        ExplicitStates.Remove(card);
        if (commands is not null || upgradeCommands is not null)
            ExplicitStates.Add(card, new CommandState(CloneCommands(commands), CloneCommands(upgradeCommands)));
    }

    public static void CopyExplicitState(CardModel source, CardModel destination)
    {
        if (ExplicitStates.TryGetValue(source, out CommandState? state))
            SetExplicitState(destination, state.Commands, state.UpgradeCommands);
    }

    public static void ClearExplicitState(CardModel card) => ExplicitStates.Remove(card);
    public static void Reset() => ExplicitStates = new();
    public static List<string>? CloneCommands(IReadOnlyList<string>? commands) => commands?.ToList();
    public static List<string>? NormalizeCommands(IReadOnlyList<string>? commands) =>
        commands?.Select(command => (command ?? string.Empty).Trim()).ToList();
    public static bool CommandsEqual(IReadOnlyList<string>? left, IReadOnlyList<string>? right) =>
        ReferenceEquals(left, right) || left is not null && right is not null && left.SequenceEqual(right, StringComparer.Ordinal);
}
