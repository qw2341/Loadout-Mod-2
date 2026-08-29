#nullable enable

namespace Loadout.Keywords;

using System.Threading.Tasks;
using Loadout.Services.PowerGiver;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

public abstract class FatalPowerStealKeywordModel : LoadoutFatalKeywordModel
{
    protected abstract bool IncludesDebuffs { get; }

    protected abstract bool AppliesPermanently { get; }

    public override bool RequiresFatalTargetSnapshots => true;

    public override async Task AfterFatalTargets(
        CardModel card,
        PlayerChoiceContext choiceContext,
        FatalKeywordContext fatalContext)
    {
        foreach (FatalTargetSnapshot target in fatalContext.Targets)
        {
            foreach (FatalPowerSnapshot snapshot in target.Powers)
            {
                if (snapshot.Type != PowerType.Buff
                    && (!IncludesDebuffs
                        || snapshot.Type != PowerType.Debuff))
                {
                    continue;
                }

                PowerModel power = snapshot.Power;
                if (power.Amount == 0)
                    continue;

                if (AppliesPermanently)
                {
                    await PowerGiverStateService.AdjustCounterFromCardAsync(
                        power.Id.ToString(),
                        power.Amount,
                        card,
                        choiceContext);
                    continue;
                }

                await PowerCmd.Apply(
                    choiceContext,
                    (PowerModel)power.ClonePreservingMutability(),
                    card.Owner.Creature,
                    power.Amount,
                    card.Owner.Creature,
                    card,
                    silent: false);
            }
        }
    }
}
