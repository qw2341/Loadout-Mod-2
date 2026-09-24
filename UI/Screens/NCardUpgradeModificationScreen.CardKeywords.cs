#nullable enable

namespace Loadout.UI.Screens;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Loadout.Keywords;
using Loadout.PanelItems;
using Loadout.Patches.Cards.CardModification;
using Loadout.Services.CardModification;
using Loadout.Services.Targets;
using Loadout.UI.Managers;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Runs;

public partial class NCardUpgradeModificationScreen
{
    private int GetCardKeywordEntryCount(CardKeyword keyword)
    {
        string key = LoadoutKeywords.GetStorageKey(keyword);
        return _draft.AddedCardKeywordEntries?.Count(entry => string.Equals(
            entry.KeywordKey,
            key,
            StringComparison.OrdinalIgnoreCase)) ?? 0;
    }

    private void AddCardKeywordEntry(CardKeyword keyword)
    {
        if (!LoadoutKeywordRegistry.TryGet(keyword, out LoadoutKeywordModel model)
            || model is not LoadoutCardKeywordModel cardModel)
            return;

        List<LoadoutCardKeywordEntry> entries =
            LoadoutCardKeywordEntry.CloneList(
                _draft.AddedCardKeywordEntries) ?? [];
        entries.Add(cardModel.CreateDefaultEntry());
        _draft.AddedCardKeywordEntries = entries;
        QueueRebuild();
    }

    private void RemoveCardKeywordEntry(CardKeyword keyword)
    {
        string key = LoadoutKeywords.GetStorageKey(keyword);
        List<LoadoutCardKeywordEntry> entries =
            LoadoutCardKeywordEntry.CloneList(
                _draft.AddedCardKeywordEntries) ?? [];
        int index = entries.FindLastIndex(entry => string.Equals(
            entry.KeywordKey,
            key,
            StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return;
        entries.RemoveAt(index);
        _draft.AddedCardKeywordEntries = entries;
        QueueRebuild();
    }

    private void AddCardKeywordVariableControls()
    {
        if (_leftControls is null)
            return;

        List<LoadoutCardKeywordEntry> baseEntries =
            LoadoutCardKeywordEntry.CloneList(
                _baseState.CardKeywordEntries) ?? [];
        List<LoadoutCardKeywordEntry> addedEntries =
            LoadoutCardKeywordEntry.CloneList(
                _draft.AddedCardKeywordEntries) ?? [];
        Dictionary<string, int> totals = baseEntries.Concat(addedEntries)
            .GroupBy(entry => entry.KeywordKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Count(),
                StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> numbers =
            new(StringComparer.OrdinalIgnoreCase);
        Dictionary<(string KeywordKey, string CardId), int> occurrences =
            new(KeywordCardIdentityComparer.Instance);

        if (baseEntries.Count > 0)
        {
            _leftControls.AddChild(CreateSectionLabel(
                LocMan.Loc(
                    "CARD_MOD_CARD_KEYWORD_ENTRY_UPGRADES",
                    "Card Entry Upgrades")));
        }

        for (int index = 0; index < baseEntries.Count; index++)
        {
            if (!LoadoutCardKeywordState.TryResolveKeywordModel(
                    baseEntries[index].KeywordKey,
                    out LoadoutCardKeywordModel model))
                continue;

            LoadoutCardKeywordEntry baseEntry = baseEntries[index];
            int occurrence = GetAndIncrementCardOccurrence(occurrences, baseEntry);
            LoadoutCardKeywordEntryUpgrade? configured = FindCardEntryUpgrade(
                model.StorageKey,
                baseEntry.CardId,
                occurrence);
            int number = numbers.GetValueOrDefault(model.StorageKey) + 1;
            numbers[model.StorageKey] = number;
            string suffix = totals.GetValueOrDefault(model.StorageKey) > 1
                ? $" {number}"
                : string.Empty;
            if (baseEntry.IsRandom)
            {
                NLoadoutCardFilterStepper pool = new();
                pool.InitPool(configured?.ReplacementPoolId ?? baseEntry.PoolId, value =>
                {
                    UpdateCardEntryUpgrade(model.StorageKey, baseEntry.CardId, occurrence,
                        entry => entry.ReplacementPoolId = value == baseEntry.PoolId ? null : value);
                    RefreshPreview();
                });
                _leftControls.AddChild(CreateRow(
                    LocMan.Loc("CARD_MOD_RANDOM_CARD_POOL", "Card Pool") + suffix, pool));
                NLoadoutCardFilterStepper rarity = new();
                rarity.InitRarity(configured?.ReplacementRarity ?? baseEntry.Rarity, value =>
                {
                    UpdateCardEntryUpgrade(model.StorageKey, baseEntry.CardId, occurrence,
                        entry => entry.ReplacementRarity = value == baseEntry.Rarity ? null : value);
                    RefreshPreview();
                });
                _leftControls.AddChild(CreateRow(
                    LocMan.Loc("FILTER_GROUP_RARITY", "Rarity") + suffix, rarity));
                NLoadoutCardFilterStepper upgraded = new();
                upgraded.InitUpgraded(configured?.ReplacementUpgraded ?? baseEntry.Upgraded, value =>
                {
                    UpdateCardEntryUpgrade(model.StorageKey, baseEntry.CardId, occurrence,
                        entry => entry.ReplacementUpgraded = value == baseEntry.Upgraded ? null : value);
                    RefreshPreview();
                });
                _leftControls.AddChild(CreateRow(
                    LocMan.Loc("CARD_MOD_RANDOM_CARD_UPGRADED", "Upgraded") + suffix, upgraded));
            }
            else
            {
                NLoadoutCardSelector selector = new();
                selector.Init(configured?.ReplacementCardId ?? baseEntry.CardId,
                    configured?.ReplacementUpgraded ?? baseEntry.Upgraded);
                selector.SelectRequested += () =>
                {
                    if (!CardPrinter.TryOpenKeywordCardPicker((selected, upgraded) =>
                        {
                            if (!GodotObject.IsInstanceValid(this) || !IsInsideTree())
                                return;
                            SetCardEntryUpgradeReplacement(
                                model.StorageKey,
                                baseEntry.CardId,
                                occurrence,
                                selected.Id.ToString());
                            UpdateCardEntryUpgrade(model.StorageKey, baseEntry.CardId, occurrence,
                                entry => entry.ReplacementUpgraded = upgraded == baseEntry.Upgraded ? null : upgraded);
                            QueueRebuild();
                        },
                        out string error))
                    {
                        GD.PushWarning(error);
                    }
                };
                _leftControls.AddChild(CreateRow(
                    LocMan.Loc(
                        "CARD_MOD_CARD_KEYWORD_REPLACEMENT",
                        "Replacement Card") + suffix,
                    selector));
            }

            AddStepperRow(
                _leftControls,
                LocMan.Loc(
                    "CARD_MOD_CARD_KEYWORD_UPGRADE_AMOUNT",
                    "Upgrade Amount") + suffix,
                configured?.AmountDelta ?? 0,
                int.MinValue,
                int.MaxValue,
                amount =>
                {
                    SetCardEntryUpgradeAmount(
                        model.StorageKey,
                        baseEntry.CardId,
                        occurrence,
                        amount);
                    RefreshPreview();
                });
            NLoadoutPileTypeStepper pile = new();
            pile.Init(configured?.ReplacementPile ?? baseEntry.Pile, value =>
            {
                UpdateCardEntryUpgrade(model.StorageKey, baseEntry.CardId, occurrence,
                    entry => entry.ReplacementPile = value == baseEntry.Pile ? null : value);
                RefreshPreview();
            });
            _leftControls.AddChild(CreateRow(LocMan.Loc("CARD_MOD_ADD_CARD_PILE", "Destination Pile") + suffix, pile));
            NLoadoutCardCostStepper cost = new();
            cost.Init(configured?.ReplacementFreeToPlay ?? baseEntry.FreeToPlay, value =>
            {
                UpdateCardEntryUpgrade(model.StorageKey, baseEntry.CardId, occurrence,
                    entry => entry.ReplacementFreeToPlay = value == baseEntry.FreeToPlay ? null : value);
                RefreshPreview();
            });
            _leftControls.AddChild(CreateRow(LocMan.Loc("CARD_MOD_CARD_FREE", "Free to Play") + suffix, cost));
        }

        if (addedEntries.Count > 0)
        {
            _leftControls.AddChild(CreateSectionLabel(
                LocMan.Loc(
                    "CARD_MOD_CARD_KEYWORD_ADDED_ENTRIES",
                    "Added on Upgrade")));
        }

        for (int index = 0; index < addedEntries.Count; index++)
        {
            if (!LoadoutCardKeywordState.TryResolveKeywordModel(
                    addedEntries[index].KeywordKey,
                    out LoadoutCardKeywordModel model))
                continue;

            int number = numbers.GetValueOrDefault(model.StorageKey) + 1;
            numbers[model.StorageKey] = number;
            int capturedIndex = index;
            string suffix = totals.GetValueOrDefault(model.StorageKey) > 1
                ? $" {number}"
                : string.Empty;
            if (addedEntries[index].IsRandom)
            {
                NLoadoutCardFilterStepper pool = new();
                pool.InitPool(addedEntries[index].PoolId, value =>
                {
                    UpdateAddedCardKeywordEntry(capturedIndex, entry => entry.PoolId = value);
                    RefreshPreview();
                });
                _leftControls.AddChild(CreateRow(
                    LocMan.Loc("CARD_MOD_RANDOM_CARD_POOL", "Card Pool") + suffix, pool));
                NLoadoutCardFilterStepper rarity = new();
                rarity.InitRarity(addedEntries[index].Rarity, value =>
                {
                    UpdateAddedCardKeywordEntry(capturedIndex, entry => entry.Rarity = value);
                    RefreshPreview();
                });
                _leftControls.AddChild(CreateRow(
                    LocMan.Loc("FILTER_GROUP_RARITY", "Rarity") + suffix, rarity));
                NLoadoutCardFilterStepper upgraded = new();
                upgraded.InitUpgraded(addedEntries[index].Upgraded, value =>
                {
                    UpdateAddedCardKeywordEntry(capturedIndex, entry => entry.Upgraded = value);
                    RefreshPreview();
                });
                _leftControls.AddChild(CreateRow(
                    LocMan.Loc("CARD_MOD_RANDOM_CARD_UPGRADED", "Upgraded") + suffix, upgraded));
            }
            else
            {
                NLoadoutCardSelector selector = new();
                selector.Init(addedEntries[index].CardId, addedEntries[index].Upgraded);
                selector.SelectRequested += () =>
                {
                    if (!CardPrinter.TryOpenKeywordCardPicker((selected, upgraded) =>
                        {
                            if (!GodotObject.IsInstanceValid(this) || !IsInsideTree())
                                return;
                            UpdateAddedCardKeywordEntry(
                                capturedIndex,
                                entry => { entry.CardId = selected.Id.ToString(); entry.Upgraded = upgraded; });
                            QueueRebuild();
                        },
                        out string error))
                    {
                        GD.PushWarning(error);
                    }
                };
                _leftControls.AddChild(CreateRow(
                    LocMan.Loc(
                        model.CardLabelLocKey,
                        model.GetTitle()) + suffix,
                    selector));
            }

            AddStepperRow(
                _leftControls,
                LocMan.Loc(
                    model.AmountLabelLocKey,
                    $"{model.GetTitle()} Amount") + suffix,
                addedEntries[index].Amount,
                0,
                int.MaxValue,
                amount =>
                {
                    UpdateAddedCardKeywordEntry(
                        capturedIndex,
                        entry => entry.Amount = amount);
                    RefreshPreview();
                });
            NLoadoutPileTypeStepper pile = new();
            pile.Init(addedEntries[index].Pile, value =>
            {
                UpdateAddedCardKeywordEntry(capturedIndex, entry => entry.Pile = value);
                RefreshPreview();
            });
            _leftControls.AddChild(CreateRow(LocMan.Loc("CARD_MOD_ADD_CARD_PILE", "Destination Pile") + suffix, pile));
            NLoadoutCardCostStepper cost = new();
            cost.Init(addedEntries[index].FreeToPlay, value =>
            {
                UpdateAddedCardKeywordEntry(capturedIndex, entry => entry.FreeToPlay = value);
                RefreshPreview();
            });
            _leftControls.AddChild(CreateRow(LocMan.Loc("CARD_MOD_CARD_FREE", "Free to Play") + suffix, cost));
        }
    }

    private void UpdateAddedCardKeywordEntry(
        int index,
        Action<LoadoutCardKeywordEntry> update)
    {
        List<LoadoutCardKeywordEntry> entries =
            LoadoutCardKeywordEntry.CloneList(
                _draft.AddedCardKeywordEntries) ?? [];
        if (index < 0 || index >= entries.Count)
            return;
        update(entries[index]);
        _draft.AddedCardKeywordEntries = entries;
    }

    private LoadoutCardKeywordEntryUpgrade? FindCardEntryUpgrade(
        string keywordKey,
        string originalCardId,
        int occurrenceIndex) =>
        _draft.CardKeywordEntryUpgrades?.LastOrDefault(entry =>
            CardEntryUpgradeMatches(
                entry,
                keywordKey,
                originalCardId,
                occurrenceIndex));

    private void SetCardEntryUpgradeAmount(
        string keywordKey,
        string originalCardId,
        int occurrenceIndex,
        int amountDelta)
    {
        UpdateCardEntryUpgrade(
            keywordKey,
            originalCardId,
            occurrenceIndex,
            entry => entry.AmountDelta = amountDelta);
    }

    private void SetCardEntryUpgradeReplacement(
        string keywordKey,
        string originalCardId,
        int occurrenceIndex,
        string selectedCardId)
    {
        UpdateCardEntryUpgrade(
            keywordKey,
            originalCardId,
            occurrenceIndex,
            entry => entry.ReplacementCardId = string.Equals(
                selectedCardId,
                originalCardId,
                StringComparison.OrdinalIgnoreCase)
                    ? null
                    : selectedCardId);
    }

    private void UpdateCardEntryUpgrade(
        string keywordKey,
        string originalCardId,
        int occurrenceIndex,
        Action<LoadoutCardKeywordEntryUpgrade> update)
    {
        List<LoadoutCardKeywordEntryUpgrade> entries =
            LoadoutCardKeywordEntryUpgrade.CloneList(
                _draft.CardKeywordEntryUpgrades) ?? [];
        int index = entries.FindLastIndex(entry => CardEntryUpgradeMatches(
            entry,
            keywordKey,
            originalCardId,
            occurrenceIndex));
        LoadoutCardKeywordEntryUpgrade entry = index >= 0
            ? entries[index]
            : new LoadoutCardKeywordEntryUpgrade
            {
                KeywordKey = keywordKey,
                OriginalCardId = originalCardId,
                OccurrenceIndex = occurrenceIndex
            };
        update(entry);
        if (entry.IsEmpty)
        {
            if (index >= 0)
                entries.RemoveAt(index);
        }
        else if (index < 0)
        {
            entries.Add(entry);
        }
        _draft.CardKeywordEntryUpgrades = entries;
    }

    private static bool CardEntryUpgradeMatches(
        LoadoutCardKeywordEntryUpgrade entry,
        string keywordKey,
        string originalCardId,
        int occurrenceIndex) =>
        entry.OccurrenceIndex == occurrenceIndex
        && string.Equals(
            entry.KeywordKey,
            keywordKey,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            entry.OriginalCardId,
            originalCardId,
            StringComparison.OrdinalIgnoreCase);

    private static int GetAndIncrementCardOccurrence(
        Dictionary<(string KeywordKey, string CardId), int> occurrences,
        LoadoutCardKeywordEntry entry)
    {
        (string KeywordKey, string CardId) key =
            (entry.KeywordKey, entry.CardId);
        occurrences.TryGetValue(key, out int occurrence);
        occurrences[key] = occurrence + 1;
        return occurrence;
    }

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

}
