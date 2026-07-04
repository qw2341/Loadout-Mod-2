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
        SelectItemAdapter<LoadoutOwnedItem<CardModel>> cardModifierAdapter = new()
        {
            GetId = item => CommonHelpers.OwnedItemId(item),
            GetName = item => CardPrinter.FormatCardTitle(item.Model),
            GetSearchText = item => $"{item.Model.Id} {CardPrinter.FormatCardTitle(item.Model)} {item.Model.TitleLocString} {item.Model.Description}",
            CreateView = (item, state) => CardPrinter.CreateCardGridItem(item.Model, state),
            ViewReady = (_, view) => CardPrinter.RefreshCardVisuals(view),
            UpdateView = (_, view, state) =>
            {
                CardPrinter.ForceRefreshCardVisuals(view);
                CardPrinter.UpdateCardGridItem(view, state);
            },
            BindActivation = (item, view, activate) => CardPrinter.BindCardActivation(
                view,
                activate,
                () => OpenCardModificationScreen(item, view))
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
                "host_permamods", LocMan.Loc("HOST_PERMAMODS_TITLE", "Host Permamods"),
                _ => OpenHostPermamodConflictScreen(),
                CommonHelpers.LoadActionButtonIcon("CardModifier.png"));
        }

        CommonHelpers.CreateAndAddDynamicLoadoutItem(GetSelectedTargetDeckCardsForModifier,
            cardModifierAdapter,
            BuildCardModifierScreen,
            HandleUpgradeCardActivatedAsync,
            "CardModifier.png",
            "Card Modifier",
            "Modifies cards in your current deck.",
            (screen, refresh) =>
            {
                LoadoutTargetService.UpsertTargetDropdown(
                    screen,
                    CardModifierTargetDropdownName,
                    CardModifierTargetKey,
                    LoadoutTargetMode.PlayersOnly,
                    refresh);
            });

    }

    public static Task<IReadOnlyList<LastActionEntry>> HandleUpgradeCardActivatedAsync(NGenericSelectScreen screen, IGenericSelectItem selectItem)
    {
        if (selectItem.UntypedModel is not LoadoutOwnedItem<CardModel> item)
            return Task.FromResult<IReadOnlyList<LastActionEntry>>([]);

        bool requested = LoadoutActionService.Request(
            LoadoutActionKind.UpgradeCard,
            item.Model.Id,
            screen.GetCurrentActivationMultiplier(),
            LoadoutTargetSelection.ForPlayer(item.OwnerNetId),
            item.Index,
            item.Model.Id);

        if (requested && selectItem.View is Control view)
            CommonHelpers.PlayCardSmithFeedback(view);

        return Task.FromResult<IReadOnlyList<LastActionEntry>>([]);
    }

    public static void HandleUpgradeAllDeckCards(NGenericSelectScreen screen)
    {
        LoadoutTargetSelection target = LoadoutTargetService.GetSelected(CardModifierTargetKey, LoadoutTargetMode.PlayersOnly);
        if (!LoadoutActionService.Request(LoadoutActionKind.UpgradeAllDeckCards, ModelId.none, 1, target))
            return;

        screen.ForEachVisibleItemView((item, view) =>
        {
            if (item.UntypedModel is LoadoutOwnedItem<CardModel>)
                CommonHelpers.PlayCardSmithFeedback(view);
        }, materializeMissing: true);
    }

    private static void OpenCardModificationScreen(LoadoutOwnedItem<CardModel> item, Control sourceView)
    {
        NLoadoutPanelRoot root = NLoadoutPanelRoot.Instance;
        if (root is null)
            return;

        NCardModificationScreen screen = NCardModificationScreen.Create();
        screen.Name = $"CardModification_{CommonHelpers.MakeSafeNodeName(CommonHelpers.OwnedItemId(item))}";
        screen.Init(
            item,
            GetSelectedTargetDeckCardsForModifier(),
            () =>
            {
                if (GodotObject.IsInstanceValid(sourceView) && sourceView.IsInsideTree())
                    CardPrinter.ForceRefreshCardVisuals(sourceView);
            });
        root.OpenScreen(screen);
    }

    private static void OpenHostPermamodConflictScreen()
    {
        if (!CardModificationMultiplayerSyncService.HasPendingHostPermanentSnapshot)
        {
            GD.PushWarning("CardModifier: no host permamod snapshot is available.");
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
