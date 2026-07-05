#nullable enable

namespace Loadout.Services.Loadouts;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Loadout.Services.Actions;
using Loadout.Services.CardModification;
using Loadout.Services.Targets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Runs;

public static class LoadoutApplyService
{
    public static event Action<string>? WarningRaised;

    public static bool RequestApply(SavedLoadout loadout, LoadoutTargetSelection target)
    {
        Player? localPlayer = GetLocalRunPlayer();
        if (localPlayer is null)
            return false;

        LoadoutTargetSelection effectiveTarget = ForceClientTargetToSelf(target, localPlayer);
        string encodedPayload = LoadoutSerializationService.Encode(loadout);
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new ApplyLoadoutGameAction(
            localPlayer,
            loadout.Kind,
            encodedPayload,
            effectiveTarget,
            CombatManager.Instance.IsInProgress));
        return true;
    }

    private static LoadoutTargetSelection ForceClientTargetToSelf(LoadoutTargetSelection target, Player localPlayer)
    {
        try
        {
            return RunManager.Instance.NetService.Type == NetGameType.Client
                ? LoadoutTargetSelection.ForPlayer(localPlayer.NetId)
                : target;
        }
        catch
        {
            return target;
        }
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
            Warn($"Loadout: could not resolve local player for loadout apply. {exception.Message}");
            return null;
        }
    }

    internal static void Warn(string message)
    {
        GD.PushWarning(message);

        try
        {
            WarningRaised?.Invoke(message);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Loadout: warning handler failed. {exception.Message}");
        }
    }
}

public sealed class ApplyLoadoutGameAction : GameAction
{
    public ApplyLoadoutGameAction(
        Player player,
        LoadoutKind kind,
        string encodedPayload,
        LoadoutTargetSelection target,
        bool enqueuedInCombat)
    {
        Player = player;
        Kind = kind;
        EncodedPayload = encodedPayload ?? string.Empty;
        Target = target;
        WasEnqueuedInCombat = enqueuedInCombat;
    }

    public override ulong OwnerId => Player.NetId;

    public override GameActionType ActionType => WasEnqueuedInCombat
        ? GameActionType.CombatPlayPhaseOnly
        : GameActionType.Any;

    public Player Player { get; }
    public LoadoutKind Kind { get; }
    public string EncodedPayload { get; }
    public LoadoutTargetSelection Target { get; }
    public bool WasEnqueuedInCombat { get; }

    protected override async Task ExecuteAction()
    {
        if (!LoadoutSerializationService.TryDecode(EncodedPayload, out SavedLoadout loadout, out string error))
        {
            LoadoutApplyService.Warn($"Loadout: synchronized loadout apply failed. {error}");
            return;
        }

        loadout.Kind = Kind;
        IReadOnlyList<Player> targets = ResolveTargetPlayers();
        HashSet<ulong> changedCardPlayers = new();
        HashSet<ulong> changedRelicPlayers = new();
        foreach (Player targetPlayer in targets)
        {
            (bool cardsChanged, bool relicsChanged) = await ApplyToPlayer(loadout, targetPlayer);
            if (cardsChanged)
                changedCardPlayers.Add(targetPlayer.NetId);
            if (relicsChanged)
                changedRelicPlayers.Add(targetPlayer.NetId);
        }

        if (changedCardPlayers.Count > 0)
            LoadoutRunContentChangeService.Notify(LoadoutRunContentKind.Cards, changedCardPlayers);
        if (changedRelicPlayers.Count > 0)
            LoadoutRunContentChangeService.Notify(LoadoutRunContentKind.Relics, changedRelicPlayers);
    }

    public override INetAction ToNetAction()
    {
        return new NetApplyLoadoutGameAction
        {
            kind = Kind,
            encodedPayload = EncodedPayload,
            target = Target,
            enqueuedInCombat = WasEnqueuedInCombat
        };
    }

    public override string ToString()
    {
        return $"ApplyLoadoutGameAction player {Player.NetId} kind {Kind} target {Target}";
    }

    private async Task<(bool CardsChanged, bool RelicsChanged)> ApplyToPlayer(SavedLoadout loadout, Player targetPlayer)
    {
        bool cardsChanged = false;
        bool relicsChanged = false;
        if (loadout.HasCards)
            cardsChanged = await ReplaceCards(targetPlayer, loadout.Cards);

        if (loadout.HasRelics)
            relicsChanged = await ReplaceRelics(targetPlayer, loadout.Relics);

        return (cardsChanged, relicsChanged);
    }

    private async Task<bool> ReplaceCards(Player targetPlayer, IReadOnlyList<SavedCardLoadoutEntry> entries)
    {
        List<CardModel> existingCards = targetPlayer.Deck.Cards.ToList();
        if (existingCards.Count > 0)
        {
            try
            {
                await CardPileCmd.RemoveFromDeck(existingCards, showPreview: false);
            }
            catch (Exception exception)
            {
                LoadoutApplyService.Warn($"Loadout: failed to clear deck for player {targetPlayer.NetId}. {exception.Message}");
            }
        }

        List<CardModel> cardsToAdd = [];
        Dictionary<CardModel, CardModificationState> stateByCard = new(ReferenceEqualityComparer.Instance);
        foreach (SavedCardLoadoutEntry entry in entries)
        {
            CardModel? canonical = LoadoutSerializationService.ResolveCard(entry.ModelId);
            if (canonical is null)
            {
                LoadoutApplyService.Warn($"Loadout: skipped unknown card '{entry.ModelId}'.");
                continue;
            }

            int count = Math.Max(1, entry.Count);
            for (int i = 0; i < count; i++)
            {
                CardModel? card = CreateDeckCard(targetPlayer, canonical, entry.UpgradeLevel);
                if (card is null)
                    continue;

                cardsToAdd.Add(card);
                if (entry.ModificationState is not null && !entry.ModificationState.IsEmpty)
                    stateByCard[card] = entry.ModificationState.Clone();
            }
        }

        if (cardsToAdd.Count == 0)
        {
            CardModificationStateService.ReplaceTemporaryStatesForPlayer(
                targetPlayer,
                new Dictionary<CardModel, CardModificationState>(ReferenceEqualityComparer.Instance));
            return existingCards.Count > 0;
        }

        IReadOnlyList<CardPileAddResult> results;
        try
        {
            results = await CardPileCmd.Add(cardsToAdd, targetPlayer.Deck, skipVisuals: true);
        }
        catch (Exception exception)
        {
            LoadoutApplyService.Warn($"Loadout: failed adding loadout cards to player {targetPlayer.NetId}. {exception.Message}");
            CardModificationStateService.ReplaceTemporaryStatesForPlayer(
                targetPlayer,
                new Dictionary<CardModel, CardModificationState>(ReferenceEqualityComparer.Instance));
            return existingCards.Count > 0;
        }

        Dictionary<CardModel, CardModificationState> appliedStatesByCard = new(ReferenceEqualityComparer.Instance);
        foreach (CardPileAddResult result in results)
        {
            if (!result.success || !stateByCard.TryGetValue(result.cardAdded, out CardModificationState? state))
                continue;

            appliedStatesByCard[result.cardAdded] = state;
        }

        CardModificationStateService.ReplaceTemporaryStatesForPlayer(targetPlayer, appliedStatesByCard);
        return existingCards.Count > 0 || results.Any(result => result.success);
    }

    private static CardModel? CreateDeckCard(Player targetPlayer, CardModel canonical, int upgradeLevel)
    {
        try
        {
            CardModel card = targetPlayer.RunState.CreateCard(canonical, targetPlayer);
            int upgrades = Math.Min(Math.Max(0, upgradeLevel), Math.Max(0, card.MaxUpgradeLevel));
            for (int i = 0; i < upgrades && card.IsUpgradable; i++)
                CardCmd.Upgrade(card, CardPreviewStyle.None);

            return card;
        }
        catch (Exception exception)
        {
            LoadoutApplyService.Warn($"Loadout: failed creating card '{canonical.Id}'. {exception.Message}");
            return null;
        }
    }

    private async Task<bool> ReplaceRelics(Player targetPlayer, IReadOnlyList<SavedRelicLoadoutEntry> entries)
    {
        bool changed = targetPlayer.Relics.Count > 0;
        foreach (RelicModel relic in targetPlayer.Relics.ToList())
        {
            try
            {
                await RelicCmd.Remove(relic);
            }
            catch (Exception exception)
            {
                LoadoutApplyService.Warn($"Loadout: failed removing relic '{relic.Id}' from player {targetPlayer.NetId}. {exception.Message}");
            }
        }

        foreach (SavedRelicLoadoutEntry entry in entries)
        {
            RelicModel? canonical = LoadoutSerializationService.ResolveRelic(entry.ModelId);
            if (canonical is null)
            {
                LoadoutApplyService.Warn($"Loadout: skipped unknown relic '{entry.ModelId}'.");
                continue;
            }

            int count = Math.Max(1, entry.Count);
            for (int i = 0; i < count; i++)
            {
                try
                {
                    await RelicCmd.Obtain(canonical.ToMutable(), targetPlayer);
                    changed = true;
                }
                catch (Exception exception)
                {
                    LoadoutApplyService.Warn($"Loadout: stopped adding relic '{entry.ModelId}' to player {targetPlayer.NetId} after {i}/{count}. {exception.Message}");
                    break;
                }
            }
        }

        return changed;
    }

    private IReadOnlyList<Player> ResolveTargetPlayers()
    {
        LoadoutTargetSelection effectiveTarget = GetServerSafeTarget();
        IReadOnlyList<Player> players = LoadoutTargetService.ResolvePlayers(effectiveTarget, Player.RunState);
        return players.Count > 0 ? players : [Player];
    }

    private LoadoutTargetSelection GetServerSafeTarget()
    {
        try
        {
            if (RunManager.Instance.NetService.Type == NetGameType.Host
                && Player.NetId != RunManager.Instance.NetService.NetId)
            {
                return LoadoutTargetSelection.ForPlayer(Player.NetId);
            }
        }
        catch
        {
            // Singleplayer and replay services do not need target hardening here.
        }

        return Target;
    }
}

public struct NetApplyLoadoutGameAction : INetAction, IPacketSerializable
{
    public LoadoutKind kind;
    public string encodedPayload;
    public LoadoutTargetSelection target;
    public bool enqueuedInCombat;

    public GameAction ToGameAction(Player player)
    {
        return new ApplyLoadoutGameAction(
            player,
            kind,
            encodedPayload ?? string.Empty,
            target,
            enqueuedInCombat);
    }

    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt((int)kind, 4);
        writer.WriteString(encodedPayload ?? string.Empty);
        writer.WriteInt((int)target.Scope, 4);
        writer.WriteBool(target.PlayerNetId.HasValue);
        if (target.PlayerNetId.HasValue)
            writer.WriteULong(target.PlayerNetId.Value);
        writer.WriteBool(enqueuedInCombat);
    }

    public void Deserialize(PacketReader reader)
    {
        kind = (LoadoutKind)reader.ReadInt(4);
        encodedPayload = reader.ReadString();
        LoadoutTargetScope scope = (LoadoutTargetScope)reader.ReadInt(4);
        ulong? playerNetId = reader.ReadBool() ? reader.ReadULong() : null;
        target = new LoadoutTargetSelection(scope, playerNetId);
        enqueuedInCombat = reader.ReadBool();
    }

    public override string ToString()
    {
        return $"NetApplyLoadoutGameAction {kind} target {target}";
    }
}
