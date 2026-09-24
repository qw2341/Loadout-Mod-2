#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Godot;
using Loadout.PanelItems;
using Loadout.Patches.Cards.CardModification;
using Loadout.Services.CardModification;
using Loadout.UI.Managers;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.TextEffects;

public static class LoadoutCardKeywordState
{
    private const int UnknownCardWarningLimit = 32;
    private sealed class ExplicitState
    {
        public ExplicitState(
            IReadOnlyList<LoadoutCardKeywordEntry>? baseEntries,
            IReadOnlyList<LoadoutCardKeywordEntryUpgrade>? entryUpgrades,
            IReadOnlyList<LoadoutCardKeywordEntry>? addedEntries)
        {
            BaseEntries = LoadoutCardKeywordEntry.CloneList(baseEntries);
            EntryUpgrades = LoadoutCardKeywordEntryUpgrade.CloneList(entryUpgrades);
            AddedEntries = LoadoutCardKeywordEntry.CloneList(addedEntries);
        }

        public List<LoadoutCardKeywordEntry>? BaseEntries { get; }
        public List<LoadoutCardKeywordEntryUpgrade>? EntryUpgrades { get; }
        public List<LoadoutCardKeywordEntry>? AddedEntries { get; }
    }

    private static ConditionalWeakTable<CardModel, ExplicitState> ExplicitStates = new();
    private static readonly Dictionary<string, CardModel> CardsById =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> WarnedUnknownCardIds =
        new(StringComparer.OrdinalIgnoreCase);
    private static bool _cardCacheBuilt;
    private static bool _unknownWarningLimitReported;

    public static void Reset()
    {
        ExplicitStates = new ConditionalWeakTable<CardModel, ExplicitState>();
        CardsById.Clear();
        WarnedUnknownCardIds.Clear();
        _cardCacheBuilt = false;
        _unknownWarningLimitReported = false;
    }

    public static void SetExplicitState(
        CardModel card,
        CardModificationSpec state)
    {
        SetExplicitState(
            card,
            state.CardKeywordEntries,
            state.UpgradeModification.CardKeywordEntryUpgrades,
            state.UpgradeModification.AddedCardKeywordEntries);
    }

    public static void SetExplicitState(
        CardModel card,
        IReadOnlyList<LoadoutCardKeywordEntry>? baseEntries,
        IReadOnlyList<LoadoutCardKeywordEntryUpgrade>? entryUpgrades,
        IReadOnlyList<LoadoutCardKeywordEntry>? addedEntries)
    {
        ExplicitStates.Remove(card);
        ExplicitStates.Add(card, new ExplicitState(
            baseEntries,
            entryUpgrades,
            addedEntries));
    }

    public static void CopyExplicitState(CardModel source, CardModel destination)
    {
        if (!ExplicitStates.TryGetValue(source, out ExplicitState? state))
            return;

        SetExplicitState(
            destination,
            state.BaseEntries,
            state.EntryUpgrades,
            state.AddedEntries);
        Synchronize(destination);
    }

    public static void ClearExplicitState(CardModel card)
    {
        ExplicitStates.Remove(card);
    }

    public static void Synchronize(CardModel card)
    {
        if (card.IsCanonical)
        {
            if (LoadoutKeywords.Has(card, LoadoutKeywords.AddCard)
                || LoadoutKeywords.Has(card, LoadoutKeywords.AddRandomCard))
                LoadoutKeywordRegistry.SynchronizeDynamicVars(card);
            return;
        }

        bool needsVariables = false;
        foreach (LoadoutCardKeywordModel model in
                 LoadoutCardKeywordModel.All)
        {
            bool enabled = HasEffectiveEntries(card, model.StorageKey);
            bool present = LoadoutKeywords.Has(card, model.Keyword);
            needsVariables |= enabled || present;
            if (enabled && !present)
                card.AddKeyword(model.Keyword);
            else if (!enabled && present)
                card.RemoveKeyword(model.Keyword);
        }

        if (needsVariables)
            LoadoutKeywordRegistry.SynchronizeDynamicVars(card);
    }

    public static IEnumerable<LoadoutCardKeywordEntry> GetEffectiveEntries(
        CardModel card,
        string keywordKey)
    {
        ResolveLists(
            card,
            out IReadOnlyList<LoadoutCardKeywordEntry>? baseEntries,
            out IReadOnlyList<LoadoutCardKeywordEntryUpgrade>? entryUpgrades,
            out IReadOnlyList<LoadoutCardKeywordEntry>? addedEntries);
        InfiniteUpgradeScalingMode scalingMode =
            InfiniteUpgradeValueScaling.Resolve(card);

        foreach (LoadoutCardKeywordEntry entry in GetEffectiveEntries(
                     baseEntries,
                     entryUpgrades,
                     addedEntries,
                     card.CurrentUpgradeLevel,
                     scalingMode))
        {
            if (MatchesKeyword(entry, keywordKey))
                yield return entry;
        }
    }

    public static IEnumerable<LoadoutCardKeywordEntry> GetEffectiveEntries(
        IReadOnlyList<LoadoutCardKeywordEntry>? baseEntries,
        IReadOnlyList<LoadoutCardKeywordEntryUpgrade>? entryUpgrades,
        IReadOnlyList<LoadoutCardKeywordEntry>? addedEntries,
        int upgradeLevel)
    {
        return GetEffectiveEntries(
            baseEntries,
            entryUpgrades,
            addedEntries,
            upgradeLevel,
            InfiniteUpgradeScalingMode.None);
    }

    public static IEnumerable<LoadoutCardKeywordEntry> GetEffectiveEntries(
        IReadOnlyList<LoadoutCardKeywordEntry>? baseEntries,
        IReadOnlyList<LoadoutCardKeywordEntryUpgrade>? entryUpgrades,
        IReadOnlyList<LoadoutCardKeywordEntry>? addedEntries,
        int upgradeLevel,
        bool useInfiniteUpgradeValues)
    {
        return GetEffectiveEntries(
            baseEntries,
            entryUpgrades,
            addedEntries,
            upgradeLevel,
            useInfiniteUpgradeValues
                ? InfiniteUpgradeScalingMode.Incremental
                : InfiniteUpgradeScalingMode.None);
    }

    public static IEnumerable<LoadoutCardKeywordEntry> GetEffectiveEntries(
        IReadOnlyList<LoadoutCardKeywordEntry>? baseEntries,
        IReadOnlyList<LoadoutCardKeywordEntryUpgrade>? entryUpgrades,
        IReadOnlyList<LoadoutCardKeywordEntry>? addedEntries,
        int upgradeLevel,
        InfiniteUpgradeScalingMode scalingMode)
    {
        foreach (EffectiveCardKeywordEntry effective in GetEffectiveEntryStates(
                     baseEntries,
                     entryUpgrades,
                     addedEntries,
                     upgradeLevel,
                     scalingMode))
        {
            yield return effective.Entry;
        }
    }

    private static IEnumerable<EffectiveCardKeywordEntry> GetEffectiveEntryStates(
        IReadOnlyList<LoadoutCardKeywordEntry>? baseEntries,
        IReadOnlyList<LoadoutCardKeywordEntryUpgrade>? entryUpgrades,
        IReadOnlyList<LoadoutCardKeywordEntry>? addedEntries,
        int upgradeLevel,
        InfiniteUpgradeScalingMode scalingMode)
    {
        int level = Math.Max(0, upgradeLevel);
        long cumulativeUpgradeBonus = scalingMode
                                      == InfiniteUpgradeScalingMode.Incremental
            ? InfiniteUpgradeValueScaling.GetCumulativeUpgradeBonus(level)
            : 0L;
        Dictionary<EntryIdentity, LoadoutCardKeywordEntryUpgrade>? upgradesByIdentity =
            level > 0 && entryUpgrades is not null
                ? entryUpgrades
                    .Where(entry => entry.HasIdentity)
                    .GroupBy(CreateIdentity)
                    .ToDictionary(group => group.Key, group => group.Last())
                : null;
        Dictionary<(string KeywordKey, string CardId), int> occurrences =
            new(KeywordCardIdentityComparer.Instance);

        if (baseEntries is not null)
        {
            foreach (LoadoutCardKeywordEntry baseEntry in baseEntries)
            {
                LoadoutCardKeywordEntry effective = baseEntry.Clone();
                bool amountWasUpgraded = false;
                int occurrence = GetAndIncrementOccurrence(occurrences, baseEntry);
                EntryIdentity identity = new(
                    baseEntry.KeywordKey,
                    baseEntry.CardId,
                    occurrence);
                if (level > 0
                    && upgradesByIdentity?.TryGetValue(
                        identity,
                        out LoadoutCardKeywordEntryUpgrade? upgrade) == true)
                {
                    if (upgrade.AmountDelta != 0
                        && scalingMode == InfiniteUpgradeScalingMode.Double)
                    {
                        effective.Amount = DoubleAmount(
                            baseEntry.Amount,
                            level);
                    }
                    else
                    {
                        long effectiveAmount = (long)baseEntry.Amount
                                               + (long)upgrade.AmountDelta * level;
                        if (upgrade.AmountDelta != 0)
                            effectiveAmount += cumulativeUpgradeBonus;
                        effective.Amount = SaturatingAmount(effectiveAmount);
                    }
                    amountWasUpgraded = upgrade.AmountDelta != 0;
                    if (!string.IsNullOrWhiteSpace(upgrade.ReplacementCardId))
                        effective.CardId = upgrade.ReplacementCardId;
                    if (upgrade.ReplacementUpgraded.HasValue)
                        effective.Upgraded = upgrade.ReplacementUpgraded.Value;
                    if (upgrade.ReplacementPile.HasValue)
                        effective.Pile = upgrade.ReplacementPile.Value;
                    if (upgrade.ReplacementPoolId is not null)
                        effective.PoolId = upgrade.ReplacementPoolId;
                    if (upgrade.ReplacementRarity.HasValue)
                        effective.Rarity = upgrade.ReplacementRarity.Value;
                    if (upgrade.ReplacementFreeToPlay.HasValue)
                        effective.FreeToPlay = upgrade.ReplacementFreeToPlay.Value;
                    effective.Amount = Math.Max(0, effective.Amount);
                }

                yield return new EffectiveCardKeywordEntry(
                    effective,
                    amountWasUpgraded);
            }
        }

        if (level == 0 || addedEntries is null)
            yield break;

        foreach (LoadoutCardKeywordEntry addedEntry in addedEntries)
        {
            LoadoutCardKeywordEntry effective = addedEntry.Clone();
            effective.Amount = scalingMode == InfiniteUpgradeScalingMode.Double
                ? DoubleAmount(addedEntry.Amount, level - 1)
                : SaturatingAmount(
                    (long)addedEntry.Amount * level
                    + cumulativeUpgradeBonus);
            effective.Amount = Math.Max(0, effective.Amount);
            yield return new EffectiveCardKeywordEntry(
                effective,
                AmountWasUpgraded: true);
        }
    }

    public static bool HasEffectiveEntries(CardModel card, string keywordKey)
    {
        ResolveLists(
            card,
            out IReadOnlyList<LoadoutCardKeywordEntry>? baseEntries,
            out _,
            out IReadOnlyList<LoadoutCardKeywordEntry>? addedEntries);
        return baseEntries?.Any(entry => MatchesKeyword(entry, keywordKey)) == true
               || card.CurrentUpgradeLevel > 0
               && addedEntries?.Any(entry => MatchesKeyword(entry, keywordKey)) == true;
    }

    public static bool HasConfiguredEntries(
        IReadOnlyList<LoadoutCardKeywordEntry>? entries) =>
        entries?.Any(entry => !entry.IsEmpty) == true;

    public static List<LoadoutCardKeywordEntryUpgrade>? PruneEntryUpgrades(
        IReadOnlyList<LoadoutCardKeywordEntry>? baseEntries,
        IReadOnlyList<LoadoutCardKeywordEntryUpgrade>? entryUpgrades)
    {
        if (entryUpgrades is null)
            return null;

        HashSet<EntryIdentity> validIdentities = [];
        Dictionary<(string KeywordKey, string CardId), int> occurrences =
            new(KeywordCardIdentityComparer.Instance);
        if (baseEntries is not null)
        {
            foreach (LoadoutCardKeywordEntry entry in baseEntries)
            {
                validIdentities.Add(new EntryIdentity(
                    entry.KeywordKey,
                    entry.CardId,
                    GetAndIncrementOccurrence(occurrences, entry)));
            }
        }

        return entryUpgrades
            .Where(entry => entry.HasIdentity
                            && validIdentities.Contains(CreateIdentity(entry)))
            .Select(entry => entry.Clone())
            .ToList();
    }

    public static bool TryResolveKeywordModel(
        string? keywordKey,
        out LoadoutCardKeywordModel model)
    {
        if (LoadoutKeywords.TryResolve(keywordKey, out CardKeyword keyword)
            && LoadoutKeywordRegistry.TryGet(
                keyword,
                out LoadoutKeywordModel resolved)
            && resolved is LoadoutCardKeywordModel cardModel)
        {
            model = cardModel;
            return true;
        }

        model = null!;
        return false;
    }

    public static bool TryResolveCard(string? cardId, out CardModel card)
    {
        EnsureCardCache();
        if (!string.IsNullOrWhiteSpace(cardId)
            && CardsById.TryGetValue(cardId.Trim(), out CardModel? resolved))
        {
            card = resolved;
            return true;
        }

        card = null!;
        return false;
    }

    public static string GetDefaultCardId() => ModelDb.Card<MegaCrit.Sts2.Core.Models.Cards.Shiv>().Id.ToString();

    public static bool IsSupportedPile(PileType pile) =>
        pile is PileType.Hand or PileType.Draw or PileType.Discard or PileType.Exhaust or PileType.Deck;

    public static string FormatEntries(CardModel card, string keywordKey)
    {
        ResolveLists(card, out var baseEntries, out var upgrades, out var addedEntries);
        var entries = GetEffectiveEntryStates(baseEntries, upgrades, addedEntries,
            card.CurrentUpgradeLevel, InfiniteUpgradeValueScaling.Resolve(card))
            .Where(effective => MatchesKeyword(effective.Entry, keywordKey));
        string separator = LocMan.Loc("CARD_MOD_CARD_KEYWORD_SEPARATOR", ", ");
        return string.Join(" ", entries.GroupBy(effective =>
            (effective.Entry.Pile, FreeToPlay: effective.Entry.EffectiveFreeToPlay)).Select(group =>
        {
            string sentence = LocMan.Loc("CARD_MOD_CARD_KEYWORD_SENTENCE", "Add {0} into your [gold]{1}[/gold].",
                string.Join(separator, group.Select(effective => FormatEntry(effective, card.UpgradePreviewType.IsPreview()))),
                GetPileLabel(group.Key.Pile));
            string costText = GetFreeToPlayDescription(group.Key.FreeToPlay, group.Sum(effective => (long)effective.Entry.Amount) == 1);
            return string.IsNullOrEmpty(costText) ? sentence : sentence + " " + costText;
        }));
    }

    public static string GetFreeToPlayDescription(LoadoutGeneratedCardCost cost, bool singular) => cost switch
    {
        LoadoutGeneratedCardCost.FreeThisTurn => singular
            ? LocMan.Loc("CARD_MOD_CARD_FREE_TURN_SINGLE", "It's free to play this turn.")
            : LocMan.Loc("CARD_MOD_CARD_FREE_TURN_PLURAL", "They are free to play this turn."),
        LoadoutGeneratedCardCost.FreeThisCombat => singular
            ? LocMan.Loc("CARD_MOD_CARD_FREE_COMBAT_SINGLE", "It's free to play this combat.")
            : LocMan.Loc("CARD_MOD_CARD_FREE_COMBAT_PLURAL", "They are free to play this combat."),
        _ => string.Empty
    };

    public static void ApplyGeneratedCardCost(CardModel generated, LoadoutCardKeywordEntry entry)
    {
        switch (entry.EffectiveFreeToPlay)
        {
            case LoadoutGeneratedCardCost.FreeThisTurn:
                generated.SetToFreeThisTurn();
                break;
            case LoadoutGeneratedCardCost.FreeThisCombat:
                generated.SetToFreeThisCombat();
                break;
        }
    }

    public static string GetPileLabel(PileType pile) => pile switch
    {
        PileType.Draw => LocMan.Loc("CARD_MOD_CARD_PILE_DRAW", "Draw Pile"),
        PileType.Discard => LocMan.Loc("CARD_MOD_CARD_PILE_DISCARD", "Discard Pile"),
        PileType.Exhaust => LocMan.Loc("CARD_MOD_CARD_PILE_EXHAUST", "Exhaust Pile"),
        PileType.Deck => LocMan.Loc("CARD_MOD_CARD_PILE_DECK", "Deck"),
        _ => LocMan.Loc("CARD_MOD_CARD_PILE_HAND", "Hand")
    };

    public static string GetCardTitle(string cardId, bool upgraded)
    {
        string title = TryResolveCard(cardId, out CardModel card)
            ? CardPrinter.FormatCardTitle(card)
            : GetCardIdFallback(cardId);
        return upgraded ? title + "+" : title;
    }

    private static string FormatEntry(EffectiveCardKeywordEntry effective, bool highlight)
    {
        var entry = effective.Entry;
        string amount = entry.Amount.ToString(CultureInfo.InvariantCulture);
        if (highlight && effective.AmountWasUpgraded)
            amount = StsTextUtilities.HighlightChangeText(amount, 1);
        if (entry.IsRandom)
            return AddRandomCardKeyword.FormatEntry(entry, amount);
        return LocMan.Loc("CARD_MOD_CARD_KEYWORD_ENTRY", "{0} {1}", amount,
            $"[gold]{GetCardTitle(entry.CardId, entry.Upgraded)}[/gold]");
    }

    public static int SaturatingMultiply(int amount, int multiplier)
    {
        long product = (long)amount * multiplier;
        return SaturatingAmount(product);
    }

    public static void WarnUnknownCard(string cardId)
    {
        if (WarnedUnknownCardIds.Contains(cardId))
            return;
        if (WarnedUnknownCardIds.Count >= UnknownCardWarningLimit)
        {
            if (!_unknownWarningLimitReported)
            {
                _unknownWarningLimitReported = true;
                GD.PushWarning(
                    "Loadout card keyword: additional unknown card warnings were suppressed.");
            }
            return;
        }

        if (WarnedUnknownCardIds.Add(cardId))
            GD.PushWarning($"Loadout card keyword: unknown card '{cardId}' was skipped.");
    }

    private static void ResolveLists(
        CardModel card,
        out IReadOnlyList<LoadoutCardKeywordEntry>? baseEntries,
        out IReadOnlyList<LoadoutCardKeywordEntryUpgrade>? entryUpgrades,
        out IReadOnlyList<LoadoutCardKeywordEntry>? addedEntries)
    {
        if (ExplicitStates.TryGetValue(card, out ExplicitState? explicitState))
        {
            baseEntries = explicitState.BaseEntries;
            entryUpgrades = explicitState.EntryUpgrades;
            addedEntries = explicitState.AddedEntries;
            return;
        }

        baseEntries = null;
        entryUpgrades = null;
        addedEntries = null;
        if (PermanentCardModificationStore.TryGetDelta(
                card.Id,
                out CardModificationDelta? permanent))
        {
            baseEntries = permanent.CardKeywordEntries;
            entryUpgrades = permanent.UpgradeModification.CardKeywordEntryUpgrades;
            addedEntries = permanent.UpgradeModification.AddedCardKeywordEntries;
        }

        if (!CardModificationFields.TryGet(card, out CardModificationCardData temporary))
            return;

        if (temporary.Delta.CardKeywordEntries is not null)
            baseEntries = temporary.Delta.CardKeywordEntries;
        if (temporary.Delta.UpgradeModification.CardKeywordEntryUpgrades is not null)
        {
            entryUpgrades = temporary.Delta.UpgradeModification.CardKeywordEntryUpgrades;
        }
        if (temporary.Delta.UpgradeModification.AddedCardKeywordEntries is not null)
        {
            addedEntries = temporary.Delta.UpgradeModification.AddedCardKeywordEntries;
        }
    }

    private static EntryIdentity CreateIdentity(
        LoadoutCardKeywordEntryUpgrade entry) =>
        new(entry.KeywordKey, entry.OriginalCardId, entry.OccurrenceIndex);

    private static int GetAndIncrementOccurrence(
        Dictionary<(string KeywordKey, string CardId), int> occurrences,
        LoadoutCardKeywordEntry entry)
    {
        (string KeywordKey, string CardId) key = (entry.KeywordKey, entry.CardId);
        occurrences.TryGetValue(key, out int occurrence);
        occurrences[key] = occurrence + 1;
        return occurrence;
    }

    private static int DoubleAmount(int amount, int doublings)
    {
        if (amount == 0 || doublings <= 0)
            return amount;

        long result = amount;
        for (int i = 0; i < doublings; i++)
        {
            result *= 2L;
            if (result > int.MaxValue)
                return int.MaxValue;
            if (result < int.MinValue)
                return int.MinValue;
        }

        return (int)result;
    }

    private static int SaturatingAmount(long amount) =>
        amount > int.MaxValue
            ? int.MaxValue
            : amount < int.MinValue
                ? int.MinValue
                : (int)amount;

    private readonly record struct EntryIdentity(
        string KeywordKey,
        string OriginalCardId,
        int OccurrenceIndex)
    {
        public bool Equals(EntryIdentity other) =>
            OccurrenceIndex == other.OccurrenceIndex
            && string.Equals(KeywordKey, other.KeywordKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(OriginalCardId, other.OriginalCardId, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode() => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(KeywordKey),
            StringComparer.OrdinalIgnoreCase.GetHashCode(OriginalCardId),
            OccurrenceIndex);
    }

    private readonly record struct EffectiveCardKeywordEntry(
        LoadoutCardKeywordEntry Entry,
        bool AmountWasUpgraded);

    private sealed class KeywordCardIdentityComparer
        : IEqualityComparer<(string KeywordKey, string CardId)>
    {
        public static readonly KeywordCardIdentityComparer Instance = new();

        public bool Equals(
            (string KeywordKey, string CardId) x,
            (string KeywordKey, string CardId) y) =>
            string.Equals(x.KeywordKey, y.KeywordKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.CardId, y.CardId, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string KeywordKey, string CardId) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.KeywordKey),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.CardId));
    }

    private static bool MatchesKeyword(
        LoadoutCardKeywordEntry entry,
        string keywordKey) =>
        string.Equals(
            entry.KeywordKey,
            keywordKey,
            StringComparison.OrdinalIgnoreCase);

    private static string GetCardIdFallback(string cardId)
    {
        string trimmed = cardId.Trim();
        int separator = Math.Max(trimmed.LastIndexOf(':'), trimmed.LastIndexOf('/'));
        return separator >= 0 && separator + 1 < trimmed.Length
            ? trimmed[(separator + 1)..]
            : trimmed;
    }

    private static void EnsureCardCache()
    {
        if (_cardCacheBuilt)
            return;

        CardsById.Clear();
        foreach (CardModel card in ModelDb.AllCards)
        {
            CardsById[card.Id.ToString()] = card;
            CardsById.TryAdd(card.Id.Entry, card);
        }
        _cardCacheBuilt = true;
    }
}
