#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Godot;
using Loadout.PanelItems;
using Loadout.Patches.Cards.CardModification;
using Loadout.Services.CardModification;
using Loadout.Services.PowerGiver;
using Loadout.UI.Managers;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Commands;
using System.Threading.Tasks;
using BaseLib.Cards.Variables;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.TextEffects;

public enum LoadoutPowerKeywordTargetMode
{
    SelectedCreature,
    Self,
    AllEnemies,
    AllPlayers,
    AnotherPlayer
}

public enum LoadoutPowerKeywordDescriptionStyle
{
    EntryList,
    PermanentGainLose,
    FatalPermanentGainLose
}

public abstract class LoadoutPowerKeywordModel : LoadoutKeywordModel
{
    public override LoadoutKeywordPresentation Presentation =>
        LoadoutKeywordPresentation.DescriptionOnly;

    public override LoadoutKeywordEditorSection EditorSection =>
        LoadoutKeywordEditorSection.Power;

    public override LoadoutKeywordEditorControlKind EditorControlKind =>
        LoadoutKeywordEditorControlKind.RepeatablePower;

    public abstract LoadoutPowerKeywordTargetMode TargetMode { get; }

    public abstract string DisplayVarName { get; }

    public abstract string AmountLabelLocKey { get; }

    public abstract string PowerLabelLocKey { get; }

    public override bool HasOnPlayEffect => true;

    public override bool ChangesTargeting =>
        TargetMode is LoadoutPowerKeywordTargetMode.SelectedCreature
            or LoadoutPowerKeywordTargetMode.AnotherPlayer;

    protected static IReadOnlyList<LoadoutKeywordDynamicVarDefinition>
        CreateDisplayVariables(
            string displayVarName,
            string labelLocKey,
            string storageKey,
            LoadoutPowerKeywordDescriptionStyle descriptionStyle =
                LoadoutPowerKeywordDescriptionStyle.EntryList) =>
        [
            new(
                displayVarName,
                0m,
                int.MinValue,
                int.MaxValue,
                labelLocKey,
                (name, _) => new DisplayVar<CardModel>(
                    name,
                    card => LoadoutPowerKeywordState.FormatEntries(
                        card,
                        storageKey,
                        descriptionStyle)),
                EditorVisible: false)
        ];

    protected static async Task ApplyPermanentlyToPowerGiver(
        CardModel card,
        PlayerChoiceContext choiceContext,
        string keywordKey,
        int repetitionCount)
    {
        if (repetitionCount <= 0)
            return;

        foreach (LoadoutPowerKeywordEntry entry in
                 LoadoutPowerKeywordState.GetEffectiveEntries(
                     card,
                     keywordKey))
        {
            if (!LoadoutPowerKeywordState.TryResolvePower(
                    entry.PowerId,
                    out PowerModel canonical))
            {
                LoadoutPowerKeywordState.WarnUnknownPower(entry.PowerId);
                continue;
            }

            int amount = LoadoutPowerKeywordState.SaturatingMultiply(
                entry.Amount,
                repetitionCount);
            if (amount == 0)
                continue;

            await PowerGiverStateService.AdjustCounterFromCardAsync(
                canonical.Id.ToString(),
                amount,
                card,
                choiceContext);
        }
    }

    public override async Task AfterOnPlay(
        CardModel card,
        PlayerChoiceContext choiceContext,
        CardPlay cardPlay,
        object? capturedState)
    {
        IReadOnlyList<Creature> targets = ResolveTargets(card, cardPlay);
        if (targets.Count == 0)
            return;

        foreach (LoadoutPowerKeywordEntry entry in
                 LoadoutPowerKeywordState.GetEffectiveEntries(
                     card,
                     StorageKey))
        {
            if (!LoadoutPowerKeywordState.TryResolvePower(
                    entry.PowerId,
                    out PowerModel canonical))
            {
                LoadoutPowerKeywordState.WarnUnknownPower(entry.PowerId);
                continue;
            }

            foreach (Creature target in targets)
            {
                if (target.IsDead)
                    continue;
                await PowerCmd.Apply(
                    choiceContext,
                    canonical.ToMutable(),
                    target,
                    entry.Amount,
                    card.Owner.Creature,
                    card,
                    silent: false);
            }
        }
    }

    private IReadOnlyList<Creature> ResolveTargets(
        CardModel card,
        CardPlay cardPlay)
    {
        Creature source = card.Owner.Creature;
        return TargetMode switch
        {
            LoadoutPowerKeywordTargetMode.Self => [source],
            LoadoutPowerKeywordTargetMode.AllEnemies =>
                source.CombatState?.Enemies.ToList() ?? [],
            LoadoutPowerKeywordTargetMode.AllPlayers =>
                source.CombatState?.PlayerCreatures.ToList() ?? [],
            LoadoutPowerKeywordTargetMode.AnotherPlayer =>
                cardPlay.Target is { IsDead: false } target
                && !ReferenceEquals(target, source)
                && source.CombatState?.PlayerCreatures.Contains(target) == true
                    ? [target]
                    : [],
            _ => cardPlay.Target is { IsDead: false } target
                ? [target]
                : []
        };
    }
}

public static class LoadoutPowerKeywordState
{
    private const int UnknownPowerWarningLimit = 32;
    private sealed class ExplicitState
    {
        public ExplicitState(
            IReadOnlyList<LoadoutPowerKeywordEntry>? baseEntries,
            IReadOnlyList<LoadoutPowerKeywordEntryUpgrade>? entryUpgrades,
            IReadOnlyList<LoadoutPowerKeywordEntry>? addedEntries)
        {
            BaseEntries = LoadoutPowerKeywordEntry.CloneList(baseEntries);
            EntryUpgrades = LoadoutPowerKeywordEntryUpgrade.CloneList(entryUpgrades);
            AddedEntries = LoadoutPowerKeywordEntry.CloneList(addedEntries);
        }

        public List<LoadoutPowerKeywordEntry>? BaseEntries { get; }
        public List<LoadoutPowerKeywordEntryUpgrade>? EntryUpgrades { get; }
        public List<LoadoutPowerKeywordEntry>? AddedEntries { get; }
    }

    private static ConditionalWeakTable<CardModel, ExplicitState> ExplicitStates = new();
    private static readonly Dictionary<string, PowerModel> PowersById =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> WarnedUnknownPowerIds =
        new(StringComparer.OrdinalIgnoreCase);
    private static bool _powerCacheBuilt;
    private static bool _unknownWarningLimitReported;

    public static void Reset()
    {
        ExplicitStates = new ConditionalWeakTable<CardModel, ExplicitState>();
        PowersById.Clear();
        WarnedUnknownPowerIds.Clear();
        _powerCacheBuilt = false;
        _unknownWarningLimitReported = false;
    }

    public static void SetExplicitState(
        CardModel card,
        CardModificationSpec state)
    {
        SetExplicitState(
            card,
            state.PowerKeywordEntries,
            state.UpgradeModification.PowerKeywordEntryUpgrades,
            state.UpgradeModification.AddedPowerKeywordEntries);
    }

    public static void SetExplicitState(
        CardModel card,
        IReadOnlyList<LoadoutPowerKeywordEntry>? baseEntries,
        IReadOnlyList<LoadoutPowerKeywordEntryUpgrade>? entryUpgrades,
        IReadOnlyList<LoadoutPowerKeywordEntry>? addedEntries)
    {
        ExplicitStates.Remove(card);
        ExplicitStates.Add(card, new ExplicitState(
            baseEntries,
            entryUpgrades,
            addedEntries));
    }

    public static void CopyExplicitState(CardModel source, CardModel destination)
    {
        if (!ExplicitStates.TryGetValue(source, out ExplicitState? state))
            return;

        SetExplicitState(
            destination,
            state.BaseEntries,
            state.EntryUpgrades,
            state.AddedEntries);
        Synchronize(destination);
    }

    public static void ClearExplicitState(CardModel card)
    {
        ExplicitStates.Remove(card);
    }

    public static void Synchronize(CardModel card)
    {
        if (card.IsCanonical)
        {
            LoadoutKeywordRegistry.SynchronizeDynamicVars(card);
            return;
        }

        ResolveLists(card, out IReadOnlyList<LoadoutPowerKeywordEntry>? baseEntries,
            out _, out IReadOnlyList<LoadoutPowerKeywordEntry>? addedEntries);
        HashSet<LoadoutKeywordModel>? configured = null;
        AddConfiguredModels(baseEntries, ref configured);
        if (card.CurrentUpgradeLevel > 0)
            AddConfiguredModels(addedEntries, ref configured);

        IReadOnlyList<LoadoutKeywordModel> active = LoadoutKeywordRegistry.ResolveActiveModels(
            card, model => model is LoadoutPowerKeywordModel);
        if (configured is null && active.Count == 0)
        {
            LoadoutKeywordRegistry.SynchronizeDynamicVars(card);
            return;
        }

        HashSet<LoadoutKeywordModel> candidates = new(configured ?? []);
        candidates.UnionWith(active);
        List<LoadoutKeywordModel> ordered = candidates.ToList();
        ordered.Sort(LoadoutKeywordRegistry.CompareRegistration);
        foreach (LoadoutKeywordModel model in ordered)
        {
            bool enabled = configured?.Contains(model) == true;
            bool present = LoadoutKeywords.Has(card, model.Keyword);
            if (enabled && !present)
                card.AddKeyword(model.Keyword);
            else if (!enabled && present)
                card.RemoveKeyword(model.Keyword);
        }

        LoadoutKeywordRegistry.SynchronizeDynamicVars(card);
    }

    private static void AddConfiguredModels(
        IReadOnlyList<LoadoutPowerKeywordEntry>? entries,
        ref HashSet<LoadoutKeywordModel>? models)
    {
        if (entries is null)
            return;
        foreach (LoadoutPowerKeywordEntry entry in entries)
        {
            if (LoadoutKeywordRegistry.TryGet(entry.KeywordKey, out LoadoutKeywordModel model)
                && model is LoadoutPowerKeywordModel)
                (models ??= []).Add(model);
        }
    }

    public static IEnumerable<LoadoutPowerKeywordEntry> GetEffectiveEntries(
        CardModel card,
        string keywordKey)
    {
        foreach (LoadoutPowerKeywordEntry entry in GetEffectiveEntries(card))
        {
            if (MatchesKeyword(entry, keywordKey))
                yield return entry;
        }
    }

    public static IEnumerable<LoadoutPowerKeywordEntry> GetEffectiveEntries(CardModel card)
    {
        ResolveLists(
            card,
            out IReadOnlyList<LoadoutPowerKeywordEntry>? baseEntries,
            out IReadOnlyList<LoadoutPowerKeywordEntryUpgrade>? entryUpgrades,
            out IReadOnlyList<LoadoutPowerKeywordEntry>? addedEntries);
        if ((baseEntries is null || baseEntries.Count == 0)
            && (card.CurrentUpgradeLevel <= 0 || addedEntries is null || addedEntries.Count == 0))
            return Array.Empty<LoadoutPowerKeywordEntry>();

        InfiniteUpgradeScalingMode scalingMode =
            InfiniteUpgradeValueScaling.Resolve(card);

        return GetEffectiveEntries(baseEntries, entryUpgrades, addedEntries,
            card.CurrentUpgradeLevel, scalingMode);
    }

    public static IEnumerable<LoadoutPowerKeywordEntry> GetEffectiveEntries(
        IReadOnlyList<LoadoutPowerKeywordEntry>? baseEntries,
        IReadOnlyList<LoadoutPowerKeywordEntryUpgrade>? entryUpgrades,
        IReadOnlyList<LoadoutPowerKeywordEntry>? addedEntries,
        int upgradeLevel)
    {
        return GetEffectiveEntries(
            baseEntries,
            entryUpgrades,
            addedEntries,
            upgradeLevel,
            InfiniteUpgradeScalingMode.None);
    }

    public static IEnumerable<LoadoutPowerKeywordEntry> GetEffectiveEntries(
        IReadOnlyList<LoadoutPowerKeywordEntry>? baseEntries,
        IReadOnlyList<LoadoutPowerKeywordEntryUpgrade>? entryUpgrades,
        IReadOnlyList<LoadoutPowerKeywordEntry>? addedEntries,
        int upgradeLevel,
        bool useInfiniteUpgradeValues)
    {
        return GetEffectiveEntries(
            baseEntries,
            entryUpgrades,
            addedEntries,
            upgradeLevel,
            useInfiniteUpgradeValues
                ? InfiniteUpgradeScalingMode.Incremental
                : InfiniteUpgradeScalingMode.None);
    }

    public static IEnumerable<LoadoutPowerKeywordEntry> GetEffectiveEntries(
        IReadOnlyList<LoadoutPowerKeywordEntry>? baseEntries,
        IReadOnlyList<LoadoutPowerKeywordEntryUpgrade>? entryUpgrades,
        IReadOnlyList<LoadoutPowerKeywordEntry>? addedEntries,
        int upgradeLevel,
        InfiniteUpgradeScalingMode scalingMode)
    {
        foreach (EffectivePowerKeywordEntry effective in GetEffectiveEntryStates(
                     baseEntries,
                     entryUpgrades,
                     addedEntries,
                     upgradeLevel,
                     scalingMode))
        {
            yield return effective.Entry;
        }
    }

    private static IEnumerable<EffectivePowerKeywordEntry> GetEffectiveEntryStates(
        IReadOnlyList<LoadoutPowerKeywordEntry>? baseEntries,
        IReadOnlyList<LoadoutPowerKeywordEntryUpgrade>? entryUpgrades,
        IReadOnlyList<LoadoutPowerKeywordEntry>? addedEntries,
        int upgradeLevel,
        InfiniteUpgradeScalingMode scalingMode)
    {
        int level = Math.Max(0, upgradeLevel);
        long cumulativeUpgradeBonus = scalingMode
                                      == InfiniteUpgradeScalingMode.Incremental
            ? InfiniteUpgradeValueScaling.GetCumulativeUpgradeBonus(level)
            : 0L;
        Dictionary<EntryIdentity, LoadoutPowerKeywordEntryUpgrade>? upgradesByIdentity =
            level > 0 && entryUpgrades is not null
                ? entryUpgrades
                    .Where(entry => entry.HasIdentity)
                    .GroupBy(CreateIdentity)
                    .ToDictionary(group => group.Key, group => group.Last())
                : null;
        Dictionary<(string KeywordKey, string PowerId), int> occurrences =
            new(KeywordPowerIdentityComparer.Instance);

        if (baseEntries is not null)
        {
            foreach (LoadoutPowerKeywordEntry baseEntry in baseEntries)
            {
                LoadoutPowerKeywordEntry effective = baseEntry.Clone();
                bool amountWasUpgraded = false;
                int occurrence = GetAndIncrementOccurrence(occurrences, baseEntry);
                EntryIdentity identity = new(
                    baseEntry.KeywordKey,
                    baseEntry.PowerId,
                    occurrence);
                if (level > 0
                    && upgradesByIdentity?.TryGetValue(
                        identity,
                        out LoadoutPowerKeywordEntryUpgrade? upgrade) == true)
                {
                    if (upgrade.AmountDelta != 0
                        && scalingMode == InfiniteUpgradeScalingMode.Double)
                    {
                        effective.Amount = DoubleAmount(
                            baseEntry.Amount,
                            level);
                    }
                    else
                    {
                        long effectiveAmount = (long)baseEntry.Amount
                                               + (long)upgrade.AmountDelta * level;
                        if (upgrade.AmountDelta != 0)
                            effectiveAmount += cumulativeUpgradeBonus;
                        effective.Amount = SaturatingAmount(effectiveAmount);
                    }
                    amountWasUpgraded = upgrade.AmountDelta != 0;
                    if (!string.IsNullOrWhiteSpace(upgrade.ReplacementPowerId))
                        effective.PowerId = upgrade.ReplacementPowerId;
                }

                yield return new EffectivePowerKeywordEntry(
                    effective,
                    amountWasUpgraded);
            }
        }

        if (level == 0 || addedEntries is null)
            yield break;

        foreach (LoadoutPowerKeywordEntry addedEntry in addedEntries)
        {
            LoadoutPowerKeywordEntry effective = addedEntry.Clone();
            effective.Amount = scalingMode == InfiniteUpgradeScalingMode.Double
                ? DoubleAmount(addedEntry.Amount, level - 1)
                : SaturatingAmount(
                    (long)addedEntry.Amount * level
                    + cumulativeUpgradeBonus);
            yield return new EffectivePowerKeywordEntry(
                effective,
                AmountWasUpgraded: true);
        }
    }

    public static bool HasEffectiveEntries(CardModel card, string keywordKey)
    {
        ResolveLists(
            card,
            out IReadOnlyList<LoadoutPowerKeywordEntry>? baseEntries,
            out _,
            out IReadOnlyList<LoadoutPowerKeywordEntry>? addedEntries);
        return baseEntries?.Any(entry => MatchesKeyword(entry, keywordKey)) == true
               || card.CurrentUpgradeLevel > 0
               && addedEntries?.Any(entry => MatchesKeyword(entry, keywordKey)) == true;
    }

    public static bool HasConfiguredEntries(
        IReadOnlyList<LoadoutPowerKeywordEntry>? entries) =>
        entries?.Any(entry => !entry.IsEmpty) == true;

    public static List<LoadoutPowerKeywordEntryUpgrade>? PruneEntryUpgrades(
        IReadOnlyList<LoadoutPowerKeywordEntry>? baseEntries,
        IReadOnlyList<LoadoutPowerKeywordEntryUpgrade>? entryUpgrades)
    {
        if (entryUpgrades is null)
            return null;

        HashSet<EntryIdentity> validIdentities = [];
        Dictionary<(string KeywordKey, string PowerId), int> occurrences =
            new(KeywordPowerIdentityComparer.Instance);
        if (baseEntries is not null)
        {
            foreach (LoadoutPowerKeywordEntry entry in baseEntries)
            {
                validIdentities.Add(new EntryIdentity(
                    entry.KeywordKey,
                    entry.PowerId,
                    GetAndIncrementOccurrence(occurrences, entry)));
            }
        }

        return entryUpgrades
            .Where(entry => entry.HasIdentity
                            && validIdentities.Contains(CreateIdentity(entry)))
            .Select(entry => entry.Clone())
            .ToList();
    }

    public static bool TryResolveKeywordModel(
        string? keywordKey,
        out LoadoutPowerKeywordModel model)
    {
        if (LoadoutKeywords.TryResolve(keywordKey, out CardKeyword keyword)
            && LoadoutKeywordRegistry.TryGet(
                keyword,
                out LoadoutKeywordModel resolved)
            && resolved is LoadoutPowerKeywordModel powerModel)
        {
            model = powerModel;
            return true;
        }

        model = null!;
        return false;
    }

    public static bool TryResolvePower(string? powerId, out PowerModel power)
    {
        EnsurePowerCache();
        if (!string.IsNullOrWhiteSpace(powerId)
            && PowersById.TryGetValue(powerId.Trim(), out PowerModel? resolved))
        {
            power = resolved;
            return true;
        }

        power = null!;
        return false;
    }

    public static string GetDefaultStrengthPowerId()
    {
        EnsurePowerCache();
        PowerModel? strength = ModelDb.AllPowers.FirstOrDefault(power => power is StrengthPower);
        return strength?.Id.ToString() ?? "Strength";
    }

    public static string FormatEntries(
        CardModel card,
        string keywordKey,
        LoadoutPowerKeywordDescriptionStyle descriptionStyle =
            LoadoutPowerKeywordDescriptionStyle.EntryList)
    {
        ResolveLists(
            card,
            out IReadOnlyList<LoadoutPowerKeywordEntry>? baseEntries,
            out IReadOnlyList<LoadoutPowerKeywordEntryUpgrade>? entryUpgrades,
            out IReadOnlyList<LoadoutPowerKeywordEntry>? addedEntries);
        InfiniteUpgradeScalingMode scalingMode =
            InfiniteUpgradeValueScaling.Resolve(card);
        bool highlightUpgradeAmounts = card.UpgradePreviewType.IsPreview();
        List<EffectivePowerKeywordEntry> entries =
            GetEffectiveEntryStates(
                    baseEntries,
                    entryUpgrades,
                    addedEntries,
                    card.CurrentUpgradeLevel,
                    scalingMode)
                .Where(effective => MatchesKeyword(
                    effective.Entry,
                    keywordKey))
                .ToList();
        if (descriptionStyle == LoadoutPowerKeywordDescriptionStyle.EntryList)
        {
            return JoinFormattedEntries(
                entries,
                highlightUpgradeAmounts,
                useAbsoluteAmounts: false);
        }

        string gains = JoinFormattedEntries(
            entries.Where(entry => entry.Entry.Amount > 0),
            highlightUpgradeAmounts,
            useAbsoluteAmounts: true);
        string losses = JoinFormattedEntries(
            entries.Where(entry => entry.Entry.Amount < 0),
            highlightUpgradeAmounts,
            useAbsoluteAmounts: true);
        List<string> sentences = [];
        bool fatal = descriptionStyle ==
                     LoadoutPowerKeywordDescriptionStyle.FatalPermanentGainLose;
        if (!string.IsNullOrWhiteSpace(gains))
        {
            sentences.Add(LocMan.Loc(
                fatal
                    ? "CARD_MOD_POWER_KEYWORD_FATAL_PERMANENT_GAIN"
                    : "CARD_MOD_POWER_KEYWORD_PERMANENT_GAIN",
                fatal
                    ? "If [gold]Fatal[/gold], gain {0} permanently."
                    : "Gain {0} permanently.",
                gains));
        }

        if (!string.IsNullOrWhiteSpace(losses))
        {
            sentences.Add(LocMan.Loc(
                fatal
                    ? "CARD_MOD_POWER_KEYWORD_FATAL_PERMANENT_LOSE"
                    : "CARD_MOD_POWER_KEYWORD_PERMANENT_LOSE",
                fatal
                    ? "If [gold]Fatal[/gold], lose {0} permanently."
                    : "Lose {0} permanently.",
                losses));
        }

        return string.Join(' ', sentences);
    }

    private static string JoinFormattedEntries(
        IEnumerable<EffectivePowerKeywordEntry> entries,
        bool highlightUpgradeAmounts,
        bool useAbsoluteAmounts)
    {
        string separator = LocMan.Loc(
            "CARD_MOD_POWER_KEYWORD_SEPARATOR",
            ", ");
        return string.Join(
            separator,
            entries.Select(effective => FormatEntry(
                effective,
                highlightUpgradeAmounts,
                useAbsoluteAmounts)));
    }

    private static string FormatEntry(
        EffectivePowerKeywordEntry effective,
        bool highlightUpgradeAmounts,
        bool useAbsoluteAmounts)
    {
        LoadoutPowerKeywordEntry entry = effective.Entry;
        bool resolved = TryResolvePower(entry.PowerId, out PowerModel power);
        string title = resolved
            ? CommonHelpers.FormatPowerTitle(power)
            : GetPowerIdFallback(entry.PowerId);
        title = $"[gold]{title}[/gold]";
        long displayAmount = useAbsoluteAmounts
            ? Math.Abs((long)entry.Amount)
            : entry.Amount;
        string amount = displayAmount.ToString(CultureInfo.InvariantCulture);
        if (highlightUpgradeAmounts && effective.AmountWasUpgraded)
            amount = StsTextUtilities.HighlightChangeText(amount, 1);
        bool usesPointClassifier = resolved
            && power is StrengthPower or DexterityPower or FocusPower;
        return LocMan.Loc(
            usesPointClassifier
                ? "CARD_MOD_POWER_KEYWORD_ENTRY_POINT"
                : "CARD_MOD_POWER_KEYWORD_ENTRY",
            "{0} {1}",
            amount,
            title);
    }

    public static int SaturatingMultiply(int amount, int multiplier)
    {
        long product = (long)amount * multiplier;
        return SaturatingAmount(product);
    }

    public static void WarnUnknownPower(string powerId)
    {
        if (WarnedUnknownPowerIds.Contains(powerId))
            return;
        if (WarnedUnknownPowerIds.Count >= UnknownPowerWarningLimit)
        {
            if (!_unknownWarningLimitReported)
            {
                _unknownWarningLimitReported = true;
                GD.PushWarning(
                    "Loadout power keyword: additional unknown power warnings were suppressed.");
            }
            return;
        }

        if (WarnedUnknownPowerIds.Add(powerId))
            GD.PushWarning($"Loadout power keyword: unknown power '{powerId}' was skipped.");
    }

    private static void ResolveLists(
        CardModel card,
        out IReadOnlyList<LoadoutPowerKeywordEntry>? baseEntries,
        out IReadOnlyList<LoadoutPowerKeywordEntryUpgrade>? entryUpgrades,
        out IReadOnlyList<LoadoutPowerKeywordEntry>? addedEntries)
    {
        if (ExplicitStates.TryGetValue(card, out ExplicitState? explicitState))
        {
            baseEntries = explicitState.BaseEntries;
            entryUpgrades = explicitState.EntryUpgrades;
            addedEntries = explicitState.AddedEntries;
            return;
        }

        baseEntries = null;
        entryUpgrades = null;
        addedEntries = null;
        if (PermanentCardModificationStore.TryGetDelta(
                card.Id,
                out CardModificationDelta? permanent))
        {
            baseEntries = permanent.PowerKeywordEntries;
            entryUpgrades = permanent.UpgradeModification.PowerKeywordEntryUpgrades;
            addedEntries = permanent.UpgradeModification.AddedPowerKeywordEntries;
        }

        if (!CardModificationFields.TryGet(card, out CardModificationCardData temporary))
            return;

        if (temporary.Delta.PowerKeywordEntries is not null)
            baseEntries = temporary.Delta.PowerKeywordEntries;
        if (temporary.Delta.UpgradeModification.PowerKeywordEntryUpgrades is not null)
        {
            entryUpgrades = temporary.Delta.UpgradeModification.PowerKeywordEntryUpgrades;
        }
        if (temporary.Delta.UpgradeModification.AddedPowerKeywordEntries is not null)
        {
            addedEntries = temporary.Delta.UpgradeModification.AddedPowerKeywordEntries;
        }
    }

    private static EntryIdentity CreateIdentity(
        LoadoutPowerKeywordEntryUpgrade entry) =>
        new(entry.KeywordKey, entry.OriginalPowerId, entry.OccurrenceIndex);

    private static int GetAndIncrementOccurrence(
        Dictionary<(string KeywordKey, string PowerId), int> occurrences,
        LoadoutPowerKeywordEntry entry)
    {
        (string KeywordKey, string PowerId) key = (entry.KeywordKey, entry.PowerId);
        occurrences.TryGetValue(key, out int occurrence);
        occurrences[key] = occurrence + 1;
        return occurrence;
    }

    private static int DoubleAmount(int amount, int doublings)
    {
        if (amount == 0 || doublings <= 0)
            return amount;

        long result = amount;
        for (int i = 0; i < doublings; i++)
        {
            result *= 2L;
            if (result > int.MaxValue)
                return int.MaxValue;
            if (result < int.MinValue)
                return int.MinValue;
        }

        return (int)result;
    }

    private static int SaturatingAmount(long amount) =>
        amount > int.MaxValue
            ? int.MaxValue
            : amount < int.MinValue
                ? int.MinValue
                : (int)amount;

    private readonly record struct EntryIdentity(
        string KeywordKey,
        string OriginalPowerId,
        int OccurrenceIndex)
    {
        public bool Equals(EntryIdentity other) =>
            OccurrenceIndex == other.OccurrenceIndex
            && string.Equals(KeywordKey, other.KeywordKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(OriginalPowerId, other.OriginalPowerId, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode() => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(KeywordKey),
            StringComparer.OrdinalIgnoreCase.GetHashCode(OriginalPowerId),
            OccurrenceIndex);
    }

    private readonly record struct EffectivePowerKeywordEntry(
        LoadoutPowerKeywordEntry Entry,
        bool AmountWasUpgraded);

    private sealed class KeywordPowerIdentityComparer
        : IEqualityComparer<(string KeywordKey, string PowerId)>
    {
        public static readonly KeywordPowerIdentityComparer Instance = new();

        public bool Equals(
            (string KeywordKey, string PowerId) x,
            (string KeywordKey, string PowerId) y) =>
            string.Equals(x.KeywordKey, y.KeywordKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.PowerId, y.PowerId, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string KeywordKey, string PowerId) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.KeywordKey),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.PowerId));
    }

    private static bool MatchesKeyword(
        LoadoutPowerKeywordEntry entry,
        string keywordKey) =>
        string.Equals(
            entry.KeywordKey,
            keywordKey,
            StringComparison.OrdinalIgnoreCase);

    private static string GetPowerIdFallback(string powerId)
    {
        string trimmed = powerId.Trim();
        int separator = Math.Max(trimmed.LastIndexOf(':'), trimmed.LastIndexOf('/'));
        return separator >= 0 && separator + 1 < trimmed.Length
            ? trimmed[(separator + 1)..]
            : trimmed;
    }

    private static void EnsurePowerCache()
    {
        if (_powerCacheBuilt)
            return;

        PowersById.Clear();
        foreach (PowerModel power in ModelDb.AllPowers)
        {
            PowersById[power.Id.ToString()] = power;
            PowersById.TryAdd(power.Id.Entry, power);
        }
        _powerCacheBuilt = true;
    }
}
