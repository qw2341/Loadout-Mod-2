#nullable enable

namespace Loadout.Services.Compatibility;

using System;
using System.Collections.Generic;
using BaseLib.Utils.ModInterop;
using Godot;
using MegaCrit.Sts2.Core.Models;

[ModInterop("MultiEnchantmentMod", "MultiEnchantmentMod.Api.MultiEnchantmentApi")]
public static class MultiEnchantmentApiInterop
{
    public static bool RequireApiVersion(int minimum)
    {
        return false;
    }

    public static IReadOnlyList<EnchantmentModel> GetEnchantments(
        CardModel card,
        bool includeMarkers)
    {
        return card.Enchantment is null
            ? Array.Empty<EnchantmentModel>()
            : [card.Enchantment];
    }

    public static EnchantmentModel? Enchant(
        CardModel card,
        EnchantmentModel enchantment,
        decimal amount,
        object? scopeOverride)
    {
        return null;
    }

    public static EnchantmentModel? CopyEnchantment(
        CardModel target,
        EnchantmentModel source,
        object? scopeOverride,
        bool preserveScopeProgress)
    {
        return null;
    }

    public static int RemoveEnchantmentFromAll(
        IEnumerable<CardModel> cards,
        Type enchantmentType)
    {
        return 0;
    }
}

public static class MultiEnchantmentBridge
{
    public static bool Available => MultiEnchantmentApiInterop.RequireApiVersion(2);

    public static IReadOnlyList<EnchantmentModel> GetAll(CardModel card)
    {
        if (!Available)
        {
            return card.Enchantment is null
                ? Array.Empty<EnchantmentModel>()
                : [card.Enchantment];
        }

        try
        {
            return MultiEnchantmentApiInterop.GetEnchantments(card, includeMarkers: false);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"MultiEnchantment bridge: failed reading enchantments. {exception.Message}");
            return card.Enchantment is null
                ? Array.Empty<EnchantmentModel>()
                : [card.Enchantment];
        }
    }

    public static bool Add(CardModel card, EnchantmentModel instance, int amount)
    {
        if (!Available)
            return false;

        try
        {
            return MultiEnchantmentApiInterop.Enchant(
                card,
                instance,
                Math.Max(1, amount),
                scopeOverride: null) is not null;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"MultiEnchantment bridge: failed adding '{instance.Id}'. {exception.Message}");
            return false;
        }
    }

    public static bool Copy(CardModel target, EnchantmentModel source)
    {
        if (!Available)
            return false;

        try
        {
            return MultiEnchantmentApiInterop.CopyEnchantment(
                target,
                source,
                scopeOverride: null,
                preserveScopeProgress: true) is not null;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"MultiEnchantment bridge: failed copying '{source.Id}'. {exception.Message}");
            return false;
        }
    }

    public static bool Remove(CardModel card, EnchantmentModel instance)
    {
        if (!Available)
            return false;

        try
        {
            return MultiEnchantmentApiInterop.RemoveEnchantmentFromAll(
                [card],
                instance.GetType()) > 0;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"MultiEnchantment bridge: failed removing '{instance.Id}'. {exception.Message}");
            return false;
        }
    }
}
