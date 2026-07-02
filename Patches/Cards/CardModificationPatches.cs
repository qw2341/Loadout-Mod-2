#nullable enable

namespace Loadout.Patches.Cards;

using HarmonyLib;
using Loadout.Services.CardModification;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

[HarmonyPatch(typeof(RunState), nameof(RunState.CreateCard), typeof(CardModel), typeof(Player))]
public static class RunStateCreateCardPatch
{
    [HarmonyPostfix]
    public static void Postfix(CardModel __result)
    {
        CardModificationStateService.ApplyPermanentToCard(__result);
    }
}

[HarmonyPatch(typeof(CombatState), nameof(CombatState.CreateCard), typeof(CardModel), typeof(Player))]
public static class CombatStateCreateCardPatch
{
    [HarmonyPostfix]
    public static void Postfix(CardModel __result)
    {
        CardModificationStateService.ApplyPermanentToCard(__result);
    }
}

[HarmonyPatch(typeof(RunState), nameof(RunState.LoadCard), typeof(SerializableCard), typeof(Player))]
public static class RunStateLoadCardPatch
{
    [HarmonyPostfix]
    public static void Postfix(CardModel __result)
    {
        CardModificationStateService.ApplyPermanentToCard(__result);
    }
}

[HarmonyPatch(typeof(Player), "PopulateDeck")]
public static class PlayerPopulateDeckCardModificationPatch
{
    [HarmonyPostfix]
    public static void Postfix(Player __instance)
    {
        CardModificationStateService.ApplySavedRunStateToPlayerDeck(__instance);
    }
}

[HarmonyPatch(typeof(NCard), nameof(NCard.Create), typeof(CardModel), typeof(ModelVisibility))]
public static class NCardCreateCardModificationPatch
{
    [HarmonyPrefix]
    public static void Prefix(ref CardModel card)
    {
        card = CardModificationStateService.CreatePermanentPreviewCard(card);
    }
}
