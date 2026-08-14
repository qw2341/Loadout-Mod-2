#nullable enable

namespace Loadout.Patches.ContentBans;

using HarmonyLib;
using Loadout.Services.ContentBans;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

internal sealed class ContentBanDeckPreventer : AbstractModel
{
    public override bool ShouldReceiveCombatHooks => true;

    public override Task AfterAddToDeckPrevented(CardModel card)
    {
        if (!card.HasBeenRemovedFromState)
            card.RemoveFromState();
        return Task.CompletedTask;
    }
}

[HarmonyPatch(typeof(CardCreationOptions), nameof(CardCreationOptions.GetPossibleCards))]
internal static class ContentBanCardCreationOptionsPatch
{
    [HarmonyPostfix]
    internal static void Postfix(ref IEnumerable<CardModel> __result)
    {
        if (ContentBanService.HasAnyBans(ContentBanKind.Card))
            __result = __result.Where(card => !ContentBanService.IsBanned(card));
    }
}

[HarmonyPatch(typeof(CardFactory), nameof(CardFactory.FilterForCombat))]
internal static class ContentBanCombatCardPoolPatch
{
    [HarmonyPostfix]
    internal static void Postfix(ref IEnumerable<CardModel> __result)
    {
        if (ContentBanService.HasAnyBans(ContentBanKind.Card))
            __result = __result.Where(card => !ContentBanService.IsBanned(card));
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyMerchantCardPool))]
internal static class ContentBanMerchantCardPoolPatch
{
    [HarmonyPostfix]
    internal static void Postfix(ref IEnumerable<CardModel> __result)
    {
        if (ContentBanService.HasAnyBans(ContentBanKind.Card))
            __result = __result.Where(card => !ContentBanService.IsBanned(card)).ToArray();
    }
}

[HarmonyPatch(typeof(CardFactory), nameof(CardFactory.GetDefaultTransformationOptions))]
internal static class ContentBanDefaultTransformationPoolPatch
{
    [HarmonyPostfix]
    internal static void Postfix(ref IEnumerable<CardModel> __result)
    {
        if (ContentBanService.HasAnyBans(ContentBanKind.Card))
            __result = __result.Where(card => !ContentBanService.IsBanned(card)).ToArray();
    }
}

[HarmonyPatch(typeof(CardCmd), nameof(CardCmd.Transform), typeof(IEnumerable<CardTransformation>), typeof(Rng), typeof(MegaCrit.Sts2.Core.Nodes.CommonUi.CardPreviewStyle))]
internal static class ContentBanCardTransformationPatch
{
    private static readonly AsyncLocal<int> BypassDepth = new();

    [HarmonyPrefix]
    internal static bool Prefix(
        IEnumerable<CardTransformation> transformations,
        Rng? rng,
        MegaCrit.Sts2.Core.Nodes.CommonUi.CardPreviewStyle style,
        ref Task<IEnumerable<CardPileAddResult>> __result)
    {
        if (BypassDepth.Value > 0)
            return true;
        if (!ContentBanService.HasAnyBans(ContentBanKind.Card))
            return true;

        CardTransformation[] input = transformations.ToArray();
        List<(int Index, CardTransformation Transformation)> allowed = [];
        CardPileAddResult?[] output = new CardPileAddResult?[input.Length];

        for (int index = 0; index < input.Length; index++)
        {
            CardTransformation transformation = input[index];
            CardTransformation? filtered = Filter(transformation);
            if (filtered is { } value)
                allowed.Add((index, value));
            else
                output[index] = Failure(transformation.Original);
        }

        if (allowed.Count == input.Length)
            return true;

        __result = RunAllowedAsync(allowed, output, rng, style);
        return false;
    }

    private static CardTransformation? Filter(CardTransformation transformation)
    {
        if (transformation.Replacement is { } replacement)
            return ContentBanService.IsBanned(replacement) ? null : transformation;

        IEnumerable<CardModel> candidates;
        try
        {
            candidates = transformation.ReplacementOptions
                         ?? CardFactory.GetDefaultTransformationOptions(transformation.Original, transformation.IsInCombat);
        }
        catch
        {
            return null;
        }

        CardModel[] legal = candidates.Where(card => !ContentBanService.IsBanned(card)).ToArray();
        return legal.Length == 0 ? null : new CardTransformation(transformation.Original, legal);
    }

    private static async Task<IEnumerable<CardPileAddResult>> RunAllowedAsync(
        IReadOnlyList<(int Index, CardTransformation Transformation)> allowed,
        CardPileAddResult?[] output,
        Rng? rng,
        MegaCrit.Sts2.Core.Nodes.CommonUi.CardPreviewStyle style)
    {
        if (allowed.Count > 0)
        {
            BypassDepth.Value++;
            try
            {
                CardPileAddResult[] results = (await CardCmd.Transform(allowed.Select(item => item.Transformation), rng, style)).ToArray();
                for (int i = 0; i < allowed.Count; i++)
                    output[allowed[i].Index] = i < results.Length ? results[i] : Failure(allowed[i].Transformation.Original);
            }
            finally
            {
                BypassDepth.Value--;
            }
        }

        return output.Select((result, index) => result ?? Failure(allowed.First(item => item.Index == index).Transformation.Original)).ToArray();
    }

    private static CardPileAddResult Failure(CardModel original) => new()
    {
        success = false,
        cardAdded = original,
        oldPile = original.Pile
    };
}

[HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.AddGeneratedCardsToCombat))]
internal static class ContentBanGeneratedCardPatch
{
    private static readonly AsyncLocal<int> BypassDepth = new();

    [HarmonyPrefix]
    internal static bool Prefix(
        IEnumerable<CardModel> cards,
        PileType newPileType,
        Player? creator,
        CardPilePosition position,
        ref Task<IReadOnlyList<CardPileAddResult>> __result)
    {
        if (BypassDepth.Value > 0)
            return true;
        if (!ContentBanService.HasAnyBans(ContentBanKind.Card))
            return true;

        CardModel[] input = cards.ToArray();
        if (!input.Any(ContentBanService.IsBanned))
            return true;

        __result = AddAllowedAsync(input, newPileType, creator, position);
        return false;
    }

    private static async Task<IReadOnlyList<CardPileAddResult>> AddAllowedAsync(
        IReadOnlyList<CardModel> input,
        PileType newPileType,
        Player? creator,
        CardPilePosition position)
    {
        CardPileAddResult?[] results = new CardPileAddResult?[input.Count];
        List<(int Index, CardModel Card)> allowed = [];
        for (int i = 0; i < input.Count; i++)
        {
            CardModel card = input[i];
            if (!ContentBanService.IsBanned(card))
            {
                allowed.Add((i, card));
                continue;
            }

            results[i] = Failure(card);
            if (card.Pile is null && !card.HasBeenRemovedFromState)
                card.RemoveFromState();
        }

        if (allowed.Count > 0)
        {
            BypassDepth.Value++;
            try
            {
                IReadOnlyList<CardPileAddResult> native = await CardPileCmd.AddGeneratedCardsToCombat(
                    allowed.Select(item => item.Card), newPileType, creator, position);
                for (int i = 0; i < allowed.Count; i++)
                    results[allowed[i].Index] = i < native.Count ? native[i] : Failure(allowed[i].Card);
            }
            finally
            {
                BypassDepth.Value--;
            }
        }

        return results.Select((result, index) => result ?? Failure(input[index])).ToArray();
    }

    private static CardPileAddResult Failure(CardModel card) => new()
    {
        success = false,
        cardAdded = card,
        oldPile = card.Pile
    };
}

[HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.Add), typeof(IEnumerable<CardModel>), typeof(CardPile), typeof(CardPilePosition), typeof(AbstractModel), typeof(bool), typeof(bool))]
internal static class ContentBanDirectCardAddPatch
{
    private static readonly AsyncLocal<int> BypassDepth = new();

    [HarmonyPrefix]
    internal static bool Prefix(
        IEnumerable<CardModel> cards,
        CardPile newPile,
        CardPilePosition position,
        AbstractModel? clonedBy,
        bool skipVisuals,
        bool isChangingOwners,
        ref Task<IReadOnlyList<CardPileAddResult>> __result)
    {
        if (BypassDepth.Value > 0)
            return true;
        if (!ContentBanService.HasAnyBans(ContentBanKind.Card))
            return true;

        IReadOnlyList<CardModel> input = cards as IReadOnlyList<CardModel> ?? cards.ToArray();
        bool hasBlockedCard = false;
        for (int index = 0; index < input.Count; index++)
        {
            if (!IsBlockedNewAcquisition(input[index], newPile))
                continue;
            hasBlockedCard = true;
            break;
        }
        if (!hasBlockedCard)
            return true;
        __result = AddAllowedAsync(input, newPile, position, clonedBy, skipVisuals, isChangingOwners);
        return false;
    }

    private static async Task<IReadOnlyList<CardPileAddResult>> AddAllowedAsync(
        IReadOnlyList<CardModel> input,
        CardPile newPile,
        CardPilePosition position,
        AbstractModel? clonedBy,
        bool skipVisuals,
        bool isChangingOwners)
    {
        CardPileAddResult?[] results = new CardPileAddResult?[input.Count];
        List<(int Index, CardModel Card)> allowed = [];
        for (int index = 0; index < input.Count; index++)
        {
            CardModel card = input[index];
            if (!IsBlockedNewAcquisition(card, newPile))
            {
                allowed.Add((index, card));
                continue;
            }
            results[index] = Failure(card);
            if (!card.HasBeenRemovedFromState)
                card.RemoveFromState();
        }

        if (allowed.Count > 0)
        {
            BypassDepth.Value++;
            try
            {
                IReadOnlyList<CardPileAddResult> native = await CardPileCmd.Add(
                    allowed.Select(item => item.Card), newPile, position, clonedBy, skipVisuals, isChangingOwners);
                for (int i = 0; i < allowed.Count; i++)
                    results[allowed[i].Index] = i < native.Count ? native[i] : Failure(allowed[i].Card);
            }
            finally
            {
                BypassDepth.Value--;
            }
        }

        return results.Select((result, index) => result ?? Failure(input[index])).ToArray();
    }

    private static CardPileAddResult Failure(CardModel card) => new()
    {
        success = false,
        cardAdded = card,
        oldPile = card.Pile
    };

    private static bool IsBlockedNewAcquisition(CardModel card, CardPile newPile)
    {
        if (card.FloorAddedToDeck is not null)
            return false;
        if (newPile.Type != PileType.Deck && card.Pile is not null)
            return false;
        return ContentBanService.IsBanned(card);
    }
}

[HarmonyPatch(typeof(RelicGrabBag), nameof(RelicGrabBag.PullFromFront), typeof(RelicRarity), typeof(Func<RelicModel, bool>), typeof(IRunState))]
internal static class ContentBanRelicGrabBagFrontPatch
{
    [HarmonyPrefix]
    internal static void Prefix(ref Func<RelicModel, bool> filter)
    {
        if (!ContentBanService.HasAnyBans(ContentBanKind.Relic))
            return;
        Func<RelicModel, bool> original = filter;
        filter = relic => original(relic) && !ContentBanService.IsBanned(relic);
    }
}

[HarmonyPatch(typeof(RelicGrabBag), nameof(RelicGrabBag.PullFromBack), typeof(RelicRarity), typeof(Func<RelicModel, bool>), typeof(IRunState))]
internal static class ContentBanRelicGrabBagBackPatch
{
    [HarmonyPrefix]
    internal static void Prefix(ref Func<RelicModel, bool> filter)
    {
        if (!ContentBanService.HasAnyBans(ContentBanKind.Relic))
            return;
        Func<RelicModel, bool> original = filter;
        filter = relic => original(relic) && !ContentBanService.IsBanned(relic);
    }
}

[HarmonyPatch(typeof(RelicGrabBag), nameof(RelicGrabBag.HasAvailableRelics))]
internal static class ContentBanRelicAvailabilityPatch
{
    private static readonly FieldInfo DequesField = AccessTools.Field(typeof(RelicGrabBag), "_deques");
    private static readonly FieldInfo FallbackField = AccessTools.Field(typeof(RelicGrabBag), "_mpFallbackDequeue");

    [HarmonyPostfix]
    internal static void Postfix(RelicGrabBag __instance, IRunState runState, ref bool __result)
    {
        if (!__result || !ContentBanService.HasAnyBans(ContentBanKind.Relic))
            return;
        Dictionary<RelicRarity, List<RelicModel>> deques = (Dictionary<RelicRarity, List<RelicModel>>)DequesField.GetValue(__instance)!;
        List<RelicModel> fallback = (List<RelicModel>)FallbackField.GetValue(__instance)!;
        __result = deques.Values.SelectMany(values => values).Concat(fallback)
            .Any(relic => relic.IsAllowed(runState) && !ContentBanService.IsBanned(relic));
    }
}

[HarmonyPatch(typeof(RelicCmd), nameof(RelicCmd.Obtain), typeof(RelicModel), typeof(Player), typeof(int))]
internal static class ContentBanRelicObtainPatch
{
    [HarmonyPrefix]
    internal static bool Prefix(RelicModel relic, ref Task<RelicModel> __result)
    {
        if (!ContentBanService.HasAnyBans(ContentBanKind.Relic) || !ContentBanService.IsBanned(relic))
            return true;
        __result = Task.FromResult<RelicModel>(null!);
        return false;
    }
}

[HarmonyPatch(typeof(PotionFactory), nameof(PotionFactory.GetPotionOptions))]
internal static class ContentBanPotionOptionsPatch
{
    [HarmonyPostfix]
    internal static void Postfix(ref IEnumerable<PotionModel> __result)
    {
        if (ContentBanService.HasAnyBans(ContentBanKind.Potion))
            __result = __result.Where(potion => !ContentBanService.IsBanned(potion));
    }
}

[HarmonyPatch(typeof(PotionFactory), nameof(PotionFactory.CreateRandomPotionOutOfCombat))]
internal static class ContentBanSinglePotionOutOfCombatPatch
{
    [HarmonyPrefix]
    internal static bool Prefix(Player player, Rng rng, IEnumerable<PotionModel>? blacklist, ref PotionModel __result)
        => TryCreate(player, rng, blacklist, inCombat: false, ref __result);

    internal static bool TryCreate(Player player, Rng rng, IEnumerable<PotionModel>? blacklist, bool inCombat, ref PotionModel result)
    {
        if (!ContentBanService.HasAnyBans(ContentBanKind.Potion))
            return true;
        HashSet<ModelId> blocked = (blacklist ?? Array.Empty<PotionModel>()).Select(potion => potion.Id).ToHashSet();
        PotionModel[] options = PotionFactory.GetPotionOptions(player)
            .Where(potion => !blocked.Contains(potion.Id))
            .Where(potion => !inCombat || potion.CanBeGeneratedInCombat)
            .ToArray();
        if (options.Length == 0)
        {
            result = null!;
            return false;
        }

        float roll = rng.NextFloat();
        PotionRarity rarity = roll <= 0.1f ? PotionRarity.Rare : roll <= 0.35f ? PotionRarity.Uncommon : PotionRarity.Common;
        PotionModel[] matching = options.Where(potion => potion.Rarity == rarity).ToArray();
        result = matching.Length == 0 ? null! : rng.NextItem(matching)!;
        return false;
    }
}

[HarmonyPatch(typeof(PotionFactory), nameof(PotionFactory.CreateRandomPotionInCombat))]
internal static class ContentBanSinglePotionInCombatPatch
{
    [HarmonyPrefix]
    internal static bool Prefix(Player player, Rng rng, IEnumerable<PotionModel>? blacklist, ref PotionModel __result)
        => ContentBanSinglePotionOutOfCombatPatch.TryCreate(player, rng, blacklist, inCombat: true, ref __result);
}

[HarmonyPatch(typeof(PotionFactory), nameof(PotionFactory.CreateRandomPotionsOutOfCombat))]
internal static class ContentBanPotionBatchPatch
{
    [HarmonyPrefix]
    internal static bool Prefix(Player player, int count, Rng rng, IEnumerable<PotionModel>? blacklist, ref IEnumerable<PotionModel> __result)
    {
        if (!ContentBanService.HasAnyBans(ContentBanKind.Potion))
            return true;
        HashSet<ModelId> blocked = (blacklist ?? Array.Empty<PotionModel>()).Select(potion => potion.Id).ToHashSet();
        List<PotionModel> available = PotionFactory.GetPotionOptions(player)
            .Where(potion => !blocked.Contains(potion.Id))
            .ToList();
        List<PotionModel> generated = [];
        for (int i = 0; i < count && available.Count > 0; i++)
        {
            float roll = rng.NextFloat();
            PotionRarity rarity = roll <= 0.1f ? PotionRarity.Rare : roll <= 0.35f ? PotionRarity.Uncommon : PotionRarity.Common;
            PotionModel[] matching = available.Where(potion => potion.Rarity == rarity).ToArray();
            if (matching.Length == 0)
                continue;
            PotionModel? potion = rng.NextItem(matching);
            if (potion is null)
                continue;
            generated.Add(potion);
            available.Remove(potion);
        }
        __result = generated;
        return false;
    }
}

internal static class ContentBanStartingInventoryScope
{
    private static readonly AsyncLocal<int> Depth = new();
    internal static bool IsActive => Depth.Value > 0;
    internal static void Enter() => Depth.Value++;
    internal static void Exit() => Depth.Value = Math.Max(0, Depth.Value - 1);
}

[HarmonyPatch(typeof(Player), "PopulateStartingInventory")]
internal static class ContentBanStartingInventoryPatch
{
    [HarmonyPrefix]
    internal static void Prefix(out bool __state)
    {
        __state = ContentBanService.HasAnyBans();
        if (__state)
            ContentBanStartingInventoryScope.Enter();
    }

    [HarmonyFinalizer]
    internal static Exception? Finalizer(Exception? __exception, bool __state)
    {
        if (__state)
            ContentBanStartingInventoryScope.Exit();
        return __exception;
    }
}

[HarmonyPatch(typeof(Player), "PopulateDeck")]
internal static class ContentBanStartingDeckPatch
{
    [HarmonyPrefix]
    internal static void Prefix(ref IEnumerable<CardModel> cards)
    {
        if (ContentBanStartingInventoryScope.IsActive)
            cards = cards.Where(card => !ContentBanService.IsPermanentlyBanned(ContentBanTarget.Card(card))).ToArray();
    }
}

[HarmonyPatch(typeof(Player), "PopulateRelics")]
internal static class ContentBanStartingRelicsPatch
{
    [HarmonyPrefix]
    internal static void Prefix(ref IEnumerable<RelicModel> relics)
    {
        if (ContentBanStartingInventoryScope.IsActive)
            relics = relics.Where(relic => !ContentBanService.IsPermanentlyBanned(ContentBanTarget.Relic(relic))).ToArray();
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.AddPotionInternal))]
internal static class ContentBanStartingPotionPatch
{
    [HarmonyPrefix]
    internal static bool Prefix(PotionModel potion, ref PotionProcureResult __result)
    {
        if (!ContentBanStartingInventoryScope.IsActive
            || !ContentBanService.IsPermanentlyBanned(ContentBanTarget.Potion(potion)))
            return true;

        __result = new PotionProcureResult
        {
            potion = potion,
            success = false,
            failureReason = PotionProcureFailureReason.NotAllowed
        };
        return false;
    }
}
