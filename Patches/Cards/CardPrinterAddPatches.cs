#nullable enable

namespace Loadout.Patches.Cards;

using Godot;
using HarmonyLib;
using Loadout.Services.Actions;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;

[HarmonyPatch(typeof(CardPile), nameof(CardPile.MaxCardsInHand), MethodType.Getter)]
public static class CardPrinterMaxCardsInHandPatch
{
    [HarmonyPostfix]
    public static void Postfix(ref int __result)
    {
        if (LoadoutCardAddRules.ShouldIgnoreHandLimit)
            __result = int.MaxValue;
    }
}

[HarmonyPatch(typeof(HandPosHelper), nameof(HandPosHelper.GetPosition))]
public static class CardPrinterOverflowHandPositionPatch
{
    [HarmonyPrefix]
    public static bool Prefix(int handSize, int cardIndex, ref Vector2 __result)
    {
        if (handSize <= 10)
            return true;

        int index = Mathf.Clamp(cardIndex, 0, handSize - 1);
        float midpoint = (handSize - 1) * 0.5f;
        float offset = midpoint <= 0f ? 0f : (index - midpoint) / midpoint;
        float edge = Mathf.Abs(offset);
        __result = new Vector2(offset * 640f, -50f + Mathf.Pow(edge, 1.7f) * 95f);
        return false;
    }
}

[HarmonyPatch(typeof(HandPosHelper), nameof(HandPosHelper.GetAngle))]
public static class CardPrinterOverflowHandAnglePatch
{
    [HarmonyPrefix]
    public static bool Prefix(int handSize, int cardIndex, ref float __result)
    {
        if (handSize <= 10)
            return true;

        int index = Mathf.Clamp(cardIndex, 0, handSize - 1);
        float midpoint = (handSize - 1) * 0.5f;
        float offset = midpoint <= 0f ? 0f : (index - midpoint) / midpoint;
        __result = offset * 15f;
        return false;
    }
}

[HarmonyPatch(typeof(HandPosHelper), nameof(HandPosHelper.GetScale))]
public static class CardPrinterOverflowHandScalePatch
{
    [HarmonyPrefix]
    public static bool Prefix(int handSize, ref Vector2 __result)
    {
        if (handSize <= 10)
            return true;

        __result = Vector2.One * (0.8f * Mathf.Clamp(10f / handSize, 0.45f, 0.85f));
        return false;
    }
}
