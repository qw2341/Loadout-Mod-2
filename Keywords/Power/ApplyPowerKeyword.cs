#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using System.Threading.Tasks;
using BaseLib.Cards.Variables;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using Loadout.Services.CardModification;

public sealed class ApplyPowerKeyword : LoadoutPowerKeywordModel
{
    public const string DisplayVarName = "LoadoutApplyPowerList";

    private static readonly IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        VariableDefinitions =
        [
            new(
                DisplayVarName,
                0m,
                int.MinValue,
                int.MaxValue,
                "DYNAMIC_VAR_LOADOUT_APPLY_POWER_LIST",
                (name, _) => new DisplayVar<CardModel>(
                    name,
                    card => LoadoutPowerKeywordState.FormatEntries(
                        card,
                        LoadoutKeywords.ApplyPowerKey)),
                EditorVisible: false)
        ];

    public static ApplyPowerKeyword Instance { get; } = new();

    private ApplyPowerKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.ApplyPower;

    public override string StorageKey => LoadoutKeywords.ApplyPowerKey;

    public override string TitleLocKey => "LOADOUT-APPLY_POWER.title";

    public override string? CardTextLocKey => "LOADOUT-APPLY_POWER.cardText";

    public override IReadOnlyList<LoadoutKeywordDynamicVarDefinition> DynamicVars =>
        VariableDefinitions;

    public override bool HasOnPlayEffect => true;

    public override bool ChangesTargeting => true;

    public override async Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        if (cardPlay.Target is not { IsDead: false } target)
            return;

        foreach (LoadoutPowerKeywordEntry entry in
                 LoadoutPowerKeywordState.GetEffectiveEntries(card, StorageKey))
        {
            if (!LoadoutPowerKeywordState.TryResolvePower(
                    entry.PowerId,
                    out PowerModel canonical))
            {
                LoadoutPowerKeywordState.WarnUnknownPower(entry.PowerId);
                continue;
            }

            await PowerCmd.Apply(
                choiceContext,
                canonical.ToMutable(),
                target,
                entry.Amount,
                card.Owner.Creature,
                card,
                silent: false);
        }
    }
}
