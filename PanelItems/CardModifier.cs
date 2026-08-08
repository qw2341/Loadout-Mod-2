using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Loadout.Services.Actions;
using Loadout.Services.CardModification;
using Loadout.Services.LastActions;
using Loadout.Services.Targets;
using Loadout.Patches.Cards.CardModification;
using Loadout.UI;
using Loadout.UI.Managers;
using Loadout.UI.Screens;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Runs;

namespace Loadout.PanelItems;

public class CardModifier
{
    public const string CardModifierTargetKey = "card_modifier";
    private const string CardModifierTargetDropdownName = "CardModifierTargetDropdown";
    public static void Initialize()
    {
        NGenericSelectScreen modifierScreen = null;
        SelectItemAdapter<LoadoutOwnedItem<CardModel>> cardModifierAdapter = new()
        {
            GetId = item => CommonHelpers.OwnedSlotItemId(item),
            GetName = item => CardPrinter.FormatCardTitle(item.Model),
            GetSearchText = item => $"{item.Model.Id} {CardPrinter.FormatCardTitle(item.Model)} {item.Model.GetDescriptionForPile(item.CardPileType ?? PileType.Deck)}",
            CreateView = (item, state) => CardPrinter.CreateCardGridItem(item.Model, state, item.CardPileType ?? PileType.Deck),
            ViewReady = (item, view) => CardPrinter.RefreshCardVisuals(view, item.Model, item.CardPileType ?? PileType.Deck),
            UpdateView = (item, view, state) =>
            {
                CardPrinter.ForceRefreshCardVisuals(view, item.Model, item.CardPileType ?? PileType.Deck);
                CardPrinter.UpdateCardGridItem(view, state);
            },
            BindActivationWithCleanup = (item, view, activate) => CardPrinter.BindCardActivationWithCleanup(
                view,
                activate,
                () => OpenCardModificationScreen(modifierScreen, item, view))
        };

        void RefreshModifiedOwnedCard(LoadoutOwnedItem<CardModel> changed, LoadoutCardVisualRefreshKind refreshKind)
        {
            if (changed.CardPileType is null or PileType.Deck
                || modifierScreen is not NCardSelectScreen cardScreen
                || !GodotObject.IsInstanceValid(cardScreen)
                || !cardScreen.IsInsideTree()
                || !cardScreen.IsVisibleInTree())
            {
                return;
            }

            string itemId = CommonHelpers.OwnedSlotItemId(changed);
            Callable.From(() =>
            {
                cardScreen.RefreshItemById(
                    itemId,
                    (_, view) =>
                    {
                        if (refreshKind == LoadoutCardVisualRefreshKind.Reload)
                            CardPrinter.ReloadCardVisuals(view, changed.Model, changed.CardPileType ?? PileType.Deck);
                        else
                            CardPrinter.RefreshCardVisuals(view, changed.Model, changed.CardPileType ?? PileType.Deck);
                    },
                    refreshMetadata: true,
                    refreshLayout: true);
            }).CallDeferred();
        }

        CardModificationRuntime.OwnedCardChanged += RefreshModifiedOwnedCard;

        void BuildCardModifierScreen(SelectScreenBuilder<LoadoutOwnedItem<CardModel>> builder)
        {
            builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
            builder.Materialization(SelectMaterializationMode.Lazy);
            builder.Layout(5, NCard.defaultSize * NCardHolder.smallScale, 32, 40, paddingLeft: 0f, paddingTop: 200f, paddingRight: 0f);
            builder.ActionButton(
                "upgrade_all", LocMan.Loc("UPGRADE_ALL", "Upgrade All"),
                HandleUpgradeAllDeckCards,
                CommonHelpers.LoadActionButtonIcon("CardModifier.png"));
            if (IsMultiplayerClient())
            {
                builder.ActionButton(
                    "host_permamods", LocMan.Loc("HOST_PERMAMODS_DOWNLOAD_TITLE", "Download Host Permamods"),
                    _ => OpenHostPermamodConflictScreen(),
                    CommonHelpers.LoadActionButtonIcon("CardModifier.png"));
            }
        }

        CommonHelpers.CreateAndAddDynamicLoadoutItem(
            () => GetSelectedTargetCardsForModifier(modifierScreen as NCardSelectScreen),
            cardModifierAdapter,
            BuildCardModifierScreen,
            HandleUpgradeCardActivatedAsync,
            "CardModifier.png",
            LocMan.Loc("CARDMODIFIER_TITLE", "Card Modifier"),
            LocMan.Loc("CARDMODIFIER_DESC", "Right-click this relic to modify any card you want; right-click cards to modify them."),
            (screen, refresh) =>
            {
                modifierScreen = screen;
                LoadoutTargetService.UpsertTargetDropdown(
                    screen,
                    CardModifierTargetDropdownName,
                    CardModifierTargetKey,
                    LoadoutTargetMode.PlayersOnly,
                    () =>
                    {
                        if (screen is NCardSelectScreen targetCardScreen)
                            targetCardScreen.RefreshObservedPiles();
                        refresh();
                    });
                if (screen is NCardSelectScreen cardScreen)
                {
                    cardScreen.ConfigurePileTarget(
                        LoadoutCardPileTarget.Deck,
                        LoadoutCardPileTargets.OwnedCardOptions,
                        _ => refresh());
                    cardScreen.ConfigureObservedPiles(
                        () => LoadoutCardPileTargets.ResolveObservedPiles(
                            LoadoutTargetService.GetSelected(CardModifierTargetKey, LoadoutTargetMode.PlayersOnly),
                            cardScreen.SelectedPileTarget),
                        refresh);
                }
            },
            (_, _) => { },
            selectScreenScenePath: CommonHelpers.CardSelectScreenScenePath,
            reconcileModelsOnEveryOpen: false,
            refreshModelsAfterActivation: false,
            syncChangesWhileHidden: false);

    }

    public static Task<IReadOnlyList<LastActionEntry>> HandleUpgradeCardActivatedAsync(NGenericSelectScreen screen, IGenericSelectItem selectItem)
    {
        if (selectItem.UntypedModel is not LoadoutOwnedItem<CardModel> item)
            return Task.FromResult<IReadOnlyList<LastActionEntry>>([]);

        bool requested = LoadoutImmediateMutationService.RequestUpgradeCard(
            item,
            screen.GetCurrentActivationMultiplier());

        if (requested && selectItem.View is Control view)
            CommonHelpers.PlayCardSmithFeedback(view);

        return Task.FromResult<IReadOnlyList<LastActionEntry>>([]);
    }

    public static void HandleUpgradeAllDeckCards(NGenericSelectScreen screen)
    {
        LoadoutTargetSelection target = LoadoutTargetService.GetSelected(CardModifierTargetKey, LoadoutTargetMode.PlayersOnly);
        LoadoutCardPileTarget pileTarget = screen is NCardSelectScreen cardScreen
            ? cardScreen.SelectedPileTarget
            : LoadoutCardPileTarget.Deck;
        if (!LoadoutImmediateMutationService.RequestUpgradeAllDeckCards(target, pileTarget))
            return;

        screen.ForEachVisibleItemView((item, view) =>
        {
            if (item.UntypedModel is LoadoutOwnedItem<CardModel>)
                CommonHelpers.PlayCardSmithFeedback(view);
        });
    }

    public static bool AddCopiesToTargetDeck(LoadoutOwnedItem<CardModel> item, int amount)
    {
        return LoadoutImmediateMutationService.RequestAddDeckCardCopies(item, amount);
    }

    private static void OpenCardModificationScreen(
        NGenericSelectScreen selectScreen,
        LoadoutOwnedItem<CardModel> fallbackItem,
        Control sourceView)
    {
        LoadoutOwnedItem<CardModel> item = ResolveCurrentItem(selectScreen, sourceView, fallbackItem);
        NLoadoutPanelRoot root = NLoadoutPanelRoot.Instance;
        if (root is null)
            return;

        NCardModificationScreen modificationScreen = NCardModificationScreen.Create();
        modificationScreen.Name = $"CardModification_{CommonHelpers.MakeSafeNodeName(CommonHelpers.OwnedSlotItemId(item))}";
        SelectScrollOffsetState parentScroll = selectScreen.CaptureScrollOffset();
        modificationScreen.Init(
            item,
            GetSelectedTargetCardsForModifier(selectScreen as NCardSelectScreen),
            () =>
            {
                if (GodotObject.IsInstanceValid(selectScreen))
                    selectScreen.RestoreScrollOffset(parentScroll);
            });
        root.OpenScreen(modificationScreen);
    }

    private static LoadoutOwnedItem<CardModel> ResolveCurrentItem(
        NGenericSelectScreen screen,
        Control sourceView,
        LoadoutOwnedItem<CardModel> fallbackItem)
    {
        if (screen is not null
            && sourceView is not null
            && screen.TryGetItemForView(sourceView, out IGenericSelectItem currentItem)
            && currentItem.UntypedModel is LoadoutOwnedItem<CardModel> currentOwnedCard)
        {
            return currentOwnedCard;
        }

        return fallbackItem;
    }

    private static bool SameOwnedItem(LoadoutOwnedItem<CardModel> left, LoadoutOwnedItem<CardModel> right)
    {
        return left.OwnerNetId == right.OwnerNetId
               && left.Index == right.Index
               && ReferenceEquals(left.Model, right.Model);
    }

    private static void OpenHostPermamodConflictScreen()
    {
        if (!CardModificationNetProtocol.HasPendingHostPermanentSnapshot)
        {
            GD.PushWarning("CardModifier: no host permamod snapshot is available to download.");
            return;
        }

        NLoadoutPanelRoot root = NLoadoutPanelRoot.Instance;
        if (root is null)
            return;

        NHostPermamodConflictScreen screen = new()
        {
            Name = "HostPermamodConflict"
        };
        root.OpenScreen(screen);
    }

    private static bool IsMultiplayerClient()
    {
        try
        {
            return RunManager.Instance.NetService.Type == NetGameType.Client;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<LoadoutOwnedItem<CardModel>> GetSelectedTargetCardsForModifier(NCardSelectScreen screen)
    {
        LoadoutTargetSelection target = LoadoutTargetService.GetSelected(CardModifier.CardModifierTargetKey, LoadoutTargetMode.PlayersOnly);
        LoadoutCardPileTarget pileTarget = screen?.SelectedPileTarget ?? LoadoutCardPileTarget.Deck;
        return LoadoutCardPileTargets.BuildOwnedCards(target, pileTarget);
    }
}
