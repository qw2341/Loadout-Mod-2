#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Loadout.Services.Morphing;
using Loadout.Services.PowerGiver;
using Loadout.Services.Targets;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

public abstract class PermanentFormStealKeywordModel : LoadoutFatalKeywordModel
{
    protected abstract bool StacksPowers { get; }

    public override bool RequiresFatalTargetSnapshots => true;

    public override async Task AfterFatalTargets(
        CardModel card,
        PlayerChoiceContext choiceContext,
        FatalKeywordContext fatalContext)
    {
        ulong playerNetId = card.Owner.NetId;
        LoadoutTargetSelection playerTarget =
            LoadoutTargetSelection.ForPlayer(playerNetId);

        foreach (FatalTargetSnapshot target in fatalContext.Targets)
        {
            if (target.MonsterId == ModelId.none)
                continue;

            Dictionary<string, int> stolenPowers = AggregatePowers(target);
            if (StacksPowers)
            {
                await ApplyPermanentPowerDeltas(
                    stolenPowers,
                    card,
                    choiceContext);
            }
            else
            {
                IReadOnlyDictionary<string, int> previousPowers =
                    BottledMonsterMorphService
                        .GetReplacementFormPowerContributions(playerNetId);
                Dictionary<string, int> replacementDeltas =
                    GetReplacementDeltas(previousPowers, stolenPowers);
                await ApplyPermanentPowerDeltas(
                    replacementDeltas,
                    card,
                    choiceContext);
                BottledMonsterMorphService
                    .SetReplacementFormPowerContributions(
                        playerNetId,
                        stolenPowers);
            }

            BottledMonsterMorphService.ApplySynchronizedMorph(
                target.MonsterId,
                playerTarget);
        }
    }

    private static Dictionary<string, int> AggregatePowers(
        FatalTargetSnapshot target)
    {
        Dictionary<string, int> powers = new(StringComparer.Ordinal);
        foreach (FatalPowerSnapshot snapshot in target.Powers)
        {
            if (snapshot.Type is not (PowerType.Buff or PowerType.Debuff)
                || snapshot.Power.Amount == 0)
                continue;

            string powerId = snapshot.Power.Id.ToString();
            powers[powerId] = SaturatingAdd(
                powers.GetValueOrDefault(powerId),
                snapshot.Power.Amount);
            if (powers[powerId] == 0)
                powers.Remove(powerId);
        }

        return powers;
    }

    private static Dictionary<string, int> GetReplacementDeltas(
        IReadOnlyDictionary<string, int> previous,
        IReadOnlyDictionary<string, int> next)
    {
        Dictionary<string, int> deltas = new(StringComparer.Ordinal);
        foreach (string powerId in previous.Keys
                     .Concat(next.Keys)
                     .Distinct(StringComparer.Ordinal))
        {
            int delta = SaturatingSubtract(
                next.GetValueOrDefault(powerId),
                previous.GetValueOrDefault(powerId));
            if (delta != 0)
                deltas[powerId] = delta;
        }

        return deltas;
    }

    private static async Task ApplyPermanentPowerDeltas(
        IReadOnlyDictionary<string, int> deltas,
        CardModel card,
        PlayerChoiceContext choiceContext)
    {
        foreach ((string powerId, int delta) in deltas
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            await PowerGiverStateService.AdjustCounterFromCardAsync(
                powerId,
                delta,
                card,
                choiceContext);
        }
    }

    private static int SaturatingAdd(int left, int right)
    {
        long result = (long)left + right;
        return result > int.MaxValue
            ? int.MaxValue
            : result < int.MinValue
                ? int.MinValue
                : (int)result;
    }

    private static int SaturatingSubtract(int left, int right)
    {
        long result = (long)left - right;
        return result > int.MaxValue
            ? int.MaxValue
            : result < int.MinValue
                ? int.MinValue
                : (int)result;
    }
}
