#nullable enable

namespace Loadout.UI.CustomRuns;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HarmonyLib;
using Loadout.Patches.Cards.CardModification;
using Loadout.Services.CardModification;
using Loadout.Services.CustomRuns.Catalog;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.Loadouts;
using Loadout.Services.RelicModification;
using Loadout.Services.Targets;
using Loadout.UI.Screens;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Unlocks;

public static class CustomRunEditorPreviewService
{
    public const string PreviewMeta = "LoadoutCustomRunPreview";
    private static readonly System.Reflection.FieldInfo? DeckPlayerField =
        AccessTools.Field(typeof(NDeckViewScreen), "_player");

    public static bool TryOpenDeck(
        RunSetupDefinition setup,
        CharacterModel? defaultCharacter,
        out string error)
    {
        error = string.Empty;
        NLoadoutPanelRoot? root = NLoadoutPanelRoot.Instance;
        CharacterModel? character = ResolveCharacter(setup, defaultCharacter);
        if (root is null || character is null || DeckPlayerField is null)
        {
            error = "The native deck view is not available.";
            return false;
        }

        try
        {
            Player player = Player.CreateForNewRun(character, UnlockState.all, ulong.MaxValue - 7);
            player.Deck.Clear(silent: true);
            foreach (SavedCardLoadoutEntry entry in GetCardEntries(setup, character))
            {
                CardModel? card = CreateCard(entry);
                if (card is null)
                    continue;
                card.FloorAddedToDeck = 1;
                player.Deck.AddInternal(card, -1, silent: true);
            }

            string scenePath = SceneHelper.GetScenePath("screens/deck_view_screen");
            PackedScene scene = PreloadManager.Cache.GetScene(scenePath);
            NDeckViewScreen screen = scene.Instantiate<NDeckViewScreen>(PackedScene.GenEditState.Disabled);
            screen.Name = "CustomRunDeckView";
            screen.SetMeta(PreviewMeta, true);
            DeckPlayerField.SetValue(screen, player);
            root.OpenScreen(screen);
            return true;
        }
        catch (Exception exception)
        {
            error = $"Could not open the native deck view: {exception.Message}";
            return false;
        }
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

    private static CharacterModel? ResolveCharacter(
        RunSetupDefinition setup,
        CharacterModel? defaultCharacter = null)
    {
        if (setup.Character.Mode == SelectionMode.Fixed)
        {
            foreach (string id in setup.Character.FixedModelIds)
            {
                if (CustomRunCatalogService.TryResolve(SelectionModelKind.Character, id, out CustomRunCatalogEntry entry)
                    && entry.Model is CharacterModel character
                    && character.IsPlayable)
                    return character;
            }
        }
        return defaultCharacter is { IsPlayable: true }
            ? defaultCharacter
            : ModelDb.AllCharacters.FirstOrDefault(character => character.IsPlayable);
    }

    private static IReadOnlyList<SavedCardLoadoutEntry> GetCardEntries(
        RunSetupDefinition setup,
        CharacterModel defaultCharacter)
    {
        if (setup.StartingDeck.Mode != SelectionMode.Fixed)
        {
            return defaultCharacter.StartingDeck
                .Select(card => new SavedCardLoadoutEntry { ModelId = card.Id.ToString() })
                .ToList();
        }
        return setup.StartingCardEntries.Count > 0
            ? setup.StartingCardEntries
            : setup.StartingDeck.FixedModelIds.Select(id => new SavedCardLoadoutEntry { ModelId = id }).ToList();
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
