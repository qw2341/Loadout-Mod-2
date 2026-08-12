#nullable enable

namespace Loadout.Services.CustomRuns.Runtime;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Loadout.Services.Actions;
using Loadout.Services.Compatibility;
using Loadout.Services.CustomRuns.Catalog;
using Loadout.Services.CustomRuns.Compilation;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.CustomRuns.Registry;
using Loadout.UI.Screens;
using Loadout.UI;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.ValueProps;

internal static class CustomRunRuleEvaluator
{
    public static bool AllowsByLimit(
        CustomRunRuntimeEvent runtimeEvent,
        CompiledRuleDefinition rule,
        int priorChainExecutions)
    {
        RuleLimitDefinition limit = rule.Limit;
        CustomRunRuleCounterState counter = CustomRunRuleRuntimeService.GetCounter(rule.Id);
        bool untilConditionMet = limit.Kind == RuleLimitKind.UntilCondition
                                 && EvaluateConditions(limit.UntilConditions, runtimeEvent, rule.Id);
        return CustomRunRuleLimitLogic.Allows(limit, counter, priorChainExecutions, untilConditionMet);
    }

    public static bool EvaluateConditions(
        ConditionGroupDefinition group,
        CustomRunRuntimeEvent runtimeEvent,
        string ruleId)
    {
        return CustomRunConditionGroupLogic.Evaluate(
            group,
            condition => EvaluateConditionWithoutNegation(condition, runtimeEvent, ruleId));
    }

    public static async Task<CustomRunResolvedDecision?> ResolveDecisionAsync(
        CustomRunRuntimeEvent runtimeEvent,
        string ruleId,
        RuleComponentSpec action)
    {
        List<Player> targets = ResolveTargets(GetTarget(action), runtimeEvent);
        CustomRunResolvedDecision decision = new()
        {
            RuleId = ruleId,
            ActionTypeId = action.TypeId,
            TargetPlayerIds = targets.Select(player => player.NetId).ToList()
        };

        switch (action.TypeId)
        {
            case "Loadout2:GainPower":
                decision.ModelIds = GetExactModel(action, "powerId", SelectionModelKind.Power);
                decision.Amount = ReadNumber(action, "amount", runtimeEvent, ruleId);
                break;
            case "Loadout2:GainEnergy":
            case "Loadout2:DrawCards":
            case "Loadout2:GainGold":
            case "Loadout2:Heal":
            case "Loadout2:LoseHp":
                decision.Amount = ReadNumber(action, "amount", runtimeEvent, ruleId);
                break;
            case "Loadout2:ObtainCard":
                decision.ModelIds = GetExactModel(action, "cardId", SelectionModelKind.Card);
                break;
            case "Loadout2:ObtainRelic":
                decision.ModelIds = GetExactModel(action, "relicId", SelectionModelKind.Relic);
                break;
            case "Loadout2:ObtainPotion":
                decision.ModelIds = GetExactModel(action, "potionId", SelectionModelKind.Potion);
                break;
            case "Loadout2:ObtainCards":
            case "Loadout2:ObtainRelics":
            case "Loadout2:ObtainPotions":
            case "Loadout2:SpawnMonsters":
                await ResolveMatcherSelectionAsync(action, runtimeEvent, ruleId, targets, decision);
                break;
            case "Loadout2:GainPowers":
                await ResolveMatcherSelectionAsync(action, runtimeEvent, ruleId, targets, decision);
                decision.Amount = ReadNumber(action, "amount", runtimeEvent, ruleId);
                break;
            case "Loadout2:AddCardToHand":
            case "Loadout2:AddCardToDrawPile":
            case "Loadout2:AddCardToDiscardPile":
                decision.ModelIds = GetExactModel(action, "cardId", SelectionModelKind.Card);
                decision.Amount = ReadNumber(action, "amount", runtimeEvent, ruleId);
                decision.Pile = action.TypeId switch
                {
                    "Loadout2:AddCardToHand" => PileType.Hand.ToString(),
                    "Loadout2:AddCardToDrawPile" => PileType.Draw.ToString(),
                    _ => PileType.Discard.ToString()
                };
                break;
            case "Loadout2:SetVariable":
            case "Loadout2:AddToVariable":
            case "Loadout2:SubtractFromVariable":
                decision.VariableId = RuleComponentParameterService.GetString(action, "variableId");
                if (CustomRunRuleRuntimeService.Variables.TryGetDefinition(
                        decision.VariableId,
                        out ResolvedVariableDefinition definition)
                    && definition.ValueType == VariableValueType.Boolean)
                {
                    decision.IsBoolean = true;
                    decision.BooleanValue = RuleComponentParameterService.GetBoolean(action, "amount");
                }
                else
                    decision.Amount = ReadNumber(action, "amount", runtimeEvent, ruleId);
                break;
            default:
                return null;
        }

        if (RequiresPlayers(action.TypeId) && decision.TargetPlayerIds.Count == 0)
            return null;
        if (RequiresModels(action.TypeId) && decision.ModelIds.Count == 0 && decision.ModelIdsByPlayer.Count == 0)
            return null;
        return decision;
    }

    public static async Task ApplyDecisionAsync(
        CustomRunRuntimeEvent runtimeEvent,
        CustomRunResolvedDecision decision)
    {
        List<Player> targets = decision.TargetPlayerIds
            .Distinct()
            .Select(id => CustomRunRuleRuntimeService.RunState.GetPlayer(id))
            .Where(player => player is not null)
            .Cast<Player>()
            .OrderBy(GetLobbySlot)
            .ThenBy(player => player.NetId)
            .ToList();
        decimal amount = ToDecimal(decision.Amount);
        switch (decision.ActionTypeId)
        {
            case "Loadout2:GainPower":
                foreach (Player target in targets)
                foreach (string id in decision.ModelIds)
                {
                    if (ResolveModel<PowerModel>(SelectionModelKind.Power, id) is { } power)
                    {
                        await PowerCmd.Apply(
                            new ThrowingPlayerChoiceContext(),
                            power.ToMutable(),
                            target.Creature,
                            amount,
                            target.Creature,
                            null);
                    }
                }
                break;
            case "Loadout2:GainEnergy":
                foreach (Player target in targets)
                    await PlayerCmd.GainEnergy(amount, target);
                break;
            case "Loadout2:DrawCards":
                foreach (Player target in targets)
                    await CardPileCmd.Draw(new ThrowingPlayerChoiceContext(), amount, target);
                break;
            case "Loadout2:GainGold":
                foreach (Player target in targets)
                    await PlayerCmd.GainGold(amount, target);
                break;
            case "Loadout2:Heal":
                foreach (Player target in targets)
                    await CreatureCmd.Heal(target.Creature, amount);
                break;
            case "Loadout2:LoseHp":
                foreach (Player target in targets)
                {
                    await CreatureCmd.Damage(
                        new ThrowingPlayerChoiceContext(),
                        target.Creature,
                        amount,
                        ValueProp.Unblockable | ValueProp.Unpowered,
                        target.Creature);
                }
                break;
            case "Loadout2:ObtainCard":
            case "Loadout2:ObtainCards":
                foreach (Player target in targets)
                    await AddDeckCardsAsync(target, GetModelsForPlayer(decision, target.NetId));
                break;
            case "Loadout2:ObtainRelic":
            case "Loadout2:ObtainRelics":
                foreach (Player target in targets)
                foreach (string id in GetModelsForPlayer(decision, target.NetId))
                {
                    if (ResolveModel<RelicModel>(SelectionModelKind.Relic, id) is not { } relic)
                        continue;
                    await RelicCmd.Obtain(relic.ToMutable(), target);
                }
                break;
            case "Loadout2:ObtainPotion":
            case "Loadout2:ObtainPotions":
                foreach (Player target in targets)
                foreach (string id in GetModelsForPlayer(decision, target.NetId))
                {
                    if (ResolveModel<PotionModel>(SelectionModelKind.Potion, id) is { } potion)
                    {
                        using (LoadoutContentAcquisitionRules.IgnoreModelRestrictions())
                            await PotionCmd.TryToProcure(potion.ToMutable(), target);
                    }
                }
                break;
            case "Loadout2:GainPowers":
                foreach (Player target in targets)
                foreach (string id in GetModelsForPlayer(decision, target.NetId))
                {
                    if (ResolveModel<PowerModel>(SelectionModelKind.Power, id) is { } power)
                    {
                        await PowerCmd.Apply(
                            new ThrowingPlayerChoiceContext(),
                            power.ToMutable(),
                            target.Creature,
                            amount,
                            target.Creature,
                            null);
                    }
                }
                break;
            case "Loadout2:SpawnMonsters":
                foreach (string id in decision.ModelIds
                             .Concat(decision.ModelIdsByPlayer.Values.SelectMany(ids => ids))
                             .Distinct(StringComparer.Ordinal))
                {
                    if (ResolveModel<MonsterModel>(SelectionModelKind.Monster, id) is { } monster)
                        await LoadoutSummonMonsterService.SummonMonsterNowAsync(monster.Id);
                }
                break;
            case "Loadout2:AddCardToHand":
            case "Loadout2:AddCardToDrawPile":
            case "Loadout2:AddCardToDiscardPile":
                if (Enum.TryParse(decision.Pile, out PileType pile))
                {
                    foreach (Player target in targets)
                        await AddCombatCardsAsync(target, decision.ModelIds, Math.Max(0, (int)Math.Truncate(decision.Amount)), pile);
                }
                break;
            case "Loadout2:SetVariable":
                CustomRunRuleRuntimeService.Variables.Set(
                    decision.VariableId,
                    decision.TargetPlayerIds,
                    decision.RuleId,
                    new CustomRunVariableValue
                    {
                        Number = decision.Amount,
                        Boolean = decision.BooleanValue
                    });
                break;
            case "Loadout2:AddToVariable":
                CustomRunRuleRuntimeService.Variables.Add(
                    decision.VariableId,
                    decision.TargetPlayerIds,
                    decision.RuleId,
                    decision.Amount);
                break;
            case "Loadout2:SubtractFromVariable":
                CustomRunRuleRuntimeService.Variables.Add(
                    decision.VariableId,
                    decision.TargetPlayerIds,
                    decision.RuleId,
                    -decision.Amount);
                break;
        }
    }

    private static bool EvaluateConditionWithoutNegation(
        RuleComponentSpec condition,
        CustomRunRuntimeEvent runtimeEvent,
        string ruleId)
    {
        return condition.TypeId switch
        {
            "Loadout2:Always" => true,
            "Loadout2:TriggeringPlayerIs" => ResolveTargets(GetTarget(condition), runtimeEvent)
                .Any(player => player.NetId == runtimeEvent.TriggeringPlayerId),
            "Loadout2:PlayerHasRole" => EvaluatePlayerHasRole(condition, runtimeEvent),
            "Loadout2:CardMatches" => MatchesEventModel(condition, runtimeEvent, "matcher", SelectionModelKind.Card),
            "Loadout2:RelicMatches" => MatchesEventModel(condition, runtimeEvent, "matcher", SelectionModelKind.Relic),
            "Loadout2:PotionMatches" => MatchesEventModel(condition, runtimeEvent, "matcher", SelectionModelKind.Potion),
            "Loadout2:PowerMatches" => MatchesEventModel(condition, runtimeEvent, "matcher", SelectionModelKind.Power),
            "Loadout2:MonsterMatches" => MatchesEventModel(condition, runtimeEvent, "matcher", SelectionModelKind.Monster),
            "Loadout2:PlayerHasCard" => ResolveTargets(GetTarget(condition), runtimeEvent).Any(player =>
                player.Deck.Cards.Any(card => ModelMatches(card, RuleComponentParameterService.GetString(condition, "cardId")))),
            "Loadout2:PlayerHasRelic" => ResolveTargets(GetTarget(condition), runtimeEvent).Any(player =>
                player.Relics.Any(relic => ModelMatches(relic, RuleComponentParameterService.GetString(condition, "relicId")))),
            "Loadout2:PlayerHasPower" => ResolveTargets(GetTarget(condition), runtimeEvent).Any(player =>
                player.Creature.Powers.Any(power => ModelMatches(power, RuleComponentParameterService.GetString(condition, "powerId")))),
            "Loadout2:PlayerHasCardsMatching" => HasMatchingInventory(condition, runtimeEvent, player => player.Deck.Cards),
            "Loadout2:PlayerHasRelicsMatching" => HasMatchingInventory(condition, runtimeEvent, player => player.Relics),
            "Loadout2:PlayerHasPotionsMatching" => HasMatchingInventory(condition, runtimeEvent, player => player.Potions),
            "Loadout2:PlayerHasPowersMatching" => HasMatchingInventory(condition, runtimeEvent, player => player.Creature.Powers),
            "Loadout2:NumericComparison" => Compare(
                ReadNumber(condition, "left", runtimeEvent, ruleId),
                ReadNumber(condition, "right", runtimeEvent, ruleId),
                RuleComponentParameterService.GetString(condition, "operator")),
            "Loadout2:VariableComparison" => EvaluateVariableComparison(condition, runtimeEvent, ruleId),
            "Loadout2:Chance" => EvaluateChance(condition, runtimeEvent, ruleId),
            _ => false
        };
    }

    private static bool EvaluatePlayerHasRole(RuleComponentSpec condition, CustomRunRuntimeEvent runtimeEvent)
    {
        string roleId = RuleComponentParameterService.GetString(condition, "roleId");
        return ResolveTargets(GetTarget(condition), runtimeEvent).Any(player =>
            string.Equals(GetPlayerSetup(player.NetId)?.RoleId, roleId, StringComparison.Ordinal));
    }

    private static bool MatchesEventModel(
        RuleComponentSpec condition,
        CustomRunRuntimeEvent runtimeEvent,
        string key,
        SelectionModelKind kind)
    {
        return runtimeEvent.ModelKind == kind
               && RuleComponentParameterService.TryGet(condition, key, out ModelMatchSpec matcher)
               && matcher.ModelIds.Any(id => string.Equals(id, runtimeEvent.ModelId, StringComparison.Ordinal));
    }

    private static bool HasMatchingInventory<TModel>(
        RuleComponentSpec condition,
        CustomRunRuntimeEvent runtimeEvent,
        Func<Player, IEnumerable<TModel>> selector)
        where TModel : AbstractModel
    {
        if (!RuleComponentParameterService.TryGet(condition, "matcher", out ModelMatchSpec matcher))
            return false;
        int minimum = Math.Max(0, RuleComponentParameterService.GetInt32(condition, "minimumMatches", 1));
        HashSet<string> ids = matcher.ModelIds.ToHashSet(StringComparer.Ordinal);
        return ResolveTargets(GetTarget(condition), runtimeEvent).Any(player =>
            selector(player).Count(model => ids.Contains(model.Id.ToString())) >= minimum);
    }

    private static bool EvaluateVariableComparison(
        RuleComponentSpec condition,
        CustomRunRuntimeEvent runtimeEvent,
        string ruleId)
    {
        string variableId = RuleComponentParameterService.GetString(condition, "variableId");
        CustomRunVariableValue current = CustomRunRuleRuntimeService.Variables.Read(
            variableId,
            runtimeEvent.TriggeringPlayerId,
            ruleId);
        string comparison = RuleComponentParameterService.GetString(condition, "operator");
        if (CustomRunRuleRuntimeService.Variables.TryGetDefinition(variableId, out ResolvedVariableDefinition definition)
            && definition.ValueType == VariableValueType.Boolean)
        {
            bool expected = RuleComponentParameterService.GetBoolean(condition, "value");
            return comparison == "Equal" ? current.Boolean == expected : current.Boolean != expected;
        }
        return Compare(current.Number, ReadNumber(condition, "value", runtimeEvent, ruleId), comparison);
    }

    private static bool EvaluateChance(
        RuleComponentSpec condition,
        CustomRunRuntimeEvent runtimeEvent,
        string ruleId)
    {
        double percent = Math.Clamp(ReadNumber(condition, "percent", runtimeEvent, ruleId), 0d, 100d);
        int roll = CustomRunRuleRuntimeService.NextIndex(1_000_000, $"chance:{runtimeEvent.EventId}:{ruleId}");
        return roll < percent * 10_000d;
    }

    private static double ReadNumber(
        RuleComponentSpec component,
        string key,
        CustomRunRuntimeEvent runtimeEvent,
        string ruleId)
    {
        if (!RuleComponentParameterService.TryGet(component, key, out NumericValueSpec value))
            return 0d;
        return value.Source switch
        {
            NumericValueSourceKind.Constant => value.Constant,
            NumericValueSourceKind.Variable when value.ReferenceId is not null =>
                CustomRunRuleRuntimeService.Variables.Read(
                    value.ReferenceId,
                    runtimeEvent.TriggeringPlayerId,
                    ruleId).Number,
            NumericValueSourceKind.EventContext => ReadEventValue(value.ReferenceId, runtimeEvent),
            _ => 0d
        };
    }

    private static double ReadEventValue(string? id, CustomRunRuntimeEvent runtimeEvent)
    {
        Player? player = CustomRunRuleRuntimeService.RunState.GetPlayer(runtimeEvent.TriggeringPlayerId);
        return id switch
        {
            "CurrentHp" => (double)(player?.Creature.CurrentHp ?? 0m),
            "MaxHp" => (double)(player?.Creature.MaxHp ?? 0m),
            "Gold" => player?.Gold ?? 0,
            "Energy" => player?.PlayerCombatState?.Energy ?? 0,
            "TurnNumber" => player?.PlayerCombatState?.TurnNumber ?? 0,
            "PlayerCount" => CustomRunRuleRuntimeService.RunState.Players.Count,
            _ => runtimeEvent.Amount
        };
    }

    private static RuleTargetSpec GetTarget(RuleComponentSpec component)
    {
        return RuleComponentParameterService.TryGet(component, "target", out RuleTargetSpec target)
            ? target
            : new RuleTargetSpec();
    }

    private static List<Player> ResolveTargets(RuleTargetSpec target, CustomRunRuntimeEvent runtimeEvent)
    {
        List<Player> players = CustomRunRuleRuntimeService.RunState.Players
            .OrderBy(GetLobbySlot)
            .ThenBy(player => player.NetId)
            .ToList();
        Player? triggering = CustomRunRuleRuntimeService.RunState.GetPlayer(runtimeEvent.TriggeringPlayerId);
        IEnumerable<Player> selected = target.TypeId switch
        {
            "Loadout2:TriggeringPlayer" => triggering is null ? [] : [triggering],
            "Loadout2:Host" => players.Where(player => player.NetId == CustomRunRuleRuntimeService.Snapshot.HostPlayerId),
            "Loadout2:AllPlayers" => players,
            "Loadout2:AllOtherPlayers" => players.Where(player => player.NetId != runtimeEvent.TriggeringPlayerId),
            "Loadout2:RandomPlayer" => SelectRandom(players, $"target:{runtimeEvent.EventId}"),
            "Loadout2:SpecificPlayerSlot" => players.Where(player =>
                GetLobbySlot(player) == RuleComponentParameterService.GetInt32(
                    new RuleComponentSpec { Parameters = target.Parameters }, "slot")),
            "Loadout2:PlayersWithRole" => FilterRole(players, target),
            "Loadout2:RandomPlayerWithRole" => SelectRandom(FilterRole(players, target).ToList(), $"role_target:{runtimeEvent.EventId}"),
            _ => []
        };
        return selected.DistinctBy(player => player.NetId).ToList();
    }

    private static IEnumerable<Player> FilterRole(IEnumerable<Player> players, RuleTargetSpec target)
    {
        string roleId = RuleComponentParameterService.GetString(
            new RuleComponentSpec { Parameters = target.Parameters },
            "roleId");
        return players.Where(player => string.Equals(GetPlayerSetup(player.NetId)?.RoleId, roleId, StringComparison.Ordinal));
    }

    private static IEnumerable<Player> SelectRandom(IReadOnlyList<Player> players, string context)
    {
        int index = CustomRunRuleRuntimeService.NextIndex(players.Count, context);
        return index < 0 ? [] : [players[index]];
    }

    private static ResolvedPlayerSetup? GetPlayerSetup(ulong playerId)
    {
        return CustomRunRuleRuntimeService.Snapshot.Players.FirstOrDefault(player => player.PlayerId == playerId);
    }

    private static int GetLobbySlot(Player player)
    {
        return GetPlayerSetup(player.NetId)?.LobbySlot ?? int.MaxValue;
    }

    private static async Task ResolveMatcherSelectionAsync(
        RuleComponentSpec action,
        CustomRunRuntimeEvent runtimeEvent,
        string ruleId,
        IReadOnlyList<Player> targets,
        CustomRunResolvedDecision decision)
    {
        _ = ruleId;
        if (!RuleComponentParameterService.TryGet(action, "matcher", out ModelMatchSpec matcher))
            return;
        List<string> available = matcher.ModelIds
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        int count = Math.Clamp(RuleComponentParameterService.GetInt32(action, "count", 1), 0, available.Count);
        if (string.Equals(RuleComponentParameterService.GetString(action, "selectionMode"), "Choose", StringComparison.Ordinal)
            && targets.Count > 0)
        {
            bool canSkip = RuleComponentParameterService.GetBoolean(action, "canSkip");
            foreach (Player target in targets.OrderBy(GetLobbySlot).ThenBy(player => player.NetId))
            {
                IReadOnlyList<string> targetChoice = await CustomRunRuntimeChoiceService.RequestChoiceAsync(
                    target.NetId,
                    matcher.ModelKind,
                    available,
                    count,
                    count,
                    canSkip,
                    CustomRunRuleRuntimeService.ExportState().Revision);
                if (targetChoice.Count > 0)
                    decision.ModelIdsByPlayer[target.NetId] = targetChoice.ToList();
            }
            return;
        }
        List<string> selected = [];
        for (int i = 0; i < count && available.Count > 0; i++)
        {
            int index = CustomRunRuleRuntimeService.NextIndex(
                available.Count,
                $"matcher:{runtimeEvent.EventId}:{action.TypeId}:{i}");
            selected.Add(available[index]);
            available.RemoveAt(index);
        }
        decision.ModelIds = selected;
    }

    private static IReadOnlyList<string> GetModelsForPlayer(CustomRunResolvedDecision decision, ulong playerId)
    {
        return decision.ModelIdsByPlayer.TryGetValue(playerId, out List<string>? selected)
            ? selected
            : decision.ModelIds;
    }

    private static List<string> GetExactModel(
        RuleComponentSpec action,
        string key,
        SelectionModelKind kind)
    {
        string id = RuleComponentParameterService.GetString(action, key);
        return CustomRunCatalogService.TryResolve(kind, id, out CustomRunCatalogEntry entry)
            ? [entry.ModelId]
            : [];
    }

    private static async Task AddDeckCardsAsync(Player target, IEnumerable<string> modelIds)
    {
        List<CardModel> cards = [];
        foreach (string id in modelIds)
        {
            if (ResolveModel<CardModel>(SelectionModelKind.Card, id) is { } canonical)
                cards.Add(target.RunState.CreateCard(canonical, target));
        }
        if (cards.Count == 0)
            return;
        IReadOnlyList<CardPileAddResult> results;
        using (LoadoutContentAcquisitionRules.IgnoreModelRestrictions())
            results = await Sts2Compatibility.AddCards(cards, target.Deck);
        if (LocalContext.IsMe(target))
        {
            CardPreviewStyle style = results.Count > 5
                ? CardPreviewStyle.GridLayout
                : CardPreviewStyle.HorizontalLayout;
            if (NLoadoutPanelRoot.Instance?.TryPreviewCardPileAdd(results, 1.2f, style) != true)
                CardCmd.PreviewCardPileAdd(results, 1.2f, style);
        }
    }

    private static async Task AddCombatCardsAsync(
        Player target,
        IEnumerable<string> modelIds,
        int amount,
        PileType pile)
    {
        ICombatState? combat = target.Creature.CombatState;
        if (combat is null || amount <= 0)
            return;
        List<CardModel> cards = [];
        foreach (string id in modelIds)
        {
            if (ResolveModel<CardModel>(SelectionModelKind.Card, id) is not { } canonical)
                continue;
            for (int i = 0; i < amount; i++)
                cards.Add(combat.CreateCard(canonical, target));
        }
        if (cards.Count > 0)
            await CardPileCmd.AddGeneratedCardsToCombat(cards, pile, target);
    }

    private static TModel? ResolveModel<TModel>(SelectionModelKind kind, string id)
        where TModel : AbstractModel
    {
        return CustomRunCatalogService.TryResolve(kind, id, out CustomRunCatalogEntry entry)
            ? entry.Model as TModel
            : null;
    }

    private static bool Compare(double left, double right, string comparison)
    {
        return comparison switch
        {
            "Equal" => left == right,
            "NotEqual" => left != right,
            "Less" => left < right,
            "LessOrEqual" => left <= right,
            "Greater" => left > right,
            "GreaterOrEqual" => left >= right,
            _ => false
        };
    }

    private static bool ModelMatches(AbstractModel model, string id)
    {
        return string.Equals(model.Id.ToString(), id, StringComparison.Ordinal)
               || string.Equals(model.Id.Entry, id, StringComparison.OrdinalIgnoreCase);
    }

    private static decimal ToDecimal(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 0m;
        return (decimal)Math.Clamp(value, (double)decimal.MinValue, (double)decimal.MaxValue);
    }

    private static bool RequiresPlayers(string actionTypeId)
    {
        return actionTypeId != "Loadout2:SpawnMonsters";
    }

    private static bool RequiresModels(string actionTypeId)
    {
        return actionTypeId is "Loadout2:GainPower" or "Loadout2:ObtainCard" or "Loadout2:ObtainRelic"
            or "Loadout2:ObtainPotion" or "Loadout2:ObtainCards" or "Loadout2:ObtainRelics"
            or "Loadout2:ObtainPotions" or "Loadout2:GainPowers" or "Loadout2:SpawnMonsters"
            or "Loadout2:AddCardToHand" or "Loadout2:AddCardToDrawPile" or "Loadout2:AddCardToDiscardPile";
    }
}
