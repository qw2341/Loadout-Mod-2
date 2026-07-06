#nullable enable

namespace Loadout.Patches.Cards;

using System.Reflection;
using Godot;
using HarmonyLib;
using Loadout.Services.Actions;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;

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

[HarmonyPatch(typeof(NPlayerHand), "StartCardPlay")]
public static class CardPrinterOverflowHandShortcutPatch
{
    private static readonly FieldInfo? SelectCardShortcutsField = AccessTools.Field(typeof(NPlayerHand), "_selectCardShortcuts");
    private static readonly StringName OverflowNoShortcut = new("loadout_overflow_card_no_shortcut");

    [HarmonyPrefix]
    public static void Prefix(NPlayerHand __instance, NHandCardHolder holder)
    {
        int index = holder.GetIndex();
        if (index < 10 || SelectCardShortcutsField?.GetValue(__instance) is not StringName[] current)
            return;

        int requiredLength = index + 1;
        if (current.Length >= requiredLength)
            return;

        StringName[] expanded = new StringName[requiredLength];
        for (int i = 0; i < current.Length; i++)
            expanded[i] = current[i];

        for (int i = current.Length; i < expanded.Length; i++)
            expanded[i] = OverflowNoShortcut;

        SelectCardShortcutsField.SetValue(__instance, expanded);
    }
}
