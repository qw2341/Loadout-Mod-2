#nullable enable

namespace Loadout.UI.CustomRuns;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Loadout.Patches.Cards.CardModification;
using Loadout.Services.CardModification;
using Loadout.Services.CustomRuns.Catalog;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.Loadouts;
using Loadout.Services.RelicModification;
using Loadout.Services.Targets;
using Loadout.UI.Screens;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Unlocks;

public static class CustomRunEditorPreviewService
{
    public static IReadOnlyList<LoadoutOwnedItem<CardModel>> CreateOwnedCards(
        IReadOnlyList<SavedCardLoadoutEntry> entries)
    {
        Player owner = CreateAuthoringOwner();
        List<LoadoutOwnedItem<CardModel>> items = [];
        for (int index = 0; index < entries.Count; index++)
        {
            CardModel? card = CreateCard(entries[index]);
            if (card is not null)
                items.Add(new LoadoutOwnedItem<CardModel>(owner, index, card));
        }
        return items;
    }

    public static IReadOnlyList<LoadoutOwnedItem<RelicModel>> CreateOwnedRelics(
        IReadOnlyList<SavedRelicLoadoutEntry> entries)
    {
        Player owner = CreateAuthoringOwner();
        List<LoadoutOwnedItem<RelicModel>> items = [];
        for (int index = 0; index < entries.Count; index++)
        {
            RelicModel? relic = CreateRelic(entries[index]);
            if (relic is not null)
                items.Add(new LoadoutOwnedItem<RelicModel>(owner, index, relic));
        }
        return items;
    }

    public static void PreviewCardAdd(CardModel canonical, int upgradeLevel, int amount)
    {
        Player owner = CreateAuthoringOwner();
        List<CardModel> cards = [];
        for (int copy = 0; copy < Math.Max(1, amount); copy++)
        {
            CardModel? card = CreateCard(new SavedCardLoadoutEntry
            {
                ModelId = canonical.Id.ToString(),
                UpgradeLevel = upgradeLevel
            });
            if (card is null)
                continue;
            card.FloorAddedToDeck = 1;
            owner.Deck.AddInternal(card, -1, silent: true);
            cards.Add(card);
        }
        NLoadoutPanelRoot.Instance?.TryPreviewCustomRunCardAdd(cards);
    }

    public static void PreviewCardRemoval(IReadOnlyList<CardModel> cards)
    {
        if (cards.Count == 0)
            return;
        Player owner = CreateAuthoringOwner();
        foreach (CardModel card in cards)
            owner.Deck.AddInternal(card, -1, silent: true);
        NLoadoutPanelRoot.Instance?.TryPreviewCustomRunCardRemoval(cards);
    }

    public static void OpenCardModifier(
        List<SavedCardLoadoutEntry> entries,
        int selectedIndex,
        Action changed)
    {
        if (NLoadoutPanelRoot.Instance is not { } root || entries.Count == 0)
            return;

        Player owner = CreateAuthoringOwner();
        List<LoadoutOwnedItem<CardModel>> items = [];
        Dictionary<CardModel, SavedCardLoadoutEntry> entryByCard = new(ReferenceEqualityComparer.Instance);
        for (int index = 0; index < entries.Count; index++)
        {
            CardModel? card = CreateCard(entries[index], includeState: false);
            if (card is null)
                continue;
            LoadoutOwnedItem<CardModel> item = new(owner, index, card);
            items.Add(item);
            entryByCard[card] = entries[index];
        }
        if (items.Count == 0)
            return;

        LoadoutOwnedItem<CardModel> selected = items.FirstOrDefault(item => item.Index == selectedIndex) ?? items[0];
        NCardModificationScreen screen = NCardModificationScreen.Create();
        screen.Name = "CustomRunCardModifier";
        screen.InitForCustomRun(
            selected,
            items,
            item => entryByCard[item.Model].ModificationState?.Clone() ?? new CardModificationSpec(),
            (item, state) =>
            {
                SavedCardLoadoutEntry entry = entryByCard[item.Model];
                entry.ModificationState = state.IsEmpty ? null : state.Clone();
                changed();
            },
            (item, amount) =>
            {
                SavedCardLoadoutEntry source = entryByCard[item.Model];
                int insertAt = Math.Clamp(item.Index + 1, 0, entries.Count);
                for (int copy = 0; copy < Math.Max(1, amount); copy++)
                    entries.Insert(insertAt + copy, source.Clone());
                changed();
            });
        root.OpenScreen(screen);
    }

    public static void OpenRelicModifier(
        List<SavedRelicLoadoutEntry> entries,
        int selectedIndex,
        Action changed)
    {
        if (NLoadoutPanelRoot.Instance is not { } root || entries.Count == 0)
            return;

        Player owner = CreateAuthoringOwner();
        List<LoadoutOwnedItem<RelicModel>> items = [];
        Dictionary<RelicModel, SavedRelicLoadoutEntry> entryByRelic = new(ReferenceEqualityComparer.Instance);
        for (int index = 0; index < entries.Count; index++)
        {
            RelicModel? relic = CreateRelic(entries[index], includeState: false);
            if (relic is null)
                continue;
            LoadoutOwnedItem<RelicModel> item = new(owner, index, relic);
            items.Add(item);
            entryByRelic[relic] = entries[index];
        }
        if (items.Count == 0)
            return;

        LoadoutOwnedItem<RelicModel> selected = items.FirstOrDefault(item => item.Index == selectedIndex) ?? items[0];
        NRelicModificationScreen screen = NRelicModificationScreen.Create();
        screen.Name = "CustomRunRelicModifier";
        screen.InitForCustomRun(
            selected,
            items,
            item => entryByRelic[item.Model].ModificationState?.Clone() ?? new RelicModificationState(),
            (item, state) =>
            {
                SavedRelicLoadoutEntry entry = entryByRelic[item.Model];
                entry.ModificationState = state.IsEmpty ? null : state.Clone();
                changed();
            },
            (item, amount) =>
            {
                SavedRelicLoadoutEntry source = entryByRelic[item.Model];
                int insertAt = Math.Clamp(item.Index + 1, 0, entries.Count);
                for (int copy = 0; copy < Math.Max(1, amount); copy++)
                    entries.Insert(insertAt + copy, source.Clone());
                changed();
            });
        root.OpenScreen(screen);
    }

    private static Player CreateAuthoringOwner()
    {
        CharacterModel character = ModelDb.AllCharacters.First(candidate => candidate.IsPlayable);
        return Player.CreateForNewRun(character, UnlockState.all, ulong.MaxValue - 8);
    }

    private static CardModel? CreateCard(SavedCardLoadoutEntry entry, bool includeState = true)
    {
        if (!CustomRunCatalogService.TryResolve(SelectionModelKind.Card, entry.ModelId, out CustomRunCatalogEntry catalog)
            || catalog.Model is not CardModel canonical)
            return null;
        CardModel card = canonical.ToMutable();
        for (int upgrade = 0; upgrade < entry.UpgradeLevel && card.IsUpgradable; upgrade++)
        {
            card.UpgradeInternal();
            card.FinalizeUpgradeInternal();
        }
        return !includeState || entry.ModificationState is null
            ? card
            : CardModificationRuntime.CreatePreviewCard(card, entry.ModificationState);
    }

    private static RelicModel? CreateRelic(SavedRelicLoadoutEntry entry, bool includeState = true)
    {
        if (!CustomRunCatalogService.TryResolve(SelectionModelKind.Relic, entry.ModelId, out CustomRunCatalogEntry catalog)
            || catalog.Model is not RelicModel canonical)
            return null;
        RelicModel relic = canonical.ToMutable();
        RelicModificationStateService.ApplyPermanentToRelic(relic);
        return !includeState || entry.ModificationState is null
            ? relic
            : RelicModificationStateService.CreatePreviewRelic(relic, entry.ModificationState);
    }
}
