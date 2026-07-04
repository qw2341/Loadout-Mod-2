#nullable enable

namespace Loadout.Services.Actions;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Loadout.Services.CardModification;
using Loadout.Services.PowerGiver;
using Loadout.Services.Targets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Runs;

public enum LoadoutActionKind
{
    AddCard,
    AddRelic,
    AddPotion,
    RemoveCard,
    UpgradeCard,
    UpgradeAllDeckCards,
    RemoveRelic,
    AdjustPower,
    CardModification
}

public static class LoadoutActionService
{
    public static bool Request(
        LoadoutActionKind kind,
        ModelId modelId,
        int amount,
        LoadoutTargetSelection target,
        int ownedItemIndex = -1,
        ModelId? expectedModelId = null,
        CardModificationOperation cardModificationOperation = CardModificationOperation.None,
        string? cardModificationStateJson = null,
        RelicModel? ownedRelic = null)
    {
        Player? localPlayer = GetLocalRunPlayer();
        if (localPlayer is null)
            return false;

        LoadoutGameAction action = new(
            localPlayer,
            kind,
            modelId,
            amount,
            target,
            ownedItemIndex,
            expectedModelId ?? ModelId.none,
            CombatManager.Instance.IsInProgress,
            cardModificationOperation,
            cardModificationStateJson ?? string.Empty,
            ownedRelic);
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);
        return true;
    }

    public static bool RequestRemoveRelic(LoadoutOwnedItem<RelicModel> item)
    {
        return Request(
            LoadoutActionKind.RemoveRelic,
            item.Model.Id,
            1,
            LoadoutTargetSelection.ForPlayer(item.OwnerNetId),
            item.Index,
            item.Model.Id,
            ownedRelic: item.Model);
    }

    public static bool RequestCardModification(
        CardModificationOperation operation,
        LoadoutOwnedItem<CardModel> item,
        CardModificationState? state = null)
    {
        string stateJson = state is null
            ? string.Empty
            : JsonSerializer.Serialize(state);

        return Request(
            LoadoutActionKind.CardModification,
            item.Model.Id,
            1,
            LoadoutTargetSelection.ForPlayer(item.OwnerNetId),
            item.Index,
            item.Model.Id,
            operation,
            stateJson);
    }

    private static Player? GetLocalRunPlayer()
    {
        try
        {
            return RunManager.Instance.IsInProgress
                ? LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState())
                : null;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"LoadoutPanel: could not resolve local player for loadout action. {exception.Message}");
            return null;
        }
    }
}

public sealed class LoadoutGameAction : GameAction
{
    public LoadoutGameAction(
        Player player,
        LoadoutActionKind kind,
        ModelId modelId,
        int amount,
        LoadoutTargetSelection target,
        int ownedItemIndex,
        ModelId expectedModelId,
        bool enqueuedInCombat,
        CardModificationOperation cardModificationOperation = CardModificationOperation.None,
        string cardModificationStateJson = "",
        RelicModel? ownedRelic = null)
    {
        Player = player;
        Kind = kind;
        ModelId = modelId;
        Amount = amount;
        Target = target;
        OwnedItemIndex = ownedItemIndex;
        ExpectedModelId = expectedModelId;
        WasEnqueuedInCombat = enqueuedInCombat;
        CardModificationOperation = cardModificationOperation;
        CardModificationStateJson = cardModificationStateJson ?? string.Empty;
        OwnedRelic = ownedRelic;
    }

    public override ulong OwnerId => Player.NetId;

    public override GameActionType ActionType => WasEnqueuedInCombat
        ? GameActionType.CombatPlayPhaseOnly
        : GameActionType.Any;

    public Player Player { get; }
    public LoadoutActionKind Kind { get; }
    public ModelId ModelId { get; }
    public int Amount { get; }
    public LoadoutTargetSelection Target { get; }
    public int OwnedItemIndex { get; }
    public ModelId ExpectedModelId { get; }
    public bool WasEnqueuedInCombat { get; }
    public CardModificationOperation CardModificationOperation { get; }
    public string CardModificationStateJson { get; }
    private RelicModel? OwnedRelic { get; }

    protected override async Task ExecuteAction()
    {
        switch (Kind)
        {
            case LoadoutActionKind.AddCard:
                await AddCardCopiesAsync();
                break;
            case LoadoutActionKind.AddRelic:
                await AddRelicCopiesAsync();
                break;
            case LoadoutActionKind.AddPotion:
                await AddPotionCopiesAsync();
                break;
            case LoadoutActionKind.RemoveCard:
                await RemoveDeckCardAsync();
                CardModificationStateService.NotifyStateChanged();
                break;
            case LoadoutActionKind.UpgradeCard:
                UpgradeDeckCard();
                CardModificationStateService.NotifyStateChanged();
                break;
            case LoadoutActionKind.UpgradeAllDeckCards:
                UpgradeAllDeckCards();
                CardModificationStateService.NotifyStateChanged();
                break;
            case LoadoutActionKind.RemoveRelic:
                if (await RemoveRelicAsync())
                    CardModificationStateService.NotifyStateChanged();
                break;
            case LoadoutActionKind.AdjustPower:
                PowerGiverStateService.AdjustCounterFromAction(ModelId.ToString(), Amount, Target, Player);
                break;
            case LoadoutActionKind.CardModification:
                ApplyCardModification();
                break;
        }
    }

    public override INetAction ToNetAction()
    {
        return new NetLoadoutGameAction
        {
            kind = Kind,
            modelId = ModelId,
            amount = Amount,
            target = Target,
            ownedItemIndex = OwnedItemIndex,
            expectedModelId = ExpectedModelId,
            enqueuedInCombat = WasEnqueuedInCombat,
            cardModificationOperation = CardModificationOperation,
            cardModificationStateJson = CardModificationStateJson
        };
    }

    public override string ToString()
    {
        return $"LoadoutGameAction player {Player.NetId} kind {Kind} model {ModelId} amount {Amount} target {Target}";
    }

    private async Task AddCardCopiesAsync()
    {
        if (Amount <= 0 || ResolveCanonicalCard(ModelId) is not { } canonicalCard)
            return;

        foreach (Player targetPlayer in ResolveTargetPlayers())
        {
            await AddDeckCardCopiesAsync(targetPlayer, canonicalCard);

            if (CombatManager.Instance.IsInProgress)
                await AddGeneratedCardCopiesToCombatHandAsync(targetPlayer, canonicalCard);
        }
    }

    private async Task AddDeckCardCopiesAsync(Player targetPlayer, CardModel canonicalCard)
    {
        List<CardModel> cards = CreateRunCards(targetPlayer, canonicalCard, Amount);
        if (cards.Count == 0)
            return;

        IReadOnlyList<CardPileAddResult> results;
        try
        {
            results = await CardPileCmd.Add(cards, targetPlayer.Deck);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"LoadoutPanel: failed adding deck cards '{ModelId}' to player {targetPlayer.NetId}. {exception.Message}");
            return;
        }

        List<CardPileAddResult> addedResults = results.Where(result => result.success).ToList();
        if (addedResults.Count == 0)
            return;

        CardPreviewStyle previewStyle = addedResults.Count > 5
            ? CardPreviewStyle.MessyLayout
            : CardPreviewStyle.HorizontalLayout;
        CardCmd.PreviewCardPileAdd(addedResults, 2f, previewStyle);
    }

    private async Task AddGeneratedCardCopiesToCombatHandAsync(Player targetPlayer, CardModel canonicalCard)
    {
        if (Amount <= 0)
            return;

        ICombatState? combatState = targetPlayer.Creature.CombatState;
        if (combatState is null)
            return;

        List<CardModel> cards = CreateCombatCards(combatState, targetPlayer, canonicalCard, Amount);
        if (cards.Count == 0)
            return;

        foreach (CardModel card in cards)
            CombatManager.Instance.History.CardGenerated(combatState, card, targetPlayer);

        IReadOnlyList<CardPileAddResult> results;
        try
        {
            using (LoadoutCardAddRules.IgnoreHandLimit())
                results = await CardPileCmd.Add(cards, PileType.Hand);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"LoadoutPanel: failed adding combat hand cards '{ModelId}' to player {targetPlayer.NetId}. {exception.Message}");
            return;
        }

        foreach (CardPileAddResult result in results)
        {
            if (result.success)
                await Hook.AfterCardGeneratedForCombat(combatState, result.cardAdded, targetPlayer);
        }
    }

    private List<CardModel> CreateRunCards(Player targetPlayer, CardModel canonicalCard, int amount)
    {
        List<CardModel> cards = new(Math.Max(0, amount));
        for (int i = 0; i < amount; i++)
        {
            try
            {
                cards.Add(targetPlayer.RunState.CreateCard(canonicalCard, targetPlayer));
            }
            catch (Exception exception)
            {
                GD.PushWarning($"LoadoutPanel: stopped creating deck card '{ModelId}' for player {targetPlayer.NetId} after {cards.Count}/{amount}. {exception.Message}");
                break;
            }
        }

        return cards;
    }

    private List<CardModel> CreateCombatCards(ICombatState combatState, Player targetPlayer, CardModel canonicalCard, int amount)
    {
        List<CardModel> cards = new(Math.Max(0, amount));
        for (int i = 0; i < amount; i++)
        {
            try
            {
                cards.Add(combatState.CreateCard(canonicalCard, targetPlayer));
            }
            catch (Exception exception)
            {
                GD.PushWarning($"LoadoutPanel: stopped creating combat hand card '{ModelId}' for player {targetPlayer.NetId} after {cards.Count}/{amount}. {exception.Message}");
                break;
            }
        }

        return cards;
    }

    private async Task AddRelicCopiesAsync()
    {
        if (Amount <= 0 || ResolveCanonicalRelic(ModelId) is not { } canonicalRelic)
            return;

        foreach (Player targetPlayer in ResolveTargetPlayers())
        {
            for (int i = 0; i < Amount; i++)
            {
                try
                {
                    await RelicCmd.Obtain(canonicalRelic.ToMutable(), targetPlayer);
                }
                catch (Exception exception)
                {
                    GD.PushWarning($"LoadoutPanel: stopped adding relic '{ModelId}' to player {targetPlayer.NetId} after {i}/{Amount}. {exception.Message}");
                    break;
                }
            }
        }
    }

    private async Task AddPotionCopiesAsync()
    {
        if (Amount <= 0 || ResolveCanonicalPotion(ModelId) is not { } canonicalPotion)
            return;

        foreach (Player targetPlayer in ResolveTargetPlayers())
        {
            for (int i = 0; i < Amount; i++)
            {
                try
                {
                    PotionProcureResult result = await PotionCmd.TryToProcure(canonicalPotion.ToMutable(), targetPlayer);
                    if (!result.success)
                        break;
                }
                catch (Exception exception)
                {
                    GD.PushWarning($"LoadoutPanel: stopped adding potion '{ModelId}' to player {targetPlayer.NetId} after {i}/{Amount}. {exception.Message}");
                    break;
                }
            }
        }
    }

    private async Task RemoveDeckCardAsync()
    {
        if (TryGetOwnedDeckCard() is not { } card)
            return;

        await CardPileCmd.RemoveFromDeck(card);
    }

    private void UpgradeDeckCard()
    {
        if (TryGetOwnedDeckCard() is not { } card)
            return;

        int upgrades = Math.Max(1, Amount);
        for (int i = 0; i < upgrades; i++)
            CardCmd.Upgrade(card, CardPreviewStyle.None);
    }

    private void UpgradeAllDeckCards()
    {
        int upgrades = Math.Max(1, Amount);
        foreach (Player targetPlayer in ResolveTargetPlayers())
        {
            foreach (CardModel card in targetPlayer.Deck.Cards.ToList())
            {
                for (int i = 0; i < upgrades; i++)
                    CardCmd.Upgrade(card, CardPreviewStyle.None);
            }
        }
    }

    private async Task<bool> RemoveRelicAsync()
    {
        if (TryGetOwnedRelic() is not { } relic)
            return false;

        await RelicCmd.Remove(relic);
        return true;
    }

    private void ApplyCardModification()
    {
        CardModificationState? state = null;
        if (!string.IsNullOrWhiteSpace(CardModificationStateJson))
        {
            try
            {
                state = JsonSerializer.Deserialize<CardModificationState>(CardModificationStateJson);
            }
            catch (Exception exception)
            {
                GD.PushWarning($"CardModification: failed to deserialize synchronized state. {exception.Message}");
            }
        }

        CardModificationStateService.ApplySynchronizedOperation(
            CardModificationOperation,
            ModelId,
            Target,
            OwnedItemIndex,
            ExpectedModelId,
            state,
            Player);
    }

    private CardModel? TryGetOwnedDeckCard()
    {
        Player? targetPlayer = ResolveSingleTargetPlayer();
        if (targetPlayer is null || OwnedItemIndex < 0 || OwnedItemIndex >= targetPlayer.Deck.Cards.Count)
            return null;

        CardModel card = targetPlayer.Deck.Cards[OwnedItemIndex];
        return IdMatches(card, ExpectedModelId) && card.Pile?.Type == PileType.Deck
            ? card
            : null;
    }

    private RelicModel? TryGetOwnedRelic()
    {
        Player? targetPlayer = ResolveSingleTargetPlayer();
        if (targetPlayer is null)
            return null;

        if (OwnedRelic is not null
            && targetPlayer.Relics.Contains(OwnedRelic)
            && IdMatches(OwnedRelic, ExpectedModelId))
        {
            return OwnedRelic;
        }

        if (OwnedItemIndex < 0 || OwnedItemIndex >= targetPlayer.Relics.Count)
            return null;

        RelicModel relic = targetPlayer.Relics[OwnedItemIndex];
        return IdMatches(relic, ExpectedModelId) ? relic : null;
    }

    private IReadOnlyList<Player> ResolveTargetPlayers()
    {
        IReadOnlyList<Player> players = LoadoutTargetService.ResolvePlayers(Target, Player.RunState);
        return players.Count > 0 ? players : [Player];
    }

    private Player? ResolveSingleTargetPlayer()
    {
        return Target.Scope == LoadoutTargetScope.Player && Target.PlayerNetId.HasValue
            ? Player.RunState.GetPlayer(Target.PlayerNetId.Value)
            : Player;
    }

    private static bool IdMatches(AbstractModel model, ModelId id)
    {
        return id == ModelId.none
               || model.Id == id
               || string.Equals(model.Id.ToString(), id.ToString(), StringComparison.Ordinal)
               || string.Equals(model.Id.Entry, id.Entry, StringComparison.OrdinalIgnoreCase);
    }

    private static CardModel? ResolveCanonicalCard(ModelId id)
    {
        return ModelDb.AllCards.FirstOrDefault(card => IdMatches(card, id));
    }

    private static RelicModel? ResolveCanonicalRelic(ModelId id)
    {
        return ModelDb.AllRelics.FirstOrDefault(relic => IdMatches(relic, id));
    }

    private static PotionModel? ResolveCanonicalPotion(ModelId id)
    {
        return ModelDb.AllPotions.FirstOrDefault(potion => IdMatches(potion, id));
    }
}

public struct NetLoadoutGameAction : INetAction, IPacketSerializable
{
    public LoadoutActionKind kind;
    public ModelId modelId;
    public int amount;
    public LoadoutTargetSelection target;
    public int ownedItemIndex;
    public ModelId expectedModelId;
    public bool enqueuedInCombat;
    public CardModificationOperation cardModificationOperation;
    public string cardModificationStateJson;

    public GameAction ToGameAction(Player player)
    {
        return new LoadoutGameAction(
            player,
            kind,
            modelId,
            amount,
            target,
            ownedItemIndex,
            expectedModelId,
            enqueuedInCombat,
            cardModificationOperation,
            cardModificationStateJson ?? string.Empty);
    }

    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt((int)kind, 8);
        writer.WriteFullModelId(modelId);
        writer.WriteInt(amount);
        writer.WriteInt((int)target.Scope, 4);
        writer.WriteBool(target.PlayerNetId.HasValue);
        if (target.PlayerNetId.HasValue)
            writer.WriteULong(target.PlayerNetId.Value);
        writer.WriteInt(ownedItemIndex);
        writer.WriteFullModelId(expectedModelId);
        writer.WriteBool(enqueuedInCombat);
        writer.WriteInt((int)cardModificationOperation, 8);
        writer.WriteString(cardModificationStateJson ?? string.Empty);
    }

    public void Deserialize(PacketReader reader)
    {
        kind = (LoadoutActionKind)reader.ReadInt(8);
        modelId = reader.ReadFullModelId();
        amount = reader.ReadInt();
        LoadoutTargetScope scope = (LoadoutTargetScope)reader.ReadInt(4);
        ulong? playerNetId = reader.ReadBool() ? reader.ReadULong() : null;
        target = new LoadoutTargetSelection(scope, playerNetId);
        ownedItemIndex = reader.ReadInt();
        expectedModelId = reader.ReadFullModelId();
        enqueuedInCombat = reader.ReadBool();
        cardModificationOperation = (CardModificationOperation)reader.ReadInt(8);
        cardModificationStateJson = reader.ReadString();
    }

    public override string ToString()
    {
        return $"NetLoadoutGameAction {kind} model {modelId} amount {amount} target {target}";
    }
}
