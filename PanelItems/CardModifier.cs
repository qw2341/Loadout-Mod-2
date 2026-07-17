using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Loadout.Services.Actions;
using Loadout.Services.CardModification;
using Loadout.Services.LastActions;
using Loadout.Services.Targets;
using Loadout.UI;
using Loadout.UI.Managers;
using Loadout.UI.Screens;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;

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
            GetSearchText = item => $"{item.Model.Id} {CardPrinter.FormatCardTitle(item.Model)} {item.Model.TitleLocString} {item.Model.Description}",
            CreateView = (item, state) => CardPrinter.CreateCardGridItem(item.Model, state),
            ViewReady = (item, view) => CardPrinter.RefreshCardVisuals(view, item.Model),
            UpdateView = (item, view, state) =>
            {
                CardPrinter.ForceRefreshCardVisuals(view, item.Model);
                CardPrinter.UpdateCardGridItem(view, state);
            },
            BindActivationWithCleanup = (item, view, activate) => CardPrinter.BindCardActivationWithCleanup(
                view,
                activate,
                () => OpenCardModificationScreen(modifierScreen, item, view))
        };

        void BuildCardModifierScreen(SelectScreenBuilder<LoadoutOwnedItem<CardModel>> builder)
        {
            builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
            builder.Materialization(SelectMaterializationMode.Lazy);
            builder.Layout(5, NCard.defaultSize * NCardHolder.smallScale, 32, 40, paddingLeft: 0f, paddingTop: 200f, paddingRight: 0f);
            builder.ActionButton(
                "upgrade_all", LocMan.Loc("UPGRADE_ALL", "Upgrade All"),
                HandleUpgradeAllDeckCards,
                CommonHelpers.LoadActionButtonIcon("CardModifier.png"));
            builder.ActionButton(
                "host_permamods", LocMan.Loc("HOST_PERMAMODS_DOWNLOAD_TITLE", "Download Host Permamods"),
                _ => OpenHostPermamodConflictScreen(),
                CommonHelpers.LoadActionButtonIcon("CardModifier.png"));
        }

        CommonHelpers.CreateAndAddDynamicLoadoutItem(GetSelectedTargetDeckCardsForModifier,
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
                    refresh);
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
        if (!LoadoutImmediateMutationService.RequestUpgradeAllDeckCards(target))
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
            GetSelectedTargetDeckCardsForModifier(),
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

    private static IReadOnlyList<LoadoutOwnedItem<CardModel>> GetSelectedTargetDeckCardsForModifier()
    {
        LoadoutTargetSelection target = LoadoutTargetService.GetSelected(CardModifier.CardModifierTargetKey, LoadoutTargetMode.PlayersOnly);
        return LoadoutTargetService.BuildOwnedItems(target, player => player.Deck.Cards.ToList());
    }
}
