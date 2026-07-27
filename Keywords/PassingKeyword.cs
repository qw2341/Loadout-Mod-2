#nullable enable

namespace Loadout.Keywords;

using System.Collections.Generic;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

internal static class PassingKeyword
{
    internal static Player? GetTarget(CardModel card, Player currentResultPlayer)
    {
        Player owner = card.Owner;
        ICombatState? combatState = card.CombatState;
        if (combatState is null)
            return null;

        List<Player>? candidates = null;
        bool currentPlayerIsCandidate = false;
        foreach (Creature teammate in combatState.GetTeammatesOf(owner.Creature))
        {
            Player? player = teammate.Player;
            if (!teammate.IsAlive
                || !teammate.IsPlayer
                || player is null
                || player == owner)
            {
                continue;
            }

            (candidates ??= new List<Player>(2)).Add(player);
            currentPlayerIsCandidate |= player == currentResultPlayer;
        }

        if (currentPlayerIsCandidate)
        {
            // THE_BALL already selected this living ally through the native
            // result-location path, so preserve it without consuming RNG twice.
            return currentResultPlayer;
        }

        return candidates is { Count: > 0 }
            ? owner.RunState.Rng.CombatTargets.NextItem(candidates)
            : null;
    }
}
