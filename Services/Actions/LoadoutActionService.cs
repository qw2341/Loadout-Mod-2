#nullable enable

namespace Loadout.Services.Actions;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
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
    AdjustPower
}

public static class LoadoutActionService
{
    public static bool Request(
        LoadoutActionKind kind,
        ModelId modelId,
        int amount,
        LoadoutTargetSelection target,
        int ownedItemIndex = -1,
        ModelId? expectedModelId = null)
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
            CombatManager.Instance.IsInProgress);
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);
        return true;
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
        bool enqueuedInCombat)
    {
        Player = player;
        Kind = kind;
        ModelId = modelId;
        Amount = amount;
        Target = target;
        OwnedItemIndex = ownedItemIndex;
        ExpectedModelId = expectedModelId;
        WasEnqueuedInCombat = enqueuedInCombat;
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
                break;
            case LoadoutActionKind.UpgradeCard:
                UpgradeDeckCard();
                break;
            case LoadoutActionKind.UpgradeAllDeckCards:
                UpgradeAllDeckCards();
                break;
            case LoadoutActionKind.RemoveRelic:
                await RemoveRelicAsync();
                break;
            case LoadoutActionKind.AdjustPower:
                PowerGiverStateService.AdjustCounterFromAction(ModelId.ToString(), Amount, Target, Player);
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
            enqueuedInCombat = WasEnqueuedInCombat
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
            List<CardPileAddResult> results = new();
            for (int i = 0; i < Amount; i++)
            {
                try
                {
                    CardModel card = targetPlayer.RunState.CreateCard(canonicalCard, targetPlayer);
                    CardPileAddResult result = await CardPileCmd.Add(card, PileType.Deck);
                    if (!result.success)
                        break;

                    results.Add(result);
                }
                catch (Exception exception)
                {
                    GD.PushWarning($"LoadoutPanel: stopped adding card '{ModelId}' to player {targetPlayer.NetId} after {results.Count}/{Amount}. {exception.Message}");
                    break;
                }
            }

            if (results.Count > 0)
            {
                CardPreviewStyle previewStyle = results.Count > 5
                    ? CardPreviewStyle.MessyLayout
                    : CardPreviewStyle.HorizontalLayout;
                CardCmd.PreviewCardPileAdd(results, 2f, previewStyle);
            }
        }
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

    private async Task RemoveRelicAsync()
    {
        if (TryGetOwnedRelic() is not { } relic)
            return;

        await RelicCmd.Remove(relic);
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
        if (targetPlayer is null || OwnedItemIndex < 0 || OwnedItemIndex >= targetPlayer.Relics.Count)
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

    public GameAction ToGameAction(Player player)
    {
        return new LoadoutGameAction(player, kind, modelId, amount, target, ownedItemIndex, expectedModelId, enqueuedInCombat);
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
    }

    public override string ToString()
    {
        return $"NetLoadoutGameAction {kind} model {modelId} amount {amount} target {target}";
    }
}
